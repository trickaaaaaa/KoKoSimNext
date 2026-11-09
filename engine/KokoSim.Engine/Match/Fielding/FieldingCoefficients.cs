using System;

namespace KokoSim.Engine.Match.Fielding;

/// <summary>守備解決の係数（YAML駆動, 統計回帰で調整）。</summary>
public sealed record FieldingCoefficients
{
    /// <summary>
    /// 表示能力→物理層の効き（設計書21・<see cref="KokoSim.Engine.Players.AbilityEffectCoefficients"/>）。正典は YAML の <c>ability_effect:</c> 1ブロックで、
    /// ローダが batting/fielding/baserunning の3ブロックへ<b>同じ値</b>を配る（解決式が手元の係数束から引けるようにするため）。
    /// 3つが食い違わないことは <c>AbilityEffectConsistencyTests</c> が固定する。
    /// </summary>
    public KokoSim.Engine.Players.AbilityEffectCoefficients AbilityEffect { get; init; } = new();

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

    /// <summary>空中捕球に許容する到達時間の倍率（滞空時間×この値まで間に合えば捕球）。
    /// issue #740 で 1.00→0.93（4秒上限の撤去で増えたアウトを打率帯へ戻す校正）。着地の直前（滞空の7%以内）に届く打球は
    /// 捕れず、捕球余裕が小さい負になる＝3Dでは「背走ジャンピングで届かない」芝居で描かれる（棒立ちで落ちる絵にならない）。</summary>
    public double CatchReachFactor { get; init; } = 0.895;

    /// <summary>
    /// 空中捕球の到達可否に効く守備力の傾き（打球判断＝走路取りの巧拙）。
    /// 実効倍率 = CatchReachFactor ×(1 + (Fielding−50)×この値)。守備50で 1.0＝恒等（帯不変）。
    /// </summary>
    public double CatchReachFieldingSlope { get; init; } = 0.004;

    /// <summary>
    /// 空中捕球で走れる時間の上限[s]（滞空時間比例のままだと深い飛球ほど守備範囲が無制限に広がるため）。
    /// 走れる距離 ≈ (この値 − 反応遅延)×スプリント速度。
    /// issue #740 で実質撤去（99）: 4秒で頭打ちにすると、絵では落下点に先着した野手の飛球が安打になっていた
    /// （安打フライの 8.7%）。判定を絵の物理（反応＋距離÷脚力 ≤ 滞空時間）にそろえる。
    /// </summary>
    public double CatchReachCapSeconds { get; init; } = 99.0;

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
    ///
    /// <para><b>0.0（2026-08-14）</b>。issue #358 で 0.0→0.60 とした校正項を
    /// <see cref="InfieldHandleJitterMeanSeconds"/>（物理層のもたつき）へ移した
    /// ＝判定は素のタイミング比較（<c>defenseTime &lt;= runnerTime</c>）に戻った。</para>
    ///
    /// <para>なぜ移したか: この項は<b>判定の秒を動かすのに絵の秒には出ない</b>ため、
    /// 「送球が先に届いた絵なのにセーフ」が構造的に出ていた（実測: 内野安打 1,374件のうち
    /// 1,147件＝83.5% が絵の上では送球が先着・平均 0.37秒）。項の実体は
    /// 「シムに無い内野安打の源（もたつき・弾き）＝守備所要の分散が0」だったので、
    /// <b>分散を持つ実際の秒</b>として物理層で払わせるのが筋（不変条件#1）。</para>
    ///
    /// <para>0 以外の値を入れると再び絵と判定が食い違う。残してあるのは
    /// 「境界に定数を戻す」を意図的に選べるようにするためだけで、既定は 0。
    /// data/coefficients.yaml と同値。</para>
    /// </summary>
    public double ForceOutMarginSeconds { get; init; } = 0.0;

