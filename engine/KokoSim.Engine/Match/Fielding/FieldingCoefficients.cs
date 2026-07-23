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

    /// <summary>
    /// 空中捕球の到達可否に効く守備力の傾き（打球判断＝走路取りの巧拙）。
    /// 実効倍率 = CatchReachFactor ×(1 + (Fielding−50)×この値)。守備50で 1.0＝恒等（帯不変）。
    /// </summary>
    public double CatchReachFieldingSlope { get; init; } = 0.004;

    /// <summary>
    /// 空中捕球で走れる時間の上限[s]（滞空時間比例のままだと深い飛球ほど守備範囲が無制限に広がるため）。
    /// 走れる距離 ≈ (この値 − 反応遅延)×スプリント速度。
    /// </summary>
    public double CatchReachCapSeconds { get; init; } = 4.00;

    /// <summary>送球の持ち替え・リリース所要[s]。</summary>
    public double ThrowTransferSeconds { get; init; } = 0.70;

    /// <summary>
    /// トランスファー倍率の守備(Fielding)傾き（Issue #36）。倍率 = max(min, 1 − (Fielding−50)×この値)。
    /// 守備50で×1.0＝恒等（帯不変）。走塁系（捕手ポップ/外野返球/中継/帰塁）の同名係数と値を揃える。
    /// </summary>
    public double ThrowTransferFieldingSlope { get; init; } = 0.004;

    /// <summary>トランスファー倍率の下限（最速側のクランプ, Issue #36）。守備が高いほどここへ張り付く。</summary>
    public double ThrowTransferFactorMin { get; init; } = 0.70;

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

    // 失策モデル（issue #123, 2026-07-23）。既定値は data/coefficients.yaml と同値＝実ゲーム（Unity は
    // new GameContext() の既定を使う）とsim/テストで同じ守備力連動になるよう揃える。変更時は
    // determinism-baseline.txt の再生成が必要（DeterminismBaselineDump）。
    /// <summary>捕球エラーの基準確率（Catching50時）＝両軍計≈2.4/試合。</summary>
    public double ErrorBaseProb { get; init; } = 0.064;
    /// <summary>捕球(Catching−50)あたりのエラー変化（守備が平均より低い側＝弱小の傾き）。急にして弱小同士を大量失策に。</summary>
    public double ErrorCatchingSlope { get; init; } = 0.0065;
    /// <summary>守備が平均(50)より高い側の傾き（弱小側 ErrorCatchingSlope と非対称）。緩くして精鋭側に
    /// 「守備を上げるほど失策が減る」勾配を残す（下限へ潰さない＝能力向上の意味を保つ, issue #123 2026-07-23）。</summary>
    public double ErrorCatchingSlopeStrong { get; init; } = 0.001;
    /// <summary>捕球エラー確率の下限クランプ（守備力が高いほどここへ張り付く）。</summary>
    public double ErrorMinProb { get; init; } = 0.001;
    /// <summary>捕球エラー確率の上限クランプ（守備力が低いほどここへ張り付く）。守備が低い同士の試合を
    /// 大量失策（両校計10個規模）にするための天井。</summary>
    public double ErrorMaxProb { get; init; } = 0.30;

    // --- 塁打数の決定（Issue #24: 距離しきい値を廃し、転がり＋幾何＋走力の連続量で決める） ---

    /// <summary>着地後の転がりの減速度[m/s²]（芝・土の摩擦＋バウンドの損失を集約）。</summary>
    public double RollDecelMps2 { get; init; } = 2.60;

    /// <summary>水平に近い打球（ライナー）が最初のバウンドで保持する前進速度の割合。滑って伸びる。</summary>
    public double RollRetentionFlat { get; init; } = 0.72;

    /// <summary>真上から落ちる打球（大飛球）が最初のバウンドで保持する前進速度の割合。失速して死ぬ。</summary>
    public double RollRetentionSteep { get; init; } = 0.25;

    /// <summary>フェンスに達した打球の跳ね返り処理に要する追加時間[s]（カロムの方向は当面無視）。</summary>
    public double FenceCaromSeconds { get; init; } = 0.40;

    /// <summary>外野手が転がる打球を拾い上げるのに要する時間[s]（内野ゴロの InfieldPlayOverheadSeconds 相当）。</summary>
    public double OutfieldPickupSeconds { get; init; } = 0.30;

    /// <summary>
    /// 送球距離1mあたりの失速係数[1/m]。到達時間 = 距離÷送球速度×(1 + 距離×この値)。
    /// 長い送球ほど山なり／中継が要るぶん遅くなることを、しきい値なしの連続量で表す。
    /// </summary>
    public double ThrowDistanceDragPerM { get; init; } = 0.0045;

    /// <summary>一塁到達後の巡航速度倍率（静止からの加速を含む「本塁→一塁」平均速度に対する比）。</summary>
    public double RunningTopSpeedFactor { get; init; } = 1.15;

    /// <summary>塁を回るロス[s]（ベースを踏んでの方向転換）。</summary>
    public double BaseTurnSeconds { get; init; } = 0.30;

    /// <summary>
    /// 進塁を試みる判断マージン[s]（走者所要＋この値 ≤ 送球到達 なら次の塁を陥れる）。
    /// 送球の逸れ・捕球・タッチのロスがあるため「送球より僅かに遅い」程度なら実戦では次の塁を陥れる。
    /// </summary>
    public double ExtraBaseMarginSeconds { get; init; } = 0.40;
}
