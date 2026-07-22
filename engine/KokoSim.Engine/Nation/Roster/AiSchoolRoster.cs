using System.Collections.Generic;
using System.Linq;

namespace KokoSim.Engine.Nation.Roster;

/// <summary>
/// AI校1校の永続ロスター（設計書 OPEN-QUESTIONS Q19 / #80）。各学年約10人・計約30人を保持し、
/// ベンチ入り20人は <see cref="AiTeamBuilder"/> がTeam構成時に選抜する。ライフサイクル（4月入学・夏後引退・
/// 代替わり再編成）と節目成長は <see cref="AiRosterCycle"/> が駆動する。状態はカレンダー節目の純関数
/// （観測・対戦の有無で変わらない・不変条件#2）。
/// </summary>
public sealed class AiSchoolRoster
{
    private readonly List<AiPlayer> _players;

    /// <summary>この学校のID（生成シード・成績帰属の基準）。</summary>
    public int SchoolId { get; }

    /// <summary>校内で次に採番する選手ID（在籍中不変・新入生に連番で払い出す）。</summary>
    public int NextPlayerId { get; internal set; }

    public AiSchoolRoster(int schoolId, IEnumerable<AiPlayer> players, int nextPlayerId)
    {
        SchoolId = schoolId;
        _players = players.ToList();
        NextPlayerId = nextPlayerId;
    }

    /// <summary>在籍する全選手（学年1〜3。引退者は含まない＝除去済み）。</summary>
    public IReadOnlyList<AiPlayer> Players => _players;

    /// <summary>指定学年の在籍者。</summary>
    public IEnumerable<AiPlayer> InGrade(int grade) => _players.Where(p => p.Grade == grade);

    internal void Add(AiPlayer p) => _players.Add(p);

    internal void RemoveWhere(System.Predicate<AiPlayer> match) => _players.RemoveAll(match);
}
