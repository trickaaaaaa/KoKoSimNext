namespace KokoSim.Engine.Match.Game;

/// <summary>走塁進塁の確率係数（YAML駆動, 得点分布の統計回帰で調整）。</summary>
public sealed record BaserunningCoefficients
{
    /// <summary>単打で1塁走者が3塁まで進む基準確率（走力50時）。</summary>
    public double FirstToThirdOnSingle { get; init; } = 0.28;
    /// <summary>単打で2塁走者が生還する基準確率。</summary>
    public double SecondToHomeOnSingle { get; init; } = 0.62;
    /// <summary>二塁打で1塁走者が生還する基準確率。</summary>
    public double FirstToHomeOnDouble { get; init; } = 0.48;
    /// <summary>走力(Speed−50)あたりの追加進塁確率。</summary>
    public double SpeedSlope { get; init; } = 0.006;

    /// <summary>凡打(&lt;2アウト)で3塁走者が生還（犠飛・進塁打）する基準確率。</summary>
    public double SacFlyScoreProb { get; init; } = 0.30;
    /// <summary>凡打(&lt;2アウト)で走者が1つ進む基準確率（進塁打）。</summary>
    public double ProductiveOutAdvanceProb { get; init; } = 0.18;
    /// <summary>1塁走者あり・&lt;2アウトの凡打で併殺になる基準確率（走力で減少）。</summary>
    public double DoublePlayProb { get; init; } = 0.13;
    /// <summary>1塁走者あり・&lt;2アウトの凡打でDP不成立時に野選（FC）となる基準確率
    /// （打者走力で上昇。design-14 P1-1）。issue #402で 0.0→0.06 に正値化＝実ゲームで野選が発火（YAML校正値と一致）。</summary>
    public double FieldersChoiceProb { get; init; } = 0.06;
    /// <summary>失策出塁（ReachedOnError）時、全走者＋打者に追加1進塁が発生する基準確率（design-14 P1-6）。
    /// issue #169で 0.0→0.20 に正値化＝実ゲームで失策連鎖が発火（YAML sim値と一致）。base のゲート下で rng は1回のまま。</summary>
    public double ErrorExtraAdvanceProb { get; init; } = 0.20;
    /// <summary>失策連鎖の送球精度連動（Issue #37 / design-14 P1-6b）。連鎖確率 = ErrorExtraAdvanceProb −
    /// (送球者 ThrowAccuracy − 50)×この値。issue #169で 0.0→0.003 に正値化＝「送球が不正確な野手の失策ほど連鎖しやすい」（YAML sim値と一致）。</summary>
    public double ErrorExtraAdvanceAccuracySlope { get; init; } = 0.003;
    /// <summary>一塁空き or 2アウトの三振で振り逃げが成立する基準確率（design-14 P1-2）。
    /// issue #402で 0.0→0.001 に正値化＝実ゲームで振り逃げが発火（YAML校正値と一致）。
    /// issue #525で着弾コースゲート（<see cref="CanBatteryMiss"/>）導入に伴い 0.001→0.39 へ再校正
    /// （対象を第3ストライクが低い/大きく外れた球に絞ったぶん基準を引き上げ、振り逃げ/試合を校正前の
    /// ≈0.12-0.15 と同オーダーに保つ。Heavy実測 0.12-0.15/試合＝帯 0.02-0.40 内）。</summary>
    public double DropThirdStrikeReachProb { get; init; } = 0.39;
    /// <summary>捕手Catching(-50)あたりの振り逃げ成立確率の減少幅。</summary>
    public double DropThirdStrikeCatchingSlope { get; init; } = 0.002;

    // === 暴投・パスボール（design-14 P2-8）===
    // 走者がいる各投球（実際にキャッチャーへ到達/通過した球のみ＝ファウル・インプレーは対象外）で、
    // バッテリーミスにより全走者が1つ進塁（三塁走者は生還）する。暴投=投手責/パスボール=捕手責と
    // 意味は分けるが、記録は当面「合算1カウント」で簡略化する（design-14の割り切り）。
    // issue #402で 0.0→YAML校正値に正値化＝実ゲームで暴投・パスボールが発火。
    // 走者無しの打席・Foul/InPlayの投球では分岐自体に入らずrng消費ゼロ。
    // issue #525で着弾コースゲート（<see cref="CanBatteryMiss"/>）導入に伴い 0.0012/0.0008→0.035/0.023 へ再校正
    // （対象を低い/大きく外れた球に絞ったぶん基準を引き上げ、暴投・パスボール/試合を校正前の ≈0.56-0.59 と
    // 同オーダーに保つ。Heavy実測 0.54-0.58/試合＝帯 0.15-1.00 内）。制球/捕球スロープは副次的な微調整として
    // 据え置き（主たる制球依存は着弾コースが低い/外れる頻度＝ゲートの通過率で自然に効く）。
    /// <summary>暴投の基準確率（投手責）。</summary>
    public double WildPitchProb { get; init; } = 0.035;
    /// <summary>投手Control(-50)あたりの暴投確率の減少幅。</summary>
    public double WildPitchControlSlope { get; init; } = 0.0006;
    /// <summary>パスボールの基準確率（捕手責）。</summary>
    public double PassedBallProb { get; init; } = 0.023;
    /// <summary>捕手Catching(-50)あたりのパスボール確率の減少幅。</summary>
    public double PassedBallCatchingSlope { get; init; } = 0.0006;

