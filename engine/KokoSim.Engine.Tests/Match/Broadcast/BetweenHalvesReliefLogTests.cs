using System.Collections.Generic;
using System.Linq;
using KokoSim.Engine.Core;
using KokoSim.Engine.Match.Broadcast;
using KokoSim.Engine.Match.Field;
using KokoSim.Engine.Match.Game;
using KokoSim.Engine.Match.Tactics;
using KokoSim.Engine.Match.Timeline.Playback;
using KokoSim.Engine.Players;
using Xunit;

namespace KokoSim.Engine.Tests.Match.Broadcast;

/// <summary>
/// 回の切れ目の投手交代（issue #1052・2026-11-17 ユーザー確認）。3アウトになったら攻守交代で、3アウト直後には
/// 継投を判断しない。継投は「イニングの途中」か「守備に就く半回の頭（最初の打者を迎える前）」で行い、交代は
/// 起きたその場で交代ログ（テロップ・実況履歴の元）に積む。認める流れ:
/// ①回の頭から新しい投手 ②攻撃中に投手へ代打→その代打がそのまま次の回に投げる ③攻撃中に投手へ代打→次の回の頭から別の投手。
/// </summary>
public sealed class BetweenHalvesReliefLogTests
{
    private static Player P(FieldPosition pos, string name, int number = 0) => new()
    {
        Position = pos, Name = name, UniformNumber = number,
        Contact = 50, Power = 50, LaunchTendency = 50, Discipline = 50,
        Speed = 50, ArmStrength = 50, Fielding = 50, Catching = 50,
    };

    private static Player Rp(string name, int number = 0)
        => P(FieldPosition.Pitcher, name, number) with { Pitching = PitcherAttributes.LeagueAverage };

    /// <summary>非DH。投手は <paramref name="pitcherSlot"/> 番目（0始まり）の打順に入る。</summary>
    private static Team Team(string n, int pitcherSlot = 8, ITacticsBrain? brain = null)
    {
        var fielders = new List<Player>
        {
            P(FieldPosition.Catcher, n + "捕", 2), P(FieldPosition.FirstBase, n + "一", 3),
            P(FieldPosition.SecondBase, n + "二", 4), P(FieldPosition.ThirdBase, n + "三", 5),
            P(FieldPosition.Shortstop, n + "遊", 6), P(FieldPosition.LeftField, n + "左", 7),
            P(FieldPosition.CenterField, n + "中", 8), P(FieldPosition.RightField, n + "右", 9),
        };
        fielders.Insert(pitcherSlot, Rp(n + "先発P", 1));
        return new Team
        {
            Name = n,
            BattingOrder = fielders,
            PitcherSlot = pitcherSlot,
            Tactics = brain,
            Reserves = TeamReserves.Merge(
                new[] { Rp(n + "二番手", 10), Rp(n + "三番手", 11) },
                new[] { P(FieldPosition.FirstBase, n + "控1", 13), P(FieldPosition.SecondBase, n + "控2", 14) }),
        };
    }

    private static Team DhTeam(string n, ITacticsBrain? brain = null) => new()
    {
        Name = n,
        BattingOrder = new List<Player>
        {
            P(FieldPosition.Catcher, n + "捕"), P(FieldPosition.FirstBase, n + "一"),
            P(FieldPosition.SecondBase, n + "二"), P(FieldPosition.ThirdBase, n + "三"),
            P(FieldPosition.Shortstop, n + "遊"), P(FieldPosition.LeftField, n + "左"),
            P(FieldPosition.CenterField, n + "中"), P(FieldPosition.RightField, n + "右"),
            P(FieldPosition.Catcher, n + "DH"),
        },
        DhSlot = 8,
        StartingPitcher = Rp(n + "先発P", 1),
        Tactics = brain,
        Reserves = TeamReserves.Merge(new[] { Rp(n + "二番手", 10), Rp(n + "三番手", 11) }, new[] { P(FieldPosition.FirstBase, n + "控1", 13) }),
    };

    private static GameContext Ctx() => new() { CaptureTimelines = true, CapturePitches = true };

    /// <summary>
    /// 筋書きどおりに動くブレイン。継投判断の状況をすべて記録する。
    /// <paramref name="reliefAtHeadOf"/> の回は、その回の最初の継投判断（＝半回の頭）で継投する。
    /// <paramref name="pinchHitForPitcher"/> なら、1回に投手の打順が回ってきたら野手の控えを代打に送る。
    /// </summary>
    private sealed class ScriptBrain(int? reliefAtHeadOf = null, bool pinchHitForPitcher = false) : ITacticsBrain
    {
        public List<PitchingChangeSituation> Seen { get; } = new();

        public OffensiveSign CallOffense(in TacticsSituation s, IRandomSource rng) => OffensiveSign.Swing;
        public DefensiveTactics CallDefense(in TacticsSituation s, IRandomSource rng) => DefensiveTactics.Default;
        public bool CallOffenseTimeout(in TacticsSituation s, IRandomSource rng) => false;
        public bool CallDefenseTimeout(in TacticsSituation s, IRandomSource rng) => false;

