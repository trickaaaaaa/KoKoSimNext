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
/// 併殺・野選を 13%固定テーブル（DoublePlayProb）から物理レースへ（Issue #369, 設計書12 §2）。
/// 守備は幾何で二段（捕球点→二塁の封殺 P_force2／二塁→一塁の転送で打者を刺せるか P_relay_out）を純関数で解き、
/// 3分岐を選ぶ:
/// A) 二塁封殺不可（俊足走者/深い右方向ゴロ）または併殺の見込み薄 → 一塁のみ（打者アウト・一塁走者は二塁へ, RNG非消費）／
/// B) 併殺を狙ったが転送が打者に僅かに間に合わず → 野選（二塁封殺＋打者一塁生存）／
/// C) 二塁も一塁も取れる → 併殺。
/// 適用は「内野ゴロ×一塁走者×打者一塁到達時刻あり（home!=null）」の実試合経路のみ。それ以外は従来テーブル。
/// 方向性・深さ・打者/走者の脚がすべて効くこと（幾何からの創発）を担保する。
/// </summary>
public sealed class DoublePlayRaceTests
{
    private static readonly BaserunningCoefficients C = new();
    private static readonly FieldGeometry Field = new();

    // 内野ゴロ（捕球点・処理時刻・肩・打者一塁到達時刻を指定）。BatterToFirstSeconds を渡すと物理レースが効く。
    private static HomePlayContext Grounder(
        double x, double z, double fieldedAt, double batterToFirst, int arm = 55,
        FieldPosition? fielderRole = null, DefenseDepth depth = DefenseDepth.Normal)
    {
        var situation = new HomePlaySituation(
            new Vector3D(x, 0, z), fieldedAt, new Player { ArmStrength = arm }.ToFielder().ThrowSpeedMps);
        return new HomePlayContext(
            Field, situation, new TacticsCoefficients(), 0.5, depth, IsFly: false,
            ThrowArriveSeconds: null, BatterToFirstSeconds: batterToFirst,
            FielderRole: fielderRole);
    }

    /// <summary>一塁走者が二塁へ到達する見込み（P_r1_safe）＝分岐A-1（封殺不可）かどうかを決める純関数の量。</summary>
    private static double PR1Safe(HomePlayContext home, Player r1)
        => HomePlayResolver.SuccessProbability(
            r1, 1, home.Situation, home.Field, C, HomePlayResolver.SecondForceParams(home.Field, C),
            C.DoublePlayR1StartDelaySeconds);

    /// <summary>二塁封殺後、打者が二塁経由の転送を上回って一塁生存する見込み（＝野選になる確率）。
    /// <paramref name="pivotArmMps"/> は中継球速の直接指定（issue⑦・既定null＝従来の RelayThrowSpeedMps 定数）。</summary>
    private static double PBatterSafe(HomePlayContext home, double? pivotArmMps = null)
    {
        var arriveAtSecond = HomePlayResolver.ResolveThrowPath(home.Situation, Field.SecondBase, C).ArriveAtSeconds;
        var pivotTransfer = C.DoublePlayPivotTransferSeconds;
        var pivot = new HomePlaySituation(
            Field.SecondBase, arriveAtSecond, pivotArmMps ?? C.RelayThrowSpeedMps, home.Situation.OutfielderFielding);
        var relayToFirst = HomePlayResolver
            .ResolveThrowPath(pivot, Field.FirstBase, C, transferSecondsOverride: pivotTransfer)
            .ArriveAtSeconds + C.SecondForceTagSeconds;
        var margin = relayToFirst - home.BatterToFirstSeconds!.Value + C.DoublePlayRelayBias;
        return MathUtil.Clamp(MathUtil.Logistic(margin / C.DoublePlayRelayMarginScale), 0.01, 0.99);
    }

    private static int DpCount(HomePlayContext home, ulong seed, int trials = 3000)
    {
        var rng = new Xoshiro256Random(seed);
        var dp = 0;
        for (var i = 0; i < trials; i++)
        {
            var (_, _, batterSafeOnFc, moves) = RunOnce(home, new Player { Speed = 50, Baserunning = 50 }, new Player(), rng);
            if (!batterSafeOnFc && ForceOutAtSecond(moves)) dp++;
        }
        return dp;
    }

    // 1死一塁の内野ゴロを1回解く（打者アウトのカウントは呼び出し側の battersOwnOut なので extraOuts には含まれない）。
    private static (BaseState Bases, int ExtraOuts, bool BatterSafeOnFc, System.Collections.Generic.IReadOnlyList<RunnerMove> Moves) RunOnce(
        HomePlayContext home, Player r1, Player batter, IRandomSource rng, int currentOuts = 1,
        Player? r2 = null, Player? r3 = null, BaserunningCoefficients? coeff = null)
    {
        var bases = new BaseState { First = r1, Second = r2, Third = r3 };
        var (_, extraOuts, _, batterSafeOnFc, _, moves) = BaserunningModel.ApplyDetailed(
            bases, PlateAppearanceResult.InPlayOut, batter, currentOuts, coeff ?? C, rng, collectMoves: true, home);
        return (bases, extraOuts, batterSafeOnFc, moves);
    }

    // 満塁（一・二・三塁）の内野ゴロを1回解く。BaseOuts.Home も返す（Q31＝本塁自己フォースの統計参考値）。
    private static (BaseState Bases, int ExtraOuts, int HomeOuts, bool BatterSafeOnFc,
        System.Collections.Generic.IReadOnlyList<RunnerMove> Moves) RunOnceLoaded(
        HomePlayContext home, Player r1, Player r2, Player r3, Player batter, IRandomSource rng, int currentOuts = 1)
    {
        var bases = new BaseState { First = r1, Second = r2, Third = r3 };
        var (_, extraOuts, baseOuts, batterSafeOnFc, _, moves) = BaserunningModel.ApplyDetailed(
            bases, PlateAppearanceResult.InPlayOut, batter, currentOuts, C, rng, collectMoves: true, home);
        return (bases, extraOuts, baseOuts.Home, batterSafeOnFc, moves);
    }

    private static bool ForceOutAtSecond(System.Collections.Generic.IReadOnlyList<RunnerMove> moves)
        => moves.Any(m => m is { FromBase: 1, ToBase: 2, Out: true });

    private static bool SelfForceOutAt(System.Collections.Generic.IReadOnlyList<RunnerMove> moves, int toBase)
        => moves.Any(m => m.ToBase == toBase && m.Out && m.SelfForceDoublePlayLead);

    /// <summary>標準の二塁封殺（feed+pivotタッチ）の RunnerMove か。二塁の自己フォース（issue #1549）は
    /// 同じ FromBase=1/ToBase=2/Force=true の形を取る＝<see cref="ForceOutAtSecond"/>（構造だけの判定）
    /// では区別できないため、SelfForceDoublePlayLead を除外して区別する（GameEngine の分岐と同じ判別式）。</summary>
    private static bool StandardForceOutAtSecond(System.Collections.Generic.IReadOnlyList<RunnerMove> moves)
        => moves.Any(m => m is { FromBase: 1, ToBase: 2, Out: true, Force: true } && !m.SelfForceDoublePlayLead);

    // ================= 係数束（純関数・rng不使用）=================

    [Fact]
    public void SecondForceParams_TargetIsSecondBase_UsesSecondForceCoefficients()
    {
        var p = HomePlayResolver.SecondForceParams(Field, C);
        Assert.Equal(2, p.TargetBase);
        Assert.Equal(Field.SecondBase.X, p.TargetPoint.X, 6);
        Assert.Equal(Field.SecondBase.Z, p.TargetPoint.Z, 6);
        Assert.Equal(C.SecondForceTagSeconds, p.TagSeconds, 12);
        Assert.Equal(C.SecondForceSuccessBias, p.SuccessBias, 12);
        Assert.Equal(C.SecondForceMarginScale, p.MarginScale, 12);
    }

    // ================= 幾何・脚からの創発（純関数の順序性）=================

    [Fact]
    public void Order_DeeperFieldingPoint_RaisesRunnerSafeAtSecond()
    {
        // 捕球点を二塁から遠く（深く）＋処理を遅くするほど、二塁封殺が間に合いにくく（P_r1_safe が上がる）＝封殺不可へ。
        var r1 = new Player { Speed = 50, Baserunning = 50 };
        var probs = new System.Collections.Generic.List<double>();
        foreach (var (x, z, at) in new[] { (-6.0, 37.0, 1.4), (-12.0, 32.0, 1.7), (-18.0, 27.0, 2.0), (-24.0, 22.0, 2.4) })
            probs.Add(PR1Safe(Grounder(x, z, at, 4.2), r1));
        for (var i = 1; i < probs.Count; i++)
            Assert.True(probs[i] > probs[i - 1], $"深いほど封殺不可に近づくはず: {string.Join(",", probs.Select(p => p.ToString("F3")))}");
    }

    [Fact]
    public void Order_FasterBatter_RaisesBatterSafeAtFirst()
    {
        // 打者走者が速い（一塁到達が早い）ほど、二塁経由の転送を上回って一塁生存する見込み（野選）が上がる。
        var slow = PBatterSafe(Grounder(-6, 37, 1.4, batterToFirst: 4.60));
        var fast = PBatterSafe(Grounder(-6, 37, 1.4, batterToFirst: 3.60));
        Assert.True(fast > slow + 0.3, $"快足打者(={fast:F3})は鈍足打者(={slow:F3})より一塁生存しやすいはず");
    }

    // ================= 分岐 C（併殺）: 二遊間の浅いゴロ＝二塁が近い＝封殺容易＋転送短 =================

