using System.Collections.Generic;
using System.Linq;
using KokoSim.Engine.Core;
using KokoSim.Engine.Match.AtBat;
using KokoSim.Engine.Match.Field;
using KokoSim.Engine.Match.Fielding;
using KokoSim.Engine.Match.Game;
using KokoSim.Engine.Match.Tactics;
using KokoSim.Engine.Match.Timeline;
using KokoSim.Engine.Players;
using Xunit;

namespace KokoSim.Engine.Tests.Match.Timeline;

/// <summary>
/// フォース／タッチプレーの表示専用フラグ（issue #489）。<see cref="RunnerMove.Force"/> /
/// <see cref="RunnerLeg.Force"/> が「一塁封殺・封殺の二塁・満塁の本塁封殺」の3種でだけ true になり、
/// それ以外（本塁クロスプレー・盗塁死・牽制死・進塁レースでの走塁死）は既定の false（タッチ）のまま
/// であることを担保する。判定・帯には一切影響しない表示専用の契約テスト。
/// </summary>
public sealed class ForceTagDisplayTests
{
    private static readonly BaserunningCoefficients C = new();
    private static readonly FieldGeometry Field = new();

    // ---- 打者走者の一塁（常にフォース） ----

    private static FieldingPlay GrounderOut() => new()
    {
        Result = BattedBallResult.Out,
        LandingX = -9.0, LandingZ = 30.0, HangTimeSeconds = 0.3, ApexHeightM = 0.4,
        RangeM = 31.3, BearingDeg = -16.0, FielderRole = FieldPosition.Shortstop,
        FieldedAtSeconds = 1.4, ThrowArriveSeconds = 3.0, BatterToFirstSeconds = 4.2, IsFly = false,
    };

    [Fact]
    public void BatterGroundOut_AtFirst_IsForce()
    {
        var play = GrounderOut();
        var tl = TimelineBuilder.BuildBattedBall(
            play, Field.StandardAlignment(), Field, null, runnersOn: false, TimelineBuilder.DescribeResult(play));
        // 内野ゴロのアウトは主レッグ＋一塁駆け抜け（オーバーラン, issue #443）の2本が同じ Label="打" で並ぶ。
        // フォース/アウトの判定に意味があるのは主レッグ（OutAtEnd を持つ方）だけ。
        var leg = tl.Runners.Single(r => r.Label == "打" && r.OutAtEnd);
        Assert.True(leg.Force, "打者走者の一塁封殺はフォースのはず（タッチの芝居を出さない）");
    }

    // ---- 併殺・野選の二塁封殺（常にフォース） ----

    private static HomePlayContext PhysicalRaceDoublePlayGrounder()
    {
        // DoublePlayRaceTests.NearSecond_SlowBatter_ProducesDoublePlay と同じ設定
        // （二遊間の浅いゴロ＋鈍足打者＝二塁封殺が常に成立）。
        var situation = new HomePlaySituation(
            new Vector3D(-6, 0, 37), 1.4, new Player { ArmStrength = 55 }.ToFielder().ThrowSpeedMps);
        return new HomePlayContext(
            Field, situation, new TacticsCoefficients(), 0.5, DefenseDepth.Normal, IsFly: false,
            ThrowArriveSeconds: null, BatterToFirstSeconds: 4.60);
    }

    [Fact]
    public void DoublePlay_PhysicalRace_ForceOutAtSecond_IsForce()
    {
        var rng = new Xoshiro256Random(101);
        var home = PhysicalRaceDoublePlayGrounder();
        int found = 0;
        for (var i = 0; i < 200; i++)
        {
            var bases = new BaseState { First = new Player { Speed = 50, Baserunning = 50 } };
            var (_, _, _, _, _, moves) = BaserunningModel.ApplyDetailed(
                bases, PlateAppearanceResult.InPlayOut, new Player(), 1, C, rng, collectMoves: true, home);
            foreach (var m in moves.Where(m => m is { FromBase: 1, ToBase: 2, Out: true }))
            {
                found++;
                Assert.True(m.Force, "物理レースの二塁封殺はフォースのはず");
            }
        }
        Assert.True(found > 0, "二塁封殺の RunnerMove が1件も観測されない");
    }

