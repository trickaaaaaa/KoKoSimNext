using UnityEngine;

namespace KokoSim.Unity.Match
{
    /// <summary>
    /// 対決/守備ビュー（<see cref="DuelStageRig"/>）の調整用・撮影用の可変 static を1か所へ束ねたもの。
    /// キャリブ窓・撮影レシピ（captures.yaml の overrides は <c>DuelTuning.Xxx</c>）・Shooter が上書きし、
    /// <see cref="DuelStageRig"/> が毎フレーム読む。既定値＝実ゲームの値（撮影用の上書きは -1/false/null が「なし」）。
    /// </summary>
    public static class DuelTuning
    {
        /// <summary>もたつきの芝居を付けるか（見比べ撮影用の静的スイッチ。既定 true）。</summary>
        public static bool HandleBobbleEnabled = true;
        /// <summary>捕ってから投げるまでの立ち止まりを埋めるか（捕球の収録を流し切る＋余れば送球の的へサイドステップで寄る）（見比べ撮影用の静的スイッチ。既定 true）。</summary>
        public static bool StepBeforeThrowEnabled = true;

        /// <summary>
        /// タッチプレーで逸れを捕った形を見せる最短の秒（捕球の瞬間から。12fps で約1コマ強）と、そこからタッチの
        /// 瞬間まで収録を等速で流す最短の秒（issue #979）。判定の遅れ（<c>ScatterRecoverySeconds</c>）だけでは
        /// 足りないときの下限＝収録は短く切って（頭を捨てて）つなぎ、**倍率は上げない**。**絵で決める値**。
        /// </summary>
        public static float ScatterTagShowSeconds = 0.10f, ScatterTagRunUpSeconds = 0.12f;

        /// <summary>本塁打の構えへ移るクロスフェードの長さ[s]。**絵で決める値**。</summary>
        public static float HomeRunCrouchFadeSeconds = 0.35f;

        /// <summary>構え→立って待つのクロスフェード[s]。**絵で決める値**。</summary>
        public static float PostHrStandFadeSeconds = 0.6f;

        /// <summary>
        /// 捕球中に的へ開くとき、グラブが収録の位置からずれてよい量[m]（issue #505 ②）。
        /// ずれた分は捕球IK（<c>DuelCatcherPose.ReachMitt</c>）が球へ引き戻す＝**IK の仕事量の予算**。
        /// 大きくすると「最初から的を向いて投げる」が増える代わりに捕球の腕が伸びる。
        /// **絵で決める値**なので比較撮影のために差し替えられるようにしてある。
        /// </summary>
        public static float FieldCatchOpenBudgetM = 0.18f;

        /// <summary>捕球からこの秒数までに投げるなら「このあと投げる」とみなす[s]（issue #505 ②）。</summary>
        public static float FieldCatchOpenLeadSeconds = 0.8f;

        /// <summary>
        /// 守備カメラの据え位置（stage-local[m]）。本塁後方に**2台**据える（監督決定 2026-08-01・案D）。
        ///
        /// 高さは絵に直結する。低いほど視線が寝て選手を横から見る＝立体が出るが、
        /// 遠くの被写体では背後のフェンス（3.2m）と暗幕（7m）が画面の半分を埋める
        /// （寄った枠は縦10m級なので、90m先へ向けると縦の画角は6.6°しかない）。
        /// 高くすると外野の背景に芝と空が入る代わりに、内野が見下ろしになる。
        /// **足して2で割れない**ので内野用と外野用に分け、被写体の深さで切り替える。
        ///
        /// ⚠ z は **> −16**（バックネット書き割り z=−16・34×8m より手前）。
        /// 奥へ引くと壁が画面下半分を黒く塞ぐ（2026-08-01 実害）。
        /// </summary>
        public static Vector3 FieldStationInfield = new(0f, 10f, -14f);

        /// <inheritdoc cref="FieldStationInfield"/>
        public static Vector3 FieldStationOutfield = new(0f, 24f, -14f);

        /// <summary>
        /// 注視点（高さ）の追従速度上限[m/s]（issue #399②-2）。①（頂点・着地点基準）でセグメント中の
        /// 目標は一定になるが、セグメントの変わり目（接触→滞空／着地・捕球→保持）では目標が段差で変わる。
        /// ここへ即座に飛ぶと段差そのものが目に付くので、Mathf.MoveTowards で寄せる。
        /// 全景へ戻す「カット」（<see cref="DuelStageRig._fieldCamCutT"/>）は対象外＝そこは既存どおり一気に切り替える。
        /// **絵で決める値**なので比較撮影のために差し替えられるようにしてある。
        /// </summary>
        public static float FieldAimYSpeedCapMPerSecond = 20f;

