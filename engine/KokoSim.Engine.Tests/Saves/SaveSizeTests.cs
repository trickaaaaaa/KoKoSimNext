using System;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Text;
using KokoSim.Engine.Nation.Roster;
using KokoSim.Engine.Saves;
using Xunit;
using Xunit.Abstractions;

namespace KokoSim.Engine.Tests.Saves;

/// <summary>
/// セーブサイズの実測（issue #507 の論点2 / #483 への書き戻し材料）。全4000校を materialize した最悪ケースの
/// JSON＋gzip 後サイズを測る。Heavy（数十秒〜）＝日常ループでは回さない。結果は out/overnight/ へも残す。
/// </summary>
[Trait("Category", "Heavy")]
public sealed class SaveSizeTests
{
    private readonly ITestOutputHelper _out;
    public SaveSizeTests(ITestOutputHelper output) => _out = output;

    private static int GzipLength(string text)
    {
        var raw = Encoding.UTF8.GetBytes(text);
        using var ms = new MemoryStream();
        using (var gz = new GZipStream(ms, CompressionLevel.Optimal, leaveOpen: true))
            gz.Write(raw, 0, raw.Length);
        return (int)ms.Length;
    }

    [Fact]
    public void FullNation_Materialized_GzipSize_IsMeasuredAndBounded()
    {
        var nation = SaveTestFixtures.BuildNation();
        var deps = new AiRosterDeps();
        var rosters = new NationRosters(deps);

        var sw = Stopwatch.StartNew();
        rosters.BootstrapAll(nation.Schools, yearIndex: 3);   // 全校を3学年ブートストラップ（最悪ケース）
        var bootstrapMs = sw.ElapsedMilliseconds;

        var state = SaveTestFixtures.BuildRichState();
        state.NationRosters = new System.Collections.Generic.List<AiSchoolRoster>(rosters.Snapshot());

        sw.Restart();
        var json = SaveCodec.Serialize(state);
        var serializeMs = sw.ElapsedMilliseconds;

        var rawBytes = Encoding.UTF8.GetByteCount(json);
        var gzBytes = GzipLength(json);

        var schoolCount = 0;
        var playerCount = 0;
        foreach (var r in rosters.Snapshot()) { schoolCount++; playerCount += r.Players.Count; }

        // 往復（デシリアライズ）も最悪ケースで通ることを確認。
        Assert.True(SaveCodec.TryDeserialize(json, out _, out var err), err);

        var report = new StringBuilder();
        report.AppendLine("# セーブサイズ実測（issue #507・全国 materialize 最悪ケース）");
        report.AppendLine($"- 学校数: {schoolCount}");
        report.AppendLine($"- 全国AI選手数: {playerCount}（平均 {(double)playerCount / schoolCount:F1} 人/校）");
        report.AppendLine($"- JSON 生サイズ: {rawBytes:N0} バイト（{rawBytes / 1024.0 / 1024.0:F2} MiB）");
        report.AppendLine($"- gzip 後サイズ: {gzBytes:N0} バイト（{gzBytes / 1024.0 / 1024.0:F2} MiB）");
        report.AppendLine($"- 圧縮率: {(double)gzBytes / rawBytes:P1}");
        report.AppendLine($"- gzip 後の1校あたり: {(double)gzBytes / schoolCount:F0} バイト");
        report.AppendLine($"- 参考時間: bootstrap {bootstrapMs} ms / serialize {serializeMs} ms");
        var text = report.ToString();
        _out.WriteLine(text);

        try
        {
            var dir = Path.Combine(FindRepoRoot(), "out", "overnight");
            Directory.CreateDirectory(dir);
            File.WriteAllText(Path.Combine(dir, "save-size-measurement.txt"), text);
        }
        catch (Exception e) { _out.WriteLine($"(レポート書き出しに失敗: {e.Message})"); }

        // 緩い上限（回帰の網。実測はレポート参照）。50 MiB を超えたら差分保存等の再検討シグナル。
        Assert.True(gzBytes < 50 * 1024 * 1024, $"gzip 後 {gzBytes} バイトが想定上限を超過");
    }

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "global.json"))) dir = dir.Parent;
        return dir?.FullName ?? Directory.GetCurrentDirectory();
    }
}
