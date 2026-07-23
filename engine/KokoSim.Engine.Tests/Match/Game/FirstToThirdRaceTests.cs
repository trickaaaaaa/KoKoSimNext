using System;
using System.Linq;
using KokoSim.Engine.Core;
using KokoSim.Engine.Match.AtBat;
using KokoSim.Engine.Match.Field;
using KokoSim.Engine.Match.Game;
using KokoSim.Engine.Match.Tactics;
using KokoSim.Engine.Players;
using Xunit;

namespace KokoSim.Engine.Tests.Match.Game;

/// <summary>
/// 単打の一塁→三塁を固定確率テーブルから物理レースへ（Issue #89, 設計書12 §3.5/§4.6）。
/// 打球の深さ・方向（外野処理点）と外野の肩から三塁送球所要を組み、走者との秒勝負で解く。
/// home=null（純テーブル経路）は従来どおりで後方互換。
/// </summary>
public sealed class FirstToThirdRaceTests
{
    private static readonly BaserunningCoefficients C = new();
    private static readonly FieldGeometry Field = new();

    private static HomePlayContext Context(double x, double z, double fieldedAt, int arm,
        double aggression = 0.5)
    {
        var situation = new HomePlaySituation(
            new Vector3D(x, 0, z), fieldedAt, new Player { ArmStrength = arm }.ToFielder().ThrowSpeedMps);
        return new HomePlayContext(Field, situation, new TacticsCoefficients(), aggression,
            DefenseDepth.Normal, IsFly: false);
    }

    // ---- 塁ごとのレース係数（純関数・rng不使用）----

    [Fact]
    public void ThirdParams_TargetIsThirdBase_TwoBasesToRun()
    {
        // 三塁パラメータは送球先が三塁ベース、走者所要は一塁→三塁＝2塁ぶん。
        var third = HomePlayResolver.ThirdParams(Field, C);
        Assert.Equal(3, third.TargetBase);
        Assert.Equal(Field.ThirdBase.X, third.TargetPoint.X, 6);
        Assert.Equal(Field.ThirdBase.Z, third.TargetPoint.Z, 6);

        var runner = new Player { Speed = 50, Baserunning = 50 };
        var toThird = HomePlayResolver.RunnerTimeSeconds(runner, 1, 3, Field, C);
        var toHome = HomePlayResolver.RunnerTimeSeconds(runner, 1, 4, Field, C);
        // 一塁→三塁(2塁) は一塁→本塁(3塁) より短い。
        Assert.True(toThird < toHome);
        // 二次リードを差し引いた 2塁ぶんの距離 ÷ スプリント速度 ＋ スタート遅延 に一致。
        var expectedDist = (2 * Field.BaseDistanceM - C.HomeLeadDistanceM);
        Assert.Equal(expectedDist / runner.ToFielder().SprintSpeedMps
            + Math.Max(0.10, C.HomeRunnerReactionIntercept - 50 * C.HomeRunnerReactionSlope), toThird, 6);
    }

    [Fact]
    public void HomeParams_Unchanged_MatchLegacyHomeMath()
    {
        // 本塁レースは HomeTagSeconds / HomeSuccessBias / HomeMarginScale をそのまま使う＝挙動据え置き。
        var runner = new Player { Speed = 55, Baserunning = 60 };
        var s = new HomePlaySituation(new Vector3D(0, 0, 40), 2.0,
            new Player { ArmStrength = 55 }.ToFielder().ThrowSpeedMps);
        var viaParams = HomePlayResolver.SuccessProbability(runner, 3, s, Field, C, HomePlayResolver.HomeParams(C));
        var viaLegacy = HomePlayResolver.SuccessProbability(runner, 3, s, Field, C); // 後方互換オーバーロード
        Assert.Equal(viaLegacy, viaParams, 12);
    }