        /// <summary>
        /// 守備カメラの寄りの深さ。2D俯瞰の既定（MaxZoom=3.4＝縦45.6m）では野手が十数pxにしかならない。
        /// 外野フライで FitBox が求めるズームは実測5.75で上限に頭打ちされていた（engine テストで固定）。
        /// **絵で決める値**なので、比較撮影のために差し替えられるようにしてある。
        /// </summary>
        public static float FieldMaxZoom = 40f;

        /// <summary>
        /// ポール際の大ファウル（issue #1074）の寄りの上限。球だけを追って寄り切ると、スタンドへ入る前後で
        /// 観客しか映らない絵になる（実測: 最後の1.5秒が観客の寄り）。ポールとスタンドが一緒に収まる広さで止める
        /// （4倍＝縦およそ39m）。**絵で決める値**。
        /// </summary>
        public static float BigFoulMaxZoom = 4f;

        /// <summary>
        /// 被写体まわりに残す余白[m]。**寄りの深さを決めているのは実はこちら**で、<see cref="FieldMaxZoom"/> ではない。
        ///
        /// <c>FitBox</c> は箱の縦横に 2×Margin を足してからズームを決めるので、箱が点まで縮んでも
        /// <c>zoom ≤ 155.2/(2×Margin)</c> ＝ **frameH は 2×Margin より小さくならない**。
        /// 既定の 6.0m では frameH の下限が 12m（＝上限 12.9 倍）で、これより上に MaxZoom を上げても
        /// 1mm も寄らない。**絵で決める値**なので比較撮影のために差し替えられるようにしてある。
        /// </summary>
        public static float FieldMarginM = 2f;

        /// <summary>
        /// 守備ビューの打球残像・尾の長さ[秒]（issue #398 ①）。対決ビュー向けの値
        /// （<see cref="DuelCalibration.BattedTrailSeconds"/>＝0.28s）は「接触から俯瞰へ渡るまで
        /// （旧・約0.55s）で消え切る」前提で詰めた長さで、いまの引きの構図（frameH 29〜65m）では
        /// 短すぎて「点」に近い。**絵で決める値**なので比較撮影のために差し替えられるようにしてある。
        /// 0.45→0.35 は監督決定（2026-08-06・issue #505 ①「くどい」）。
        /// </summary>
        public static float FieldBattedTrailSeconds = 0.35f;

        /// <summary>
        /// 守備ビューの打球残像・頭の太さ[m]（issue #398 ①）。対決ビュー向けの 0.045m は本塁付近の
        /// 寄った構図で約10pxになるよう詰めた値で、frameH 29m 級の構図では1px未満（実質見えない）。
        /// **絵で決める値**なので比較撮影のために差し替えられるようにしてある。
        /// 0.35→0.22 は監督決定（2026-08-06・issue #505 ①「くどい」）。
        /// </summary>
        public static float FieldBattedTrailWidthM = 0.22f;

        /// <summary>
        /// 守備ビューの打球残像・頭の濃さ（issue #505 ①）。
        ///
        /// ⚠ **対決ビューとは別に持つ**。濃さと色だけは <see cref="DuelCalibration.BattedTrailAlpha"/>
        /// を共有していたので、守備ビューを弱めると対決ビュー（0.045m/0.45/0.28s で詰めた絵。
        /// design-18 §4.5-④）まで薄くなっていた。尾長・太さと同じ「フィールド専用」の枠へ揃える。
        /// **絵で決める値**なので比較撮影のために差し替えられるようにしてある。
        /// 0.28 は監督決定（2026-08-06・issue #505 ①。対決ビューは 0.45 のまま）。
        /// </summary>
        public static float FieldBattedTrailAlpha = 0.28f;

        /// <summary>
        /// 送球区間でも残像を出すか（issue #505 ①）。
        ///
        /// 残像は「打球の行方（フライ／ゴロ／ライナー）を一瞬で読ませる」ための線（design-18 §4.5-④）で、
        /// **送球には要らない**（始点＝野手・終点＝塁の両方が絵に写っており、行方を線で補う必要がない）。
        /// それでも従来は区間の種別を見ずに積んでいたので、送球にも打球と同じ太さの帯が出ていた。
        /// false は監督決定（2026-08-06・issue #505 ①）。
        /// </summary>
        public static bool FieldTrailOnThrow = false;