    [Fact]
    public void NearSecond_SlowBatter_ProducesDoublePlay()
    {
        // 二遊間の浅いゴロ（捕球点が二塁に近い）＋鈍足打者 ⇒ 二塁封殺成立かつ転送が打者に間に合う＝併殺。
        var home = Grounder(x: -6, z: 37, fieldedAt: 1.4, batterToFirst: 4.60);
        var r1 = new Player { Speed = 50, Baserunning = 50 };
        Assert.True(PR1Safe(home, r1) < C.DoublePlayForceMaxRunnerSuccess, "二塁封殺は間に合う域のはず");
        Assert.True(1.0 - PBatterSafe(home) >= C.DoublePlayAttemptMinRelaySuccess, "併殺を狙う域（転送で刺せる見込み高）のはず");
        Assert.True(PBatterSafe(home) < 0.10, "鈍足打者は転送で刺される域（併殺）のはず");

        var rng = new Xoshiro256Random(101);
        int dp = 0, fc = 0;
        for (var i = 0; i < 3000; i++)
        {
            var (bases, extraOuts, batterSafeOnFc, moves) = RunOnce(home, new Player { Speed = 50, Baserunning = 50 }, new Player(), rng);
            Assert.Equal(1, extraOuts);           // 二塁封殺（一塁走者アウト）は常に成立
            Assert.True(ForceOutAtSecond(moves)); // 6-4-3/4-6-3 型の二塁封殺 RunnerMove
            if (batterSafeOnFc) { fc++; } else { dp++; Assert.Null(bases.First); } // 併殺は一塁空
        }
        Assert.True(dp > fc * 5, $"二遊間の浅いゴロ＋鈍足打者は併殺が支配的なはず（DP={dp}, FC={fc}）");
    }

    // ================= 分岐 A-1（一塁のみ・封殺不可）: 三遊間の深いゴロ＝二塁が遠い =================

    [Fact]
    public void DeepHole_ForceNotMakeable_TakesBatterAtFirst_NoRngConsumed()
    {
        // 三遊間の深いゴロ（捕球点が二塁から遠い＋処理が遅い）＝二塁封殺が間に合わない＝守備は一塁を取る。
        // 打者アウト（battersOwnOut）＋一塁走者はフォースで二塁へ。二塁封殺は起きず RNG も消費しない（不変条件#2）。
        var home = Grounder(x: -24, z: 22, fieldedAt: 2.4, batterToFirst: 4.20, arm: 55);
        var r1 = new Player { Speed = 50, Baserunning = 50 };
        Assert.True(PR1Safe(home, r1) > C.DoublePlayForceMaxRunnerSuccess, "この捕球点は封殺不可域（P_r1_safe > 閾値）のはず");

        var rng = new Xoshiro256Random(202);
        for (var i = 0; i < 50; i++)
        {
            var r = new Player { Speed = 50, Baserunning = 50 };
            var (bases, extraOuts, batterSafeOnFc, moves) = RunOnce(home, r, new Player(), rng);
            Assert.Equal(0, extraOuts);            // 二塁封殺は成立しない（分岐A）
            Assert.False(batterSafeOnFc);          // 打者は一塁アウト（野選でない）
            Assert.False(ForceOutAtSecond(moves)); // 二塁封殺 RunnerMove は出ない
            Assert.Same(r, bases.Second);          // 一塁走者はフォースで二塁へ生存
            Assert.Null(bases.First);
        }
        var fresh = new Xoshiro256Random(202);
        Assert.Equal(fresh.NextDouble(), rng.NextDouble(), 15); // 分岐A は RNG 非消費
    }

    // ================= 分岐 A-2（一塁のみ・併殺の見込み薄）: 転送が届かない＝守備は確実な一塁アウト =================

    [Fact]
    public void SlowRelay_DoublePlayNotWorthIt_TakesBatterAtFirst_NoRngConsumed()
    {
        // 中深度のゴロ: 二塁封殺自体は間に合うが、二塁→一塁の転送が打者に届く見込みが薄い（深くて転送が長い）。
        // ＝守備は併殺を狙わず「確実な一塁アウト」を取る（ルーティンなゴロ凡打＝打者一塁アウト＋一塁走者は二塁へ）。
        var home = Grounder(x: -15, z: 28, fieldedAt: 1.9, batterToFirst: 4.20, arm: 55);
        var r1 = new Player { Speed = 50, Baserunning = 50 };
        Assert.True(PR1Safe(home, r1) < C.DoublePlayForceMaxRunnerSuccess, "二塁封殺自体は間に合う域のはず");
        Assert.True(1.0 - PBatterSafe(home) < C.DoublePlayAttemptMinRelaySuccess, "転送で刺せる見込みが薄い（併殺を狙わない）域のはず");

        var rng = new Xoshiro256Random(303);
        for (var i = 0; i < 50; i++)
        {
            var r = new Player { Speed = 50, Baserunning = 50 };
            var (bases, extraOuts, batterSafeOnFc, moves) = RunOnce(home, r, new Player(), rng);
            Assert.Equal(0, extraOuts);            // 併殺を狙わない＝二塁封殺は起きない
            Assert.False(batterSafeOnFc);          // 打者は一塁アウト
            Assert.False(ForceOutAtSecond(moves));
            Assert.Same(r, bases.Second);          // 一塁走者は二塁へ進塁
        }
        var fresh = new Xoshiro256Random(303);
        Assert.Equal(fresh.NextDouble(), rng.NextDouble(), 15); // 分岐A は RNG 非消費
    }

    // ================= 分岐 B（野選）: 併殺を狙ったが快足打者が転送を僅かに上回る =================

    [Fact]
    public void NearSecond_MarginalBatter_ProducesFieldersChoice()
    {
        // 二遊間の浅いゴロ（併殺を狙う域）だが打者が際どい速さ＝転送が僅かに間に合わず一塁生存＝野選が混じる。
        var home = Grounder(x: -6, z: 37, fieldedAt: 1.4, batterToFirst: 4.40);
        var r1 = new Player { Speed = 50, Baserunning = 50 };
        Assert.True(PR1Safe(home, r1) < C.DoublePlayForceMaxRunnerSuccess, "二塁封殺は間に合う域のはず");
        Assert.True(1.0 - PBatterSafe(home) >= C.DoublePlayAttemptMinRelaySuccess, "併殺を狙う域のはず（＝野選は残差として出る）");
        Assert.InRange(PBatterSafe(home), 0.10, 0.45); // 野選が残差として一定割合出る帯

        var rng = new Xoshiro256Random(404);
        int fc = 0, dp = 0;
        var batter = new Player();
        for (var i = 0; i < 3000; i++)
        {
            var (bases, extraOuts, batterSafeOnFc, moves) = RunOnce(home, new Player { Speed = 50, Baserunning = 50 }, batter, rng);
            Assert.Equal(1, extraOuts);            // 併殺狙い＝二塁封殺は常に成立
            Assert.True(ForceOutAtSecond(moves));
            if (batterSafeOnFc) { fc++; Assert.Same(batter, bases.First); } else { dp++; }
        }
        Assert.True(fc > 0 && dp > 0, $"併殺狙いのうち野選が残差として出るはず（DP={dp}, FC={fc}）");
        Assert.InRange((double)fc / 3000, 0.10, 0.45);
    }

    // ================= 中継の地肩（issue⑦: 従来 RelayThrowSpeedMps 固定で二塁手/遊撃手の肩が無効だった）=================

    [Fact]
    public void Order_StrongerPivotArm_LowersBatterSafeAtFirst()
    {
        // 中継球速が強いほど二塁→一塁の転送が速くなり、打者が転送を上回って一塁生存する（野選）見込みは下がるはず。
        var home = Grounder(x: -6, z: 37, fieldedAt: 1.4, batterToFirst: 4.45);
        var weak = PBatterSafe(home, pivotArmMps: 25.0);
        var strong = PBatterSafe(home, pivotArmMps: 35.0);
        Assert.True(strong < weak, $"肩が強い中継のほうが打者生存(野選)しにくいはず: strong={strong:F3} < weak={weak:F3}");
    }

    [Fact]
    public void PivotArm_FlowsFromHomePlayContext_ChangesDoublePlayRate()
    {
        // HomePlayContext.DoublePlayPivotArmMps → BaserunningModel.ResolveDoublePlay の実経路で、
        // 中継の肩の強さが実際に併殺の成立率を動かすことを末端（RunOnce）まで通して確認する。
        var baseHome = Grounder(x: -6, z: 37, fieldedAt: 1.4, batterToFirst: 4.45);
        var weakHome = baseHome with { DoublePlayPivotArmMps = 25.0 };
        var strongHome = baseHome with { DoublePlayPivotArmMps = 35.0 };
        Assert.True(DpCount(strongHome, 707) > DpCount(weakHome, 707), "中継の肩が強いほうが併殺成立が多いはず");
    }

    [Fact]
    public void PivotArm_Null_FallsBackToRelayThrowSpeedMpsConstant_ExactMatch()
    {
        // DoublePlayPivotArmMps 未指定(null)は従来どおり RelayThrowSpeedMps 定数へフォールバックする
        // （帯保存の根拠＝明示的に同じ定数を渡した場合と完全一致するはず）。
        var withoutArm = Grounder(x: -6, z: 37, fieldedAt: 1.4, batterToFirst: 4.45); // DoublePlayPivotArmMps 未指定
        var withConstantArm = withoutArm with { DoublePlayPivotArmMps = C.RelayThrowSpeedMps };
        Assert.Equal(DpCount(withoutArm, 808), DpCount(withConstantArm, 808));
    }

    // ================= 併殺の代替ルート（Q31: 三塁/本塁の自己フォース）=================
    // 実際にその塁を処理した野手（FielderRole）だけが代替を検討でき、対象走者の到達見込みが
    // 二塁封殺より低い（守備に有利）ときだけ選ばれる。ゲート不成立の大多数のケースは
    // 上のテスト（一塁走者のみ・FielderRole未指定）が既に固定している＝完全不変。

    /// <summary>自己フォース一段目（処理野手自身が対象塁へ走り込む）の到達時刻[s]（issue #1273）。
    /// 送球でなく自分の脚（<see cref="BaserunningCoefficients.SelfForceFielderSprintSpeedMps"/>）で運ぶ＝
    /// BaserunningModel.SelfForceArriveAtBaseSeconds（private）と同じ式。</summary>
    private static double SelfForceArriveAtBase(HomePlayContext home, Vector3D targetPoint)
    {
        var sprintSpeedMps = home.SelfForceSprintSpeedMps ?? C.SelfForceFielderSprintSpeedMps;
        return home.Situation.BallFieldedAtSeconds + (home.Situation.BallFieldedPoint - targetPoint).Length / sprintSpeedMps;
    }

