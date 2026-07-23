using System.Collections.Generic;
using System.Linq;
using KokoSim.Engine.Career;
using KokoSim.Engine.Core;
using KokoSim.Engine.Nation;
using Xunit;

namespace KokoSim.Engine.Tests.Career;

/// <summary>
/// 番狂わせ連動の名声変動（issue #170・設計書04 §1.2）。
/// 格上撃破で↑、格下敗北で↓、順当な結果はほぼ不変、そして上昇は頭打ちすることを担保する。
/// Tier 帯: G(<30) F(30) E(40) D(50) C(60) B(70) A(80) S(90+)。
/// </summary>
public sealed class FameUpsetTests
{
    private static readonly CareerCoefficients C = new();

    // 自校=E帯(45)。相手を各帯に置いて格差を作る。
    private const double SelfE = 45.0;   // E
    private const double OppA = 85.0;    // A（自校より4段格上）
    private const double OppE = 45.0;    // E（同格）
    private const double OppG = 25.0;    // G（自校より2段格下）

    [Fact]
    public void UpsetWin_RaisesFame_ProportionalToGap()
    {
        // 格上(A, +4段)に勝利＝金星。上昇し、格差に比例する。
        var delta = FameUpsetModel.MatchDelta(SelfE, OppA, won: true, C);
        Assert.True(delta > 0, "格上撃破で名声が上がる");
        Assert.Equal(C.FameUpsetWinPerTier * 4, delta, 6);

        // 格差が小さいほど小さい（B=+3段 < A=+4段）。
        var smaller = FameUpsetModel.MatchDelta(SelfE, 75.0 /*B*/, won: true, C);
        Assert.True(smaller < delta, "格差が小さい金星ほど上昇が小さい");
    }

    [Fact]
    public void UpsetLoss_LowersFame_ProportionalToGap()
    {
        // 格下(G, -2段)に敗北＝取りこぼし。低下する。
        var delta = FameUpsetModel.MatchDelta(SelfE, OppG, won: false, C);
        Assert.True(delta < 0, "格下敗北で名声が下がる");
        Assert.Equal(C.FameUpsetLossPerTier * -2, delta, 6);
    }

    [Fact]
    public void ExpectedResults_AreNeutral()
    {
        // 順当勝ち（格下に勝つ・同格に勝つ）はほぼ0。
        Assert.Equal(0.0, FameUpsetModel.MatchDelta(SelfE, OppG, won: true, C), 6);
        Assert.Equal(0.0, FameUpsetModel.MatchDelta(SelfE, OppE, won: true, C), 6);
        // 順当負け（格上に負ける・同格に負ける）もほぼ0。
        Assert.Equal(0.0, FameUpsetModel.MatchDelta(SelfE, OppA, won: false, C), 6);
        Assert.Equal(0.0, FameUpsetModel.MatchDelta(SelfE, OppE, won: false, C), 6);
    }

    [Fact]
    public void SeasonDelta_CapsPositiveGain()
    {
        // 1シーズンに金星を量産しても、上昇は FameUpsetSeasonCap で頭打ち（急上昇しない）。
        var manyUpsetWins = Enumerable.Range(0, 6)
            .Select(_ => new TrackedMatch(OppA, Won: true))
            .ToList();
        var delta = FameUpsetModel.SeasonDelta(manyUpsetWins, SelfE, C);
        Assert.Equal(C.FameUpsetSeasonCap, delta, 6);
        // 素の総和（12*6=72）よりはるかに小さい＝頭打ちが効いている。
        Assert.True(delta < 6 * C.FameUpsetWinPerTier * 4);
    }

    [Fact]
    public void SeasonDelta_Losses_AreNotCapped_AndNet()
    {
        // 取りこぼしは頭打ちしない（緊張感を残す）。金星と混在すれば相殺される。
        var matches = new List<TrackedMatch>
        {
            new(OppA, Won: true),   // +12（金星, cap以下）
            new(OppG, Won: false),  // -7（取りこぼし）
            new(OppG, Won: false),  // -7（取りこぼし）
        };
        var delta = FameUpsetModel.SeasonDelta(matches, SelfE, C);
        var expected = System.Math.Min(C.FameUpsetSeasonCap, C.FameUpsetWinPerTier * 4)
                       + 2 * C.FameUpsetLossPerTier * -2;
        Assert.Equal(expected, delta, 6);
        Assert.True(delta < 0, "取りこぼしが金星を上回れば名声は純減する");
    }

    [Fact]
    public void NoTrackedMatches_MeansNoDelta()
    {
        Assert.Equal(0.0, FameUpsetModel.SeasonDelta(System.Array.Empty<TrackedMatch>(), SelfE, C), 6);
    }

    // ===== TournamentEngine の観測（trackSchoolId）=====

    private static readonly NationCoefficients NationCoeff = new();

    private static List<School> Bracket(int n) => Enumerable.Range(0, n)
        .Select(i => new School { Id = 100 + i, Name = $"S{i}", PrefectureId = 0, Strength = 30 + i * 4.0 })
        .ToList();

    [Fact]
    public void Tracking_DoesNotChangeResult()
    {
        // 観測は乱数を消費しない＝優勝校・勝数は trackSchoolId の有無で1ビット一致（不変条件#2）。
        var schools = Bracket(8);
        var plain = TournamentEngine.Run(schools, NationCoeff, new Xoshiro256Random(9));
        var tracked = TournamentEngine.Run(schools, NationCoeff, new Xoshiro256Random(9), trackSchoolId: 103);
        Assert.Equal(plain.Champion.Id, tracked.Champion.Id);
        Assert.Equal(plain.WinsBySchool.OrderBy(k => k.Key), tracked.WinsBySchool.OrderBy(k => k.Key));
        Assert.Empty(plain.TrackedMatches);
    }

    [Fact]
    public void Tracking_RecordsEachMatchOfTrackedSchool()
    {
        var schools = Bracket(8);
        var tracked = TournamentEngine.Run(schools, NationCoeff, new Xoshiro256Random(9), trackSchoolId: 103);
        // 追跡校の試合数＝その校の勝利数＋（最後に負けたなら1）。全て勝てば優勝＝敗戦なし。
        var wins = tracked.WinsBySchool[103];
        var lost = tracked.Champion.Id != 103;
        Assert.Equal(wins + (lost ? 1 : 0), tracked.TrackedMatches.Count);
        // 勝った試合数と TrackedMatches の勝敗内訳が整合。
        Assert.Equal(wins, tracked.TrackedMatches.Count(m => m.Won));
    }
}
