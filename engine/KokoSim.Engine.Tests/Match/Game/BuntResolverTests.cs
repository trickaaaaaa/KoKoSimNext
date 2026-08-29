using KokoSim.Engine.Core;
using KokoSim.Engine.Match.Field;
using KokoSim.Engine.Match.Game;
using KokoSim.Engine.Players;
using Xunit;

namespace KokoSim.Engine.Tests.Match.Game;

/// <summary>
/// バント解決モデルの拡張（issue bunt-no-take-on-ball-pitches）。
/// ①ボール球見送り（能力依存の確率見送り）②失敗率の技術・球速・球威・コース連動③一塁走者の封殺（タイム勝負）
/// ④スクイズの三塁走者スタート連動、の4項目を固定する。校正値は🟡（Heavy実測前の第一推定）のため
/// 絶対値でなく単調性・到達可能性で検証する（design-02 §4.3/§4.4）。
/// </summary>
public sealed class BuntResolverTests
{
    private static readonly BaserunningCoefficients C = new();
    private static readonly FieldGeometry Field = new();
    private static readonly PitcherAttributes Pitcher = PitcherAttributes.LeagueAverage;

    // ── ①ボール球見送り ──

    [Fact]
    public void TakeProbability_IsZero_AtZoneEdge_AndRisesWithDistance()
    {
        var batter = new Player { Discipline = 50 };
        var atEdge = BuntResolver.TakeProbability(0.0, batter, C);
        var farOutside = BuntResolver.TakeProbability(0.5, batter, C);
        Assert.True(farOutside > atEdge, $"距離で見送り確率が上がらない: edge={atEdge:F2} far={farOutside:F2}");
    }

    [Fact]
    public void TakeProbability_RisesWithDiscipline()
    {
        var low = BuntResolver.TakeProbability(0.3, new Player { Discipline = 20 }, C);
        var high = BuntResolver.TakeProbability(0.3, new Player { Discipline = 90 }, C);
        Assert.True(high > low, $"選球眼で見送り確率が上がらない: low={low:F2} high={high:F2}");
    }

    // ── ②失敗率の技術・球速・球威・コース連動 ──

    [Fact]
    public void SuccessRate_FallsWithCourseDifficulty()
    {
        var batter = new Player { Bunt = 60 };
        var center = BuntResolver.SuccessRate(batter, Pitcher, courseDifficulty: 0.0, C);
        var edge = BuntResolver.SuccessRate(batter, Pitcher, courseDifficulty: 1.0, C);
        Assert.True(edge < center, $"コース難度で成功率が下がらない: center={center:F2} edge={edge:F2}");
    }

    [Fact]
    public void MissShare_FallsWithBuntSkill()
    {
        var pitcher = PitcherAttributes.LeagueAverage;
        double MissRate(int bunt)
        {
            var rng = new Xoshiro256Random(1);
            var batter = new Player { Bunt = bunt };
            var missed = 0;
            const int n = 20000;
            for (var i = 0; i < n; i++)
            {
                if (BuntResolver.Resolve(batter, pitcher, pitchStuff: 0.0, courseDifficulty: 0.0, safety: false,
                        firstRunner: null, secondRunner: null, thirdRunner: null, field: null, C, rng) == BuntResult.MissedBunt)
                    missed++;
            }
            return missed / (double)n;
        }

        var low = MissRate(20);
        var high = MissRate(90);
        Assert.True(high < low, $"バント技術で空振り率が下がらない: low={low:P1} high={high:P1}");
    }

    [Fact]
    public void MissShare_RisesWithPitcherVelocityAndStuff()
    {
        double MissRate(double velocityKmh, double stuff)
        {
            var rng = new Xoshiro256Random(2);
            var batter = new Player { Bunt = 55 };
            var pitcher = new PitcherAttributes { MaxVelocityKmh = velocityKmh };
            var missed = 0;
            const int n = 20000;
            for (var i = 0; i < n; i++)
            {
                if (BuntResolver.Resolve(batter, pitcher, pitchStuff: stuff, courseDifficulty: 0.0, safety: false,
                        firstRunner: null, secondRunner: null, thirdRunner: null, field: null, C, rng) == BuntResult.MissedBunt)
                    missed++;
            }
            return missed / (double)n;
        }

        var slowPlainBall = MissRate(125, 0.0);
        var fastPlainBall = MissRate(150, 0.0);
        Assert.True(fastPlainBall > slowPlainBall, $"球速で空振り率が上がらない: slow={slowPlainBall:P1} fast={fastPlainBall:P1}");

        var lowStuff = MissRate(132, 0.0);
        var highStuff = MissRate(132, 3.0);
        Assert.True(highStuff > lowStuff, $"球威で空振り率が上がらない: low={lowStuff:P1} high={highStuff:P1}");
    }