    // === 暴投・捕逸・振り逃げの着弾コースゲート（issue #525）===
    // 「捕り損ねうる球か」は種別でなく実着弾コースで決める（監督判断 2026-08-07）。判定式は
    // BaserunningModel.CanBatteryMiss に1箇所集約する。ゾーン内の球（BottomM=0.50 以上・ゾーン内）は
    // 構造的に対象外＝捕れる球が後逸になることは起きない。狭めたぶん基準確率（WildPitchProb/
    // PassedBallProb/DropThirdStrikeReachProb）を上げて発生頻度を校正前と同オーダーに保つ。
    /// <summary>この高さ[m]未満の低い球（ワンバウンド相当）は捕手が捕り損ねうる。
    /// ストライクゾーン下端（<see cref="Field.StrikeZone.BottomM"/>=0.50）より下に置く＝ゾーン内の球と重ならない。</summary>
    public double BatteryMissLowY { get; init; } = 0.30;
    /// <summary>ストライクゾーン外へこの距離[m]以上外れた球（大きく外れた球）は捕手が捕り損ねうる。</summary>
    public double BatteryMissOutsideM { get; init; } = 0.25;

    // === 盗塁（設計書02 §4.2）。すべて秒の時間軸で校正（🟡係数は設計書07で調整） ===
    /// <summary>リード後の残り走塁距離[m]（二盗）。</summary>
    public double StealLeadDistanceM { get; init; } = 23.5;
    /// <summary>本塁→二塁の送球距離[m]（捕手ポップ）。</summary>
    public double CatchThrowDistanceM { get; init; } = 38.8;
    /// <summary>スタート遅延[s] = Intercept − Steal×Slope（盗塁パラメータでスタートが速くなる）。
    /// スプリント速度は天井速度（加速ランプ非モデル化）なので、反応＋加速不足をこの遅延で吸収する（Step4再校正）。</summary>
    public double StealReactionIntercept { get; init; } = 0.90;
    public double StealReactionSlope { get; init; } = 0.004;
    /// <summary>
    /// ジャンプ[s]（走者が投手の始動を読んで既に動き出している分。上の遅延から差し引く）。
    ///
    /// <para>2026-08-14: 判定境界の校正項 <see cref="StealSuccessBias"/>(0.38) を物理層へ移した半分
    /// （残り半分は <see cref="CatcherPopRiseSeconds"/>）。走者は「投球を見てから走り出す」のではなく
    /// 投手の始動を読んで走るので、遅延の全部を反応として払うのは過大だった＝この分を免除する。
    /// 実測: 走力50/盗塁50の二塁到達が 3.94秒 → 3.75秒（高校生の実測 3.6〜3.8秒に寄る）。</para>
    ///
    /// <para>⚠ 床（<c>max(0.10, …)</c> / ギャンブルの <c>max(0.05, …)</c>）の**後**に引く＝全走者から
    /// ちょうど同じ秒だけ引かれる。床の前に引くと快足走者だけ床に当たって引きが浅くなり、
    /// 「校正項を移しただけ（帯不変）」でなくなる。</para>
    /// </summary>
    public double StealJumpCreditSeconds { get; init; } = 0.19;
    /// <summary>投手クイック[s]（投法補正は後回し, §2.2）。</summary>
    public double PitcherQuickSeconds { get; init; } = 1.40;
    /// <summary>捕手の握り替え[s]（ポップタイム＝これ＋送球時間）。</summary>
    public double PopTransferSeconds { get; init; } = 0.70;
    /// <summary>
    /// 捕手が立ち上がって投げる形を作る分[s]（ポップタイムの**守備力で縮まない**固定部分）。
    ///
    /// <para>2026-08-14: <see cref="StealSuccessBias"/>(0.38) を物理層へ移した半分（残り半分は
    /// <see cref="StealJumpCreditSeconds"/>）。実測: 肩50のポップタイム（捕球→二塁到達）が
    /// 1.91秒 → 2.10秒（高校生の実測 2.0〜2.3秒に寄る。1.91秒はプロの好捕手の値だった）。</para>
    ///
    /// <para>⚠ <see cref="PopTransferSeconds"/> に足し込まず別項にするのは、あちらが守備力で伸縮する
    /// （<see cref="TransferFieldingSlope"/>）ため。足し込むと捕手ごとに移した秒が変わり
    /// 「校正項を移しただけ」でなくなる。立ち上がりは技術で縮められない部分＝固定が物理的にも正しい。</para>
    /// </summary>
    public double CatcherPopRiseSeconds { get; init; } = 0.19;
    /// <summary>
    /// トランスファー倍率の守備(Fielding)傾き（Issue #36）。走塁系の各握り替え（捕手ポップ/外野返球/中継/帰塁）
    /// に共通。倍率 = max(min, 1 − (Fielding−50)×この値)。守備50で×1.0＝恒等（帯不変）。
    /// <see cref="Fielding.FieldingCoefficients.ThrowTransferFieldingSlope"/> と値を揃える。
    /// </summary>
    public double TransferFieldingSlope { get; init; } = 0.004;
    /// <summary>トランスファー倍率の下限（最速側クランプ, Issue #36）。</summary>
    public double TransferFactorMin { get; init; } = 0.70;
    /// <summary>タッチ[s]。</summary>
    public double TagSeconds { get; init; } = 0.10;
    /// <summary>margin→成功確率のlogistic幅[s]（Step4再校正: 反応係数を上げ絶対タイムを現実化した分、
    /// グリッドが飽和しないよう幅を広げ、肩差が効くようにする）。</summary>
    public double StealMarginScale { get; init; } = 0.22;
    /// <summary>
    /// 成功率バイアス[s]（リーグ平均成功率を現実値に寄せる校正項, 🟡）。
    ///
    /// <para>⚠ 2026-08-14 に <b>0.38 → 0.0</b>。中身を物理層の秒へ移した
    /// （走者側 <see cref="StealJumpCreditSeconds"/> 0.19 ＋ 守備側 <see cref="CatcherPopRiseSeconds"/> 0.19）。
    /// この項は<b>判定の秒だけを動かして絵の秒には出ない</b>ので、0以外にすると
    /// 「捕手の送球が先に届いた絵なのにセーフ」が構造的に戻る（実測: 成功50%の場面で送球が0.48秒先着）。
    /// リーグ平均成功率を動かしたいときは、移した先の2項（＝絵に出る秒）を動かすこと。</para>
    /// </summary>
    public double StealSuccessBias { get; init; } = 0.0;
    /// <summary>本塁→三塁の送球距離[m]（design-14 P1-4、三盗）。</summary>
    public double CatchThrowToThirdDistanceM { get; init; } = 27.4;
    /// <summary>三盗の成功率バイアス（design-14「三盗は成功率下方」）。StealSuccessBiasに加算。</summary>
    public double StealThirdSuccessBias { get; init; } = -0.35;
    /// <summary>本盗の成功率バイアス（design-14「本盗はさらに下方」）。StealSuccessBiasに加算。</summary>
    public double StealHomeSuccessBias { get; init; } = -0.7;
    /// <summary>一・三塁で二盗を仕掛けた際、三塁走者も同時にスタートして本塁を狙う基準確率（design-14 P1-4）。
    /// 投げた塁（二塁）だけがアウト対象という原則から、成立時の三塁走者は無条件生還。
    /// issue #402で 0.0→0.4 に正値化＝実ゲームで一・三塁重盗が発火（YAML校正値と一致）。</summary>
    public double DoubleStealThirdBreakProb { get; init; } = 0.4;
    /// <summary>牽制アウト（design-14 P1-5）の基準確率。走者の盗塁パラメータ(Steal)が高いほどリードが大きく上昇、
    /// 投手のMental（牽制の鋭さの代理指標）が高いほど下降。issue #402で 0.0→0.05 に正値化＝実ゲームで牽制アウトが発火（YAML校正値と一致）。</summary>
    public double PickoffBaseProb { get; init; } = 0.05;
    /// <summary>走者Steal(-50)あたりの牽制アウト確率の上昇幅。</summary>
    public double PickoffRunnerLeadSlope { get; init; } = 0.0015;
    /// <summary>投手Mental(-50)あたりの牽制アウト確率の減少幅。</summary>
    public double PickoffPitcherSenseSlope { get; init; } = 0.001;
    /// <summary>牽制アウト確率の上限。</summary>
    public double PickoffMaxProb { get; init; } = 0.08;

