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
    /// （打者走力で上昇。design-14 P1-1）。既定0＝機能オフ、乱数消費順・結果とも従来と完全一致。</summary>
    public double FieldersChoiceProb { get; init; } = 0.0;
    /// <summary>失策出塁（ReachedOnError）時、全走者＋打者に追加1進塁が発生する基準確率（design-14 P1-6）。
    /// 既定0＝機能オフ、乱数消費順・結果とも Single と完全一致。</summary>
    public double ErrorExtraAdvanceProb { get; init; } = 0.0;
    /// <summary>一塁空き or 2アウトの三振で振り逃げが成立する基準確率（design-14 P1-2）。
    /// 既定0＝機能オフ、乱数消費順・結果とも従来と完全一致。</summary>
    public double DropThirdStrikeReachProb { get; init; } = 0.0;
    /// <summary>捕手Catching(-50)あたりの振り逃げ成立確率の減少幅。</summary>
    public double DropThirdStrikeCatchingSlope { get; init; } = 0.002;

    // === 暴投・パスボール（design-14 P2-8）===
    // 走者がいる各投球（実際にキャッチャーへ到達/通過した球のみ＝ファウル・インプレーは対象外）で、
    // バッテリーミスにより全走者が1つ進塁（三塁走者は生還）する。暴投=投手責/パスボール=捕手責と
    // 意味は分けるが、記録は当面「合算1カウント」で簡略化する（design-14の割り切り）。
    // 既定0＝機能オフ、走者無しの打席・Foul/InPlayの投球では分岐自体に入らずrng消費ゼロ。
    /// <summary>暴投の基準確率（投手責）。</summary>
    public double WildPitchProb { get; init; } = 0.0;
    /// <summary>投手Control(-50)あたりの暴投確率の減少幅。</summary>
    public double WildPitchControlSlope { get; init; } = 0.0006;
    /// <summary>パスボールの基準確率（捕手責）。</summary>
    public double PassedBallProb { get; init; } = 0.0;
    /// <summary>捕手Catching(-50)あたりのパスボール確率の減少幅。</summary>
    public double PassedBallCatchingSlope { get; init; } = 0.0006;

    // === 盗塁（設計書02 §4.2）。すべて秒の時間軸で校正（🟡係数は設計書07で調整） ===
    /// <summary>リード後の残り走塁距離[m]（二盗）。</summary>
    public double StealLeadDistanceM { get; init; } = 23.5;
    /// <summary>本塁→二塁の送球距離[m]（捕手ポップ）。</summary>
    public double CatchThrowDistanceM { get; init; } = 38.8;
    /// <summary>スタート遅延[s] = Intercept − Steal×Slope（盗塁パラメータでスタートが速くなる）。
    /// スプリント速度は天井速度（加速ランプ非モデル化）なので、反応＋加速不足をこの遅延で吸収する（Step4再校正）。</summary>
    public double StealReactionIntercept { get; init; } = 0.90;
    public double StealReactionSlope { get; init; } = 0.004;
    /// <summary>投手クイック[s]（投法補正は後回し, §2.2）。</summary>
    public double PitcherQuickSeconds { get; init; } = 1.40;
    /// <summary>捕手の握り替え[s]（ポップタイム＝これ＋送球時間）。</summary>
    public double PopTransferSeconds { get; init; } = 0.70;
    /// <summary>タッチ[s]。</summary>
    public double TagSeconds { get; init; } = 0.10;
    /// <summary>margin→成功確率のlogistic幅[s]（Step4再校正: 反応係数を上げ絶対タイムを現実化した分、
    /// グリッドが飽和しないよう幅を広げ、肩差が効くようにする）。</summary>
    public double StealMarginScale { get; init; } = 0.22;
    /// <summary>成功率バイアス[s]（リーグ平均成功率を現実値に寄せる校正項, 🟡）。</summary>
    public double StealSuccessBias { get; init; } = 0.38;
    /// <summary>本塁→三塁の送球距離[m]（design-14 P1-4、三盗）。</summary>
    public double CatchThrowToThirdDistanceM { get; init; } = 27.4;
    /// <summary>三盗の成功率バイアス（design-14「三盗は成功率下方」）。StealSuccessBiasに加算。</summary>
    public double StealThirdSuccessBias { get; init; } = -0.35;
    /// <summary>本盗の成功率バイアス（design-14「本盗はさらに下方」）。StealSuccessBiasに加算。</summary>
    public double StealHomeSuccessBias { get; init; } = -0.7;
    /// <summary>一・三塁で二盗を仕掛けた際、三塁走者も同時にスタートして本塁を狙う基準確率（design-14 P1-4）。
    /// 投げた塁（二塁）だけがアウト対象という原則から、成立時の三塁走者は無条件生還。
    /// 既定0＝機能オフ、単独二盗と乱数消費順・結果とも完全一致。</summary>
    public double DoubleStealThirdBreakProb { get; init; } = 0.0;
    /// <summary>牽制アウト（design-14 P1-5）の基準確率。走者の盗塁パラメータ(Steal)が高いほどリードが大きく上昇、
    /// 投手のMental（牽制の鋭さの代理指標）が高いほど下降。既定0＝機能オフ、乱数消費順・結果とも従来と完全一致。</summary>
    public double PickoffBaseProb { get; init; } = 0.0;
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
    /// <summary>外野手の捕球→送球（グラブ離し）[s]。</summary>
    public double OutfieldTransferSeconds { get; init; } = 0.60;
    /// <summary>中継（カットマン）の握り替え[s]。</summary>
    public double RelayTransferSeconds { get; init; } = 0.55;
    /// <summary>中継野手の送球速度[m/s]（リーグ平均内野肩）。</summary>
    public double RelayThrowSpeedMps { get; init; } = 32.0;
    /// <summary>カットマンの立ち位置（本塁からの距離割合。0.5=中間）。</summary>
    public double CutoffFractionFromHome { get; init; } = 0.5;
    /// <summary>この送球距離[m]を超えたら中継、以下は本塁へ直接返球。</summary>
    public double CutoffDistanceThresholdM { get; init; } = 60.0;
    /// <summary>本塁でのタッチ[s]。</summary>
    public double HomeTagSeconds { get; init; } = 0.12;
    /// <summary>margin→生還確率の logistic 幅[s]。</summary>
    public double HomeMarginScale { get; init; } = 0.25;
    /// <summary>生還確率バイアス[s]（憤死頻度・得点帯を現実値へ寄せる校正項）。
    /// Slice C の Heavy 実測で 2.00 に着地＝憤死≈0.26/試合・得点≈4.08/チーム（帯 3.5–6.0 内）。
    /// 走者物理の保守性（小さい二次リード・加速ランプ非モデル化・スプリント下限）をこの秒オフセットで吸収する。</summary>
    public double HomeSuccessBias { get; init; } = 2.00;
    /// <summary>ゴロ凡打で三塁走者が本塁を狙う際の追加スタート遅延[s]（G1, 設計書12 §4）。
    /// ヒットの走者(既に進行中)と違い、打球を読んでから走り出すぶん遅い＝内野前進で刺せる余地を作る。
    /// 1.55 で「内野前進は生還率を抑える(通常より下)＝AIの前進判断が正しく報われる」。</summary>
    public double HomeGrounderStartDelaySeconds { get; init; } = 1.55;

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