    /// <summary>
    /// 内野の打球処理の**もたつき**[s]（捕ってから離すまでに余分にかかる時間の平均・指数分布）。
    ///
    /// <para>実物の内野ゴロは、握り替えを一度で決められる／グラブの土手に当たる／体勢を作り直す、
    /// と処理時間が<b>右に裾を引く</b>。従来のモデルは所要が決定論値だけ（分散0）で、
    /// その足りない分を判定境界の定数（<see cref="ForceOutMarginSeconds"/>=0.60）で埋めていた。
    /// ここへ移すと<b>引いた値がそのままタイムラインの秒</b>になり、絵と判定が構造的に一致する。</para>
    ///
    /// <para>分布は指数（下限0・右に裾）。一様や正規では「速すぎる守備」が出てしまい、
    /// 内野安打が“ときどき大きくもたつく”という実物の形にならない。
    /// 平均は <see cref="Players.FielderAttributes.TransferFactor"/> で伸縮する
    /// ＝<b>守備の巧拙がもたつきの量に出る</b>（握り替えと同じ係数を使う＝手さばきの単一ソース）。</para>
    ///
    /// <para>値は内野安打率から逆算する（帯 <c>InfieldHitShare</c> 0.05〜0.15）。従来の定数 0.60 と
    /// <b>同じ内野ゴロの帰結</b>（アウト 82.8% / 内野安打 9.6% / 失策 7.6%）になる平均が 0.43 で、
    /// 分布の裾が定数ぶんの仕事をする（実測 2026-08-14・200,000打席・seed 7）。
    /// 0 にすると内野ゴロの1.5%まで潰れる（issue #358 の実測と同じ）。</para>
    ///
    /// <para><b>0.43 → 0.48（2026-08-14・ローカル統合時）</b>: issue #630 の前進捕球（チャージ）が
    /// 守備所要を縮めるため、同じ 0.43 では内野安打が 7.5% へ落ちた。#630 は同じ理由で
    /// <see cref="ForceOutMarginSeconds"/> を 0.60→0.72 へ厚くしていた＝その校正をこちら（物理層）で
    /// やり直したもの。実測（60,000打席×seed 7/42/2024）: 内野安打 9.5% / 9.1% / 9.8%
    /// ＝#630 の目標水準（9.6〜9.7%）に一致。</para>
    ///
    /// <para><b>代償</b>: 「捕ってから離すまで」が中央値 1.10秒・平均 1.23秒になる（実物は 0.6〜0.9秒）。
    /// 系統的な遅れを走路側（実捕球を遅らせて捕球点を本塁から遠ざける）へ逃がす案を実測したが、
    /// <b>処理野手の選択そのものが変わって</b>内野安打が 3.4% へ落ちた（最初に触れる野手を選ぶ式なので、
    /// 全員を遅らせると別の野手が選ばれ、間に合わない場合はゾーン最寄りへ落ちて逆に速くなる）。
    /// 走路側でやるなら選択と落とし先を作り直す必要があり、この issue の範囲を越える。
    /// data/coefficients.yaml と同値。</para>
    /// </summary>
    ///
    /// <para><b>0.48 → 0.49（2026-08-18・issue middle-infielder-charge-cap ＋ fielder-body-far-from-ball-at-catch）</b>:
    /// ミドルの前進上限 4→10m（捕球が早くなる方向）と、半径捕球の処理点を体が届く時刻へ採り直す（遅くなる方向）を
    /// 同時に入れた差し引きが −0.4pt（実測 60,000打席×seed 7/42/2024: 9.0/8.8/9.5%）。0.49 で 9.4/9.3/10.0%
    /// ＝従来水準（9.5/9.1/9.8%）に戻る。</para>
    public double InfieldHandleJitterMeanSeconds { get; init; } = 0.49;


