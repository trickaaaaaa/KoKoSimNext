namespace KokoSim.Engine.Players;

/// <summary>
/// スキルの効果量・出現率（設計書10, YAML駆動）。効果は控えめ（多数の弱スキルの集合体にしない）。
/// スキルなしの選手には一切作用しない＝既存の統計帯を変えない（不変条件#5）。
/// 検証（設計書07）でスキル保有分布と勝率寄与を確認し、壊れ性能を排除する。
/// </summary>
public sealed record SkillCoefficients
{
    // --- 打撃系（試合内・行動特性） ---
    /// <summary>尻上がり(打): 当該試合の打席数1つあたりのミート補正と上限。</summary>
    public double SlowStarterBatContactPerPa { get; init; } = 1.6;
    public double SlowStarterBatMaxBonus { get; init; } = 6.0;
    /// <summary>広角打法: 打球方向σの拡大倍率（シフト無効化）。打力には作用しない。</summary>
    public double SprayBearingFactor { get; init; } = 1.35;
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

    // --- 守備系 ---
    /// <summary>併殺の名手: 併殺成立率の加算（BaserunningModel の DoublePlayProb へ）。</summary>
    public double DoublePlayArtistBonus { get; init; } = 0.06;
    /// <summary>名捕手: 実効リードへの加算[能力点]。リードが球威へ効く経路を底上げする（設計書10, 01 §2①）。</summary>
    public int MasterCatcherLeadBonus { get; init; } = 10;

    // --- チーム系 ---
    /// <summary>精神的支柱: 主将としての統率力（緩和量）の拡大倍率（設計書09 §8）。</summary>
    public double SpiritualPillarCaptainFactor { get; init; } = 1.4;

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
    /// <summary>通常スキル1種の基礎付与確率（カテゴリ適合でさらに増減）。</summary>
    public double CommonSkillProb { get; init; } = 0.16;
    /// <summary>付与されたスキルが隠しになる割合（気づきで開示される眠った素質）。</summary>
    public double HiddenShare { get; init; } = 0.22;
    /// <summary>目玉スキル（怪物・投法の妙）の極稀な出現確率。</summary>
    public double MarqueeSkillProb { get; init; } = 0.008;
    /// <summary>統率傾向がこの値以上だと精神的支柱を持ちやすくなる閾値と加算確率。</summary>
    public int PillarLeadershipThreshold { get; init; } = 68;
    public double PillarBonusProb { get; init; } = 0.35;
}
