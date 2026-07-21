using KokoSim.Engine.Match.Game;
using KokoSim.Engine.Match.Tactics;

namespace KokoSim.Engine.Nation;

/// <summary>
/// 学校属性→敵AIプロファイルの対応（設計書11）。三層＝①采配能力 ②ティア(強さ由来) ③校風。
/// フル采配の自校戦にだけ使う。裏試合（集計モデル, §5）はこのブレインを回さず分布を直接サンプルする。
/// </summary>
public static class EnemyAiFactory
{
    /// <summary>学校の三層プロファイル。ティアは強さ由来（Tier enum の並び G=0〜S=7）。</summary>
    public static AiProfile ProfileFor(School school)
        => new(school.TacticalSense, (int)school.Tier, school.Style);

    /// <summary>学校の敵AI采配（共通の采配システムに三層を被せる）。</summary>
    public static AiTacticsBrain BrainFor(
        School school,
        TacticsCoefficients? tactics = null,
        BaserunningCoefficients? baserunning = null,
        EnemyAiCoefficients? aiCoeff = null,
        Players.FormCoefficients? form = null)
        => new(ProfileFor(school), tactics, baserunning, aiCoeff, form);
}
