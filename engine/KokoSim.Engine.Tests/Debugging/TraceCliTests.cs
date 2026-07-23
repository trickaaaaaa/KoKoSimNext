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
    /// 観測オンのオーバーヘッドが極端に悪化していないこと（設計書17 §9 F1 DoD）。
    ///
    /// <para><b>ここは目標値（+10%未満）の判定には使わない</b>。xUnit は Heavy コレクションを並列実行するので、
    /// 統計回帰シムと CPU を取り合い、同じコードで 5% にも 11% にもなる。目標値の実測は
    /// <c>trace --games 300 --measure</c> を単独で回して記録する（設計書17 §9 に実測値を記載）。
    /// このテストの役目は「観測を足したら2倍遅くなった」級の事故を止めることに限る。</para>
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
            Assert.True(s.OverheadRatio < 0.50,
                $"観測オンのオーバーヘッドが桁違いに悪化した: {s.OverheadRatio:P1}（on {s.TracedMs:F0}ms / off {s.UntracedMs:F0}ms）");
        }
        finally { Directory.Delete(dir, recursive: true); }
    }
}
