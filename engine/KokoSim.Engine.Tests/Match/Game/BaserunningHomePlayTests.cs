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
/// F2 配線（設計書12 §3, Slice C）: 本塁生還が確率テーブルから「送り判定＋時間の勝負」に切り替わる。
/// null コンテキストは従来テーブルへフォールバック（決定論・後方互換）。
/// </summary>
public sealed class BaserunningHomePlayTests
{
    private static readonly BaserunningCoefficients C = new();
    private static readonly FieldGeometry Field = new();

    private static HomePlayContext Context(
        double distanceM, double fieldedAt, int arm, double aggression,
        DefenseDepth depth = DefenseDepth.Normal, bool isFly = false)
    {
        var situation = new HomePlaySituation(
            new Vector3D(0, 0, distanceM), fieldedAt, new Player { ArmStrength = arm }.ToFielder().ThrowSpeedMps);
        return new HomePlayContext(Field, situation, new TacticsCoefficients(), aggression, depth, isFly);
    }

    [Fact]
    public void WithHomeContext_ContestedPlay_ProducesBothScoresAndHomeOuts()
    {
        // 接戦帯（浅い当たり・積極送り）を多数試行 → 生還と憤死の両方が出る。憤死は追加アウト＋本塁Outレッグ。
        var home = Context(distanceM: 15, fieldedAt: 0.7, arm: 60, aggression: 1.0);
        var rng = new Xoshiro256Random(5);
        int scored = 0, homeOuts = 0, outMoves = 0;
        for (var i = 0; i < 2000; i++)
        {
            var bases = new BaseState { Third = new Player { Speed = 50, Baserunning = 50 } };
            var (runs, extraOuts, _, _, _, moves) = BaserunningModel.ApplyDetailed(
                bases, PlateAppearanceResult.Single, new Player(), 0, C, rng, collectMoves: true, home);
            scored += runs;
            homeOuts += extraOuts;
            outMoves += moves.Count(m => m is { FromBase: 3, ToBase: 4, Out: true });
        }
        Assert.True(scored > 0, "本塁生還が一度も出ない");
        Assert.True(homeOuts > 0, "本塁憤死が一度も出ない（送球 vs 走者が機能していない）");
        Assert.Equal(homeOuts, outMoves); // 追加アウトは本塁Outレッグと一致
    }

    [Fact]
    public void NullContext_ThirdRunner_AlwaysScores_NoOuts()
    {
        // 回帰: home=null は従来テーブル＝三塁走者は単打で無条件生還・アウトは出ない。
        var rng = new Xoshiro256Random(5);
        int totalRuns = 0, totalOuts = 0;
        for (var i = 0; i < 500; i++)
        {
            var bases = new BaseState { Third = new Player { Baserunning = 50 } };
            var (runs, extraOuts, _, _, _, _) = BaserunningModel.ApplyDetailed(
                bases, PlateAppearanceResult.Single, new Player(), 0, C, rng, collectMoves: true, home: null);
            totalRuns += runs;
            totalOuts += extraOuts;
        }
        Assert.Equal(500, totalRuns); // 毎回 三塁走者が生還
        Assert.Equal(0, totalOuts);   // 安打で走塁アウトは出ない
    }

    [Fact]
    public void SafeDeepPlay_RunnerScores_OverwhelminglySafe()
    {
        // 深い当たり（長い送球・遅い処理）は三塁走者が悠々生還。生還確率は上限0.99クランプなので
        // ごく稀（≈1%）に刺されるが、圧倒的多数は生還する。
        var home = Context(distanceM: 100, fieldedAt: 3.8, arm: 50, aggression: 0.5);
        var rng = new Xoshiro256Random(1);
        int runsTotal = 0;
        for (var i = 0; i < 500; i++)
        {
            var bases = new BaseState { Third = new Player { Speed = 50, Baserunning = 50 } };
            var (runs, _, _, _, _, _) = BaserunningModel.ApplyDetailed(
                bases, PlateAppearanceResult.Single, new Player(), 0, C, rng, collectMoves: false, home);
            runsTotal += runs;
        }
        Assert.True(runsTotal >= 485, $"深い当たりで生還が少なすぎる: {runsTotal}/500");
    }

