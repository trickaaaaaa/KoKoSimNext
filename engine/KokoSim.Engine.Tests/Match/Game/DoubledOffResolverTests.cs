using KokoSim.Engine.Core;
using KokoSim.Engine.Match.Game;
using KokoSim.Engine.Players;
using Xunit;

namespace KokoSim.Engine.Tests.Match.Game;

/// <summary>
/// ライナー併殺（設計書12 §4, G2）の純関数解決。コンタクト始動(エンドラン等)の一塁走者が
/// 打球を空中で捕られた際、塁へ戻れるかを <see cref="HomePlayResolver"/> と同型の時間の勝負で解く。
/// 校正値は🟡（絶対頻度はHeavyで着地）のため、ここでは単調性・方向・決定論を検証する。
/// </summary>
public sealed class DoubledOffResolverTests
{
    // バイアスを本番校正値に依存しない固定値に留め、単体では単調性・方向だけを見る。
    private static readonly BaserunningCoefficients C = new() { DoubledOffSuccessBias = 0.0 };

    private static Player Runner(int speed = 50) => new() { Speed = speed };

    private static double SafeRate(
        Player runner, double caughtAt, double catchToBaseM, double throwSpeed, int trials = 4000, ulong seed = 5)
    {
        var rng = new Xoshiro256Random(seed);
        var safe = 0;
        for (var i = 0; i < trials; i++)
            if (DoubledOffResolver.Resolve(runner, caughtAt, catchToBaseM, throwSpeed, C, rng) == DoubledOffResult.Safe)
                safe++;
        return (double)safe / trials;
    }

    // --- 既知の方向: 長い滞空・遠い捕球点は余裕で戻れる、鋭い/近いライナーは戻れないことが多い ---

    [Fact]
    public void ToweringFlyBall_FarFromBase_RunnerAlwaysGetsBack()
    {
        // 高いフライ(滞空3.5s)・遠い捕球点(60m)＝走者は早々に見切って戻れる。
        var rate = SafeRate(Runner(), caughtAt: 3.5, catchToBaseM: 60, throwSpeed: 32);
        Assert.True(rate > 0.90, $"長い滞空でも戻れなさすぎる: {rate}");
    }

    [Fact]
    public void SharpLiner_CloseToBase_RunnerOftenDoubledOff()
    {
        // 鋭いライナー(滞空0.6s)・塁のすぐ近くでの捕球(3m)＝短い送球が間に合いやすい。
        var rate = SafeRate(Runner(), caughtAt: 0.6, catchToBaseM: 3, throwSpeed: 32);
        Assert.True(rate < 0.30, $"鋭いライナーで戻れすぎる: {rate}");
    }

    // --- 単調性 ---

    [Fact]
    public void FasterRunner_GetsBackMore()
    {
        var slow = SafeRate(Runner(speed: 20), caughtAt: 0.5, catchToBaseM: 15, throwSpeed: 30);
        var fast = SafeRate(Runner(speed: 85), caughtAt: 0.5, catchToBaseM: 15, throwSpeed: 30);
        Assert.True(fast > slow + 0.05, $"走力で戻れる率が上がっていない: slow={slow} fast={fast}");
    }

    [Fact]
    public void StrongerArm_DoublesOffMore()
    {
        var weak = SafeRate(Runner(), caughtAt: 0.5, catchToBaseM: 15, throwSpeed: 22);
        var cannon = SafeRate(Runner(), caughtAt: 0.5, catchToBaseM: 15, throwSpeed: 38);
        Assert.True(weak > cannon + 0.05, $"強肩で憤死が増えていない: weak={weak} cannon={cannon}");
    }

    [Fact]
    public void FartherCatchPoint_LetsRunnerGetBackMore()
    {
        // 捕球点が塁から遠いほど戻し送球も長くなる＝走者が戻れる余地が増える。
        var close = SafeRate(Runner(), caughtAt: 0.5, catchToBaseM: 5, throwSpeed: 30);
        var far = SafeRate(Runner(), caughtAt: 0.5, catchToBaseM: 40, throwSpeed: 30);
        Assert.True(far > close + 0.05, $"捕球点が遠くても戻れる率が上がっていない: close={close} far={far}");
    }

    [Fact]
    public void LeadGained_IsCapped_ForLongHangTimes()
    {
        // 「盲目的に走る」時間は頭打ち＝滞空が長くなってもリード(と戻り所要)は増え続けない。
        var leadAtCap = DoubledOffResolver.LeadGainedM(1.0, C);
        var leadBeyondCap = DoubledOffResolver.LeadGainedM(4.0, C);
        Assert.Equal(leadAtCap, leadBeyondCap, 6);
    }

    // --- 契約: 確率はクランプされ、決定論である ---

    [Fact]
    public void Probability_IsClampedWithinBounds()
    {
        var pEasy = DoubledOffResolver.SuccessProbability(Runner(85), 3.0, 70, 30, C);
        var pHard = DoubledOffResolver.SuccessProbability(Runner(20), 0.5, 2, 40, C);
        Assert.InRange(pEasy, 0.01, 0.99);
        Assert.InRange(pHard, 0.01, 0.99);
        Assert.True(pEasy > pHard);
    }

    [Fact]
    public void Resolve_IsDeterministic_ForSameSeed()
    {
        var runner = Runner(60);
        var a = SafeRate(runner, 0.5, 15, 30, trials: 500, seed: 42);
        var b = SafeRate(runner, 0.5, 15, 30, trials: 500, seed: 42);
        Assert.Equal(a, b);
    }
}