    [Fact]
    public void PopOutFrequency_RisesWithPitcherVelocity()
    {
        double PopRate(double velocityKmh)
        {
            var rng = new Xoshiro256Random(3);
            var batter = new Player { Bunt = 55 };
            var pitcher = new PitcherAttributes { MaxVelocityKmh = velocityKmh };
            var pop = 0;
            const int n = 20000;
            for (var i = 0; i < n; i++)
            {
                if (BuntResolver.Resolve(batter, pitcher, pitchStuff: 0.0, courseDifficulty: 0.0, safety: false,
                        firstRunner: null, secondRunner: null, thirdRunner: null, field: null, C, rng) == BuntResult.PopOut)
                    pop++;
            }
            return pop / (double)n;
        }

        var slow = PopRate(120);
        var fast = PopRate(155);
        Assert.True(fast > slow, $"球速で小フライ頻度が上がらない: slow={slow:P1} fast={fast:P1}");
    }

    // ── ③一塁走者の封殺（フォースアウト） ──

    [Fact]
    public void ForceOutAtSecond_NeverOccurs_WithoutFirstRunnerOrField()
    {
        var rng = new Xoshiro256Random(4);
        var batter = new Player { Bunt = 90, Speed = 99 };
        for (var i = 0; i < 5000; i++)
        {
            var r = BuntResolver.Resolve(batter, Pitcher, pitchStuff: 0.0, courseDifficulty: 0.0, safety: true,
                firstRunner: null, secondRunner: null, thirdRunner: null, field: null, C, rng);
            Assert.NotEqual(BuntResult.ForceOutAtSecond, r);
            Assert.NotEqual(BuntResult.ForceOutAtThird, r);
        }
    }

    [Fact]
    public void ForceOutAtSecond_Reachable_AndFallsWithRunnerSpeed()
    {
        // 打者が快足（一塁送球のアウトが不確実）だと、守備はより確実な二塁封殺へ切り替える
        // （BaserunningModel.ResolveDoublePlay と同じ「有利な方を選ぶ」比較原則, Q31）。
        double ForceOutRate(int runnerSpeed)
        {
            var rng = new Xoshiro256Random(5);
            var batter = new Player { Bunt = 90, Speed = 99 };
            var runner = new Player { Speed = runnerSpeed };
            var forced = 0;
            const int n = 20000;
            for (var i = 0; i < n; i++)
            {
                if (BuntResolver.Resolve(batter, Pitcher, pitchStuff: 0.0, courseDifficulty: 0.0, safety: true,
                        firstRunner: runner, secondRunner: null, thirdRunner: null, field: Field, C, rng) == BuntResult.ForceOutAtSecond)
                    forced++;
            }
            return forced / (double)n;
        }

        var slowRunner = ForceOutRate(1);
        var fastRunner = ForceOutRate(99);
        Assert.True(slowRunner > 0.0, "俊足打者×鈍足一塁走者でも一塁封殺が一度も出ない");
        Assert.True(slowRunner > fastRunner, $"一塁走者の走力で封殺率が下がらない: slow-runner={slowRunner:P1} fast-runner={fastRunner:P1}");
    }

    // ── ⑤二塁走者の封殺（1・2塁の真のフォース） ──

    [Fact]
    public void ForceOutAtThird_NeverOccurs_WithoutSecondRunner()
    {
        // 一塁走者だけ（二塁は空）では三塁封殺はそもそもフォースが成立しない。
        var rng = new Xoshiro256Random(6);
        var batter = new Player { Bunt = 90, Speed = 99 };
        var r1 = new Player { Speed = 1 };
        for (var i = 0; i < 5000; i++)
        {
            var r = BuntResolver.Resolve(batter, Pitcher, pitchStuff: 0.0, courseDifficulty: 0.0, safety: true,
                firstRunner: r1, secondRunner: null, thirdRunner: null, field: Field, C, rng);
            Assert.NotEqual(BuntResult.ForceOutAtThird, r);
        }
    }

    [Fact]
    public void ForceOutAtThird_Reachable_AndFallsWithRunnerSpeed()
    {
        // 1・2塁で二塁走者が鈍足だと、守備は一塁封殺よりさらに確実な三塁封殺へ切り替えうる
        // （BuntResolver.Resolve内の3択比較。ForceOutAtSecondの拡張版, Q31と同じ「有利な方を選ぶ」原則）。
        double ForceOutAtThirdRate(int r2Speed)
        {
            var rng = new Xoshiro256Random(7);
            var batter = new Player { Bunt = 90, Speed = 99 };
            var r1 = new Player { Speed = 50 };
            var r2 = new Player { Speed = r2Speed };
            var forced = 0;
            const int n = 20000;
            for (var i = 0; i < n; i++)
            {
                if (BuntResolver.Resolve(batter, Pitcher, pitchStuff: 0.0, courseDifficulty: 0.0, safety: true,
                        firstRunner: r1, secondRunner: r2, thirdRunner: null, field: Field, C, rng) == BuntResult.ForceOutAtThird)
                    forced++;
            }
            return forced / (double)n;
        }

        var slowRunner = ForceOutAtThirdRate(1);
        var fastRunner = ForceOutAtThirdRate(99);
        Assert.True(slowRunner > 0.0, "俊足打者×鈍足二塁走者でも三塁封殺が一度も出ない");
        Assert.True(slowRunner > fastRunner, $"二塁走者の走力で三塁封殺率が下がらない: slow-runner={slowRunner:P1} fast-runner={fastRunner:P1}");
    }

