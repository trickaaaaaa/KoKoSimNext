using System;
using System.Collections.Generic;
using System.Linq;
using KokoSim.Engine.Core;
using KokoSim.Engine.Match.Field;
using KokoSim.Engine.Match.Game;
using KokoSim.Engine.Players;
using KokoSim.Engine.Saves;
using KokoSim.Engine.Season;
using Xunit;

namespace KokoSim.Engine.Tests.Match.Game;

/// <summary>バッテリーの相性（設計書23 §2・issue #933）。S0 の式・B の育ち方・効き先（K=0 で恒等）・自校の記録の保存。</summary>
public sealed class BatteryChemistryTests
{
    private static readonly BatteryChemistryCoefficients C = new();

    private static Player P(int leadership = 50, int receptiveness = 50, int selflessness = 50, int grade = 0, int? id = null)
        => new() { Name = "x", Leadership = leadership, Receptiveness = receptiveness, Selflessness = selflessness, Grade = grade, SourceId = id };

    // ===== S0 =====

    [Fact]
    public void StartingAffinity_NeutralPairIsZero()
        => Assert.Equal(0.0, BatteryChemistry.StartingAffinity(P(), P(), C), 12);

    [Fact]
    public void StartingAffinity_FollowsDesignFormula()
    {
        // 捕手が引っ張る（統率75＝d0.5）→ +0.35×0.5
        Assert.Equal(0.175, BatteryChemistry.StartingAffinity(P(), P(leadership: 75), C), 12);
        // 投手が素直（素直さ75）→ +0.35×0.5
        Assert.Equal(0.175, BatteryChemistry.StartingAffinity(P(receptiveness: 75), P(), C), 12);
        // 2人とも目立ちたがり（姿勢25＝d−0.5）→ −0.30×0.25
        Assert.Equal(-0.075, BatteryChemistry.StartingAffinity(P(selflessness: 25), P(selflessness: 25), C), 12);
        // 目立ちたがりの投手×献身的な捕手＝女房役 → +0.30×0.5×0.5
        Assert.Equal(0.075, BatteryChemistry.StartingAffinity(P(selflessness: 25), P(selflessness: 75), C), 12);
        // 献身的な投手×目立ちたがりの捕手は女房役にならない（向きがある）
        Assert.Equal(0.0, BatteryChemistry.StartingAffinity(P(selflessness: 75), P(selflessness: 25), C), 12);
    }

    [Fact]
    public void StartingAffinity_IsClampedToUnitRange()
    {
        var c = C with { LeadershipWeight = 5, ReceptivenessWeight = 5 };
        Assert.Equal(1.0, BatteryChemistry.StartingAffinity(P(receptiveness: 99), P(leadership: 99), c), 12);
        Assert.Equal(-1.0, BatteryChemistry.StartingAffinity(P(receptiveness: 1), P(leadership: 1), c), 12);
    }

    [Fact]
    public void StartingAffinity_PopulationMeanIsSmall()
    {
        // 生成した投手×捕手の S0 は平均でほぼ0（設計書23 §2.2）。大きく偏っていないことだけ見る。
        var pc = new PersonalityCoefficients();
        var root = new Xoshiro256Random(0x5EED_2330UL);
        var sum = 0.0;
        const int n = 10_000;
        for (var i = 0; i < n; i++)
        {
            Player Gen(ulong salt)
            {
                var d = pc.SampleAxes(root.Fork(salt));
                var lead = (int)MathUtil.Clamp(Math.Round(50 + pc.Profile(d.SeedType).LeadershipMeanOffset), 1, 99);
                return P(lead, d.Receptiveness, d.Selflessness);
            }
            sum += BatteryChemistry.StartingAffinity(Gen((ulong)(2 * i)), Gen((ulong)(2 * i + 1)), C);
        }
        Assert.InRange(sum / n, -0.15, 0.15);
    }

    // ===== B =====

