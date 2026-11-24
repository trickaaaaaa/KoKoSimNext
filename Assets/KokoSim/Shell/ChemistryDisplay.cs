// ViewModel層（設計書06 §1: エンジン→ViewModel→UXML）。UnityEngine 非依存に保つ。
using System.Linq;
using KokoSim.Engine.Match.Field;
using KokoSim.Engine.Match.Game;
using KokoSim.Engine.Nation;
using KokoSim.Engine.Players;
using KokoSim.Engine.Season;

namespace KokoSim.Unity.Shell
{
    /// <summary>
    /// 結束を画面に出すための値（設計書23 §4・issue #935）。計算は engine の単一ソース
    /// （<see cref="Cohesion.ForTeam(Team, CohesionCoefficients)"/>）を呼ぶだけで、
    /// 画面は値を数字で出さずランクチップ（G〜S）にする（#731）。自校だけ（相手校には出さない＝Q37）。
    /// バッテリーの相性は画面に出さない（説明なしのランクは何か分からない・issue #1590）。
    /// ランクの物差しは能力・チーム力と同じ <see cref="Tiers.FromStrength"/>（結束の効き始め 70以上／30未満が B 以上／G に当たる）。
    /// </summary>
    public static class ChemistryDisplay
    {
        private static readonly CohesionCoefficients CohesionCoeff = new CohesionCoefficients();

        /// <summary>いまの自校の結束 U（試合で使うのと同じチーム＝ベンチ入りの顔ぶれ・主将・合宿などの上下）。</summary>
        public static double CurrentCohesion() => Cohesion.ForTeam(ManagerTeam(), CohesionCoeff);

        /// <summary>この部員を主将にした場合の結束 U（主将指名画面の見込み）。</summary>
        public static double CohesionIfCaptain(DevelopingPlayer candidate)
        {
            var team = ManagerTeam();
            var captain = FindPlayer(team, candidate.Id) ?? RosterTeamBuilder.ToPlayer(candidate, FieldPosition.FirstBase);
            return Cohesion.ForTeam(team, captain, CohesionCoeff);
        }

        /// <summary>結束 U（0〜100）のランク。</summary>
        public static string CohesionGrade(double u) => Tiers.FromStrength(u).ToString();

        private static Team ManagerTeam() => PlayerMatchResolver.BuildManagerTeam(NationService.ManagerSchoolName);

        private static Player FindPlayer(Team team, int id)
            => team.BattingOrder.Concat(team.Reserves).FirstOrDefault(p => p.SourceId == id)
               ?? (team.StartingPitcher != null && team.StartingPitcher.SourceId == id ? team.StartingPitcher : null);
    }
}