    // ── ⑥三塁走者の封殺（満塁の真のフォース） ──

    [Fact]
    public void ForceOutAtHome_NeverOccurs_WithoutThirdRunner()
    {
        // 1・2塁だけ（三塁は空）では本塁封殺はそもそもフォースが成立しない。
        var rng = new Xoshiro256Random(8);
        var batter = new Player { Bunt = 90, Speed = 99 };
        var r1 = new Player { Speed = 50 };
        var r2 = new Player { Speed = 1 };
        for (var i = 0; i < 5000; i++)
        {
            var r = BuntResolver.Resolve(batter, Pitcher, pitchStuff: 0.0, courseDifficulty: 0.0, safety: true,
                firstRunner: r1, secondRunner: r2, thirdRunner: null, field: Field, C, rng);
            Assert.NotEqual(BuntResult.ForceOutAtHome, r);
        }
    }

    [Fact]
    public void ForceOutAtHome_Reachable_AndFallsWithRunnerSpeed()
    {
        // 満塁で三塁走者が鈍足だと、守備は一塁/二塁封殺よりさらに確実な本塁封殺へ切り替えうる
        // （BuntResolver.Resolve内の4択比較。ForceOutAtThirdのさらなる拡張版, Q31と同じ「有利な方を選ぶ」原則）。
        double ForceOutAtHomeRate(int r3Speed)
        {
            var rng = new Xoshiro256Random(9);
            var batter = new Player { Bunt = 90, Speed = 99 };
            var r1 = new Player { Speed = 50 };
            var r2 = new Player { Speed = 50 };
            var r3 = new Player { Speed = r3Speed };
            var forced = 0;
            const int n = 20000;
            for (var i = 0; i < n; i++)
            {
                if (BuntResolver.Resolve(batter, Pitcher, pitchStuff: 0.0, courseDifficulty: 0.0, safety: true,
                        firstRunner: r1, secondRunner: r2, thirdRunner: r3, field: Field, C, rng) == BuntResult.ForceOutAtHome)
                    forced++;
            }
            return forced / (double)n;
        }

        var slowRunner = ForceOutAtHomeRate(1);
        var fastRunner = ForceOutAtHomeRate(99);
        Assert.True(slowRunner > 0.0, "俊足打者×鈍足三塁走者でも本塁封殺が一度も出ない");
        Assert.True(slowRunner > fastRunner, $"三塁走者の走力で本塁封殺率が下がらない: slow-runner={slowRunner:P1} fast-runner={fastRunner:P1}");
    }

    // ── ⑦小フライからのライナー併殺（design-09「小フライ併殺」） ──

    [Fact]
    public void PopOutDoubledOff_NeverOccurs_WithoutFirstRunnerOrField()
    {
        var rng = new Xoshiro256Random(10);
        var batter = new Player { Bunt = 55 };
        for (var i = 0; i < 20000; i++)
        {
            var r = BuntResolver.Resolve(batter, Pitcher, pitchStuff: 0.0, courseDifficulty: 0.0, safety: false,
                firstRunner: null, secondRunner: null, thirdRunner: null, field: null, C, rng);
            Assert.NotEqual(BuntResult.PopOutDoubledOff, r);
        }
    }

    [Fact]
    public void PopOutDoubledOff_Reachable_AndFallsWithRunnerSpeed()
    {
        // 小フライ（PopOut/転がしそこねの凡打）が出た一塁走者は、通常打球のStartType.Contactと同じ前提
        // （バント構えを見て事前に大きめの二次リード）で DoubledOffResolver の時間勝負に懸ける。
        double DoubledOffRate(int r1Speed)
        {
            var rng = new Xoshiro256Random(11);
            var batter = new Player { Bunt = 55 };
            var r1 = new Player { Speed = r1Speed };
            var doubled = 0;
            const int n = 20000;
            for (var i = 0; i < n; i++)
            {
                if (BuntResolver.Resolve(batter, Pitcher, pitchStuff: 0.0, courseDifficulty: 0.0, safety: false,
                        firstRunner: r1, secondRunner: null, thirdRunner: null, field: Field, C, rng)
                    == BuntResult.PopOutDoubledOff)
                    doubled++;
            }
            return doubled / (double)n;
        }

        var slowRunner = DoubledOffRate(1);
        var fastRunner = DoubledOffRate(99);
        Assert.True(slowRunner > 0.0, "鈍足一塁走者でも小フライ併殺が一度も出ない");
        Assert.True(slowRunner > fastRunner, $"一塁走者の走力で小フライ併殺率が下がらない: slow={slowRunner:P2} fast={fastRunner:P2}");
    }
}
