using KokoSim.Engine.Core;

namespace KokoSim.Engine.Season.Events;

/// <summary>1週の発火イベント。</summary>
public sealed record FiredEvent(GameEvent Event, int Week);

/// <summary>
/// イベント発火基盤（設計書03 §5.2 / 04 §3.2）。
/// 各週: 該当する固定イベントを発火 ＋ 重み付き抽選で最大1件のランダム/選択イベントを発火。
/// 同一イベントのクールダウン・年次フラグを管理する。効果適用は Phase 5 で接続。
/// </summary>
public sealed class EventScheduler
{
    private readonly IReadOnlyList<GameEvent> _events;
    private readonly Dictionary<string, int> _lastFiredWeek = new();
    private readonly HashSet<string> _annualFlags = new();
    private int _absoluteWeek;

    public EventScheduler(IReadOnlyList<GameEvent> events)
    {
        _events = events;
    }

    /// <summary>年の切り替えで年次フラグをリセット。</summary>
    public void ResetAnnualFlags() => _annualFlags.Clear();

    public IReadOnlyList<FiredEvent> Week(int week, IRandomSource rng)
    {
        _absoluteWeek++;
        var fired = new List<FiredEvent>();

        // 固定イベント（カレンダー連動）。
        foreach (var e in _events)
        {
            if (e.Kind == EventKind.Fixed && e.CalendarWeek == week && CanFire(e))
            {
                Fire(e, week, fired);
            }
        }

        // 重み付き抽選で最大1件（同一週1件, 設計書04 §3.2）。
        var pool = new List<GameEvent>();
        var totalWeight = 0.0;
        foreach (var e in _events)
        {
            if (e.Kind == EventKind.Fixed) continue;
            if (!CanFire(e)) continue;
            pool.Add(e);
            totalWeight += e.Weight;
        }

        if (pool.Count > 0 && totalWeight > 0)
        {
            // 週あたりの発火確率を抑える（毎週何か起きると煩雑）。基準50%。
            if (rng.NextDouble() < 0.5)
            {
                var roll = rng.NextDouble() * totalWeight;
                foreach (var e in pool)
                {
                    roll -= e.Weight;
                    if (roll <= 0)
                    {
                        Fire(e, week, fired);
                        break;
                    }
                }
            }
        }

        return fired;
    }

    private bool CanFire(GameEvent e)
    {
        if (e.RequiresNotAnnualFlag is { } f && _annualFlags.Contains(f)) return false;
        if (_lastFiredWeek.TryGetValue(e.Id, out var last) && _absoluteWeek - last < e.CooldownWeeks) return false;
        return true;
    }

    private void Fire(GameEvent e, int week, List<FiredEvent> fired)
    {
        _lastFiredWeek[e.Id] = _absoluteWeek;
        if (e.SetsAnnualFlag is { } flag) _annualFlags.Add(flag);
        fired.Add(new FiredEvent(e, week));
    }
}
