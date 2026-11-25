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
/// issue #1241 項目3・5。
/// 3: 一塁手の自己フォース→二塁タッチが3アウト目のとき、二塁は封殺でなくタッチプレー＝タッチより先に
/// 本塁へ着いた三塁走者の得点は認められる（得点一律0ではない）。
/// 5: 球数上限（40球）の打ち切りは打球が無い＝走者は動かず打者だけアウト（旧テーブルで併殺・得点を引かない）。
/// </summary>
public sealed class InningEndingTagTimePlayTests
{
    private static readonly BaserunningCoefficients C = new();
    private static readonly FieldGeometry Field = new();

    // 二段目の固定オーバーヘッドを埋めて一塁手の代替を選ばせる（DoublePlayRaceTests.AltFriendly と同じ作り）。
    private static readonly BaserunningCoefficients AltFriendly = C with
    {
        DoublePlaySelfForceRelayTransferSeconds = 0.0, SecondTagSeconds = 0.10, SecondTagSuccessBias = 0.0,
    };

    private static HomePlayContext FirstBaseGrounder(double fieldedAt = 1.3, double batterToFirst = 4.60)
    {
        var situation = new HomePlaySituation(
            new Vector3D(19.4, 0, 19.4), fieldedAt, new Player { ArmStrength = 55 }.ToFielder().ThrowSpeedMps);
        return new HomePlayContext(
            Field, situation, new TacticsCoefficients(), 0.5, DefenseDepth.Normal, IsFly: false,
            ThrowArriveSeconds: null, BatterToFirstSeconds: batterToFirst, FielderRole: FieldPosition.FirstBase);
    }

    private static (int Runs, int ExtraOuts, bool TagOut, bool ThirdScored, BaseState Bases) Once(
        Player r3, IRandomSource rng, BaserunningCoefficients coeff, double fieldedAt = 1.3, double batterToFirst = 4.60)
    {
        var bases = new BaseState { First = new Player { Speed = 50, Baserunning = 50 }, Third = r3 };
        var (runs, extraOuts, _, _, _, moves) = BaserunningModel.ApplyDetailed(
            bases, PlateAppearanceResult.InPlayOut, new Player(), currentOuts: 1, coeff, rng,
            collectMoves: true, FirstBaseGrounder(fieldedAt, batterToFirst));
        var tagOut = moves.Any(m => m is { FromBase: 1, ToBase: 2, Out: true, FirstBaseSelfForceTag: true });
        var scored = moves.Any(m => ReferenceEquals(m.Runner, r3) && m.ToBase == 4 && !m.Out);
        return (runs, extraOuts, tagOut, scored, bases);
    }

    [Fact]
    public void FirstBaseTag_ThirdOut_ThirdRunnerBeatingTheTag_Scores()
    {
        var rng = new Xoshiro256Random(1241);
        var tagOuts = 0;
        for (var i = 0; i < 400; i++)
        {
            var r3 = new Player { Speed = 99, Baserunning = 99 };
            var (runs, extraOuts, tagOut, scored, bases) = Once(r3, rng, AltFriendly with { HomeGrounderStartDelaySeconds = 0.0 }, fieldedAt: 2.1, batterToFirst: 5.7);
            if (!tagOut) continue;
            tagOuts++;
            Assert.Equal(1, extraOuts); // 1死から打者の一塁アウト＋二塁のタッチ＝3アウト目
            Assert.True(scored, "タッチより先に本塁へ着いた三塁走者の得点が認められるはず");
            Assert.Equal(1, runs);
            Assert.Null(bases.Third);
        }
        Assert.True(tagOuts > 0, "この設定では一塁代替でタッチアウトが出るはず");
    }

    [Fact]
    public void FirstBaseTag_ThirdOut_VerySlowThirdRunner_DoesNotScore()
    {
        var rng = new Xoshiro256Random(1241);
        var tagOuts = 0;
        for (var i = 0; i < 400; i++)
        {
            var r3 = new Player { Speed = 1, Baserunning = 1 };
            var (runs, _, tagOut, scored, _) = Once(r3, rng, AltFriendly with { HomeGrounderStartDelaySeconds = 30.0 });
            if (!tagOut) continue;
            tagOuts++;
            Assert.False(scored, "走者がタッチより遅れて本塁へ着くなら得点は無い");
            Assert.Equal(0, runs);
        }
        Assert.True(tagOuts > 0);
    }

    [Fact]
    public void StandardForceAtSecond_ThirdOut_StillNoRun()
    {
        // 標準の二塁封殺（フォース）が3アウト目なら従来どおり0点（封殺が第3アウトなら得点は認められない）。
        var situation = new HomePlaySituation(
            new Vector3D(-6, 0, 37), 1.4, new Player { ArmStrength = 55 }.ToFielder().ThrowSpeedMps);
        var home = new HomePlayContext(
            Field, situation, new TacticsCoefficients(), 0.5, DefenseDepth.Normal, IsFly: false,
            ThrowArriveSeconds: null, BatterToFirstSeconds: 5.40);
        var rng = new Xoshiro256Random(77);
        var forced = 0;
        for (var i = 0; i < 200; i++)
        {
            var bases = new BaseState { First = new Player { Speed = 50, Baserunning = 50 }, Third = new Player { Speed = 80 } };
            var (runs, extraOuts, _, fc, _, moves) = BaserunningModel.ApplyDetailed(
                bases, PlateAppearanceResult.InPlayOut, new Player(), 1, C, rng, collectMoves: true, home);
            if (fc || extraOuts != 1 || !moves.Any(m => m is { FromBase: 1, ToBase: 2, Out: true, Force: true })) continue;
            forced++;
            Assert.Equal(0, runs);
        }
        Assert.True(forced > 0);
    }

    [Fact]
    public void PitchLimitCutoff_PlainOut_RunnersHold_NoDoublePlayNoRun()
    {
        var rng = new Xoshiro256Random(40);
        for (var i = 0; i < 2000; i++)
        {
            var r1 = new Player { Speed = 50 };
            var r3 = new Player { Speed = 50 };
            var bases = new BaseState { First = r1, Third = r3 };
            var (runs, extraOuts, baseOuts, batterSafeOnFc, _, moves) = BaserunningModel.ApplyDetailed(
                bases, PlateAppearanceResult.InPlayOut, new Player(), currentOuts: 0, C, rng,
                collectMoves: true, home: null, plainOut: true);
            Assert.Equal(0, runs);
            Assert.Equal(0, extraOuts);
            Assert.Equal((0, 0), baseOuts);
            Assert.False(batterSafeOnFc);
            Assert.Empty(moves);
            Assert.Same(r1, bases.First);
            Assert.Same(r3, bases.Third);
        }
    }

    [Fact]
    public void WithoutPlainOut_NullHome_StillUsesLegacyTables()
    {
        // 打球の無い単体テスト経路（home無し）の従来テーブルは plainOut を立てない限り不変。
        var rng = new Xoshiro256Random(40);
        var scored = 0;
        for (var i = 0; i < 2000; i++)
        {
            var bases = new BaseState { Third = new Player { Speed = 50 } };
            var (runs, _, _, _, _, _) = BaserunningModel.ApplyDetailed(
                bases, PlateAppearanceResult.InPlayOut, new Player(), 0, C, rng, collectMoves: true, home: null);
            scored += runs;
        }
        Assert.True(scored > 0);
    }
}
