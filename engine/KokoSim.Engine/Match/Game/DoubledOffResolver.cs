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
        return c.DoubledOffReverseSeconds + lead / runner.ToFielder(c.AbilityEffect).SprintSpeedMps;
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

    // === ハーフウェーから戻る勝負（issue #1130）===
    // 読んで待った走者は、捕球の時点の位置（ハーフウェー）から元の塁へ戻る。飛び出し（コンタクト始動）と
    // 違って距離はリードの式でなく、走者がその時点で実際に居る距離をそのまま使う。

    /// <summary>捕球の時点で <paramref name="leadM"/> 離れている走者の戻りの所要[s]（反転遅延＋距離÷走力）。</summary>
    public static double RunnerReturnSecondsFromLead(Player runner, double leadM, BaserunningCoefficients c)
        => c.DoubledOffReverseSeconds + Math.Max(0.0, leadM) / runner.ToFielder(c.AbilityEffect).SprintSpeedMps;

    /// <summary>距離を直接与える margin[s]（守備所要 − 走者所要 ＋ バイアス。正で戻れる側）。</summary>
    public static double MarginFromLead(
        Player runner, double leadM, double catchToBaseM, double throwSpeedMps, BaserunningCoefficients c,
        int fielderFielding = 50)
        => DefenseReturnSeconds(catchToBaseM, throwSpeedMps, c, fielderFielding)
           - RunnerReturnSecondsFromLead(runner, leadM, c) + c.DoubledOffSuccessBias;

    /// <summary>距離を直接与えて戻れる確率（乱数なし・送り判断と同じクランプ）。</summary>
    public static double SuccessProbabilityFromLead(
        Player runner, double leadM, double catchToBaseM, double throwSpeedMps, BaserunningCoefficients c,
        int fielderFielding = 50)
        => MathUtil.Clamp(MathUtil.Logistic(
            MarginFromLead(runner, leadM, catchToBaseM, throwSpeedMps, c, fielderFielding) / c.DoubledOffMarginScale), 0.01, 0.99);

    /// <summary>距離を直接与えて勝負を引く（戻し送球1本の逸れ＋勝負の2つ。<see cref="Resolve(Player,double,double,double,BaserunningCoefficients,IRandomSource,int,double,int,out KokoSim.Engine.Match.Fielding.ThrowScatterModel.Scatter)"/> と同じ作法）。</summary>
    public static DoubledOffResult ResolveFromLead(
        Player runner, double leadM, double catchToBaseM, double throwSpeedMps,
        BaserunningCoefficients c, IRandomSource rng, int fielderFielding, int throwAccuracy,
        out KokoSim.Engine.Match.Fielding.ThrowScatterModel.Scatter throwScatter)
    {
        var (scatter, noise) = HomePlayResolver.SampleThrowScatter(throwAccuracy, c, rng);
        throwScatter = scatter;
        var margin = MarginFromLead(runner, leadM, catchToBaseM, throwSpeedMps, c, fielderFielding) + noise;
        return MathUtil.Chance(MathUtil.Logistic(margin / c.DoubledOffMarginScale), rng)
            ? DoubledOffResult.Safe
            : DoubledOffResult.DoubledOff;
    }

    // 勝負の抽選は 1%/99% でクランプしない（下の逸れ付きの Resolve と同じ・issue #801/#1241）。
    public static DoubledOffResult Resolve(
        Player runner, double caughtAtSeconds, double catchToBaseM, double throwSpeedMps,
        BaserunningCoefficients c, IRandomSource rng, int fielderFielding = 50, double extraLeadM = 0.0)
        => MathUtil.Chance(
                MathUtil.Logistic(Margin(runner, caughtAtSeconds, catchToBaseM, throwSpeedMps, c, fielderFielding, extraLeadM)
                    / c.DoubledOffMarginScale), rng)
            ? DoubledOffResult.Safe
            : DoubledOffResult.DoubledOff;

    /// <summary>
    /// 戻し送球1本の逸れ（issue #859）を引き、その本の遅れ（期待値との差＝ゼロ平均）を守備側へ足して勝負を引く。
    /// 逸れは判定に使った値そのものを <paramref name="throwScatter"/> で返す（絵の受け手の寄せに使う）。
    /// </summary>
    public static DoubledOffResult Resolve(
        Player runner, double caughtAtSeconds, double catchToBaseM, double throwSpeedMps,
        BaserunningCoefficients c, IRandomSource rng, int fielderFielding, double extraLeadM, int throwAccuracy,
        out KokoSim.Engine.Match.Fielding.ThrowScatterModel.Scatter throwScatter)
    {
        var (scatter, noise) = HomePlayResolver.SampleThrowScatter(throwAccuracy, c, rng);
        throwScatter = scatter;
        var margin = Margin(runner, caughtAtSeconds, catchToBaseM, throwSpeedMps, c, fielderFielding, extraLeadM) + noise;
        // 勝負の抽選は 1%/99% でクランプしない（issue #801 と同じ理由・#1130 で揃えた）。クランプは読み・送り判断のためのもので、
        // 抽選に掛けると走者が数秒早く戻っていても 1% でアウトになり、その秒を説明する絵が無い。
        return MathUtil.Chance(MathUtil.Logistic(margin / c.DoubledOffMarginScale), rng)
            ? DoubledOffResult.Safe
            : DoubledOffResult.DoubledOff;
    }
}
