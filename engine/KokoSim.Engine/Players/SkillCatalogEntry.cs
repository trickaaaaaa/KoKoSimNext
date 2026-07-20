namespace KokoSim.Engine.Players;

/// <summary>
/// スキルのカタログ定義（設計書10 §5, data/skills.yaml 由来）。
/// 効果量は coefficients.yaml の skills: に集約するので、ここは id・分類・表示情報のみ持つ。
/// UI（選手詳細のスキル欄）と気づきイベントの説明に使う。
/// </summary>
public sealed record SkillCatalogEntry(
    Skill Id,
    SkillCategory Category,
    string Name,
    string Description,
    bool HiddenEligible);