    private static double PLeadSafe(
        HomePlayContext home, Player runner, int fromBase, HomePlayResolver.BasePlayParams force)
    {
        var atBase = home.Situation with
        {
            BallFieldedPoint = force.TargetPoint, BallFieldedAtSeconds = SelfForceArriveAtBase(home, force.TargetPoint),
        };
        return HomePlayResolver.SuccessProbability(
            runner, fromBase, atBase, home.Field, C, force, C.DoublePlayR1StartDelaySeconds,
            transferSecondsOverride: 0.0);
    }

    [Fact]
    public void ThirdForceParams_TargetIsThirdBase_UsesThirdForceCoefficients()
    {
        var p = HomePlayResolver.ThirdForceParams(Field, C);
        Assert.Equal(3, p.TargetBase);
        Assert.Equal(Field.ThirdBase.X, p.TargetPoint.X, 6);
        Assert.Equal(Field.ThirdBase.Z, p.TargetPoint.Z, 6);
        Assert.Equal(C.ThirdForceTagSeconds, p.TagSeconds, 12);
        Assert.Equal(C.ThirdForceSuccessBias, p.SuccessBias, 12);
        Assert.Equal(C.ThirdForceMarginScale, p.MarginScale, 12);
    }

    [Fact]
    public void HomeForceParams_TargetIsHome_UsesHomeForceCoefficients()
    {
        var p = HomePlayResolver.HomeForceParams(C);
        Assert.Equal(4, p.TargetBase);
        Assert.Equal(0.0, p.TargetPoint.X, 6);
        Assert.Equal(0.0, p.TargetPoint.Z, 6);
        Assert.Equal(C.HomeForceTagSeconds, p.TagSeconds, 12);
        Assert.Equal(C.HomeForceSuccessBias, p.SuccessBias, 12);
        Assert.Equal(C.HomeForceMarginScale, p.MarginScale, 12);
    }

    [Fact]
    public void SelfForceThird_FielderAtThirdBase_ComebackerWins_ForcesR2AtThirdInstead()
    {
        // 三塁手の目の前（自打球点=三塁ベースそのもの）＝三塁の自己フォースが二塁封殺より圧倒的に有利。
        var home = Grounder(
            x: Field.ThirdBase.X, z: Field.ThirdBase.Z, fieldedAt: 1.3, batterToFirst: 4.60,
            fielderRole: FieldPosition.ThirdBase);
        var r2 = new Player { Speed = 50, Baserunning = 50 };
        var pR1Safe2nd = PR1Safe(home, new Player { Speed = 50, Baserunning = 50 });
        var pR2Safe3rd = PLeadSafe(home, r2, fromBase: 2, HomePlayResolver.ThirdForceParams(Field, C));
        Assert.True(pR2Safe3rd < pR1Safe2nd,
            $"三塁の自己フォースが二塁封殺より有利なはず: third={pR2Safe3rd:F4} < second={pR1Safe2nd:F4}");

        var rng = new Xoshiro256Random(909);
        int dp = 0, fc = 0;
        for (var i = 0; i < 3000; i++)
        {
            var r1 = new Player { Speed = 50, Baserunning = 50 };
            // currentOuts=0: 併殺(DP)側でも「対象走者アウト＋打者アウト」の2アウトで3アウト目に達しない
            // ようにする＝一塁走者(r1)の押し出しカスケードが最後まで走ることを検証できる。
            var (bases, extraOuts, batterSafeOnFc, moves) = RunOnce(home, r1, new Player(), rng, currentOuts: 0, r2: r2);
            Assert.Equal(1, extraOuts);
            Assert.True(SelfForceOutAt(moves, toBase: 3), "三塁での自己フォース RunnerMove が出るはず");
            Assert.False(ForceOutAtSecond(moves), "二塁封殺は起きないはず（代替が選ばれた）");
            Assert.Same(r1, bases.Second); // 二塁走者(r2)は三塁でアウト、一塁走者(r1)が代わりに二塁へ安全進塁
            if (batterSafeOnFc) fc++; else dp++;
        }
        Assert.True(dp + fc == 3000 && dp > 0, $"三塁代替でも併殺/野選が出るはず（DP={dp}, FC={fc}）");
    }

    [Fact]
    public void SelfForceThird_FartherFromBase_RaisesLeadRunnerSafety()
    {
        // issue #1273: 旧実装は自己フォース一段目（捕球点→三塁）を「送球」として送球速度(≈30m/s)で
        // 割っており、三塁手が三塁から離れても到達時刻がほぼ変わらなかった（野手が塁へ走る時間が
        // 入っていない）。修正後は三塁から離れるほど自分の脚（SprintSpeedMps≈7-8m/s）で走る時間が
        // 伸び、二塁走者の生存見込み（pLeadSafe）が実測で意味のある差になって上がる。
        var r2 = new Player { Speed = 50, Baserunning = 50 };
        var force3rd = HomePlayResolver.ThirdForceParams(Field, C);
        var probs = new System.Collections.Generic.List<double>();
        foreach (var offset in new[] { 0.0, 2.0, 4.0, 6.0, 8.0, 10.0 })
        {
            var home = Grounder(x: Field.ThirdBase.X + offset, z: Field.ThirdBase.Z, fieldedAt: 1.3, batterToFirst: 4.60,
                fielderRole: FieldPosition.ThirdBase);
            probs.Add(PLeadSafe(home, r2, fromBase: 2, force3rd));
        }
        for (var i = 1; i < probs.Count; i++)
            Assert.True(probs[i] > probs[i - 1],
                $"三塁から離れるほど二塁走者の生存見込みが上がるはず: {string.Join(",", probs.Select(p => p.ToString("F4")))}");
        // 旧実装（送球速度≈32.6m/sで割る）なら10m離れても到達時刻は10/32.6≈0.31s増にしかならず生存見込みは
        // 低いまま動かない。脚力(≈7.25m/s)で割れば10/7.25≈1.38s増＝帯を大きく動かすはず（旧実装は届かない域）。
        Assert.True(probs[5] > 0.10,
            $"10m離れた捕球点では生存見込みが意味のある域まで上がるはず（旧実装はここまで届かない）: {probs[5]:F4}");
    }

    [Fact]
    public void SelfForceThird_NearButOffThirdBase_RunnerSpeedAffectsForceRate()
    {
        // issue #1273 の再現: 三塁付近（ベースから5m、issueの実測「±6m」相当）で処理したゴロで、
        // 旧実装は二塁走者の走力を20/50/80と振っても三塁での自己フォース成立率がほぼ不変だった
        // （能力感度の穴）。修正後は走力が遅い走者ほど三塁で封殺されやすく、速い走者ほど免れる。
        int ForceRate(double offset, int speed)
        {
            var home = Grounder(x: Field.ThirdBase.X + offset, z: Field.ThirdBase.Z, fieldedAt: 1.3, batterToFirst: 4.60,
                fielderRole: FieldPosition.ThirdBase);
            var rng = new Xoshiro256Random(2026);
            var forced = 0;
            for (var i = 0; i < 2000; i++)
            {
                var r1 = new Player { Speed = 50, Baserunning = 50 };
                var r2 = new Player { Speed = speed, Baserunning = speed };
                var (_, _, _, moves) = RunOnce(home, r1, new Player(), rng, currentOuts: 0, r2: r2);
                if (SelfForceOutAt(moves, toBase: 3)) forced++;
            }
            return forced;
        }

        var offsets = new[] { 6.0, 7.0, 8.0, 9.0 };
        var slow = offsets.Sum(o => ForceRate(o, 1));
        var fast = offsets.Sum(o => ForceRate(o, 99));
        Assert.True(slow > fast,
            $"遅い走者(走力1)のほうが三塁で封殺されやすいはず（遅い={slow}, 速い={fast}, offsets={string.Join(",", offsets)}）");
    }

    [Fact]
    public void SelfForceThird_BallFarFromThirdBase_FallsBackToSecondForce()
    {
        // 三塁手の守備位置がゲート成立でも、捕球点が三塁から遠ければ（＝二塁封殺のほうが有利なら）
        // 代替を選ばず今日どおり二塁封殺へフォールバックする。自己フォースは「握り替えゼロ」の分だけ
        // 元々有利なので、二塁側が勝つには捕球点をほぼ二塁そのもの（三塁からは一塁間ぶん遠い）にする必要がある。
        var home = Grounder(
            x: Field.SecondBase.X, z: Field.SecondBase.Z, fieldedAt: 1.4, batterToFirst: 4.60,
            fielderRole: FieldPosition.ThirdBase);
        var r2 = new Player { Speed = 50, Baserunning = 50 };
        var pR1Safe2nd = PR1Safe(home, new Player { Speed = 50, Baserunning = 50 });
        var pR2Safe3rd = PLeadSafe(home, r2, fromBase: 2, HomePlayResolver.ThirdForceParams(Field, C));
        Assert.True(pR2Safe3rd > pR1Safe2nd, "この捕球点は二塁封殺のほうが有利なはず（三塁は遠い）");

        var rng = new Xoshiro256Random(910);
        for (var i = 0; i < 200; i++)
        {
            var r1 = new Player { Speed = 50, Baserunning = 50 };
            var (_, _, _, moves) = RunOnce(home, r1, new Player(), rng, r2: r2);
            Assert.False(SelfForceOutAt(moves, toBase: 3), "三塁代替は選ばれないはず");
        }
    }

    [Fact]
    public void SelfForceThird_RequiresR2Present_FallsBackWhenNoRunnerOnSecond()
    {
        // 三塁手が自打球でも、二塁に走者が居なければ代替の対象が無い＝今日どおり二塁封殺（一塁走者のみ）。
        var home = Grounder(
            x: Field.ThirdBase.X, z: Field.ThirdBase.Z, fieldedAt: 1.3, batterToFirst: 4.60,
            fielderRole: FieldPosition.ThirdBase);
        var rng = new Xoshiro256Random(911);
        for (var i = 0; i < 50; i++)
        {
            var r1 = new Player { Speed = 50, Baserunning = 50 };
            var (_, _, _, moves) = RunOnce(home, r1, new Player(), rng); // r2 なし
            Assert.False(SelfForceOutAt(moves, toBase: 3), "二塁走者が居ないので三塁代替は起こりえない");
        }
    }

