using System;
using System.Collections.Generic;
using System.Linq;
using KokoSim.Engine.Core;
using KokoSim.Engine.Nation;
using KokoSim.Engine.Season;
using Xunit;
using Xunit.Abstractions;

namespace KokoSim.Engine.Tests.Season;

/// <summary>
/// 自校の新入生の物差し（issue #1080・2026-11-19 オーナー決定「自校も投手は据え置き」）。
/// AI校と同じく投手寄りの新入生は今の中心のまま、野手だけを <see cref="RosterCoefficients.BatterScaleDrop"/>
/// 下げ、新入生で組んだチームの総合がちょうど <see cref="RosterCoefficients.StrengthTargetDrop"/> だけ下がる
/// （＝AI校のチーム総合の下げ幅と揃い、自校とAI校の相対的な強さが変わらない）。
/// </summary>
public sealed class OwnIntakeScaleDropTests
{
    private readonly ITestOutputHelper _out;
    public OwnIntakeScaleDropTests(ITestOutputHelper output) => _out = output;

    private static readonly RosterCoefficients C = new();
    private static readonly TeamStrengthCoefficients T = new();

    /// <summary>新入生3学年ぶんで組んだチームの総合の平均（同じ乱数列で比べる）。</summary>
    private static (double Overall, double PitcherVelocity) Measure(RosterCoefficients c, double center, int teams = 200)
    {
        var sum = 0.0;
        var vel = new List<double>();
        for (var t = 0; t < teams; t++)
        {
            var rng = new Xoshiro256Random((ulong)(1000 + t));
            var roster = new List<DevelopingPlayer>();
            for (var y = 0; y < 3; y++)
                roster.AddRange(ProspectGenerator.Intake(y + 1, c, rng, talentCenter: center, fixedCount: 10));
            sum += TeamStrengthProfile.Compute(roster, T).Overall;
            vel.AddRange(roster.Where(p => PitcherRoleResolver.LeansPitcher(p, c)).Select(p => (double)p.Level(AbilityKind.Velocity)));
        }
        return (sum / teams, vel.Count == 0 ? 0 : vel.Average());
    }

    /// <summary>
    /// 中心40以上はAI校（強さ35〜85で −6.9〜−7.1・実測 2026-11-19）と同じだけ下がる。中心32以下は野手が能力の
    /// 下限10に張り付き、下げ幅が浅くなる（実測: 中心32で −4.9・25で −3.1）。AI校も総合18付近が下限で同じく
    /// 浅くなる。弱い側を −7 へ合わせるには中心32で野手を24下げる必要があり、序盤の野手がほぼ全員下限10になる
    /// ＝個性の狙いと逆行するため、一律12で受け入れた（2026-11-19 オーナー判断）。入学直後だけの比較で、
    /// 下限の差は練習の伸び（低い能力ほど伸びやすい）で縮む見込み。
    /// </summary>
    [Theory]
    [InlineData(25.0, 2.5)]
    [InlineData(32.0, 4.0)]
    [InlineData(40.0, 6.0)]
    [InlineData(55.0, 6.0)]
    [InlineData(70.0, 6.0)]
    public void TeamOverall_DropsByTheSameAmountAsAiSchools_AndPitchersStayOnTheOldScale(double center, double minDrop)
    {
        var before = Measure(C with { BatterScaleDrop = 0.0 }, center);
        var after = Measure(C, center);
        var drop = before.Overall - after.Overall;
        _out.WriteLine($"center={center}: 総合 {before.Overall:F2}→{after.Overall:F2}（−{drop:F2}）／投手の球速Lv {before.PitcherVelocity:F2}→{after.PitcherVelocity:F2}");

        Assert.InRange(drop, minDrop, C.StrengthTargetDrop + 1.0);
        // 投手は据え置き（差は投手寄り判定の境目に居る二刀流ぶんの誤差だけ・実測 0.05 以下）
        Assert.InRange(after.PitcherVelocity - before.PitcherVelocity, -0.2, 0.2);
    }
}