    [Fact]
    public void DoublePlay_TableFallback_ForceOutAtSecond_IsForce()
    {
        // home=null（従来テーブル経路）でも DoublePlayProb / FieldersChoiceProb の二塁封殺は
        // 同じくフォース（design-14 以前からの旧経路。単体テスト/純テーブル経路のフォールバック）。
        var rng = new Xoshiro256Random(303);
        int found = 0;
        for (var i = 0; i < 500; i++)
        {
            var bases = new BaseState { First = new Player { Speed = 0, Baserunning = 0 } };
            var (_, _, _, _, _, moves) = BaserunningModel.ApplyDetailed(
                bases, PlateAppearanceResult.InPlayOut, new Player(), 1, C, rng, collectMoves: true, home: null);
            foreach (var m in moves.Where(m => m is { FromBase: 1, ToBase: 2, Out: true }))
            {
                found++;
                Assert.True(m.Force, "旧テーブル経路の二塁封殺もフォースのはず");
            }
        }
        Assert.True(found > 0, "旧テーブル経路で二塁封殺の RunnerMove が1件も観測されない");
    }

    // ---- 満塁ゴロ×内野前進の本塁封殺（フォース） ----

    private static HomePlayContext BasesLoadedInfieldIn() => new(
        Field, new HomePlaySituation(new Vector3D(0, 0, 35), 1.3, new Player { ArmStrength = 50 }.ToFielder().ThrowSpeedMps),
        new TacticsCoefficients(), 0.5, DefenseDepth.In, IsFly: false);

    [Fact]
    public void BasesLoaded_InfieldIn_ForceOutAtHome_IsForce()
    {
        var rng = new Xoshiro256Random(14);
        var home = BasesLoadedInfieldIn();
        int found = 0;
        for (var i = 0; i < 1000; i++)
        {
            var bases = new BaseState
            {
                First = new Player(), Second = new Player(),
                Third = new Player { Speed = 50, Baserunning = 50 },
            };
            var (_, _, _, _, _, moves) = BaserunningModel.ApplyDetailed(
                bases, PlateAppearanceResult.InPlayOut, new Player(), 0, C, rng, collectMoves: true, home);
            foreach (var m in moves.Where(m => m is { FromBase: 3, ToBase: 4, Out: true }))
            {
                found++;
                Assert.True(m.Force, "満塁×内野前進の本塁封殺はフォースのはず");
            }
        }
        Assert.True(found > 0, "満塁の本塁封殺 RunnerMove が1件も観測されない");
    }

    // ---- 本塁クロスプレー（非フォース＝タッチ） ----

    [Fact]
    public void HomeCrossPlay_NotForced_OutAtHome_IsTag()
    {
        // 三塁走者のみ（一・二塁は空）＝フォースではない、単打の本塁突入（F2の時間の勝負）。
        var home = new HomePlayContext(
            Field, new HomePlaySituation(new Vector3D(0, 0, 15), 0.7, new Player { ArmStrength = 60 }.ToFielder().ThrowSpeedMps),
            new TacticsCoefficients(), 1.0, DefenseDepth.Normal, IsFly: false);
        var rng = new Xoshiro256Random(5);
        int found = 0;
        for (var i = 0; i < 2000; i++)
        {
            var bases = new BaseState { Third = new Player { Speed = 50, Baserunning = 50 } };
            var (_, _, _, _, _, moves) = BaserunningModel.ApplyDetailed(
                bases, PlateAppearanceResult.Single, new Player(), 0, C, rng, collectMoves: true, home);
            foreach (var m in moves.Where(m => m is { FromBase: 3, ToBase: 4, Out: true }))
            {
                found++;
                Assert.False(m.Force, "非フォースの本塁クロスプレーはタッチプレーのはず");
            }
        }
        Assert.True(found > 0, "本塁憤死の RunnerMove が1件も観測されない");
    }