        /// <summary>
        /// 送球区間で球を追うか。2D俯瞰の既定は「始点と終点を両方収める」＝送球の距離ぶん必ず引くので、
        /// 6-3 の一塁到達や中継バックホームという**そのプレーで一番見たい瞬間が一番引いた絵になる**
        /// （実測 frameH 20m / 56m）。3Dでは中継カメラと同じく球を追う側にする。
        /// </summary>
        public static bool FieldThrowFollowsBall = true;

        /// <summary>
        /// 送球の枠（始点・終点を収める側）を投げ手から球へ**伸ばす**か（issue #566）。
        /// 既定の切り替え式は枠が階段状に跳び、寄っている3Dではズームが追いつく前にアイムだけが
        /// 投げ手と受け手のあいだの地面へ着く。伸ばす式なら跳ばない。切り分け用に残す静的スイッチ。
        /// </summary>
        public static bool FieldThrowBoxGrows = true;

        /// <summary>
        /// パンとズームを連動させるか（issue #566）。パン上限 45m/s に対しズーム上限は 2倍/秒 しか
        /// 無いので、被写体が切り替わる瞬間に**枠が狭いまま被写体のあいだの地面を舐める**区間ができる。
        /// on にすると、遅れたぶんだけ枠を広げて生の被写体を必ず収める（＝広げる方向にしか効かない）。
        /// 追従が足りているプレーでは1キーフレームも変わらない。切り分け用に残す静的スイッチ。
        /// </summary>
        public static bool FieldKeepSubjectFramed = true;

        /// <summary>
        /// 内野ゴロ・併殺の固定枠（issue #689・監督決定: 案Bベース＋アウトの瞬間だけ一塁へ寄せる）。
        /// 接触から送球リリースまでは「本塁〜二塁を収める固定枠」に据えて動かさず、最後の送球が出てから
        /// 到達までのあいだに、送球の終点（一塁）へゆっくり寄せて決着まで据える。
        /// false にすると従来の追従（比較撮影用の切り替え）。
        /// issue #742（監督決定 2026-11-03: 案B＝捕る野手中心・縦12m）で既定を false にした＝固定枠は見比べ用に残すだけ。
        /// </summary>
        public static bool FieldInfieldFixedFrame = false;

        /// <summary>
        /// 内野ゴロを「捕る野手（＋球）中心の追従」で撮るときの枠の縦[m]（issue #742 の見比べ用・null＝使わない）。
        /// 固定枠（<see cref="FieldInfieldFixedFrame"/>）を切った状態で値を入れると、注視点は engine の追従プラン
        /// （打球中＝球と捕りに行く野手／保持中＝球を持つ野手＝併殺なら捕球した野手→二塁のピボット／送球中＝送球の終点）
        /// のまま、縦の実寸だけこの値に据える。既定 12m（監督決定 2026-11-03・案B。比較撮影で 44m固定／12m／18m を見比べた）。
        /// パンの遅れで被写体が枠から外れそうなときは <see cref="FieldKeepSubjectFramed"/> が枠を広げる（広げる方向だけ）。
        /// </summary>
        public static float? InfieldFollowFrameH = 12f;

        /// <summary>
        /// 内野ゴロの追従で、注視点を追従プラン（球と野手のあいだ）でなく**球そのもの**に置くか（2026-11-03 監督報告
        /// 「捕球の瞬間が見えない」）。プランの注視点＋パン速度上限だと、速いゴロは捕球の瞬間に枠が追いつかない。
        /// 球は捕球の瞬間グラブの中にある＝球を真ん中に置けば捕球は必ず枠に入る。送球も球を追う。
        /// </summary>
        public static bool InfieldFollowOnBall = true;

        /// <summary>
        /// 球追いのなじませ時定数[s]。一次遅れの追従に同じ秒数の先読みを足す＝等速の球には遅れが0になり、
        /// 打った瞬間・捕った瞬間の折れ目だけが滑らかになる。
        /// </summary>
        public static float InfieldBallFollowTauS = 0.12f;

        /// <summary>
        /// プレーの終わりに全景（縦155m）へ戻すか（issue field-view-cut-back-sooner ④）。
        /// **絵で決める値**なので比較撮影のために差し替えられるようにしてある（true で旧挙動）。
        /// engine のプラン（<see cref="CameraFraming.ReturnsToWide"/>）と3D側のカット
        /// （<see cref="DuelStageRig._fieldCamCutT"/>）の両方がここを見る＝片方だけ変えるとプランと実カットがずれる。
        /// </summary>
        public static bool FieldReturnsToWide = false;