        public Player? CallPinchHit(in SubstitutionSituation s, IRandomSource rng)
            => pinchHitForPitcher && s.Inning == 1 && s.UpcomingBatterIsPitcher
                ? s.Reserves.First(x => x.Position != FieldPosition.Pitcher)
                : null;

        public (Player Runner, Player Sub)? CallPinchRun(in SubstitutionSituation s, IRandomSource rng) => null;
        public (Player Out, Player Sub)? CallDefensiveSub(in SubstitutionSituation s, IRandomSource rng) => null;

        public PitchingChangeDecision? CallPitchingChange(in PitchingChangeSituation s, IRandomSource rng)
        {
            var inning = s.Inning;
            var firstOfInning = Seen.All(x => x.Inning != inning);
            Seen.Add(s);
            return firstOfInning && s.Inning == reliefAtHeadOf
                ? new PitchingChangeDecision(PitchingChangeReason.Fatigue)
                : null;
        }
    }

    private static List<SubstitutionEvent> HomeEvents(MatchProgression prog)
        => prog.Substitutions.Where(e => !e.TeamIsAway).ToList();

    /// <summary>指定の半回の先頭打者の1球目の窓まで進める（その時点の交代ログと対戦を見る）。</summary>
    private static LiveAtBat ToHalfHead(MatchProgression prog, int inning, bool isTop)
    {
        while (true)
        {
            var r = prog.AdvancePitch();
            Assert.NotEqual(AdvancePitchResult.Finished, r);
            if (r == AdvancePitchResult.Pitch && prog.CurrentAtBat is { PitchNumberInPa: 1 } at
                && at.Inning == inning && at.IsTop == isTop) return at;
        }
    }

    [Fact]
    public void PitchingChange_IsNeverJudgedAtTheThirdOut_ButAtTheHeadOfEachLaterDefensiveHalf()
    {
        // 後攻（1回表から毎回守る）の継投判断の状況を全部見る
        var brain = new ScriptBrain();
        var prog = new MatchProgression(Team("A"), Team("H", brain: brain), Ctx(), 7UL);
        prog.FinishRemaining();

        Assert.NotEmpty(brain.Seen);
        Assert.All(brain.Seen, s => Assert.True(s.Outs < 3, $"{s.Inning}回 3アウト直後に継投を判断した"));
        // 2回以降は、その回の最初の判断が頭（無死・失点0）。1回は先発の頭なので判断しない。
        foreach (var inning in brain.Seen.Select(s => s.Inning).Distinct().Where(i => i >= 2))
        {
            var head = brain.Seen.First(s => s.Inning == inning);
            Assert.Equal(0, head.Outs);
            Assert.Equal(0, head.RunsAllowedThisInning);
        }
        Assert.Contains(brain.Seen, s => s.Inning == 2);
    }

    [Fact]
    public void Flow1_ReliefAtTheHeadOfTheInning_IsLoggedRightThere()
    {
        var prog = new MatchProgression(DhTeam("A"), DhTeam("H", new ScriptBrain(reliefAtHeadOf: 2)), Ctx(), 7UL);
        ToHalfHead(prog, 1, isTop: false);
        Assert.Empty(HomeEvents(prog));   // 1回裏（後攻の攻撃）には何も起きていない

        var at = ToHalfHead(prog, 2, isTop: true);
        Assert.Equal("H二番手", at.PitcherName);   // 2回表の1球目から新しい投手
        var e = Assert.Single(HomeEvents(prog));
        Assert.Equal((2, true), (e.Inning, e.IsTopHalf));
        Assert.Equal(new SubstitutionEntry(SubstitutionEntryKind.Relief, 0,
                new SubstitutionPlayer(null, 10, "H二番手"), new SubstitutionPlayer(null, 1, "H先発P"), FieldPosition.Pitcher),
            Assert.Single(e.Entries));
        Assert.Equal("2回表 投手交代 H二番手（ピッチャー） ← H先発P", SubstitutionTelop.HistoryLine(e));
    }

