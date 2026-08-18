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
/// （恒等・単調・ピボット不動・上位帯の差が縮む）を固定する。得点環境が目標に入るかは
/// <c>simulate-tiers</c> の実測で見る（帯としての固定は Step 4）。</para>
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
    /// 既定は膝なし（knee=100）の一様圧縮（issue prefecture-dominance・2026-08-18）。旧既定の膝60は
    /// 「上を100で元尺度へ戻す」勾配 1.225 で上位帯の差を raw より広げ（80→75.5 / 65→57.1＝差18.4）、
    /// 県1位 vs 2位が単試合 99% になっていた。一様なら差15 → 8.25（86%）。膝の式自体は残す（比較・実験用）。
    /// </summary>
    [Fact]
    public void Default_IsUniform_TopGapNarrowed()
    {
        var c = new RosterCoefficients();
        Assert.Equal(100.0, c.TierCompressionKnee, 6);
        Assert.Equal(40.0 + 0.55 * 40.0, c.CompressStrength(80.0), 6);          // 62.0
        Assert.Equal(40.0 + 0.55 * 25.0, c.CompressStrength(65.0), 6);          // 53.75
        var gap = c.CompressStrength(80.0) - c.CompressStrength(65.0);
        Assert.True(gap < 15.0 * 0.6, $"上位帯の差が縮んでいない: 80vs65 → {gap:F2}");

        // 膝60（旧既定）は上位帯の差を raw より広げる＝一強の主因。式は生きていることも固定する。
        var kneed = c with { TierCompressionKnee = 60.0 };
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

    /// <summary>低ティア帯の実力差が実際に縮む（勾配を緩めるという目的の直接確認）。</summary>
    [Fact]
    public void LowTierGap_IsNarrowed()
    {
        var c = new RosterCoefficients();
        var before = 50.0 - 35.0;                                     // 自校(≒35) と D(50) の実力差
        var after = c.CompressStrength(50.0) - 35.0;
        Assert.True(after < before * 0.75, $"低ティア差が縮んでいない: {before:F1} → {after:F1}");
    }

    /// <summary>
    /// 生成の入口が圧縮を通っている（写像を足しただけで呼ばれていない、を防ぐ）。
    /// <see cref="StrengthTeamFactory.Create"/> は Unity 実プレイの大会展望・練習試合の経路。
    /// </summary>
    [Fact]
    public void StrengthTeamFactory_AppliesCompression()
    {
        var compressed = StrengthTeamFactory.Create(30.0, "圧縮あり", new Xoshiro256Random(7));
        var raw = StrengthTeamFactory.Create(30.0, "圧縮なし", new Xoshiro256Random(7),
            rosterCoeff: new RosterCoefficients { TierCompressionSlope = 1.0, TierCompressionKnee = 100.0 });

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
