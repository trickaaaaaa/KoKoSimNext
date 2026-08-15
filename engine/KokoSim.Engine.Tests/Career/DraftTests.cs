using System.Linq;
using KokoSim.Engine.Career.Draft;
using KokoSim.Engine.Core;
using KokoSim.Engine.Match.Field;
using KokoSim.Engine.Match.Game;
using KokoSim.Engine.Nation;
using KokoSim.Engine.Season;
using KokoSim.Engine.Stats;
using Xunit;

namespace KokoSim.Engine.Tests.Career;

/// <summary>
/// ドラフト（設計書20）の注目度算出・候補バンド判定・10月最終週の指名処理を、
/// 手計算値と決定論で検証する。表示専用・独立Fork乱数＝帯不変を前提とする。
/// </summary>
public sealed class DraftTests
{
    // 注目度の打者軸（NotabilityModel.BatterWeights）＋選手画面の打撃力チップ（弾道/選球眼）。
    // 後者を埋めないと打撃力が C（0.7×level）に留まり、足切り（§3.1a）で全員が圏外になる。
    private static readonly AbilityKind[] BatterKinds =
    {
        AbilityKind.Contact, AbilityKind.Power, AbilityKind.Speed,
        AbilityKind.Fielding, AbilityKind.ArmStrength, AbilityKind.Catching,
        AbilityKind.LaunchTendency, AbilityKind.Discipline,
    };
    private static readonly AbilityKind[] PitcherKinds =
    {
        AbilityKind.Velocity, AbilityKind.Control, AbilityKind.Stamina, AbilityKind.PitchRank,
    };

    private static readonly TeamStrengthCoefficients S = new();

    private static DevelopingPlayer Batter(int id, int grade, int level, int cap, string name = "打者")
    {
        var p = new DevelopingPlayer { Id = id, Name = name, Grade = grade };
        PitcherRoleResolver.SeedAptitudeForRole(p, false);
        foreach (var k in BatterKinds) { p.SetLevel(k, level); p.SetCap(k, cap); }
        return p;
    }

    private static DevelopingPlayer Pitcher(int id, int grade, int level, int cap, string name = "投手")
    {
        var p = new DevelopingPlayer { Id = id, Name = name, Grade = grade };
        PitcherRoleResolver.SeedAptitudeForRole(p, true);
        foreach (var k in PitcherKinds) { p.SetLevel(k, level); p.SetCap(k, cap); }
        return p;
    }

    private static PlayerStats BattingStats(int id, int games, int pa, int ab, int hits, int hr)
    {
        var s = new PlayerStats(id);
        s.Batting.Add(new BattingLine(1, FieldPosition.CenterField, "X",
            PlateAppearances: pa, AtBats: ab, Hits: hits, Doubles: 0, Triples: 0, HomeRuns: hr,
            Rbi: 0, Walks: 0, StrikeOuts: 0, SourceId: id));
        // Games は PA>0 の1試合として1加算されるので、複数試合ぶんを別ラインで積む。
        for (var g = 1; g < games; g++)
            s.Batting.Add(new BattingLine(1, FieldPosition.CenterField, "X",
                1, 0, 0, 0, 0, 0, 0, 1, 0, SourceId: id));   // 四球のみ＝出場1試合、打数に影響しない
        return s;
    }

    private static PlayerStats PitchingStats(int id, int outs, int bf, int hits, int runs, int k)
    {
        var s = new PlayerStats(id);
        s.Pitching.Add(new PitchingLine("X", Outs: outs, BattersFaced: bf, Hits: hits, Runs: runs,
            StrikeOuts: k, Walks: 0, Pitches: bf * 4, SourceId: id), started: true, win: false, loss: false);
        return s;
    }

    // ── 注目度：能力合成（現在値×隠し上限ブレンド） ──

    [Fact]
    public void AbilityScore_BlendsCurrentAndCap_ByCeilingBlend()
    {
        var c = new DraftCoefficients(); // CeilingBlend=0.35
        var p = Batter(1, 3, level: 80, cap: 90);

        // 加重合計1.0なので cur=80, cap=90 → 0.65*80 + 0.35*90 = 83.5
        Assert.Equal(83.5, NotabilityModel.AbilityScore(p, c), 3);
    }

    [Fact]
    public void PerformanceScore_NoStats_IsNeutralFifty()
    {
        var c = new DraftCoefficients();
        var p = Batter(1, 3, 70, 80);
        Assert.Equal(50.0, NotabilityModel.PerformanceScore(p, null, c), 6);
    }

