using System;
using KokoSim.Engine.Core;
using KokoSim.Engine.Match.Field;
using KokoSim.Engine.Match.Game;
using KokoSim.Engine.Players;
using Xunit;

namespace KokoSim.Engine.Tests.Match.Game;

/// <summary>
/// 本塁クロスプレー（バックホーム憤死, 設計書12 §3, F2 Slice A）の純関数解決。
/// 盗塁と同型の「時間の勝負」＝走者(走力＋走塁判断) vs 外野処理＋中継＋タッチ。
/// 校正値は 🟡（絶対頻度は Slice C の Heavy で着地）のため、ここでは単調性・方向・決定論を検証する。
/// </summary>
public sealed class HomePlayResolverTests
{
    // 単体は接戦帯で単調性を見るため、生産校正値(HomeSuccessBias)に依存しない固定バイアスに固定する。
    private static readonly BaserunningCoefficients C = new() { HomeSuccessBias = 0.6 };
    private static readonly FieldGeometry Field = new();

    /// <summary>外野の処理点（方位0=センター方向, 距離d）。</summary>
    private static HomePlaySituation Situation(double distanceM, double fieldedAt, int arm = 50)
        => new(new Vector3D(0, 0, distanceM), fieldedAt, new Player { ArmStrength = arm }.ToFielder().ThrowSpeedMps);

    private static double ScoreRate(Player runner, int fromBase, HomePlaySituation s, int trials = 4000, ulong seed = 5)
    {
        var rng = new Xoshiro256Random(seed);
        var safe = 0;
        for (var i = 0; i < trials; i++)
            if (HomePlayResolver.Resolve(runner, fromBase, s, Field, C, rng) == HomePlayResult.Safe) safe++;
        return (double)safe / trials;
    }

    private static Player Runner(int speed = 50, int baserunning = 50) => new() { Speed = speed, Baserunning = baserunning };

    // --- 既知の方向: 三塁走者は単打でほぼ生還、一塁走者は単打で本塁を狙えば刺されやすい ---

    [Fact]
    public void RunnerFromThird_OnOutfieldSingle_ScoresAlmostAlways()
    {
        // センター前〜中間の単打（処理点70m・処理3.0s）。三塁走者は余裕で生還。
        var rate = ScoreRate(Runner(), fromBase: 3, Situation(70, 3.0));
        Assert.True(rate > 0.90, $"三塁走者の生還率が低すぎる: {rate}");
    }

    [Fact]
    public void RunnerFromFirst_TryingHome_IsGunnedDownOften()
    {
        // 同じ打球で一塁から本塁を狙う（3塁ぶん走る）＝到達が遅く憤死が増える。
        var rate = ScoreRate(Runner(), fromBase: 1, Situation(70, 3.0));
        Assert.True(rate < 0.50, $"一塁走者の本塁突入が安全すぎる: {rate}");
    }

    // --- 単調性: 走力・深い打球ほど生還、強肩・浅い打球ほど憤死 ---

    [Fact]
    public void FasterRunner_ScoresMore()
    {
        var s = Situation(72, 3.2); // 二塁走者が回れば接戦になる通常の単打
        var slow = ScoreRate(Runner(speed: 25), 2, s);
        var fast = ScoreRate(Runner(speed: 85), 2, s);
        Assert.True(fast > slow + 0.05, $"走力で生還率が上がっていない: slow={slow} fast={fast}");
    }

    [Fact]
    public void DeeperBall_ScoresMore_ThanShallow()
    {
        var runner = Runner();
        var shallow = ScoreRate(runner, 2, Situation(50, 2.2)); // 浅い・早い処理
        var deep = ScoreRate(runner, 2, Situation(95, 3.6));    // 深い・遅い処理＝長い送球
        Assert.True(deep > shallow + 0.05, $"深い打球で生還率が上がっていない: shallow={shallow} deep={deep}");
    }