    // === バント（設計書02 §4.3） ===
    /// <summary>基礎成功率 = BuntBase + Bunt×BuntSkillSlope − 球速補正。</summary>
    public double BuntBase { get; init; } = 0.60;
    public double BuntSkillSlope { get; init; } = 0.005;
    /// <summary>球速補正: max(0, velo−Ref)×Penalty を成功率から減算。</summary>
    public double BuntVelocityRefKmh { get; init; } = 130.0;
    public double BuntVelocityPenaltyPerKmh { get; init; } = 0.004;
    /// <summary>空振り/ファウル/小フライの発生シェア（成功判定の前に分岐）。</summary>
    public double BuntMissShare { get; init; } = 0.07;
    /// <summary>ファウル基準シェア（Bunt50時）。上手い打者ほど下がる（§4.3, Step4-③）。</summary>
    public double BuntFoulShare { get; init; } = 0.18;
    /// <summary>ファウルシェアの技術傾き（(Bunt−50)あたり減算）。下限は BuntFoulFloor。</summary>
    public double BuntFoulSkillSlope { get; init; } = 0.0012;
    public double BuntFoulFloor { get; init; } = 0.06;
    public double BuntPopShare { get; init; } = 0.05;

    // === バント内野安打の時間軸判定（設計書02 §4.1b/§4.3, Step4-②）===
    // 犠打・セーフティとも「打者走者→一塁」対「バント処理→一塁送球」の秒勝負に通す。
    // これにより快足×好バントが送りバントでも数%生きる（従来は safety のみ内野安打枝を持ち0%だった）。
    /// <summary>本塁→一塁の塁間[m]（守備側 BaseDistanceM と同一軸）。</summary>
    public double BuntBaseDistanceM { get; init; } = 27.4;
    /// <summary>打者走者の実戦反応オフセット[s]（守備側 RunnerReactionSeconds と同値で揃える）。</summary>
    public double BuntRunnerReactionSeconds { get; init; } = 0.45;
    /// <summary>左打者の駆け抜け短縮[s]（守備側 LeftBatterFirstStepBonusSeconds と同値）。</summary>
    public double BuntLeftFirstStepBonusSeconds { get; init; } = 0.13;
    /// <summary>犠打の構え遅延[s]（早く見せて走り出しが遅れる）。</summary>
    public double SacrificeBuntSquareDelaySeconds { get; init; } = 0.35;
    /// <summary>セーフティ（プッシュ/ドラッグ）の構え遅延[s]（走りながら転がすので小さい）。</summary>
    public double SafetyBuntSquareDelaySeconds { get; init; } = 0.12;
    /// <summary>バント処理→一塁送球の基準所要[s]（コーナー突進＋素手処理＋短距離送球）。</summary>
    public double BuntFieldThrowBaseSeconds { get; init; } = 3.70;
    /// <summary>好バントほど際どく転がり処理が遅れる係数（(Bunt−50)あたり加算）。</summary>
    public double BuntPlacementSlope { get; init; } = 0.006;
    /// <summary>内野安打 margin→確率の logistic 幅[s]。</summary>
    public double BuntInfieldHitTimeScale { get; init; } = 0.14;

