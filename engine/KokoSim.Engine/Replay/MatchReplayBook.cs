namespace KokoSim.Engine.Replay;

/// <summary>
/// 注目試合の再生データの台帳（issue #1580）。積んだ順（古い順）。同じ試合（<see cref="MatchReplayRecord.Id"/>）を
/// 二度積むと上書き＝プリフェッチや積み直しで同じカードが何度解かれても1件のまま。
///
/// <para>裏試合は背景スレッドで解くので、書き込み・読み出しは<b>ロックの下</b>で行い、読み出しは複製を返す。</para>
/// </summary>
public sealed class MatchReplayBook
{
    private readonly List<MatchReplayRecord> _records = new();
    private readonly object _gate = new();

    public int Count
    {
        get { lock (_gate) return _records.Count; }
    }

    /// <summary>全件（古い順）の複製。</summary>
    public IReadOnlyList<MatchReplayRecord> Records
    {
        get { lock (_gate) return _records.ToArray(); }
    }

    public void Add(MatchReplayRecord record)
    {
        lock (_gate)
        {
            var i = _records.FindIndex(r => r.Id == record.Id);
            if (i >= 0) _records[i] = record;
            else _records.Add(record);
        }
    }

    public MatchReplayRecord? Find(string id)
    {
        lock (_gate) return _records.Find(r => r.Id == id);
    }

    /// <summary>保持期間（<see cref="MatchReplayPolicy.IsRetained"/>）を過ぎた記録を捨てる。捨てた件数を返す。</summary>
    public int Prune(int currentYear)
    {
        lock (_gate) return _records.RemoveAll(r => !MatchReplayPolicy.IsRetained(r, currentYear));
    }

    /// <summary>
    /// 転任で学校の同定を付け替える（<see cref="Stats.MatchRecordBook.HandOverSchool"/> と同じ「学校に残る記録」）。
    /// 去る学校の試合の自校側（<see cref="Stats.MatchRecordBook.ManagerKey"/>）は全国での校IDへ、
    /// 転任先の学校がかつて戦った試合の側は自校のキーへ戻す。試合の同定（<see cref="MatchReplayRecord.Id"/>）は変えない。
    /// </summary>
    public void HandOverSchool(int departingSchoolId, int arrivingSchoolId)
    {
        const int manager = Stats.MatchRecordBook.ManagerKey;
        int Map(int id) => id == manager ? departingSchoolId : id == arrivingSchoolId ? manager : id;
        lock (_gate)
            for (var i = 0; i < _records.Count; i++)
            {
                var r = _records[i];
                _records[i] = r with { AwaySchoolId = Map(r.AwaySchoolId), HomeSchoolId = Map(r.HomeSchoolId) };
            }
    }

    /// <summary>セーブ復元・新規開始専用。</summary>
    public void Clear()
    {
        lock (_gate) _records.Clear();
    }
}
