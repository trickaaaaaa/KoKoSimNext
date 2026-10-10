using KokoSim.Engine.Match.Field;
using KokoSim.Engine.Match.Fielding;
using KokoSim.Engine.Match.Game;
using KokoSim.Engine.Players;
using Xunit;

namespace KokoSim.Engine.Tests.Match.Game;

/// <summary>
/// 走者の時計（<see cref="RunnerClock"/>, Issue #316）。
///
/// Phase A の要求は「3つの式を1本に畳んでも**数値が1ビットも変わらない**」こと。
/// 走者所要時間は本塁レースの logistic と塁打数のしきい値比較に直接入るので、
/// 丸め1つで判定が反転し帯が動く（不変条件#2 決定論 / #5 統計回帰）。
/// ここが緑なら、表示側を同じ関数へ載せ替えても判定は一切動かない。
/// </summary>
public sealed class RunnerClockTests
{
    private static readonly FieldGeometry Field = new();

    private static Player RunnerWith(int speed, int baserunning)
        => new()
        {
            Name = "走者", Bats = Handedness.Right, Throws = Handedness.Right,
            Speed = speed, Baserunning = baserunning,
        };

    // ── 塁上から（本塁/三塁レース）: HomePlayResolver とビット一致 ──

    [Theory]
    [InlineData(3, 4)]   // 三塁→本塁
    [InlineData(2, 4)]   // 二塁→本塁
    [InlineData(1, 4)]   // 一塁→本塁
    [InlineData(1, 3)]   // 一塁→三塁（Issue #89）
    [InlineData(2, 3)]   // 二塁→三塁
    public void FromBase_ReproducesHomePlayResolver_BitExact(int fromBase, int toBase)
    {
        // #316 以前の物理値（加速なし・回る損失なし）に固定して比較する。畳み込み自体が数値を
        // 変えていないことの担保なので、Phase B で既定値を動かしてもこの保証は残す。
        var c = new BaserunningCoefficients { RunnerTopSpeedFactor = 1.0, RunnerBaseTurnSeconds = 0.0 };
        foreach (var speed in new[] { 1, 25, 50, 75, 99 })
        foreach (var baserunning in new[] { 1, 30, 50, 80, 99 })
        foreach (var extraDelay in new[] { 0.0, 0.12, 0.45 })
        {
            var runner = RunnerWith(speed, baserunning);
            // #316 以前の HomePlayResolver.RunnerTimeSeconds の式をそのまま書き下したもの。
            var startDelay = System.Math.Max(
                0.10, c.HomeRunnerReactionIntercept - c.AbilityEffect.Running(runner.Baserunning) * c.HomeRunnerReactionSlope);
            var distance = System.Math.Max(
                0.0, (toBase - fromBase) * Field.BaseDistanceM - c.HomeLeadDistanceM);
            var expected = distance / runner.ToFielder(c.AbilityEffect).SprintSpeedMps + startDelay + extraDelay;

            var actual = RunnerClock.SegmentSeconds(
                runner, c.AbilityEffect, fromBase, toBase, HomePlayResolver.RunnerContextOf(runner, c), Field, extraDelay);

            Assert.Equal(expected, actual);   // 許容差なし＝ビット一致
            // 委譲後の公開APIも同値（呼び手の載せ替えで判定が動かないことの担保）。
            Assert.Equal(
                expected, HomePlayResolver.RunnerTimeSeconds(runner, fromBase, toBase, Field, c, extraDelay));
        }
    }

    // ── 打席から（塁打数）: ExtraBaseResolver の perBase とビット一致 ──

    [Fact]
    public void FromBatterBox_ReproducesExtraBaseResolverPerBase_BitExact()
    {
        var c = new FieldingCoefficients();
        foreach (var speed in new[] { 1, 25, 50, 75, 99 })
        {
            var batter = new BatterAttributes { Speed = speed };
            // ExtraBaseResolver.ResolveBases の perBase（塁打数判定の1レッグぶん）。
            var sprint = batter.SpeedToFirstMps() * c.RunningTopSpeedFactor;
            var expected = Field.BaseDistanceM / sprint + c.BaseTurnSeconds;

            var actual = RunnerClock.SegmentSeconds(
                batter.SpeedToFirstMps(), 1, 2, ExtraBaseResolver.BatterContextOf(c), Field);

            Assert.Equal(expected, actual);   // 許容差なし＝ビット一致
        }
    }

    /// <summary>
    /// 打席からの文脈は走者の SpeedToFirstMps（打者の一塁到達速度）に載る。
    /// Player→BatterAttributes と Player→FielderAttributes で速度定義がズレていないことの確認。
    /// </summary>
    [Fact]
    public void FromBatterBox_UsesSameSprintSpeedAsBatterAttributes()
    {
        foreach (var speed in new[] { 1, 50, 99 })
        {
            var expected = new BatterAttributes { Speed = speed }.SpeedToFirstMps();
            var actual = RunnerWith(speed, 50).ToBatter().SpeedToFirstMps();
            Assert.Equal(expected, actual);
        }
    }

