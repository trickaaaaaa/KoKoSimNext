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
        double x, double z, double fieldedAt, double batterToFirst, int arm = 55, double pivotBonus = 0.0)
    {
        var situation = new HomePlaySituation(
            new Vector3D(x, 0, z), fieldedAt, new Player { ArmStrength = arm }.ToFielder().ThrowSpeedMps);
        return new HomePlayContext(
            Field, situation, new TacticsCoefficients(), 0.5, DefenseDepth.Normal, IsFly: false,
            ThrowArriveSeconds: null, BatterToFirstSeconds: batterToFirst, DoublePlayPivotBonusSeconds: pivotBonus);
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
        var pivotTransfer = Math.Max(0.0, C.DoublePlayPivotTransferSeconds - home.DoublePlayPivotBonusSeconds);
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
        HomePlayContext home, Player r1, Player batter, IRandomSource rng, int currentOuts = 1)
    {
        var bases = new BaseState { First = r1 };
        var (_, extraOuts, _, batterSafeOnFc, _, moves) = BaserunningModel.ApplyDetailed(
            bases, PlateAppearanceResult.InPlayOut, batter, currentOuts, C, rng, collectMoves: true, home);
        return (bases, extraOuts, batterSafeOnFc, moves);
    }

    private static bool ForceOutAtSecond(System.Collections.Generic.IReadOnlyList<RunnerMove> moves)
        => moves.Any(m => m is { FromBase: 1, ToBase: 2, Out: true });

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

    // ================= 「併殺の名手」スキル: 転送時間の短縮（確率でなく時間）=================

    [Fact]
    public void DoublePlayArtist_ShortensPivot_RaisesDoublePlayChance()
    {
        // 併殺を狙う域の打者で、名手ボーナス（転送短縮）が打者生存（野選）を下げる＝併殺成立を押し上げる。
        var plain = Grounder(x: -6, z: 37, fieldedAt: 1.4, batterToFirst: 4.45, pivotBonus: 0.0);
        var artist = Grounder(x: -6, z: 37, fieldedAt: 1.4, batterToFirst: 4.45, pivotBonus: C.DoublePlayPivotTransferSeconds);
        Assert.True(1.0 - PBatterSafe(plain) >= C.DoublePlayAttemptMinRelaySuccess, "素の守備でも併殺を狙う域のはず");
        Assert.True(PBatterSafe(artist) < PBatterSafe(plain),
            $"名手（転送短縮）は打者生存確率を下げる（＝併殺しやすい）はず: artist={PBatterSafe(artist):F3} < plain={PBatterSafe(plain):F3}");

        int Dp(HomePlayContext h, ulong seed)
        {
            var rng = new Xoshiro256Random(seed);
            var dp = 0;
            for (var i = 0; i < 3000; i++)
            {
                var (_, _, batterSafeOnFc, moves) = RunOnce(h, new Player { Speed = 50, Baserunning = 50 }, new Player(), rng);
                if (!batterSafeOnFc && ForceOutAtSecond(moves)) dp++;
            }
            return dp;
        }
        Assert.True(Dp(artist, 505) > Dp(plain, 505), "名手のいる守備は併殺成立が多いはず");
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
}
