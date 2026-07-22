using System.Collections.Generic;
using KokoSim.Engine.Match.Game;

namespace KokoSim.Engine.Stats;

/// <summary>
/// 全国の大会通算成績（設計書05 §1.4 / #43）。裏試合フルシム化で全4000校の全試合のボックススコアを
/// 学校ごとの <see cref="StatBook"/> へ畳み込み、対戦前に相手校の「今大会成績」を参照できるようにする。
/// キーは (校ID, 校内選手ID)＝校ごとに StatBook を持ち、その中は選手ID（<see cref="Player.SourceId"/>）。
/// 純データ・決定論・UnityEngine 非依存。Shell（横断状態）が保持する。
/// </summary>
public sealed class NationTournamentStats
{
    private readonly Dictionary<int, StatBook> _bySchool = new();
    // 全国裏試合はバックグラウンドスレッドで回し得る（設計書05 §1.4）。自校県（メインスレッド）と
    // 他県（背景スレッド）が同時に別校を畳み込むため、辞書構造の競合をロックで防ぐ（校キーは disjoint）。
    private readonly object _gate = new();

    /// <summary>学校IDの今大会成績（未登録なら null）。UIの相手校成績欄・新聞が引く。</summary>
    public StatBook? ForSchool(int schoolId)
    {
        lock (_gate) return _bySchool.TryGetValue(schoolId, out var b) ? b : null;
    }

    /// <summary>成績を持つ学校ID一覧（新聞・注目選手の走査用・スナップショット）。</summary>
    public IReadOnlyCollection<int> Schools
    {
        get { lock (_gate) return new List<int>(_bySchool.Keys); }
    }

    /// <summary>大会切替時に全消去（今大会スコープ, Q15未決3 の「今大会」相当）。</summary>
    public void StartTournament() { lock (_gate) _bySchool.Clear(); }

    private StatBook BookFor(int schoolId)
    {
        if (!_bySchool.TryGetValue(schoolId, out var b)) { b = new StatBook(); _bySchool[schoolId] = b; }
        return b;
    }

    /// <summary>
    /// 1試合ぶんを両校へ畳み込む。away=先攻校ID・home=後攻校ID。勝敗投手は各サイドで自動判定
    /// （<see cref="DecisionOfRecord"/>）。相手校の生成選手も SourceId（校内ID）を持つので集計に乗る。
    /// </summary>
    public void FoldMatch(int awaySchoolId, int homeSchoolId, GameResult result)
    {
        lock (_gate)
        {
            Fold(awaySchoolId, result, managerIsAway: true);
            Fold(homeSchoolId, result, managerIsAway: false);
        }
    }

    private void Fold(int schoolId, GameResult result, bool managerIsAway)
    {
        var (winPid, losePid) = DecisionOfRecord.Resolve(result, managerIsAway);
        BookFor(schoolId).FoldGame(result, managerIsAway, winPid, losePid);
    }
}