    [Fact]
    public void PerformanceScore_LowSampleShrinksTowardNeutral()
    {
        var c = new DraftCoefficients(); // BatterMinPlateAppearances=40
        var p = Batter(1, 3, 70, 80);
        // OPS高いが打席5のみ → shrink=5/40=0.125 で中立50へ強く収縮
        var fewPa = NotabilityModel.PerformanceScore(p, BattingStats(1, games: 2, pa: 5, ab: 5, hits: 4, hr: 2), c);
        var manyPa = NotabilityModel.PerformanceScore(p, BattingStats(1, games: 12, pa: 45, ab: 45, hits: 30, hr: 8), c);
        Assert.True(fewPa < manyPa);
        Assert.True(System.Math.Abs(fewPa - 50.0) < System.Math.Abs(manyPa - 50.0));
    }

    // ── 注目度→バンド ──

    [Theory]
    [InlineData(90.0, DraftRankBand.FirstRound)]
    [InlineData(80.0, DraftRankBand.UpperRound)]
    [InlineData(70.0, DraftRankBand.MiddleRound)]
    [InlineData(60.0, DraftRankBand.LowerRound)]
    [InlineData(50.0, DraftRankBand.None)]
    public void BandOf_MapsThresholds(double notability, DraftRankBand expected)
    {
        Assert.Equal(expected, NotabilityModel.BandOf(notability, new DraftCoefficients()));
    }

    [Fact]
    public void WeakPlayer_IsNotCandidate()
    {
        var c = new DraftCoefficients();
        var p = Batter(1, 2, level: 40, cap: 50);       // abilityScore=43.5, perf=50 → 46.75 < 58
        var e = DraftEngine.Evaluate(p, null, c, S);
        Assert.Equal(DraftRankBand.None, e.Band);
        Assert.False(e.Band.IsCandidate());
    }

    [Fact]
    public void Prospect_AnyGrade_CanBecomeCandidate()
    {
        var c = new DraftCoefficients();
        // 1年生でも能力＋成績が高ければ候補入りしうる（設計書20 §1）。
        var freshman = Batter(7, grade: 1, level: 92, cap: 98, name: "1年怪物");
        var e = DraftEngine.Evaluate(freshman, BattingStats(7, 12, 48, 45, 30, 10), c, S);
        Assert.True(e.Band.IsCandidate());
        Assert.Equal(1, e.Grade);
    }

    [Fact]
    public void PitcherRole_UsesPitcherWeightsAndStats()
    {
        var c = new DraftCoefficients();
        var ace = Pitcher(3, 3, level: 85, cap: 92, name: "エース");
        var e = DraftEngine.Evaluate(ace, PitchingStats(3, outs: 90, bf: 90, hits: 40, runs: 5, k: 100), c, S);
        Assert.True(e.EvaluatedAsPitcher);
        Assert.True(e.Band.IsCandidate());
    }

    // ── EvaluateRoster スナップショット ──

    [Fact]
    public void EvaluateRoster_ReturnsAllPlayers_InRosterOrder()
    {
        var c = new DraftCoefficients();
        var roster = new[]
        {
            Batter(1, 3, 90, 98, "強打者"),
            Batter(2, 2, 45, 50, "控え"),
        };
        var stats = new System.Collections.Generic.Dictionary<int, PlayerStats>
        {
            [1] = BattingStats(1, 12, 48, 45, 30, 9),
        };
        var evals = DraftEngine.EvaluateRoster(roster, id => stats.TryGetValue(id, out var st) ? st : null, c, S);

        Assert.Equal(2, evals.Count);
        Assert.Equal(1, evals[0].PlayerId);
        Assert.Equal(2, evals[1].PlayerId);
        Assert.True(evals[0].Notability > evals[1].Notability);
        Assert.True(evals[0].Band.IsCandidate());
        Assert.False(evals[1].Band.IsCandidate());
    }

    // ── 10月最終週の指名（3年のみ・決定論） ──

    [Fact]
    public void RunNomination_OnlyThirdYearCandidates()
    {
        var c = new DraftCoefficients();
        var roster = new[]
        {
            Batter(1, 3, 92, 98, "3年候補"),   // 候補・対象
            Batter(2, 2, 92, 98, "2年候補"),   // 候補だが2年 → 対象外
            Batter(3, 3, 45, 50, "3年控え"),   // 3年だが非候補 → 対象外
        };
        var result = DraftEngine.RunNomination(1, roster,
            _ => null, c, S, new Xoshiro256Random(42));

        Assert.All(result.Picks, p => Assert.Equal(1, p.PlayerId));   // 1人だけ
    }

    [Fact]
    public void RunNomination_IsDeterministic_ForSameSeed()
    {
        var c = new DraftCoefficients();
        var roster = new[] { Batter(1, 3, 80, 88), Batter(4, 3, 78, 84) };

        var a = DraftEngine.RunNomination(3, roster, _ => null, c, S, new Xoshiro256Random(7));
        var b = DraftEngine.RunNomination(3, roster, _ => null, c, S, new Xoshiro256Random(7));

        Assert.Equal(a.Picks.Count, b.Picks.Count);
        for (var i = 0; i < a.Picks.Count; i++)
        {
            Assert.Equal(a.Picks[i].PlayerId, b.Picks[i].PlayerId);
            Assert.Equal(a.Picks[i].Nominated, b.Picks[i].Nominated);
            Assert.Equal(a.Picks[i].Round, b.Picks[i].Round);
        }
    }

