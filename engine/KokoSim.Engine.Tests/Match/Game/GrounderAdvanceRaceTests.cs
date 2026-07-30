using System;
using System.Collections.Generic;
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
/// ゴロ凡打の進塁打（二塁→三塁）を 18%固定テーブルから物理レースへ（Issue #318, 設計書12 §3.5）。
/// 守備は幾何（捕球点→三塁の直接送球でどれだけ刺せそうか＝P_direct）で3分岐を純関数で選ぶ:
/// A) 走者自重（RNG非消費）／ B) 一塁を捨てて三塁へ直接送球（野選・P_direct でレース）／
/// C) 一塁を取り走者は転送レース（P_relay）。方向性は係数でなく幾何から創発する。
/// 適用は「ゴロ凡打×二塁走者あり×一塁走者なし×三塁空き×extraOuts==0」の1場面のみ。それ以外は従来テーブル。
/// </summary>
public sealed class GrounderAdvanceRaceTests
{
    private static readonly BaserunningCoefficients C = new();
    private static readonly FieldGeometry Field = new();

    private static HomePlayContext Grounder(
        double x, double z, double fieldedAt, int arm = 50, double aggression = 0.5,
        double? throwArriveSeconds = null, bool isFly = false)
    {
        var situation = new HomePlaySituation(
            new Vector3D(x, 0, z), fieldedAt, new Player { ArmStrength = arm }.ToFielder().ThrowSpeedMps);
        return new HomePlayContext(Field, situation, new TacticsCoefficients(), aggression,
            DefenseDepth.Normal, isFly, throwArriveSeconds);
    }

    /// <summary>捕球点→三塁の直接送球に対する走者の到達確率（分岐選択に使う純関数の量）。</summary>
    private static double PDirect(HomePlayContext home, Player runner)
        => HomePlayResolver.SuccessProbability(
            runner, 2, home.Situation, home.Field, C, HomePlayResolver.AdvanceParams(home.Field, C),
            C.GrounderAdvanceStartDelaySeconds);

    // ゴロ凡打×二塁走者あり×一塁走者なし×三塁空き（＝物理レースが効く唯一の場面）。
    private static (BaseState Bases, int ExtraOuts, bool BatterSafeOnFc, IReadOnlyList<RunnerMove> Moves) RunOnce(
        HomePlayContext home, Player r2, Player batter, IRandomSource rng)
    {
        var bases = new BaseState { Second = r2 };
        var (_, extraOuts, _, batterSafeOnFc, _, moves) = BaserunningModel.ApplyDetailed(
            bases, PlateAppearanceResult.InPlayOut, batter, 0, C, rng, collectMoves: true, home);
        return (bases, extraOuts, batterSafeOnFc, moves);
    }

    // ================= 係数束（純関数・rng不使用）=================

    [Fact]
    public void AdvanceParams_TargetIsThirdBase_UsesAdvanceCoefficients()
    {
        var p = HomePlayResolver.AdvanceParams(Field, C);
        Assert.Equal(3, p.TargetBase);
        Assert.Equal(Field.ThirdBase.X, p.TargetPoint.X, 6);
        Assert.Equal(Field.ThirdBase.Z, p.TargetPoint.Z, 6);
        Assert.Equal(C.AdvanceTagSeconds, p.TagSeconds, 12);
        Assert.Equal(C.AdvanceSuccessBias, p.SuccessBias, 12);
        Assert.Equal(C.AdvanceMarginScale, p.MarginScale, 12);
    }

    [Fact]
    public void ResolveThrowPath_TransferOverrideNull_IsBitIdenticalToLegacy()
    {
        // 既存呼び出し（transferSecondsOverride を足さない）は現行 OutfieldTransferSeconds とビット一致（帯不変）。
        var s = new HomePlaySituation(new Vector3D(20, 0, 40), 2.0,
            new Player { ArmStrength = 55 }.ToFielder().ThrowSpeedMps);
        var legacy = HomePlayResolver.ResolveThrowPath(s, Field.ThirdBase, C);
        var explicitNull = HomePlayResolver.ResolveThrowPath(s, Field.ThirdBase, C, transferSecondsOverride: null);
        Assert.Equal(legacy.ReleaseAtSeconds, explicitNull.ReleaseAtSeconds, 15);
        Assert.Equal(legacy.ArriveAtSeconds, explicitNull.ArriveAtSeconds, 15);
        // 明示的に別の握り替え秒を渡すと到達時刻が変わる（転送握り替えが効く）。
        var overridden = HomePlayResolver.ResolveThrowPath(s, Field.ThirdBase, C,
            transferSecondsOverride: C.AdvanceRelayTransferSeconds);
        Assert.NotEqual(legacy.ReleaseAtSeconds, overridden.ReleaseAtSeconds);
    }

    // ================= 順序性（幾何・走力からの創発）=================

