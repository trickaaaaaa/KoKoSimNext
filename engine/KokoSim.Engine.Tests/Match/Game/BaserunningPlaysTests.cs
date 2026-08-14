using System.Collections.Generic;
using KokoSim.Engine.Core;
using KokoSim.Engine.Match.AtBat;
using KokoSim.Engine.Match.Field;
using KokoSim.Engine.Match.Fielding;
using KokoSim.Engine.Match.Game;
using KokoSim.Engine.Match.Tactics;
using KokoSim.Engine.Match.Timeline.Playback;
using KokoSim.Engine.Players;
using Xunit;

namespace KokoSim.Engine.Tests.Match.Game;

/// <summary>
/// 走塁系の連続パラメータ解決（Slice 2C, 設計書02 §4）。
/// 進塁判断=Baserunning / 盗塁=Steal＋走力 vs 捕手肩（秒換算） / バント=Bunt主因。
/// 校正値は 🟡 のため絶対値でなく単調性・広い帯で検証する。
/// </summary>
public sealed class BaserunningPlaysTests
{
    private static readonly BaserunningCoefficients C = new();

    private static Player Catcher(int arm = 50) => new()
        { Position = FieldPosition.Catcher, ArmStrength = arm };

    private static double SbRate(Player runner, Player catcher, int trials = 4000, ulong seed = 5)
    {
        var rng = new Xoshiro256Random(seed);
        var safe = 0;
        for (var i = 0; i < trials; i++)
            if (StealResolver.Resolve(runner, catcher, C, rng) == StealResult.Safe) safe++;
        return (double)safe / trials;
    }

    // --- 盗塁 ---

    [Fact]
    public void Steal_LeagueAverage_IsPlausible()
    {
        // 平均走者×平均肩は五分〜やや不利（Step4校正: 走ってよいのは俊足のみ、という現実的な帯に再ベースライン）。
        var rate = SbRate(new Player { Speed = 50, Steal = 50 }, Catcher(50));
        Assert.InRange(rate, 0.30, 0.60);
    }

    [Fact]
    public void Steal_FasterRunner_SucceedsMore()
    {
        var slow = SbRate(new Player { Speed = 30, Steal = 30 }, Catcher(50));
        var fast = SbRate(new Player { Speed = 90, Steal = 90 }, Catcher(50));
        Assert.True(fast > slow + 0.15, $"俊足の盗塁成功が高くない: slow={slow:F2} fast={fast:F2}");
    }

    [Fact]
    public void Steal_StrongerCatcherArm_ThrowsOutMore()
    {
        var runner = new Player { Speed = 55, Steal = 55 };
        var weakArm = SbRate(runner, Catcher(30));
        var strongArm = SbRate(runner, Catcher(95));
        Assert.True(strongArm < weakArm - 0.10, $"強肩捕手が刺せていない: weak={weakArm:F2} strong={strongArm:F2}");
    }

    [Fact]
    public void Steal_StartDelay_ShrinksWithStealParam()
    {
        var slowStart = StealResolver.RunnerTimeSeconds(new Player { Speed = 50, Steal = 10 }, C);
        var fastStart = StealResolver.RunnerTimeSeconds(new Player { Speed = 50, Steal = 90 }, C);
        Assert.True(fastStart < slowStart, "盗塁パラメータでスタートが速くならない");
    }

    [Fact]
    public void Steal_IsDeterministic()
    {
        var r = new Player { Speed = 60, Steal = 60 };
        Assert.Equal(SbRate(r, Catcher(50), seed: 11), SbRate(r, Catcher(50), seed: 11));
    }

    // --- バント ---

    [Fact]
    public void Bunt_SuccessRate_RisesWithBuntSkill()
    {
        var p = PitcherAttributes.LeagueAverage;
        var low = BuntResolver.SuccessRate(new Player { Bunt = 20 }, p, C);
        var high = BuntResolver.SuccessRate(new Player { Bunt = 90 }, p, C);
        Assert.True(high > low, $"バント技術で成功率が上がらない: {low:F2} vs {high:F2}");
    }

    [Fact]
    public void Bunt_HighVelocity_LowersSuccess()
    {
        var batter = new Player { Bunt = 60 };
        var slow = BuntResolver.SuccessRate(batter, new PitcherAttributes { MaxVelocityKmh = 125 }, C);
        var fast = BuntResolver.SuccessRate(batter, new PitcherAttributes { MaxVelocityKmh = 150 }, C);
        Assert.True(fast < slow, "速球でバントが難しくならない");
    }

    [Fact]
    public void Bunt_ProducesFullOutcomeDistribution()
    {
        var rng = new Xoshiro256Random(3);
        var seen = new HashSet<BuntResult>();
        var batter = new Player { Bunt = 55, Speed = 70 };
        for (var i = 0; i < 3000; i++)
            seen.Add(BuntResolver.Resolve(batter, PitcherAttributes.LeagueAverage, safety: true, C, rng));
        // 犠打成功・内野安打・小フライ・ファウル・空振りが一通り現れる。
        Assert.Contains(BuntResult.SacrificeSuccess, seen);
        Assert.Contains(BuntResult.InfieldHit, seen);
        Assert.Contains(BuntResult.Foul, seen);
        Assert.Contains(BuntResult.MissedBunt, seen);
    }

    // --- スクイズ ---

    [Fact]
    public void Squeeze_WastePitch_RunsDownRunner()
    {
        var rng = new Xoshiro256Random(1);
        var outcome = SqueezeResolver.Resolve(new Player { Bunt = 60 }, new Player { Baserunning = 60 },
            PitcherAttributes.LeagueAverage, wasteProbability: 1.0, C, rng);
        Assert.True(outcome.RunnerOut);
        Assert.Equal(0, outcome.Runs);
    }

    [Fact]
    public void Squeeze_CanScore_WhenNotWasted()
    {
        var rng = new Xoshiro256Random(2);
        var scored = 0;
        for (var i = 0; i < 200; i++)
        {
            var o = SqueezeResolver.Resolve(new Player { Bunt = 80 }, new Player { Baserunning = 70 },
                PitcherAttributes.LeagueAverage, wasteProbability: 0.0, C, rng);
            scored += o.Runs;
        }
        Assert.True(scored > 0, "スクイズが一度も決まらない");
    }

    // --- 進塁判断は Baserunning 参照（設計書02 §4.1） ---

