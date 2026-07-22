namespace KokoSim.Engine.Nation.Tournaments;

/// <summary>
/// 大会中の投手を跨いで同定する安定キー（issue #41）。
/// 自校投手は育成選手ID（<see cref="BoxScore"/> の SourceId）で一意。
/// 相手校の生成投手は SourceId=null だが、校ID＋年度の決定論生成なので背番号が安定キーになる。
/// </summary>
/// <remarks>BoxScore は Match.Game 名前空間。ここでは値の同定だけを担い、集計は <see cref="TournamentPitchLedger"/> が行う。</remarks>
public readonly record struct PitcherLedgerKey
{
    private readonly bool _bySource;
    private readonly int _a;
    private readonly int _b;

    private PitcherLedgerKey(bool bySource, int a, int b)
    {
        _bySource = bySource;
        _a = a;
        _b = b;
    }

    /// <summary>自校投手（育成選手ID＝SourceId）。</summary>
    public static PitcherLedgerKey ForPlayer(int sourceId) => new(true, sourceId, 0);

    /// <summary>相手校の生成投手（校ID＋背番号）。</summary>
    public static PitcherLedgerKey ForOpponent(int schoolId, int uniformNumber) => new(false, schoolId, uniformNumber);
}

/// <summary>
/// 大会中の投手球数台帳（issue #41・設計書05 §1.3）。
/// 自校戦の詳細シム結果から (投手キー, 球数, 試合日) を記録し、直近ウィンドウの累計球数と休養日数を答える。
/// 大会終了で <see cref="Reset"/>。決定論入力（球数・試合日）だけで構成＝同シード同結果。
/// 本流（試合開始時の priorWeekPitches 配線）は別issue（帯再校正とセット）。ここは記録/集計/リセットのみ。
/// </summary>
public sealed class TournamentPitchLedger
{
    // 投手キー → その大会での登板記録（試合日昇順で追記）。
    private readonly Dictionary<PitcherLedgerKey, List<Outing>> _outings = new();

    private readonly record struct Outing(int MatchDay, int Pitches);

    /// <summary>登板を記録（同一投手・同一試合日の複数記録は合算＝継投を跨いだ再登板等も球数を足す）。</summary>
    public void Record(PitcherLedgerKey key, int pitches, int matchDay)
    {
        if (pitches <= 0) return;
        if (!_outings.TryGetValue(key, out var list))
        {
            list = new List<Outing>();
            _outings[key] = list;
        }

        // 同じ試合日の既存記録があれば合算（新規登板は末尾に追加＝試合日は単調非減少で呼ばれる想定）。
        for (var i = 0; i < list.Count; i++)
        {
            if (list[i].MatchDay == matchDay)
            {
                list[i] = new Outing(matchDay, list[i].Pitches + pitches);
                return;
            }
        }
        list.Add(new Outing(matchDay, pitches));
    }

    /// <summary>その投手が currentDay の直前 windowDays 日間（currentDay 当日を含まない）に投げた累計球数。</summary>
    public int PitchesWithin(PitcherLedgerKey key, int currentDay, int windowDays)
    {
        if (!_outings.TryGetValue(key, out var list)) return 0;
        var since = currentDay - windowDays;
        var sum = 0;
        foreach (var o in list)
            if (o.MatchDay >= since && o.MatchDay < currentDay) sum += o.Pitches;
        return sum;
    }

    /// <summary>その投手の直近登板日（currentDay 当日を含まない）。未登板なら null。</summary>
    public int? LastOutingDay(PitcherLedgerKey key, int currentDay)
    {
        if (!_outings.TryGetValue(key, out var list)) return null;
        int? last = null;
        foreach (var o in list)
            if (o.MatchDay < currentDay && (last is null || o.MatchDay > last)) last = o.MatchDay;
        return last;
    }

    /// <summary>前登板からの休養日数（currentDay − 直近登板日）。未登板なら null（＝フレッシュ）。</summary>
    public int? RestDays(PitcherLedgerKey key, int currentDay)
    {
        var last = LastOutingDay(key, currentDay);
        return last is null ? null : currentDay - last.Value;
    }

    /// <summary>大会終了時のリセット（全投手の記録を破棄）。</summary>
    public void Reset() => _outings.Clear();
}
