using KokoSim.Engine.Core;
using KokoSim.Engine.Match.Game;
using KokoSim.Engine.Nation;
using KokoSim.Engine.Season;
using Xunit;

namespace KokoSim.Engine.Tests.Season;

/// <summary>
/// ティア→能力の圧縮（issue #578 Step 1・OPEN-QUESTIONS Q24 の確定2）。
///
/// <para>#536 の実測で分かったのは「Fランクが弱い」ではなく<b>勾配が急すぎる</b>こと
/// （強度10ポイント＝表示1ティアごとに得点が約0.4倍）だった。ここでは写像そのものの性質
/// （恒等・単調・ピボット不動・上位帯の差が縮む）を固定する。2026-08-19（設計書21）から既定は恒等＝
/// 寝かせる役は表示→物理の効き <c>ability_effect</c> に移り、圧縮は比較・実験用の式として残る。</para>
/// </summary>
public sealed class TierCompressionTests
{
    /// <summary>slope=1 は恒等写像＝圧縮を入れる前と1ビット一致する（非破壊の担保）。</summary>
    [Theory]
    [InlineData(20.0)]
    [InlineData(40.0)]
    [InlineData(60.0)]
    [InlineData(85.0)]
    [InlineData(100.0)]
    public void Slope1_IsIdentity(double strength)
    {
        var c = new RosterCoefficients { TierCompressionSlope = 1.0, TierCompressionKnee = 100.0 };
        Assert.Equal(strength, c.CompressStrength(strength), 9);
    }

    /// <summary>ピボットの学校は圧縮の前後で動かない（写像の定義そのもの）。</summary>
    [Fact]
    public void Pivot_IsFixedPoint()
    {
        var c = new RosterCoefficients();
        Assert.Equal(c.TierCompressionPivot, c.CompressStrength(c.TierCompressionPivot), 9);
    }

    /// <summary>
    /// 既定は<b>恒等</b>（slope 1.0・knee 100。設計書21・2026-08-19）。AI校だけを生成で寝かせる圧縮は表示ランクと中身を
    /// 乖離させたので廃止し、勾配を寝かせる役は表示→物理の効き（<c>ability_effect</c>）が全員対称に担う。
    /// 式は比較・実験用に残す（slope 0.55＝一様圧縮／knee 60＝旧既定の膝）ので、その性質も固定しておく。
    /// </summary>
    [Fact]
    public void Default_IsIdentity_AndTheFormulaStillWorksForExperiments()
    {
        var c = new RosterCoefficients();
        Assert.Equal(1.0, c.TierCompressionSlope, 9);
        Assert.Equal(100.0, c.TierCompressionKnee, 6);
        foreach (var s in new[] { 10.0, 40.0, 65.0, 80.0, 99.0 })
            Assert.Equal(s, c.CompressStrength(s), 9);

        // 一様 0.55（2026-08-18 の旧既定）: 80→62 / 65→53.75、上位帯の差 15 → 8.25。
        var uniform = c with { TierCompressionSlope = 0.55 };
        Assert.Equal(40.0 + 0.55 * 40.0, uniform.CompressStrength(80.0), 6);
        Assert.Equal(40.0 + 0.55 * 25.0, uniform.CompressStrength(65.0), 6);
        Assert.True(uniform.CompressStrength(80.0) - uniform.CompressStrength(65.0) < 15.0 * 0.6);

        // 膝60（#578 の旧既定）は「上を100で元尺度へ戻す」勾配 1.225 で上位帯の差を raw より広げる＝一強の主因だった。
        var kneed = uniform with { TierCompressionKnee = 60.0 };
        Assert.Equal(100.0, kneed.CompressStrength(100.0), 6);
        Assert.True(kneed.CompressStrength(80.0) - kneed.CompressStrength(65.0) > 15.0,
            $"膝60は上位帯を raw より広げるはず: {kneed.CompressStrength(80.0) - kneed.CompressStrength(65.0):F2}");
    }

    /// <summary>強い学校ほど能力中心も高い（順序が入れ替わらない＝ティアの意味が壊れない）。</summary>
    [Fact]
    public void IsStrictlyIncreasing()
    {
        var c = new RosterCoefficients();
        var prev = double.NegativeInfinity;
        for (var s = 10.0; s <= 100.0; s += 1.0)
        {
            var v = c.CompressStrength(s);
            Assert.True(v > prev, $"強度 {s} で単調性が壊れた（{prev:F3} → {v:F3}）");
            prev = v;
        }
    }

    /// <summary>圧縮を入れたとき低ティア帯の実力差が実際に縮む（式の向きの直接確認。既定は恒等なので明示 0.55）。</summary>
    [Fact]
    public void LowTierGap_IsNarrowed()
    {
        var c = new RosterCoefficients { TierCompressionSlope = 0.55 };
        var before = 50.0 - 35.0;                                     // 自校(≒35) と D(50) の実力差
        var after = c.CompressStrength(50.0) - 35.0;
        Assert.True(after < before * 0.75, $"低ティア差が縮んでいない: {before:F1} → {after:F1}");
    }

    /// <summary>
    /// 生成の入口が圧縮を通っている（写像を足しただけで呼ばれていない、を防ぐ）。
    /// <see cref="StrengthTeamFactory.Create"/> は Unity 実プレイの大会展望・練習試合の経路。既定は恒等なので圧縮側を明示する。
    /// </summary>
    [Fact]
    public void StrengthTeamFactory_AppliesCompression()
    {
        var compressed = StrengthTeamFactory.Create(30.0, "圧縮あり", new Xoshiro256Random(7),
            rosterCoeff: new RosterCoefficients { TierCompressionSlope = 0.55, TierCompressionKnee = 100.0 });
        var raw = StrengthTeamFactory.Create(30.0, "圧縮なし", new Xoshiro256Random(7));

        var a = Average(compressed);
        var b = Average(raw);
        Assert.True(a > b, $"弱小校（強度30）が圧縮で持ち上がっていない: 圧縮あり {a:F1} / なし {b:F1}");
    }

    private static double Average(Team team)
    {
        var sum = 0.0;
        var n = 0;
        foreach (var p in team.BattingOrder)
        {
            sum += (p.Contact + p.Power + p.Discipline + p.Speed + p.ArmStrength + p.Fielding) / 6.0;
            n++;
        }
        return sum / n;
    }
}