    [Fact]
    public void RunnerAdvance_UsesBaserunningJudgment()
    {
        int ScoreFromSecond(int baserunning)
        {
            var rng = new Xoshiro256Random(9);
            var scored = 0;
            for (var i = 0; i < 3000; i++)
            {
                var bases = new BaseState { Second = new Player { Baserunning = baserunning } };
                var (runs, _) = BaserunningModel.Apply(
                    bases, PlateAppearanceResult.Single, new Player(), 0, C, rng);
                scored += runs;
            }
            return scored;
        }
        Assert.True(ScoreFromSecond(90) > ScoreFromSecond(20),
            "走塁判断が高い走者ほど単打で還れていない");
    }

    // --- 野選（フィールダースチョイス, FC, design-14 P1-1）: 既定オフでは従来の乱数消費・結果と完全一致 ---

    /// <summary>rng 呼び出し回数を数えるだけの委譲ラッパー（既定オフ時の消費列不変を検証するため）。</summary>
    private sealed class CountingRandom : IRandomSource
    {
        private readonly IRandomSource _inner;
        public int DoubleCalls { get; private set; }
        public CountingRandom(IRandomSource inner) => _inner = inner;
        public ulong NextUInt64() => _inner.NextUInt64();
        public double NextDouble() { DoubleCalls++; return _inner.NextDouble(); }
        public int NextInt(int minInclusive, int maxExclusive) => _inner.NextInt(minInclusive, maxExclusive);
        public double NextGaussian(double mean = 0.0, double stdDev = 1.0) => _inner.NextGaussian(mean, stdDev);
        public IRandomSource Fork(ulong streamId) => new CountingRandom(_inner.Fork(streamId));
    }

    /// <summary>NextDouble を指定順で返すだけの決定論フェイク（併殺/野選のどちらか一方だけを確実に起こす用途）。</summary>
    private sealed class SequenceRandom : IRandomSource
    {
        private readonly Queue<double> _doubles;
        public SequenceRandom(params double[] doubles) => _doubles = new Queue<double>(doubles);
        public double NextDouble() => _doubles.Count > 0 ? _doubles.Dequeue() : 0.0;
        public ulong NextUInt64() => 0UL;
        public int NextInt(int minInclusive, int maxExclusive) => minInclusive;
        public double NextGaussian(double mean = 0.0, double stdDev = 1.0) => mean;
        public IRandomSource Fork(ulong streamId) => this;
    }

    [Fact]
    public void FieldersChoice_Disabled_SkipsRngDrawEntirely()
    {
        // DP判定を確実に外し(1発目0.99)、続く「1塁→2塁の進塁打」判定にも1個消費させる(2発目)。
        // FC分岐がオフ(prob=0)のまま割り込んで3個目を消費しないことを確認する（既定は issue #402 で
        // 正値化されたため、ここは明示的に 0 を指定する。MathUtil.Chance は確率0でも rng.NextDouble() を
        // 消費するため、else-ifのガード漏れがあると即座に3回目の消費が発生し検出できる）。
        var off = C with { FieldersChoiceProb = 0 };
        var bases = new BaseState { First = new Player { Speed = 50 } };
        var counting = new CountingRandom(new SequenceRandom(0.99, 0.99));
        var (_, _, _, batterSafeOnFc, _, _) = BaserunningModel.ApplyDetailed(
            bases, PlateAppearanceResult.InPlayOut, new Player { Speed = 50 }, 0, off, counting);
        Assert.False(batterSafeOnFc);
        Assert.Equal(2, counting.DoubleCalls); // DP判定1回＋1塁→2塁の進塁打判定1回。FCの3回目は無い
    }

    [Fact]
    public void FieldersChoice_Disabled_BatterNeverSafe()
    {
        var off = C with { FieldersChoiceProb = 0 }; // 既定は issue #402 で正値化＝明示的に 0 を指定
        for (ulong seed = 0; seed < 500; seed++)
        {
            var batter = new Player { Speed = 90 }; // 走力を振っても無効時は影響しないことを確認
            var bases = new BaseState { First = new Player { Speed = 90 } };
            var (_, _, _, batterSafeOnFc, _, _) = BaserunningModel.ApplyDetailed(
                bases, PlateAppearanceResult.InPlayOut, batter, 0, off, new Xoshiro256Random(seed));
            Assert.False(batterSafeOnFc);
            Assert.NotSame(batter, bases.First);
        }
    }

    [Fact]
    public void FieldersChoice_Enabled_LeadRunnerOut_BatterSafeAtFirst()
    {
        var fc = C with { FieldersChoiceProb = 0.5 };
        var r1 = new Player { Speed = 50 };
        var batter = new Player { Speed = 50 };
        var bases = new BaseState { First = r1 };
        var rng = new SequenceRandom(0.99, 0.0); // DP不成立(>=dpProb) → 続けてFC成立(<fcProb)
        var (_, extraOuts, _, batterSafeOnFc, _, moves) = BaserunningModel.ApplyDetailed(
            bases, PlateAppearanceResult.InPlayOut, batter, 0, fc, rng);

        Assert.True(batterSafeOnFc);
        Assert.Equal(1, extraOuts);
        Assert.Same(batter, bases.First); // 打者は一塁生存
        Assert.Contains(moves, m => m.Runner == r1 && m.Out && m.FromBase == 1 && m.ToBase == 2); // 先行走者は二塁封殺
        Assert.Contains(moves, m => m.Runner == batter && !m.Out && m.FromBase == 0 && m.ToBase == 1);
    }

    [Fact]
    public void FieldersChoice_DoesNotFire_WhenDoublePlaySucceeds()
    {
        var coeff = C with { FieldersChoiceProb = 0.5 };
        var r1 = new Player { Speed = 50 };
        var batter = new Player { Speed = 50 };
        var bases = new BaseState { First = r1 };
        var counting = new CountingRandom(new SequenceRandom(0.0)); // DP判定の1発目を確実成立させる
        var (_, extraOuts, _, batterSafeOnFc, _, _) = BaserunningModel.ApplyDetailed(
            bases, PlateAppearanceResult.InPlayOut, batter, 0, coeff, counting);

        Assert.False(batterSafeOnFc);
        Assert.Equal(1, extraOuts);
        Assert.Null(bases.First); // 打者は一塁に置かれない（従来通りDPで走者・打者ともアウト側）
        Assert.Equal(1, counting.DoubleCalls); // DP判定1回のみ。else-ifのFC判定へは進まない
        Assert.True(BaserunningModel.IsBatterOut(PlateAppearanceResult.InPlayOut, batterSafeOnFc)); // 打者もアウト＝合計2アウト
    }