    // --- G1: 内野深さ×ゴロ×本塁（設計書12 §4/§5, 深さの相互参照） ---

    // 内野ゴロ（処理点35m・肩50）の三塁走者。前進は浅く処理（fielded早い）を模す。
    private static HomePlayContext Grounder(DefenseDepth depth)
    {
        var fielded = depth == DefenseDepth.In ? 1.3 : depth == DefenseDepth.Deep ? 1.7 : 1.5;
        return new(Field,
            new HomePlaySituation(new Vector3D(0, 0, 35), fielded, new Player { ArmStrength = 50 }.ToFielder().ThrowSpeedMps),
            new TacticsCoefficients(), 0.5, depth, IsFly: false);
    }

    private static (int runs, int homeOuts) GrounderR3(DefenseDepth depth, Player runner, int trials = 3000, ulong seed = 5)
    {
        var rng = new Xoshiro256Random(seed);
        int runs = 0, homeOuts = 0;
        for (var i = 0; i < trials; i++)
        {
            var bases = new BaseState { Third = runner };
            var (r, _, baseOuts, _, _, _) = BaserunningModel.ApplyDetailed(
                bases, PlateAppearanceResult.InPlayOut, new Player(), 0, C, rng, collectMoves: false, Grounder(depth));
            runs += r; homeOuts += baseOuts.Home;
        }
        return (runs, homeOuts);
    }

    [Fact]
    public void InfieldDepth_OrdersGrounderScoring_DeepConcedes_InSuppresses()
    {
        var avg = new Player { Speed = 50, Baserunning = 50 };
        var deep = GrounderR3(DefenseDepth.Deep, avg);
        var normal = GrounderR3(DefenseDepth.Normal, avg);
        var inn = GrounderR3(DefenseDepth.In, avg);

        Assert.Equal(3000, deep.runs);       // 後退＝献上＝無条件生還
        Assert.Equal(0, deep.homeOuts);
        Assert.InRange(normal.runs, 600, 1500); // 通常＝従来テーブル(SacFlyScoreProb≈0.30)、憤死なし
        Assert.Equal(0, normal.homeOuts);
        // 前進＝本塁で勝負。平均走者は自重が多く生還を抑える（通常より下）＝AIの前進が報われる。
        Assert.True(inn.runs < normal.runs, $"内野前進が生還を抑えていない: In={inn.runs} Normal={normal.runs}");
    }

    [Fact]
    public void InfieldIn_FastRunner_ChallengesAndCanBeGunnedDown()
    {
        // 快足走者は内野前進でも本塁へ突入＝生還も憤死も出る（際どいクロスプレーのドラマ）。
        var fast = new Player { Speed = 85, Baserunning = 80 };
        var (runs, homeOuts) = GrounderR3(DefenseDepth.In, fast);
        Assert.True(runs > 0, "快足でも一度も生還しない");
        Assert.True(homeOuts > 0, "内野前進で本塁憤死(タッチアウト)が一度も出ない");
    }

    // --- G2: ライナー併殺（コンタクト始動の一塁走者が空中で捕られ戻れない, 設計書12 §4） ---

    // 接戦帯のライナー捕球（処理点15m・肩30・滞空0.5s）。一塁走者ありの内野ゴロ凡打を想定。
    private static HomePlayContext LinerCatch(bool isFly)
        => new(Field,
            new HomePlaySituation(new Vector3D(0, 0, 15), 0.5, new Player { ArmStrength = 30 }.ToFielder().ThrowSpeedMps),
            new TacticsCoefficients(), 0.5, DefenseDepth.Normal, IsFly: isFly);

    private static (int extraOuts, int linerOutMoves) ContactR1OnFly(
        StartType r1Start, bool isFly, int trials = 3000, ulong seed = 5)
    {
        var rng = new Xoshiro256Random(seed);
        var extraOuts = 0;
        var linerOutMoves = 0;
        for (var i = 0; i < trials; i++)
        {
            var bases = new BaseState { First = new Player { Speed = 50 } };
            var (_, outs, _, _, _, moves) = BaserunningModel.ApplyDetailed(
                bases, PlateAppearanceResult.InPlayOut, new Player(), 0, C, rng,
                collectMoves: true, LinerCatch(isFly), r1Start);
            extraOuts += outs;
            linerOutMoves += moves.Count(m => m is { FromBase: 1, ToBase: 1, Out: true });
        }
        return (extraOuts, linerOutMoves);
    }

