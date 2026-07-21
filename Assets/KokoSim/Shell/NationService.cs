using System.Collections.Generic;
using KokoSim.Engine.Core;
using KokoSim.Engine.Nation;

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
            Strength = TeamOverall.Of(RosterService.Roster),
        };
    }
}
