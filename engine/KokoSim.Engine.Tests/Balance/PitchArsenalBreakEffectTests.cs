using System.Threading.Tasks;
using KokoSim.Engine.Core;
using KokoSim.Engine.Match.AtBat;
using KokoSim.Engine.Players;
using Xunit;
using Xunit.Abstractions;

namespace KokoSim.Engine.Tests.Balance;

/// <summary>
/// issue #835 の完了条件「同じ最速・同じ能力で、変化球ありのほうが失点が少ない」「球種ごとに空振り率・
/// ゴロ率の差が出る」を固定する。回転軸（issue #835）が無かった旧実装では、球種を増やしても被OPSが
/// 1ビットも動かなかった（全球種バックスピン固定で変化球が「遅いだけのストレート」になっていたため）。
/// 母数は大きめ（PA級の統計テスト。大量反復のため Category=Heavy）。
/// </summary>
[Trait("Category", "Heavy")]
public sealed class PitchArsenalBreakEffectTests
{
    private readonly ITestOutputHelper _out;
    public PitchArsenalBreakEffectTests(ITestOutputHelper output) => _out = output;

    private sealed class Acc
    {
        public long Pa, Ab, H, Tb, Bb, So, Hr;
        public long FastballBip, FastballGroundBall, BreakingBip, BreakingGroundBall;
        public double Obp => Pa > 0 ? (double)(H + Bb) / Pa : 0;
        public double Slg => Ab > 0 ? (double)Tb / Ab : 0;
        public double Ops => Obp + Slg;
        public double HrRate => Pa > 0 ? (double)Hr / Pa : 0;
        public double FastballGroundBallRate => FastballBip > 0 ? (double)FastballGroundBall / FastballBip : 0;
        public double BreakingGroundBallRate => BreakingBip > 0 ? (double)BreakingGroundBall / BreakingBip : 0;
    }

    /// <summary>ストレートのみのレパートリー（issue本文の計測条件と同じ: 能力はすべて50）。</summary>
    private static PitcherAttributes StraightOnly(double maxVelocityKmh) => new()
    {
        MaxVelocityKmh = maxVelocityKmh,
        Control = 50,
        PitchRank = 50,
        Repertoire = new[] { PitchSlot.FastballOf(50) },
    };

    /// <summary>ストレート＋スライダー＋フォーク（issue本文の計測条件と同じ）。</summary>
    private static PitcherAttributes WithBreakingBalls(double maxVelocityKmh) => new()
    {
        MaxVelocityKmh = maxVelocityKmh,
        Control = 50,
        PitchRank = 50,
        Repertoire = new[]
        {
            PitchSlot.FastballOf(50),
            new PitchSlot { Type = PitchType.Slider, Power = 50, Sharpness = 50 },
            new PitchSlot { Type = PitchType.Fork, Power = 50, Sharpness = 50 },
        },
    };

    private static Acc Simulate(PitcherAttributes pitcher, int pa, ulong seedBase)
    {
        var batter = BatterAttributes.LeagueAverage;
        var ctx = new AtBatContext();
        var root = new Xoshiro256Random(seedBase);
        var forks = new IRandomSource[pa];
        for (var i = 0; i < pa; i++) forks[i] = root.Fork((ulong)i);

        var total = new Acc();
        Parallel.For(0, pa,
            () => new Acc(),
            (i, _, local) =>
            {
                var rng = forks[i];
                var result = AtBatResolver.ResolveDetailed(batter, pitcher, ctx, rng);
                var r = result.Result;
                local.Pa++;
                if (r.IsAtBat()) local.Ab++;
                if (r.IsHit()) local.H++;
                local.Tb += r.TotalBases();
                if (r is PlateAppearanceResult.Walk or PlateAppearanceResult.HitByPitch) local.Bb++;
                if (r == PlateAppearanceResult.Strikeout) local.So++;
                if (r == PlateAppearanceResult.HomeRun) local.Hr++;

                // 球種別のゴロ率（issue #835 完了条件②: フォークの縦変化でゴロが増える）。
                if (result.PitchLog is { } log)
                {
                    foreach (var p in log)
                    {
                        if (p.Kind != PitchKind.InPlay || p.BattedBall is not { } ball) continue;
                        var isGroundBall = ball.LaunchAngleDeg < 10.0;
                        if (p.PitchType == PitchType.Fastball)
                        {
                            local.FastballBip++;
                            if (isGroundBall) local.FastballGroundBall++;
                        }
                        else if (p.PitchType == PitchType.Fork)
                        {
                            local.BreakingBip++;
                            if (isGroundBall) local.BreakingGroundBall++;
                        }
                    }
                }
                return local;
            },
            local =>
            {
                lock (total)
                {
                    total.Pa += local.Pa; total.Ab += local.Ab; total.H += local.H; total.Tb += local.Tb;
                    total.Bb += local.Bb; total.So += local.So; total.Hr += local.Hr;
                    total.FastballBip += local.FastballBip; total.FastballGroundBall += local.FastballGroundBall;
                    total.BreakingBip += local.BreakingBip; total.BreakingGroundBall += local.BreakingGroundBall;
                }
            });
        return total;
    }

    [Fact]
    public void PitcherWithBreakingBalls_AllowsLowerOps_ThanStraightOnly_AtSameTopSpeed()
    {
        const int Pa = 30_000;
        const double TopSpeedKmh = 135.0;

        var straight = Simulate(StraightOnly(TopSpeedKmh), Pa, 0x835_0001UL);
        var withBreaking = Simulate(WithBreakingBalls(TopSpeedKmh), Pa, 0x835_0002UL);

        _out.WriteLine($"ストレートのみ: PA {straight.Pa} OPS {straight.Ops:0.000} HR% {straight.HrRate:P2}");
        _out.WriteLine($"＋スラ＋フォーク: PA {withBreaking.Pa} OPS {withBreaking.Ops:0.000} HR% {withBreaking.HrRate:P2}");
        _out.WriteLine($"フォークのゴロ率 {withBreaking.BreakingGroundBallRate:P1}（打球 {withBreaking.BreakingBip}）"
            + $" / ストレートのゴロ率(同投手内) {withBreaking.FastballGroundBallRate:P1}（打球 {withBreaking.FastballBip}）");

        Assert.True(withBreaking.Ops < straight.Ops,
            $"変化球持ち投手の被OPS({withBreaking.Ops:0.000})がストレートのみ({straight.Ops:0.000})より低くなっていない（issue #835）");

        // 完了条件②: 同一投手内でもフォークのほうがゴロ率が高い（誘発縦変化がストレートより小さい＝ホップしない分だけ打球角度が下がるため。issue #911）。
        Assert.True(withBreaking.BreakingBip > 200 && withBreaking.FastballBip > 200,
            "ゴロ率比較に十分な打球数が無い");
        Assert.True(withBreaking.BreakingGroundBallRate > withBreaking.FastballGroundBallRate,
            $"フォークのゴロ率({withBreaking.BreakingGroundBallRate:P1})がストレート({withBreaking.FastballGroundBallRate:P1})を上回っていない");
    }
}
