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
    /// 内野ゴロ処理のオーバーヘッド[s]（設計書02 §4.1b の時間軸校正）。捕球姿勢づくり・ステップ。
    /// <see cref="ThrowTransferSeconds"/> と足したものが「捕ってから離すまで」＝守備50で 0.80s
    /// （守備90で0.69s／守備20で0.88s）。実際の内野手の 0.6〜0.9秒に合わせてある。
    /// issue #358 で 0.88→0.10 へ引き戻した。0.88 は #261 の得点環境の戻しを「内野手の処理時間」という
    /// 物理量に載せた値で、握り替え0.70と合わせて<b>捕球点で1.58秒ボールを持つ</b>＝実際の約2倍だった
    /// （判定だけでなく2D俯瞰の絵にもそのまま出ていた）。得点環境の戻しは
    /// <see cref="ForceOutMarginSeconds"/>（判定側の余裕）へ移し、物理層は現実の秒に戻す。
    /// data/coefficients.yaml と同値。
    /// </summary>
    public double InfieldPlayOverheadSeconds { get; init; } = 0.10;

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

    /// <summary>
    /// 一塁でのアウト判定の安全マージン[s]（守備側がこの秒数だけ余裕を持てばアウト）。
    /// issue #358 で 0.0→0.60。<see cref="InfieldPlayOverheadSeconds"/> の水増しをやめた分の
    /// 得点環境をここへ移した項＝<b>物理層（＝タイムラインの絵）は現実の秒のまま、内野安打の頻度だけを
    /// 判定側で校正する</b>。0.0 のままだと内野安打が内野ゴロの1.5%へ潰れる（実測。実際は6〜7%規模）。
    /// 実体は①シムに無い内野安打の源（握り替えのもたつき・弾き・守備位置の揺らぎ＝守備所要の分散が0）と
    /// ②捕球点が実戦より本塁寄り＝送球距離が短いぶんの残差。①を分散として物理層へ入れられれば
    /// その分だけ小さくできる（将来の増分）。値は打率・BABIP帯から逆算した 0.60
    /// （0.55 だと AVG が帯下限 .240 に張り付く）。data/coefficients.yaml と同値。
    ///
    /// <para>issue #630（2026-08-13・オーナー選択）で 0.60→0.72。前進捕球（<see cref="ChargeSlowBallSpeedMps"/>）で
    /// 守備所要が平均 0.046s 縮んだ分をここへ戻し、内野安打の水準（内野ゴロの 9.6%）と AVG 帯を据え置いた。
    /// <b>チャージは守備を速くする＝内野安打を減らす方向</b>なので、この下駄は薄くではなく厚くなる
    /// （issue の当初想定とは逆向き）。実測（60000打席/10000打席×seed 42・7・2024）:
    /// 0.72 で内野安打 9.7%・AVG .251/.244/.237＝修正前（9.6%・.250/.244/.237）と同等。</para>
    /// </summary>
    public double ForceOutMarginSeconds { get; init; } = 0.72;

    /// <summary>
    /// 飛球の捕球余裕[s]の際どさしきい値（判定オーバーレイ「フェア」, Issue #328②）。
    /// |CatchMarginSeconds| がこれ未満の落球だけ、着地の瞬間に「フェア」を表示する。
    /// <see cref="Game.BaserunningCoefficients.CloseCallMarginSeconds"/> と同型の表示専用しきい値。
    /// </summary>
    public double CatchCloseCallMarginSeconds { get; init; } = 0.15;

    // 失策モデル（issue #123, 2026-07-23）。既定値は data/coefficients.yaml と同値＝実ゲーム（Unity は
    // new GameContext() の既定を使う）とsim/テストで同じ守備力連動になるよう揃える。変更時は
    // determinism-baseline.txt の再生成が必要（DeterminismBaselineDump）。
    /// <summary>捕球エラーの基準確率（Catching50時）。送球ロール(ThrowErrorBaseProb)有効化に伴い再配分（issue #169, 2026-07-23）：
    /// 旧0.064（捕球のみで両軍計≈2.4/試合校正）から、内野ゴロ失策の約40%を送球由来へ移す分だけ捕球側を下げた値。
    /// issue #532（2026-08-08）で×1.29（0.0384→0.0495）：外野フライ落球を難度連動（<see cref="FlyErrorBaseProb"/>）で減らした分、
    /// errors_per_game を現行水準（≈2.4）に保つため内野ゴロ側を引き上げた（捕球60/送球40の按分は維持）。</summary>
    public double ErrorBaseProb { get; init; } = 0.0495;
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

    // 空中捕球（フライ落球）の失策モデル（issue #532, 2026-08-08）。内野ゴロの ErrorBaseProb（＝捕球60%/送球40%の
    // 按分で校正した内野向けの基準）を共有していたため、平凡なフライも際どい飛球も同じ確率で落球し、外野が全失策の
    // 約39%を占めていた。フライ専用の基準確率＋捕球余裕（catchMargin＝物理層の滞空予算−到達所要）連動の難度で
    // 伸縮させる（不変条件#1: catchMargin は滞空時間と到達時間から出た物理量）。乱数消費は従来どおり1ロールのまま
    // （確率だけが変わる＝決定論 baseline の再生成を伴う。不変条件#2 の同シード同結果は保たれる）。
    /// <summary>
    /// フライ落球の基準確率（捕球余裕≈0＝ぎりぎりの飛球・Catching50時）。<see cref="ErrorBaseProb"/>（内野ゴロ）とは
    /// 独立に校正する。実効確率は <see cref="FlyErrorRoutineMarginSeconds"/> による難度で 0（平凡）〜この値（際どい）へ伸縮する。
    /// </summary>
    public double FlyErrorBaseProb { get; init; } = 0.20;
    /// <summary>
    /// フライを「平凡（難度0＝落球確率が下限 <see cref="ErrorMinProb"/> 近くへ落ちる）」とみなす捕球余裕[s]。
    /// 難度 = clamp(1 − catchMargin/この値, 0, 1)。余裕がこの秒数以上ある飛球は難度0（＝ほぼ確実に捕る）、
    /// 余裕0で難度1（＝基準確率フル）。大きいほど「難しい飛球」の範囲が広がり外野失策が増える＝帯校正ノブ。
    /// </summary>
    public double FlyErrorRoutineMarginSeconds { get; init; } = 0.35;

    // 送球エラー（内野ゴロ→一塁の2段階目, Issue #37 / design-14 P1-6b）。捕球ロール(Catching)の後に
    // 送球ロール(ThrowAccuracy)を引く。既定 base=0.0 は「送球ロールを一切引かない」＝現行の捕球のみ判定と
    // 乱数消費順・結果とも完全一致（ErrorExtraAdvanceProb と同じ gating 方式で決定論を保つ）。
    /// <summary>送球エラーの基準確率（ThrowAccuracy50時）。issue #169で 0.0→0.0266 に正値化＝実ゲームで悪送球が発火。
    /// Ac50 で内野ゴロ失策の約40%が送球由来になる按分（残り≈60%が捕球由来 ErrorBaseProb）。
    /// issue #532 で×1.29（0.0266→0.0343・捕球60/送球40の按分維持）。決定論baselineは再生成済み。</summary>
    public double ThrowErrorBaseProb { get; init; } = 0.0343;
    /// <summary>送球精度(ThrowAccuracy−50)あたりの送球エラー確率の減少幅。精度が高いほど悪送球が減る。
    /// クランプは捕球エラーと同じ ErrorMinProb/ErrorMaxProb を流用。</summary>
    public double ThrowErrorAccuracySlope { get; init; } = 0.004;

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

    /// <summary>
    /// 「ライナー」に分類する頂点÷水平到達距離の上限（これ以下は低い弾道＝ライナー）。issue #451 で 0.10→0.15。
    /// 旧0.10では「60m飛んで頂点7〜10mの平たい球」が「フライ」を名乗り、絵（高く上がるフライ弧）と形（弾丸ライナー）が
    /// 食い違っていた（外野フライ頂点 p50=16m＝実物20〜35mの下端未満）。この分類は表示・実況（TimelineBuilder の
    /// 「痛烈なライナー！」/「高々と上がった！」）専用で、捕球可否・送球判定は <see cref="FlyApexThresholdM"/>（apex≥2.5m）で
    /// 別に決まる＝**帯不変**（BABIP・本塁打率・得点は動かない）。0.15 で外野フライ（range≥70m）の頂点 p50 が
    /// 実物オーダー（20m台・seed42/7/2024 で 20.6〜20.9m）に入り、平たい弾道はライナーとして表示される。
    /// </summary>
    public double LinerApexRangeRatio { get; init; } = 0.15;

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

    /// <summary>
    /// 投手がゴロを処理するときの追加反応遅延[s]（Issue #261）。投手は投球フォロースルー直後で守備体勢が
    /// 崩れているため、中央付近を通る打球でもマウンド前の一点しか実質的に捕れない。この遅延を投手の到達時間に
    /// 上乗せすることで、中堅寄りへ転がる打球は二遊間へ渡り、投ゴロが真のセンター返し・弱い前ゴロに収束する。
    /// 0で従来（投手も他内野手と同じ反応）。処理野手の割り当てにのみ効き、頭上抜け判定（帯校正）には影響しない。
    /// </summary>
    public double PitcherFieldingReactionPenaltySeconds { get; init; } = 0.35;

    // --- 前進捕球（チャージ, issue #630）---
    // 従来は処理野手が「守備位置に最も近い経路上の点」で打球を待った＝ボテボテのゴロほど長く突っ立って
    // 待つ絵になり、内野安打が転がっていた。緩い打球だけ前へ出て早い時刻・本塁寄りの点で捕る。
    // 乱数は引かない（幾何と時間だけ＝不変条件#1/#2）。

    /// <summary>
    /// 前進捕球を始める打球の「緩さ」しきい[m/s]（待って捕るときの打球速度）。この速度で前進量0、
    /// 静止球で前進量が上限いっぱいへ直線的に増える（＝しきいの前後で絵と判定が不連続に飛ばない）。
    /// 強い打球は前進量0＝<b>従来と完全に同じ捕球点・処理時刻</b>になる。
    ///
    /// <para>30.0＝オーナー選択（2026-08-13）。「強い当たりでも3〜4m詰める・緩い当たりは思い切り突っ込む」水準で、
    /// 実測では遊撃定位置への 86km/h のゴロが 2.18s/39m地点 → 1.88s/35m地点、115km/h のゴロが
    /// 1.51s/39m → 1.39s/36m。母集団では平均捕球が 1.237→1.191s。減った内野安打は
    /// <see cref="ForceOutMarginSeconds"/> を 0.60→0.72 へ厚くして水準ごと戻してある（帯は書き換えていない）。</para>
    /// </summary>
    public double ChargeSlowBallSpeedMps { get; init; } = 30.0;

    /// <summary>コーナー（投手・一塁手・三塁手）が前へ出られる距離[m]（最短の守備範囲からの上乗せ）。
    /// 一塁が近く送球が短いぶん、実戦でも思い切って突っ込める役割。</summary>
    public double ChargeMaxAdvanceCornerM { get; init; } = 6.0;

    /// <summary>ミドル（二塁手・遊撃手）が前へ出られる距離[m]。一塁までの送球が長く、
    /// 体勢を崩すと間に合わないぶんコーナーより浅い。</summary>
    public double ChargeMaxAdvanceMiddleM { get; init; } = 4.0;

    /// <summary>前進捕球いっぱいのときの握り替え追加時間[s]（走りながら捕る＝体勢が悪い）。
    /// 前進量に比例して掛ける。前進で縮んだ捕球時刻をそのぶん食い戻す＝一方的な守備強化にしない項。</summary>
    public double ChargeThrowPenaltySeconds { get; init; } = 0.12;

    /// <summary>前進捕球いっぱいのときの送球精度の低下[点]（走りながらの送球は逸れる）。
    /// 前進量に比例して野手の ThrowAccuracy から引き、悪送球ロール（design-14 P1-6b）へ効かせる。</summary>
    public double ChargeThrowAccuracyPenalty { get; init; } = 6.0;

    /// <summary>イレギュラーバウンドの発生確率（一律。球場の土質との接続は将来の別Issue）。</summary>
    public double IrregularBounceProb { get; init; } = 0.030;

    /// <summary>イレギュラー時の横ぶれの最大角[deg]（±この範囲で一様）。</summary>
    public double IrregularBounceLateralDeg { get; init; } = 12.0;

    /// <summary>イレギュラー時の反発係数の揺らぎ幅（e が ×[1−この値, 1+この値] の範囲で振れる）。</summary>
    public double IrregularBounceRestitutionSwing { get; init; } = 0.60;

    /// <summary>イレギュラーバウンドで処理する打球のエラー確率への加算。</summary>
    public double ErrorIrregularBonus { get; init; } = 0.10;
}
