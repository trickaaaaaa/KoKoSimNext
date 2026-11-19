using System.Collections.Generic;
using KokoSim.Engine.Debugging;
using KokoSim.Engine.Match.Field;
using KokoSim.Engine.Match.Game;
using KokoSim.Engine.Match.Timeline.Playback;
using KokoSim.Engine.Players;
using Xunit;

namespace KokoSim.Engine.Tests.Match.Timeline;

/// <summary>
/// 観測口に投手の動揺が載る（issue #1178）。対決ビューが頭上の渦巻きと投球前の汗を出す根拠＝
/// <see cref="LiveTeamSnapshot.PitcherRattled"/> が、engine の <see cref="TeamState.PitcherRattled"/> を
/// 読むだけで正しく両校分載ることを保証する。読むだけ＝試合結果に影響しない。
/// </summary>
public sealed class MatchLiveSnapshotRattledTests
{
    private static Player P(FieldPosition pos, string name) => new()
    {
        Position = pos, Name = name,
        Contact = 50, Power = 50, LaunchTendency = 50, Discipline = 50,
        Speed = 50, ArmStrength = 50, Fielding = 50, Catching = 50,
    };

    private static Player Rp(string name) => P(FieldPosition.Pitcher, name) with { Pitching = PitcherAttributes.LeagueAverage };

    private static Team Team(string n) => new()
    {
        Name = n,
        BattingOrder = new List<Player>
        {
            P(FieldPosition.Catcher, n + "捕"), P(FieldPosition.FirstBase, n + "一"),
            P(FieldPosition.SecondBase, n + "二"), P(FieldPosition.ThirdBase, n + "三"),
            P(FieldPosition.Shortstop, n + "遊"), P(FieldPosition.LeftField, n + "左"),
            P(FieldPosition.CenterField, n + "中"), P(FieldPosition.RightField, n + "右"),
            Rp(n + "先発P"),
        },
        PitcherSlot = 8,
        Reserves = new[] { Rp(n + "二番手") },
    };

    private static MatchProgression NewProg(ulong seed, ScenarioStart? start = null)
        => new(Team("A"), Team("H"), new GameContext { CaptureTimelines = true, CapturePitches = true }, seed, start);

    // 動揺した状態で始める場面（先攻の攻撃＝後攻 home の投手が守備側）。
    private static readonly ScenarioStart RattledStart = new() { Inning = 1, IsTop = true, PitcherRattled = true };

    [Fact]
    public void Snapshot_StartsCalm_WhenNothingHappenedYet()
    {
        var prog = NewProg(11UL);
        prog.AdvancePitch();   // 1回表の最初の采配窓
        var snap = prog.Snapshot();
        Assert.False(snap.Away.PitcherRattled);
        Assert.False(snap.Home.PitcherRattled);
    }

    [Fact]
    public void Snapshot_CarriesTheDefendingPitcherRattled_FromAScenarioStart()
    {
        var prog = NewProg(11UL, RattledStart);
        prog.AdvancePitch();   // 最初の采配窓（1球も投げる前）
        var snap = prog.Snapshot();
        Assert.True(snap.Home.PitcherRattled);    // 守備側の投手
        Assert.False(snap.Away.PitcherRattled);   // 攻撃側チームの投手は載らない
    }

    [Fact]
    public void DefenseTimeout_ClearsTheRattledFlag_InTheSnapshot()
    {
        var prog = NewProg(11UL, RattledStart);
        prog.AdvancePitch();
        Assert.True(prog.Snapshot().Home.PitcherRattled);

        Assert.Equal(MatchProgression.PlayerTimeoutResult.Defense, prog.CallTimeout(teamIsAway: false));

        Assert.False(prog.Snapshot().Home.PitcherRattled);
    }

    // 自然に動揺する試合で、連続出塁で立つこと・半回が替わる最初の窓では守備に就いた投手が落ち着いていること。
    [Fact]
    public void Snapshot_RattledIsRaisedByBaserunnersAndDroppedAtTheHalfInningChange()
    {
        var sawRattled = false;
        for (var seed = 1UL; seed <= 40UL && !sawRattled; seed++)
        {
            var prog = NewProg(seed);
            (int Inning, bool Top)? lastHalf = null;
            while (true)
            {
                var r = prog.AdvancePitch();
                if (r is AdvancePitchResult.Finished or AdvancePitchResult.AwaitingForcedPitcherChange) break;
                if (r != AdvancePitchResult.Pitch || prog.CurrentAtBat is not { } at) continue;
                var snap = prog.Snapshot();
                var half = (at.Inning, at.IsTop);
                var defense = at.IsTop ? snap.Home : snap.Away;
                if (lastHalf is { } prev && prev != half)
                {
                    // 半回の替わり目の最初の窓＝守備に就いた投手は落ち着いて入り直している。
                    Assert.False(defense.PitcherRattled, $"seed={seed} {at.Inning}回{(at.IsTop ? "表" : "裏")}頭で動揺が残った");
                }
                lastHalf = half;
                if (defense.PitcherRattled) sawRattled = true;
            }
        }
        Assert.True(sawRattled, "40試合回しても投手が一度も動揺しなかった（観測口が常に false）");
    }

    // 観測口は読むだけ＝毎球スナップショットを取っても試合結果は変わらない。
    [Fact]
    public void TakingSnapshots_DoesNotChangeTheResult_WithARattledStart()
    {
        static string Run(bool snapshots)
        {
            var prog = NewProg(23UL, RattledStart);
            while (true)
            {
                var r = prog.AdvancePitch();
                if (snapshots) _ = prog.Snapshot();
                if (r == AdvancePitchResult.Finished) break;
                if (r == AdvancePitchResult.AwaitingForcedPitcherChange) break;
            }
            var g = prog.BuildResult();
            return $"{g.AwayRuns}-{g.HomeRuns}/{g.InningsPlayed}/{g.TotalPitches}";
        }
        Assert.Equal(Run(snapshots: false), Run(snapshots: true));
    }
}