    [Fact]
    public void ContactStart_CaughtLiner_CanDoubleOffTheRunner()
    {
        // extraOuts には既存の一般併殺(DoublePlayProb, r1の有無だけで独立に発生)も混ざるため、
        // 新メカニズム固有の判定は「一塁へ戻れず」のレッグ数(linerOutMoves)で見る。
        var (_, linerOutMoves) = ContactR1OnFly(StartType.Contact, isFly: true);
        Assert.True(linerOutMoves > 0, "コンタクト始動のライナー併殺が一度も出ない");
    }

    [Fact]
    public void NormalStart_SameCatch_NeverDoublesOff()
    {
        // 通常スタート（エンドラン無し）は同じ捕球でも戻る余裕がある前提＝この追加リスクの対象外。
        // extraOuts自体は既存の一般併殺で非ゼロになり得るため、新メカニズム固有のレッグ数だけを見る。
        var (_, linerOutMoves) = ContactR1OnFly(StartType.Normal, isFly: true);
        Assert.Equal(0, linerOutMoves);
    }

    [Fact]
    public void ContactStart_GroundBall_NeverTriggersLinerDoubleOff()
    {
        // ゴロ（IsFly:false）は対象外。一般併殺(DoublePlayProb)以外の追加アウトは出ない。
        var (_, linerOutMoves) = ContactR1OnFly(StartType.Contact, isFly: false);
        Assert.Equal(0, linerOutMoves);
    }

    // --- #87: 満塁の単打で二塁走者が消失するバグの回帰 ---
    // (1) newSecond/newThird の2変数上書き (2) 安打側のフォース進塁の欠落、の両方を検出する。

    [Fact]
    public void BasesLoaded_Single_ForcedRunners_NoneVanish_ThirdCannotHoldAtThird()
    {
        // 三塁への送球が有利（近距離・速い処理・強肩）＋超慎重(aggression=0)＝二塁走者は本来なら自重したい
        // 状況。しかし満塁なのでフォース: 三塁走者は自重不可（無条件生還）、二塁走者は自重できず三塁へ、
        // 一塁走者は二塁へ確定する。誰も消えない。
        var home = Context(distanceM: 10, fieldedAt: 0.3, arm: 80, aggression: 0.0);
        var rng = new Xoshiro256Random(5);
        var r1 = new Player { Name = "R1", Baserunning = 50 };
        var r2 = new Player { Name = "R2", Baserunning = 50 };
        var r3 = new Player { Name = "R3", Baserunning = 50 };
        var batter = new Player { Name = "Batter" };
        var bases = new BaseState { First = r1, Second = r2, Third = r3 };

        var (runs, extraOuts, _, _, _, moves) = BaserunningModel.ApplyDetailed(
            bases, PlateAppearanceResult.Single, batter, 0, C, rng, collectMoves: true, home);

        Assert.Equal(1, runs);       // 三塁走者(フォース)は自重できず無条件生還
        Assert.Equal(0, extraOuts);
        Assert.Equal(3, bases.RunnerCount); // 誰も消えない: 二塁走者・一塁走者・打者が塁上に残る
        Assert.Same(r2, bases.Third);   // 二塁走者は自重できず三塁へ押し出される
        Assert.Same(r1, bases.Second);  // 一塁走者は二塁へ確定
        Assert.Same(batter, bases.First);
        Assert.Contains(moves, m => m.Runner == r3 && m.FromBase == 3 && m.ToBase == 4 && !m.Out);
        Assert.Contains(moves, m => m.Runner == r2 && m.FromBase == 2 && m.ToBase == 3 && !m.Out);
        Assert.Contains(moves, m => m.Runner == r1 && m.FromBase == 1 && m.ToBase == 2 && !m.Out);
    }

    // --- #87 完了条件: 打席の前後で 走者数 + 得点 + アウト が保存される（走者を落とさない） ---

