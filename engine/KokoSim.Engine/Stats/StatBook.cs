using System.Collections.Generic;
using KokoSim.Engine.Match.Game;

namespace KokoSim.Engine.Stats;

/// <summary>
/// 選手ID→累積成績の帳簿（1スコープぶん＝通算 or 今大会）。試合結果の<b>片側</b>のラインだけを畳み込む。
/// 学校ごとに1冊持つ（自校＝<see cref="PlayerStatStore"/>／相手校＝<see cref="NationTournamentStats"/>）＝
/// <b>この中の選手IDは校の中でしか一意でない</b>ので、帳簿をまたいで同じIDを引かないこと（issue #634）。
/// SourceId を持たない選手（旧 StrengthTeamFactory の使い捨て生成）はスキップする。純データ・決定論。
/// </summary>
public sealed class StatBook
{
    private readonly Dictionary<int, PlayerStats> _players = new();

    /// <summary>登録済み選手の成績（読み取り専用ビュー）。</summary>
    public IReadOnlyDictionary<int, PlayerStats> Players => _players;

    /// <summary>選手IDの成績を取得（未登録なら null）。UIの成績欄はこれを引く。</summary>
    public PlayerStats? Get(int sourceId) => _players.TryGetValue(sourceId, out var s) ? s : null;

    /// <summary>全消去（今大会スコープの大会切替時に使う）。</summary>
    public void Clear() => _players.Clear();

    /// <summary>
    /// <paramref name="source"/> の中身をこの帳簿へ複写する（元の中身は捨てる）。
    ///
    /// <para>用途は「今大会スコープが次の大会でクリアされる前に、別スコープへ焼き取る」こと
    /// （県予選成績＝<see cref="PlayerStatStore.PrefQualifier"/> /
    /// <see cref="NationTournamentStats.PrefQualifierForSchool"/>）。参照ではなく値を複写するので、
    /// この後 <paramref name="source"/> を Clear しても焼いた側は残る。</para>
    /// </summary>
    public void ReplaceWith(StatBook source)
    {
        _players.Clear();
        foreach (var kv in source._players)
        {
            var dst = For(kv.Key);
            dst.Batting.Merge(kv.Value.Batting);
            dst.Pitching.Merge(kv.Value.Pitching);
            dst.Fielding.Merge(kv.Value.Fielding);
        }
    }

    /// <summary>
    /// <paramref name="source"/> の成績を選手IDごとに<b>足し込む</b>（<see cref="ReplaceWith"/> と違い元の中身を残す）。
    /// 同じIDの選手がいれば累計へ加算する＝呼び出し側は「同じ校の同じ選手」のときだけ使うこと（issue #634）。
    /// </summary>
    public void MergeFrom(StatBook source)
    {
        foreach (var kv in source._players)
        {
            var dst = For(kv.Key);
            dst.Batting.Merge(kv.Value.Batting);
            dst.Pitching.Merge(kv.Value.Pitching);
            dst.Fielding.Merge(kv.Value.Fielding);
        }
    }

    /// <summary>
    /// 選手IDを <paramref name="idMap"/>（旧ID→新ID）で付け替えた複写を返す（元は変えない）。
    /// 対応表に無いID（その学校に今居ない選手）は捨てる＝転任で選手IDを振り直すとき、
    /// 帳簿のキーも一緒に写して「選手の通算は選手に付く」を保つため（issue #1261）。
    /// </summary>
    public StatBook Remapped(IReadOnlyDictionary<int, int> idMap)
    {
        var copy = new StatBook();
        foreach (var kv in _players)
        {
            if (!idMap.TryGetValue(kv.Key, out var newId)) continue;
            var dst = copy.For(newId);
            dst.Batting.Merge(kv.Value.Batting);
            dst.Pitching.Merge(kv.Value.Pitching);
            dst.Fielding.Merge(kv.Value.Fielding);
        }
        return copy;
    }

    /// <summary><paramref name="keep"/> に無い選手IDの成績を捨てる（名簿外の古い成績の掃除。issue #1261 のセーブ移行）。</summary>
    internal void Retain(ISet<int> keep)
    {
        var drop = new List<int>();
        foreach (var id in _players.Keys) if (!keep.Contains(id)) drop.Add(id);
        foreach (var id in drop) _players.Remove(id);
    }

    private PlayerStats For(int id)
    {
        if (!_players.TryGetValue(id, out var s)) { s = new PlayerStats(id); _players[id] = s; }
        return s;
    }

    /// <summary>セーブ復元専用（issue #507・<see cref="Saves.StatsSaves"/> だけが使う）。ID の枠を作って返す。</summary>
    internal PlayerStats GetOrCreate(int id) => For(id);

    /// <summary>1試合ぶんを畳み込む。managerIsAway で自校が先攻/後攻どちらのボックススコアかを選ぶ。</summary>
    public void FoldGame(GameResult r, bool managerIsAway)
    {
        var batting = managerIsAway ? r.AwayBatting : r.HomeBatting;
        var pitching = managerIsAway ? r.AwayPitching : r.HomePitching;
        var fielding = managerIsAway ? r.AwayFielding : r.HomeFielding;

        foreach (var line in batting)
            if (line.SourceId is int id) For(id).Batting.Add(line);

        // 投手成績。登板順の先頭＝先発として GamesStarted を計上。
        for (var i = 0; i < pitching.Count; i++)
        {
            var line = pitching[i];
            if (line.SourceId is not int id) continue;
            For(id).Pitching.Add(line, started: i == 0);
        }

        foreach (var line in fielding)
            if (line.SourceId is int id) For(id).Fielding.Add(line);
    }
}
