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
/// フライアウト×一塁走者（issue fly-out-legacy-double-play-table / fly-out-runner-dp-table-and-doubled-off-depiction）。
/// 13% 固定の併殺テーブル・野選テーブルは<b>ゴロ／home無し</b>だけに効き、空中で捕られた打球で一塁走者が
/// 余分にアウトになるのは飛び出しの物理レース（<see cref="DoubledOffResolver"/>＝一塁へ戻れず）だけ。
/// 通常始動の走者は帰塁できる（0件）。盗塁で走っていた走者（r1StealBreak）とギャンブル始動は勝負に乗る。
/// </summary>
public sealed class FlyOutDoubledOffTests
{
    private static readonly FieldGeometry Field = new();

    /// <summary>外野フライの捕球（処理点 distanceM・捕球時刻 fieldedAt・肩 arm）。</summary>
    private static HomePlayContext FlyCatch(double distanceM = 70, double fieldedAt = 3.5, int arm = 50)
        => new(Field,
            new HomePlaySituation(new Vector3D(0, 0, distanceM), fieldedAt, new Player { ArmStrength = arm }.ToFielder().ThrowSpeedMps),
            new TacticsCoefficients(), 0.5, DefenseDepth.Normal, IsFly: true);

    private static (int forceAtSecond, int fcBatterSafe, int doubledOff, int safeReturn, int r1Advance) Run(
        BaserunningCoefficients c, HomePlayContext home, StartType r1Start, bool stealBreak, int trials = 2000, ulong seed = 11)
    {
        var rng = new Xoshiro256Random(seed);
        int forceAtSecond = 0, fcBatterSafe = 0, doubledOff = 0, safeReturn = 0, r1Advance = 0;
        for (var i = 0; i < trials; i++)
        {
            var r1 = new Player { Speed = 40 };
            var bases = new BaseState { First = r1 };
            var (_, _, _, batterSafeOnFc, _, moves) = BaserunningModel.ApplyDetailed(
                bases, PlateAppearanceResult.InPlayOut, new Player(), 0, c, rng,
                collectMoves: true, home, r1Start, r1StealBreak: stealBreak);
            forceAtSecond += moves.Count(m => m is { FromBase: 1, ToBase: 2, Out: true });
            if (batterSafeOnFc) fcBatterSafe++;
            doubledOff += moves.Count(m => m is { FromBase: 1, ToBase: 1, Out: true });
            safeReturn += moves.Count(m => m is { FromBase: 1, ToBase: 1, Out: false });
            r1Advance += moves.Count(m => m is { FromBase: 1, ToBase: 2, Out: false });
            // 帰塁セーフなら塁上は不変
            if (moves.Any(m => m is { FromBase: 1, ToBase: 1, Out: false })) Assert.Same(r1, bases.First);
        }
        return (forceAtSecond, fcBatterSafe, doubledOff, safeReturn, r1Advance);
    }

    [Fact]
    public void FlyOut_NeverUsesTheGrounderDoublePlayOrFieldersChoiceTable()
    {
        // テーブルを 100% にしてもフライでは一度も引かない（走力40＝旧式なら 19% で二塁封殺）。
        var c = new BaserunningCoefficients { DoublePlayProb = 1.0, FieldersChoiceProb = 1.0 };
        var (forceAtSecond, fcBatterSafe, doubledOff, _, _) = Run(c, FlyCatch(), StartType.Normal, stealBreak: false);
        Assert.Equal(0, forceAtSecond);
        Assert.Equal(0, fcBatterSafe);
        Assert.Equal(0, doubledOff); // 通常始動＝飛び出しの勝負にも乗らない
    }

    [Fact]
    public void GrounderWithoutBatterToFirst_StillFallsBackToTheTable()
    {
        // ゴロの純テーブル経路（BatterToFirstSeconds なし）は従来どおり（DoublePlayRaceTests と同じ前提）。
        var c = new BaserunningCoefficients { DoublePlayProb = 1.0 };
        var home = FlyCatch() with { IsFly = false };
        var (forceAtSecond, _, _, _, _) = Run(c, home, StartType.Normal, stealBreak: false, trials: 400);
        Assert.InRange(forceAtSecond, 150, 250); // テーブルの上限クランプ 0.5 で約半分
    }

    [Fact]
    public void StealBreakRunner_OnAShallowFly_CanBeDoubledOff_AndSafeReturnsAreDepictedButKeepTheBase()
    {
        // 盗塁で走っていた走者×浅い外野フライ（40m・2.2s）: 憤死も帰塁セーフも出る。
        var c = new BaserunningCoefficients();
        var (forceAtSecond, _, doubledOff, safeReturn, r1Advance) =
            Run(c, FlyCatch(distanceM: 40, fieldedAt: 2.2, arm: 70), StartType.Normal, stealBreak: true);
        Assert.Equal(0, forceAtSecond);
        Assert.True(doubledOff > 0, "盗塁で走っていた走者が浅いフライで一度も刺されない");
        Assert.True(safeReturn > 0, "帰塁セーフの move（絵の材料）が一度も積まれない");
        Assert.Equal(0, r1Advance); // 飛び出して戻った走者はその捕球でタッチアップしない
    }

    [Fact]
    public void GambleStart_IsTreatedLikeAStealBreak()
    {
        var c = new BaserunningCoefficients();
        var (_, _, doubledOff, safeReturn, _) =
            Run(c, FlyCatch(distanceM: 40, fieldedAt: 2.2, arm: 70), StartType.Gamble, stealBreak: false);
        Assert.True(doubledOff + safeReturn > 0, "ギャンブル始動が飛び出しの勝負に乗らない");
    }

    [Fact]
    public void DeepFly_StealBreakRunner_MostlyGetsBack()
    {
        // 深いフライ（90m・4.5s）は戻し送球が長い＝ほぼ帰塁できる（飛び出し憤死は現実でも稀）。
        var c = new BaserunningCoefficients();
        var (_, _, doubledOff, safeReturn, _) =
            Run(c, FlyCatch(distanceM: 90, fieldedAt: 4.5, arm: 50), StartType.Normal, stealBreak: true);
        Assert.True(safeReturn > 0);
        Assert.True(doubledOff <= (doubledOff + safeReturn) * 0.05, $"深いフライで憤死 {doubledOff}/{doubledOff + safeReturn}");
    }

    [Fact]
    public void DoubledOffMoves_CarryTheDisplayMaterial()
    {
        var c = new BaserunningCoefficients();
        var rng = new Xoshiro256Random(3);
        var home = FlyCatch(distanceM: 40, fieldedAt: 2.2, arm: 70);
        var (_, _, _, _, _, moves) = BaserunningModel.ApplyDetailed(
            new BaseState { First = new Player { Speed = 40 } }, PlateAppearanceResult.InPlayOut, new Player(), 0, c, rng,
            collectMoves: true, home, StartType.Normal, r1StealBreak: true);
        var mv = Assert.Single(moves, m => m.FromBase == 1 && m.ToBase == 1);
        Assert.NotNull(mv.DoubledOffThrowArriveSeconds);
        Assert.True(mv.DoubledOffThrowArriveSeconds > 2.2);
        Assert.InRange(mv.DoubledOffMaxLeadM, c.StealBreakExtraLeadM, c.StealBreakExtraLeadM + c.LinerCommitCapSeconds * c.LinerReferenceSprintSpeedMps + 1e-9);
        Assert.InRange(mv.DoubledOffLeadM, 0.0, mv.DoubledOffMaxLeadM);
    }
}
