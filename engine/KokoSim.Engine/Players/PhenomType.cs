namespace KokoSim.Engine.Players;

/// <summary>
/// 怪物（Phenom）の種別（設計書 OPEN-QUESTIONS Q20 / design-04 §2）。中堅・弱小校にも現れる才能外れ値。
/// 尖り型は「主軸能力S帯＋同系統の支持能力の底上げ」をパッケージで持たせる（単発1能力S化では怪物感が出ない）。
/// 総合型は主要能力を高帯へ＝世代の怪物（design-02 S帯）。<see cref="None"/> が非怪物（通常選手）。
/// 具体的な主軸／支持能力と帯は data/coefficients.yaml の phenom: に外出しする（不変条件#4）。
/// 隠しフラグ＝新聞・大会展望・テストから参照する（Q20 §5）。
/// </summary>
public enum PhenomType
{
    /// <summary>非怪物（通常の生成選手）。</summary>
    None = 0,

    /// <summary>剛腕（尖り型・投手）: 球速S＋キレ・スタミナ底上げ（制球は学校準拠＝ノーコン剛腕の味を残す）。</summary>
    Ace,

    /// <summary>超技巧（尖り型・投手）: 制球S＋キレ底上げ。</summary>
    Finesse,

    /// <summary>スラッガー（尖り型・野手）: パワーS＋ミート底上げ・弾道高め。</summary>
    Slugger,

    /// <summary>韋駄天（尖り型・野手）: 走力S（盗塁・走塁連動）＋ミート底上げ（出塁してこそ怖い）。</summary>
    Speedster,

    /// <summary>鉄砲肩（尖り型・捕手/外野）: 肩S＋守備・捕球底上げ。</summary>
    StrongArm,

    /// <summary>総合型（稀）: ポジション相応の主要能力を高帯へ＝S級（世代の怪物）。</summary>
    AllRound,
}

/// <summary>怪物種別のユーティリティ（尖り型/総合型の判別）。</summary>
public static class PhenomTypes
{
    /// <summary>尖り型（パッケージ）か。</summary>
    public static bool IsSpike(this PhenomType t) => t is PhenomType.Ace or PhenomType.Finesse
        or PhenomType.Slugger or PhenomType.Speedster or PhenomType.StrongArm;

    /// <summary>怪物か（非None）。</summary>
    public static bool IsPhenom(this PhenomType t) => t != PhenomType.None;
}