    [Fact]
    public void SelfForceThird_RequiresExactFielderRoleMatch_FallsBackForOtherPositions()
    {
        // 同じ捕球点でも FielderRole が三塁手でなければ（例: 遊撃手）ゲート不成立＝代替を検討しない。
        var home = Grounder(
            x: Field.ThirdBase.X, z: Field.ThirdBase.Z, fieldedAt: 1.3, batterToFirst: 4.60,
            fielderRole: FieldPosition.Shortstop);
        var rng = new Xoshiro256Random(912);
        for (var i = 0; i < 50; i++)
        {
            var r1 = new Player { Speed = 50, Baserunning = 50 };
            var r2 = new Player { Speed = 50, Baserunning = 50 };
            var (_, _, _, moves) = RunOnce(home, r1, new Player(), rng, r2: r2);
            Assert.False(SelfForceOutAt(moves, toBase: 3), "遊撃手処理では三塁代替のゲートは成立しないはず");
        }
    }

    [Fact]
    public void SelfForceThird_RelayTooSlowForFastBatter_StepsOnThirdOnly_NoRngConsumed()
    {
        // 三塁の自己フォース自体は確実だが、一塁への転送が快足打者に間に合わない＝併殺は仕掛けない（分岐A-2）。
        // issue #797: それでも三塁は楽に踏める＝**三塁を踏むだけ**で二塁走者を封殺し、打者は一塁に残す（RNG非消費）。
        var home = Grounder(
            x: Field.ThirdBase.X, z: Field.ThirdBase.Z, fieldedAt: 1.3, batterToFirst: 2.8,
            fielderRole: FieldPosition.ThirdBase);
        var r2 = new Player { Speed = 50, Baserunning = 50 };
        var force3rd = HomePlayResolver.ThirdForceParams(Field, C);
        Assert.True(PLeadSafe(home, r2, 2, force3rd) <= C.DoublePlayForceOnlyMaxRunnerSuccess, "三塁の自己フォースは楽に間に合う域のはず");

        var rng = new Xoshiro256Random(913);
        for (var i = 0; i < 50; i++)
        {
            var r1 = new Player { Speed = 50, Baserunning = 50 };
            var batter = new Player();
            var (bases, extraOuts, batterSafeOnFc, moves) = RunOnce(home, r1, batter, rng, r2: r2);
            Assert.Equal(1, extraOuts);                          // 二塁走者を三塁で封殺
            Assert.True(batterSafeOnFc);                         // 打者は一塁に残る
            Assert.True(SelfForceOutAt(moves, toBase: 3));
            Assert.Contains(moves, m => m.ForceOnly && m.ToBase == 3);
            Assert.False(ForceOutAtSecond(moves));
            Assert.Same(batter, bases.First);
            Assert.Same(r1, bases.Second);                       // 一塁走者は押し出されて二塁へ
            Assert.Null(bases.Third);
        }
        var fresh = new Xoshiro256Random(913);
        Assert.Equal(fresh.NextDouble(), rng.NextDouble(), 15); // RNG非消費
    }

    [Fact]
    public void SelfForceThird_RelayTooSlow_WithForceOnlyOff_TakesFirstOnly()
    {
        // 新しい分岐を止める（閾値0）と従来どおり＝仕掛けず一塁だけ（回帰の対照）。
        var off = C with { DoublePlayForceOnlyMaxRunnerSuccess = 0.0 };
        var home = Grounder(
            x: Field.ThirdBase.X, z: Field.ThirdBase.Z, fieldedAt: 1.3, batterToFirst: 2.8,
            fielderRole: FieldPosition.ThirdBase);
        var rng = new Xoshiro256Random(913);
        var (_, extraOuts, batterSafeOnFc, moves) = RunOnce(home, new Player { Speed = 50, Baserunning = 50 }, new Player(), rng,
            r2: new Player { Speed = 50, Baserunning = 50 }, coeff: off);
        Assert.Equal(0, extraOuts);
        Assert.False(batterSafeOnFc);
        Assert.False(SelfForceOutAt(moves, toBase: 3));
    }

    // ================= 分岐 A-3（先の走者だけ封殺・issue #797）: 併殺は無理だが二塁封殺は楽 =================

    [Fact]
    public void NearSecond_VeryFastBatter_TakesLeadRunnerOnly_NoRngConsumed()
    {
        // 二遊間の浅いゴロ（二塁封殺は楽）だが、打者が快足で二塁→一塁の転送が間に合わない＝併殺は狙わない。
        // 旧実装はここで二塁を捨てて一塁へ投げ、一塁走者を二塁へ進めていた。いまは一塁走者を二塁で封殺し、
        // 打者を一塁に残す（転送しない＝野選ではない）。
        var home = Grounder(x: -6, z: 37, fieldedAt: 1.4, batterToFirst: 3.4);
        var r1 = new Player { Speed = 50, Baserunning = 50 };
        Assert.True(PR1Safe(home, r1) <= C.DoublePlayForceOnlyMaxRunnerSuccess, "二塁封殺は楽に間に合う域のはず");
        Assert.True(1.0 - PBatterSafe(home) < C.DoublePlayAttemptMinRelaySuccess, "併殺は狙わない域のはず");

        var rng = new Xoshiro256Random(404);
        for (var i = 0; i < 50; i++)
        {
            var runner = new Player { Speed = 50, Baserunning = 50 };
            var batter = new Player();
            var (bases, extraOuts, batterSafeOnFc, moves) = RunOnce(home, runner, batter, rng);
            Assert.Equal(1, extraOuts);
            Assert.True(batterSafeOnFc);
            Assert.True(ForceOutAtSecond(moves));
            Assert.Contains(moves, m => m.ForceOnly && m.FromBase == 1 && m.ToBase == 2 && m.Force);
            Assert.Same(batter, bases.First);
            Assert.Null(bases.Second);
        }
        var fresh = new Xoshiro256Random(404);
        Assert.Equal(fresh.NextDouble(), rng.NextDouble(), 15); // 分岐A-3 も RNG 非消費
    }

    [Fact]
    public void NearSecond_VeryFastBatter_WithForceOnlyOff_TakesFirstOnly()
    {
        var off = C with { DoublePlayForceOnlyMaxRunnerSuccess = 0.0 };
        var home = Grounder(x: -6, z: 37, fieldedAt: 1.4, batterToFirst: 3.4);
        var runner = new Player { Speed = 50, Baserunning = 50 };
        var (bases, extraOuts, batterSafeOnFc, moves) = RunOnce(home, runner, new Player(), new Xoshiro256Random(404), coeff: off);
        Assert.Equal(0, extraOuts);
        Assert.False(batterSafeOnFc);
        Assert.False(ForceOutAtSecond(moves));
        Assert.Same(runner, bases.Second);
    }

    [Fact]
    public void SelfForceHome_FielderAtCatcher_BasesLoadedComebacker_ForcesR3AtHome()
    {
        // 満塁・捕手の目の前（自打球点=本塁）＝本塁の自己フォースが二塁封殺より圧倒的に有利。
        // 二塁走者・一塁走者は押し出しカスケードでそれぞれ三塁・二塁へ安全進塁するはず。
        var home = Grounder(x: 0, z: 0, fieldedAt: 1.2, batterToFirst: 4.60, fielderRole: FieldPosition.Catcher);
        var r3 = new Player { Speed = 50, Baserunning = 50 };
        var pR1Safe2nd = PR1Safe(home, new Player { Speed = 50, Baserunning = 50 });
        var pR3SafeHome = PLeadSafe(home, r3, fromBase: 3, HomePlayResolver.HomeForceParams(C));
        Assert.True(pR3SafeHome < pR1Safe2nd, "本塁の自己フォースが二塁封殺より有利なはず");

        var rng = new Xoshiro256Random(914);
        int dp = 0, fc = 0;
        for (var i = 0; i < 3000; i++)
        {
            var r1 = new Player { Speed = 50, Baserunning = 50 };
            var r2 = new Player { Speed = 50, Baserunning = 50 };
            var (bases, extraOuts, homeOuts, batterSafeOnFc, moves) =
                RunOnceLoaded(home, r1, r2, r3, new Player(), rng, currentOuts: 0);
            Assert.Equal(1, extraOuts);
            Assert.Equal(1, homeOuts); // 本塁封殺の統計参考値（Q31）
            Assert.True(SelfForceOutAt(moves, toBase: 4), "本塁での自己フォース RunnerMove が出るはず");
            Assert.Same(r2, bases.Third); // 三塁走者(r3)は本塁でアウト、二塁走者(r2)が代わりに三塁へ安全進塁
            Assert.True(moves.Any(m => m is { FromBase: 2, ToBase: 3, Out: false }), "二塁走者は三塁へ安全進塁するはず");
            Assert.True(moves.Any(m => m is { FromBase: 1, ToBase: 2, Out: false }), "一塁走者は二塁へ安全進塁するはず");
            if (batterSafeOnFc) fc++; else dp++;
        }
        Assert.True(dp + fc == 3000 && dp > 0, $"本塁代替でも併殺/野選が出るはず（DP={dp}, FC={fc}）");
    }

    [Fact]
    public void SelfForceHome_RequiresR3Present_FallsBackWhenBasesNotLoaded()
    {
        // 捕手が自打球でも、三塁に走者が居なければ（＝満塁でなければ）本塁代替の対象が無い。
        var home = Grounder(x: 0, z: 0, fieldedAt: 1.2, batterToFirst: 4.60, fielderRole: FieldPosition.Catcher);
        var rng = new Xoshiro256Random(915);
        for (var i = 0; i < 50; i++)
        {
            var r1 = new Player { Speed = 50, Baserunning = 50 };
            var r2 = new Player { Speed = 50, Baserunning = 50 };
            var (_, _, _, moves) = RunOnce(home, r1, new Player(), rng, r2: r2); // r3 なし＝満塁でない
            Assert.False(SelfForceOutAt(moves, toBase: 4), "満塁でなければ本塁代替は起こりえない");
        }
    }