        /// <summary>
        /// パン（注視点の水平）の速度上限[m/s]。**絵で決める値**。中継の速いパン（送球の
        /// 振り向き等）は許し、プラン由来のスパイクだけ削る。実測（2026-08-05）: 飛翔中のパンは
        /// 5〜50°/s 級で穏やか＝これは安全弁で、忙しさの主犯はズーム側。
        /// </summary>
        public static float FieldPanSpeedCapMPerSecond = 35f;

        /// <summary>
        /// ズーム（縦に収める実寸）の速度上限[m/s]。**絵で決める値**。監督報告（2026-08-05
        /// 「外野フライでもカメラが忙しい」「球追いズームを緩めて」）を受けて追加。
        /// 実測: 飛翔中に画角20〜130°/sの鋸歯が1.5秒続き、捕球後の寄りは1秒で16m級（27m/s）
        /// 詰めていた。12m/sに絞ると鋸歯は消え、寄りは8割まで入った所で全景カットに渡る。
        /// </summary>
        public static float FieldZoomSpeedCapMPerSecond = 12f;

        /// <summary>打者が打席を外して下がる距離[m]（本塁から遠ざかる向き）。</summary>
        public static float OtBatterBackM = 1.3f;

        /// <summary>打者のベンチ側に並ぶ伝令との間隔[m]（腰と腰）。耳打ちで伝令の口と打者の耳が 0.2〜0.3m に収まる所。</summary>
        public static float OtPairGapM = 0.55f;

        /// <summary>並んだ2人が互いへ向き合う角度[度]（センター方向から内へ）。</summary>
        public static float OtFaceInDeg = 30f;

        /// <summary>2人が揃ってから耳打ちしている秒数[s]。</summary>
        public static float OtTalkSeconds = 3.6f;

        /// <summary>散り始めてから畳むまでの下限[s]（打者が打席へ戻り着く方が遅ければそちら）。</summary>
        public static float OtLeaveMinSeconds = 2.0f;

        /// <summary>耳打ちで伝令の上体を打者へ傾ける角度[度]。</summary>
        public static float OtLeanDeg = 20f;

        /// <summary>打者のうなずきの深さ[度]と回数。</summary>
        public static float OtNodDeg = 13f;

        /// <summary>
        /// カメラ（2人の中点からの相対[m]。x は打者の側の符号を掛けて左右を映す＝+が本塁から遠い側）。
        /// 正面のやや外側から2人の顔と、奥に本塁・捕手を入れる（2026-11-21 監督決定。本塁側の斜め前・外側の真横寄りの
        /// 案と録画で見比べて選んだ）。
        /// </summary>
        public static Vector3 OtCameraOffset = new Vector3(1.6f, 1.5f, 5.2f);

        /// <summary>縦に収めたい実寸[m]（2人の全身と足元の打席。寄りすぎない＝3d-character.md）。</summary>
        public static float OtFrameHeightM = 4.6f;

        /// <summary>注視点の高さ[m]。胸より下を見て2人を上寄せにし、下に出るテロップと重ねない。</summary>
        public static float OtLookY = -0.25f;

        /// <summary>頭ボーンから渦巻きの中心までの高さ[m]（帽子のすぐ上に乗せる。オーナー確認2026-11-20）。</summary>
        public static float SwirlLiftM = 0.20f;

        /// <summary>渦巻きの面をカメラへ向ける度合い（0=真水平／1=画面に正対）。水平のままだと真横から見て線に潰れる。0.20＝地面と平行に寝た輪に見える（オーナー確認2026-11-20）。</summary>
        public static float RattledSwirlFaceCamera = 0.20f;

        /// <summary>
        /// セット姿勢でグラブ側の手首を掌側へ曲げる角度[度]（掌屈）。グラブの指先側がポケットの向き＝握りの側へ倒れ、
        /// 握った手を外からかぶせて覆う。
        /// </summary>
        public static float SetGloveFlexDeg = 58f;

