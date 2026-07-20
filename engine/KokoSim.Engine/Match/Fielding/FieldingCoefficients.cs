using System;

namespace KokoSim.Engine.Match.Fielding;

/// <summary>守備解決の係数（YAML駆動, 統計回帰で調整）。</summary>
public sealed record FieldingCoefficients
{
    /// <summary>守備位置適性の基準値（この適性で補正なし＝×1.0）。設計書01 §1.1。</summary>
    public double AptitudeNeutral { get; init; } = 50.0;
    /// <summary>適性1ポイントあたりの実効守備力補正の傾き。</summary>
    public double AptitudeSlopePerPoint { get; init; } = 0.006;
    /// <summary>適性補正の下限倍率（慣れないポジションのペナルティ上限）。</summary>
    public double AptitudeFactorMin { get; init; } = 0.60;
    /// <summary>適性補正の上限倍率（本職ポジションのボーナス上限）。</summary>
    public double AptitudeFactorMax { get; init; } = 1.30;

    /// <summary>
    /// 守備位置適性→実効守備力の倍率（設計書01 §1.1）。本職＝高適性でフル、慣れないポジション＝ペナルティ。
    /// 基準50で×1.0。表示層(Fielding)に乗算してから物理層へ通す（二層構造を保つ）。
    /// </summary>
    public double AptitudeFactor(int aptitude) => Math.Clamp(
        1.0 + (aptitude - AptitudeNeutral) * AptitudeSlopePerPoint, AptitudeFactorMin, AptitudeFactorMax);

    /// <summary>フライと判定する最高到達点の閾値[m]。これ以上は空中捕球の対象。</summary>
    public double FlyApexThresholdM { get; init; } = 2.5;

    /// <summary>空中捕球に許容する到達時間の倍率（滞空時間×この値まで間に合えば捕球）。</summary>
    public double CatchReachFactor { get; init; } = 1.00;

    /// <summary>送球の持ち替え・リリース所要[s]。</summary>
    public double ThrowTransferSeconds { get; init; } = 0.70;

    /// <summary>
    /// 内野ゴロ処理の実戦オーバーヘッド[s]（設計書02 §4.1b の時間軸校正）。捕球姿勢づくり・ステップ・
    /// 打球が野手へ転がる時間を集約した項。走者を実戦タイム（本塁→一塁≈4.2s）へ合わせても
    /// 内野安打率が現実的に保たれるよう、守備側も同じ秒軸へ引き上げる。
    /// </summary>
    public double InfieldPlayOverheadSeconds { get; init; } = 0.30;

    /// <summary>
    /// 打者走者の実戦オフセット[s]（設計書02 §4.1b）。反応＋スイング後の走り出し＋静止からの加速不足を含む。
    /// 「距離÷天井速度」だけでは実戦タイムより速すぎるため、この offset で本塁→一塁を現実値へ合わせる
    /// （走力50・右打者で約4.2s に校正）。
    /// </summary>
    public double RunnerReactionSeconds { get; init; } = 0.45;

    /// <summary>左打者の一塁到達短縮[s]（設計書02 §4.1b/§1.1c: 打席が一塁側に近く到達が速い）。</summary>
    public double LeftBatterFirstStepBonusSeconds { get; init; } = 0.13;

    /// <summary>内野が処理できる打球の最大到達距離[m]（これを越えて転がると内野を抜ける）。</summary>
    public double InfieldDepthM { get; init; } = 46.0;

    /// <summary>一塁でのアウト判定の安全マージン[s]（守備側がこの秒数だけ余裕を持てばアウト）。</summary>
    public double ForceOutMarginSeconds { get; init; } = 0.0;

    /// <summary>捕球エラーの基準確率（Catching50時）。</summary>
    public double ErrorBaseProb { get; init; } = 0.018;
    /// <summary>捕球(Catching−50)あたりのエラー減少。</summary>
    public double ErrorCatchingSlope { get; init; } = 0.0003;

    /// <summary>二塁打と判定する安打の最小到達距離[m]（外野の間を抜けた深い打球）。</summary>
    public double DoubleDistanceM { get; init; } = 80.0;
    /// <summary>三塁打と判定する最小到達距離[m]（稀。深い gap のみ）。</summary>
    public double TripleDistanceM { get; init; } = 113.0;
}