    // === 本塁クロスプレー（バックホーム憤死, 設計書12 §3, F2）===
    // 走塁 §4.1 と同じ時間の勝負を本塁で解く。走者(走力＋走塁判断) vs 外野処理＋中継＋タッチ。
    /// <summary>本塁生還を狙う走者の判断遅延[s] = Intercept − 走塁判断×Slope。</summary>
    public double HomeRunnerReactionIntercept { get; init; } = 0.55;
    public double HomeRunnerReactionSlope { get; init; } = 0.004;
    /// <summary>塁上の二次リード分の距離短縮[m]（残り塁間距離から差し引く）。</summary>
    public double HomeLeadDistanceM { get; init; } = 3.5;
    /// <summary>
    /// 塁上の走者のトップスピード係数（Issue #316）。リードを取って動き出している走者は
    /// 静止スタートより速い。打者走者側の <c>FieldingCoefficients.RunningTopSpeedFactor</c> と同値。
    /// </summary>
    public double RunnerTopSpeedFactor { get; init; } = 1.15;
    /// <summary>
    /// 塁上の走者がベースを1つ回るたびの損失[s]（Issue #316）。払うのは**目標塁の手前で回る塁**の数
    /// （三塁→本塁は0回、二塁→本塁は三塁を回るので1回）。
    /// 打者走者側の <c>FieldingCoefficients.BaseTurnSeconds</c> と同値。
    /// </summary>
    public double RunnerBaseTurnSeconds { get; init; } = 0.30;
    /// <summary>外野手の捕球→送球（グラブ離し）[s]。</summary>
    public double OutfieldTransferSeconds { get; init; } = 0.60;
    /// <summary>中継（カットマン）の握り替え[s]。</summary>
    public double RelayTransferSeconds { get; init; } = 0.55;
    /// <summary>中継野手の送球速度[m/s]（リーグ平均内野肩）。</summary>
    public double RelayThrowSpeedMps { get; init; } = 32.0;
    /// <summary>カットマンの立ち位置（本塁からの距離割合。0.5=中間）。</summary>
    public double CutoffFractionFromHome { get; init; } = 0.5;
    /// <summary>
    /// 外野手が中継を挟まず投げ切れる時間[s]（issue #526）。中継に入る距離は
    /// <c>肩の送球速度[m/s] × この秒数</c> で決まる＝**強肩ほど遠くから直接返球できる**。
    ///
    /// 旧 <c>CutoffDistanceThresholdM = 60.0</c>（距離固定）を置き換えた。固定だと肩が判定に
    /// 一切効かず、しきい値をまたいだ瞬間の損失が**強肩ほど大きい**（実測: Arm20 で +0.41s に対し
    /// Arm99 は +0.71s）＝実際の野球と逆になっていた。中継では肩が前半（外野手→カットマン）に
    /// しか効かず、固定の握り替え <see cref="RelayTransferSeconds"/> が上乗せされるため。
    ///
    /// 1.88 は Arm 50（送球速度 31.94 m/s）でちょうど 60.05m ＝リーグ平均の挙動を旧値と揃える値。
    /// 中継に入る距離: Arm20→52m ／ Arm50→60m ／ Arm80→68m ／ Arm99→73m。
    ///
    /// ⚠ この係数は「**遠い球は外野手がノーバウンドで届かない**」という、engine の送球モデル
    /// （等速直線・距離減衰なし）に無い物理をしきい値で代用している。単に「直接と中継の速い方を選ぶ」に
    /// すると Arm51 以上では中継が**絶対に選ばれなくなる**（0.55×守備補正 &lt; dist×0.5×(1/肩 − 1/32) が
    /// 肩&gt;32m/s で恒真）＝中継が消滅するので、その置き換えはしないこと。距離減衰のモデル化は案B（未着手）。
    /// </summary>
    public double CutoffThrowSeconds { get; init; } = 1.88;
    /// <summary>本塁でのタッチ[s]。</summary>
    public double HomeTagSeconds { get; init; } = 0.12;
    /// <summary>margin→生還確率の logistic 幅[s]。</summary>
    public double HomeMarginScale { get; init; } = 0.25;
    /// <summary>生還確率バイアス[s]（憤死頻度・得点帯を現実値へ寄せる校正項）。
    /// 走者物理の保守性をこの秒オフセットで吸収する。
    /// Issue #316 で走者の加速ランプ（トップスピード係数）をモデル化し、三塁→本塁が 0.43s 速くなったため、
    /// 吸収していたぶんを差し引いて 2.00 → 1.57（実測に合わせたのではなく、直した秒数をそのまま引いた値）。
    /// Heavy 実測: 本塁憤死 0.324–0.338/試合・得点 6.64–6.66/チーム（いずれも帯内）。</summary>
    public double HomeSuccessBias { get; init; } = 1.57;
    /// <summary>ゴロ凡打で三塁走者が本塁を狙う際の追加スタート遅延[s]（G1, 設計書12 §4）。
    /// ヒットの走者(既に進行中)と違い、打球を読んでから走り出すぶん遅い＝内野前進で刺せる余地を作る。
    /// 1.55 で「内野前進は生還率を抑える(通常より下)＝AIの前進判断が正しく報われる」。</summary>
    public double HomeGrounderStartDelaySeconds { get; init; } = 1.55;