    [Fact]
    public void Order_Depth_DeeperFieldingPoint_RaisesReachProbability()
    {
        // ①深さ: 捕球点を三塁から遠く（深く）するほど直接送球が長い＝走者の到達確率が単調に上がる。
        var runner = new Player { Speed = 50, Baserunning = 50 };
        var probs = new List<double>();
        foreach (var (x, z, at) in new[] { (10.0, 26.0, 1.6), (14.0, 34.0, 1.9), (18.0, 42.0, 2.2), (22.0, 50.0, 2.5) })
            probs.Add(PDirect(Grounder(x, z, at), runner));
        for (var i = 1; i < probs.Count; i++)
            Assert.True(probs[i] > probs[i - 1], $"深いほど到達確率が上がるはず: {string.Join(",", probs.Select(p => p.ToString("F3")))}");
    }

    [Fact]
    public void Order_Direction_ThreeShortGap_LowerThan_OneTwoGap()
    {
        // ②方向: 同じ深さ（|Z| 等距離）でも三遊間（三塁に近い＝短い送球）は一二塁間より到達確率が明確に低い。
        var runner = new Player { Speed = 50, Baserunning = 50 };
        var threeGap = Grounder(x: -12, z: 40, fieldedAt: 2.0); // 三遊間（三塁側）
        var oneTwoGap = Grounder(x: 12, z: 40, fieldedAt: 2.0);  // 一二塁間（一塁側）
        var pThree = PDirect(threeGap, runner);
        var pOneTwo = PDirect(oneTwoGap, runner);
        Assert.True(pOneTwo > pThree + 0.15,
            $"一二塁間(={pOneTwo:F3})は三遊間(={pThree:F3})より明確に到達しやすいはず（方向性の創発）");
    }

    [Fact]
    public void Order_Runner_FasterRunner_RaisesReachProbability()
    {
        // ③走者: 走力・走塁判断を上げると走者所要が短く＝到達確率が上がる。
        var home = Grounder(x: 16, z: 42, fieldedAt: 2.1);
        var slow = PDirect(home, new Player { Speed = 20, Baserunning = 20 });
        var fast = PDirect(home, new Player { Speed = 90, Baserunning = 90 });
        Assert.True(fast > slow + 0.15, $"快足(={fast:F3})は鈍足(={slow:F3})より到達しやすいはず");
    }

    // ================= 分岐 A（自重・RNG非消費）=================

    [Fact]
    public void BranchA_ShortThrow_HoldsAtSecond_NoRngConsumed()
    {
        // 三遊間の浅いゴロ＝直接送球が短い＝P_direct が閾値未満＝自重（二塁残留）。RNG非消費（不変条件#2）。
        var home = Grounder(x: -14, z: 22, fieldedAt: 1.5, arm: 70);
        var r2 = new Player { Speed = 50, Baserunning = 50 };
        var t = HomeSendDecision.SendAdvanceThreshold(0.5, new TacticsCoefficients());
        Assert.True(PDirect(home, r2) < t, "この捕球点は自重域（P_direct < 閾値）のはず");

        var rng = new Xoshiro256Random(321);
        for (var i = 0; i < 50; i++)
        {
            var (bases, extraOuts, batterSafeOnFc, _) = RunOnce(home, r2, new Player(), rng);
            Assert.Equal(0, extraOuts);         // 自重＝アウトなし
            Assert.False(batterSafeOnFc);       // 打者は一塁アウトのまま（野選でない）
            Assert.Same(r2, bases.Second);      // 二塁残留
            Assert.Null(bases.Third);
        }
        // 50打席が RNG を1つも消費していなければ、次の draw は真新しい rng の先頭と一致する。
        var fresh = new Xoshiro256Random(321);
        Assert.Equal(fresh.NextDouble(), rng.NextDouble(), 15);
    }

    // ================= 分岐 B（一塁を捨てて三塁へ直接送球＝野選）=================

    [Fact]
    public void BranchB_DirectThrow_BatterSafeOnFc_AndStandsAtFirst()
    {
        // 中距離ゴロ＝P_direct が [閾値, max] の帯＝守備は一塁を捨てて三塁へ直接送球。打者は野選で一塁生存。
        var home = Grounder(x: 12, z: 38, fieldedAt: 2.2, arm: 80);
        var r2 = new Player { Speed = 50, Baserunning = 50 };
        var t = HomeSendDecision.SendAdvanceThreshold(0.5, new TacticsCoefficients());
        var pd = PDirect(home, r2);
        Assert.InRange(pd, t, C.AdvanceDirectThrowMaxRunnerSuccess); // 分岐B域を保証

        var batter = new Player();
        var (bases, _, batterSafeOnFc, _) = RunOnce(home, r2, batter, new Xoshiro256Random(5));
        Assert.True(batterSafeOnFc, "分岐B＝野選で打者は生存するはず");
        Assert.Same(batter, bases.First); // 打者は一塁に立つ
    }