    [Fact]
    public void StrongerOutfieldArm_GunsDownMore()
    {
        var runner = Runner();
        var weak = ScoreRate(runner, 2, Situation(70, 3.1, arm: 20));
        var cannon = ScoreRate(runner, 2, Situation(70, 3.1, arm: 90));
        Assert.True(weak > cannon + 0.03, $"強肩で憤死が増えていない: weak={weak} cannon={cannon}");
    }

    [Fact]
    public void BetterBaserunningRead_ScoresMore()
    {
        var s = Situation(70, 3.1); // 接戦帯（走塁判断の差が生還率に出る）
        var dull = ScoreRate(Runner(baserunning: 20), 2, s);
        var sharp = ScoreRate(Runner(baserunning: 90), 2, s);
        Assert.True(sharp >= dull, $"走塁判断で生還率が下がっている: dull={dull} sharp={sharp}");
    }

    // --- 契約: 確率はクランプされ、決定論である ---

    [Fact]
    public void Probability_IsClampedWithinBounds()
    {
        // 到達不能な深い打球（生還ほぼ確実）でも上限、浅い正面（憤死ほぼ確実）でも下限を割らない。
        var pEasy = HomePlayResolver.SuccessProbability(Runner(85), 3, Situation(115, 4.5), Field, C);
        var pHard = HomePlayResolver.SuccessProbability(Runner(20), 1, Situation(35, 1.8, arm: 95), Field, C);
        Assert.InRange(pEasy, 0.01, 0.99);
        Assert.InRange(pHard, 0.01, 0.99);
        Assert.True(pEasy > pHard);
    }

    [Fact]
    public void Resolve_IsDeterministic_ForSameSeed()
    {
        var runner = Runner(60, 55);
        var s = Situation(65, 2.9);
        var a = ScoreRate(runner, 2, s, trials: 500, seed: 42);
        var b = ScoreRate(runner, 2, s, trials: 500, seed: 42);
        Assert.Equal(a, b);
    }

    // --- 判定オーバーレイ（Issue #59）: Margin はSuccessProbabilityの内部計算と一致し、logistic(0)=0.5 ---

    [Fact]
    public void Margin_MatchesSuccessProbability_ViaLogistic()
    {
        var runner = Runner(60, 55);
        var s = Situation(70, 3.0);
        var margin = HomePlayResolver.Margin(runner, 2, s, Field, C);
        var prob = HomePlayResolver.SuccessProbability(runner, 2, s, Field, C);
        Assert.Equal(MathUtil.Clamp(MathUtil.Logistic(margin / C.HomeMarginScale), 0.01, 0.99), prob, 9);
    }

    [Fact]
    public void Margin_Zero_ImpliesFiftyFiftyProbability()
    {
        // margin=0 の境界ちょうどでは logistic(0)=0.5（HomeMarginScaleに関わらず）。
        var runner = Runner();
        // 処理点を調整してmarginがほぼ0になる地点を探索（境界=SuccessProbability≈0.5）。
        HomePlaySituation Near(double d) => Situation(d, 3.0);
        var lo = 10.0; var hi = 150.0;
        for (var i = 0; i < 60; i++)
        {
            var mid = (lo + hi) / 2;
            var m = HomePlayResolver.Margin(runner, 2, Near(mid), Field, C);
            if (m < 0) lo = mid; else hi = mid;
        }
        var boundaryMargin = HomePlayResolver.Margin(runner, 2, Near((lo + hi) / 2), Field, C);
        var boundaryProb = HomePlayResolver.SuccessProbability(runner, 2, Near((lo + hi) / 2), Field, C);
        Assert.True(Math.Abs(boundaryMargin) < 0.01, $"境界探索が収束していない: margin={boundaryMargin}");
        Assert.True(Math.Abs(boundaryProb - 0.5) < 0.01, $"margin≈0でも確率が0.5から離れている: {boundaryProb}");
    }

    [Fact]
    public void DefenseTime_UsesRelayForLongThrows_AndDirectForShort()
    {
        // 閾値(60m)を跨ぐと中継が入り、直線外挿より遅くなる（握り替え分）。
        var shortThrow = HomePlayResolver.DefenseTimeSeconds(Situation(50, 0.0), C);
        var longThrow = HomePlayResolver.DefenseTimeSeconds(Situation(90, 0.0), C);
        Assert.True(longThrow > shortThrow, "遠い送球ほど時間がかかる");
    }