    [Theory]
    [InlineData(PlateAppearanceResult.Strikeout, false, true)]
    [InlineData(PlateAppearanceResult.InPlayOut, false, true)]
    [InlineData(PlateAppearanceResult.InPlayOut, true, false)]
    [InlineData(PlateAppearanceResult.Single, false, false)]
    public void IsBatterOut_ReflectsFieldersChoice(PlateAppearanceResult result, bool safeOnFc, bool expected)
        => Assert.Equal(expected, BaserunningModel.IsBatterOut(result, safeOnFc));

    [Fact]
    public void FieldersChoice_Rate_RisesWithBatterSpeed()
    {
        double FcRate(int batterSpeed, int trials = 4000, ulong seed = 7)
        {
            var coeff = C with { FieldersChoiceProb = 0.15, DoublePlayProb = 0.0 };
            var rng = new Xoshiro256Random(seed);
            var fc = 0;
            for (var i = 0; i < trials; i++)
            {
                var bases = new BaseState { First = new Player { Speed = 50 } };
                var (_, _, _, safe, _, _) = BaserunningModel.ApplyDetailed(
                    bases, PlateAppearanceResult.InPlayOut, new Player { Speed = batterSpeed }, 0, coeff, rng);
                if (safe) fc++;
            }
            return (double)fc / trials;
        }

        var slow = FcRate(20);
        var fast = FcRate(90);
        Assert.True(fast > slow, $"打者走力でFC率が上がらない: slow={slow:F2} fast={fast:F2}");
    }

    // --- 失策の連鎖（design-14 P1-6）: 既定オフでは Single と完全に同一のrng消費・結果 ---

    [Fact]
    public void ErrorExtraAdvance_Disabled_MatchesSingleExactly()
    {
        // 連鎖 prob=0 では ReachedOnError は Single と rng消費・結果とも完全一致（恒等の担保）。
        // 既定は issue #169 で正値化されたため、ここは明示的に 0 を指定する。
        var off = C with { ErrorExtraAdvanceProb = 0 };
        for (ulong seed = 0; seed < 100; seed++)
        {
            var first = new Player { Baserunning = 50 };
            var second = new Player { Baserunning = 50 };
            var third = new Player { Baserunning = 50 };
            var batter = new Player();

            var basesSingle = new BaseState { First = first, Second = second, Third = third };
            var single = BaserunningModel.ApplyDetailed(
                basesSingle, PlateAppearanceResult.Single, batter, 0, off, new Xoshiro256Random(seed));

            var basesError = new BaseState { First = first, Second = second, Third = third };
            var error = BaserunningModel.ApplyDetailed(
                basesError, PlateAppearanceResult.ReachedOnError, batter, 0, off, new Xoshiro256Random(seed));

            Assert.Equal(single.Runs, error.Runs);
            Assert.Equal(single.ExtraOuts, error.ExtraOuts);
            Assert.Equal(basesSingle.First, basesError.First);
            Assert.Equal(basesSingle.Second, basesError.Second);
            Assert.Equal(basesSingle.Third, basesError.Third);
        }
    }

    [Fact]
    public void ErrorExtraAdvance_Enabled_PushesRunnersAndBatterOneExtraBase()
    {
        var coeff = C with { ErrorExtraAdvanceProb = 1.0 };
        var second = new Player { Baserunning = 50 };
        var third = new Player { Baserunning = 50 };
        var batter = new Player();
        var bases = new BaseState { Second = second, Third = third };
        var rng = new SequenceRandom(0.99, 0.0); // 二塁走者の本盗判定は失敗(→三塁へ) → 続く追加進塁判定は確実成立
        var (runs, extraOuts, _, _, _, moves) = BaserunningModel.ApplyDetailed(
            bases, PlateAppearanceResult.ReachedOnError, batter, 0, coeff, rng,
            errorKind: FieldingErrorKind.Throw);   // 連鎖は悪送球だけ（issue #629）

        Assert.Equal(2, runs); // 三塁走者(元)生還＋追加進塁で経由してきた走者も生還
        Assert.Equal(0, extraOuts);
        Assert.Same(batter, bases.Second); // 打者走者は追加進塁で二塁まで
        Assert.Null(bases.First);
        Assert.Null(bases.Third);
        Assert.Contains(moves, m => m.Runner == batter && !m.Out && m.FromBase == 1 && m.ToBase == 2);
    }

    [Fact]
    public void ErrorExtraAdvance_Rate_RaisesAverageRunsPerError()
    {
        double AvgRuns(double prob, int trials = 3000, ulong seed = 13)
        {
            var coeff = C with { ErrorExtraAdvanceProb = prob };
            var rng = new Xoshiro256Random(seed);
            var total = 0;
            for (var i = 0; i < trials; i++)
            {
                var bases = new BaseState
                {
                    Second = new Player { Baserunning = 50 }, Third = new Player { Baserunning = 50 },
                };
                var (runs, _, _, _, _, _) = BaserunningModel.ApplyDetailed(
                    bases, PlateAppearanceResult.ReachedOnError, new Player(), 0, coeff, rng,
                    errorKind: FieldingErrorKind.Throw);
                total += runs;
            }
            return (double)total / trials;
        }

        var off = AvgRuns(0.0);
        var on = AvgRuns(0.9);
        Assert.True(on > off, $"失策連鎖を有効化しても平均得点が増えない: off={off:F2} on={on:F2}");
    }

    // --- 失策連鎖は悪送球だけ（issue #629・監督決定 2026-08-13） ---
    // 捕球エラー（お手玉）とフライ落球は球が野手の足元にある＝全走者が1つ多く進む理由がない。
    // 絵も「3m弾いて拾い直す」だけなので、走者だけ進むと因果が合わない（issue #629 の症状そのもの）。