    [Fact]
    public void SelfForceHome_PitcherAlsoGatesIn_ComebackerToMound()
    {
        // 投手の目の前で止まる弱いゴロ（本塁至近＝ゲートは Catcher/Pitcher の両方）でも本塁代替を検討できる。
        // マウンド付近（本塁から18m超）まで戻って拾う打球は、自分で本塁へ走り込むより投げるほうが速い
        // （issue #1273: 野手が塁へ走る時間を正しく入れると、遠い捕球点では自己フォースは選ばれない）。
        var home = Grounder(x: 0, z: 3.0, fieldedAt: 0.6, batterToFirst: 4.60, fielderRole: FieldPosition.Pitcher);
        var r3 = new Player { Speed = 50, Baserunning = 50 };
        var pR1Safe2nd = PR1Safe(home, new Player { Speed = 50, Baserunning = 50 });
        var pR3SafeHome = PLeadSafe(home, r3, fromBase: 3, HomePlayResolver.HomeForceParams(C));
        Assert.True(pR3SafeHome < pR1Safe2nd, "マウンド付近の自打球も本塁の自己フォースが有利なはず");

        var rng = new Xoshiro256Random(916);
        var r1x = new Player { Speed = 50, Baserunning = 50 };
        var r2x = new Player { Speed = 50, Baserunning = 50 };
        var (_, extraOuts, homeOuts, _, moves) = RunOnceLoaded(home, r1x, r2x, r3, new Player(), rng);
        Assert.Equal(1, extraOuts);
        Assert.Equal(1, homeOuts);
        Assert.True(SelfForceOutAt(moves, toBase: 4));
    }

    [Fact]
    public void SelfForce_Deterministic_SameSeedSameResult()
    {
        var home = Grounder(
            x: Field.ThirdBase.X, z: Field.ThirdBase.Z, fieldedAt: 1.3, batterToFirst: 4.60,
            fielderRole: FieldPosition.ThirdBase);
        int Count(ulong seed)
        {
            var rng = new Xoshiro256Random(seed);
            var dp = 0;
            for (var i = 0; i < 500; i++)
            {
                var r1 = new Player { Speed = 50, Baserunning = 50 };
                var r2 = new Player { Speed = 50, Baserunning = 50 };
                var (_, _, batterSafeOnFc, moves) = RunOnce(home, r1, new Player(), rng, r2: r2);
                if (!batterSafeOnFc && SelfForceOutAt(moves, toBase: 3)) dp++;
            }
            return dp;
        }
        Assert.Equal(Count(42), Count(42));
        Assert.True(Count(42) > 0, "この設定では三塁代替の併殺が出るはず");
    }

    // ================= 決定論・従来テーブルへのフォールバック =================

    [Fact]
    public void Deterministic_SameSeedSameResult()
    {
        var home = Grounder(x: -6, z: 37, fieldedAt: 1.4, batterToFirst: 4.45);
        int Count(ulong seed)
        {
            var rng = new Xoshiro256Random(seed);
            var dp = 0;
            for (var i = 0; i < 500; i++)
            {
                var (_, _, batterSafeOnFc, moves) = RunOnce(home, new Player { Speed = 50, Baserunning = 50 }, new Player(), rng);
                if (!batterSafeOnFc && ForceOutAtSecond(moves)) dp++;
            }
            return dp;
        }
        Assert.Equal(Count(42), Count(42));
        Assert.True(Count(42) > 0, "この設定では併殺が出るはず（決定論の中身が空でないこと）");
    }

    [Fact]
    public void NullBatterToFirst_FallsBackToLegacyTable()
    {
        // home に打者一塁到達時刻が無い（内野ゴロでない等）＝物理レースは組めず従来テーブル（DoublePlayProb）へ。
        // 従来テーブルは一塁走者の Speed だけを見る 13%固定＝物理（幾何・打者脚を見る）とは明確に別挙動。
        var legacy = new HomePlayContext(
            Field,
            new HomePlaySituation(new Vector3D(-6, 0, 37), 1.4, new Player { ArmStrength = 55 }.ToFielder().ThrowSpeedMps),
            new TacticsCoefficients(), 0.5, DefenseDepth.Normal, IsFly: false); // BatterToFirstSeconds 未指定＝null
        var rng = new Xoshiro256Random(606);
        int dp = 0;
        for (var i = 0; i < 4000; i++)
        {
            var bases = new BaseState { First = new Player { Speed = 50 } };
            var (_, extraOuts, _, batterSafeOnFc, _, moves) = BaserunningModel.ApplyDetailed(
                bases, PlateAppearanceResult.InPlayOut, new Player(), 1, C, rng, collectMoves: true, legacy);
            if (!batterSafeOnFc && extraOuts == 1 && ForceOutAtSecond(moves)) dp++;
        }
        // 13%固定テーブル（Speed50で 0.13）＝4000試行で概ね 440〜600。物理（この浅いゴロならほぼ全併殺）とは明確に違う。
        Assert.InRange(dp, 400, 660);
    }

    // ================= 併殺の代替（一塁手の自己フォース→二塁タッチ, issue 3-6-tag-double-play） =================

    private static double PLeadSafeBatter(HomePlayContext home, HomePlayResolver.BasePlayParams force)
    {
        // 一塁手自身が一塁へ走り込む一段目は送球でなく自分の脚（issue #1273）。
        var margin = SelfForceArriveAtBase(home, force.TargetPoint) + force.TagSeconds
            - home.BatterToFirstSeconds!.Value + force.SuccessBias;
        return MathUtil.Clamp(MathUtil.Logistic(margin / force.MarginScale), 0.01, 0.99);
    }

    /// <summary>この代替での一塁走者の二塁タッチ回避見込み（判定モデルの pR1SafeTag と同じ式）。
    /// 一段目（一塁手が一塁へ走り込む）の到達時刻は <paramref name="home"/> の実捕球点から求める（issue #1273）。</summary>
    private static double PR1SafeTag(HomePlayContext home, Player r1)
    {
        var firstForce = HomePlayResolver.FirstForceParams(Field, C);
        var situation = new HomePlaySituation(
            Field.FirstBase, SelfForceArriveAtBase(home, firstForce.TargetPoint),
            home.Situation.OutfielderThrowSpeedMps, home.Situation.OutfielderFielding);
        var secondTag = HomePlayResolver.SecondTagParams(Field, C);
        return HomePlayResolver.SuccessProbability(
            r1, 1, situation, Field, C, secondTag, C.DoublePlayR1StartDelaySeconds,
            transferSecondsOverride: C.DoublePlaySelfForceRelayTransferSeconds);
    }

    private static bool FirstBaseTagOutAt(System.Collections.Generic.IReadOnlyList<RunnerMove> moves, bool? outAtEnd = null)
        => moves.Any(m => m.FromBase == 1 && m.ToBase == 2 && m.FirstBaseSelfForceTag && (outAtEnd is null || m.Out == outAtEnd));

    [Fact]
    public void FirstForceParams_TargetIsFirstBase_UsesFirstForceCoefficients()
    {
        var p = HomePlayResolver.FirstForceParams(Field, C);
        Assert.Equal(1, p.TargetBase);
        Assert.Equal(Field.FirstBase.X, p.TargetPoint.X, 6);
        Assert.Equal(Field.FirstBase.Z, p.TargetPoint.Z, 6);
        Assert.Equal(C.FirstForceTagSeconds, p.TagSeconds, 12);
        Assert.Equal(C.FirstForceSuccessBias, p.SuccessBias, 12);
        Assert.Equal(C.FirstForceMarginScale, p.MarginScale, 12);
    }

    [Fact]
    public void SecondTagParams_TargetIsSecondBase_UsesSecondTagCoefficients()
    {
        var p = HomePlayResolver.SecondTagParams(Field, C);
        Assert.Equal(2, p.TargetBase);
        Assert.Equal(Field.SecondBase.X, p.TargetPoint.X, 6);
        Assert.Equal(Field.SecondBase.Z, p.TargetPoint.Z, 6);
        Assert.Equal(C.SecondTagSeconds, p.TagSeconds, 12);
        Assert.Equal(C.SecondTagSuccessBias, p.SuccessBias, 12);
        Assert.Equal(C.SecondTagMarginScale, p.MarginScale, 12);
    }

    // 一塁手の自己フォース→二塁タッチ代替（issue 3-6-tag-double-play）は、issue #1273 で一段目に
    // 野手が塁へ走る時間（SelfForceFielderSprintSpeedMps）を正しく入れた結果、現行係数では構造的に
    // 標準の二塁封殺（3-6-3型）へ勝てなくなった（下の FirstBaseTag_EvenAtBestCaseGeometry_NeverBeatsSecondForce
    // が最良条件＝捕球点が一塁ベース直上でも勝てないことを固定している）。理由は二段目の固定オーバーヘッドの差:
    // 代替側（DoublePlaySelfForceRelayTransferSeconds 0.70 + SecondTagSeconds 0.22 − SecondTagSuccessBias 0.12 ＝0.80）
    // が標準側（OutfieldTransferSeconds 0.60 + SecondForceTagSeconds 0.10 ＝0.70）より0.10s重く、二塁が
    // フォースでなくタッチ（打者が自己フォースで先にアウトになり、一塁走者のフォースが外れるため）に
    // なる分だけ遅いぶんを一段目の移動距離0でも埋め切れない。この代替自体の内部ロジック（A-1/A-2・走者保存則）
    // は将来の係数チューニングで再び選ばれ得るため、下のテスト群はその二段目オーバーヘッドだけを軽くした
    // 係数で経路を強制的に通し、回帰ガードとして残す（一段目の脚力そのものは本物の係数のまま）。
    private static readonly BaserunningCoefficients AltFriendly = C with
    {
        DoublePlaySelfForceRelayTransferSeconds = 0.0, SecondTagSeconds = 0.10, SecondTagSuccessBias = 0.0,
    };

    [Fact]
    public void FirstBaseTag_EvenAtBestCaseGeometry_NeverBeatsSecondForce()
    {
        // 一塁ベースのほぼ真上で捕った一塁ゴロ（自己フォースの移動距離がほぼ0＝一段目は最良条件）でも、
        // 二段目の固定オーバーヘッドの差（上のコメント）で代替は標準の二塁封殺に勝てない（issue #1273）。
        var home = Grounder(x: 19.4, z: 19.4, fieldedAt: 1.3, batterToFirst: 4.60, fielderRole: FieldPosition.FirstBase);
        var r1Probe = new Player { Speed = 50, Baserunning = 50 };
        var pR1Safe2nd = PR1Safe(home, r1Probe);
        var pR1SafeTagValue = PR1SafeTag(home, r1Probe);
        Assert.True(pR1SafeTagValue > pR1Safe2nd,
            $"最良条件でも代替は標準の二塁封殺に勝てないはず: tag={pR1SafeTagValue:F4} > second={pR1Safe2nd:F4}");

        var rng = new Xoshiro256Random(1060);
        for (var i = 0; i < 200; i++)
        {
            var r1 = new Player { Speed = 50, Baserunning = 50 };
            var (_, _, _, moves) = RunOnce(home, r1, new Player(), rng, currentOuts: 0);
            Assert.False(FirstBaseTagOutAt(moves), "最良条件でも代替は選ばれないはず");
        }
    }