        /// <summary>
        /// 前腕の軸まわりのひねり[度]。掌屈だけ（58°）でも握りは大きく隠れるが、ひねりを足すと
        /// グラブの縁が握りの残りを覆う。値は掃引の実測（2026-11-14・右投げ）:
        /// 見えている握りの頂点（中継カメラ/二塁走者）＝ 収録のまま 344/318 → 掌屈60° 43/32 → 58°+30° 10/12。
        /// 左投げ（鏡映）は 292/295 → 47/13。掌屈は 65° を超えると指先側が握りを通り過ぎてまた見え始める（75°で277）。
        /// </summary>
        public static float SetGloveRollDeg = 30f;

        /// <summary>跳ねている時間[s]。**絵で決める値**。</summary>
        public static float StandHopSeconds = 0.6f;

        /// <summary>跳ねの高さ＝入射速さ×この係数（上限・下限つき）[m]。</summary>
        public static float StandHopHeightPerMps = 0.05f;

        /// <summary>跳ねで進む距離＝入射の水平速さ×この係数[m]。座席の段を1〜3段ぶん転がる程度。</summary>
        public static float StandHopRunPerMps = 0.08f;

        /// <summary>弾丸ライナーの追従の速さ（パン・ズームの速度上限に掛ける倍率）。**絵で決める値**。</summary>
        public static float LinerCamSpeedScale = 2.2f;

        /// <summary>ぎりぎりの本塁打で、柵を越える瞬間に寄せる縦の実寸[m]。</summary>
        public static float WallFrameH = 13f;

        /// <summary>柵を越える何秒前から寄せ始めるか／寄せ切るか[s]。</summary>
        public static float WallBlendStartSeconds = 0.9f;

        public static float WallBlendFullSeconds = 0.35f;

        /// <summary>寄せを抜き始める時刻＝着弾から（スタンドに当たらないときは柵を越えてから）の据え置き[s]と、抜けきるまでの秒数[s]。</summary>
        public static float WallNoHitHoldSeconds = 0.5f;

        public static float WallReleaseSeconds = 0.55f;

        /// <summary>集まる・散るときの走る速さ[m/s]（収録4.28を0.93倍＝軽いジョグ）。</summary>
        public static float TimeoutRunSpeed = 4.0f;

        /// <summary>輪に入る直前・散り始めの歩く速さ[m/s]（収録0.73の1.37倍。歩きの上限1.45倍の内側）。</summary>
        public static float TimeoutWalkSpeed = 1.0f;

        /// <summary>輪に入る前に歩く距離[m]。</summary>
        public static float TimeoutWalkInM = 1.6f;

        /// <summary>輪の半径[m]（投手の位置から各人の立ち位置まで）。</summary>
        public static float TimeoutRingRadiusM = 1.2f;

        /// <summary>集まる人が走り始める位置の、輪からの最大距離[m]。定位置がこれより遠い人は画面外のこの距離から走ってくる。</summary>
        public static float TimeoutStartRadiusM = 9f;

        /// <summary>全員が輪に入ってから話している秒数[s]。</summary>
        public static float TimeoutTalkSeconds = 4.0f;

        /// <summary>散り始めてから場面を畳むまで[s]。</summary>
        public static float TimeoutLeaveSeconds = 2.6f;

        /// <summary>
        /// カメラの位置（ステージ座標: +x=一塁側・+z=センター方向・マウンド z=18.44）。本塁側の斜め上から輪を見下ろす
        /// （2026-11-21 監督決定。一塁側・二塁側の案と録画で見比べて選んだ）。
        /// </summary>
        public static Vector3 TimeoutCameraPos = new Vector3(5.5f, 7.0f, 5.0f);

        /// <summary>縦に収めたい実寸[m]（輪の全身と足元のマウンド。構図は画角でなく実寸で決める＝3d-character.md）。</summary>
        public static float TimeoutFrameHeightM = 7.5f;

        /// <summary>
        /// カメラの注視点の高さ[m]。輪の胸（約0.9m）より下を見る＝輪を画面の上寄せに置き、下に出る伝令役の
        /// テロップ（交代テロップと同じ器）と重ねない（2026-11-21 録画で輪の下半分がテロップに隠れていた）。
        /// </summary>
        public static float TimeoutLookY = -0.8f;

        /// <summary>人だかりの中心（本塁の少し前。ステージ座標 m）。勝ち越しの走者（本塁打なら打者）がここに立つ。</summary>
        public static Vector2 WalkoffCenter = new Vector2(0.0f, 3.0f);

