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
/// この場面に至った時点で打者走者は一塁アウトが確定しており（実際の内野は確実な1アウトを捨てて三塁の
/// 走者を直接狙わない＝issue productive-out-second-to-third-not-advancing で「一塁を捨てて三塁へ直接送球」
/// 分岐を廃止）、二塁走者の帰趨は一塁送球到達を起点にした転送レース（P_relay）だけで決まる:
/// A) 転送の脅威が実在する（P_relay &lt; 送り閾値）→ 走者自重（RNG非消費）／
/// B) 脅威が薄い → 走者は三塁へ進み、転送とレース（P_relay）。
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

    /// <summary>一塁送球到達を起点にした転送レースでの走者到達確率（分岐選択に使う純関数の量）。
    /// <see cref="BaserunningModel"/> 内部の P_relay 計算と同じ組み立て（一塁ベース起点・中継速度・
    /// AdvanceRelayTransferSeconds）。</summary>
    private static double PRelay(HomePlayContext home, Player runner)
    {
        Assert.NotNull(home.ThrowArriveSeconds);
        var advance = HomePlayResolver.AdvanceParams(Field, C);
        var relay = new HomePlaySituation(
            Field.FirstBase, home.ThrowArriveSeconds!.Value, C.RelayThrowSpeedMps, home.Situation.OutfielderFielding);
        return HomePlayResolver.SuccessProbability(
            runner, 2, relay, Field, C, advance, C.GrounderAdvanceStartDelaySeconds,
            transferSecondsOverride: C.AdvanceRelayTransferSeconds);
    }

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
    public void Order_ThrowArriveLater_RaisesReachProbability()
    {
        // ①一塁送球到達が遅いほど中継の起点が遅れる＝走者の到達確率が単調に上がる。
        var runner = new Player { Speed = 50, Baserunning = 50 };
        var probs = new List<double>();
        foreach (var arrive in new[] { 1.6, 1.9, 2.2, 2.5 })
            probs.Add(PRelay(Grounder(x: 9, z: 40, fieldedAt: 1.4, throwArriveSeconds: arrive), runner));
        for (var i = 1; i < probs.Count; i++)
            Assert.True(probs[i] > probs[i - 1], $"一塁到達が遅いほど到達確率が上がるはず: {string.Join(",", probs.Select(p => p.ToString("F3")))}");
    }

    [Fact]
    public void Order_Runner_FasterRunner_RaisesReachProbability()
    {
        // ②走者: 走力・走塁判断を上げると走者所要が短く＝到達確率が上がる。
        var home = Grounder(x: 9, z: 40, fieldedAt: 1.4, throwArriveSeconds: 2.1);
        var slow = PRelay(home, new Player { Speed = 20, Baserunning = 20 });
        var fast = PRelay(home, new Player { Speed = 90, Baserunning = 90 });
        Assert.True(fast > slow + 0.15, $"快足(={fast:F3})は鈍足(={slow:F3})より到達しやすいはず");
    }

    // ================= 分岐 A（自重・RNG非消費）=================

    [Fact]
    public void BranchA_FastRelay_HoldsAtSecond_NoRngConsumed()
    {
        // 一塁送球到達が早い（中継の脅威が実在する）＝P_relay が閾値未満＝自重（二塁残留）。RNG非消費（不変条件#2）。
        var home = Grounder(x: 9, z: 40, fieldedAt: 1.2, arm: 70, throwArriveSeconds: 1.7);
        var r2 = new Player { Speed = 50, Baserunning = 50 };
        var t = HomeSendDecision.SendAdvanceThreshold(0.5, new TacticsCoefficients());
        Assert.True(PRelay(home, r2) < t, "この一塁到達は自重域（P_relay < 閾値）のはず");

        var rng = new Xoshiro256Random(321);
        for (var i = 0; i < 50; i++)
        {
            var (bases, extraOuts, batterSafeOnFc, _) = RunOnce(home, r2, new Player(), rng);
            Assert.Equal(0, extraOuts);         // 自重＝アウトなし
            Assert.False(batterSafeOnFc);       // 打者は一塁アウトのまま
            Assert.Same(r2, bases.Second);      // 二塁残留
            Assert.Null(bases.Third);
        }
        // 50打席が RNG を1つも消費していなければ、次の draw は真新しい rng の先頭と一致する。
        var fresh = new Xoshiro256Random(321);
        Assert.Equal(fresh.NextDouble(), rng.NextDouble(), 15);
    }

    // ================= 分岐 B（一塁経由の転送レース＝進塁打の既定形）=================

    [Fact]
    public void BranchB_RoutineGrounder_BatterOut_RunnerAdvancesUsually()
    {
        // 標準的な二塁手守備位置・現実的な捕球/送球到達時刻（issue productive-out-second-to-third-not-advancing の
        // 実測レンジ）＝定型的な進塁打のはず。打者は常にアウト（野選にならない）、走者はほとんどのケースで三塁へ。
        var home = Grounder(x: 9, z: 40, fieldedAt: 1.4, arm: 50, throwArriveSeconds: 3.0);
        var r2 = new Player { Speed = 50, Baserunning = 50 };
        var t = HomeSendDecision.SendAdvanceThreshold(0.5, new TacticsCoefficients());
        Assert.True(PRelay(home, r2) >= t, "標準的な二塁ゴロは進塁を試みる域のはず");

        var rng = new Xoshiro256Random(11);
        int reached = 0, held = 0, outs = 0;
        const int trials = 2000;
        for (var i = 0; i < trials; i++)
        {
            var (bases, extraOuts, batterSafeOnFc, moves) = RunOnce(home, r2, new Player(), rng);
            Assert.False(batterSafeOnFc); // 打者は常に一塁アウト（野選にならない）
            if (bases.Third == r2) reached++;
            else if (bases.Second == r2) held++;
            else outs += extraOuts; // 三塁での走塁死
        }
        Assert.True(reached > trials * 0.7, $"標準的な二塁ゴロは大半が進塁するはず（到達={reached}/{trials}）");
    }

    [Fact]
    public void BranchB_Relay_ProducesReachAndRunnerOut()
    {
        // 中継が際どくなる送球到達（拮抗域）＝三塁到達と三塁での走塁死の両方が出る。刺殺は extraOuts＋(2→3,Out)。
        var home = Grounder(x: 9, z: 40, fieldedAt: 1.4, arm: 50, throwArriveSeconds: 2.0);
        var r2 = new Player { Speed = 50, Baserunning = 50 };
        var rng = new Xoshiro256Random(29);
        int reached = 0, outs = 0, extra = 0;
        for (var i = 0; i < 4000; i++)
        {
            var (_, extraOuts, batterSafeOnFc, moves) = RunOnce(home, r2, new Player(), rng);
            Assert.False(batterSafeOnFc); // 打者は常に一塁アウト
            reached += moves.Count(m => m is { FromBase: 2, ToBase: 3, Out: false });
            outs += moves.Count(m => m is { FromBase: 2, ToBase: 3, Out: true });
            extra += extraOuts;
        }
        Assert.True(reached > 0, "三塁到達が一度も出ない（レースが機能していない）");
        Assert.True(outs > 0, "三塁での走塁死が一度も出ない（刺殺が起きない）");
        Assert.Equal(outs, extra); // この設定では追加アウトは進塁レースの憤死のみ
    }

    [Fact]
    public void NullThrowArrive_BloodlessAdvance_NoRngConsumed()
    {
        // 一塁送球到達時刻が無い（転送起点が組めない）＝無血進塁（RNG非消費・フォールバック）。
        var home = Grounder(x: 20, z: 48, fieldedAt: 2.4, arm: 55, throwArriveSeconds: null);
        var r2 = new Player { Speed = 25, Baserunning = 25 };

        var rng = new Xoshiro256Random(77);
        for (var i = 0; i < 40; i++)
        {
            var (bases, extraOuts, batterSafeOnFc, _) = RunOnce(home, r2, new Player(), rng);
            Assert.Equal(0, extraOuts);
            Assert.False(batterSafeOnFc);   // 打者は一塁アウト
            Assert.Same(r2, bases.Third);   // 走者は無血で三塁へ
            Assert.Null(bases.Second);
        }
        var fresh = new Xoshiro256Random(77);
        Assert.Equal(fresh.NextDouble(), rng.NextDouble(), 15); // 無血進塁は RNG 非消費
    }

    // ================= 従来テーブルへのフォールバック =================

    [Fact]
    public void FlyOut_UsesTagUpRace_NotLegacyTable_Deterministic()
    {
        // フライアウト（IsFly=true）はゴロの転送レースではなくタッチアップの物理レースで解く
        // （design-14 P3-11。かつては走者の時計が別物という理由で18%固定テーブルへ落としていた）。
        // この浅いフライ（48m）は三塁への送球が短く到達見込みが送り閾値に届かない＝全試行で自重する。
        // 18%固定テーブルのままなら 2000試行で 300〜460 程度進んでいた。深さ依存の網羅は FlyTagUpTests。
        var flyShallow = Grounder(x: 20, z: 48, fieldedAt: 2.4, arm: 55, throwArriveSeconds: 2.9, isFly: true);
        var r2 = new Player { Speed = 50, Baserunning = 50 };
        var a = CountAdvances(flyShallow, r2, seed: 99);
        var b = CountAdvances(flyShallow, r2, seed: 99);
        Assert.Equal(a, b); // 決定論
        Assert.Equal(0, a);
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
        // 一塁送球到達が早い＝物理なら P_relay が閾値未満＝自重（進塁0%・RNG非消費）になる域を選ぶ。
        // ⇒ extraOuts==0 は物理で自重（≈0%）、extraOuts>0 は従来テーブル（≈18%）＝挙動が切り替わる。
        var home = new HomePlayContext(
            Field,
            new HomePlaySituation(new Vector3D(8, 0, 33), 1.5, new Player { ArmStrength = 50 }.ToFielder().ThrowSpeedMps),
            new TacticsCoefficients(), 0.5, DefenseDepth.In, IsFly: false, ThrowArriveSeconds: 1.9);
        var r2 = new Player { Speed = 50, Baserunning = 50 };
        var r3 = new Player { Speed = 50, Baserunning = 50 };
        var tAdvance = HomeSendDecision.SendAdvanceThreshold(0.5, new TacticsCoefficients());
        Assert.True(PRelay(home, r2) < tAdvance, "この一塁到達は二塁走者の物理レースでは自重域のはず");

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