    [Fact]
    public void RunNomination_HighProbability_Nominates_WithRoundFromBand()
    {
        // 指名確率を実質1にして確定させる（rng に依らず nominated=true を保証）。
        var c = new DraftCoefficients { NominationMidpoint = 0.0, NominationSpread = 1.0 };
        var monster = Batter(1, 3, 95, 99, "怪物");   // FirstRound
        var result = DraftEngine.RunNomination(2, new[] { monster },
            _ => BattingStats(1, 12, 48, 45, 32, 12), c, S, new Xoshiro256Random(1));

        var pick = Assert.Single(result.Picks);
        Assert.True(pick.Nominated);
        Assert.Equal(DraftRankBand.FirstRound, pick.Band);
        Assert.Equal(1, pick.Round);                 // FirstRound → 1位
        Assert.Single(result.Nominated);
    }

    [Fact]
    public void RunNomination_LowProbability_LeavesCandidateUndrafted()
    {
        // 指名確率を実質0にして指名漏れを確定させる（候補だが指名されない＝物語として残る）。
        var c = new DraftCoefficients { NominationMidpoint = 1000.0, NominationSpread = 1.0 };
        var candidate = Batter(1, 3, 92, 98);        // 候補（FirstRound）だが…
        var result = DraftEngine.RunNomination(2, new[] { candidate },
            _ => null, c, S, new Xoshiro256Random(1));

        var pick = Assert.Single(result.Picks);      // 候補なのでピック行は立つ
        Assert.False(pick.Nominated);                // 指名はされない
        Assert.Equal(0, pick.Round);
        Assert.Empty(result.Nominated);
    }

    // ── 候補入りの足切り（§3.1a: 打撃力／投手力チップの高い方が B 未満なら圏外） ──

    /// <summary>
    /// 実害の再現: 打撃力チップが D（現在値55）でも、隠し上限が高く（cap 90）成績も良ければ
    /// 注目度は中位圏に届く。床が無いと「打者ランクDが4位指名」になる。床があれば圏外。
    /// </summary>
    [Fact]
    public void RankDBatter_WithHighCeilingAndStats_IsBlockedByFloor()
    {
        var c = new DraftCoefficients();
        var p = Batter(1, 3, level: 55, cap: 90, name: "D打者");
        var stats = BattingStats(1, games: 14, pa: 56, ab: 50, hits: 25, hr: 8);   // OPS 1.0超

        var n = NotabilityModel.Compute(p, stats, c);
        Assert.Equal(Tier.D, PlayerStrengthProfile.Compute(p, S).BattingTier);
        Assert.True(NotabilityModel.BandOf(n, c) >= DraftRankBand.MiddleRound,
            $"床が無ければ中位圏に届くはず（notability={n:F1}）");

        var e = DraftEngine.Evaluate(p, stats, c, S);
        Assert.Equal(DraftRankBand.None, e.Band);
        Assert.Equal(n, e.Notability, 6);            // 注目度自体は据え置き（表示用）

        var result = DraftEngine.RunNomination(1, new[] { p }, _ => stats,
            new DraftCoefficients { NominationMidpoint = 0.0 }, S, new Xoshiro256Random(1));
        Assert.Empty(result.Picks);                  // 指名対象にすら乗らない
    }

    [Fact]
    public void RankBBatter_PassesFloor()
    {
        var c = new DraftCoefficients();
        var p = Batter(1, 3, level: 70, cap: 85);    // 打撃力ちょうど B
        Assert.Equal(Tier.B, PlayerStrengthProfile.Compute(p, S).BattingTier);
        Assert.True(NotabilityModel.MeetsCandidateFloor(p, S, c));
    }

    [Fact]
    public void Floor_TakesHigherOfBattingAndPitching()
    {
        var c = new DraftCoefficients();
        var ace = Pitcher(1, 3, level: 80, cap: 90);   // 投手力 A・打撃力 G
        Assert.True(NotabilityModel.MeetsCandidateFloor(ace, S, c));

        var weak = Pitcher(2, 3, level: 60, cap: 90);  // 投手力 C・打撃力 G
        Assert.False(NotabilityModel.MeetsCandidateFloor(weak, S, c));
    }

    [Fact]
    public void Floor_IsCoefficientDriven()
    {
        var p = Batter(1, 3, level: 55, cap: 90);      // 打撃力 D
        Assert.False(NotabilityModel.MeetsCandidateFloor(p, S, new DraftCoefficients()));
        Assert.True(NotabilityModel.MeetsCandidateFloor(p, S, new DraftCoefficients { CandidateMinTier = Tier.D }));
        Assert.True(NotabilityModel.MeetsCandidateFloor(p, S, new DraftCoefficients { CandidateMinTier = Tier.G }));
    }
}
