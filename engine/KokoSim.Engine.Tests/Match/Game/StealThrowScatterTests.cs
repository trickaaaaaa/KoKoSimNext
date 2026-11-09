using KokoSim.Engine.Core;
using KokoSim.Engine.Match.Field;
using KokoSim.Engine.Match.Game;
using KokoSim.Engine.Players;
using Xunit;

namespace KokoSim.Engine.Tests.Match.Game;

/// <summary>
/// 捕手の盗塁送球に通した送球の逸れ（issue #841）。StealResolver レベルの振る舞いを確認する。
/// </summary>
public sealed class StealThrowScatterTests
{
    private static readonly BaserunningCoefficients C = new();

    private static Player Catcher(int throwAccuracy = 50) => new()
        { Position = FieldPosition.Catcher, ArmStrength = 50, ThrowAccuracy = throwAccuracy, Fielding = 50 };

    private static Player Receiver(int catching) => new() { Catching = catching };

    /// <summary>リーグ平均（送球精度50・受け手Catching50）では、送球の逸れを足しても DefenseTimeSeconds が
    /// 従来どおりの値のまま＝帯不変（不変条件#5、50が恒等の作法）。</summary>
    [Fact]
    public void DefenseTimeSeconds_LeagueAverage_UnchangedFromPreScatterFormula()
    {
        var catcher = Catcher(throwAccuracy: 50);
        var withReceiver = StealResolver.DefenseTimeSeconds(catcher, C, receiver: Receiver(50));
        var withoutReceiver = StealResolver.DefenseTimeSeconds(catcher, C); // receiver未指定＝Catching50相当

        // 旧式（送球の逸れ導入前）の素の合計: クイック＋立ち上がり＋握り替え＋送球＋タッチ。
        var catcherFielder = catcher.ToFielder(C.AbilityEffect);
        var throwTime = C.CatchThrowDistanceM / catcherFielder.ThrowSpeedMps;
        var popTransfer = C.PopTransferSeconds * catcherFielder.TransferFactor(C.TransferFieldingSlope, C.TransferFactorMin);
        var legacy = C.PitcherQuickSeconds + C.CatcherPopRiseSeconds + popTransfer + throwTime + C.TagSeconds;

        Assert.Equal(legacy, withReceiver, 9);
        Assert.Equal(legacy, withoutReceiver, 9);
    }

    /// <summary>送球精度が高いほど逸れが小さく、タッチまでの期待秒が短くなる＝守備に有利
    /// （盗塁成功率は下がる方向）。精度20と80で実際に差が出る（完了条件）。</summary>
    [Fact]
    public void DefenseTimeSeconds_HigherThrowAccuracy_IsShorter()
    {
        var low = StealResolver.DefenseTimeSeconds(Catcher(throwAccuracy: 20), C);
        var high = StealResolver.DefenseTimeSeconds(Catcher(throwAccuracy: 80), C);
        Assert.True(high < low, $"送球精度80({high:F3}s)が20({low:F3}s)より長い（短くなるはず）");
    }

    /// <summary>受け手の捕球が高いほど逸れからの遅れが縮む＝タッチまでの期待秒が短くなる。</summary>
    [Fact]
    public void DefenseTimeSeconds_HigherReceiverCatching_IsShorter()
    {
        var catcher = Catcher(throwAccuracy: 20); // 逸れが大きい捕手でないと受け手の差が見えにくい
        var lowReceiver = StealResolver.DefenseTimeSeconds(catcher, C, receiver: Receiver(20));
        var highReceiver = StealResolver.DefenseTimeSeconds(catcher, C, receiver: Receiver(80));
        Assert.True(highReceiver < lowReceiver,
            $"受け手の捕球80({highReceiver:F3}s)が20({lowReceiver:F3}s)より長い（短くなるはず）");
    }

    /// <summary>送球精度が低いほど悪送球（逸れ大）の頻度が増え、無条件セーフが増える。</summary>
    [Fact]
    public void ResolveVerdict_LowThrowAccuracy_ProducesMoreWildThrowSafes()
    {
        var runner = new Player { Speed = 50, Steal = 50 };

        int SafeCount(int throwAccuracy, ulong seedBase)
        {
            var catcher = Catcher(throwAccuracy);
            var rng = new Xoshiro256Random(seedBase);
            var safes = 0;
            for (var i = 0; i < 4000; i++)
                if (StealResolver.ResolveVerdict(runner, catcher, C, rng).Safe) safes++;
            return safes;
        }

        var lowAccuracySafes = SafeCount(throwAccuracy: 10, seedBase: 1);
        var highAccuracySafes = SafeCount(throwAccuracy: 95, seedBase: 1);

        Assert.True(lowAccuracySafes > highAccuracySafes,
            $"送球精度10のセーフ数({lowAccuracySafes})が95({highAccuracySafes})以下＝逸れが効いていない");
    }

    /// <summary>逸れが大きい（悪送球）判定を強制すると無条件セーフになる、という
    /// ThrowScatterModel.IsWild の意図が ResolveVerdict 経路でも保たれる（統計的確認）。
    /// 送球精度を極端に下げて閾値超えの頻度を上げ、極端に遅い走者でもセーフが混ざることを確認する。</summary>
    [Fact]
    public void ResolveVerdict_WildThrow_CanMakeEvenSlowRunnerSafe()
    {
        // 走力1＝ほぼ動けない走者。通常はほぼ確実にアウトになるはずの対戦。
        var slowRunner = new Player { Speed = 1, Steal = 1 };
        var catcher = Catcher(throwAccuracy: 0); // 下限の送球精度＝σが最大
        var rng = new Xoshiro256Random(777);

        var safes = 0;
        for (var i = 0; i < 20000; i++)
            if (StealResolver.ResolveVerdict(slowRunner, catcher, C, rng).Safe) safes++;

        Assert.True(safes > 0, "送球精度0の捕手でも、のろい走者が一度もセーフにならない（悪送球が機能していない）");
    }
}
