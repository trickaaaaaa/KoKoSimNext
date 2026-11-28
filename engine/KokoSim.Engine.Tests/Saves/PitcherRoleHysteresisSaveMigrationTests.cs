using System.Linq;
using System.Text.RegularExpressions;
using KokoSim.Engine.Core;
using KokoSim.Engine.Saves;
using KokoSim.Engine.Season;
using Xunit;

namespace KokoSim.Engine.Tests.Saves;

/// <summary>
/// 投手寄り判定の遊み「前回判定」（issue #1792・v54）のセーブ往復。v54 はキーを持ち、往復で値が残る。
/// v53 以前（キーが無い）は null で読む＝次の <see cref="PitcherRoleResolver.LeansPitcher(DevelopingPlayer, RosterCoefficients)"/>
/// 呼び出しが遊み無しの素の比較で決め、以後はその結果に従う（判定の物差しが適性から能力へ変わったので、
/// 旧セーブの選手も読み込み後は新しい物差しで判定される＝これが「現行の遊み無し判定で初期値を埋める」移行の実体）。
/// </summary>
public sealed class PitcherRoleHysteresisSaveMigrationTests
{
    private static string AsLegacyV53(string json)
    {
        Assert.Contains("\"pitcherRoleHyst\":", json);
        json = Regex.Replace(json, "\"pitcherRoleHyst\":(null|true|false),", "");
        Assert.DoesNotContain("\"pitcherRoleHyst\"", json);
        return json.Replace($"\"version\":{SaveCodec.CurrentVersion}", "\"version\":53");
    }

    private static System.Collections.Generic.List<DevelopingPlayer> Roster()
        => ProspectGenerator.Intake(2, new RosterCoefficients(), new Xoshiro256Random(31)).ToList();

    [Fact]
    public void CurrentVersion_IsAtLeastV54()
        => Assert.True(SaveCodec.CurrentVersion >= 54);

    [Fact]
    public void RoundTrip_KeepsHysteresisState()
    {
        var roster = Roster();
        roster[0].PitcherRoleLastLeansPitcher = true;
        roster[1].PitcherRoleLastLeansPitcher = false;
        // roster[2] 以降は null のまま（まだ一度も判定していない選手も居ていい）。
        var json = SaveCodec.Serialize(new SaveState { MasterSeed = 5, Roster = roster });
        Assert.True(SaveCodec.TryDeserialize(json, out var s, out var err), err);

        Assert.True(s.Roster[0].PitcherRoleLastLeansPitcher);
        Assert.False(s.Roster[1].PitcherRoleLastLeansPitcher);
    }

    /// <summary>
    /// v53以前（キー欠損）は null で読み、最初の判定で能力ベースの素の比較が確定する
    /// （読み込み直後に LeansPitcher を呼んでも、能力は旧セーブのまま変わらないので決定論的に同じ値になる）。
    /// </summary>
    [Fact]
    public void LegacySave_ReadsAsNull_ThenResolvesDeterministically()
    {
        var roster = Roster();
        var json = AsLegacyV53(SaveCodec.Serialize(new SaveState { MasterSeed = 12345, Roster = roster }));

        Assert.True(SaveCodec.TryDeserialize(json, out var a, out var e1), e1);
        Assert.All(a.Roster, p => Assert.Null(p.PitcherRoleLastLeansPitcher));

        Assert.True(SaveCodec.TryDeserialize(json, out var b, out var e2), e2);
        var rc = new RosterCoefficients();
        var resultsA = a.Roster.Select(p => PitcherRoleResolver.LeansPitcher(p, rc)).ToList();
        var resultsB = b.Roster.Select(p => PitcherRoleResolver.LeansPitcher(p, rc)).ToList();
        Assert.Equal(resultsA, resultsB);
    }
}
