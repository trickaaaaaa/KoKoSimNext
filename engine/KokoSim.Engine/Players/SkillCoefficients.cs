namespace KokoSim.Engine.Players;

/// <summary>
/// スキルの効果量・出現率（設計書10, YAML駆動）。効果は控えめ（多数の弱スキルの集合体にしない）。
/// スキルなしの選手には一切作用しない＝既存の統計帯を変えない（不変条件#5）。
/// 検証（設計書07）でスキル保有分布と勝率寄与を確認し、壊れ性能を排除する。
/// </summary>
public sealed record SkillCoefficients
{
    // --- 打撃系（試合内・行動特性） ---
    /// <summary>目慣れ（旧 尻上がり・野手）: 当該試合の打席数1つあたりのミート補正と上限。</summary>
    public double SlowStarterBatContactPerPa { get; init; } = 1.6;
    public double SlowStarterBatMaxBonus { get; init; } = 6.0;
    /// <summary>
    /// 流し打ち（旧「広角打法」を置き換え・issue #907）: 引っ張りバイアスの縮小倍率（&lt;1）。issue #262 の広角打法の意味を
    /// そのまま引き継ぐ＝「早くても差し込まれても方向が大こけしない」。残差σには作用しない。打力にも作用しない。
    /// </summary>
    public double OppositeFieldPullFactor { get; init; } = 0.55;
    /// <summary>流し打ち: 逆方向（打者から見て引っ張りの反対側）へ飛んだ打球の初速の上乗せ率（0.04＝+4%）。</summary>
    public double OppositeFieldExitBonus { get; init; } = 0.04;
    /// <summary>流し打ち: 「逆方向」とみなす打球方向[deg]（中堅からこれ以上逆方向へ寄った打球だけ強くなる）。</summary>
    public double OppositeFieldMinBearingDeg { get; init; } = 10.0;
    /// <summary>速球打ち: ストレート系（ストレート・ツーシーム）の球に対するミートの加算[能力点]。</summary>
    public double FastballHitterContactBonus { get; init; } = 6.0;
    /// <summary>変化球打ち: 変化球に対するミートの加算[能力点]（空振りが減る）。</summary>
    public double BreakingBallHitterContactBonus { get; init; } = 5.0;
    /// <summary>変化球打ち: 変化球に対する選球眼の加算[能力点]（ボール球を振りにくい）。</summary>
    public double BreakingBallHitterDisciplineBonus { get; init; } = 6.0;
    /// <summary>つなぎの打撃: 走者ありの打席で打球方向を右方向（一塁側＝＋）へずらす量[deg]。打者の左右に依らない。</summary>
    public double TeamBattingBearingShiftDeg { get; init; } = 5.0;
    /// <summary>つなぎの打撃: 走者ありの打席で打球角をずらす量[deg]（負＝低く＝ゴロ寄り）。</summary>
    public double TeamBattingLaunchShiftDeg { get; init; } = -2.0;
    /// <summary>つなぎの打撃: 走者ありの打席でコンタクト品質へ足す量（負＝打球が弱くなりヒットになりにくい）。</summary>
    public double TeamBattingQualityShift { get; init; } = -0.005;
    /// <summary>初球から振る: 初球にスイングを仕掛ける確率。</summary>
    public double FirstPitchSwingProb { get; init; } = 0.62;
    /// <summary>粘り打ち: ファウル率の拡大倍率（球数を吐かせる）。</summary>
    public double GrinderFoulFactor { get; init; } = 1.30;
    /// <summary>ムラっけ: 当日の出来（day-form）の振れ幅の拡大倍率。</summary>
    public double StreakyVarianceFactor { get; init; } = 1.7;

    // --- 投手系（試合内・非線形） ---
    /// <summary>尻上がり(投): 当該試合の対戦打者数1人あたりのコントロール補正と上限。</summary>
    public double SlowStarterPitchControlPerBf { get; init; } = 0.45;
    public double SlowStarterPitchMaxBonus { get; init; } = 8.0;
    /// <summary>打者一巡: この対戦打者数を超えると崩れる（3巡目≈18人〜）。</summary>
    public int SecondTimeThroughBattersFaced { get; init; } = 18;
    public double SecondTimeThroughControlPenalty { get; init; } = 6.0;
    public double SecondTimeThroughStuffPenalty { get; init; } = 0.10;
    /// <summary>荒れ球: コントロールを実効的に下げ（四球増）、球威を上げる（打ちにくさ）。</summary>
    public double EffectivelyWildControlPenalty { get; init; } = 7.0;
    public double EffectivelyWildStuffBonus { get; init; } = 0.13;
    /// <summary>クセ球: 球速に依らず球威（空振り誘発）を上げる質的個性。</summary>
    public double DeceptiveBallStuffBonus { get; init; } = 0.18;

