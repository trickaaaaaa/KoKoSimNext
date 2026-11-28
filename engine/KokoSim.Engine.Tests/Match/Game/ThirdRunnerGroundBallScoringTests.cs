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
/// 三塁走者ありの内野ゴロで、生還を古い表（SacFlyScoreProb一律30%）で決めていた件の直し（issue #1788,
/// design-12 §4.6）。無死一・三塁の併殺（二塁封殺→一塁）で三塁走者が本塁へ走らず残っていた・普通の
/// 内野ゴロも打球の深さ/走者の脚が効かなかった。守備の送球の行き先（二塁+一塁で使い切ったか／本塁へ
/// 投げたか）で3分岐に組み替える:
/// A) 併殺成立（二塁封殺＋打者一塁アウト）＝送球は二塁+一塁で使い切り、本塁は空く＝無条件生還。
/// B) 二塁封殺だけ（野選・打者は一塁生存）＝送球は二塁の1本だけ＝Aと同じく無条件生還。
/// C) 併殺なしの通常の内野ゴロ（打者だけ一塁アウト）＝前進と同じ本塁の時間の勝負。見込みが薄ければ
///    守備は本塁を捨てて一塁で確実に打者を取る（1点は渡す）。
/// </summary>
public sealed class ThirdRunnerGroundBallScoringTests
{
    private static readonly BaserunningCoefficients C = new();
    private static readonly FieldGeometry Field = new();

    // 一・三塁の内野ゴロ（遊撃手が処理＝標準の二塁封殺ルート）。DoublePlayRaceTests の
    // Order_RelayJustMissesConverts系と同じ座標（併殺/野選が両方出る帯）に三塁走者を乗せる。
    private static HomePlayContext Grounder(
        double x, double z, double fieldedAt, double batterToFirst, int arm = 55, DefenseDepth depth = DefenseDepth.Normal)
    {
        var situation = new HomePlaySituation(
            new Vector3D(x, 0, z), fieldedAt, new Player { ArmStrength = arm }.ToFielder().ThrowSpeedMps);
        return new HomePlayContext(Field, situation, new TacticsCoefficients(), 0.5, depth, IsFly: false,
            BatterToFirstSeconds: batterToFirst);
    }

    // ===== A・B: 一・三塁、無死、一塁走者の併殺/野選が成立した打球は三塁走者が無条件で生還する =====

    [Fact]
    public void FirstAndThird_DoublePlayOrForceOnlyAtSecond_ThirdRunnerAlwaysScores()
    {
        // DoublePlayRaceTests.Order_RelayJustMissesConverts と同じ帯（併殺・野選が両方出る）に三塁走者を乗せる。
        var home = Grounder(x: -6, z: 37, fieldedAt: 1.4, batterToFirst: 4.45);
        var rng = new Xoshiro256Random(11);
        int dpTrials = 0, fcTrials = 0, noDpTrials = 0;
        for (var i = 0; i < 6000; i++)
        {
            var r1 = new Player { Name = "R1", Speed = 50, Baserunning = 50 };
            var r3 = new Player { Name = "R3", Speed = 50, Baserunning = 50 };
            var bases = new BaseState { First = r1, Third = r3 };
            var (runs, extraOuts, baseOuts, batterSafeOnFc, _, moves) = BaserunningModel.ApplyDetailed(
                bases, PlateAppearanceResult.InPlayOut, new Player { Name = "Batter" }, currentOuts: 0, C, rng,
                collectMoves: true, home);

            if (extraOuts == 0) { noDpTrials++; continue; } // 併殺不成立（一塁走者は二塁へ安全進塁）＝C分岐の対象外
            Assert.Equal(1, runs); // A・B どちらでも三塁走者は無条件で生還
            Assert.Equal(0, baseOuts.Home); // 送球は本塁へ行っていない＝本塁憤死は出ない
            Assert.Null(bases.Third); // 三塁走者は生還して塁を離れている
            Assert.True(moves.Any(m => m.FromBase == 3 && m.ToBase == 4 && !m.Out), "三塁→本塁の生還moveが無い");
            if (batterSafeOnFc) fcTrials++; else dpTrials++;
        }
        Assert.True(dpTrials > 0, $"併殺成立のケースが無い（テストが空回り）: dp={dpTrials} fc={fcTrials} noDp={noDpTrials}");
        Assert.True(fcTrials > 0, $"野選（二塁封殺だけ）のケースが無い（テストが空回り）: dp={dpTrials} fc={fcTrials} noDp={noDpTrials}");
    }

