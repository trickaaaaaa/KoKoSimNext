namespace KokoSim.Engine.Season.Events;

/// <summary>
/// 週次イベントの発火係数（設計書03 §5 / 04 §3.2・issue #609）。
/// 従来 <see cref="EventScheduler"/> にハードコードされていた「同一週最大1件・発火率50%」を係数へ出したもの。
/// <b>C#既定は data/coefficients.yaml の events: と一致させること</b>（不変条件#4・
/// <c>CoefficientsDefaultsConsistencyTests</c> が全プロパティを固定する）。
/// </summary>
public sealed record EventCoefficients
{
    /// <summary>
    /// ランダム/選択イベントを1件でも引く確率。毎週何か起きると煩雑なので抑える。
    /// 固定イベント（カレンダー連動）はこの確率を通さず必ず発火する。
    /// </summary>
    public double WeeklyFireProbability { get; init; } = 0.5;

    /// <summary>1週に提示するランダム/選択イベントの上限件数（固定イベントは別枠で必ず出る）。</summary>
    public int MaxRandomPerWeek { get; init; } = 1;
}
