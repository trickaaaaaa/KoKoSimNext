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
/// フォース進塁の確定化（2026-07-20, OPEN-QUESTIONS 2026-07-19項）。
/// ゴロ凡打で押し出される走者（一塁=常時 / 二塁=一塁走者あり / 三塁=満塁）は確定で進塁し、
/// ProductiveOutAdvanceProb は非フォース専用に限定される。フライと home 無しは従来のまま。
/// </summary>
public sealed class ForceAdvanceTests
{
    private static readonly BaserunningCoefficients C = new();
    private static readonly FieldGeometry Field = new();

    // 内野ゴロ（処理点35m・肩50）。BaserunningHomePlayTests.Grounder と同型。
    private static HomePlayContext Grounder(DefenseDepth depth = DefenseDepth.Normal)
    {
        var fielded = depth == DefenseDepth.In ? 1.3 : depth == DefenseDepth.Deep ? 1.7 : 1.5;
        return new(Field,
            new HomePlaySituation(new Vector3D(0, 0, 35), fielded, new Player { ArmStrength = 50 }.ToFielder().ThrowSpeedMps),
            new TacticsCoefficients(), 0.5, depth, IsFly: false);
    }

    private static HomePlayContext Fly()
        => new(Field,
            new HomePlaySituation(new Vector3D(0, 0, 55), 2.5, new Player { ArmStrength = 50 }.ToFielder().ThrowSpeedMps),
            new TacticsCoefficients(), 0.5, DefenseDepth.Normal, IsFly: true);

    [Fact]
    public void Grounder_ForcedFirstRunner_NeverStrandedAtFirst()
    {
        // 1死1塁の内野ゴロ: 一塁走者はフォース＝併殺で死ぬか二塁へ進むかの二択。一塁残留は存在しない。
        var rng = new Xoshiro256Random(11);
        var advanced = 0;
        for (var i = 0; i < 2000; i++)
        {
            var r1 = new Player { Speed = 50, Baserunning = 50 };
            var bases = new BaseState { First = r1 };
            BaserunningModel.ApplyDetailed(
                bases, PlateAppearanceResult.InPlayOut, new Player(), 1, C, rng, collectMoves: false, Grounder());
            Assert.Null(bases.First); // 一塁に残る結末は無い
            if (ReferenceEquals(bases.Second, r1)) advanced++;
        }
        Assert.True(advanced > 0, "二塁への確定進塁が一度も観測されない");
    }

    [Fact]
    public void Grounder_ForcedSecondRunner_AlwaysReachesThird()
    {
        // 無死一二塁の内野ゴロ: 二塁走者もフォース＝必ず三塁へ（併殺の犠牲になるのは一塁走者側）。
        var rng = new Xoshiro256Random(12);
        for (var i = 0; i < 2000; i++)
        {
            var r2 = new Player { Speed = 50, Baserunning = 50 };
            var bases = new BaseState { First = new Player(), Second = r2 };
            BaserunningModel.ApplyDetailed(
                bases, PlateAppearanceResult.InPlayOut, new Player(), 0, C, rng, collectMoves: false, Grounder());
            Assert.Same(r2, bases.Third); // 二塁残留は存在しない
        }
    }

    [Fact]
    public void Grounder_BasesLoaded_ForcedThirdRunner_AlwaysResolvesHome()
    {
        // 無死満塁の内野ゴロ（通常守備）: 三塁走者はフォースで自重できない＝守備が一塁/併殺を選ぶ以上、必ず生還。
        var rng = new Xoshiro256Random(13);
        for (var i = 0; i < 2000; i++)
        {
            var r3 = new Player { Speed = 50, Baserunning = 50 };
            var bases = new BaseState { First = new Player(), Second = new Player(), Third = r3 };
            var (runs, _, _, _, _, _) = BaserunningModel.ApplyDetailed(
                bases, PlateAppearanceResult.InPlayOut, new Player(), 0, C, rng, collectMoves: false, Grounder());
            Assert.True(runs >= 1, "満塁ゴロで三塁走者が生還しない結末が出た（フォースで残留は不可能）");
            Assert.False(ReferenceEquals(bases.Third, r3)); // 元の三塁走者は必ず本塁へ（三塁は二塁走者が埋め直す）
        }
    }

    [Fact]
    public void Grounder_BasesLoaded_InfieldIn_ForcePlayAtHome_BothOutcomes()
    {
        // 満塁×内野前進: 本塁封殺の勝負。自重は不可能＝生還か本塁アウトの二択で、両方が出る。
        var rng = new Xoshiro256Random(14);
        int scored = 0, gunnedDown = 0, held = 0;
        for (var i = 0; i < 3000; i++)
        {
            var r3 = new Player { Speed = 50, Baserunning = 50 };
            var bases = new BaseState { First = new Player(), Second = new Player(), Third = r3 };
            var (runs, _, homeOuts, _, _, _) = BaserunningModel.ApplyDetailed(
                bases, PlateAppearanceResult.InPlayOut, new Player(), 0, C, rng, collectMoves: false,
                Grounder(DefenseDepth.In));
            scored += runs;
            gunnedDown += homeOuts;
            if (ReferenceEquals(bases.Third, r3)) held++;
        }
        Assert.True(scored > 0, "本塁封殺の勝負で一度も生還しない");
        Assert.True(gunnedDown > 0, "本塁封殺の勝負で一度も刺されない");
        Assert.Equal(0, held); // フォース走者の三塁残留（自重）は存在しない
    }

    [Fact]
    public void Fly_FirstRunner_NotForced_TagUpRemainsProbabilistic()
    {
        // フライ（タッグアップ）: 一塁走者はフォースではない＝従来の確率進塁のまま、一塁残留が多数出る。
        var rng = new Xoshiro256Random(15);
        var stayed = 0;
        for (var i = 0; i < 1000; i++)
        {
            var r1 = new Player { Speed = 50, Baserunning = 50 };
            var bases = new BaseState { First = r1 };
            BaserunningModel.ApplyDetailed(
                bases, PlateAppearanceResult.InPlayOut, new Player(), 0, C, rng, collectMoves: false, Fly());
            if (ReferenceEquals(bases.First, r1)) stayed++;
        }
        Assert.True(stayed > 300, $"フライで一塁残留が少なすぎる（フォース扱いになっていないか）: {stayed}/1000");
    }

    [Fact]
    public void NullContext_LegacyPath_Unchanged_Probabilistic()
    {
        // home 無し（従来互換経路）: ゴロ/フライを判別できないためフォース対象外＝確率進塁のまま。
        var rng = new Xoshiro256Random(16);
        var stayed = 0;
        for (var i = 0; i < 1000; i++)
        {
            var r1 = new Player { Speed = 50, Baserunning = 50 };
            var bases = new BaseState { First = r1 };
            BaserunningModel.ApplyDetailed(
                bases, PlateAppearanceResult.InPlayOut, new Player(), 0, C, rng, collectMoves: false, home: null);
            if (ReferenceEquals(bases.First, r1)) stayed++;
        }
        Assert.True(stayed > 300, $"home無し経路の挙動が変わっている: 一塁残留 {stayed}/1000");
    }
}
