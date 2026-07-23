using KokoSim.Engine.Match.Tactics;
using KokoSim.Engine.Nation;
using KokoSim.Engine.Players;
using KokoSim.Engine.Season;

namespace KokoSim.Engine.Nation.Roster;

/// <summary>
/// 永続ロスターの生成・成長に要る係数と語彙をまとめた依存束（引数肥大の回避）。
/// すべて既定コンストラクタを持つので、テストは <c>new AiRosterDeps()</c> でよい。
/// </summary>
public sealed record AiRosterDeps
{
    /// <summary>永続ロスター係数（学年人数・新入生の質・成長予算・怪物）。</summary>
    public PersistentRosterCoefficients Persistent { get; init; } = new();

    /// <summary>ロスター係数（投打の利き・球質タイプ等。既存生成と共有）。</summary>
    public RosterCoefficients Roster { get; init; } = new();

    /// <summary>性格係数（既存生成と共有）。</summary>
    public PersonalityCoefficients Personality { get; init; } = new();

    /// <summary>選手名語彙（既存生成と共有）。</summary>
    public PlayerNameVocab NameVocab { get; init; } = new();

    /// <summary>チーム総合力係数（逆算配分のターゲット算出・6指標）。</summary>
    public TeamStrengthCoefficients TeamStrength { get; init; } = new();

    /// <summary>調子係数（相手校の試合ごと調子variance の抽選に使う・Q21）。既存生成と共有。</summary>
    public FormCoefficients Form { get; init; } = new();

    /// <summary>敵AI係数（校風・ティア。<see cref="AiTeamBuilder"/> のエース温存判断で使う, issue #42）。</summary>
    public EnemyAiCoefficients EnemyAi { get; init; } = new();
}
