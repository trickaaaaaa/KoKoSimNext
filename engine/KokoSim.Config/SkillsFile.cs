using KokoSim.Engine.Players;

namespace KokoSim.Config;

/// <summary>
/// data/skills.yaml（スキルカタログ）のローダ（設計書10 §5）。IOはこの層に隔離し、解釈はエンジンの
/// 純パーサ <see cref="SkillCatalogParser"/> に委譲する（<see cref="SchoolNamesLoader"/> と同型。
/// Balance と Unity が同一コードで解釈する＝決定論保証、issue skills-yaml-not-data-driven）。
/// スキルの追加は YAML 編集＋Skill enum への1行追加で完結する（不変条件#4）。
/// </summary>
public static class SkillsCatalogLoader
{
    public static IReadOnlyList<SkillCatalogEntry> LoadFromFile(string path) => Parse(File.ReadAllText(path));

    public static IReadOnlyList<SkillCatalogEntry> Parse(string yaml) => SkillCatalogParser.Parse(yaml);
}