    // === 三塁への進塁レース（単打の一塁→三塁, Issue #89, 設計書12 §3.5/§4.6）===
    // 本塁と同じ「走者(走力＋走塁判断) vs 外野処理＋中継＋三塁送球」を三塁ベースで解く。
    // 走者反応・二次リード・外野処理/中継の各係数は本塁と共有し、塁固有はタッチ・バイアス・幅の3つだけ分離する。
    /// <summary>三塁でのタッチ[s]（ベースタッチ。本塁のクロスプレーより短い）。</summary>
    public double ThirdTagSeconds { get; init; } = 0.10;
    /// <summary>三塁到達 margin→確率の logistic 幅[s]。本塁より広め＝打球の深さ・外野の肩が
    /// 送り/自重の境界を跨ぐよう感度を持たせる（Issue #89 の主眼）。</summary>
    public double ThirdMarginScale { get; init; } = 0.45;
    /// <summary>三塁到達確率バイアス[s]（一塁→三塁の到達率・三塁憤死頻度を現実値へ寄せる校正項）。
    /// 走者物理の保守性を吸収する。
    /// Issue #316 で加速ランプとベースを回る損失をモデル化し、一塁→三塁が 0.62s 速くなったため、
    /// 吸収していたぶんを差し引いて 1.50 → 0.88（直した秒数をそのまま引いた値）。
    /// Heavy 実測: 三塁憤死 0.219–0.239/試合＝#316 前（0.222–0.234）とほぼ同値に戻る。</summary>
    public double ThirdSuccessBias { get; init; } = 0.88;