    [Fact]
    public void RunnerAdvance_ConservesRunnerCountAcrossRandomConfigurations()
    {
        var rng = new Xoshiro256Random(2026);
        var results = new[]
        {
            PlateAppearanceResult.Single, PlateAppearanceResult.Double, PlateAppearanceResult.ReachedOnError,
        };

        for (var trial = 0; trial < 5000; trial++)
        {
            var r1 = rng.NextDouble() < 0.5 ? new Player { Name = "R1", Baserunning = rng.NextInt(20, 90) } : null;
            var r2 = rng.NextDouble() < 0.5 ? new Player { Name = "R2", Baserunning = rng.NextInt(20, 90) } : null;
            var r3 = rng.NextDouble() < 0.5 ? new Player { Name = "R3", Baserunning = rng.NextInt(20, 90) } : null;
            var batter = new Player { Name = "Batter" };
            var bases = new BaseState { First = r1, Second = r2, Third = r3 };
            var preRunners = bases.RunnerCount;

            HomePlayContext? home = rng.NextDouble() < 0.5
                ? Context(
                    distanceM: rng.NextInt(10, 100), fieldedAt: 0.3 + rng.NextDouble() * 3.0,
                    arm: rng.NextInt(30, 95), aggression: rng.NextDouble())
                : null;
            var result = results[rng.NextInt(0, results.Length)];

            var (runs, extraOuts) = BaserunningModel.Apply(bases, result, batter, currentOuts: 0, C, rng, home);

            var onBase = new[] { bases.First, bases.Second, bases.Third }.Where(p => p is not null).ToList();
            Assert.True(onBase.Count == onBase.Distinct().Count(), "同じ走者が複数の塁に重複している");
            Assert.Equal(preRunners + 1, onBase.Count + runs + extraOuts); // 元の走者+打者 = 塁上+得点+アウト
        }
    }

    // --- #88: 二塁打で一塁走者が一塁に残ってしまうバグの回帰 ---
    // 打者は二塁打で必ず二塁を占有するため、一塁走者は一塁にも二塁にも戻れない。三塁が先行走者(自重)
    // で塞がっている場合は、その先行走者を本塁へ押し出して一塁走者が三塁に入る（フォースの延長）。

    [Fact]
    public void Double_FirstAndSecondRunner_BothHold_SecondRunnerPushedHome_FirstTakesThird()
    {
        // 近距離・速い処理・強肩・超慎重＝二塁走者・一塁走者ともに本塁は自重したい状況。
        var home = Context(distanceM: 10, fieldedAt: 0.3, arm: 80, aggression: 0.0);
        var rng = new Xoshiro256Random(5);
        var r1 = new Player { Name = "R1", Baserunning = 50 };
        var r2 = new Player { Name = "R2", Baserunning = 50 };
        var batter = new Player { Name = "Batter" };
        var bases = new BaseState { First = r1, Second = r2 };

        var (runs, extraOuts, _, _, _, moves) = BaserunningModel.ApplyDetailed(
            bases, PlateAppearanceResult.Double, batter, 0, C, rng, collectMoves: true, home);

        Assert.Equal(1, runs);        // 二塁走者(先行)は三塁を明け渡すため押し出され本塁へ
        Assert.Equal(0, extraOuts);
        Assert.Null(bases.First);     // 一塁に残る結末は無い（#88の症状そのもの）
        Assert.Same(batter, bases.Second);
        Assert.Same(r1, bases.Third); // 一塁走者は三塁まで進む
        Assert.Single(moves, m => ReferenceEquals(m.Runner, r2)); // r2のmoveは1本(2→4)。2→3→4の2段階にしない
        Assert.Contains(moves, m => m.Runner == r2 && m.FromBase == 2 && m.ToBase == 4 && !m.Out);
        Assert.Contains(moves, m => m.Runner == r1 && m.FromBase == 1 && m.ToBase == 3 && !m.Out);
        Assert.Contains(moves, m => m.Runner == batter && m.FromBase == 0 && m.ToBase == 2 && !m.Out);
    }