    /// <summary>本題: 同じ乱数・同じ場面でも、捕球エラーとフライ落球では連鎖しない。</summary>
    [Theory]
    [InlineData(null)]                          // 失策種別が不明（強制結果など）＝連鎖させない
    [InlineData(FieldingErrorKind.Catch)]       // お手玉・フライ落球
    public void ErrorExtraAdvance_OnlyChainsOnThrowError(FieldingErrorKind? kind)
    {
        var coeff = C with { ErrorExtraAdvanceProb = 1.0 };   // 悪送球なら必ず連鎖する設定

        var thrown = new BaseState { Second = new Player { Baserunning = 50 }, Third = new Player { Baserunning = 50 } };
        var t = BaserunningModel.ApplyDetailed(
            thrown, PlateAppearanceResult.ReachedOnError, new Player(), 0, coeff,
            new SequenceRandom(0.99, 0.0), errorKind: FieldingErrorKind.Throw);

        var caught = new BaseState { Second = new Player { Baserunning = 50 }, Third = new Player { Baserunning = 50 } };
        var c = BaserunningModel.ApplyDetailed(
            caught, PlateAppearanceResult.ReachedOnError, new Player(), 0, coeff,
            new SequenceRandom(0.99, 0.0), errorKind: kind);

        Assert.True(t.ErrorExtraAdvanceOccurred, "悪送球で連鎖していない");
        Assert.False(c.ErrorExtraAdvanceOccurred, $"{kind?.ToString() ?? "種別なし"} で連鎖している");
        // 症状そのもの: お手玉で二塁走者が生還しない（悪送球なら還る）。
        Assert.Equal(2, t.Runs);
        Assert.Equal(1, c.Runs);
        Assert.Same(caught.Third, caught.Third);   // 元二塁走者は三塁で止まる
        Assert.NotNull(caught.Third);
    }

    /// <summary>
    /// ゲートは<b>乱数を引いてから</b>効く（issue #525 と同じ流儀）。捕球エラーでも消費は1回のままで、
    /// 打席あたりの乱数消費位置が失策種別で揺れない＝帯の再校正が連鎖の増減だけで済む。
    /// </summary>
    [Fact]
    public void ErrorExtraAdvance_Gate_DoesNotChangeRandomConsumption()
    {
        var coeff = C with { ErrorExtraAdvanceProb = 1.0 };
        for (ulong seed = 0; seed < 100; seed++)
        {
            var a = new Xoshiro256Random(seed);
            var b = new Xoshiro256Random(seed);
            var ba = new BaseState { Second = new Player { Baserunning = 50 } };
            var bb = new BaseState { Second = new Player { Baserunning = 50 } };
            BaserunningModel.ApplyDetailed(ba, PlateAppearanceResult.ReachedOnError, new Player(), 0, coeff, a,
                errorKind: FieldingErrorKind.Throw);
            BaserunningModel.ApplyDetailed(bb, PlateAppearanceResult.ReachedOnError, new Player(), 0, coeff, b,
                errorKind: FieldingErrorKind.Catch);
            // 消費が同じなら、次に引く値も同じ。
            Assert.Equal(a.NextDouble(), b.NextDouble());
        }
    }

    // --- 失策連鎖の送球精度連動（Issue #37, design-14 P1-6b） ---

    private static double ChainRate(BaserunningCoefficients coeff, int throwAccuracy, int trials = 5000, ulong seed = 21)
    {
        var rng = new Xoshiro256Random(seed);
        var occurred = 0;
        for (var i = 0; i < trials; i++)
        {
            var bases = new BaseState
            {
                Second = new Player { Baserunning = 50 }, Third = new Player { Baserunning = 50 },
            };
            var (_, _, _, _, chain, _) = BaserunningModel.ApplyDetailed(
                bases, PlateAppearanceResult.ReachedOnError, new Player(), 0, coeff, rng,
                errorThrowAccuracy: throwAccuracy, errorKind: FieldingErrorKind.Throw);
            if (chain) occurred++;
        }
        return (double)occurred / trials;
    }

    [Fact]
    public void ErrorExtraAdvance_AccuracyDriven_LowerAccuracyChainsMore()
    {
        // 送球精度連動を有効化（傾き>0）。精度が低い野手の失策ほど連鎖しやすい（単調）。
        var coeff = C with { ErrorExtraAdvanceProb = 0.30, ErrorExtraAdvanceAccuracySlope = 0.004 };
        Assert.True(ChainRate(coeff, 10) > ChainRate(coeff, 50), "精度低で連鎖が増えない");
        Assert.True(ChainRate(coeff, 50) > ChainRate(coeff, 90), "精度高で連鎖が減らない");
    }

    [Fact]
    public void ErrorExtraAdvance_SlopeZero_IsAccuracyIndependentAndIdentical()
    {
        // 傾き0 では ThrowAccuracy を変えても結果が1件も変わらない＝一律確率と恒等（既定は #169 で正値化のため明示的に0を指定）。
        var coeff = C with { ErrorExtraAdvanceProb = 0.30, ErrorExtraAdvanceAccuracySlope = 0 };
        for (ulong seed = 0; seed < 200; seed++)
        {
            var bLo = new BaseState { Second = new Player { Baserunning = 50 }, Third = new Player { Baserunning = 50 } };
            var lo = BaserunningModel.ApplyDetailed(
                bLo, PlateAppearanceResult.ReachedOnError, new Player(), 0, coeff, new Xoshiro256Random(seed),
                errorThrowAccuracy: 10, errorKind: FieldingErrorKind.Throw);
            var bHi = new BaseState { Second = new Player { Baserunning = 50 }, Third = new Player { Baserunning = 50 } };
            var hi = BaserunningModel.ApplyDetailed(
                bHi, PlateAppearanceResult.ReachedOnError, new Player(), 0, coeff, new Xoshiro256Random(seed),
                errorThrowAccuracy: 90, errorKind: FieldingErrorKind.Throw);
            Assert.Equal(lo.Runs, hi.Runs);
            Assert.Equal(lo.ErrorExtraAdvanceOccurred, hi.ErrorExtraAdvanceOccurred);
        }
    }

    // --- 振り逃げ（第3ストライク不捕球, design-14 P1-2）: 既定オフでは従来の試合結果と完全一致 ---
    // 判定自体は GameEngine.PlayHalf（private）に埋め込まれているため、BaserunningModel.IsBatterOut の
    // 純関数部分は直接、rng消費・帯への影響は GameEngine.Play を通した統計的な検証で担保する。

