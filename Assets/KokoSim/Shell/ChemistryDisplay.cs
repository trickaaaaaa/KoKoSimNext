// ViewModel層（設計書06 §1: エンジン→ViewModel→UXML）。UnityEngine 非依存に保つ。
using System;
using System.Linq;
using KokoSim.Engine.Match.Field;
using KokoSim.Engine.Match.Game;
using KokoSim.Engine.Nation;
using KokoSim.Engine.Players;
using KokoSim.Engine.Season;

namespace KokoSim.Unity.Shell
{
    /// <summary>
    /// 結束とバッテリーの相性を画面に出すための値（設計書23 §4・issue #935）。計算は engine の単一ソース
    /// （<see cref="Cohesion.ForTeam(Team, CohesionCoefficients)"/>／<see cref="BatteryChemistry.For"/>）を呼ぶだけで、
    /// 画面は値を数字で出さずランクチップ（G〜S）とバーにする（#731）。自校だけ（相手校には出さない＝Q37）。
    /// ランクの物差しは能力・チーム力と同じ <see cref="Tiers.FromStrength"/>（結束の効き始め 70以上／30未満が B 以上／G に当たる）。
    /// </summary>
    public static class ChemistryDisplay
    {
        private static readonly CohesionCoefficients CohesionCoeff = new CohesionCoefficients();
        private static readonly BatteryChemistryCoefficients ChemistryCoeff = new BatteryChemistryCoefficients();

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

        /// <summary>
        /// 自校の投手×捕手の相性（ランクと、積み重ねの割合 0〜1）。積み重ねは公式戦・練習試合で一緒に守ったイニング（BatteryLedger）。
        /// 同じ選手どうしは対象外（null）。
        /// </summary>
        public static (string Grade, float Bond)? Battery(DevelopingPlayer pitcher, DevelopingPlayer catcher)
        {
            if (pitcher == null || catcher == null || pitcher.Id == catcher.Id) return null;
            var p = RosterTeamBuilder.ToPlayer(pitcher, FieldPosition.Pitcher);
            var c = RosterTeamBuilder.ToPlayer(catcher, FieldPosition.Catcher);
            var innings = GameSession.Current.BatteryLedger.Innings(pitcher.Id, catcher.Id);
            var k = BatteryChemistry.For(p, c, innings, ChemistryCoeff);
            var bond = BatteryChemistry.Bond(innings, ChemistryCoeff);
            return (Tiers.FromStrength(50.0 + 50.0 * k).ToString(), (float)Math.Max(0.0, Math.Min(1.0, bond)));
        }

        /// <summary>
        /// 試合中の自校の選手どうしの相性（交代パネル）。積み重ねは <see cref="Player.SourceId"/>（＝部員の Id）で台帳を引く。
        /// ID を持たない・同じ選手どうしは null。
        /// </summary>
        public static (string Grade, float Bond)? Battery(Player pitcher, Player catcher)
        {
            if (pitcher == null || catcher == null || ReferenceEquals(pitcher, catcher)) return null;
            if (pitcher.SourceId is not int pid || catcher.SourceId is not int cid || pid == cid) return null;
            var innings = GameSession.Current.BatteryLedger.Innings(pid, cid);
            var k = BatteryChemistry.For(pitcher, catcher, innings, ChemistryCoeff);
            var bond = BatteryChemistry.Bond(innings, ChemistryCoeff);
            return (Tiers.FromStrength(50.0 + 50.0 * k).ToString(), (float)Math.Max(0.0, Math.Min(1.0, bond)));
        }

        private static Team ManagerTeam() => PlayerMatchResolver.BuildManagerTeam(NationService.ManagerSchoolName);

        private static Player FindPlayer(Team team, int id)
            => team.BattingOrder.Concat(team.Reserves).FirstOrDefault(p => p.SourceId == id)
               ?? (team.StartingPitcher != null && team.StartingPitcher.SourceId == id ? team.StartingPitcher : null);
    }
}