    [Fact]
    public void BranchB_DirectThrow_ProducesReachAndRunnerOut()
    {
        // 分岐B: レースは P_direct で解く＝三塁到達と三塁での走塁死の両方が出る。刺殺は extraOuts＋(2→3,Out)。
        var home = Grounder(x: 12, z: 38, fieldedAt: 2.2, arm: 80);
        var r2 = new Player { Speed = 50, Baserunning = 50 };
        var rng = new Xoshiro256Random(11);
        int reached = 0, outs = 0, extra = 0;
        for (var i = 0; i < 4000; i++)
        {
            var (bases, extraOuts, batterSafeOnFc, moves) = RunOnce(home, r2, new Player(), rng);
            Assert.True(batterSafeOnFc); // 分岐Bは常に野選
            reached += moves.Count(m => m is { FromBase: 2, ToBase: 3, Out: false });
            outs += moves.Count(m => m is { FromBase: 2, ToBase: 3, Out: true });
            extra += extraOuts;
        }
        Assert.True(reached > 0, "分岐Bで三塁到達が一度も出ない（レースが機能していない）");
        Assert.True(outs > 0, "分岐Bで三塁での走塁死が一度も出ない（刺殺が起きない）");
        Assert.Equal(outs, extra); // この設定では追加アウトは進塁レースの憤死のみ
    }

    // ================= 分岐 C（一塁を取る＝打者アウト・転送レース）=================

    [Fact]
    public void BranchC_Relay_BatterOut_ProducesReachAndRunnerOut()
    {
        // 深いゴロ＝P_direct が max 超＝守備は一塁を取る（打者アウト）。走者は一塁経由の転送とレース（P_relay）。
        // 鈍足＋早めの一塁送球到達で P_relay を中庸にし、到達と刺殺の両方を出す。
        var home = Grounder(x: 20, z: 48, fieldedAt: 2.4, arm: 55, throwArriveSeconds: 2.9);
        var r2 = new Player { Speed = 25, Baserunning = 25 };
        Assert.True(PDirect(home, r2) > C.AdvanceDirectThrowMaxRunnerSuccess, "この捕球点は分岐C域（P_direct > max）のはず");

        var rng = new Xoshiro256Random(29);
        int reached = 0, outs = 0, extra = 0;
        for (var i = 0; i < 4000; i++)
        {
            var (_, extraOuts, batterSafeOnFc, moves) = RunOnce(home, r2, new Player(), rng);
            Assert.False(batterSafeOnFc); // 分岐Cは打者アウト（野選でない）
            reached += moves.Count(m => m is { FromBase: 2, ToBase: 3, Out: false });
            outs += moves.Count(m => m is { FromBase: 2, ToBase: 3, Out: true });
            extra += extraOuts;
        }
        Assert.True(reached > 0, "分岐Cで三塁到達（進塁打）が一度も出ない");
        Assert.True(outs > 0, "分岐Cで三塁での走塁死が一度も出ない（稀な刺殺が起きない）");
        Assert.Equal(outs, extra);
    }

    [Fact]
    public void BranchC_NullThrowArrive_BloodlessAdvance_NoRngConsumed()
    {
        // 分岐C域だが一塁送球時刻が無い（転送起点が組めない）＝無血進塁（RNG非消費・フォールバック）。
        var home = Grounder(x: 20, z: 48, fieldedAt: 2.4, arm: 55, throwArriveSeconds: null);
        var r2 = new Player { Speed = 25, Baserunning = 25 };
        Assert.True(PDirect(home, r2) > C.AdvanceDirectThrowMaxRunnerSuccess, "分岐C域のはず");

        var rng = new Xoshiro256Random(77);
        for (var i = 0; i < 40; i++)
        {
            var (bases, extraOuts, batterSafeOnFc, _) = RunOnce(home, r2, new Player(), rng);
            Assert.Equal(0, extraOuts);
            Assert.False(batterSafeOnFc);   // 打者は一塁アウト（分岐C）
            Assert.Same(r2, bases.Third);   // 走者は無血で三塁へ
            Assert.Null(bases.Second);
        }
        var fresh = new Xoshiro256Random(77);
        Assert.Equal(fresh.NextDouble(), rng.NextDouble(), 15); // 無血進塁は RNG 非消費
    }

    // ================= 従来テーブルへのフォールバック =================