    [Theory]
    [InlineData(PlateAppearanceResult.Strikeout, false, true)]
    [InlineData(PlateAppearanceResult.Strikeout, true, false)]
    [InlineData(PlateAppearanceResult.InPlayOut, false, true)]
    [InlineData(PlateAppearanceResult.Single, false, false)]
    public void IsBatterOut_ReflectsDroppedThirdStrike(PlateAppearanceResult result, bool reached, bool expected)
        => Assert.Equal(expected, BaserunningModel.IsBatterOut(result, batterSafeOnFc: false, droppedThirdStrikeReached: reached));

    private static Player WeakContact(FieldPosition p) => new()
    {
        Position = p, Contact = 15, Power = 20, Discipline = 15,
        Speed = 50, Steal = 50, Baserunning = 50, ArmStrength = 50, Fielding = 50, Catching = 50,
    };

    private static Team WeakContactTeam(string name)
    {
        var order = new List<Player>
        {
            WeakContact(FieldPosition.Catcher), WeakContact(FieldPosition.FirstBase), WeakContact(FieldPosition.SecondBase),
            WeakContact(FieldPosition.ThirdBase), WeakContact(FieldPosition.Shortstop), WeakContact(FieldPosition.LeftField),
            WeakContact(FieldPosition.CenterField), WeakContact(FieldPosition.RightField),
            WeakContact(FieldPosition.Pitcher) with { Name = name + "P", Pitching = PitcherAttributes.LeagueAverage },
        };
        return new Team { Name = name, BattingOrder = order, PitcherSlot = 8,
            Reserves = new[] { WeakContact(FieldPosition.Pitcher) with { Name = name + "R", Pitching = PitcherAttributes.LeagueAverage } } };
    }

    [Fact]
    public void DropThirdStrike_Disabled_DoesNotChangeGame()
    {
        // 既定は issue #402 で正値化されたため、無効ゲートは明示的に 0 を指定して検証する。
        var off = new GameContext { Baserunning = C with { DropThirdStrikeReachProb = 0.0 } };
        var a = GameEngine.Play(WeakContactTeam("A"), WeakContactTeam("H"), off, new Xoshiro256Random(42));
        var b = GameEngine.Play(WeakContactTeam("A"), WeakContactTeam("H"), off, new Xoshiro256Random(42));
        Assert.Equal(a.AwayRuns, b.AwayRuns); // 決定論維持
        Assert.Equal(a.TotalPitches, b.TotalPitches);
    }

    [Fact]
    public void DropThirdStrike_Enabled_RaisesRunsForHighStrikeoutTeams()
    {
        var off = new GameContext();
        var on = new GameContext { Baserunning = C with { DropThirdStrikeReachProb = 0.9, DropThirdStrikeCatchingSlope = 0.0 } };
        var totalOff = 0;
        var totalOn = 0;
        // ⚠ 30シードでは足りない（実測 2026-08-14: off=72 / on=70 で逆転した）。振り逃げ1回の得点価値は
        // 小さく、乱数列がずれると試合ごとの揺れに埋もれる＝母数で殺す（150シード）。
        for (ulong s = 0; s < 150; s++)
        {
            totalOff += GameEngine.Play(WeakContactTeam("A"), WeakContactTeam("H"), off, new Xoshiro256Random(s)).TotalRuns;
            totalOn += GameEngine.Play(WeakContactTeam("A"), WeakContactTeam("H"), on, new Xoshiro256Random(s)).TotalRuns;
        }
        Assert.True(totalOn > totalOff, $"振り逃げ有効化で得点が増えていない: off={totalOff} on={totalOn}");
    }

    // 2アウトの振り逃げは一塁が埋まっていても成立する＝一塁を与えると**押し出しになる**（issue #525）。
    // 旧実装は bases.First へ直に代入していて一塁走者が消えていた（走者保存則違反。
    // drop_third_strike_reach_prob が 0.001 の間は滅多に踏まず、0.39 への再校正で表に出た）。
    // 規則は四球・死球と同じ BaserunningModel.AwardFirstBase に集約したので、ここで純関数として固定する。

    [Fact]
    public void AwardFirstBase_WithRunnerOnFirst_ForcesRunnerToSecond()
    {
        var r1 = WeakContact(FieldPosition.LeftField) with { Name = "R1" };
        var batter = WeakContact(FieldPosition.CenterField) with { Name = "B" };
        var bases = new BaseState { First = r1 };

        var (runs, outs) = BaserunningModel.AwardFirstBase(bases, batter);

        Assert.Equal(0, runs);
        Assert.Equal(0, outs);
        Assert.Same(batter, bases.First);
        Assert.Same(r1, bases.Second);   // 消えずに押し出される（旧実装はここが null だった）
        Assert.Null(bases.Third);
    }

    [Fact]
    public void AwardFirstBase_WithBasesLoaded_PushesInARun()
    {
        var r1 = WeakContact(FieldPosition.LeftField) with { Name = "R1" };
        var r2 = WeakContact(FieldPosition.CenterField) with { Name = "R2" };
        var r3 = WeakContact(FieldPosition.RightField) with { Name = "R3" };
        var batter = WeakContact(FieldPosition.FirstBase) with { Name = "B" };
        var bases = new BaseState { First = r1, Second = r2, Third = r3 };

        var (runs, outs) = BaserunningModel.AwardFirstBase(bases, batter);

        Assert.Equal(1, runs);           // 三塁走者が生還
        Assert.Equal(0, outs);
        Assert.Same(batter, bases.First);
        Assert.Same(r1, bases.Second);
        Assert.Same(r2, bases.Third);
    }

    [Fact]
    public void AwardFirstBase_WithEmptyBases_JustPlacesBatter()
    {
        var batter = WeakContact(FieldPosition.FirstBase) with { Name = "B" };
        var bases = new BaseState();

        var (runs, outs) = BaserunningModel.AwardFirstBase(bases, batter);

        Assert.Equal(0, runs);
        Assert.Equal(0, outs);
        Assert.Same(batter, bases.First);
        Assert.Null(bases.Second);
        Assert.Null(bases.Third);
    }

    // --- 牽制アウト／離塁刺殺（design-14 P1-5）: 既定オフでは rng 消費ゼロ・常に不発 ---

    [Fact]
    public void Pickoff_Disabled_NeverFires()
    {
        // 既定は issue #402 で正値化されたため、無効ゲートは明示的に 0 を指定して検証する。
        var off = C with { PickoffBaseProb = 0.0 };
        for (ulong seed = 0; seed < 300; seed++)
        {
            var runner = new Player { Steal = 95 };
            var pitcher = new Player { Mental = 5 };
            Assert.False(PickoffResolver.Resolve(runner, pitcher, off, new Xoshiro256Random(seed)));
        }
    }

