using System.Collections.Generic;
using System.Linq;
using KokoSim.Engine.Match.Game;

namespace KokoSim.Engine.Season;

/// <summary>投手ID×捕手IDの組の、一緒に守ったイニング数（重み付き。設計書23 §2.3）。</summary>
public readonly record struct BatteryInningsEntry(int PitcherId, int CatcherId, double Innings);

/// <summary>
/// 自校のバッテリーの積み重ねの記録（設計書23 §2.3・issue #933）。投手ID×捕手IDの組ごとに一緒に守ったイニング数を持つ
/// （公式戦1・練習試合／紅白戦0.5の重み＝Q38）。減らない。どちらかが卒業・転校したら消える（<see cref="Prune"/>）。
/// セーブに載る（v28）。AI校は持たない（同じ学校の年数で近似＝<see cref="BatteryChemistry.YearsTogether"/>）。
/// 乱数不使用。
/// </summary>
public sealed class BatteryLedger
{
    private readonly Dictionary<(int PitcherId, int CatcherId), double> _innings = new();

    /// <summary>組の数。</summary>
    public int Count => _innings.Count;

    /// <summary>その組のイニング数（無ければ0）。</summary>
    public double Innings(int pitcherId, int catcherId)
        => _innings.TryGetValue((pitcherId, catcherId), out var v) ? v : 0.0;

    /// <summary>イニングを足す（0以下は無視）。</summary>
    public void Add(int pitcherId, int catcherId, double innings)
    {
        if (innings <= 0) return;
        _innings[(pitcherId, catcherId)] = Innings(pitcherId, catcherId) + innings;
    }

    /// <summary>
    /// 自校の1試合ぶんを積む。<paramref name="managerWasAway"/> で自校側の記録を選び、アウト数/3 に
    /// 公式戦なら <see cref="BatteryChemistryCoefficients.OfficialInningWeight"/>、練習試合なら
    /// <see cref="BatteryChemistryCoefficients.PracticeInningWeight"/> を掛ける。
    /// </summary>
    public void AddGame(GameResult result, bool managerWasAway, bool isOfficial, BatteryChemistryCoefficients c)
    {
        var weight = isOfficial ? c.OfficialInningWeight : c.PracticeInningWeight;
        foreach (var line in managerWasAway ? result.AwayBatteryOuts : result.HomeBatteryOuts)
            Add(line.PitcherId, line.CatcherId, line.Outs / 3.0 * weight);
    }

    /// <summary>在籍していない選手（卒業・転校）を含む組を消す。</summary>
    public void Prune(IEnumerable<int> enrolledIds)
    {
        var alive = new HashSet<int>(enrolledIds);
        foreach (var key in _innings.Keys.Where(k => !alive.Contains(k.PitcherId) || !alive.Contains(k.CatcherId)).ToList())
            _innings.Remove(key);
    }

    /// <summary>全部消す（新チーム・転任など、部員が入れ替わるとき）。</summary>
    public void Clear() => _innings.Clear();

    /// <summary>試合へ渡す読み取り専用の写し（<see cref="Team.BatteryInnings"/>）。</summary>
    public IReadOnlyDictionary<(int PitcherId, int CatcherId), double> Snapshot()
        => new Dictionary<(int PitcherId, int CatcherId), double>(_innings);

    /// <summary>保存用の並び（投手ID→捕手IDの昇順＝決定論）。</summary>
    public IReadOnlyList<BatteryInningsEntry> Entries()
        => _innings.OrderBy(kv => kv.Key.PitcherId).ThenBy(kv => kv.Key.CatcherId)
            .Select(kv => new BatteryInningsEntry(kv.Key.PitcherId, kv.Key.CatcherId, kv.Value)).ToList();

    /// <summary>保存から復元する。</summary>
    public static BatteryLedger From(IEnumerable<BatteryInningsEntry> entries)
    {
        var l = new BatteryLedger();
        foreach (var e in entries) l.Add(e.PitcherId, e.CatcherId, e.Innings);
        return l;
    }
}
