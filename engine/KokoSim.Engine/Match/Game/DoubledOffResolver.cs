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
    /// <param name="extraLeadM">接触より前に稼いでいたリード[m]（盗塁で投球と同時に走っていた走者。
    /// <see cref="BaserunningCoefficients.StealBreakExtraLeadM"/>）。既定0＝コンタクト始動。</param>
    public static double LeadGainedM(double caughtAtSeconds, BaserunningCoefficients c, double extraLeadM = 0.0)
    {
        var capped = Math.Min(caughtAtSeconds, c.LinerCommitCapSeconds);
        var runSeconds = Math.Max(0.0, capped - c.LinerBreakReactionSeconds);
        var maxLead = runSeconds * c.LinerReferenceSprintSpeedMps + Math.Max(0.0, extraLeadM);
        // コミット上限を過ぎたら打球を見ながら塁の方へ戻る（長い滞空ほど捕球時点のリードは小さい）。
        var retreat = c.DoubledOffRetreatSpeedMps * Math.Max(0.0, caughtAtSeconds - c.LinerCommitCapSeconds);
        return Math.Max(0.0, maxLead - retreat);
    }

    /// <summary>飛び出しの最大リード[m]（コミット上限まで走った時点。絵で「ここまで出てから戻る」の折り返し点）。</summary>
    public static double MaxLeadM(double caughtAtSeconds, BaserunningCoefficients c, double extraLeadM = 0.0)
    {
        var capped = Math.Min(caughtAtSeconds, c.LinerCommitCapSeconds);
        var runSeconds = Math.Max(0.0, capped - c.LinerBreakReactionSeconds);
        return runSeconds * c.LinerReferenceSprintSpeedMps + Math.Max(0.0, extraLeadM);
    }

    /// <summary>走者が塁へ戻るのに要する時間[s]（反転の判断遅延＋戻りの距離÷走力）。</summary>
    public static double RunnerReturnSeconds(
        Player runner, double caughtAtSeconds, BaserunningCoefficients c, double extraLeadM = 0.0)
    {
        var lead = LeadGainedM(caughtAtSeconds, c, extraLeadM);
        return c.DoubledOffReverseSeconds + lead / runner.ToFielder().SprintSpeedMps;
    }

    /// <summary>守備の送球所要時間[s]（捕球点→塁, 握り替え＋送球＋タッチ）。
    /// fielderFielding=捕球野手の守備。握り替えを能力で伸縮（Issue #36）。既定50＝現行固定値と恒等。</summary>
    public static double DefenseReturnSeconds(double catchToBaseM, double throwSpeedMps, BaserunningCoefficients c,
        int fielderFielding = 50)
    {
        var transfer = c.DoubledOffTransferSeconds
                       * new FielderAttributes { Fielding = fielderFielding }.TransferFactor(c.TransferFieldingSlope, c.TransferFactorMin);
        return transfer + catchToBaseM / throwSpeedMps + c.DoubledOffTagSeconds;
    }

    /// <summary>margin[s]（守備所要 − 走者所要 ＋ バイアス。正で塁へ戻れる側）。際どさ判定（CloseCall）にも使う。</summary>
    public static double Margin(
        Player runner, double caughtAtSeconds, double catchToBaseM, double throwSpeedMps, BaserunningCoefficients c,
        int fielderFielding = 50, double extraLeadM = 0.0)
        => DefenseReturnSeconds(catchToBaseM, throwSpeedMps, c, fielderFielding)
           - RunnerReturnSeconds(runner, caughtAtSeconds, c, extraLeadM) + c.DoubledOffSuccessBias;

    /// <summary>塁へ戻れる（Safe）確率（走塁と同式のlogistic）。</summary>
    public static double SuccessProbability(
        Player runner, double caughtAtSeconds, double catchToBaseM, double throwSpeedMps, BaserunningCoefficients c,
        int fielderFielding = 50, double extraLeadM = 0.0)
    {
        var margin = Margin(runner, caughtAtSeconds, catchToBaseM, throwSpeedMps, c, fielderFielding, extraLeadM);
        return MathUtil.Clamp(MathUtil.Logistic(margin / c.DoubledOffMarginScale), 0.01, 0.99);
    }

    public static DoubledOffResult Resolve(
        Player runner, double caughtAtSeconds, double catchToBaseM, double throwSpeedMps,
        BaserunningCoefficients c, IRandomSource rng, int fielderFielding = 50, double extraLeadM = 0.0)
        => MathUtil.Chance(
                SuccessProbability(runner, caughtAtSeconds, catchToBaseM, throwSpeedMps, c, fielderFielding, extraLeadM), rng)
            ? DoubledOffResult.Safe
            : DoubledOffResult.DoubledOff;
}