    // ---- 進塁レース（三塁での走塁死・非フォース＝タッチ） ----

    [Fact]
    public void GrounderAdvanceRace_ThirdBaseOut_IsTag()
    {
        // 二塁走者のみ（一塁空＝非フォース）の内野ゴロで三塁を狙って刺される物理レース（Issue #318）。
        var home = new HomePlayContext(
            Field, new HomePlaySituation(new Vector3D(-24, 0, 22), 2.4, new Player { ArmStrength = 70 }.ToFielder().ThrowSpeedMps),
            new TacticsCoefficients(), 1.0, DefenseDepth.Normal, IsFly: false,
            ThrowArriveSeconds: null, BatterToFirstSeconds: 4.2);
        var rng = new Xoshiro256Random(318);
        int found = 0;
        for (var i = 0; i < 2000; i++)
        {
            var bases = new BaseState { Second = new Player { Speed = 80, Baserunning = 80 } };
            var (_, _, _, _, _, moves) = BaserunningModel.ApplyDetailed(
                bases, PlateAppearanceResult.InPlayOut, new Player(), 0, C, rng, collectMoves: true, home);
            foreach (var m in moves.Where(m => m is { FromBase: 2, ToBase: 3, Out: true }))
            {
                found++;
                Assert.False(m.Force, "非フォースの三塁進塁レースはタッチプレーのはず");
            }
        }
        Assert.True(found > 0, "三塁での走塁死 RunnerMove が1件も観測されない（帯・設定を見直す）");
    }

    // ---- 盗塁死・牽制死（フォース概念が無い＝常にタッチ） ----

    [Fact]
    public void StealTimeline_CaughtStealing_IsTag()
    {
        var runner = new Player { Speed = 80, Steal = 75 };
        var catcher = new Player { Position = FieldPosition.Catcher, ArmStrength = 60 };
        var tl = TimelineBuilder.BuildSteal(runner, catcher, StealResult.CaughtStealing, C, Field);
        var leg = Assert.Single(tl.Runners);
        Assert.True(leg.OutAtEnd);
        Assert.False(leg.Force, "盗塁死はタッチプレーのはず");
    }

    [Fact]
    public void PickoffTimeline_PickedOff_IsTag()
    {
        var runner = new Player { Speed = 70, Steal = 60 };
        var pitcher = new Player { Position = FieldPosition.Pitcher, ArmStrength = 55, Mental = 50 };
        var tl = TimelineBuilder.BuildPickoff(runner, pitcher, "first", pickedOff: true, C, Field);
        var leg = Assert.Single(tl.Runners);
        Assert.True(leg.OutAtEnd);
        Assert.False(leg.Force, "牽制死はタッチプレーのはず");
    }

    // ---- AppendRunnerLegs: Force は決着レッグにだけ乗る（CloseCall と同じ流儀） ----

    [Fact]
    public void AppendRunnerLegs_ForceOnlyOnDecisiveLeg()
    {
        var fielding = new FieldingCoefficients();
        var baserunning = new BaserunningCoefficients();
        var play = GrounderOut();
        var tl = TimelineBuilder.BuildBattedBall(
            play, Field.StandardAlignment(), Field, null, runnersOn: true, TimelineBuilder.DescribeResult(play));

        // 一塁→三塁（2レッグに分割される）で最終到達だけアウト・フォース。
        var moves = new List<RunnerMove>
        {
            new(new Player { Speed = 50 }, 1, 3, Out: true) { Force = true },
        };
        var withLegs = TimelineBuilder.AppendRunnerLegs(tl, moves, Field, baserunning, fielding);
        var legs = withLegs.Runners.Where(r => r.Label == "走1").OrderBy(r => r.T0).ToList();
        Assert.True(legs.Count >= 2, "一塁→三塁は2レッグ以上に分かれるはず");
        for (var i = 0; i < legs.Count - 1; i++)
            Assert.False(legs[i].Force, $"途中レッグ{i}にForceが乗っている（決着レッグだけに乗るはず）");
        Assert.True(legs[^1].Force, "最終レッグにForceが乗っていない");
    }
}