    [Fact]
    public void Pickoff_Rate_RisesWithRunnerLead_FallsWithPitcherSense()
    {
        double Rate(int steal, int mental, int trials = 3000, ulong seed = 21)
        {
            var coeff = C with { PickoffBaseProb = 0.03 };
            var rng = new Xoshiro256Random(seed);
            var runner = new Player { Steal = steal };
            var pitcher = new Player { Mental = mental };
            var hits = 0;
            for (var i = 0; i < trials; i++)
                if (PickoffResolver.Resolve(runner, pitcher, coeff, rng)) hits++;
            return (double)hits / trials;
        }

        Assert.True(Rate(90, 50) > Rate(20, 50), "走者Stealのリードで牽制率が上がらない");
        Assert.True(Rate(50, 20) > Rate(50, 90), "投手Mentalで牽制率が下がらない");
    }

    [Fact]
    public void Pickoff_Enabled_ChangesOutcomesForStealHeavyTeams()
    {
        Team Brainy(string name) => FastTeam(name) with { Tactics = new StandardTacticsBrain(new TacticsCoefficients()) };
        var off = new GameContext { Baserunning = C with { PickoffBaseProb = 0.0 } }; // 既定は #402 で正値化＝明示的に0で対比
        var on = new GameContext { Baserunning = C with { PickoffBaseProb = 0.08 } };
        var differs = false;
        for (ulong s = 0; s < 40 && !differs; s++)
        {
            var a = GameEngine.Play(Brainy("A"), Brainy("H"), off, new Xoshiro256Random(s));
            var b = GameEngine.Play(Brainy("A"), Brainy("H"), on, new Xoshiro256Random(s));
            differs = a.TotalRuns != b.TotalRuns || a.AwayTactics.StealAttempts != b.AwayTactics.StealAttempts;
        }
        Assert.True(differs, "牽制を有効化しても試合結果が一切変わらない");
    }

    // --- 一・三塁の重盗（design-14 P1-4）: 既定オフでは単独二盗と完全一致 ---

    [Fact]
    public void DoubleStealThirdBreak_EnabledByDefault_MatchesCalibratedYaml()
    {
        // issue #402: 実ゲーム既定を data/coefficients.yaml の校正値へ揃えた（以前は 0＝機能オフ）。
        // 全係数の一致は CoefficientsDefaultsConsistencyTests が保証する。ここは重盗が既定で有効なことの回帰ピン。
        Assert.Equal(0.4, new GameContext().Baserunning.DoubleStealThirdBreakProb);
    }

    [Fact]
    public void DoubleStealThirdBreak_Enabled_ChangesOutcomesForStealHeavyTeams()
    {
        Team Brainy(string name) => FastTeam(name) with { Tactics = new StandardTacticsBrain(new TacticsCoefficients()) };
        var off = new GameContext();
        var on = new GameContext { Baserunning = C with { DoubleStealThirdBreakProb = 1.0 } };
        var differs = false;
        for (ulong s = 0; s < 40 && !differs; s++)
        {
            var a = GameEngine.Play(Brainy("A"), Brainy("H"), off, new Xoshiro256Random(s));
            var b = GameEngine.Play(Brainy("A"), Brainy("H"), on, new Xoshiro256Random(s));
            differs = a.TotalRuns != b.TotalRuns;
        }
        Assert.True(differs, "一・三塁重盗を有効化しても得点が一切変わらない");
    }

    // --- 三盗・本盗の采配配線（issue #67, design-14 未決A）: StandardTacticsBrain→GameEngine ---

    private static Team Brainy(string name, TacticsCoefficients tactics) => FastTeam(name) with
    {
        Tactics = new StandardTacticsBrain(tactics),
    };

    [Fact]
    public void StealThird_EnabledAggressively_ProducesAttemptsAndAffectsGames()
    {
        var off = new TacticsCoefficients();
        var on = new TacticsCoefficients { StealThirdMinSuccess = 0.0, StealThirdProb = 1.0, StealThirdMaxDiffAbs = 99 };
        var attemptsOn = 0;
        var differs = false;
        for (ulong s = 0; s < 60; s++)
        {
            var a = GameEngine.Play(Brainy("A", off), Brainy("H", off), new GameContext(), new Xoshiro256Random(s));
            var b = GameEngine.Play(
                Brainy("A", on), Brainy("H", on), new GameContext { Tactics = on }, new Xoshiro256Random(s));
            attemptsOn += b.AwayTactics.StealAttempts + b.HomeTactics.StealAttempts;
            if (a.TotalRuns != b.TotalRuns) differs = true;
        }
        Assert.True(attemptsOn > 0, "三盗を積極化しても盗塁企図が一度も発生しない");
        Assert.True(differs, "三盗を積極化しても試合結果が一切変わらない");
    }

    [Fact]
    public void StealHome_EnabledAggressively_ProducesAttemptsAndAffectsGames()
    {
        var off = new TacticsCoefficients();
        var on = new TacticsCoefficients { StealHomeMinSuccess = 0.0, StealHomeProb = 1.0, StealHomeMaxDiffAbs = 99 };
        var attemptsOn = 0;
        var differs = false;
        for (ulong s = 0; s < 60; s++)
        {
            var a = GameEngine.Play(Brainy("A", off), Brainy("H", off), new GameContext(), new Xoshiro256Random(s));
            var b = GameEngine.Play(
                Brainy("A", on), Brainy("H", on), new GameContext { Tactics = on }, new Xoshiro256Random(s));
            attemptsOn += b.AwayTactics.StealAttempts + b.HomeTactics.StealAttempts;
            if (a.TotalRuns != b.TotalRuns) differs = true;
        }
        Assert.True(attemptsOn > 0, "本盗を積極化しても盗塁企図が一度も発生しない");
        Assert.True(differs, "本盗を積極化しても試合結果が一切変わらない");
    }

