using System.Linq;
using KokoSim.Engine.Core;
using KokoSim.Engine.Match.Game;
using KokoSim.Engine.Match.Tactics;
using KokoSim.Engine.Nation;
using KokoSim.Engine.Players;
using KokoSim.Engine.Tests.Match.Game;
using Xunit;

namespace KokoSim.Engine.Tests.Match.Broadcast;

/// <summary>
/// 采配AIの伝令のログ（issue #1739）。<see cref="GameProgress.Timeouts"/>／<see cref="GameResult.TimeoutLog"/> が、
/// AIが伝令を送った打席にだけ（校・攻撃/守備・伝令役・何打席目か）を1件ずつ積み、送らない打席には積まないことを固定する。
/// </summary>
public sealed class TimeoutLogTests
{
    /// <summary>n 回目の問い合わせだけ伝令を送る（0以下＝送らない）。ほかの判断は無指示。</summary>
    private sealed class ScriptedTimeoutBrain : ITacticsBrain
    {
        private readonly int _defenseOn, _offenseOn;
        private int _defenseCalls, _offenseCalls;
        public ScriptedTimeoutBrain(int defenseOn, int offenseOn) { _defenseOn = defenseOn; _offenseOn = offenseOn; }

        public OffensiveSign CallOffense(in TacticsSituation s, IRandomSource rng) => OffensiveSign.Swing;
        public DefensiveTactics CallDefense(in TacticsSituation s, IRandomSource rng) => DefensiveTactics.Default;
        public bool CallOffenseTimeout(in TacticsSituation s, IRandomSource rng) => ++_offenseCalls == _offenseOn;
        public bool CallDefenseTimeout(in TacticsSituation s, IRandomSource rng) => ++_defenseCalls == _defenseOn;
        public Player? CallPinchHit(in SubstitutionSituation s, IRandomSource rng) => null;
        public (Player Runner, Player Sub)? CallPinchRun(in SubstitutionSituation s, IRandomSource rng) => null;
        public (Player Out, Player Sub)? CallDefensiveSub(in SubstitutionSituation s, IRandomSource rng) => null;
        public PitchingChangeDecision? CallPitchingChange(in PitchingChangeSituation s, IRandomSource rng)
            => s.FatigueTriggered || s.AtWeeklyLimit ? new PitchingChangeDecision(PitchingChangeReason.Fatigue) : null;
    }

    private static GameResult Play(ITacticsBrain? homeBrain)
    {
        var rng = new Xoshiro256Random(11UL);
        var away = StrengthTeamFactory.Create(55, "A", rng);
        var home = StrengthTeamFactory.Create(55, "B", rng) with { Tactics = homeBrain };
        return GameEngine.Play(away, home, new GameContext(), rng);
    }

    [Fact]
    public void NoTimeoutCalled_LeavesNoEntry()
    {
        Assert.Empty(Play(new ScriptedTimeoutBrain(0, 0)).TimeoutLog);
        Assert.Empty(Play(null).TimeoutLog);
    }

    [Fact]
    public void DefenseTimeout_IsLoggedOnceAtThePlateAppearanceItWasSent()
    {
        // ホームは表の守備＝3回目の問い合わせは1回表の3打席目（打席の添字2）
        var result = Play(new ScriptedTimeoutBrain(defenseOn: 3, offenseOn: 0));

        var e = Assert.Single(result.TimeoutLog);
        Assert.False(e.IsOffense);
        Assert.False(e.TeamIsAway);
        Assert.True(e.IsTopHalf);
        Assert.Equal(1, e.Inning);
        Assert.Equal(2, e.PlateAppearance);
        Assert.Equal(1, result.Log[e.PlateAppearance].Inning);
        Assert.True(result.Log[e.PlateAppearance].IsTop);
    }

    [Fact]
    public void OffenseTimeout_IsLoggedOnceWithTheBottomHalfAndTheHomeTeam()
    {
        var result = Play(new ScriptedTimeoutBrain(defenseOn: 0, offenseOn: 2));

        var e = Assert.Single(result.TimeoutLog);
        Assert.True(e.IsOffense);
        Assert.False(e.TeamIsAway);
        Assert.False(e.IsTopHalf);
        Assert.Equal(1, e.Inning);
        var pa = result.Log[e.PlateAppearance];
        Assert.Equal(1, pa.Inning);
        Assert.False(pa.IsTop);
    }

    [Fact]
    public void Messenger_IsTheBenchPlayerWhoWasPicked_OrNullWhenNobodyIsLeft()
    {
        var rng = new Xoshiro256Random(11UL);
        var away = StrengthTeamFactory.Create(55, "A", rng);
        var home = StrengthTeamFactory.Create(55, "B", rng) with { Tactics = new ScriptedTimeoutBrain(defenseOn: 1, offenseOn: 0) };
        var result = GameEngine.Play(away, home, new GameContext(), rng);

        var e = Assert.Single(result.TimeoutLog);
        if (e.Messenger is { } m)
            Assert.True(home.Reserves.Any(r => r.Name == m.Name && r.UniformNumber == m.Number));
    }

    [Fact]
    public void Log_DoesNotChangeTheGameResultDigest()
    {
        var a = Play(new ScriptedTimeoutBrain(defenseOn: 3, offenseOn: 2));
        var b = Play(new ScriptedTimeoutBrain(defenseOn: 3, offenseOn: 2));
        Assert.Equal(2, a.TimeoutLog.Count);
        Assert.Equal(GameResultDigest.Sha256Of(a), GameResultDigest.Sha256Of(b));
    }
}

public sealed class TimeoutTelopTests
{
    [Fact]
    public void Row_ShowsNumberAndName_OrTheRecorderWhenNobodyRuns()
    {
        var row = KokoSim.Engine.Match.Broadcast.TimeoutTelop.Row(new SubstitutionPlayer(7, 13, "田中 一郎"));
        Assert.Equal(("伝令", 13, "田中 一郎", ""), (row.Kind, row.Number, row.Name, row.Position));

        var recorder = KokoSim.Engine.Match.Broadcast.TimeoutTelop.Row(null);
        Assert.Equal((0, "記録員"), (recorder.Number, recorder.Name));
    }

    [Fact]
    public void HistoryLine_NamesTheTeamTheSideAndTheMessenger()
    {
        var def = new TimeoutEvent(3, true, false, false, 9, new SubstitutionPlayer(null, 12, "田中 一郎"));
        Assert.Equal("3回表 桜丘 守備伝令 田中 一郎", KokoSim.Engine.Match.Broadcast.TimeoutTelop.HistoryLine(def, "桜丘"));

        var off = new TimeoutEvent(5, false, true, true, 20, null);
        Assert.Equal("5回裏 北都 攻撃伝令 記録員", KokoSim.Engine.Match.Broadcast.TimeoutTelop.HistoryLine(off, "北都"));
    }
}