    // === ゴロ凡打の進塁打レース（二塁→三塁, Issue #318, 設計書12 §3.5）===
    // ゴロ凡打×二塁走者あり×一塁走者なし×三塁空きの1場面（＝典型的な進塁打）を 18%固定テーブル
    // （ProductiveOutAdvanceProb）から「捕球点→三塁の物理レース」へ置換する。守備は幾何（P_direct）で
    // 「一塁を取る/一塁を捨てて三塁へ直接投げる/走者は自重」を選ぶ。走者の式は本塁・三塁と共有し新設しない。
    /// <summary>ゴロを読んでから走り出す追加スタート遅延[s]（G1のゴロ本塁レース HomeGrounderStartDelaySeconds=1.55
    /// より短い＝進塁は生還ほど慎重に見極めない）。</summary>
    public double GrounderAdvanceStartDelaySeconds { get; init; } = 0.90;
    /// <summary>進塁先（三塁）でのタッチ[s]（<see cref="ThirdTagSeconds"/> と同軸）。</summary>
    public double AdvanceTagSeconds { get; init; } = 0.10;
    /// <summary>進塁到達 margin→確率の logistic 幅[s]（三塁レースと同軸）。</summary>
    public double AdvanceMarginScale { get; init; } = 0.45;
    /// <summary>進塁到達確率バイアス[s]（校正項）。帯校正はこの1本だけで行う（Issue #318）。</summary>
    public double AdvanceSuccessBias { get; init; } = 0.00;
    /// <summary>一塁手の転送握り替え[s]（一塁を取ってから三塁へ転送する経路 P_relay 用。
    /// <see cref="RelayTransferSeconds"/> と同軸）。</summary>
    public double AdvanceRelayTransferSeconds { get; init; } = 0.55;
    /// <summary>走者の三塁到達見込みがこれ以下なら守備は一塁を捨てて三塁へ直接送球する（分岐B/Cの境界）。</summary>
    public double AdvanceDirectThrowMaxRunnerSuccess { get; init; } = 0.45;