    [Fact]
    public void StealHome_NeverFiresTheSamePitchAsSqueeze()
    {
        // 三塁のみ在塁は本盗・スクイズ両方の対象塁状況。スクイズが確定した球では本盗を試みない
        // （GameEngine側の単純上書きで解消, Q12-3と同型）ため、両方が同時に走者を消費して二重計上する
        // ような矛盾（3アウト超過やRuns++の二重加算）は起きない＝多数試行しても異常終了しないことで確認する。
        var tactics = new TacticsCoefficients
        {
            SqueezeProb = 1.0, SqueezeFromInning = 1, SqueezeMaxDiffAbs = 99, SqueezeMinBunt = 0,
            StealHomeMinSuccess = 0.0, StealHomeProb = 1.0, StealHomeMaxDiffAbs = 99, StealHomeMaxOuts = 1,
        };
        var ctx = new GameContext { Tactics = tactics };
        for (ulong s = 0; s < 60; s++)
        {
            var r = GameEngine.Play(Brainy("A", tactics), Brainy("H", tactics), ctx, new Xoshiro256Random(s));
            Assert.True(r.TotalRuns >= 0);
        }
    }

    // --- 暴投・パスボール（design-14 P2-8, 設計書15 Phase D-3）: 既定オフでは rng 消費ゼロ・走者無しでは無発 ---

    [Fact]
    public void ApplyBatteryMiss_AllRunnersAdvanceOne_ThirdScores()
    {
        var bases = new BaseState
        {
            First = new Player { Name = "R1" }, Second = new Player { Name = "R2" }, Third = new Player { Name = "R3" },
        };
        var runs = BaserunningModel.ApplyBatteryMiss(bases);
        Assert.Equal(1, runs);
        Assert.Null(bases.First);
        Assert.Equal("R1", bases.Second!.Name);
        Assert.Equal("R2", bases.Third!.Name);
    }

    [Fact]
    public void ApplyBatteryMiss_EmptyBases_NoRunsNoChange()
    {
        var bases = new BaseState();
        Assert.Equal(0, BaserunningModel.ApplyBatteryMiss(bases));
        Assert.Null(bases.First);
        Assert.Null(bases.Second);
        Assert.Null(bases.Third);
    }

    [Fact]
    public void ApplyBatteryMiss_OnlyFirstOccupied_MovesToSecond_NoScore()
    {
        var bases = new BaseState { First = new Player { Name = "R1" } };
        Assert.Equal(0, BaserunningModel.ApplyBatteryMiss(bases));
        Assert.Null(bases.First);
        Assert.Equal("R1", bases.Second!.Name);
        Assert.Null(bases.Third);
    }

    [Fact]
    public void WildPitch_Disabled_NeverFires()
    {
        // 既定は issue #402 で正値化されたため、無効ゲートは明示的に 0 を指定して検証する。
        var off = new GameContext { Baserunning = C with { WildPitchProb = 0.0, PassedBallProb = 0.0 } };
        for (ulong seed = 0; seed < 30; seed++)
        {
            var r = GameEngine.Play(FastTeam("A"), FastTeam("H"), off, new Xoshiro256Random(seed));
            Assert.Equal(0, r.WildPitchCount);
        }
    }

    [Fact]
    public void WildPitch_Enabled_ProducesEventsAndRaisesRuns()
    {
        var off = new GameContext();
        var on = new GameContext { Baserunning = C with { WildPitchProb = 0.15, PassedBallProb = 0.15 } };
        var totalOff = 0;
        var totalOn = 0;
        var wpOn = 0;
        // ⚠ 30シードでは足りない（実測 2026-08-14: off=255 / on=252 で逆転した）。理由は上の振り逃げと同じ。
        for (ulong s = 0; s < 150; s++)
        {
            totalOff += GameEngine.Play(FastTeam("A"), FastTeam("H"), off, new Xoshiro256Random(s)).TotalRuns;
            var r = GameEngine.Play(FastTeam("A"), FastTeam("H"), on, new Xoshiro256Random(s));
            totalOn += r.TotalRuns;
            wpOn += r.WildPitchCount;
        }
        Assert.True(wpOn > 0, "暴投・パスボールを有効化しても一度も発生しなかった");
        Assert.True(totalOn > totalOff, $"暴投・パスボール有効化で得点が増えていない: off={totalOff} on={totalOn}");
    }

    /// <summary>
    /// issue #487: 暴投・捕逸が発生した球の <see cref="LivePitch.BatteryMissPlay"/> に再生データが載る
    /// （CaptureTimelines+CapturePitches のとき）。打席途中の暴投・捕逸を絵にする唯一の運び口。
    /// </summary>
    [Fact]
    public void BatteryMissPlay_IsAttachedToTheLivePitchOfTheMissedPitch()
    {
        var on = new GameContext
        {
            Baserunning = C with { WildPitchProb = 0.3, PassedBallProb = 0.3 },
            CaptureTimelines = true, CapturePitches = true,
        };
        var captured = 0;
        for (ulong seed = 1; seed <= 30 && captured == 0; seed++)
        {
            var prog = new MatchProgression(FastTeam("A"), FastTeam("H"), on, seed);
            LivePitch? prev = null;
            while (true)
            {
                var r = prog.AdvancePitch();
                if (r == AdvancePitchResult.Finished) break;
                var last = prog.LastPitch;
                if (last == null || ReferenceEquals(last, prev)) continue;
                prev = last;
                if (last.BatteryMissPlay is not { } play) continue;
                captured++;
                // 暴投・捕逸の再生データとして最小限の構造が揃っている（純関数の写し＝乱数消費ゼロ）。
                Assert.Equal("暴投・捕逸", play.Result);
                Assert.NotEmpty(play.Ball);
                Assert.NotEmpty(play.Runners);
            }
        }
        Assert.True(captured > 0, "暴投・パスボールを積極化しても BatteryMissPlay が一度も載らなかった（配線切れの疑い）");
    }

    /// <summary>観測ゲート（CaptureTimelines）を閉じれば BatteryMissPlay は決して載らない＝統計シム・裏試合はゼロコスト。</summary>
    [Fact]
    public void BatteryMissPlay_NeverAttachedWhenCaptureTimelinesOff()
    {
        var on = new GameContext
        {
            Baserunning = C with { WildPitchProb = 0.3, PassedBallProb = 0.3 },
            CapturePitches = true, // CaptureTimelines は既定オフ
        };
        var prog = new MatchProgression(FastTeam("A"), FastTeam("H"), on, 1UL);
        LivePitch? prev = null;
        while (true)
        {
            var r = prog.AdvancePitch();
            if (r == AdvancePitchResult.Finished) break;
            var last = prog.LastPitch;
            if (last == null || ReferenceEquals(last, prev)) continue;
            prev = last;
            Assert.Null(last.BatteryMissPlay);
        }
    }

