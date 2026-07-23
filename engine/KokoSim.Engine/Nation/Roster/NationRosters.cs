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
    // 全国裏試合をバックグラウンドスレッドで回すため（設計書05 §1.4）、辞書構造の競合をロックで防ぐ。
    // 生成済みロスターの Player リストは試合中は不変（成長はカレンダー節目＝メインスレッドのみ）ため、
    // ロックするのは辞書の読み書き（GetOrBootstrap の追加・TryGet の参照）だけでよい。
    private readonly object _gate = new();

    public NationRosters(AiRosterDeps deps) => _deps = deps;

    public AiRosterDeps Deps => _deps;

    /// <summary>登録済み（ブートストラップ済み）学校ID のスナップショット。</summary>
    public IReadOnlyCollection<int> Schools
    {
        get { lock (_gate) return new List<int>(_rosters.Keys); }
    }

    public bool TryGet(int schoolId, out AiSchoolRoster roster)
    {
        lock (_gate) return _rosters.TryGetValue(schoolId, out roster!);
    }

    /// <summary>学校のロスターを取得（未生成なら (校ID, 現在年度) から3学年ブートストラップ）。</summary>
    public AiSchoolRoster GetOrBootstrap(School school, int yearIndex)
    {
        lock (_gate)
        {
            if (!_rosters.TryGetValue(school.Id, out var r))
            {
                r = AiRosterFactory.Bootstrap(school, yearIndex, _deps);
                _rosters[school.Id] = r;
            }
            return r;
        }
    }

    /// <summary>全参加校をあらかじめブートストラップする（ゲーム開始時の一括生成・性能実測対象）。</summary>
    public void BootstrapAll(IEnumerable<School> schools, int yearIndex)
    {
        foreach (var s in schools) GetOrBootstrap(s, yearIndex);
    }

    /// <summary>
    /// 学校の現在ロスターから試合可能な Team を組む（展望・実戦の単一ソース）。<paramref name="aceRest"/> は
    /// エース温存判断（issue #42）の入力（既定 null＝常時エース先発＝展望・従来呼び出しと同じ挙動）。
    /// </summary>
    public Team TeamFor(School school, int yearIndex, ModernRules? modernRules = null, int? calendarYear = null,
        AceRestContext? aceRest = null)
        => AiTeamBuilder.Build(GetOrBootstrap(school, yearIndex), school, yearIndex, _deps, modernRules, calendarYear,
            aceRest);

    /// <summary>
    /// 夏大会後の代替わり: 生成済み全校の3年生を引退させる（秋の新チーム＝下級生のみ）。
    /// カレンダー節目（メインスレッド）でのみ呼ぶ＝背景フルシムと同時実行しない前提。
    /// </summary>
    public void RetireGraduatesAll()
    {
        List<AiSchoolRoster> snapshot;
        lock (_gate) snapshot = new List<AiSchoolRoster>(_rosters.Values);
        foreach (var r in snapshot) AiRosterCycle.RetireGraduates(r);
    }

    /// <summary>年度替わり（4月）: 渡した全校で進級・新入生加入・逆算配分成長を適用する（未生成は生成してから）。</summary>
    public void AdvanceYearAll(IEnumerable<School> schools, int newYearIndex)
    {
        foreach (var s in schools)
        {
            var r = GetOrBootstrap(s, newYearIndex - 1);
            AiRosterCycle.AdvanceOneYear(r, s, newYearIndex, _deps);
        }
    }

    /// <summary>
    /// 年度替わり（4月）: 既にブートストラップ済みの学校だけ進める（全4000校を毎年触らない）。
    /// <paramref name="schoolById"/> は成長ターゲット（進化後Strength）を引くための学校解決。
    /// カレンダー節目（メインスレッド）でのみ呼ぶ。
    /// </summary>
    public void AdvanceExistingYear(System.Func<int, School?> schoolById, int newYearIndex)
    {
        List<int> ids;
        lock (_gate) ids = new List<int>(_rosters.Keys);
        foreach (var id in ids)
        {
            var s = schoolById(id);
            if (s is null) continue;
            AiSchoolRoster r;
            lock (_gate) { if (!_rosters.TryGetValue(id, out r!)) continue; }
            AiRosterCycle.AdvanceOneYear(r, s, newYearIndex, _deps);
        }
    }
}