    // === 併殺の物理レース（内野ゴロ×一塁走者, Issue #369, 設計書12 §2）===
    // 13%固定テーブル（DoublePlayProb）を二段の秒勝負へ置換する: ①捕球点→二塁の封殺が間に合うか（P_force2）
    // ②二塁→一塁の転送（ピボット）が間に合うか。守備は幾何で3分岐を純関数で選ぶ:
    // A) 二塁封殺が間に合わない（一塁走者が二塁へ到達しそう）→ 一塁を取る（打者アウト＝従来のフォールバック）／
    // B) 二塁は取れるが転送が間に合わない → 野選（二塁封殺＋打者一塁生存）／ C) 二塁も一塁も取れる → 併殺。
    // 走者所要は本塁・三塁レースと同じ HomePlayResolver 経由（新しい走者の式は書かない, 不変条件#1）。
    // 方向性・深さは係数でなく幾何から創発する（二遊間の浅いゴロは二塁が近い＝封殺容易＋転送短＝併殺／
    // 三遊間の深いゴロや一塁側のゴロは二塁が遠い＝封殺困難＝一塁のみ／快足打者は転送を上回る＝野選）。
    /// <summary>一塁走者がゴロを読んでから二塁へ走り出す追加スタート遅延[s]（RunnerContext の反応に上乗せ。
    /// フォース走者は打球を見てから走るぶん、盗塁のような好スタートは切れない）。</summary>
    public double DoublePlayR1StartDelaySeconds { get; init; } = 0.30;
    /// <summary>二塁封殺のタッチ/捕球[s]（<see cref="ThirdTagSeconds"/> と同軸）。</summary>
    public double SecondForceTagSeconds { get; init; } = 0.10;
    /// <summary>二塁封殺 margin→到達確率の logistic 幅[s]（三塁・進塁レースと同軸＝打球の深さ・肩を効かせる）。</summary>
    public double SecondForceMarginScale { get; init; } = 0.45;
    /// <summary>二塁封殺 到達確率バイアス[s]（校正項。一塁走者物理の保守性を吸収する）。</summary>
    public double SecondForceSuccessBias { get; init; } = 0.00;
    /// <summary>一塁走者の二塁到達見込み（P_r1_safe）がこれを超えたら守備は封殺自体が不可＝一塁を取る（分岐A）。
    /// 内野ゴロの二塁封殺は短い送球で走者(≈3.5s)に先着するのが通常＝この分岐Aは俊足走者/深い右方向ゴロ等の稀ケース。
    /// <see cref="AdvanceDirectThrowMaxRunnerSuccess"/> と同型の「守備が幾何で送球先を選ぶ」境界。</summary>
    public double DoublePlayForceMaxRunnerSuccess { get; init; } = 0.60;
    /// <summary>二塁封殺は間に合うが、二塁→一塁の転送で打者を刺せる見込み（P_relay_out）がこれ未満なら
    /// 守備は併殺を狙わず「確実な一塁アウト」を取る（分岐A＝打者一塁アウト＋一塁走者は二塁へ）。
    /// ＝ルーティンなゴロ凡打（進塁打）と併殺狙いを分ける境界。高いほど併殺を狙わず一塁のみが増える。
    /// これにより野選（二塁封殺＋打者生存）は「併殺を狙った末に転送が僅かに間に合わなかった残差」に限られ、
    /// 現実の稀な発生頻度に収まる（守備はそもそも刺せない相手には二塁へ投げない）。</summary>
    public double DoublePlayAttemptMinRelaySuccess { get; init; } = 0.80;
    /// <summary>二塁ベース経由の転送（ピボット）握り替え[s]。一塁手の中継 <see cref="RelayTransferSeconds"/> とは別値
    /// （捕って踏み替えて投げ返す連携動作＝中継より長い。#318 が外野値と分けたのと同じ扱い）。
    /// 「併殺の名手」スキル（<see cref="Players.Skill.DoublePlayArtist"/>）はこの時間を短縮する
    /// ＝確率への加算でなく時間の短縮（Skill.cs:39「二塁経由の送球が速い」通りの効き方, Issue #369）。</summary>
    public double DoublePlayPivotTransferSeconds { get; init; } = 0.90;
    /// <summary>二塁→一塁の転送送球 margin→打者生存（野選）確率の logistic 幅[s]（一塁の際どい封殺＝狭め）。</summary>
    public double DoublePlayRelayMarginScale { get; init; } = 0.20;
    /// <summary>二塁→一塁の転送送球の校正項[s]（打者生存＝野選頻度を現実値へ寄せる。正で打者生存＝野選寄り）。
    /// 併殺打/試合と得点環境を Heavy 実測で校正するノブ（#318 の AdvanceSuccessBias と同役）。</summary>
    public double DoublePlayRelayBias { get; init; } = 0.00;

    // === 犠飛のタッチアップ（Issue #90, 設計書12 §3.5）===
    // タッグアップは走者の時計の起点が「捕球の瞬間」である点だけが本塁クロスプレーと異なる。走者は捕球時刻に
    // スタート（二次リードなし＝全塁間 27.4m を走る）、守備も同じ捕球時刻から送球する＝共通起点が捕球時刻。
    // 勝負は「三塁→本塁を走る時間」対「捕球地点→本塁の送球時間」。深い外野フライは送球が長く生還、浅いフライは
    // 送球が短く自重/憤死。走者反応・外野処理/中継の各係数は本塁と共有し、タッチは HomeTagSeconds を流用、
    // 塁固有はバイアス・幅の2つだけ分離する。
    /// <summary>タッチアップ到達 margin→生還確率の logistic 幅[s]。本塁クロスプレーより広め＝打球の深さ・
    /// 外野の肩が送り/自重の境界を跨ぐよう感度を持たせる（Issue #90 の主眼）。</summary>
    public double TagUpMarginScale { get; init; } = 0.40;
    /// <summary>タッチアップ生還確率バイアス[s]（生還率・本塁憤死頻度を現実値へ寄せる校正項）。
    /// 走者物理の保守性（二次リードなし・加速ランプ非モデル化・スプリント下限）をこの秒オフセットで吸収する。
    /// Heavy 実測で校正（Issue #90）: 1.50 で 得点≈5.1/チーム・本塁憤死≈0.365/試合（帯内）かつ球質タイプの
    /// 実戦バランス中立（Archetypes_AreBalanceNeutral_InActualGames）を維持。</summary>
    public double TagUpSuccessBias { get; init; } = 1.50;