    [Fact]
    public void Double_FirstAndThirdRunner_BothHold_ThirdRunnerPushedHome_FirstTakesThird()
    {
        // 二塁走者がいなくても、三塁走者(自重)が塞いでいれば同じ押し出しが起きる。
        var home = Context(distanceM: 10, fieldedAt: 0.3, arm: 80, aggression: 0.0);
        var rng = new Xoshiro256Random(5);
        var r1 = new Player { Name = "R1", Baserunning = 50 };
        var r3 = new Player { Name = "R3", Baserunning = 50 };
        var batter = new Player { Name = "Batter" };
        var bases = new BaseState { First = r1, Third = r3 };

        var (runs, extraOuts, _, _, _, moves) = BaserunningModel.ApplyDetailed(
            bases, PlateAppearanceResult.Double, batter, 0, C, rng, collectMoves: true, home);

        Assert.Equal(1, runs);
        Assert.Equal(0, extraOuts);
        Assert.Null(bases.First);
        Assert.Same(batter, bases.Second);
        Assert.Same(r1, bases.Third);
        Assert.Contains(moves, m => m.Runner == r3 && m.FromBase == 3 && m.ToBase == 4 && !m.Out);
        Assert.Contains(moves, m => m.Runner == r1 && m.FromBase == 1 && m.ToBase == 3 && !m.Out);
    }

    // --- #88 完了条件: RunnerMove の最終legの行き先と最終 BaseState が常に一致する ---

    [Fact]
    public void RunnerMoves_LastLegPerRunner_MatchesFinalBaseStateOrScoring()
    {
        var rng = new Xoshiro256Random(880088);
        var results = new[]
        {
            PlateAppearanceResult.Single, PlateAppearanceResult.Double, PlateAppearanceResult.ReachedOnError,
        };

        for (var trial = 0; trial < 5000; trial++)
        {
            var r1 = rng.NextDouble() < 0.5 ? new Player { Name = "R1", Baserunning = rng.NextInt(20, 90) } : null;
            var r2 = rng.NextDouble() < 0.5 ? new Player { Name = "R2", Baserunning = rng.NextInt(20, 90) } : null;
            var r3 = rng.NextDouble() < 0.5 ? new Player { Name = "R3", Baserunning = rng.NextInt(20, 90) } : null;
            var batter = new Player { Name = "Batter" };
            var bases = new BaseState { First = r1, Second = r2, Third = r3 };

            HomePlayContext? home = rng.NextDouble() < 0.5
                ? Context(
                    distanceM: rng.NextInt(10, 100), fieldedAt: 0.3 + rng.NextDouble() * 3.0,
                    arm: rng.NextInt(30, 95), aggression: rng.NextDouble())
                : null;
            var result = results[rng.NextInt(0, results.Length)];

            var (_, _, _, _, _, moves) = BaserunningModel.ApplyDetailed(
                bases, result, batter, currentOuts: 0, C, rng, collectMoves: true, home);

            var runnersWithOrigin = new (Player? Runner, int OriginBase)[]
                { (r1, 1), (r2, 2), (r3, 3), (batter, 0) };

            foreach (var (runner, originBase) in runnersWithOrigin)
            {
                if (runner is null) continue;
                var lastLeg = moves.LastOrDefault(m => ReferenceEquals(m.Runner, runner));
                int? finalBase = ReferenceEquals(bases.First, runner) ? 1
                    : ReferenceEquals(bases.Second, runner) ? 2
                    : ReferenceEquals(bases.Third, runner) ? 3
                    : null;

                if (lastLeg is null)
                {
                    Assert.True(originBase == finalBase,
                        $"move記録が無いのに元の塁からいなくなった: {runner.Name} origin={originBase} final={finalBase}");
                    continue;
                }
                if (lastLeg.Out || lastLeg.ToBase == 4)
                {
                    Assert.True(finalBase is null,
                        $"得点/アウトのはずが塁上に残っている: {runner.Name} final={finalBase}");
                }
                else
                {
                    Assert.True(lastLeg.ToBase == finalBase,
                        $"最終move(ToBase={lastLeg.ToBase})と塁状態(final={finalBase})が不一致: {runner.Name}");
                }
            }
        }
    }
}
