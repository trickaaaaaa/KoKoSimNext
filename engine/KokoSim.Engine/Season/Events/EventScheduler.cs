using KokoSim.Engine.Core;

namespace KokoSim.Engine.Season.Events;

/// <summary>
/// 1週の発火イベント。<see cref="Script"/> は発火時に確定した会話（issue #609）＝
/// 提示層は選び直さない（何度開いても同じ文になる）。
/// </summary>
public sealed record FiredEvent(GameEvent Event, int Week, EventScript Script);

/// <summary>
/// イベント発火基盤（設計書03 §5.2 / 04 §3.2）。
/// 各週: 該当する固定イベントを発火 ＋ 重み付き抽選でランダム/選択イベントを発火（上限は係数）。
/// 同一イベントのクールダウン・年次フラグを管理する。効果適用は Phase 5 で接続。
/// </summary>
public sealed class EventScheduler
{
    private readonly IReadOnlyList<GameEvent> _events;
    private readonly EventCoefficients _coeff;
    private readonly Dictionary<string, int> _lastFiredWeek = new();
    private readonly HashSet<string> _annualFlags = new();
    private int _absoluteWeek;

    public EventScheduler(IReadOnlyList<GameEvent> events, EventCoefficients? coeff = null)
    {
        _events = events;
        _coeff = coeff ?? new EventCoefficients();
    }

    /// <summary>年の切り替えで年次フラグをリセット。</summary>
    public void ResetAnnualFlags() => _annualFlags.Clear();

    // ── セーブ往復（issue #609）。載せないとロードでクールダウンが全部明け、
    //    直前に見たイベントが即もう一度出る。 ──

    /// <summary>通算経過週（クールダウンの基準）。</summary>
    public int AbsoluteWeek => _absoluteWeek;

    /// <summary>イベントIDごとの最終発火週（通算週）。</summary>
    public IReadOnlyDictionary<string, int> LastFiredWeeks => _lastFiredWeek;

    /// <summary>立っている年次フラグ。</summary>
    public IReadOnlyCollection<string> AnnualFlags => _annualFlags;

    /// <summary>セーブ復元専用。状態をまるごと差し替える。</summary>
    public void Restore(int absoluteWeek, IReadOnlyDictionary<string, int> lastFiredWeeks,
        IReadOnlyCollection<string> annualFlags)
    {
        _absoluteWeek = absoluteWeek;
        _lastFiredWeek.Clear();
        foreach (var kv in lastFiredWeeks) _lastFiredWeek[kv.Key] = kv.Value;
        _annualFlags.Clear();
        foreach (var f in annualFlags) _annualFlags.Add(f);
    }

    public IReadOnlyList<FiredEvent> Week(int week, IRandomSource rng)
    {
        _absoluteWeek++;
        var fired = new List<FiredEvent>();

        // 固定イベント（カレンダー連動）。抽選を通さず必ず出る。
        foreach (var e in _events)
        {
            if (e.Kind == EventKind.Fixed && e.CalendarWeek == week && CanFire(e))
            {
                Fire(e, week, fired, rng);
            }
        }

        // 重み付き抽選（同一週の上限は係数, 設計書04 §3.2）。
        for (var picked = 0; picked < _coeff.MaxRandomPerWeek; picked++)
        {
            var pool = new List<GameEvent>();
            var totalWeight = 0.0;
            foreach (var e in _events)
            {
                if (e.Kind == EventKind.Fixed) continue;
                if (!CanFire(e)) continue;
                pool.Add(e);
                totalWeight += e.Weight;
            }
            if (pool.Count == 0 || totalWeight <= 0) break;

            // 週あたりの発火確率を抑える（毎週何か起きると煩雑）。
            if (rng.NextDouble() >= _coeff.WeeklyFireProbability) break;

            var roll = rng.NextDouble() * totalWeight;
            foreach (var e in pool)
            {
                roll -= e.Weight;
                if (roll <= 0)
                {
                    Fire(e, week, fired, rng);
                    break;
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

    private void Fire(GameEvent e, int week, List<FiredEvent> fired, IRandomSource rng)
    {
        _lastFiredWeek[e.Id] = _absoluteWeek;
        if (e.SetsAnnualFlag is { } flag) _annualFlags.Add(flag);
        fired.Add(new FiredEvent(e, week, PickScript(e, _absoluteWeek, rng)));
    }

    /// <summary>
    /// 会話バリエーションを1つ選ぶ（issue #609）。<b>親ストリームを進めない Fork</b> で引くので、
    /// バリエーションを増やしても既存の発火抽選の乱数列は 1 語もずれない（＝この機能を足しても
    /// 「いつ何が起きるか」は変わらない）。同じ (イベントID, 通算週) なら常に同じ会話。
    /// </summary>
    public static EventScript PickScript(GameEvent e, int absoluteWeek, IRandomSource rng)
    {
        if (e.Variants.Count == 0) return EventScript.Single(EventRoles.Narration, e.Text);
        if (e.Variants.Count == 1) return e.Variants[0];
        var stream = Fnv1a(e.Id) ^ ((ulong)absoluteWeek * 0x9E3779B97F4A7C15UL);
        return e.Variants[rng.Fork(stream).NextInt(0, e.Variants.Count)];
    }

    private static ulong Fnv1a(string s)
    {
        var hash = 0xcbf29ce484222325UL;
        foreach (var ch in s)
        {
            hash ^= ch;
            hash *= 0x100000001b3UL;
        }
        return hash;
    }
}
