using System.Collections.Generic;
using KokoSim.Engine.Core;
using KokoSim.Engine.Nation;
using KokoSim.Engine.Nation.Roster;
using KokoSim.Engine.Stats;

namespace KokoSim.Unity.Shell
{
    /// <summary>
    /// 架空全国（4000校）の単一ソース。従来は HomeState が private static で抱えていたため、
    /// 練習試合の申込先（県内校）を引く画面が同じ母集団を参照できなかった。ここに集約し、
    /// 大会の出場校も練習試合の相手も同じ Nation・同じ School インスタンスを見るようにする。
    /// 生成は決定論（seed=2026 固定・不変条件#2）で初回アクセス時に1回だけ走る。
    /// </summary>
    public static class NationService
    {
        /// <summary>自校ID（生成校と衝突しない専用ID）。</summary>
        public const int ManagerSchoolId = -1;

        /// <summary>本拠県（神奈川。Prefecture.Id は0基点＝JIS番号-1）。</summary>
        public const int PrefectureId = 13;

        public const string ManagerSchoolName = "桜丘";

        private static readonly NationCoefficients Coeff = new NationCoefficients();
        private static Nation _nation;
        private static List<School> _prefecture;

        public static Nation Nation => _nation ?? (_nation = NationGenerator.Generate(
            SchoolNameVocabProvider.Default, Coeff, new Xoshiro256Random(2026)));

        /// <summary>本拠県の全高校（自校は含まない）。順序は生成順で安定。</summary>
        public static IReadOnlyList<School> PrefectureSchools
        {
            get
            {
                if (_prefecture != null) return _prefecture;
                _prefecture = new List<School>();
                foreach (var s in Nation.InPrefecture(PrefectureId))
                {
                    if (s.Id == ManagerSchoolId) continue;
                    _prefecture.Add(s);
                }
                return _prefecture;
            }
        }

        /// <summary>
        /// 自校（School 表現）。強さは共有ロスター由来の総合力（TeamOverall＝6指標の標準化総合）で、
        /// チーム総合力パネルの表示値と同じ尺度になる。
        /// </summary>
        public static School ManagerSchool() => new School
        {
            Id = ManagerSchoolId,
            Name = ManagerSchoolName,
            PrefectureId = PrefectureId,
            Strength = TeamOverall.Of(RosterService.Active),
        };

        // ===== AI校の永続ロスター（#80）＋全国通算成績（#43）=====
        // 使い捨て生成（StrengthTeamFactory.ForSchool）を廃し、全4000校の選手個人を年をまたいで継続させる。
        // 生成はロスター参照時に (校ID, 年度) から遅延ブートストラップ（決定論）。GameContext と同様、係数は既定
        // （Unity側は coefficients.yaml を読まない従来挙動に合わせる。調整はテスト/Balance CLI 経由）。

        private static readonly AiRosterDeps RosterDeps = new AiRosterDeps();
        private static NationRosters _rosters;
        private static NationTournamentStats _tournamentStats;
        private static Dictionary<int, School> _schoolById;

        /// <summary>全国の永続ロスター保持庫（遅延生成の単一ソース）。</summary>
        public static NationRosters Rosters => _rosters ?? (_rosters = new NationRosters(RosterDeps));

        /// <summary>全国の今大会通算成績（裏試合フルシムが積む単一ソース）。</summary>
        public static NationTournamentStats TournamentStats
            => _tournamentStats ?? (_tournamentStats = new NationTournamentStats());

        /// <summary>学校ID→School 解決（成長ターゲット＝進化後Strength を引く）。自校IDは対象外＝null。</summary>
        public static School SchoolById(int id)
        {
            if (_schoolById == null)
            {
                _schoolById = new Dictionary<int, School>();
                foreach (var s in Nation.Schools) _schoolById[s.Id] = s;
            }
            return _schoolById.TryGetValue(id, out var found) ? found : null;
        }

        /// <summary>夏大会後の代替わり: 生成済み全校の3年生を引退させる（秋の新チーム＝下級生のみ）。</summary>
        public static void RetireAiGraduates() => Rosters.RetireGraduatesAll();

        /// <summary>年度替わり（4月）: 生成済み全校で進級・新入生加入・逆算配分成長を適用する。</summary>
        public static void AdvanceAiYear(int newYearIndex) => Rosters.AdvanceExistingYear(SchoolById, newYearIndex);
    }
}