    /// <summary>
    /// 判定オーバーレイ（Issue #59）: margin[s]の絶対値がこの値未満なら「際どい」＝セーフ表示の対象。
    /// アウトは常に表示するため使わない。表示専用（判定・帯には無関係, 決定論を変えない）。
    /// </summary>
    public double CloseCallMarginSeconds { get; init; } = 0.15;

    // === ライナー併殺（コンタクト始動の走者が打球を空中で捕られ塁へ戻れない, 設計書12 §4, G2）===
    // 本塁の時間勝負と同型: 走者が捕球までに稼いだリード(走行) vs 守備の塁への戻し送球。
    /// <summary>コンタクト始動〜走り出しの反応[s]（接触と同時に飛び出す＝ほぼ即時）。</summary>
    public double LinerBreakReactionSeconds { get; init; } = 0.05;
    /// <summary>「盲目的に走る」時間の上限[s]。高いフライは滞空が長くても、走者は途中で
    /// 軌道を見て早めに戻り支度を始める（滞空時間なりにリードが伸び続けない＝現実的な頭打ち）。</summary>
    public double LinerCommitCapSeconds { get; init; } = 1.00;
    /// <summary>リード距離の算出に使う基準走力[m/s]（走者個人の走力ではない）。二次リードは
    /// コーチング上の目安距離で決まり個人差は小さい＝戻りの所要時間でこそ個人の走力差が効く。</summary>
    public double LinerReferenceSprintSpeedMps { get; init; } = 7.25;
    /// <summary>捕球を見て反転するまでの判断遅延[s]（打球音・視認で戻り出しが遅れる）。</summary>
    public double DoubledOffReverseSeconds { get; init; } = 0.35;
    /// <summary>捕球した野手の捕球→送球（グラブ離し）[s]（内野寄りの打球なので外野より短い）。</summary>
    public double DoubledOffTransferSeconds { get; init; } = 0.30;
    /// <summary>塁でのタッチ[s]。</summary>
    public double DoubledOffTagSeconds { get; init; } = 0.10;
    /// <summary>margin→塁へ戻れる(Safe)確率の logistic 幅[s]。</summary>
    public double DoubledOffMarginScale { get; init; } = 0.20;
    /// <summary>戻れる確率バイアス[s]（Heavy実測で校正, 🟡）。</summary>
    public double DoubledOffSuccessBias { get; init; } = 0.0;

    // === 守備の読み/ピッチアウト（設計書09 §1, 設計書12 §5, G3）===
    // 「捕手リード＋投手センス vs 状況の意外性」を確率化。読まれた盗塁はピッチアウトで捕手が優位に立ち
    // 送球が速くなる＝刺されやすい。意表（低い企図度・ギャンブル始動）は読まれにくい。
    /// <summary>盗塁企図の基準「予想されやすさ」E∈[0,1]。快足盗塁屋のセオリー通りほど高い（読まれやすい）。</summary>
    public double StealExpectednessIntercept { get; init; } = 0.42;
    /// <summary>走者のSteal能力→予想度（快足盗塁屋は動きが読まれやすい）。</summary>
    public double StealExpectednessStealSlope { get; init; } = 0.006;
    /// <summary>ギャンブル始動は意表＝予想度を下げる（読まれにくくなる）。</summary>
    public double GambleUnexpectednessReduction { get; init; } = 0.28;
    /// <summary>守備の読み力の基準（捕手リード＋投手センスが50/50で得られる読み力）。</summary>
    public double StealReadIntercept { get; init; } = 0.50;
    /// <summary>捕手リード(Lead−50)あたりの読み力加算。</summary>
    public double StealReadCatcherLeadSlope { get; init; } = 0.006;
    /// <summary>投手センス(Mental−50)あたりの読み力加算（バッテリーで読む）。</summary>
    public double StealReadPitcherSenseSlope { get; init; } = 0.003;
    /// <summary>ピッチアウト（読み切り）確率の上限。読み力×予想度でも越えない。</summary>
    public double MaxPitchoutProb { get; init; } = 0.55;
    /// <summary>ピッチアウト成功時の守備時間短縮[s]（捕手が立って構え、外し球で楽に捕って送る＝優位）。</summary>
    public double PitchoutDefenseBonusSeconds { get; init; } = 0.35;
    /// <summary>ギャンブル走者はピッチアウトに無防備＝さらに刺されやすい追加短縮[s]。</summary>
    public double GamblePitchoutExtraBonusSeconds { get; init; } = 0.20;
    /// <summary>ギャンブル始動の好スタート[s]（走者の実効スタートを前倒し＝時間短縮）。</summary>
    public double GambleJumpBonusSeconds { get; init; } = 0.15;
}