    [Fact]
    public void Bond_GrowsWithInnings_AndSaturates()
    {
        Assert.Equal(0.0, BatteryChemistry.Bond(0, C), 12);
        Assert.Equal(1.0 - Math.Exp(-1.0), BatteryChemistry.Bond(60, C), 12);
        Assert.InRange(BatteryChemistry.Bond(72, C), 0.65, 0.75);   // エース級が1シーズン組んで0.7前後
        Assert.True(BatteryChemistry.Bond(300, C) > 0.99);
    }

    [Fact]
    public void AiApproximation_UsesYearsTogether()
    {
        // 3年の投手×2年の捕手＝同じ学校で2年。
        Assert.Equal(2.0, BatteryChemistry.YearsTogether(P(grade: 3), P(grade: 2)));
        var k = BatteryChemistry.For(P(grade: 3), P(grade: 2), innings: null, C);
        Assert.Equal(C.BondWeight * BatteryChemistry.Bond(2 * C.AiInningsPerYear, C), k, 12);
        // 記録を持つチームは記録のイニングを使う（学年は見ない）。
        Assert.Equal(0.0, BatteryChemistry.For(P(grade: 3), P(grade: 2), innings: 0.0, C), 12);
    }

    [Fact]
    public void SamePersonOrNoCatcher_IsZero()
    {
        var p = P(receptiveness: 90);
        Assert.Equal(0.0, BatteryChemistry.For(p, p, null, C));
        Assert.Equal(0.0, BatteryChemistry.For(p, null, null, C));
    }

    // ===== 効き先（K=0 で恒等） =====

    [Fact]
    public void Effects_AreIdentityAtKZero()
    {
        Assert.Equal(63, BatteryChemistry.ApplyToLead(63, 0.0, C));
        Assert.Equal(2, BatteryChemistry.RecoveryOuts(2, 0.0, C));
        Assert.Equal(1.0, BatteryChemistry.PinchControlMultiplier(0.0, 8, 8, C));
    }

    [Fact]
    public void Effects_FollowK()
    {
        Assert.Equal(69, BatteryChemistry.ApplyToLead(60, 1.0, C));    // 60×1.15＝69
        Assert.Equal(46, BatteryChemistry.ApplyToLead(50, -0.5, C));   // 50×0.925＝46.25→46
        Assert.Equal(1, BatteryChemistry.RecoveryOuts(2, 0.6, C));
        Assert.Equal(2, BatteryChemistry.RecoveryOuts(2, 0.59, C));
        Assert.Equal(1, BatteryChemistry.RecoveryOuts(1, 1.0, C));     // 1未満にしない
        // ピンチの制球: 圧が低い場面は効かない、最大圧で +3%×K
        Assert.Equal(1.0, BatteryChemistry.PinchControlMultiplier(1.0, 2, 8, C));
        Assert.Equal(1.03, BatteryChemistry.PinchControlMultiplier(1.0, 8, 8, C), 12);
        Assert.Equal(1.0 - 0.03 * 0.5 * 0.5, BatteryChemistry.PinchControlMultiplier(-0.5, 4, 8, C), 12);
    }

    // ===== 試合への配線 =====

    private static Team TeamWithIds(string name, int idBase, IReadOnlyDictionary<(int, int), double>? innings = null)
    {
        var slots = new[]
        {
            FieldPosition.Catcher, FieldPosition.FirstBase, FieldPosition.SecondBase, FieldPosition.ThirdBase,
            FieldPosition.Shortstop, FieldPosition.LeftField, FieldPosition.CenterField, FieldPosition.RightField,
        };
        var order = slots.Select((pos, i) => new Player { Name = name + i, Position = pos, SourceId = idBase + i }).ToList();
        order.Add(new Player
        {
            Name = name + "P", Position = FieldPosition.Pitcher, SourceId = idBase + 9,
            Pitching = new PitcherAttributes { MaxVelocityKmh = 135, Control = 50, StaminaPitches = 140, PitchRank = 50 },
        });
        return new Team { Name = name, BattingOrder = order, PitcherSlot = 8, BatteryInnings = innings };
    }

