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

    /// <summary>
    /// 学校の敵AI采配（共通の采配システムに三層を被せる）。校風（<see cref="AiTacticsBrain.ApplyStyle"/>）に
    /// 加えて監督傾向（issue #55, <see cref="ManagerTraitEffects.ApplyTactics"/>）を采配係数へ重ねる。
    /// 傾向なし（既定）は恒等＝従来挙動・帯不変。継投系・抜擢型はここ（采配ブレイン）ではなくチーム編成側で効く。
    /// </summary>
    public static AiTacticsBrain BrainFor(
        School school,
        TacticsCoefficients? tactics = null,
        BaserunningCoefficients? baserunning = null,
        EnemyAiCoefficients? aiCoeff = null,
        Players.FormCoefficients? form = null)
    {
        var ai = aiCoeff ?? new EnemyAiCoefficients();
        var withTraits = ManagerTraitEffects.ApplyTactics(tactics ?? new TacticsCoefficients(), school.ManagerTraits, ai);
        return new AiTacticsBrain(ProfileFor(school), withTraits, baserunning, ai, form);
    }
}

/// <summary>
/// 裏試合フルシムの敵AI采配供給（<see cref="Tournaments.IEnemyBrainFactory"/> の既定実装）。全校の継投・交代を
/// <see cref="EnemyAiFactory.BrainFor"/> で駆動する（設計書11 / #40）。エンジン内で完結＝Shell はこれを差すだけ。
/// </summary>
public sealed class EnemyAiBrainFactory : Tournaments.IEnemyBrainFactory
{
    public Match.Tactics.ITacticsBrain BrainFor(School school) => EnemyAiFactory.BrainFor(school);
}