    /// <summary>立ち上がり難: このイニングまで（1〜2回）制球・球威が落ちる。</summary>
    public int ShakyStartInnings { get; init; } = 2;
    public double ShakyStartControlPenalty { get; init; } = 6.0;
    public double ShakyStartStuffPenalty { get; init; } = 0.08;
    /// <summary>クイック◎: 走者ありのクイックの秒の短縮[s]（盗塁の守備側の秒から引く）。</summary>
    public double QuickDeliverySeconds { get; init; } = 0.10;
    /// <summary>クイック×: 走者ありのクイックの秒の延長[s]（盗塁の守備側の秒へ足す）。</summary>
    public double SlowDeliverySeconds { get; init; } = 0.10;
    /// <summary>火消し: 途中登板した投手が、この人数（対戦打者数）に達するまで上乗せを受ける。</summary>
    public int FiremanBatters { get; init; } = 3;
    public double FiremanControlBonus { get; init; } = 6.0;
    public double FiremanStuffBonus { get; init; } = 0.08;
    /// <summary>火消し: 継投AIのリリーフ選びで、火消し持ちの候補の比較値（4能力の和）へ足す量。</summary>
    public double FiremanReliefScoreBonus { get; init; } = 12.0;

    // --- チャンス◎×・ピンチ◎×（設計書10 §0.1・issue #907）: PressureModel の倍率の振れ raw に掛かる側 ---
    /// <summary>◎: raw の下限＝+この値×プレッシャー指数（圧による低下を消し、わずかなプラスにする）。</summary>
    public double ClutchFloorPerPressure { get; init; } = 0.004;
    /// <summary>×: raw が負のときの拡大倍率（圧による低下を大きくする）。</summary>
    public double ChokeNegativeAmplify { get; init; } = 2.0;
    /// <summary>×: raw の上限＝−この値×プレッシャー指数（精神力が高くても圧で少しは落ちる）。</summary>
    public double ChokeCeilingPerPressure { get; init; } = 0.008;

    // --- チーム系 ---
    /// <summary>精神的支柱: 主将としての統率力（緩和量）の拡大倍率（設計書09 §8）。</summary>
    public double SpiritualPillarCaptainFactor { get; init; } = 1.4;
    /// <summary>
    /// ムードメーカー: 出場中（投手 or 守備でフィールドに立っている）なら、プレッシャー負補正の緩和量
    /// （<see cref="Match.Game.TeamState.MitigationFor"/>）へ加算する固定量。主将の統率力とは別枠で
    /// 誰か1人でも出場していれば加算（複数人保有でも重複加算しない）。
    /// </summary>
    public double MoodmakerMitigationBonus { get; init; } = 0.06;
    /// <summary>
    /// 練習リーダー: 同じロスターに1人でも保有者が居ると、本人以外の全部員の週間経験値へ掛かる倍率
    /// （<see cref="Season.DevelopmentModel.TeamSkillExpFactor"/>）。
    /// </summary>
    public double PracticeLeaderExpFactor { get; init; } = 1.05;
    /// <summary>
    /// お手本: 自分より学年が上（先輩）で同ポジション（最高適性）の保有者が居ると、その後輩の
    /// 週間経験値へ掛かる倍率（<see cref="Season.DevelopmentModel.TeamSkillExpFactor"/>）。
    /// </summary>
    public double RoleModelExpFactor { get; init; } = 1.08;

    // --- 体質・成長系 ---
    public double DiligentExpFactor { get; init; } = 1.15;
    public double LazyExpFactor { get; init; } = 0.85;
    public double DurableInjuryFactor { get; init; } = 0.55;
    public double InjuryProneInjuryFactor { get; init; } = 1.8;

    // --- 目玉（怪物: 複数分野に控えめだが確かな上乗せ） ---
    public double MonsterContactBonus { get; init; } = 4.0;
    public double MonsterPowerBonus { get; init; } = 4.0;
    public double MonsterControlBonus { get; init; } = 4.0;
    public double MonsterStuffBonus { get; init; } = 0.10;

    // --- 生成（設計書10 §3/§6: 才能・性格に応じて0〜数個、目玉は極稀、一部は隠し） ---
    /// <summary>1選手が持つ通常スキルの最大数。</summary>
    public int MaxSkillsPerPlayer { get; init; } = 3;
    /// <summary>通常スキル1種の基礎付与確率（チーム系・体質系）。</summary>
    public double CommonSkillProb { get; init; } = 0.16;
    /// <summary>
    /// 打撃系・投手系スキル1種の付与確率（投手寄せ重み w で振り分ける前。負スキルはこの半分）。issue #907 でプールが
    /// 倍近くに増えたので、1人あたりの保有数が増えすぎないよう通常スキルとは別に持つ。
    /// </summary>
    public double PlaySkillProb { get; init; } = 0.10;
    /// <summary>付与されたスキルが隠しになる割合（気づきで開示される眠った素質）。</summary>
    public double HiddenShare { get; init; } = 0.22;
    /// <summary>目玉スキル（怪物・投法の妙）の極稀な出現確率。</summary>
    public double MarqueeSkillProb { get; init; } = 0.008;
    /// <summary>統率傾向がこの値以上だと精神的支柱を持ちやすくなる閾値と加算確率。</summary>
    public int PillarLeadershipThreshold { get; init; } = 68;
    public double PillarBonusProb { get; init; } = 0.35;
}