    [Fact]
    public void Fallback_FlyOut_UsesLegacyTable_Deterministic()
    {
        // フライアウト（IsFly=true）は走者の時計が別物＝物理レース対象外。従来テーブル（productive_out_advance_prob）。
        var flyDeep = Grounder(x: 20, z: 48, fieldedAt: 2.4, arm: 55, throwArriveSeconds: 2.9, isFly: true);
        var r2 = new Player { Speed = 50, Baserunning = 50 };
        var a = CountAdvances(flyDeep, r2, seed: 99);
        var b = CountAdvances(flyDeep, r2, seed: 99);
        Assert.Equal(a, b); // 決定論
        // 18%固定テーブル（走力補正で微増）＝2000試行で概ね 300〜460。物理レース（深い→ほぼ全進塁）とは明確に違う。
        Assert.InRange(a, 250, 500);
    }

    [Fact]
    public void Fallback_NullContext_UsesLegacyTable_Deterministic()
    {
        // home==null（単体テスト・純テーブル経路）は従来テーブル。
        var r2 = new Player { Speed = 50, Baserunning = 50 };
        var rng = new Xoshiro256Random(42);
        var advanced = 0;
        for (var i = 0; i < 2000; i++)
        {
            var bases = new BaseState { Second = r2 };
            BaserunningModel.ApplyDetailed(
                bases, PlateAppearanceResult.InPlayOut, new Player(), 0, C, rng, collectMoves: false, home: null);
            if (bases.Third is not null) advanced++;
        }
        Assert.InRange(advanced, 250, 500); // 18%固定テーブル域
    }

    [Fact]
    public void Fallback_ExtraOutsNonZero_UsesLegacyTable_NotPhysical()
    {
        // extraOuts>0（この打球の送球はもう使われた＝本塁憤死等）では、物理レースへ入らず従来テーブル 18% へ
        // フォールバックする。浅い中央ゴロ＋前進守備で三塁走者を本塁で勝負（送り閾値を超える P_home）させ、
        // r3 が生還した打席（extraOuts==0）と本塁憤死した打席（extraOuts>0）で二塁走者の進塁挙動が変わることで確認。
        // 捕球点は三塁に近い＝物理なら P_direct が閾値未満＝自重（進塁0%・RNG非消費）になる域を選ぶ。
        // ⇒ extraOuts==0 は物理で自重（≈0%）、extraOuts>0 は従来テーブル（≈18%）＝挙動が切り替わる。
        var home = new HomePlayContext(
            Field,
            new HomePlaySituation(new Vector3D(8, 0, 33), 1.5, new Player { ArmStrength = 50 }.ToFielder().ThrowSpeedMps),
            new TacticsCoefficients(), 0.5, DefenseDepth.In, IsFly: false, ThrowArriveSeconds: 2.6);
        var r2 = new Player { Speed = 50, Baserunning = 50 };
        var r3 = new Player { Speed = 50, Baserunning = 50 };
        var tAdvance = HomeSendDecision.SendAdvanceThreshold(0.5, new TacticsCoefficients());
        Assert.True(PDirect(home, r2) < tAdvance, "この捕球点は二塁走者の物理レースでは自重域のはず");

        var rng = new Xoshiro256Random(2024);
        int advOnExtra = 0, nExtra = 0, advOnClean = 0, nClean = 0;
        for (var i = 0; i < 8000; i++)
        {
            var bases = new BaseState { Second = r2, Third = r3 };
            var (_, extraOuts, _, _, _, _) = BaserunningModel.ApplyDetailed(
                bases, PlateAppearanceResult.InPlayOut, new Player(), 0, C, rng, collectMoves: false, home);
            var advanced = bases.Third == r2; // 二塁走者が三塁へ進んだか
            if (extraOuts > 0) { nExtra++; if (advanced) advOnExtra++; }
            else { nClean++; if (advanced) advOnClean++; }
        }
        Assert.True(nExtra > 500 && nClean > 500, $"両区分に十分な試行が必要（extra={nExtra}, clean={nClean}）");
        var rateExtra = (double)advOnExtra / nExtra;   // extraOuts>0＝従来テーブル ≈18%
        var rateClean = (double)advOnClean / nClean;   // extraOuts==0＝物理で自重 ≈0%
        Assert.InRange(rateExtra, 0.10, 0.30);         // 従来テーブル域
        Assert.True(rateClean < 0.02, $"物理は自重域＝ほぼ0%のはず（clean={rateClean:F3}）");
        Assert.True(rateExtra > rateClean + 0.08,
            $"従来テーブル(extra={rateExtra:F2})と物理自重(clean={rateClean:F2})で挙動が切り替わるはず");
    }

    private static int CountAdvances(HomePlayContext home, Player r2, ulong seed)
    {
        var rng = new Xoshiro256Random(seed);
        var advanced = 0;
        for (var i = 0; i < 2000; i++)
        {
            var bases = new BaseState { Second = r2 };
            BaserunningModel.ApplyDetailed(
                bases, PlateAppearanceResult.InPlayOut, new Player(), 0, C, rng, collectMoves: false, home);
            if (bases.Third is not null) advanced++;
        }
        return advanced;
    }
}