    [Fact]
    public void FirstBaseTag_FielderAtFirstBase_WideGrounder_SelectsAlternateOverSecondForce()
    {
        // 一塁ベース付近で捕った一塁ゴロ＝二段目の固定オーバーヘッドを AltFriendly で埋めれば代替が有利になる
        // （回帰ガード。現行係数での非選択は上の EvenAtBestCaseGeometry テストが別に固定している）。
        var home = Grounder(x: 19.4, z: 19.4, fieldedAt: 1.3, batterToFirst: 4.60, fielderRole: FieldPosition.FirstBase);
        var rng = new Xoshiro256Random(1060);
        int dp = 0, safe = 0;
        for (var i = 0; i < 3000; i++)
        {
            var r1 = new Player { Speed = 50, Baserunning = 50 };
            var (bases, extraOuts, batterSafeOnFc, moves) = RunOnce(home, r1, new Player(), rng, currentOuts: 0, coeff: AltFriendly);
            Assert.False(batterSafeOnFc, "この代替では打者は常にアウトのまま（野選にはならない）");
            Assert.False(moves.Any(m => m.FromBase == 1 && m.ToBase == 2 && m.Out && m.Force), "標準の二塁封殺（Force）は起きないはず（代替が選ばれた）");
            if (extraOuts == 1)
            {
                dp++;
                Assert.True(FirstBaseTagOutAt(moves, outAtEnd: true), "一塁走者が二塁でタッチアウトになるはず");
                Assert.Null(bases.Second);
            }
            else
            {
                safe++;
                Assert.Equal(0, extraOuts);
                Assert.True(FirstBaseTagOutAt(moves, outAtEnd: false), "タッチ失敗で一塁走者が二塁セーフになるはず");
                Assert.Same(r1, bases.Second);
            }
            Assert.Null(bases.First); // 打者は一塁手の自己フォースで一塁アウト＝一塁走者は必ず一塁を去る
        }
        Assert.True(dp + safe == 3000 && dp > 0 && safe > 0,
            $"この代替は確率的にDP/タッチ失敗の両方が出るはず（DP={dp}, 失敗={safe}）");
    }

    [Fact]
    public void FirstBaseTag_BallNearStandardFirstBasePosition_FallsBackToSecondForce()
    {
        // 一塁手の標準守備位置付近の捕球点は、直接二塁へ投げる標準ルートのほうが有利（二塁に近い）＝
        // 代替は選ばれず、今日どおりの二塁封殺（3-6-3型）に落ちる。
        var home = Grounder(x: 15, z: 20, fieldedAt: 1.3, batterToFirst: 4.60, fielderRole: FieldPosition.FirstBase);
        var r1Probe = new Player { Speed = 50, Baserunning = 50 };
        Assert.True(PR1Safe(home, r1Probe) < PR1SafeTag(home, r1Probe), "この捕球点では標準の二塁封殺のほうが有利なはず");

        var rng = new Xoshiro256Random(1061);
        for (var i = 0; i < 200; i++)
        {
            var r1 = new Player { Speed = 50, Baserunning = 50 };
            var (_, _, _, moves) = RunOnce(home, r1, new Player(), rng);
            Assert.False(FirstBaseTagOutAt(moves), "この捕球点では代替は選ばれないはず");
        }
    }

    [Fact]
    public void FirstBaseTag_RequiresExactFielderRoleMatch_FallsBackForOtherPositions()
    {
        // 同じ捕球点でも FielderRole が一塁手でなければ（例: 遊撃手）ゲート不成立＝代替を検討しない。
        var home = Grounder(x: 21, z: 17, fieldedAt: 1.3, batterToFirst: 4.60, fielderRole: FieldPosition.Shortstop);
        var rng = new Xoshiro256Random(1062);
        for (var i = 0; i < 50; i++)
        {
            var r1 = new Player { Speed = 50, Baserunning = 50 };
            var (_, _, _, moves) = RunOnce(home, r1, new Player(), rng);
            Assert.False(FirstBaseTagOutAt(moves), "遊撃手処理では一塁代替のゲートは成立しないはず");
        }
    }

    [Fact]
    public void FirstBaseTag_SelfForceNotViable_NoRngConsumed()
    {
        // 捕球点が一塁から遠く、快足打者なら一塁の自己フォース自体が間に合わない（分岐A-1）＝この代替は
        // 選ばれない（既存の代替＝三塁/本塁の自己フォースと同じ流儀）。RNG非消費。
        var home = Grounder(x: 90, z: 10, fieldedAt: 1.3, batterToFirst: 3.00, fielderRole: FieldPosition.FirstBase);
        var firstForce = HomePlayResolver.FirstForceParams(Field, C);
        Assert.True(PLeadSafeBatter(home, firstForce) > C.DoublePlayForceMaxRunnerSuccess, "この捕球点では自己フォース自体が間に合わないはず");

        var rng = new Xoshiro256Random(1063);
        for (var i = 0; i < 50; i++)
        {
            var r1 = new Player { Speed = 50, Baserunning = 50 };
            var (_, extraOuts, batterSafeOnFc, moves) = RunOnce(home, r1, new Player(), rng);
            Assert.Equal(0, extraOuts);
            Assert.False(batterSafeOnFc);
            Assert.False(FirstBaseTagOutAt(moves));
        }
        var fresh = new Xoshiro256Random(1063);
        Assert.Equal(fresh.NextDouble(), rng.NextDouble(), 15); // RNG非消費
    }

    [Fact]
    public void FirstBaseTag_RelayTooWeakForFastRunner_TakesBatterOnly_NoRngConsumed()
    {
        // 一塁の自己フォースは確実だが、二塁へのタッチが快足の一塁走者には間に合わない見込み＝
        // 守備は投げない（分岐A-2相当）。打者は単独アウトのまま、一塁走者は普通に二塁へ安全進塁する。RNG非消費。
        var home = Grounder(x: 19.4, z: 19.4, fieldedAt: 1.3, batterToFirst: 4.60, fielderRole: FieldPosition.FirstBase);
        var fastR1 = new Player { Speed = 99, Baserunning = 99 };
        Assert.True(1.0 - PR1SafeTag(home, fastR1) < C.DoublePlayAttemptMinRelaySuccess, "快足の一塁走者には二塁タッチが間に合わない見込みのはず");

        var rng = new Xoshiro256Random(1064);
        for (var i = 0; i < 50; i++)
        {
            var r1 = new Player { Speed = 99, Baserunning = 99 };
            var (bases, extraOuts, batterSafeOnFc, moves) = RunOnce(home, r1, new Player(), rng);
            Assert.Equal(0, extraOuts);
            Assert.False(batterSafeOnFc);
            Assert.False(FirstBaseTagOutAt(moves), "投げないので表示専用フラグも立たないはず");
            Assert.Null(bases.First);
            Assert.Same(r1, bases.Second); // 一塁走者は普通に（フォースで）二塁へ進む
        }
        var fresh = new Xoshiro256Random(1064);
        Assert.Equal(fresh.NextDouble(), rng.NextDouble(), 15); // RNG非消費
    }

    [Fact]
    public void FirstBaseTag_ProducesDoublePlay_SecondOutIsTagNotForceOrForceOnly()
    {
        var home = Grounder(x: 19.4, z: 19.4, fieldedAt: 1.3, batterToFirst: 4.60, fielderRole: FieldPosition.FirstBase);
        var rng = new Xoshiro256Random(1065);
        RunnerMove? outMove = null;
        for (var i = 0; i < 3000 && outMove is null; i++)
        {
            var r1 = new Player { Speed = 50, Baserunning = 50 };
            var (_, extraOuts, _, moves) = RunOnce(home, r1, new Player(), rng, currentOuts: 0, coeff: AltFriendly);
            if (extraOuts == 1) outMove = moves.First(m => m.FromBase == 1 && m.ToBase == 2 && m.Out);
        }
        Assert.NotNull(outMove);
        Assert.False(outMove!.Force, "一塁を先に踏むと二塁のフォースは外れる＝タッチプレー");
        Assert.False(outMove.ForceOnly);
        Assert.True(outMove.FirstBaseSelfForceTag);
    }

    [Fact]
    public void FirstBaseTag_Deterministic_SameSeedSameResult()
    {
        var home = Grounder(x: 19.4, z: 19.4, fieldedAt: 1.3, batterToFirst: 4.60, fielderRole: FieldPosition.FirstBase);
        int Count(ulong seed)
        {
            var rng = new Xoshiro256Random(seed);
            var dp = 0;
            for (var i = 0; i < 500; i++)
            {
                var r1 = new Player { Speed = 50, Baserunning = 50 };
                var (_, _, batterSafeOnFc, moves) = RunOnce(home, r1, new Player(), rng, coeff: AltFriendly);
                if (!batterSafeOnFc && FirstBaseTagOutAt(moves, outAtEnd: true)) dp++;
            }
            return dp;
        }
        Assert.Equal(Count(42), Count(42));
        Assert.True(Count(42) > 0, "この設定では一塁代替の併殺が出るはず");
    }

    // ---- 二塁・三塁に走者がいる場面（PR #1076 レビュー: タッチ失敗で走者の状態が壊れていた） ----

    /// <summary>走者の保存則: 打席開始時の人数（打者＋走者）＝塁上＋得点＋この打席のアウト。</summary>
    private static void AssertRunnersConserved(int before, BaseState bases, int runs, int outs, string label)
    {
        var onBase = (bases.First is null ? 0 : 1) + (bases.Second is null ? 0 : 1) + (bases.Third is null ? 0 : 1);
        Assert.True(before == onBase + runs + outs,
            $"{label}: 走者の数が合わない（開始 {before} ＝ 塁上 {onBase} ＋ 得点 {runs} ＋ アウト {outs} のはず）");
        // Player は値で等価な record＝同じ能力の別人を同一視しないよう参照で比べる
        var people = new[] { bases.First, bases.Second, bases.Third }.Where(p => p is not null).ToList();
        Assert.True(people.Distinct(System.Collections.Generic.ReferenceEqualityComparer.Instance).Count() == people.Count,
            $"{label}: 同じ走者が2つの塁に立っている");
    }