        /// <summary>
        /// 色数制限（ポスタライズ）の段数。0または1で無効＝素通し。
        /// 監督選定（2026-07-28・絵柄マトリクス）: 6段・輪郭なし を採用既定とした。
        /// → 監督判定（2026-08-05・クリーン化見比べ）: **無効（1）** へ改定。ポストの実装・
        /// ブリット経路は温存する（戻すときは 6f に上げるだけ。DuelStagePixelPost Pass0）。
        /// </summary>
        public static float PosterizeLevels = 1f;

        /// <summary>
        /// 【開発用】AssetDatabase を使わず**索引だけ**で組む（＝Player ビルドと同じ経路）。
        /// エディタのロードは AssetDatabase を先に見るので、索引が壊れていても手元では絵が出る。
        /// ビルドを作らずにビルド経路を確かめるための唯一の入口。
        /// </summary>
        public static bool ForceCatalogOnly;

        /// <summary>
        /// 【開発用】購入素体を無視してU2素体で組む（issue #621）。購入素体は gitignore 配下で
        /// 手元に無い環境がある＝U2側にも実装が要る作り（帽子つばの塗り分け等）を、素体を消さずに確かめる。
        /// </summary>
        public static bool ForceU2Body;

        /// <summary>【開発用】体型比較撮影向けに素体FBXを差し替える（null=既定のSuperhero Male）。
        /// 素体を替える時は対応する着衣ベイク資産（UniformMeshOverrideForCapture）も揃えること＝
        /// ベイクは bindposes/boneWeights を素体からコピーするため、素体と着衣メッシュは必ず同源で組。</summary>
        internal static string BodyFbxOverrideForCapture;

        /// <summary>【開発用】塁審の帽子の位置の見比べ（BaseUmpireCapMount への加算[m]）。</summary>
        public static float BaseUmpCapAddYForCapture, BaseUmpCapAddZForCapture;

        /// <summary>【開発用】比較シート撮影向けに着衣メッシュ資産を差し替える（null=既定のSlim0）。</summary>
        internal static string UniformMeshOverrideForCapture;

#if UNITY_EDITOR
        /// <summary>スクショ用（issue #1079 の見比べ）: 走りながら掬う外野ゴロの収録。</summary>
        public static DuelStageRig.OutfieldRunThroughPick OutfieldRunThroughPickForCapture = DuelStageRig.OutfieldRunThroughPick.TypeF;

        /// <summary>スクショ用（issue #1079 の見比べ）: 0 以上なら外野手の送球を距離によらずこの添字の収録にする。</summary>
        public static int OutfieldThrowAnchorForCapture = -1;

        /// <summary>スクショ用: 開きの秒の上書き（負＝既定のまま）。</summary>
        public static float ThrowOpenSecondsOverrideForCapture = -1f;

        /// <summary>スクショ用: 開き切る時刻をリリースの何秒前にするか（負＝収録の表のまま）。</summary>
        public static float ThrowOpenEndLeadOverrideForCapture = -1f;

        /// <summary>スクショ用（issue #1095 の見比べ）: 真にすると閉じない（胸を的へ向けたまま投げる旧挙動）。</summary>
        public static bool ThrowClosedWindupOffForCapture;

        /// <summary>スクショ用（issue #1734 の見比べ）: 真にすると打者走者の駆け抜けレッグを補わない（旧挙動）。</summary>
        public static bool BatterOverrunLegOffForCapture;

        /// <summary>スクショ用: 外野手の閉じる角度を収録によらずこの値[度]にする（負＝表のまま）。</summary>
        public static float OutfieldClosedYawOverrideForCapture = -1f;

        /// <summary>【実測プローブ用】接触のクリップ時刻を差し替える（負＝既定のまま）。</summary>
        internal static float SwingAnchorOverrideForCapture = -1f;

        /// <summary>【実測プローブ用】<see cref="DuelStageRig.SwingFinishSec"/> を差し替える（負＝既定のまま）。</summary>
        internal static float SwingFinishOverrideForCapture = -1f;

        /// <summary>【実測プローブ用】<see cref="DuelStageRig.BuntHitFinishSec"/> を差し替える（負＝既定のまま）。</summary>
        internal static float BuntHitFinishOverrideForCapture = -1f;

        /// <summary>【実測プローブ用】<see cref="DuelStageRig.BatWrapFrames"/> を差し替える（負＝既定のまま）。</summary>
        internal static float BatWrapFramesOverrideForCapture = -1f;

        /// <summary>【実測プローブ用】上の帯の差し替え（負＝既定）。</summary>
        internal static float SweepReachOverrideForCapture = -1f;
#endif
    }
}