    /// <summary>
    /// ベースを回る損失を払う回数＝**目標塁の手前で回る塁の数**（Issue #316）。三塁→本塁は
    /// まっすぐ走るので0回、二塁→本塁は三塁を回るので1回。打者走者だけは計測区間の先頭で
    /// 一塁を全速で回るので1回多い。`塁数×損失` と書くと全員1回ぶん余計に遅くなる。
    /// </summary>
    [Theory]
    [InlineData(3, 4, 0)]   // 三塁→本塁: 回る塁なし
    [InlineData(2, 4, 1)]   // 二塁→本塁: 三塁を回る
    [InlineData(1, 4, 2)]   // 一塁→本塁: 二塁と三塁を回る
    [InlineData(1, 3, 1)]   // 一塁→三塁: 二塁を回る
    public void FromBase_PaysTurnLossOnlyForRoundedBases(int fromBase, int toBase, int expectedTurns)
    {
        var runner = RunnerWith(50, 50);
        var noTurn = new BaserunningCoefficients { RunnerBaseTurnSeconds = 0.0 };
        var withTurn = new BaserunningCoefficients { RunnerBaseTurnSeconds = 0.30 };

        var a = RunnerClock.SegmentSeconds(
            runner, noTurn.AbilityEffect, fromBase, toBase, HomePlayResolver.RunnerContextOf(runner, noTurn), Field);
        var b = RunnerClock.SegmentSeconds(
            runner, withTurn.AbilityEffect, fromBase, toBase, HomePlayResolver.RunnerContextOf(runner, withTurn), Field);

        Assert.Equal(expectedTurns * 0.30, b - a, 9);
    }

    /// <summary>打者走者は1塁ぶんの区間でも一塁を回るので必ず1回ぶん払う。</summary>
    [Fact]
    public void FromBatterBox_PaysOneTurnPerSegment()
    {
        var noTurn = new FieldingCoefficients { BaseTurnSeconds = 0.0 };
        var withTurn = new FieldingCoefficients { BaseTurnSeconds = 0.30 };
        var speed = new BatterAttributes { Speed = 50 }.SpeedToFirstMps();

        var a = RunnerClock.SegmentSeconds(speed, 1, 2, ExtraBaseResolver.BatterContextOf(noTurn), Field);
        var b = RunnerClock.SegmentSeconds(speed, 1, 2, ExtraBaseResolver.BatterContextOf(withTurn), Field);

        Assert.Equal(0.30, b - a, 9);
    }

    /// <summary>
    /// #316 Phase B の核心: 2つの文脈は**同じ物理**を使う。違ってよいのは「打席から/塁上から」に
    /// 由来する3点（スタート遅延・二次リード・出発塁を回るか）だけで、脚の速さの出方
    /// （加速ランプ・ベースを回る損失）は共通でなければならない。
    /// ここが割れると、また「用途ごとに別の走者」が生えたということ。
    /// </summary>
    [Fact]
    public void BothContexts_ShareTheSameRunningPhysics()
    {
        var onBase = HomePlayResolver.RunnerContextOf(RunnerWith(50, 50), new BaserunningCoefficients());
        var fromBox = ExtraBaseResolver.BatterContextOf(new FieldingCoefficients());

        Assert.Equal(fromBox.TopSpeedFactor, onBase.TopSpeedFactor, 9);
        Assert.Equal(fromBox.TurnSecondsPerBase, onBase.TurnSecondsPerBase, 9);

        // 文脈由来の違いは残る（打者は打席から走るのでリードも反応遅延も無く、一塁を回る）。
        Assert.Equal(0.0, fromBox.StartDelaySeconds, 9);
        Assert.True(onBase.StartDelaySeconds > 0.0);
        Assert.Equal(0.0, fromBox.LeadDistanceM, 9);
        Assert.True(onBase.LeadDistanceM > 0.0);
        Assert.True(fromBox.RoundsStartingBase);
        Assert.False(onBase.RoundsStartingBase);
    }