    /// <summary>
    /// もたつきの上限（平均の何倍で頭打ちにするか）。指数分布は裾が無限に伸びるので、
    /// 「捕ってから4秒動かない内野手」が極稀に出る。<b>絵に出る量なので実在しない値は切る</b>。
    /// 3.0 なら 1.4秒＝上位 5.0% が頭打ちで、内野安打率への寄与は 0.1pt 未満（実測）。
    /// data/coefficients.yaml と同値。
    /// </summary>
    public double InfieldHandleJitterMaxFactor { get; init; } = 3.0;

    /// <summary>
    /// 内野ゴロ→一塁の握り替え倍率の守備傾き（issue #765「内野守備がもっさり」）。
    /// 倍率 = max(<see cref="InfieldTransferFactorMin"/>, 1 − (守備−50)×この値)。守備は効き後の値（design-21）。
    /// 外野返球・中継と共有の <see cref="ThrowTransferFieldingSlope"/>（0.004）のままだと守備90と20で
    /// 「捕ってから離すまで」が約0.15秒しか違わず、上手い内野手が機敏に見えなかった。
    /// 守備50で×1.0＝リーグ平均の野手（統計帯の母集団）は変わらない。data/coefficients.yaml と同値。
    /// </summary>
    public double InfieldTransferFieldingSlope { get; init; } = 0.014;

    /// <summary>内野の握り替え倍率の下限（最速側のクランプ, issue #765）。表示の守備≈98で届く。</summary>
    public double InfieldTransferFactorMin { get; init; } = 0.65;

    /// <summary>
    /// もたつき（<see cref="InfieldHandleJitterMeanSeconds"/>）の平均を守備で伸縮する傾き（issue #765）。
    /// 倍率 = max(<see cref="InfieldHandleJitterFactorMin"/>, 1 − (守備−50)×この値)。
    /// 上手い内野手は「一発で握り替える」＝もたつきがほぼ出ず、下手な内野手ほど握り損ね・お手玉が増える。
    /// 守備50で×1.0（帯不変）。表示の守備90で×0.35／守備20で×1.49。data/coefficients.yaml と同値。
    /// </summary>
    public double InfieldHandleJitterFieldingSlope { get; init; } = 0.036;

    /// <summary>もたつき倍率の下限（issue #765）。どんな名手でも、たまには握り直す。</summary>
    public double InfieldHandleJitterFactorMin { get; init; } = 0.25;
    /// <summary>
    /// お手玉の失策（2026-11-03）: もたつき[s]がこれ以上で、もたつきが無ければアウトだったセーフは
    /// 内野安打でなく失策（<see cref="FieldingErrorKind.Bobble"/>）。0以下＝無効（従来どおり内野安打）。
    /// </summary>
    public double BobbleErrorMinJitterSeconds { get; init; } = 1.3;

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
    /// errors_per_game を現行水準（≈2.4）に保つため内野ゴロ側を引き上げた（捕球60/送球40の按分は維持）。
    /// issue #810（2026-11-09）で 0.0495→0.0400：受け手の捕球ミス(<see cref="ReceiveErrorBaseProb"/>)を三段目として
    /// 新設した分だけ、捕球・送球の両方から再配分し errors_per_game を帯内（1.8–3.6）に保った。</summary>
    public double ErrorBaseProb { get; init; } = 0.0400;
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
    /// issue #532 で×1.29（0.0266→0.0343・捕球60/送球40の按分維持）。
    /// issue #810（2026-11-09）で 0.0343→0.0280：受け手の捕球ミス(<see cref="ReceiveErrorBaseProb"/>)を三段目として
    /// 新設した分だけ再配分（捕球・送球・受け手の按分は概ね48/34/18）。決定論baselineは再生成済み。</summary>
    public double ThrowErrorBaseProb { get; init; } = 0.0280;
    /// <summary>送球精度(ThrowAccuracy−50)あたりの送球エラー確率の減少幅。精度が高いほど悪送球が減る。
    /// クランプは捕球エラーと同じ ErrorMinProb/ErrorMaxProb を流用。</summary>
    public double ThrowErrorAccuracySlope { get; init; } = 0.004;

