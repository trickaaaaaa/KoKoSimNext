using System.Collections.Generic;
using KokoSim.Engine.Core;
using KokoSim.Engine.Match.Field;
using KokoSim.Engine.Match.Game;
using KokoSim.Engine.Match.Timeline.Playback;
using KokoSim.Engine.Players;
using KokoSim.Engine.Tests.Match.Game;
using Xunit;

namespace KokoSim.Engine.Tests.Match.Timeline;

/// <summary>
/// プレイヤー発動の伝令（タイム。設計書09 §3・issue #173）。攻守判定・消費・残数境界と、
/// 「伝令は乱数を消費しない＝同シード＋同操作で結果が再現する」決定論（不変条件#2）を保証する。
/// </summary>
public sealed class MatchProgressionTimeoutTests
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

    private static MatchProgression NewProg(ulong seed)
        => new(Team("A"), Team("H"), new GameContext { CaptureTimelines = true }, seed);

    // ===== 1. 攻守判定（表裏×ManagerIsAway） =====

    [Fact]
    public void FirstHalf_AwayIsBatting_HomeIsFielding()
    {
        var prog = NewProg(11UL);
        prog.Advance();   // 1回表（先攻 away の攻撃）に入る
        Assert.True(prog.IsTeamOnOffense(teamIsAway: true));
        Assert.False(prog.IsTeamOnOffense(teamIsAway: false));
    }

    // ===== 2. 消費（攻守で減る側が違う） =====

    [Fact]
    public void OffenseTimeout_WhileBatting_ConsumesOnlyTheOffenseCounter()
    {
        var prog = NewProg(11UL);
        prog.Advance();
        Assert.Equal(3, prog.OffenseTimeoutsLeft(teamIsAway: true));

        Assert.Equal(MatchProgression.PlayerTimeoutResult.Offense, prog.CallTimeout(teamIsAway: true));

        Assert.Equal(2, prog.OffenseTimeoutsLeft(teamIsAway: true));
        Assert.Equal(3, prog.DefenseTimeoutsLeft(teamIsAway: true));   // 守備伝令は減らない
    }

    [Fact]
    public void DefenseTimeout_WhileFielding_ConsumesOnlyTheDefenseCounter()
    {
        var prog = NewProg(11UL);
        prog.Advance();
        Assert.Equal(3, prog.DefenseTimeoutsLeft(teamIsAway: false));   // home は守備側

        Assert.Equal(MatchProgression.PlayerTimeoutResult.Defense, prog.CallTimeout(teamIsAway: false));

        Assert.Equal(2, prog.DefenseTimeoutsLeft(teamIsAway: false));
        Assert.Equal(3, prog.OffenseTimeoutsLeft(teamIsAway: false));   // 攻撃伝令は減らない
    }

    // ===== 3. 残数境界（0で不可・負にならない） =====

    [Fact]
    public void Timeout_Exhausted_ReturnsUnavailable_AndNeverGoesNegative()
    {
        var prog = NewProg(11UL);
        prog.Advance();
        for (var i = 0; i < 3; i++)
            Assert.Equal(MatchProgression.PlayerTimeoutResult.Offense, prog.CallTimeout(teamIsAway: true));

        Assert.Equal(0, prog.OffenseTimeoutsLeft(teamIsAway: true));
        Assert.False(prog.CanCallTimeout(teamIsAway: true));
        Assert.Equal(MatchProgression.PlayerTimeoutResult.Unavailable, prog.CallTimeout(teamIsAway: true));
        Assert.Equal(0, prog.OffenseTimeoutsLeft(teamIsAway: true));   // 0 のまま
    }

    [Fact]
    public void Timeout_AfterGameEnd_IsUnavailable()
    {
        var prog = NewProg(11UL);
        while (prog.Advance()) { /* 最後まで走らせる */ }
        Assert.False(prog.CanCallTimeout(teamIsAway: true));
        Assert.Equal(MatchProgression.PlayerTimeoutResult.Unavailable, prog.CallTimeout(teamIsAway: true));
    }

    // ===== 4. 決定論（同シード＋同操作 → 同結果。伝令は乱数を消費しない） =====

    [Theory]
    [InlineData(3UL)]
    [InlineData(31UL)]
    public void SameSeedAndSameTimeouts_ProduceTheSameResult(ulong seed)
    {
        static string Run(ulong s)
        {
            var prog = NewProg(s);
            var offUsed = 0;
            var defUsed = 0;
            while (prog.Advance())
            {
                // 攻撃中の away・守備中の away で、残があるうちに1つずつ伝令を試す（決まった操作列）。
                if (prog.IsTeamOnOffense(teamIsAway: true) && offUsed < 3 && prog.ConfirmedPlateAppearances > 4)
                {
                    if (prog.CallTimeout(teamIsAway: true) == MatchProgression.PlayerTimeoutResult.Offense) offUsed++;
                }
                else if (!prog.IsTeamOnOffense(teamIsAway: true) && defUsed < 3 && prog.ConfirmedPlateAppearances > 8)
                {
                    if (prog.CallTimeout(teamIsAway: true) == MatchProgression.PlayerTimeoutResult.Defense) defUsed++;
                }
            }
            return GameResultDigest.Sha256Of(prog.BuildResult());
        }

        Assert.Equal(Run(seed), Run(seed));
    }

    // ===== 5. 延長の枠（特別規則 15(2)②(3)②: それ以前の回数に関係なく1イニングにつき1回だけ） =====

    // 規定回を1回にして延長へ入りやすくし、延長回の打席境界では枠が1・残りも1以下（持ち越さない）を見る。
    // 規定回の間は枠3。延長に1度も入らなかったら検査が空振りなのでシードを足す。
    [Fact]
    public void TimeoutsPerPeriod_IsThreeInRegulation_AndOneInExtraInnings()
    {
        var sawExtra = false;
        for (var seed = 1UL; seed <= 40UL && !sawExtra; seed++)
        {
            var prog = new MatchProgression(Team("A"), Team("H"),
                new GameContext { CaptureTimelines = true, RegulationInnings = 1 }, seed);
            while (prog.Advance())
            {
                if (prog.Current is not { } pa) continue;
                if (pa.Inning <= 1)
                {
                    Assert.Equal(TeamState.TimeoutsPerGame, prog.TimeoutsPerPeriod);
                    continue;
                }
                sawExtra = true;
                Assert.Equal(TeamState.TimeoutsPerExtraInning, prog.TimeoutsPerPeriod);
                foreach (var away in new[] { true, false })
                {
                    Assert.True(prog.OffenseTimeoutsLeft(away) <= 1);
                    Assert.True(prog.DefenseTimeoutsLeft(away) <= 1);
                }
            }
        }
        Assert.True(sawExtra, "延長に入る試合が1つも無かった");
    }
    // ===== 5b. 3アウト目の打球の再生中（issue #1246）: 攻守は画面に映っている半回で決める =====

    // エンジンは3アウト目の打席を確定した時点で「次の半回」へ攻守が入れ替わる（SubstitutionCommands.IsOnOffense）。
    // 画面はまだその打球を再生している＝shownOffenseIsTop を渡せば画面の局面で答える。そのずれた境界まで進める。
    private static (MatchProgression Prog, bool ShownIsTop) AdvanceToHalfEndedBoundary(ulong seed)
    {
        var prog = NewProg(seed);
        while (prog.Advance())
        {
            var shownIsTop = prog.Current!.IsTop;
            // 先攻 away が攻撃中（表）なのに、エンジンは away を守備側と答える＝3アウトで半回が閉じた境界。
            if (prog.IsTeamOnOffense(teamIsAway: true) != shownIsTop) return (prog, shownIsTop);
        }
        throw new Xunit.Sdk.XunitException("3アウトで終わる打席が1つも無かった");
    }

    [Fact]
    public void ShownHalf_OverridesTheEngineHalfAtThreeOuts_ForOffenseAndOptionsAndTimeout()
    {
        var (prog, shownIsTop) = AdvanceToHalfEndedBoundary(11UL);
        var shownTeamIsAway = shownIsTop;   // 画面で攻撃しているのは、表なら away・裏なら home

        // 既定（従来どおり）はエンジンの次の半回＝画面とは逆に見えてしまう。
        Assert.False(prog.IsTeamOnOffense(shownTeamIsAway));
        Assert.False(prog.SubstitutionOptions(shownTeamIsAway).IsOffense);

        // 画面の半回を渡すと、画面で攻撃中の側は攻撃・守備中の側は守備。
        Assert.True(prog.IsTeamOnOffense(shownTeamIsAway, shownIsTop));
        Assert.False(prog.IsTeamOnOffense(!shownTeamIsAway, shownIsTop));
        Assert.True(prog.SubstitutionOptions(shownTeamIsAway, shownIsTop).IsOffense);
        Assert.False(prog.SubstitutionOptions(!shownTeamIsAway, shownIsTop).IsOffense);
        Assert.Empty(prog.SubstitutionOptions(shownTeamIsAway, shownIsTop).Runners);   // 3アウトで塁は空
    }

    [Fact]
    public void Timeout_AtThreeOuts_UsesTheShownHalf_NotTheEngineNextHalf()
    {
        var (prog, shownIsTop) = AdvanceToHalfEndedBoundary(11UL);
        var fielding = !shownIsTop;   // 画面で守備中の側（2死から3アウト目を取る側）

        Assert.True(prog.CanCallTimeout(fielding, shownIsTop));
        Assert.Equal(MatchProgression.PlayerTimeoutResult.Defense, prog.CallTimeout(fielding, null, shownIsTop));

        Assert.Equal(2, prog.DefenseTimeoutsLeft(fielding));
        Assert.Equal(3, prog.OffenseTimeoutsLeft(fielding));   // 次の半回の攻撃伝令としては消費されない
    }

    // 画面の半回で確定した伝令は、決定列にも攻守を残す＝中断保存・シークの再生でも同じ側に効く。
    [Fact]
    public void Timeout_AtThreeOuts_ReplaysOnTheSameSide()
    {
        var (prog, shownIsTop) = AdvanceToHalfEndedBoundary(11UL);
        var fielding = !shownIsTop;
        prog.CallTimeout(fielding, null, shownIsTop);

        var save = prog.Save();
        Assert.Contains(save.Decisions, d => d.Kind == GameDecisionKind.Timeout && d.AsOffense == false);

        var resumed = GameReplay.Restore(Team("A"), Team("H"), new GameContext { CaptureTimelines = true }, save);
        while (resumed.Steps.MoveNext()) { /* 保存位置まで再生 */ }
        var team = resumed.Progress.OffenseOf(fielding);
        Assert.Equal(2, team.DefenseTimeoutsLeft);
        Assert.Equal(3, team.OffenseTimeoutsLeft);
    }

    // ===== 6. 観測口（issue #1177）: 伝令が効いているかを MatchLiveSnapshot に読むだけで載せる =====

    // 伝令の後、その校が該当する側（守備伝令＝守備中／攻撃伝令＝攻撃中）で迎える打席を数え、
    // 「効いている」がちょうど効果窓（3打席）のあいだだけ立ち、切れたら倒れることを見る。
    // 攻撃伝令は打者が替わっても・半イニングをまたいでも、その校の攻撃の3打席ぶん続く。
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Snapshot_CarriesTimeoutCalm_OnlyWhileTheWindowLasts(bool offenseTimeout)
    {
        var prog = NewProg(11UL);
        prog.Advance();   // 1回表＝away の攻撃・home の守備
        var before = prog.Snapshot();
        Assert.False(before.Away.OffenseCalmActive || before.Away.DefenseCalmActive);
        Assert.False(before.Home.OffenseCalmActive || before.Home.DefenseCalmActive);

        var teamIsAway = offenseTimeout;   // 攻撃伝令は away、守備伝令は home が送る
        Assert.Equal(offenseTimeout ? MatchProgression.PlayerTimeoutResult.Offense : MatchProgression.PlayerTimeoutResult.Defense,
            prog.CallTimeout(teamIsAway));

        var covered = 0;
        while (true)
        {
            var snap = prog.Snapshot();
            var team = teamIsAway ? snap.Away : snap.Home;
            var other = teamIsAway ? snap.Home : snap.Away;
            var active = offenseTimeout ? team.OffenseCalmActive : team.DefenseCalmActive;
            // 送っていない側の効果は立たない（相手校は伝令を送っていない＝采配AIなし）。
            Assert.False(offenseTimeout ? team.DefenseCalmActive : team.OffenseCalmActive);
            Assert.False(other.OffenseCalmActive || other.DefenseCalmActive);
            if (!active) break;
            if (prog.IsTeamOnOffense(teamIsAway) == offenseTimeout) covered++;
            Assert.True(prog.Advance(), "効果窓が切れる前に試合が終わった");
        }
        Assert.Equal(new GameContext().Tactics.TimeoutDurationPa, covered);
    }

    // 観測口は読むだけ＝毎打席スナップショットを取っても試合結果は変わらない。
    [Fact]
    public void TakingSnapshots_DoesNotChangeTheResult()
    {
        static string Run(bool snapshots)
        {
            var prog = NewProg(11UL);
            var called = false;
            while (prog.Advance())
            {
                if (!called && prog.ConfirmedPlateAppearances > 4)
                    called = prog.CallTimeout(teamIsAway: true) != MatchProgression.PlayerTimeoutResult.Unavailable;
                if (snapshots) _ = prog.Snapshot();
            }
            return GameResultDigest.Sha256Of(prog.BuildResult());
        }

        Assert.Equal(Run(false), Run(true));
    }
}