    [Fact]
    public void FirstBaseTag_TagFails_WithRunnerOnSecond_OneMovePerRunner_AndRunnersConserved()
    {
        // 一・二塁で一塁手の代替（一塁自己フォース→二塁タッチ）がタッチに失敗した場合。
        // 修正前は bases.Second を一塁走者で上書きし、二塁走者のカスケードがそれを外し、一塁走者の
        // カスケードが 1→2 をもう1本積んでいた（move の二重）。
        var home = Grounder(x: 19.4, z: 19.4, fieldedAt: 1.3, batterToFirst: 4.60, fielderRole: FieldPosition.FirstBase);
        var rng = new Xoshiro256Random(1066);
        int fails = 0, dps = 0;
        for (var i = 0; i < 3000; i++)
        {
            var r1 = new Player { Speed = 50, Baserunning = 50 };
            var r2 = new Player { Speed = 50, Baserunning = 50 };
            var bases = new BaseState { First = r1, Second = r2 };
            var (runs, extraOuts, _, batterSafeOnFc, _, moves) = BaserunningModel.ApplyDetailed(
                bases, PlateAppearanceResult.InPlayOut, new Player(), 0, AltFriendly, rng, collectMoves: true, home);
            Assert.False(batterSafeOnFc);
            AssertRunnersConserved(3, bases, runs, 1 + extraOuts, $"試行{i}");
            Assert.Equal(1, moves.Count(m => ReferenceEquals(m.Runner, r1)));   // 一塁走者の move は1本だけ
            Assert.Equal(1, moves.Count(m => ReferenceEquals(m.Runner, r2)));   // 二塁走者も1本（2→3 の押し出し）
            Assert.Same(r2, bases.Third);
            Assert.Null(bases.First);
            if (FirstBaseTagOutAt(moves, outAtEnd: false))
            {
                fails++;
                Assert.Equal(0, extraOuts);
                Assert.Same(r1, bases.Second);
            }
            else if (FirstBaseTagOutAt(moves, outAtEnd: true))
            {
                dps++;
                Assert.Null(bases.Second);
            }
        }
        Assert.True(fails > 0 && dps > 0, $"タッチ失敗と併殺の両方が出るはず（失敗={fails}, 併殺={dps}）");
    }

    [Fact]
    public void FirstBaseTag_TagFails_BasesLoadedNormalDepth_RunnerScoresAndOthersAdvanceOnce()
    {
        // 満塁・通常守備: 三塁走者は押し出されて生還、二塁走者は三塁、一塁走者は二塁（タッチ失敗）。
        var home = Grounder(x: 19.4, z: 19.4, fieldedAt: 1.3, batterToFirst: 4.60, fielderRole: FieldPosition.FirstBase);
        var rng = new Xoshiro256Random(1067);
        var fails = 0;
        for (var i = 0; i < 3000; i++)
        {
            var r1 = new Player { Speed = 50, Baserunning = 50 };
            var r2 = new Player { Speed = 50, Baserunning = 50 };
            var r3 = new Player { Speed = 50, Baserunning = 50 };
            var bases = new BaseState { First = r1, Second = r2, Third = r3 };
            var (runs, extraOuts, _, _, _, moves) = BaserunningModel.ApplyDetailed(
                bases, PlateAppearanceResult.InPlayOut, new Player(), 0, AltFriendly, rng, collectMoves: true, home);
            AssertRunnersConserved(4, bases, runs, 1 + extraOuts, $"試行{i}");
            foreach (var r in new[] { r1, r2, r3 })
                Assert.Equal(1, moves.Count(m => ReferenceEquals(m.Runner, r)));
            if (!FirstBaseTagOutAt(moves, outAtEnd: false)) continue;
            fails++;
            Assert.Equal(1, runs);
            Assert.Same(r1, bases.Second);
            Assert.Same(r2, bases.Third);
        }
        Assert.True(fails > 0, "満塁でもタッチ失敗の場面が出るはず");
    }

    [Fact]
    public void FirstBaseTag_NotChosen_WhenBasesLoadedInfieldIn_HomeIsTheMainLine()
    {
        // 満塁・内野前進は本塁封殺が本筋。一塁を踏んでから二塁へ投げる代替は選ばない
        // （選ぶとタッチ失敗のあとに本塁封殺の勝負がもう一度走り、守備が二重になる）。
        var home = Grounder(x: 21, z: 17, fieldedAt: 1.3, batterToFirst: 4.60,
            fielderRole: FieldPosition.FirstBase, depth: DefenseDepth.In);
        var rng = new Xoshiro256Random(1068);
        for (var i = 0; i < 1000; i++)
        {
            var r1 = new Player { Speed = 50, Baserunning = 50 };
            var r2 = new Player { Speed = 50, Baserunning = 50 };
            var r3 = new Player { Speed = 50, Baserunning = 50 };
            var bases = new BaseState { First = r1, Second = r2, Third = r3 };
            var (runs, extraOuts, _, batterSafeOnFc, _, moves) = BaserunningModel.ApplyDetailed(
                bases, PlateAppearanceResult.InPlayOut, new Player(), 0, C, rng, collectMoves: true, home);
            Assert.False(FirstBaseTagOutAt(moves), "満塁・内野前進では一塁の自己フォース代替を選ばないはず");
            AssertRunnersConserved(4, bases, runs, (batterSafeOnFc ? 0 : 1) + extraOuts, $"試行{i}");
        }
    }

    // ================= 併殺の代替（二塁の自己フォース＝6-6-3/4-4-3, issue #1549） =================
    // 遊撃手/二塁手が自打球を自分で二塁ベースへ運んで封殺（送球なし）→一塁へ自己送球。標準の二塁封殺
    // （feed+pivotタッチ）と対象走者・対象塁は同じ一塁走者・二塁のまま＝「届き方」だけが違う比較になる。

    /// <summary>この代替での打者の一塁生存見込み（判定モデルの pBatterSafe と同じ式。二段目は自己フォース専用の
    /// 握り替え DoublePlaySelfForceRelayTransferSeconds を使う）。</summary>
    private static double PBatterSafeSelfForceSecond(HomePlayContext home, HomePlayResolver.BasePlayParams force)
    {
        var arriveAtBase = SelfForceArriveAtBase(home, force.TargetPoint);
        var selfForceSituation = new HomePlaySituation(
            force.TargetPoint, arriveAtBase, home.Situation.OutfielderThrowSpeedMps, home.Situation.OutfielderFielding);
        var relayToFirst = HomePlayResolver
            .ResolveThrowPath(selfForceSituation, Field.FirstBase, C, transferSecondsOverride: C.DoublePlaySelfForceRelayTransferSeconds)
            .ArriveAtSeconds + C.SecondForceTagSeconds;
        var margin = relayToFirst - home.BatterToFirstSeconds!.Value + C.DoublePlayRelayBias;
        return MathUtil.Clamp(MathUtil.Logistic(margin / C.DoublePlayRelayMarginScale), 0.01, 0.99);
    }

    [Fact]
    public void SecondSelfForce_FielderAtSecondBase_Shortstop_SelectsSelfForceOverStandardRoute()
    {
        // 遊撃手の目の前（自打球点=二塁ベースそのもの）＝二塁の自己フォース（6-6-3）が標準の
        // 二塁封殺（feed+pivotタッチ）より有利（握り替え分だけ速い）。
        var home = Grounder(
            x: Field.SecondBase.X, z: Field.SecondBase.Z, fieldedAt: 1.3, batterToFirst: 4.60,
            fielderRole: FieldPosition.Shortstop);
        var force2nd = HomePlayResolver.SecondForceParams(Field, C);
        var pR1Safe2nd = PR1Safe(home, new Player { Speed = 50, Baserunning = 50 });
        var pR1SafeSelf = PLeadSafe(home, new Player { Speed = 50, Baserunning = 50 }, fromBase: 1, force2nd);
        Assert.True(pR1SafeSelf < pR1Safe2nd,
            $"二塁の自己フォースが標準ルートより有利なはず: self={pR1SafeSelf:F4} < standard={pR1Safe2nd:F4}");

        var rng = new Xoshiro256Random(1549);
        int dp = 0, fc = 0;
        for (var i = 0; i < 3000; i++)
        {
            var r1 = new Player { Speed = 50, Baserunning = 50 };
            var batter = new Player();
            var (bases, extraOuts, batterSafeOnFc, moves) = RunOnce(home, r1, batter, rng, currentOuts: 0);
            Assert.Equal(1, extraOuts);
            Assert.True(SelfForceOutAt(moves, toBase: 2), "二塁での自己フォース RunnerMove が出るはず");
            Assert.False(StandardForceOutAtSecond(moves), "標準の二塁封殺（feed+pivot）は起きないはず（代替が選ばれた）");
            if (batterSafeOnFc) { fc++; Assert.Same(batter, bases.First); } else { dp++; Assert.Null(bases.First); Assert.Null(bases.Second); }
        }
        Assert.True(dp + fc == 3000 && dp > 0 && fc > 0, $"二塁の自己フォースでも併殺/野選が出るはず（DP={dp}, FC={fc}）");
    }

    [Fact]
    public void SecondSelfForce_FielderAtSecondBase_SecondBaseman_SelectsSelfForceOverStandardRoute()
    {
        // 二塁手の目の前（自打球点=二塁ベースそのもの）＝4-4-3 も同じ流儀で選ばれるはず。
        var home = Grounder(
            x: Field.SecondBase.X, z: Field.SecondBase.Z, fieldedAt: 1.3, batterToFirst: 4.60,
            fielderRole: FieldPosition.SecondBase);
        var rng = new Xoshiro256Random(1550);
        int dp = 0, fc = 0;
        for (var i = 0; i < 3000; i++)
        {
            var r1 = new Player { Speed = 50, Baserunning = 50 };
            var batter = new Player();
            var (bases, extraOuts, batterSafeOnFc, moves) = RunOnce(home, r1, batter, rng, currentOuts: 0);
            Assert.Equal(1, extraOuts);
            Assert.True(SelfForceOutAt(moves, toBase: 2), "二塁での自己フォース RunnerMove が出るはず");
            Assert.False(StandardForceOutAtSecond(moves));
            if (batterSafeOnFc) { fc++; Assert.Same(batter, bases.First); } else { dp++; Assert.Null(bases.First); Assert.Null(bases.Second); }
        }
        Assert.True(dp + fc == 3000 && dp > 0 && fc > 0, $"4-4-3 でも併殺/野選が出るはず（DP={dp}, FC={fc}）");
    }

