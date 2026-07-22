namespace KokoSim.Engine.Nation.Roster;

/// <summary>
/// AI校の永続ロスター（設計書 OPEN-QUESTIONS Q19 / #80）の係数。学年人数・新入生の質・年間成長予算・
/// 年3節目の分割比。バランス調整でC#を書き換えないため data/coefficients.yaml に外出しする（不変条件#4）。
/// </summary>
public sealed record PersistentRosterCoefficients
{
    /// <summary>各学年コホートの投手人数。野手は守備8位置に1人ずつ固定＝コホート人数 = 8＋これ。</summary>
    public int PitchersPerCohort { get; init; } = 3;

    /// <summary>
    /// 新入生（1年）の能力中心 = 学校Strength − これ（未熟スタート・3年間の実戦で開花）。
    /// 自校の <c>ProspectGenerator</c>（mental_mean 46 等）と同じ「新入生は未熟」の思想に合わせる。
    /// </summary>
    public double FreshmanGap { get; init; } = 8.0;

    /// <summary>名声由来の入学時上振れ（有名校は良い新入生が集まる）。中心に (Fame−50)×これ を加える。</summary>
    public double FameRecruitWeight { get; init; } = 0.04;

    /// <summary>
    /// 逆算配分のベースライン年間成長素点（在校生1人が1年で伸びる能力ポイント）。
    /// 残差はターゲット（進化後Strength）合わせで補正する（逆算配分）。
    /// </summary>
    public double AnnualGrowth { get; init; } = 6.0;

    /// <summary>3年（引退間際）の成長係数（引退が近いほど伸び代小）。1・2年は 1.0。</summary>
    public double SeniorGrowthFactor { get; init; } = 0.4;

    /// <summary>逆算配分の許容誤差（進化後Strength と 翌夏チーム総合 の差の許容帯・テスト整合）。</summary>
    public double TargetTolerance { get; init; } = 3.0;

    /// <summary>逆算残差補正の1適用あたりの上限（一気に寄せず複数節目でならす・暴走防止）。</summary>
    public double MaxResidualPerNode { get; init; } = 4.0;

    /// <summary>年3節目の分割比（夏大会後 / 秋大会後 / 冬明け）。合計 1.0。</summary>
    public double SummerNodeShare { get; init; } = 0.34;
    public double AutumnNodeShare { get; init; } = 0.33;
    public double WinterNodeShare { get; init; } = 0.33;

    /// <summary>怪物（Phenom）係数。</summary>
    public PhenomCoefficients Phenom { get; init; } = new();
}

/// <summary>
/// 怪物（Phenom, 設計書 OPEN-QUESTIONS Q20 / #82）の係数。出現率・タイプ比・能力帯を外出しする（不変条件#4）。
/// 主軸／支持能力の「どの能力か」の対応は enum 構造なのでコード側（<see cref="PhenomPackages"/>）に置き、
/// 帯・確率・型比だけを係数化する（バランス調整の対象＝これらのみ）。
/// </summary>
public sealed record PhenomCoefficients
{
    /// <summary>
    /// 尖り型が1校の新入生コホートに1人現れる確率（全4000校一様・名声重み無し, Q20 §2）。
    /// 既定 0.00038 ≒ 全国 4000校で期待 1.5人/年（年1〜2人/全国, Q20 §3）。
    /// </summary>
    public double SpikeRatePerSchoolYear { get; init; } = 0.00038;

    /// <summary>総合型が1校の新入生コホートに1人現れる確率。既定 0.00008 ≒ 全国 期待 0.32人/年（数年に1人, Q20 §3）。</summary>
    public double AllRoundRatePerSchoolYear { get; init; } = 0.00008;

    /// <summary>尖り型5種の抽選重み（剛腕/超技巧/スラッガー/韋駄天/鉄砲肩）。合計は任意（正規化する）。</summary>
    public double AceWeight { get; init; } = 1.0;
    public double FinesseWeight { get; init; } = 1.0;
    public double SluggerWeight { get; init; } = 1.0;
    public double SpeedsterWeight { get; init; } = 1.0;
    public double StrongArmWeight { get; init; } = 1.0;

    /// <summary>主軸能力の帯（S帯 90+）。</summary>
    public int MainMin { get; init; } = 90;
    public int MainMax { get; init; } = 99;

    /// <summary>支持能力の底上げ帯（70〜80）。</summary>
    public int SupportMin { get; init; } = 70;
    public int SupportMax { get; init; } = 82;

    /// <summary>総合型の主要能力帯（S級・世代の怪物）。</summary>
    public int AllRoundMin { get; init; } = 82;
    public int AllRoundMax { get; init; } = 95;
}
