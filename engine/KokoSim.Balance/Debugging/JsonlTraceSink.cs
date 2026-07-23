using System.Text;
using KokoSim.Engine.Debugging;
using KokoSim.Engine.Match.Game;

namespace KokoSim.Balance.Debugging;

/// <summary>
/// 観測を JSONL（1行1イベント）で書き出すシンク（設計書17 §4.2/§4.3, F1）。
/// engine は IO を持たない（不変条件#3）ので、ファイル書き込みはこの CLI 側が担う。
///
/// <para><b>行の組み立ては engine の <see cref="TraceJson"/> に委譲する</b>。キー名の単一ソースを1箇所に保ち、
/// Unity の <c>DebugBridge</c> が吐く JSON と1文字も食い違わないようにするため
/// （食い違うと <c>jq</c> のレシピが片方だけ壊れる）。</para>
/// </summary>
public sealed class JsonlTraceSink : IDebugTraceSink, System.IDisposable
{
    /// <summary>出力するイベント種別のフィルタ（設計書17 §4.4 の <c>--only</c>）。</summary>
    [System.Flags]
    public enum Kinds
    {
        Game = 1,
        Pitch = 2,
        Pa = 4,
        End = 8,
        All = Game | Pitch | Pa | End,
    }

    private readonly System.IO.TextWriter _out;
    private readonly bool _ownsWriter;
    private readonly Kinds _kinds;
    private readonly StringBuilder _sb = new(1024);

    public JsonlTraceSink(System.IO.TextWriter writer, Kinds kinds = Kinds.All, bool ownsWriter = false)
    {
        _out = writer;
        _kinds = kinds;
        _ownsWriter = ownsWriter;
    }

    /// <summary>ファイルへ書き出すシンクを開く（親ディレクトリは自動作成）。</summary>
    public static JsonlTraceSink OpenFile(string path, Kinds kinds = Kinds.All)
    {
        var dir = System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(path));
        if (!string.IsNullOrEmpty(dir)) System.IO.Directory.CreateDirectory(dir);
        // BOM なしの UTF-8。先頭行に BOM が付くと jq が読めない（JSONL の要件は「1行=1JSON」）。
        var utf8NoBom = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
        return new JsonlTraceSink(new System.IO.StreamWriter(path, append: false, utf8NoBom), kinds, ownsWriter: true);
    }

    /// <summary>ここまでに書いた行数（CLI の完了報告用）。</summary>
    public long Lines { get; private set; }

    public void OnGameStart(GameTraceHeader h)
    {
        if ((_kinds & Kinds.Game) != 0) WriteLine(TraceJson.Game(h));
    }

    public void OnPitch(PitchTrace t)
    {
        if ((_kinds & Kinds.Pitch) == 0) return;
        // pitch 行は最多（1試合300行×数百試合）なので、中間文字列を作らず直接 StringBuilder へ組む。
        _sb.Clear();
        TraceJson.Pitch(_sb, t);
        WriteBuffered();
    }

    public void OnPlateAppearance(PaTrace t)
    {
        if ((_kinds & Kinds.Pa) != 0) WriteLine(TraceJson.Pa(t));
    }

    public void OnGameEnd(GameResult r)
    {
        if ((_kinds & Kinds.End) != 0) WriteLine(TraceJson.End(r));
    }

    private void WriteLine(string json)
    {
        _out.Write(json);
        _out.Write('\n');
        Lines++;
    }

    private void WriteBuffered()
    {
        _out.Write(_sb);
        _out.Write('\n');
        Lines++;
    }

    public void Dispose()
    {
        _out.Flush();
        if (_ownsWriter) _out.Dispose();
    }
}