    // --- 犠飛のタッチアップ（Issue #90, 設計書12 §3.5）: 純関数の単調性・方向・境界 ---

    private static double TagUpScoreRate(Player runner, HomePlaySituation s, int trials = 4000, ulong seed = 5)
    {
        var rng = new Xoshiro256Random(seed);
        var p = HomePlayResolver.TagUpHomeParams(C);
        var safe = 0;
        for (var i = 0; i < trials; i++)
            if (HomePlayResolver.TagUpResolveSafe(runner, 3, s, Field, C, p, rng)) safe++;
        return (double)safe / trials;
    }

    [Fact]
    public void TagUp_DeepFly_ScoresMoreThan_ShallowFly()
    {
        // 深い中堅フライ（処理点95m・遅い捕球）＞浅い左翼フライ（処理点45m・早い捕球）。
        // 起点は捕球時刻なので滞空は相殺し、送球距離（深さ）で決まる（Issue #90 の主眼）。
        var runner = Runner();
        var shallow = TagUpScoreRate(runner, Situation(45, 1.8));
        var deep = TagUpScoreRate(runner, Situation(95, 3.6));
        Assert.True(deep > shallow + 0.05, $"深いフライで生還率が上がっていない: shallow={shallow} deep={deep}");
    }

    [Fact]
    public void TagUp_FasterRunner_ScoresMore()
    {
        var s = Situation(62, 2.6); // 接戦帯（走力差が生還率に出る中程度の深さ）
        var slow = TagUpScoreRate(Runner(speed: 25), s);
        var fast = TagUpScoreRate(Runner(speed: 85), s);
        Assert.True(fast > slow + 0.05, $"走力でタッチアップ生還率が上がっていない: slow={slow} fast={fast}");
    }

    [Fact]
    public void TagUp_StrongerArm_GunsDownMore()
    {
        var runner = Runner();
        var s = new Func<int, HomePlaySituation>(arm => Situation(62, 2.6, arm));
        var weak = TagUpScoreRate(runner, s(20));
        var cannon = TagUpScoreRate(runner, s(90));
        Assert.True(weak > cannon + 0.03, $"強肩でタッチアップ憤死が増えていない: weak={weak} cannon={cannon}");
    }

    [Fact]
    public void TagUp_RunnerStartsAtCatch_HangTimeCancels()
    {
        // タッチアップは走者も守備も捕球時刻が共通起点＝margin は捕球時刻に依存しない（同じ処理点なら滞空が
        // 違っても margin は不変）。深さ（送球距離）だけが効くことの契約。
        var runner = Runner(60, 55);
        var early = HomePlayResolver.TagUpMargin(runner, 3, Situation(70, 1.5), Field, C, HomePlayResolver.TagUpHomeParams(C));
        var late = HomePlayResolver.TagUpMargin(runner, 3, Situation(70, 4.5), Field, C, HomePlayResolver.TagUpHomeParams(C));
        Assert.Equal(early, late, 9);
    }

    [Fact]
    public void TagUp_Probability_IsClampedAndDeterministic()
    {
        var p = HomePlayResolver.TagUpHomeParams(C);
        var pEasy = HomePlayResolver.TagUpSuccessProbability(Runner(85), 3, Situation(110, 4.5), Field, C, p);
        var pHard = HomePlayResolver.TagUpSuccessProbability(Runner(20), 3, Situation(25, 1.0, arm: 95), Field, C, p);
        Assert.InRange(pEasy, 0.01, 0.99);
        Assert.InRange(pHard, 0.01, 0.99);
        Assert.True(pEasy > pHard, $"深いフライが浅いフライより生還しやすくない: easy={pEasy} hard={pHard}");
        Assert.Equal(TagUpScoreRate(Runner(60, 55), Situation(65, 2.7), 500, 42),
                     TagUpScoreRate(Runner(60, 55), Situation(65, 2.7), 500, 42));
    }
}
