using System.Globalization;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using KokoSim.Engine.Debugging;
using KokoSim.Engine.Match.AtBat;
using KokoSim.Engine.Match.Game;

namespace KokoSim.Balance.Debugging;

/// <summary>
/// 観測を JSONL（1行1イベント）で書き出すシンク（設計書17 §4.2/§4.3, F1）。
/// engine は IO を持たない（不変条件#3）ので、ファイル書き込みはこの CLI 側が担う。
///
/// <para><b>キー名は設計書17 §4.3 を単一ソースとする</b>。1試合 ~300行 × 数百試合を扱うため短縮キー。
/// <c>jq</c> で切れることを要件にする（例: <c>jq -c 'select(.t=="pitch" and .bat.pSw&gt;0.8 and .bat.sw==false)'</c>）。</para>
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

    private static readonly JavaScriptEncoder Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping;

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
        if ((_kinds & Kinds.Game) == 0) return;
        _sb.Clear();
        _sb.Append("{\"t\":\"game\",\"rng\":").Append(Str(h.RngStateHex))
           .Append(",\"fixture\":").Append(Str(h.FixtureFingerprint))
           .Append(",\"away\":").Append(Str(h.AwayName))
           .Append(",\"home\":").Append(Str(h.HomeName))
           .Append(",\"scenario\":").Append(h.ScenarioId is null ? "null" : Str(h.ScenarioId))
           .Append(",\"reg\":").Append(Int(h.RegulationInnings))
           .Append(",\"tb\":").Append(Bool(h.TieBreakEnabled))
           .Append(",\"mercy\":").Append(Bool(h.MercyRuleEnabled))
           .Append('}');
        WriteLine();
    }

    public void OnPitch(PitchTrace t)
    {
        if ((_kinds & Kinds.Pitch) == 0) return;
        _sb.Clear();
        _sb.Append("{\"t\":\"pitch\",\"i\":").Append(Int(t.Inning))
           .Append(",\"top\":").Append(Bool(t.IsTop))
           .Append(",\"o\":").Append(Int(t.Outs))
           .Append(",\"b\":").Append(Str(t.BatterId))
           .Append(",\"p\":").Append(Str(t.PitcherId))
           .Append(",\"cnt\":\"").Append(t.BallsBefore).Append('-').Append(t.StrikesBefore).Append('"')
           .Append(",\"n\":").Append(Int(t.PitchNoInPa))
           .Append(",\"N\":").Append(Int(t.PitchNoInGame));

        _sb.Append(",\"plan\":{\"ty\":").Append(Str(t.PlanType.ToString()))
           .Append(",\"ax\":").Append(Num(t.PlanAimX, 3))
           .Append(",\"ay\":").Append(Num(t.PlanAimY, 3))
           .Append(",\"kmh\":").Append(Num(t.PlanVelocityKmh, 1))
           .Append(",\"stuff\":").Append(Num(t.PlanStuff, 2)).Append('}');

        _sb.Append(",\"act\":{\"x\":").Append(Num(t.ActualX, 3))
           .Append(",\"y\":").Append(Num(t.ActualY, 3))
           .Append(",\"kmh\":").Append(Num(t.ActualKmh, 1))
           .Append(",\"ft\":").Append(Num(t.FlightTimeSeconds, 4))
           .Append(",\"ivb\":").Append(Num(t.InducedVerticalBreakM, 4))
           .Append(",\"ihb\":").Append(Num(t.InducedHorizontalBreakM, 4))
           .Append(",\"zone\":").Append(Bool(t.InZone)).Append('}');

        _sb.Append(",\"bat\":{\"pSw\":").Append(Num(t.SwingProbability, 4))
           .Append(",\"sw\":").Append(Bool(t.Swung)).Append('}');

        _sb.Append(",\"res\":{\"k\":").Append(Str(t.Kind.ToString()));
        if (t.ExitVelocityKmh is { } ev)
        {
            _sb.Append(",\"ev\":").Append(Num(ev, 1))
               .Append(",\"la\":").Append(Num(t.LaunchAngleDeg ?? 0, 1))
               .Append(",\"sa\":").Append(Num(t.SprayAngleDeg ?? 0, 1));
        }
        _sb.Append('}');

        _sb.Append(",\"st\":{\"press\":").Append(Int(t.PressureIndex))
           .Append(",\"rat\":").Append(Bool(t.Rattled))
           .Append(",\"pf\":").Append(Int(t.PitchingFatigue))
           .Append(",\"gear\":").Append(Str(t.Gear.ToString()))
           .Append(",\"pol\":").Append(Str(t.Policy.ToString())).Append('}');

        if (t.ChosenSign is not null || t.SignCandidatesCsv is not null)
        {
            _sb.Append(",\"ai\":{\"sign\":").Append(t.ChosenSign is null ? "null" : Str(t.ChosenSign))
               .Append(",\"cand\":").Append(t.SignCandidatesCsv is null ? "null" : Str(t.SignCandidatesCsv))
               .Append('}');
        }

        _sb.Append(",\"rng\":{\"sid\":").Append(t.RngStreamId.ToString(CultureInfo.InvariantCulture))
           .Append(",\"d\":").Append(Int(t.RngDrawsInPitch)).Append('}')
           .Append(",\"forced\":").Append(Bool(t.Forced))
           .Append('}');
        WriteLine();
    }

    public void OnPlateAppearance(PaTrace t)
    {
        if ((_kinds & Kinds.Pa) == 0) return;
        _sb.Clear();
        _sb.Append("{\"t\":\"pa\",\"i\":").Append(Int(t.Inning))
           .Append(",\"top\":").Append(Bool(t.IsTop))
           .Append(",\"b\":").Append(Str(t.BatterId))
           .Append(",\"res\":").Append(Str(t.Result.ToString()))
           .Append(",\"rbi\":").Append(Int(t.Rbi))
           .Append(",\"outsAfter\":").Append(Int(t.OutsAfter))
           .Append(",\"pitches\":").Append(Int(t.Pitches))
           .Append(",\"bases\":").Append(Str(t.RunnerSummary))
           .Append(",\"forced\":").Append(Bool(t.Forced))
           .Append('}');
        WriteLine();
    }

    public void OnGameEnd(GameResult r)
    {
        if ((_kinds & Kinds.End) == 0) return;
        _sb.Clear();
        _sb.Append("{\"t\":\"end\",\"away\":").Append(Int(r.AwayRuns))
           .Append(",\"home\":").Append(Int(r.HomeRuns))
           .Append(",\"innings\":").Append(Int(r.InningsPlayed))
           .Append(",\"pitches\":").Append(Int(r.TotalPitches))
           .Append(",\"forced\":").Append(Bool(r.HasForcedOutcomes))
           .Append('}');
        WriteLine();
    }

    private void WriteLine()
    {
        // StringBuilder をそのまま渡す（ToString() の中間文字列を作らない）。1試合300球×数百試合を
        // 扱うため、行ごとの1割り当てでも観測オンのオーバーヘッドに効いてくる。
        _out.Write(_sb);
        _out.Write('\n');
        Lines++;
    }

    private static string Str(string s) => "\"" + JsonEncodedText.Encode(s ?? "", Encoder) + "\"";
    private static string Int(int v) => v.ToString(CultureInfo.InvariantCulture);
    private static string Bool(bool v) => v ? "true" : "false";

    /// <summary>桁を固定して書く（差分比較のノイズを減らす）。NaN/∞ は JSON にできないので null。</summary>
    private static string Num(double v, int digits)
        => double.IsNaN(v) || double.IsInfinity(v)
            ? "null"
            : System.Math.Round(v, digits).ToString("0.####", CultureInfo.InvariantCulture);

    public void Dispose()
    {
        _out.Flush();
        if (_ownsWriter) _out.Dispose();
    }
}