    // 受け手の捕球ミス（内野ゴロ→一塁の3段階目, issue #810 / design-14 P1-6b 追記）。捕球ロール(Catching)→
    // 送球ロール(ThrowAccuracy)を通過した（＝送球自体は正確）後に、受け手の一塁手が正確な送球を落とす／
    // ショートバウンドを捕れない失策を引く。既定 base=0.0 なら「ロールを一切引かない」＝現行の2段判定と
    // 乱数消費順・結果とも完全一致（ThrowErrorBaseProb と同じ gating 方式で決定論を保つ）。
    /// <summary>受け手の捕球ミスの基準確率（一塁手 Catching50時）。issue #810で正値化＝捕球・送球の両方から
    /// 再配分し errors_per_game を帯内（1.8–3.6）に保った（捕球・送球・受け手の按分は概ね48/34/18）。
    /// 一塁手自身が処理した打球（受け手が別にいない）には適用しない。</summary>
    public double ReceiveErrorBaseProb { get; init; } = 0.0150;

    // 送球逸れの回収レース（design-12 §3.8, 2026-08-30）。悪送球が確定した後、逸れ幅→回収ロスを
    // 守備所要時間へ加算してもう一度同じ判定基準（defenseTime+ForceOutMarginSeconds vs runnerTime）で
    // 再評価する（拾い直しが間に合えばアウトのまま）。回収役（一塁手／右翼手）の走力・肩は実際の
    // Fielder情報（fielders）から取るため、速度の係数はここには持たない（<c>BaserunningCoefficients</c>と違い
    // 呼び出し元に実際の野手一覧が揃っているため）。
    /// <summary>悪送球1本あたりの逸れ幅の標準偏差[m]（半正規分布からサンプル）。内野送球は距離が短いため
    /// <c>BaserunningCoefficients.WildThrowScatterSigmaM</c>（バックホーム用）とは独立の値。</summary>
    public double WildThrowScatterSigmaM { get; init; } = 6.0;
    /// <summary>回収に動き出すまでの反応遅延[s]（受け手・バックアップ共通）。</summary>
    public double RecoveryReactionSeconds { get; init; } = 0.30;

    // 捕球ミス（お手玉）の回収レース（design-14 P1-6 追記, issue #817）。送球逸れの回収レースと同じ形で、
    // 捕球エラー(FieldingErrorKind.Catch)が確定した後も「弾いたら即失策」にしない。こぼれ幅→拾い直しの
    // ロスを守備所要時間へ加算し、同じ判定基準でもう一度評価する（間に合えばアウトに戻る）。球は捕球点の
    // すぐそばにあるのでバックアップ野手の概念は無く、回収役は常に処理野手自身（RecoveryReactionSeconds を共有）。
    /// <summary>捕球ミス1回あたりのこぼれ幅の標準偏差[m]（半正規分布からサンプル）。送球の逸れ幅
    /// (<see cref="WildThrowScatterSigmaM"/>)より小さい＝グラブ際で弾く距離は送球が逸れる距離より近い。</summary>
    public double CatchFumbleScatterSigmaM { get; init; } = 2.5;

