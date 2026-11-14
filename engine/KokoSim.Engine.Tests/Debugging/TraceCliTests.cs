using System.IO;
using System.Linq;
using System.Text.Json;
using KokoSim.Balance.Debugging;
using Xunit;

namespace KokoSim.Engine.Tests.Debugging;

/// <summary>
/// 設計書17 §4.3/§4.4（F1）の CLI 側受け入れ:
///  - JSONL が1行1JSONで、キー名が設計書§4.3どおり（jq で切れる）
///  - <c>trace-diff</c> が「係数を1つ変えたトレース」の最初の食い違い球を正しく指す
///  - 同一条件の2本は「食い違いなし」になる
/// </summary>
public sealed class TraceCliTests
{
    private static string TempDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "kokosim-trace-" + System.Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    [Fact]
    public void Trace_WritesJsonlThatParsesLineByLine()
    {
        var dir = TempDir();
        try
        {
            var path = Path.Combine(dir, "t.jsonl");
            var summary = TraceCommand.Run(new TraceCommand.Options { Games = 2, Seed = 42UL, OutPath = path });

            var lines = File.ReadAllLines(path);
            Assert.Equal(summary.Lines, lines.Length);
            // BOM が付くと jq が先頭行を読めない（JSONL の要件は「1行=1JSON」）。
            Assert.StartsWith("{", lines[0]);

            var kinds = lines.Select(l =>
            {
                using var doc = JsonDocument.Parse(l);   // 1行ずつ独立に JSON として読めること
                return doc.RootElement.GetProperty("t").GetString();
            }).ToList();

            Assert.Equal(2, kinds.Count(k => k == "game"));
            Assert.Equal(2, kinds.Count(k => k == "end"));
            Assert.True(kinds.Count(k => k == "pitch") > 100);
            Assert.True(kinds.Count(k => k == "pa") > 30);

            // 設計書§4.3 のキー名（単一ソース）。ここが変わると jq のレシピが全部壊れる。
            var firstPitch = lines.First(l => l.Contains("\"t\":\"pitch\""));
            using var pitch = JsonDocument.Parse(firstPitch);
            var root = pitch.RootElement;
            foreach (var key in new[] { "i", "top", "o", "b", "p", "cnt", "n", "N", "plan", "act", "bat", "res", "st", "rng", "forced" })
                Assert.True(root.TryGetProperty(key, out _), $"pitch 行にキー {key} がない");
            foreach (var key in new[] { "ty", "ax", "ay", "kmh", "stuff" })
                Assert.True(root.GetProperty("plan").TryGetProperty(key, out _), $"plan にキー {key} がない");
            foreach (var key in new[] { "x", "y", "kmh", "ft", "ivb", "ihb", "zone" })
                Assert.True(root.GetProperty("act").TryGetProperty(key, out _), $"act にキー {key} がない");
            Assert.True(root.GetProperty("bat").TryGetProperty("pSw", out _));
            Assert.True(root.GetProperty("bat").TryGetProperty("sw", out _));
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public void Trace_OnlyFilter_EmitsRequestedKindsOnly()
    {
        var dir = TempDir();
        try
        {
            var path = Path.Combine(dir, "p.jsonl");
            TraceCommand.Run(new TraceCommand.Options
            {
                Games = 1, Seed = 3UL, OutPath = path, Only = TraceCommand.ParseKinds("pitch"),
            });

            foreach (var line in File.ReadAllLines(path))
            {
                using var doc = JsonDocument.Parse(line);
                Assert.Equal("pitch", doc.RootElement.GetProperty("t").GetString());
            }
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public void Trace_SameSeed_ProducesIdenticalTraces()
    {
        var dir = TempDir();
        try
        {
            var a = Path.Combine(dir, "a.jsonl");
            var b = Path.Combine(dir, "b.jsonl");
            TraceCommand.Run(new TraceCommand.Options { Games = 2, Seed = 17UL, OutPath = a });
            TraceCommand.Run(new TraceCommand.Options { Games = 2, Seed = 17UL, OutPath = b });

            Assert.Equal(File.ReadAllText(a), File.ReadAllText(b));

            var diff = TraceDiff.Compare(a, b);
            Assert.True(diff.Identical);
            Assert.Null(diff.FirstDivergence);
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    /// <summary>
    /// 係数を1つだけ変えたときに、trace-diff が最初に食い違った球を指すこと。
    /// 回帰の原因特定を二分探索から1コマンドへ落とす、という本サブコマンドの存在理由そのもの。
    /// </summary>
    [Fact]
    public void TraceDiff_PointsAtTheFirstDivergingPitch()
    {
        var dir = TempDir();
        try
        {
            var a = Path.Combine(dir, "before.jsonl");
            var b = Path.Combine(dir, "after.jsonl");
            var baseOpts = new TraceCommand.Options { Games = 2, Seed = 9UL };

            TraceCommand.Run(baseOpts with { OutPath = a });
            // 係数変更のかわりに「同じ土俵で1点だけ違う条件」＝別シードで作った試合と突き合わせる。
            // （係数YAMLの書き換えはテストから触れないので、食い違いの検出能力だけをここで固定する）
            TraceCommand.Run(baseOpts with { OutPath = b, Seed = 10UL });

            var diff = TraceDiff.Compare(a, b);
            Assert.False(diff.Identical);
            Assert.NotNull(diff.FirstDivergence);
            Assert.NotNull(diff.DivergedField);
            Assert.NotEqual(diff.ValueA, diff.ValueB);

            var report = TraceDiff.Report(diff, a, b);
            Assert.Contains("最初の食い違い", report);
            Assert.Contains("球目", report);
            Assert.Contains("スイング率", report);
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    /// <summary>
    /// 観測オンのオーバーヘッドが悪化していないこと（設計書17 §9 F1 DoD）。
    ///
    /// <para><b>測り方</b>: <c>TraceCommand</c> が同じ試合を5試合ずつの塊で観測オフ／オンと交互に回し、
    /// 塊ごとの比の中央値を採る。xUnit は Heavy コレクションを並列に回し、統計回帰シムと CPU を取り合う。
    /// 全試合をオフ→オンと通しで回していた旧方式は、オンの計測中だけ周りが重なるとオンだけが何倍も遅く出て
    /// 変更と無関係に赤くなった（issue #969: 531% / 86.9%）。塊を細かく交互にすれば周りの負荷は両側に同じだけ乗り、
    /// 重なった塊の外れ値は中央値が捨てる。</para>
    ///
    /// <para><b>上限 15% の根拠</b>（issue #969 の実測）: 単独実行の中央値は 2〜7%（設計書17 §9 は 5.4〜7.3%）。
    /// 10コア機に14本の busy ループを足して 5倍近く遅くしても 2.3〜5.5%（負の値も出る）に収まった。
    /// 悪化側は、シンクが毎球 fsync する変更で 800% 超（このテストは赤になる）。一方、毎球 Flush(OS書き込みのみ)は
    /// −3〜19% でゆらぎに埋もれる＝そこまでの微悪化は拾えない。上限は正常のゆらぎ（最大7%台）の約2倍に置いた。
    /// </para>
    ///
    /// <para>    /// このテストは「観測を足して桁違いに遅くなった」を止める番人で、目標値（+10%未満）の判定は
    /// 単独計測 <c>trace --games 300 --measure</c> で行う。<b>赤くなったらまず単独で回し直す</b>:
    /// 単独でも上限を超えるなら本物の悪化、単独で正常ならゆらぎの外れ（その場合は上限でなく計測を直す）。</para>
    /// </summary>
    [Trait("Category", "Heavy")]
    [Fact]
    public void Trace_OverheadDoesNotBlowUp()
    {
        var dir = TempDir();
        try
        {
            var s = TraceCommand.Run(new TraceCommand.Options
            {
                Games = 200, Seed = 42UL, OutPath = Path.Combine(dir, "m.jsonl"), MeasureOverhead = true,
            });
            Assert.True(s.UntracedMs > 0);
            Assert.True(s.OverheadRatio < 0.15,
                $"観測オンのオーバーヘッドが悪化した: {s.OverheadRatio:P1}（塊ごとの比の中央値。計測分 on {s.TracedMs:F0}ms / off {s.UntracedMs:F0}ms）");
        }
        finally { Directory.Delete(dir, recursive: true); }
    }
}
