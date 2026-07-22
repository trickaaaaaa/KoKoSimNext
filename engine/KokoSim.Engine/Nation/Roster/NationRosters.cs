using System.Collections.Generic;
using KokoSim.Engine.Match.Game;
using KokoSim.Engine.Players;

namespace KokoSim.Engine.Nation.Roster;

/// <summary>
/// 全国の永続ロスター保持庫（設計書 OPEN-QUESTIONS Q19 / #80）。学校ID→<see cref="AiSchoolRoster"/>。
/// 使い捨て生成（<see cref="StrengthTeamFactory.ForSchool"/>）を廃し、全4000校の選手個人を年をまたいで
/// 継続させる。Shell（NationService）が横断状態として1つ保持し、カレンダー節目で成長・代替わりを駆動する。
/// ロスターは (校ID, 入学年度) の純関数で初期化＝観測・対戦の有無に依らない（不変条件#2）。
/// </summary>
public sealed class NationRosters
{
    private readonly Dictionary<int, AiSchoolRoster> _rosters = new();
    private readonly AiRosterDeps _deps;

    public NationRosters(AiRosterDeps deps) => _deps = deps;

    public AiRosterDeps Deps => _deps;

    /// <summary>登録済み学校ID。</summary>
    public IReadOnlyCollection<int> Schools => _rosters.Keys;

    public bool TryGet(int schoolId, out AiSchoolRoster roster) => _rosters.TryGetValue(schoolId, out roster!);

    /// <summary>学校のロスターを取得（未生成なら (校ID, 現在年度) から3学年ブートストラップ）。</summary>
    public AiSchoolRoster GetOrBootstrap(School school, int yearIndex)
    {
        if (!_rosters.TryGetValue(school.Id, out var r))
        {
            r = AiRosterFactory.Bootstrap(school, yearIndex, _deps);
            _rosters[school.Id] = r;
        }
        return r;
    }

    /// <summary>全参加校をあらかじめブートストラップする（ゲーム開始時の一括生成・性能実測対象）。</summary>
    public void BootstrapAll(IEnumerable<School> schools, int yearIndex)
    {
        foreach (var s in schools) GetOrBootstrap(s, yearIndex);
    }

    /// <summary>学校の現在ロスターから試合可能な Team を組む（展望・実戦の単一ソース）。</summary>
    public Team TeamFor(School school, int yearIndex, ModernRules? modernRules = null, int? calendarYear = null)
        => AiTeamBuilder.Build(GetOrBootstrap(school, yearIndex), school, yearIndex, _deps, modernRules, calendarYear);

    /// <summary>夏大会後の代替わり: 全校の3年生を引退させる（秋の新チーム＝下級生のみ）。</summary>
    public void RetireGraduatesAll()
    {
        foreach (var r in _rosters.Values) AiRosterCycle.RetireGraduates(r);
    }

    /// <summary>年度替わり（4月）: 全校で進級・新入生加入・逆算配分成長を適用する。</summary>
    public void AdvanceYearAll(IEnumerable<School> schools, int newYearIndex)
    {
        foreach (var s in schools)
        {
            var r = GetOrBootstrap(s, newYearIndex - 1);
            AiRosterCycle.AdvanceOneYear(r, s, newYearIndex, _deps);
        }
    }
}
