using System.Linq;
using KokoSim.Engine.Debugging;
using KokoSim.Engine.Match.Batting;
using KokoSim.Engine.Match.Game;
using KokoSim.Engine.Match.Pitching;
using KokoSim.Engine.Match.Timeline.Playback;
using KokoSim.Engine.Players;
using KokoSim.Engine.Tests.Match.Game;
using Xunit;

namespace KokoSim.Engine.Tests.Match.Batting;

/// <summary>打者の「慣れ」（設計書02 §2.3b・issue times-through-order-familiarity）。</summary>
public sealed class FamiliarityTests
{
    private static readonly BattingCoefficients C = new();
    private static readonly BatterAttributes Batter = new() { Contact = 50, Discipline = 50 };
    private static readonly PitchPlan Plan = new()
    {
        Type = PitchType.Fastball, AimX = 0, AimY = 0.6, VelocityKmh = 140, Stuff = 0.5, ControlLevel = 60,
    };
    private static PitchTrajectoryFeatures Feat(double breakM) => new(breakM, 0.0, 0.42);

    [Fact]
    public void ThirdPlateAppearance_WhiffsLess_ThanFirst_AndCapsAtMaxPa()
    {
        var pa1 = ContactModel.WhiffProbability(Batter, Plan, Feat(0.1), true, C, familiarityPa: 0);
        var pa3 = ContactModel.WhiffProbability(Batter, Plan, Feat(0.1), true, C, familiarityPa: 2);
        var pa9 = ContactModel.WhiffProbability(Batter, Plan, Feat(0.1), true, C, familiarityPa: 8);
        var paMax = ContactModel.WhiffProbability(Batter, Plan, Feat(0.1), true, C, familiarityPa: C.FamiliarityMaxPa);
        Assert.True(pa3 < pa1, $"3打席目 {pa3:F3} が1打席目 {pa1:F3} より空振りにくくない");
        Assert.Equal(paMax, pa9, 12);   // 上限で頭打ち
        // 控えめ（3打席目で空振り確率の相対低下は 15% 未満）。
        Assert.True((pa1 - pa3) / pa1 < 0.15, $"効きすぎ: {pa1:F3}→{pa3:F3}");
    }

    [Fact]
    public void BigBreak_DiscountsFamiliarity()
    {
        var small = ContactModel.FamiliarityEffect(2, 0.1, C);
        var big = ContactModel.FamiliarityEffect(2, 0.6, C);
        var huge = ContactModel.FamiliarityEffect(2, 1.5, C);
        Assert.True(big < small);
        Assert.Equal(0.0, huge);   // 下限0（負にならない）
        Assert.Equal(0.0, ContactModel.FamiliarityEffect(0, 0.0, C));   // 対戦回数0＝慣れ無し
    }

    [Fact]
    public void Chase_DropsWithFamiliarity()
    {
        var p0 = BatterDecision.SwingProbability(false, 0.05, 0.1, Batter, C, familiarityPa: 0);
        var p2 = BatterDecision.SwingProbability(false, 0.05, 0.1, Batter, C, familiarityPa: 2);
        Assert.True(p2 < p0);
        // ゾーン内のスイングは慣れで変えない。
        Assert.Equal(
            BatterDecision.SwingProbability(true, 0, 0.1, Batter, C, familiarityPa: 0),
            BatterDecision.SwingProbability(true, 0, 0.1, Batter, C, familiarityPa: 2), 12);
    }

    [Fact]
    public void TeamState_CountsMatchups_AndResetsForANewPitcher()
    {
        var team = DeterminismCards.Team("A");
        var state = new TeamState(team);
        var batter = team.BattingOrder[0];
        var p1 = new Player { Name = "P1", Position = KokoSim.Engine.Match.Field.FieldPosition.Pitcher };
        var p2 = new Player { Name = "P2", Position = KokoSim.Engine.Match.Field.FieldPosition.Pitcher };
        Assert.Equal(0, state.PriorPlateAppearancesAgainst(batter, p1));
        state.RecordMatchup(batter, p1);
        state.RecordMatchup(batter, p1);
        Assert.Equal(2, state.PriorPlateAppearancesAgainst(batter, p1));
        Assert.Equal(0, state.PriorPlateAppearancesAgainst(batter, p2));   // 継投＝新しい投手に対しては0
        Assert.Equal(0, state.PriorPlateAppearancesAgainst(team.BattingOrder[1], p1));
    }

    [Fact]
    public void Trace_CarriesFamiliarity_ThatGrowsThroughTheOrder()
    {
        // 観測（設計書17）: 打者一巡した後の同じ打者の PitchTrace には fam≥1 が載る。
        var sink = new ListTraceSink();
        var ctx = new GameContext { TraceSink = sink, CaptureTrace = true };
        var prog = new MatchProgression(DeterminismCards.Team("A"), DeterminismCards.Team("H"), ctx, 3UL);
        prog.AdvancePa(40);
        var pitches = sink.Pitches;
        Assert.Contains(pitches, t => t.FamiliarityPa >= 1);
        Assert.Contains(pitches, t => t.FamiliarityPa == 0);
        // 同じ打者×投手の慣れは単調非減少。
        foreach (var g in pitches.GroupBy(t => (t.BatterId, t.PitcherId)))
        {
            var last = -1;
            foreach (var t in g)
            {
                Assert.True(t.FamiliarityPa >= last);
                last = t.FamiliarityPa;
            }
        }
    }

    private sealed class ListTraceSink : IDebugTraceSink
    {
        public System.Collections.Generic.List<PitchTrace> Pitches { get; } = new();
        public void OnGameStart(GameTraceHeader header) { }
        public void OnPitch(PitchTrace t) => Pitches.Add(t);
        public void OnPlateAppearance(PaTrace t) { }
        public void OnGameEnd(GameResult result) { }
    }
}