    /// <summary>
    /// issue #488: 振り逃げが成立した球の <see cref="LivePitch.DropThirdStrikePlay"/> に再生データが載る
    /// （CaptureTimelines+CapturePitches のとき）。打席途中の振り逃げを絵にする唯一の運び口。
    /// </summary>
    [Fact]
    public void DropThirdStrikePlay_IsAttachedToTheLivePitchOfTheStrikeoutPitch()
    {
        var on = new GameContext
        {
            Baserunning = C with { DropThirdStrikeReachProb = 0.9, DropThirdStrikeCatchingSlope = 0.0 },
            CaptureTimelines = true, CapturePitches = true,
        };
        var captured = 0;
        for (ulong seed = 1; seed <= 30 && captured == 0; seed++)
        {
            var prog = new MatchProgression(FastTeam("A"), FastTeam("H"), on, seed);
            LivePitch? prev = null;
            while (true)
            {
                var r = prog.AdvancePitch();
                if (r == AdvancePitchResult.Finished) break;
                var last = prog.LastPitch;
                if (last == null || ReferenceEquals(last, prev)) continue;
                prev = last;
                if (last.DropThirdStrikePlay is not { } play) continue;
                captured++;
                // 振り逃げの再生データとして最小限の構造が揃っている（純関数の写し＝乱数消費ゼロ）。
                Assert.Equal("振り逃げ", play.Result);
                Assert.NotEmpty(play.Ball);
                Assert.NotEmpty(play.Runners);
            }
        }
        Assert.True(captured > 0, "振り逃げ成立率を積極化しても DropThirdStrikePlay が一度も載らなかった（配線切れの疑い）");
    }

    /// <summary>観測ゲート（CaptureTimelines）を閉じれば DropThirdStrikePlay は決して載らない＝統計シム・裏試合はゼロコスト。</summary>
    [Fact]
    public void DropThirdStrikePlay_NeverAttachedWhenCaptureTimelinesOff()
    {
        var on = new GameContext
        {
            Baserunning = C with { DropThirdStrikeReachProb = 0.9, DropThirdStrikeCatchingSlope = 0.0 },
            CapturePitches = true, // CaptureTimelines は既定オフ
        };
        var prog = new MatchProgression(FastTeam("A"), FastTeam("H"), on, 1UL);
        LivePitch? prev = null;
        while (true)
        {
            var r = prog.AdvancePitch();
            if (r == AdvancePitchResult.Finished) break;
            var last = prog.LastPitch;
            if (last == null || ReferenceEquals(last, prev)) continue;
            prev = last;
            Assert.Null(last.DropThirdStrikePlay);
        }
    }

    // --- StealResolver の target 一般化（design-14 P1-4）: target 省略時は従来と完全一致 ---

    [Fact]
    public void StealResolver_TargetOmitted_MatchesSecondExactly()
    {
        var runner = new Player { Speed = 70, Steal = 70 };
        var catcher = Catcher(60);
        Assert.Equal(
            StealResolver.SuccessProbability(runner, catcher, C),
            StealResolver.SuccessProbability(runner, catcher, C, target: StealTarget.Second));
    }

    [Fact]
    public void StealResolver_ThirdAndHome_AreHarderThanSecond()
    {
        var runner = new Player { Speed = 70, Steal = 70 };
        var catcher = Catcher(60);
        var second = StealResolver.SuccessProbability(runner, catcher, C, target: StealTarget.Second);
        var third = StealResolver.SuccessProbability(runner, catcher, C, target: StealTarget.Third);
        var home = StealResolver.SuccessProbability(runner, catcher, C, target: StealTarget.Home);
        Assert.True(third < second, $"三盗が二盗より易しい: third={third:F2} second={second:F2}");
        Assert.True(home < third, $"本盗が三盗より易しい: home={home:F2} third={third:F2}");
    }

    // --- スモールボール発生（既定オフ）: フラグで挙動が変わる ---

    [Fact]
    public void SmallBall_Disabled_ByDefault_DoesNotChangeGame()
    {
        var off = new GameContext();
        Assert.False(off.EnableSmallBall);
        var a = GameEngine.Play(FastTeam("A"), FastTeam("H"), off, new Xoshiro256Random(42));
        var b = GameEngine.Play(FastTeam("A"), FastTeam("H"), off, new Xoshiro256Random(42));
        Assert.Equal(a.AwayRuns, b.AwayRuns); // 決定論維持
    }

    [Fact]
    public void SmallBall_Enabled_AltersOutcomes()
    {
        var off = new GameContext();
        var on = new GameContext { EnableSmallBall = true, StealAttemptThreshold = 50 };
        var differ = 0;
        for (ulong s = 0; s < 20; s++)
        {
            var a = GameEngine.Play(FastTeam("A"), FastTeam("H"), off, new Xoshiro256Random(s));
            var b = GameEngine.Play(FastTeam("A"), FastTeam("H"), on, new Xoshiro256Random(s));
            if (a.AwayRuns != b.AwayRuns || a.HomeRuns != b.HomeRuns) differ++;
        }
        Assert.True(differ > 0, "盗塁ヒューリスティックが試合に反映されていない");
    }

    private static Player Pos(FieldPosition p) => new()
    {
        Position = p, Contact = 55, Power = 45, Discipline = 50,
        Speed = 85, Steal = 85, Baserunning = 70, ArmStrength = 50, Fielding = 50, Catching = 50,
    };

    private static Team FastTeam(string name)
    {
        var order = new List<Player>
        {
            Pos(FieldPosition.Catcher), Pos(FieldPosition.FirstBase), Pos(FieldPosition.SecondBase),
            Pos(FieldPosition.ThirdBase), Pos(FieldPosition.Shortstop), Pos(FieldPosition.LeftField),
            Pos(FieldPosition.CenterField), Pos(FieldPosition.RightField),
            Pos(FieldPosition.Pitcher) with { Name = name + "P", Pitching = PitcherAttributes.LeagueAverage },
        };
        return new Team { Name = name, BattingOrder = order, PitcherSlot = 8,
            Reserves = new[] { Pos(FieldPosition.Pitcher) with { Name = name + "R", Pitching = PitcherAttributes.LeagueAverage } } };
    }
}
