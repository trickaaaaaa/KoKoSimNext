using System;
using KokoSim.Engine.Core;
using KokoSim.Engine.Players;

namespace KokoSim.Engine.Match.Game;

/// <summary>ライナー併殺（設計書12 §4, G2）の結果。塁へ戻れたか、戻れず憤死したか。</summary>
public enum DoubledOffResult
{
    Safe,
    DoubledOff,
}

/// <summary>
/// コンタクト始動（エンドラン等, <see cref="Tactics.StartType.Contact"/>）の走者が、打球を空中で捕られた際に
/// 元の塁へ戻れるかの時間の勝負（設計書12 §4, G2）。<see cref="HomePlayResolver"/>・<see cref="StealResolver"/>
/// と同型。走者: 捕球までに稼いだリード(捕球時刻−始動反応)×走力 を、反転の判断遅延＋同じ距離を走って戻る。
/// 守備: 捕球点から塁までの送球（握り替え＋送球＋タッチ）。すべて接触（打った瞬間）＝0秒基準。
/// </summary>
public static class DoubledOffResolver
{
    /// <summary>走者が捕球までに稼いだ塁からのリード[m]。二次リードはコーチング上の目安距離で
    /// 決まるため基準走力を使う（個人の走力ではない＝戻りの所要時間でこそ走力差が効く）。</summary>
    public static double LeadGainedM(double caughtAtSeconds, BaserunningCoefficients c)
    {
        var capped = Math.Min(caughtAtSeconds, c.LinerCommitCapSeconds);
        var runSeconds = Math.Max(0.0, capped - c.LinerBreakReactionSeconds);
        return runSeconds * c.LinerReferenceSprintSpeedMps;
    }

    /// <summary>走者が塁へ戻るのに要する時間[s]（反転の判断遅延＋戻りの距離÷走力）。</summary>
    public static double RunnerReturnSeconds(Player runner, double caughtAtSeconds, BaserunningCoefficients c)
    {
        var lead = LeadGainedM(caughtAtSeconds, c);
        return c.DoubledOffReverseSeconds + lead / runner.ToFielder().SprintSpeedMps;
    }

    /// <summary>
    /// 守備の送球所要時間[s]（捕球点→塁, 握り替え＋送球＋タッチ）。fielderFieldingAbility=処理野手の守備(Fielding)
    /// [1〜100]（トランスファー秒数の起点, Issue #36）。既定50＝従来の固定秒数と恒等。
    /// </summary>
    public static double DefenseReturnSeconds(
        double catchToBaseM, double throwSpeedMps, BaserunningCoefficients c, int fielderFieldingAbility = 50)
    {
        var transferSeconds = new FielderAttributes { Fielding = fielderFieldingAbility }.TransferSeconds(
            c.DoubledOffTransferSeconds, c.TransferFieldingSlope, c.TransferSecondsFloor);
        return transferSeconds + catchToBaseM / throwSpeedMps + c.DoubledOffTagSeconds;
    }

    /// <summary>塁へ戻れる（Safe）確率（走塁と同式のlogistic）。</summary>
    public static double SuccessProbability(
        Player runner, double caughtAtSeconds, double catchToBaseM, double throwSpeedMps, BaserunningCoefficients c,
        int fielderFieldingAbility = 50)
    {
        var margin = DefenseReturnSeconds(catchToBaseM, throwSpeedMps, c, fielderFieldingAbility)
                     - RunnerReturnSeconds(runner, caughtAtSeconds, c) + c.DoubledOffSuccessBias;
        return MathUtil.Clamp(MathUtil.Logistic(margin / c.DoubledOffMarginScale), 0.01, 0.99);
    }

    public static DoubledOffResult Resolve(
        Player runner, double caughtAtSeconds, double catchToBaseM, double throwSpeedMps,
        BaserunningCoefficients c, IRandomSource rng, int fielderFieldingAbility = 50)
        => MathUtil.Chance(SuccessProbability(runner, caughtAtSeconds, catchToBaseM, throwSpeedMps, c, fielderFieldingAbility), rng)
            ? DoubledOffResult.Safe
            : DoubledOffResult.DoubledOff;
}
