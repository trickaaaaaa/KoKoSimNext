using KokoSim.Engine.Match.Field;
using KokoSim.Engine.Match.Fielding;
using KokoSim.Engine.Season;
using Xunit;

namespace KokoSim.Engine.Tests.Season;

/// <summary>
/// 実効守備力ヘルパ（issue #371）: Fielding（地力）×AptitudeFactor（適性補正）の単一ソース。
/// 判断画面（メンバー設定・スタメン設定）の実効ランク表示と RosterTeamBuilder.ToPlayer が
/// 同じ値を出すことを保証する。
/// </summary>
public sealed class EffectiveFieldingTests
{
    private static readonly FieldingCoefficients Coeff = new();

    private static DevelopingPlayer PlayerWith(int fielding, int aptitude)
    {
        var p = new DevelopingPlayer { GrowthType = GrowthType.Standard };
        p.SetLevel(AbilityKind.Fielding, fielding);
        p.SetAptitude(FieldPosition.ThirdBase, aptitude);
        return p;
    }

    [Fact]
    public void Rank_AtNeutralAptitude50_EqualsFieldingItself()
    {
        var p = PlayerWith(fielding: 60, aptitude: 50);
        Assert.Equal(60, EffectiveFielding.Rank(p, FieldPosition.ThirdBase, Coeff));
    }

    [Fact]
    public void Rank_AtAptitude99_MatchesFullPrecisionFactor()
    {
        // 適性99: factor=1+(99-50)*0.006=1.294（0-99の実用域では1.30クランプに届かない）。
        // issue本文の例表は倍率を×1.29に丸めて示した概算（60×1.29=77.4→77）だが、
        // 実装は丸めない倍率で計算する（60×1.294=77.64→78）。
        var p = PlayerWith(fielding: 60, aptitude: 99);
        Assert.Equal(1.294, Coeff.AptitudeFactor(99), 3);
        Assert.Equal(78, EffectiveFielding.Rank(p, FieldPosition.ThirdBase, Coeff));
    }

    [Fact]
    public void Rank_AtAptitude70_MatchesIssueTableExample_67()
    {
        // issue本文の例表: 守備力60・適性70 → 実効67。
        var p = PlayerWith(fielding: 60, aptitude: 70);
        Assert.Equal(67, EffectiveFielding.Rank(p, FieldPosition.ThirdBase, Coeff));
    }

    [Fact]
    public void Rank_AtAptitude30_MatchesIssueTableExample_53()
    {
        // issue本文の例表: 守備力60・適性30 → 実効53。
        var p = PlayerWith(fielding: 60, aptitude: 30);
        Assert.Equal(53, EffectiveFielding.Rank(p, FieldPosition.ThirdBase, Coeff));
    }

    [Fact]
    public void AptitudeFactor_ClampsAtMin0_60AndMax1_30_OutsideNormalRange()
    {
        // 通常の適性域(0〜99)では理論クランプ(0.60/1.30)に届かない（issue本文の指摘どおり）。
        // クランプそのものは AptitudeFactor の式レベルで検証する（極端値で直接踏む）。
        Assert.Equal(1.30, Coeff.AptitudeFactor(200));
        Assert.Equal(0.60, Coeff.AptitudeFactor(-200));
    }

    [Fact]
    public void Rank_NeverExceeds100_EvenAtMaxFieldingAndExtremeAptitude()
    {
        var p = PlayerWith(fielding: 100, aptitude: 200);
        Assert.Equal(100, EffectiveFielding.Rank(p, FieldPosition.ThirdBase, Coeff));
    }

    [Fact]
    public void Rank_NeverBelow1_EvenAtMinFieldingAndExtremeAptitude()
    {
        var p = PlayerWith(fielding: 1, aptitude: -200);
        Assert.Equal(1, EffectiveFielding.Rank(p, FieldPosition.ThirdBase, Coeff));
    }

    [Fact]
    public void ToPlayer_HealthyPlayer_FieldingMatchesEffectiveFieldingRank()
    {
        // 怪我なし（injF=1.0）なら ToPlayer が投影する Fielding は EffectiveFielding.Rank と完全一致する
        // （設計書上「実効守備力」＝怪我等の一時係数を含まない値）。
        var p = PlayerWith(fielding: 73, aptitude: 82);
        var expected = EffectiveFielding.Rank(p, FieldPosition.ThirdBase, Coeff);

        var player = RosterTeamBuilder.ToPlayer(p, FieldPosition.ThirdBase, asPitcher: false, fieldingCoeff: Coeff);

        Assert.Equal(expected, player.Fielding);
    }
}