    // ファウルフライ（issue #830）。打撃の接触判定で「ファウル」になった球（弾道を持たない）のうち、
    // この割合を打ち上がったファウルとして弾道を与え、ファウルグラウンドに落ちる前に野手が届けばアウト。
    // 弾道は打席の本流と別の乱数ストリーム（Fork）で引く＝ファウル以外の打席の乱数順は変わらない。
    /// <summary>接触判定のファウルのうち、打ち上がる（捕れる可能性がある）割合。0で無効＝従来と完全一致。</summary>
    public double FoulFlyShare { get; init; } = 0.24;
    /// <summary>打ち上がったファウルの打球角度の平均・標準偏差[度]。</summary>
    public double FoulFlyLaunchMeanDeg { get; init; } = 55.0;
    public double FoulFlyLaunchSigmaDeg { get; init; } = 12.0;
    /// <summary>打ち上がったファウルの初速の平均・標準偏差[km/h]（擦った打球＝遅い）。</summary>
    public double FoulFlyExitMeanKmh { get; init; } = 100.0;
    public double FoulFlyExitSigmaKmh { get; init; } = 18.0;
    /// <summary>打ち上がったファウルの方位角の範囲[度]（絶対値。45°＝ファウルライン、180°＝真後ろ）。一様に引く。</summary>
    public double FoulFlyBearingMinDeg { get; init; } = 46.0;
    public double FoulFlyBearingMaxDeg { get; init; } = 160.0;

    /// <summary>
    /// トンネル（股・脇を抜けて球が外野へ行く失策, issue #632）の割合。
    /// <b>捕球エラーの内訳</b>＝失策の総数は変えず、お手玉が減ってトンネルが増える
    /// （失策率は #123/#532 で実測域へ校正済みなので動かさない）。
    ///
    /// <para>0 のときは内訳ロール自体を引かない＝乱数消費順・結果とも完全に従来どおり
    /// （<see cref="ThrowErrorBaseProb"/> と同じ恒等の作法）。正値にすると
    /// 「球が外野へ行ったから走者が還った」という因果が物理層で成立する（不変条件#1）。</para>
    /// data/coefficients.yaml と同値。
    /// </summary>
    public double TunnelShareProb { get; init; } = 0.30;

    // --- 塁打数の決定（Issue #24: 距離しきい値を廃し、転がり＋幾何＋走力の連続量で決める） ---

    /// <summary>着地後の転がりの減速度[m/s²]（芝・土の摩擦＋バウンドの損失を集約）。</summary>
    public double RollDecelMps2 { get; init; } = 2.60;

    /// <summary>水平に近い打球（ライナー）が最初のバウンドで保持する前進速度の割合。滑って伸びる。</summary>
    public double RollRetentionFlat { get; init; } = 0.72;

    /// <summary>真上から落ちる打球（大飛球）が最初のバウンドで保持する前進速度の割合。失速して死ぬ。</summary>
    public double RollRetentionSteep { get; init; } = 0.25;

    /// <summary>フェンスに達した打球の跳ね返り処理に要する追加時間[s]（カロムの方向は当面無視）。</summary>
    public double FenceCaromSeconds { get; init; } = 0.40;

    /// <summary>
    /// フェンスの反発係数（issue fence-carom-and-over-the-head-cues）。跳ね返り初速＝
    /// フェンス到達時の残速 × これ。**表示専用**（判定＝回収時刻・塁打数は
    /// <see cref="FenceCaromSeconds"/> のままで一切変わらない）。
    ///
    /// <para>高校の球場の金網＋パッドを想定して 0.30〜0.40 が出発点（監督決定 2026-08-13）。
    /// 現行の <see cref="RollDecelMps2"/>=2.60 だと、左中間へ低いライナー（フェンス到達時 17.3 m/s）で
    /// e=0.35 なら約 7m 戻る。将来は球場ごとの材質差へ拡張できる（design-13 の延長）。</para>
    /// </summary>
    public double FenceRestitution { get; init; } = 0.35;

    /// <summary>外野手が転がる打球を拾い上げるのに要する時間[s]（内野ゴロの InfieldPlayOverheadSeconds 相当）。</summary>
    public double OutfieldPickupSeconds { get; init; } = 0.30;