    [Fact]
    public void ThirdReach_DeepWeakArm_HigherThan_ShallowStrongArm()
    {
        // 打球の深さ・方向・外野の肩が効く（Issue #89 の主眼）。
        var runner = new Player { Speed = 50, Baserunning = 50 };
        var third = HomePlayResolver.ThirdParams(Field, C);

        // 浅いレフト前＋強肩の右翼手相当（送球短・速い）＝到達しづらい。
        var shallowStrong = Context(x: -20, z: 30, fieldedAt: 1.6, arm: 95);
        var pHard = HomePlayResolver.SuccessProbability(runner, 1, shallowStrong.Situation, Field, C, third);

        // 深いライト線を破る当たり＋鈍足右翼手（送球長・遅い）＝到達しやすい。
        var deepWeak = Context(x: 40, z: 78, fieldedAt: 3.6, arm: 20);
        var pEasy = HomePlayResolver.SuccessProbability(runner, 1, deepWeak.Situation, Field, C, third);

        Assert.True(pEasy > pHard, $"深いライト前(={pEasy:F3})が浅いレフト前(={pHard:F3})より到達しやすいはず");
        Assert.True(pHard < 0.30, $"浅い当たり＋強肩は到達率が低いはず（{pHard:F3}）");
        Assert.True(pEasy > 0.70, $"深い当たり＋鈍足肩は到達率が高いはず（{pEasy:F3}）");
    }

    // ---- 進塁解決への配線（rng 使用）----

    [Fact]
    public void Single_FirstRunner_Contested_ProducesReachAndThirdOut()
    {
        // 際どい深めのライト前を多数試行 → 三塁到達と三塁憤死の両方が出る。
        // 憤死は追加アウト（extraOuts）＋ (1→3, Out) レッグ＋ BaseOuts.Third に計上される。
        var home = Context(x: 34, z: 70, fieldedAt: 3.0, arm: 70);
        var rng = new Xoshiro256Random(9);
        int reached = 0, thirdOuts = 0, outMoves = 0, extra = 0;
        for (var i = 0; i < 4000; i++)
        {
            var bases = new BaseState { First = new Player { Speed = 45, Baserunning = 50 } };
            var (_, extraOuts, baseOuts, _, _, moves) = BaserunningModel.ApplyDetailed(
                bases, PlateAppearanceResult.Single, new Player(), 0, C, rng, collectMoves: true, home);
            reached += moves.Count(m => m is { FromBase: 1, ToBase: 3, Out: false });
            outMoves += moves.Count(m => m is { FromBase: 1, ToBase: 3, Out: true });
            thirdOuts += baseOuts.Third;
            extra += extraOuts;
        }
        Assert.True(reached > 0, "一塁→三塁の到達が一度も出ない（送り判定＋レースが機能していない）");
        Assert.True(thirdOuts > 0, "三塁での走塁死が一度も出ない");
        Assert.Equal(thirdOuts, outMoves);   // 三塁憤死は (1→3, Out) レッグと一致
        Assert.Equal(thirdOuts, extra);      // この設定では追加アウトは三塁憤死のみ（走者は一塁のみ）
    }

    [Fact]
    public void Single_FirstRunner_ShallowCannonArm_HoldsAtSecond_NoRngConsumed()
    {
        // 浅い中前＋大砲肩＝三塁は無謀＝自重（二塁止まり）。本塁と同じ流儀で RNG を消費しない（決定論）。
        var home = Context(x: 0, z: 12, fieldedAt: 1.0, arm: 99);

        var rng = new Xoshiro256Random(123);
        for (var i = 0; i < 50; i++)
        {
            var bases = new BaseState { First = new Player { Speed = 50, Baserunning = 50 } };
            var (_, extraOuts, baseOuts, _, _, _) = BaserunningModel.ApplyDetailed(
                bases, PlateAppearanceResult.Single, new Player(), 0, C, rng, collectMoves: false, home);
            Assert.Equal(0, extraOuts);            // 自重＝アウトなし
            Assert.Equal(0, baseOuts.Third);
            Assert.NotNull(bases.Second);          // 一塁走者は二塁止まり
            Assert.Null(bases.Third);
        }
        // 上の 50 打席が RNG を1つも消費していなければ、次の draw は真新しい rng の先頭と一致する。
        var fresh = new Xoshiro256Random(123);
        Assert.Equal(fresh.NextDouble(), rng.NextDouble(), 15);
    }