    [Fact]
    public void SecondSelfForce_FarFromSecondBase_FallsBackToStandardRoute()
    {
        // 二遊間の深いゴロ（捕球点が二塁から遠い）は、標準の二塁封殺（送球）のほうが自己フォース（自分の脚）
        // より速い＝代替は選ばれず今日どおりに落ちる（issue本文「二塁ベースから遠いゴロは、比較で自然に
        // 今のルートへ落ちる」）。
        var home = Grounder(x: -15, z: 20, fieldedAt: 1.9, batterToFirst: 4.20, arm: 55, fielderRole: FieldPosition.Shortstop);
        var force2nd = HomePlayResolver.SecondForceParams(Field, C);
        var pR1Safe2nd = PR1Safe(home, new Player { Speed = 50, Baserunning = 50 });
        var pR1SafeSelf = PLeadSafe(home, new Player { Speed = 50, Baserunning = 50 }, fromBase: 1, force2nd);
        Assert.True(pR1SafeSelf > pR1Safe2nd, "この捕球点では標準ルートのほうが有利なはず（二塁は遠い）");

        var rng = new Xoshiro256Random(1551);
        for (var i = 0; i < 200; i++)
        {
            var r1 = new Player { Speed = 50, Baserunning = 50 };
            var (_, _, _, moves) = RunOnce(home, r1, new Player(), rng);
            Assert.False(SelfForceOutAt(moves, toBase: 2), "この捕球点では二塁の自己フォースは選ばれないはず");
        }
    }

    [Fact]
    public void SecondSelfForce_RequiresExactFielderRoleMatch_FallsBackForOtherPositions()
    {
        // 同じ捕球点（二塁ベースそのもの）でも FielderRole が遊撃手/二塁手でなければ（例: 三塁手の
        // 中継プレー等）ゲート不成立＝代替を検討しない。
        var home = Grounder(
            x: Field.SecondBase.X, z: Field.SecondBase.Z, fieldedAt: 1.3, batterToFirst: 4.60,
            fielderRole: FieldPosition.ThirdBase);
        var rng = new Xoshiro256Random(1552);
        for (var i = 0; i < 50; i++)
        {
            var r1 = new Player { Speed = 50, Baserunning = 50 };
            var (_, _, _, moves) = RunOnce(home, r1, new Player(), rng);
            Assert.False(SelfForceOutAt(moves, toBase: 2), "三塁手処理では二塁の自己フォースのゲートは成立しないはず");
        }
    }

    [Fact]
    public void SecondSelfForce_FielderFartherFromSecondBase_RaisesLeadRunnerSafety()
    {
        // issue #1273 と同じ流儀: 二塁から離れるほど自分の脚（SelfForceFielderSprintSpeedMps）で走る時間が
        // 伸び、一塁走者の生存見込み（自己フォースされにくさ）が単調に上がるはず。
        var r1 = new Player { Speed = 50, Baserunning = 50 };
        var force2nd = HomePlayResolver.SecondForceParams(Field, C);
        var probs = new System.Collections.Generic.List<double>();
        foreach (var offset in new[] { 0.0, 1.0, 2.0, 3.0, 4.0 })
        {
            var home = Grounder(x: Field.SecondBase.X + offset, z: Field.SecondBase.Z, fieldedAt: 1.3, batterToFirst: 4.60,
                fielderRole: FieldPosition.Shortstop);
            probs.Add(PLeadSafe(home, r1, fromBase: 1, force2nd));
        }
        for (var i = 1; i < probs.Count; i++)
            Assert.True(probs[i] > probs[i - 1],
                $"二塁から離れるほど一塁走者の生存見込みが上がるはず: {string.Join(",", probs.Select(p => p.ToString("F4")))}");
    }

    [Fact]
    public void SecondSelfForce_FasterFielderSprintSpeed_LowersLeadRunnerSafety()
    {
        // 処理野手の走力（SelfForceSprintSpeedMps）そのものを直接振る＝#1273 の再発防止（距離だけでなく
        // 脚力そのものが効くこと）。速いほど二塁へ早く到達し、一塁走者が自己フォースを免れる見込みは下がる。
        var baseHome = Grounder(x: Field.SecondBase.X + 2.0, z: Field.SecondBase.Z, fieldedAt: 1.3, batterToFirst: 4.60,
            fielderRole: FieldPosition.Shortstop);
        var r1 = new Player { Speed = 50, Baserunning = 50 };
        var force2nd = HomePlayResolver.SecondForceParams(Field, C);
        var slow = PLeadSafe(baseHome with { SelfForceSprintSpeedMps = 6.0 }, r1, fromBase: 1, force2nd);
        var fast = PLeadSafe(baseHome with { SelfForceSprintSpeedMps = 9.0 }, r1, fromBase: 1, force2nd);
        Assert.True(fast < slow, $"足の速い野手のほうが一塁走者を自己フォースしやすいはず: fast={fast:F4} < slow={slow:F4}");
    }

    [Fact]
    public void SecondSelfForce_StrongerFielderArm_LowersBatterSafeAtFirst()
    {
        // 二段目（二塁→一塁の自己送球）の肩が強いほど、打者が一塁で生存（野選）する見込みは下がるはず
        // （Order_StrongerPivotArm_LowersBatterSafeAtFirst の自己フォース版）。
        // batterToFirst は自己フォースの二段目（二塁→一塁、握り替え無しで肩だけ）が決着する域に合わせる
        // （標準ルートの PBatterSafe とは分母のオーバーヘッドが違うため同じ 4.45 だと天井/床に飽和する）。
        var home = Grounder(
            x: Field.SecondBase.X, z: Field.SecondBase.Z, fieldedAt: 1.3, batterToFirst: 3.0,
            fielderRole: FieldPosition.Shortstop);
        var force2nd = HomePlayResolver.SecondForceParams(Field, C);
        var weak = PBatterSafeSelfForceSecond(home with { Situation = home.Situation with { OutfielderThrowSpeedMps = 25.0 } }, force2nd);
        var strong = PBatterSafeSelfForceSecond(home with { Situation = home.Situation with { OutfielderThrowSpeedMps = 35.0 } }, force2nd);
        Assert.True(strong < weak, $"肩が強いほうが打者生存(野選)しにくいはず: strong={strong:F4} < weak={weak:F4}");
    }

    [Fact]
    public void SecondSelfForce_ProducesForceTrueAndSelfForceDoublePlayLead_SameMove()
    {
        // GameEngine のタイムライン分岐（SelfForceDoublePlayLead を Force より先に見る）が正しく機能する前提:
        // 二塁の自己フォースの move は標準ルートと同じ Force=true かつ SelfForceDoublePlayLead=true を両方持つ。
        var home = Grounder(
            x: Field.SecondBase.X, z: Field.SecondBase.Z, fieldedAt: 1.3, batterToFirst: 4.60,
            fielderRole: FieldPosition.Shortstop);
        var (_, _, _, moves) = RunOnce(home, new Player { Speed = 50, Baserunning = 50 }, new Player(), new Xoshiro256Random(1553), currentOuts: 0);
        var m = Assert.Single(moves, m => m is { FromBase: 1, ToBase: 2, Out: true });
        Assert.True(m.Force);
        Assert.True(m.SelfForceDoublePlayLead);
    }

    [Fact]
    public void SecondSelfForce_WithRunnerOnSecond_RunnersConservedAndCascadeAdvancesForcedRunner()
    {
        // 一・二塁の場面。二塁走者(r2)は一塁走者(r1)が自己フォースされるかどうかに関わらず、一塁走者が
        // 一塁から出る以上フォースで三塁へ押し出される（r2Forced）。走者保存則（PR #1076 と同種の確認）。
        var home = Grounder(
            x: Field.SecondBase.X, z: Field.SecondBase.Z, fieldedAt: 1.3, batterToFirst: 4.60,
            fielderRole: FieldPosition.Shortstop);
        var rng = new Xoshiro256Random(1554);
        for (var i = 0; i < 1000; i++)
        {
            var r1 = new Player { Speed = 50, Baserunning = 50 };
            var r2 = new Player { Speed = 50, Baserunning = 50 };
            var bases = new BaseState { First = r1, Second = r2 };
            var (runs, extraOuts, _, batterSafeOnFc, _, moves) = BaserunningModel.ApplyDetailed(
                bases, PlateAppearanceResult.InPlayOut, new Player(), 0, C, rng, collectMoves: true, home);
            AssertRunnersConserved(3, bases, runs, (batterSafeOnFc ? 0 : 1) + extraOuts, $"試行{i}");
            Assert.Same(r2, bases.Third); // 一塁走者が自己フォースされてもセーフでも、二塁走者は必ず三塁へ
            Assert.Equal(1, moves.Count(m => ReferenceEquals(m.Runner, r1)));
            Assert.Equal(1, moves.Count(m => ReferenceEquals(m.Runner, r2)));
        }
    }

    [Fact]
    public void SecondSelfForce_Deterministic_SameSeedSameResult()
    {
        var home = Grounder(
            x: Field.SecondBase.X, z: Field.SecondBase.Z, fieldedAt: 1.3, batterToFirst: 4.60,
            fielderRole: FieldPosition.Shortstop);
        int Count(ulong seed)
        {
            var rng = new Xoshiro256Random(seed);
            var dp = 0;
            for (var i = 0; i < 500; i++)
            {
                var r1 = new Player { Speed = 50, Baserunning = 50 };
                var (_, _, batterSafeOnFc, moves) = RunOnce(home, r1, new Player(), rng);
                if (!batterSafeOnFc && SelfForceOutAt(moves, toBase: 2)) dp++;
            }
            return dp;
        }
        Assert.Equal(Count(42), Count(42));
        Assert.True(Count(42) > 0, "この設定では二塁代替の併殺が出るはず");
    }
}