    [Fact]
    public void Flow2_PinchHitterForThePitcher_PitchesTheNextInning()
    {
        // 後攻の投手が1回裏の先頭打者。代打を送り、その代打が2回表にそのまま投げる。
        var prog = new MatchProgression(
            Team("A"), Team("H", pitcherSlot: 0, new ScriptBrain(pinchHitForPitcher: true)), Ctx(), 7UL);
        var at = ToHalfHead(prog, 2, isTop: true);
        Assert.Equal("H控1", at.PitcherName);

        var home = HomeEvents(prog);
        Assert.Equal(2, home.Count);

        var ph = home[0];
        Assert.Equal((1, false), (ph.Inning, ph.IsTopHalf));
        Assert.Equal(new SubstitutionEntry(SubstitutionEntryKind.PinchHit, 1,
                new SubstitutionPlayer(null, 13, "H控1"), new SubstitutionPlayer(null, 1, "H先発P"), FieldPosition.Pitcher),
            Assert.Single(ph.Entries));
        Assert.Equal("1回裏 代打 H控1 ← H先発P", SubstitutionTelop.HistoryLine(ph));
        Assert.Equal(new TelopRow("代打", "1番", 13, "H控1", "", "H先発P"),
            Assert.Single(Assert.Single(SubstitutionTelop.Build(new[] { ph })).Rows));

        var mound = home[1];
        Assert.Equal((2, true), (mound.Inning, mound.IsTopHalf));
        Assert.Equal(new SubstitutionEntry(SubstitutionEntryKind.PositionChange, 1,
                new SubstitutionPlayer(null, 13, "H控1"), null, FieldPosition.Pitcher),
            Assert.Single(mound.Entries));
        Assert.Equal("2回表 守備位置変更 H控1（ピッチャー）", SubstitutionTelop.HistoryLine(mound));
        Assert.Equal(new TelopRow("守備位置変更", "1番", 13, "H控1", "ピッチャー", null),
            Assert.Single(Assert.Single(SubstitutionTelop.Build(new[] { mound })).Rows));

        // 投手成績にも代打の選手が登板した投手として載る
        var result = prog.FinishRemaining();
        Assert.Contains(result.HomePitching, l => l.Name == "H控1" && l.Pitches > 0);
    }

    [Fact]
    public void Flow3_PinchHitterForThePitcher_ThenAnotherPitcherFromTheHeadOfTheNextInning()
    {
        var prog = new MatchProgression(
            Team("A"), Team("H", pitcherSlot: 0, new ScriptBrain(reliefAtHeadOf: 2, pinchHitForPitcher: true)), Ctx(), 7UL);
        var at = ToHalfHead(prog, 2, isTop: true);
        Assert.Equal("H二番手", at.PitcherName);

        var home = HomeEvents(prog);
        Assert.Equal(2, home.Count);
        Assert.Equal("1回裏 代打 H控1 ← H先発P", SubstitutionTelop.HistoryLine(home[0]));

        var relief = home[1];
        Assert.Equal((2, true), (relief.Inning, relief.IsTopHalf));
        Assert.Equal(new SubstitutionEntry(SubstitutionEntryKind.Relief, 1,
                new SubstitutionPlayer(null, 10, "H二番手"), new SubstitutionPlayer(null, 13, "H控1"), FieldPosition.Pitcher),
            Assert.Single(relief.Entries));
        Assert.Equal("2回表 投手交代 H二番手（ピッチャー） ← H控1", SubstitutionTelop.HistoryLine(relief));

        // 代打の選手は投げずに退いた＝投手成績に載らない
        var result = prog.FinishRemaining();
        Assert.DoesNotContain(result.HomePitching, l => l.Name == "H控1");
        Assert.Contains(result.HomePitching, l => l.Name == "H二番手");
    }

    [Fact]
    public void PlayerPinchHitForThePitcher_ThePinchHitterTakesTheMound_AndReplayMatches()
    {
        // 自校の采配（代打のコマンド）でも同じ流れ。取り消しの再生でも同じログになる。
        var prog = new MatchProgression(Team("A"), Team("H", pitcherSlot: 0), Ctx(), 7UL);
        var head = ToHalfHead(prog, 1, isTop: false);
        Assert.Equal(1, head.BatterOrder);
        var sub = prog.SubstitutionOptions(teamIsAway: false).Reserves.First(x => x.Name == "H控1");
        Assert.True(prog.PinchHit(teamIsAway: false, sub));

        var at = ToHalfHead(prog, 2, isTop: true);
        Assert.Equal("H控1", at.PitcherName);
        Assert.Equal(new[] { "1回裏 代打 H控1 ← H先発P", "2回表 守備位置変更 H控1（ピッチャー）" },
            HomeEvents(prog).Select(SubstitutionTelop.HistoryLine));

        var before = prog.Substitutions.Select(SubstitutionTelop.HistoryLine).ToList();
        var save = prog.Save();
        prog.SeekTo(save.ConfirmedPlateAppearances, save.ConfirmedPitchesInCurrentPa);
        Assert.Equal(before, prog.Substitutions.Select(SubstitutionTelop.HistoryLine).ToList());
    }

    [Fact]
    public void Logging_DoesNotChangeTheGame()
    {
        // 交代ログは観測専用＝ログの有無で試合結果は変わらない
        for (ulong seed = 1; seed <= 3; seed++)
        {
            var a = new MatchProgression(Team("A"), Team("H", 0, new ScriptBrain(2, true)), Ctx(), seed).FinishRemaining();
            var b = new MatchProgression(Team("A"), Team("H", 0, new ScriptBrain(2, true)),
                new GameContext { CaptureTimelines = true }, seed).FinishRemaining();
            Assert.Equal(a.AwayRuns, b.AwayRuns);
            Assert.Equal(a.HomeRuns, b.HomeRuns);
            Assert.Equal(a.Log.Select(l => l.Result), b.Log.Select(l => l.Result));
        }
    }
}