    // 外野手のファンブル（回収時に弾く・後逸, issue #811 / design-14 P1-6 追記）。内野の3段階（捕球/送球/受け手）
    // とは別の失策経路で、「安打の回収」そのものを外野手が仕損じる。安打の記録（Single/Double/Triple）は変えず、
    // 失策だけ外野手に付ける（安打＋失策）。難度は物理量（回収点でのボール速度。速いほど難しい・止まった球では
    // ほぼ発生しない）で決まる。発生時は球が逸れた分だけ回収が遅れ、その遅れた回収時刻で ResolveBases を
    // 解き直す＝一律の追加進塁ではなく時間勝負で塁打数が伸びる（不変条件#1）。
    /// <summary>
    /// 外野手のファンブルの基準確率（回収点でのボール速度が <see cref="OutfieldFumbleSpeedRefMps"/> 以上・
    /// Catching50時）。難度は回収点での打球速度で0〜1に連続的に伸縮する（速度0＝完全に止まった球では発生しない）。
    /// 捕球の効きは <see cref="ErrorCatchingSlope"/> を <see cref="ErrorBaseProb"/> に対する比率で掛ける。既定0なら「ロールを一切引かない」＝
    /// 乱数消費順・結果とも完全に従来どおり（<see cref="ThrowErrorBaseProb"/> と同じ恒等の作法）。
    /// data/coefficients.yaml と同値（issue #811で正値化・errors_per_game/outfield_error_shareの帯内で校正）。
    /// </summary>
    public double OutfieldFumbleBaseProb { get; init; } = 0.008;

    /// <summary>
    /// 回収点でのボール速度[m/s]がこの値以上で難度1（基準確率フル）とみなす。難度は 0（停止球）〜1（この速度以上）
    /// へ線形に伸縮する。
    /// </summary>
    public double OutfieldFumbleSpeedRefMps { get; init; } = 10.0;

    /// <summary>
    /// ファンブルで球が余分に逸れる時間[s]の平均（指数分布・下限0・右に裾＝たいていは小さく弾くだけ、
    /// たまに大きく後逸する）。内野のもたつき（<see cref="InfieldHandleJitterMeanSeconds"/>）と同じ形。
    /// この時間を回収時刻へ加算してから塁打数（<see cref="ExtraBaseResolver.ResolveBases"/>）を解き直す。
    /// </summary>
    public double OutfieldFumbleDelayMeanSeconds { get; init; } = 1.2;

    /// <summary>ファンブルの遅れの上限（平均の何倍で頭打ちにするか）。<see cref="InfieldHandleJitterMaxFactor"/> と同じ趣旨。</summary>
    public double OutfieldFumbleDelayMaxFactor { get; init; } = 3.0;

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
    /// 飛びつきの実効リーチ[m]（体の中心から捕球点まで。伸ばした腕＋倒れ込む体）。
    /// <see cref="InfielderFieldingRadiusM"/> が「内野を抜けるか」の判定半径なのに対し、こちらは
    /// **処理点・処理時刻**を決める（issue fielder-body-far-from-ball-at-catch）: 半径で触れると判定した打球も、
    /// 処理は体がこの距離まで球に寄れた時刻・点で行う＝絵で野手の体が球から 3〜4m 離れたまま捕球・送球しない。
    /// 常に <see cref="InfielderFieldingRadiusM"/> 以下として扱う。
    /// </summary>
    public double InfielderDiveReachM { get; init; } = 2.0;

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
    ///
    /// <para><b>4.0 → 10.0（issue middle-infielder-charge-cap）</b>: 4m は 30 m/s 分母の線形係数が掛かって実効 1〜2.4m
    /// ＝遊撃・二塁が正面のボテボテに 2〜3m しか出ず立って待っていた（実測: 79 km/h 遊撃正面で 3.3m/2.25s→内野安打。
    /// 上限なしで会える最速点は 9.6m/1.76s）。コーナーの 6m は上限なしと同値だったので触らない。突っ込んだ代償は
    /// 前進量に比例する握り替え・送球精度のペナルティが払う。</para>
    public double ChargeMaxAdvanceMiddleM { get; init; } = 10.0;

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