    [Fact]
    public void Game_RecordsBatteryOuts_ForPlayersWithIds()
    {
        var r = GameEngine.Play(TeamWithIds("A", 100), TeamWithIds("H", 200), new GameContext(), new Xoshiro256Random(7));
        var away = Assert.Single(r.AwayBatteryOuts);
        Assert.Equal((109, 100), (away.PitcherId, away.CatcherId));
        Assert.Equal(r.AwayPitching.Sum(l => l.Outs), away.Outs);
        Assert.Equal(209, Assert.Single(r.HomeBatteryOuts).PitcherId);
    }

    [Fact]
    public void Game_IsDeterministic_AndLedgerChangesTheGame()
    {
        GameResult Play(IReadOnlyDictionary<(int, int), double>? ledger)
            => GameEngine.Play(TeamWithIds("A", 100, ledger), TeamWithIds("H", 200), new GameContext(), new Xoshiro256Random(11));
        var a = Play(null);
        var b = Play(null);
        Assert.Equal(a.AwayRuns, b.AwayRuns);
        Assert.Equal(a.HomeRuns, b.HomeRuns);
        Assert.Equal(a.TotalPitches, b.TotalPitches);

        // 記録の無い全員中立のチームは K=0（学年0・性格50）＝記録0イニングを渡しても同じ試合になる。
        var zero = Play(new Dictionary<(int, int), double> { [(109, 100)] = 0.0 });
        Assert.Equal(a.TotalPitches, zero.TotalPitches);
        Assert.Equal(a.HomeRuns, zero.HomeRuns);
    }

    [Fact]
    public void TeamState_ReadsLedgerForOwnSchool()
    {
        var team = TeamWithIds("A", 100, new Dictionary<(int, int), double> { [(109, 100)] = 60.0 });
        var state = new TeamState(team);
        var k = state.BatteryK(team.BattingOrder[8], C);
        Assert.Equal(C.BondWeight * (1.0 - Math.Exp(-1.0)), k, 12);
    }

    // ===== 自校の記録 =====

    [Fact]
    public void Ledger_AddsOfficialAndPracticeWithWeights_AndPrunes()
    {
        var r = new GameResult
        {
            AwayName = "A", HomeName = "H", AwayRuns = 0, HomeRuns = 0, InningsPlayed = 9, TotalPitches = 0, PitcherChanges = 0,
            AwayBatteryOuts = new[] { new BatteryOutsLine(1, 2, 27) },
            HomeBatteryOuts = new[] { new BatteryOutsLine(9, 8, 27) },
        };
        var l = new BatteryLedger();
        l.AddGame(r, managerWasAway: true, isOfficial: true, C);
        l.AddGame(r, managerWasAway: true, isOfficial: false, C);
        Assert.Equal(9.0 + 4.5, l.Innings(1, 2), 12);
        Assert.Equal(0.0, l.Innings(9, 8));
        l.Add(3, 2, 5);
        l.Prune(new[] { 1, 2 });          // 投手3が卒業
        Assert.Equal(1, l.Count);
        Assert.Equal(13.5, l.Snapshot()[(1, 2)], 12);
    }

    [Fact]
    public void Ledger_RoundTripsThroughSave_AndOldSaveStartsEmpty()
    {
        var s = new SaveState
        {
            BatteryInnings = new List<BatteryInningsEntry> { new(1, 2, 13.5), new(3, 2, 2.0 / 3.0) },
        };
        Assert.True(SaveCodec.TryDeserialize(SaveCodec.Serialize(s), out var back, out var err), err);
        Assert.Equal(s.BatteryInnings, back.BatteryInnings);
        var ledger = BatteryLedger.From(back.BatteryInnings);
        Assert.Equal(13.5, ledger.Innings(1, 2), 12);

        // v27 のセーブ（キー無し）は空の記録で読める。
        var json = SaveCodec.Serialize(new SaveState()).Replace($"\"version\":{SaveCodec.CurrentVersion}", "\"version\":27")
            .Replace(",\"batteryInnings\":[]", "");
        Assert.True(SaveCodec.TryDeserialize(json, out var old, out var err2), err2);
        Assert.Empty(old.BatteryInnings);
    }
}
