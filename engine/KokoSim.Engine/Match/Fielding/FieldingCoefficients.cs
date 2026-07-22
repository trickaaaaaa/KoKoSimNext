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

    // --- 打球のバウンド（Issue #63 / OPEN-QUESTIONS Q14）: 着地後も物理層で軌道を継続する ---

    /// <summary>接地の反発係数 e（硬式球×内野土/芝の平均）。バウンド頂点は e² で減衰する。</summary>
    public double BounceRestitution { get; init; } = 0.45;

    /// <summary>接地時の動摩擦係数 μ。水平の減速量 = μ(1+e)|v⊥|。</summary>
    public double BounceFrictionMu { get; init; } = 0.50;

    /// <summary>
    /// 1回の接地で保持する水平速度の下限比（滑り→転がり遷移。一様球の理論値 5/7 ≒ 0.714）。
    /// 摩擦の力積がこれを越えて効くことはない＝水平に近い打球は滑って伸びる。
    /// </summary>
    public double BounceRollingRetention { get; init; } = 0.7143;

    /// <summary>これを下回る鉛直初速[m/s]の弾みは転がり扱いにする（弾み列の打ち切り）。</summary>
    public double BounceMinLaunchMps { get; init; } = 0.60;

    /// <summary>「バウンド」に分類する最大バウンド頂点[m]（これ未満は「ゴロ」）。</summary>
    public double BouncerApexThresholdM { get; init; } = 1.20;

    /// <summary>「ライナー」に分類する頂点÷水平到達距離の上限（これ以下は低い弾道＝ライナー）。</summary>
    public double LinerApexRangeRatio { get; init; } = 0.10;

    /// <summary>内野手がグラブを出して捕れる高さの上限[m]（跳んで届く範囲を含む）。これを越える弾みは頭上を抜ける。</summary>
    public double InfielderReachHeightM { get; init; } = 2.20;

    /// <summary>
    /// 内野手の横の守備範囲[m]（グラブ＋ダイビングで届く半径）。打球がこの半径内を通れば
    /// 内野手は処理できる（＝内野を抜けない）。どの内野手もこの範囲で捕えられず外野まで達する強い打球だけが
    /// 「内野を抜ける」（Issue #63 やること2＝ホール／頭上の二次展開）。大きいほど内野を抜けにくい＝帯校正ノブ。
    /// </summary>
    public double InfielderFieldingRadiusM { get; init; } = 4.20;

    /// <summary>
    /// 高く弾むバウンド（チョッパー）で内野手が待たされる時間の係数（Issue #63 やること1）。
    /// 待ち時間 = この係数 × √(2×最大バウンド頂点/g)＝頂点からの自由落下時間。
    /// 大きいほど高バウンドが内野安打になりやすい。0で従来（待ちなし）。
    /// </summary>
    public double ChopperWaitFactor { get; init; } = 0.55;

    /// <summary>内野境界の通過時刻を求める走査の時間刻み[s]（頭上抜けの判定精度）。</summary>
    public double InfieldInterceptStepSeconds { get; init; } = 0.02;

    /// <summary>イレギュラーバウンドの発生確率（一律。球場の土質との接続は将来の別Issue）。</summary>
    public double IrregularBounceProb { get; init; } = 0.030;

    /// <summary>イレギュラー時の横ぶれの最大角[deg]（±この範囲で一様）。</summary>
    public double IrregularBounceLateralDeg { get; init; } = 12.0;

    /// <summary>イレギュラー時の反発係数の揺らぎ幅（e が ×[1−この値, 1+この値] の範囲で振れる）。</summary>
    public double IrregularBounceRestitutionSwing { get; init; } = 0.60;

    /// <summary>イレギュラーバウンドで処理する打球のエラー確率への加算。</summary>
    public double ErrorIrregularBonus { get; init; } = 0.10;
}
