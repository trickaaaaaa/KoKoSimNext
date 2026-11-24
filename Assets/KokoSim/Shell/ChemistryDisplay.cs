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
    /// 画面は値を数字で出さず「チーム状態」の言葉5段にする（issue #1600・#731）。自校だけ（相手校には出さない＝Q37）。
    /// バッテリーの相性は画面に出さない（説明なしのランクは何か分からない・issue #1590）。
    /// 言葉の境目 70／30 は試合の効きの境目（<see cref="CohesionCoefficients.HighThreshold"/>／<see cref="CohesionCoefficients.LowThreshold"/>）から引く。
    /// </summary>
    /// <summary>チーム状態（結束の言葉5段）。良い順。</summary>
    public enum TeamStateLevel { Best, Good, Normal, Bad, Worst }

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

        /// <summary>言葉の「とても良い」と「普通」の区切り（表示用）。効きの境目 70／30 は係数から引く。</summary>
        public const double BestFrom = 85.0;
        public const double NormalFrom = 50.0;

        /// <summary>結束 U（0〜100）の段。境目は 85／70／50／30（70・30 は係数＝試合の効きの境目）。</summary>
        public static TeamStateLevel TeamStateOf(double u, CohesionCoefficients c = null)
        {
            c = c ?? CohesionCoeff;
            if (u >= BestFrom) return TeamStateLevel.Best;
            if (u >= c.HighThreshold) return TeamStateLevel.Good;
            if (u >= NormalFrom) return TeamStateLevel.Normal;
            if (u >= c.LowThreshold) return TeamStateLevel.Bad;
            return TeamStateLevel.Worst;
        }

        /// <summary>段の言葉（チーム状態に出す文字）。</summary>
        public static string TeamStateWord(TeamStateLevel level)
        {
            switch (level)
            {
                case TeamStateLevel.Best: return "めっちゃええ感じ";
                case TeamStateLevel.Good: return "ええ感じ";
                case TeamStateLevel.Normal: return "まあまあ";
                case TeamStateLevel.Bad: return "ちょっとギクシャク";
                default: return "バラバラ";
            }
        }

        /// <summary>結束 U（0〜100）のチーム状態の言葉。</summary>
        public static string TeamStateWord(double u, CohesionCoefficients c = null) => TeamStateWord(TeamStateOf(u, c));

        /// <summary>いまの自校のチーム状態の言葉（共通トップバー用）。</summary>
        public static string CurrentTeamStateWord() => TeamStateWord(CurrentCohesion());

        private static Team ManagerTeam() => PlayerMatchResolver.BuildManagerTeam(NationService.ManagerSchoolName);

        private static Player FindPlayer(Team team, int id)
            => team.BattingOrder.Concat(team.Reserves).FirstOrDefault(p => p.SourceId == id)
               ?? (team.StartingPitcher != null && team.StartingPitcher.SourceId == id ? team.StartingPitcher : null);
    }
}