    [Fact]
    public void Single_FirstRunner_ThirdBlockedByHeldLeadRunner_GoesToSecond()
    {
        // 二塁走者が本塁を自重して三塁で止まる（浅い当たり）と、一塁走者は三塁へ行けず二塁止まり。
        var home = Context(x: 0, z: 14, fieldedAt: 1.0, arm: 90);
        var rng = new Xoshiro256Random(7);
        var r1 = new Player { Speed = 50, Baserunning = 50 };
        var r2 = new Player { Speed = 50, Baserunning = 50 };
        var bases = new BaseState { First = r1, Second = r2 };
        var (runs, extraOuts, baseOuts, _, _, _) = BaserunningModel.ApplyDetailed(
            bases, PlateAppearanceResult.Single, new Player(), 0, C, rng, collectMoves: false, home);
        Assert.Equal(0, runs);                  // 二塁走者は生還せず三塁で自重
        Assert.Equal(0, extraOuts);
        Assert.Equal(0, baseOuts.Third);
        Assert.Same(r2, bases.Third);           // 先行走者が三塁を占有
        Assert.Same(r1, bases.Second);          // 一塁走者は三塁が塞がって二塁止まり
    }

    [Fact]
    public void Single_AtMostOneOutPerPlay_ThrowIsSingleTarget()
    {
        // 1プレーで送球先は1箇所（1球は2箇所へ投げられない）＝追加アウトは最大1。本塁と三塁の憤死は
        // 排他（片方でも刺したらもう片方は無血）。二・一塁の際どい単打を多数試行して不変条件を確認する。
        var home = Context(x: 30, z: 66, fieldedAt: 2.8, arm: 75, aggression: 1.0);
        var rng = new Xoshiro256Random(3);
        int totalOuts = 0;
        for (var i = 0; i < 4000; i++)
        {
            var bases = new BaseState
            {
                First = new Player { Speed = 45, Baserunning = 50 },
                Second = new Player { Speed = 45, Baserunning = 50 },
            };
            var (_, extraOuts, baseOuts, _, _, _) = BaserunningModel.ApplyDetailed(
                bases, PlateAppearanceResult.Single, new Player(), 0, C, rng, collectMoves: false, home);
            Assert.True(extraOuts <= 1, "1プレーで2アウト以上は発生しない（送球先は1箇所）");
            Assert.Equal(extraOuts, baseOuts.Home + baseOuts.Third); // 追加アウトの内訳は本塁＋三塁で過不足なし
            Assert.False(baseOuts.Home > 0 && baseOuts.Third > 0, "本塁と三塁を同一プレーで同時に刺すことはない");
            totalOuts += extraOuts;
        }
        Assert.True(totalOuts > 0, "際どい単打で走塁死が一度も出ない（レースが機能していない）");
    }

    [Fact]
    public void NullContext_FirstRunner_MatchesLegacyTable_Deterministic()
    {
        // 回帰: home=null は従来テーブル（first_to_third_on_single）で決定論・後方互換。
        var a = RunTableFirstToThird(seed: 42);
        var b = RunTableFirstToThird(seed: 42);
        Assert.Equal(a, b);
        Assert.InRange(a, 1200, 1650); // 5000試行の三塁到達数（基準28%＋走塁判断補正の目安、実測≈29%）
    }

    private static int RunTableFirstToThird(ulong seed)
    {
        var rng = new Xoshiro256Random(seed);
        var toThird = 0;
        for (var i = 0; i < 5000; i++)
        {
            var bases = new BaseState { First = new Player { Speed = 50, Baserunning = 50 } };
            BaserunningModel.ApplyDetailed(
                bases, PlateAppearanceResult.Single, new Player(), 0, C, rng, collectMoves: false, home: null);
            if (bases.Third is not null) toThird++;
        }
        return toThird;
    }
}