    [Fact]
    public void FirstAndThird_OneOutDoublePlayEndingInning_ThirdRunnerDoesNotAutomaticallyScore()
    {
        // 完了条件: 一死一・三塁の併殺（3アウト目が二塁封殺＝打者も一塁アウト）は、今までどおり無条件生還で
        // はない（3アウト目は封殺＝時間の勝負すら発生しない＝公認野球規則どおり無得点）。
        // 二塁封殺だけ（野選・打者は一塁生存＝2アウトで打席継続）はBと同じく無条件生還＝別集計する。
        var home = Grounder(x: -6, z: 37, fieldedAt: 1.4, batterToFirst: 4.45);
        var rng = new Xoshiro256Random(13);
        int dpTrials = 0, dpScored = 0, fcTrials = 0, fcScored = 0;
        for (var i = 0; i < 3000; i++)
        {
            var r1 = new Player { Name = "R1", Speed = 50, Baserunning = 50 };
            var r3 = new Player { Name = "R3", Speed = 50, Baserunning = 50 };
            var bases = new BaseState { First = r1, Third = r3 };
            var (runs, extraOuts, _, batterSafeOnFc, _, _) = BaserunningModel.ApplyDetailed(
                bases, PlateAppearanceResult.InPlayOut, new Player { Name = "Batter" }, currentOuts: 1, C, rng,
                collectMoves: false, home);
            if (extraOuts == 0) continue; // 併殺不成立＝チェンジにならない（このテストの対象外）
            if (batterSafeOnFc) { fcTrials++; fcScored += runs; } else { dpTrials++; dpScored += runs; }
        }
        Assert.True(dpTrials > 0, "一死での併殺（3アウト目）のケースが無い（テストが空回り）");
        Assert.Equal(0, dpScored); // 3アウト目が封殺の併殺は常に無得点（公認野球規則5.08(a)）
        Assert.True(fcTrials > 0, "一死での野選（二塁封殺だけ）のケースが無い（テストが空回り）");
        Assert.Equal(fcTrials, fcScored); // 野選は打者が一塁生存＝チェンジにならない＝Bと同じく無条件生還
    }

    // ===== C: 併殺なしの通常の内野ゴロ（home.InfieldDepth=Normal）は本塁の時間の勝負で決める =====

    private static (int runs, int homeOuts) GrounderR3(
        DefenseDepth depth, Player runner, double distanceM, double fieldedAt, int trials = 3000, ulong seed = 5, int arm = 50)
    {
        var rng = new Xoshiro256Random(seed);
        int runs = 0, homeOuts = 0;
        for (var i = 0; i < trials; i++)
        {
            var bases = new BaseState { Third = runner };
            var home = new HomePlayContext(Field,
                new HomePlaySituation(new Vector3D(0, 0, distanceM), fieldedAt, new Player { ArmStrength = arm }.ToFielder().ThrowSpeedMps),
                new TacticsCoefficients(), 0.5, depth, IsFly: false);
            var (r, _, baseOuts, _, _, _) = BaserunningModel.ApplyDetailed(
                bases, PlateAppearanceResult.InPlayOut, new Player(), currentOuts: 0, C, rng, collectMoves: false, home);
            runs += r; homeOuts += baseOuts.Home;
        }
        return (runs, homeOuts);
    }

    [Fact]
    public void NormalDepth_DeeperGroundBall_ScoresMoreOftenThanShallow()
    {
        var avg = new Player { Speed = 50, Baserunning = 50 };
        // 浅い投手前のゴロ（近い処理点・早い処理）: 即投げられる＝自重か本塁憤死に寄る。
        var shallow = GrounderR3(DefenseDepth.Normal, avg, distanceM: 15, fieldedAt: 0.7);
        // 深いゴロ（遠い処理点・遅い処理）: ほぼ生還。
        var deep = GrounderR3(DefenseDepth.Normal, avg, distanceM: 60, fieldedAt: 2.2);
        Assert.True(deep.runs > shallow.runs,
            $"深いゴロのほうが生還しやすいはず: shallow={shallow.runs} deep={deep.runs}");
        Assert.True(shallow.runs == 0 || shallow.homeOuts > 0,
            $"浅い投手前のゴロでは自重か本塁憤死のはず（無条件生還ではない）: runs={shallow.runs} homeOuts={shallow.homeOuts}");
    }

    [Fact]
    public void NormalDepth_FasterRunner_ScoresMoreOftenThanSlowRunner()
    {
        var fast = new Player { Speed = 85, Baserunning = 80 };
        var slow = new Player { Speed = 20, Baserunning = 20 };
        var (fastRuns, _) = GrounderR3(DefenseDepth.Normal, fast, distanceM: 35, fieldedAt: 1.5);
        var (slowRuns, _) = GrounderR3(DefenseDepth.Normal, slow, distanceM: 35, fieldedAt: 1.5);
        Assert.True(fastRuns > slowRuns,
            $"速い走者のほうが生還しやすいはず: fast={fastRuns} slow={slowRuns}");
    }

    [Fact]
    public void NormalDepth_UnlikelyToNailRunner_DefenseConcedesRunAndTakesSureOutAtFirst()
    {
        // 生還見込みが高すぎる（NormalDepthHomeThrowMaxRunnerSuccess超）＝守備は本塁を狙わず一塁で確実にアウトを取る。
        // 無条件で生還し、本塁憤死は一切出ない（RNG非消費＝毎回同じ結果）。
        var fast = new Player { Speed = 85, Baserunning = 80 };
        var (runs, homeOuts) = GrounderR3(DefenseDepth.Normal, fast, distanceM: 35, fieldedAt: 1.5, trials: 500);
        Assert.Equal(500, runs);
        Assert.Equal(0, homeOuts);
    }
}