    /// <summary>
    /// #316 Phase B で塁上の走者がどれだけ速くなったか（校正バイアスの引き下げ幅の根拠）。
    /// この値が動いたら home_success_bias / third_success_bias の引き下げ量も見直しが要る。
    /// </summary>
    [Theory]
    [InlineData(3, 4, 0.43)]   // 三塁→本塁: home_success_bias 2.00→1.57 の根拠
    [InlineData(1, 3, 0.62)]   // 一塁→三塁: third_success_bias 1.50→0.88 の根拠
    public void PhaseB_SpeedUp_MatchesTheBiasReduction(int fromBase, int toBase, double expectedGain)
    {
        var runner = RunnerWith(50, 50);   // 走力50・走塁50（校正の基準個体）
        var before = new BaserunningCoefficients { RunnerTopSpeedFactor = 1.0, RunnerBaseTurnSeconds = 0.0 };
        var after = new BaserunningCoefficients();

        var tBefore = RunnerClock.SegmentSeconds(
            runner, before.AbilityEffect, fromBase, toBase, HomePlayResolver.RunnerContextOf(runner, before), Field);
        var tAfter = RunnerClock.SegmentSeconds(
            runner, after.AbilityEffect, fromBase, toBase, HomePlayResolver.RunnerContextOf(runner, after), Field);

        Assert.Equal(expectedGain, tBefore - tAfter, 2);
    }

    // ── 文脈の意味づけ（回帰しやすい定数の向きを固定する） ──

    /// <summary>塁上の走者は二次リードぶん距離が短い＝打席からより1塁あたり速い。</summary>
    [Fact]
    public void FromBase_IsFasterThanBatterBox_PerBase_ByTheLead()
    {
        var runner = RunnerWith(50, 50);
        var bc = new BaserunningCoefficients();
        var lead = RunnerClock.SegmentSeconds(runner, bc.AbilityEffect, 3, 4, HomePlayResolver.RunnerContextOf(runner, bc), Field);
        var box = RunnerClock.SegmentSeconds(runner, bc.AbilityEffect, 1, 2, ExtraBaseResolver.BatterContextOf(new FieldingCoefficients()), Field)
                  + bc.HomeRunnerReactionIntercept - 50 * bc.HomeRunnerReactionSlope;

        Assert.True(lead < box, $"塁上({lead:F3}s)が打席から({box:F3}s)より速くない");
    }

    /// <summary>走力が上がれば必ず速くなる（両文脈とも単調）。</summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void FasterRunner_AlwaysArrivesEarlier(bool fromBase)
    {
        var c = new BaserunningCoefficients();
        var prev = double.MaxValue;
        for (var speed = 1; speed <= 99; speed += 7)
        {
            var runner = RunnerWith(speed, 50);
            var p = fromBase ? HomePlayResolver.RunnerContextOf(runner, c) : ExtraBaseResolver.BatterContextOf(new FieldingCoefficients());
            var t = RunnerClock.SegmentSeconds(runner, c.AbilityEffect, 1, 4, p, Field);
            Assert.True(t < prev, $"走力{speed}で遅くなっている({t:F3}s ≧ {prev:F3}s)");
            prev = t;
        }
    }

    /// <summary>走塁判断が高いほどスタートが早い（塁上の文脈のみ）。</summary>
    [Fact]
    public void BetterBaserunning_StartsEarlier()
    {
        var c = new BaserunningCoefficients();
        var slow = RunnerWith(50, 10);
        var fast = RunnerWith(50, 90);
        var tSlow = RunnerClock.SegmentSeconds(slow, c.AbilityEffect, 3, 4, HomePlayResolver.RunnerContextOf(slow, c), Field);
        var tFast = RunnerClock.SegmentSeconds(fast, c.AbilityEffect, 3, 4, HomePlayResolver.RunnerContextOf(fast, c), Field);

        Assert.True(tFast < tSlow, $"走塁判断が高い走者が速くない({tFast:F3}s ≧ {tSlow:F3}s)");
    }

    /// <summary>
    /// 反応遅延の下限0.10sは、現行の能力スケール（走塁0〜99）では**発火しない**保険。
    /// 走塁99 でも 0.55−99×0.004 = 0.154s 残る。切片・傾きを動かしたときに
    /// 「いつの間にか全員が下限に張り付いて走塁能力が効かなくなる」退行を検知する。
    /// </summary>
    [Fact]
    public void ReactionFloor_IsInactive_AcrossTheWholeAbilityScale()
    {
        // 効きは恒等で回す＝物理層が受け取りうる 0〜99 全域で下限が発火しないことの担保（既定の効き 0.55 では 99 が 77 に寝る）。
        var c = new BaserunningCoefficients { AbilityEffect = AbilityEffectCoefficients.Identity };
        for (var br = 0; br <= 99; br++)
        {
            var delay = HomePlayResolver.RunnerContextOf(RunnerWith(50, br), c).StartDelaySeconds;
            Assert.True(delay > 0.10,
                $"走塁{br} で反応遅延が下限に張り付いている（{delay:F3}s）＝能力差が消えている");
        }
        Assert.Equal(0.154, HomePlayResolver.RunnerContextOf(RunnerWith(50, 99), c).StartDelaySeconds, 6);
    }
}
