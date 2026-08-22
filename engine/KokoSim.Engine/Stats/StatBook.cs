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

    private PlayerStats For(int id)
    {
        if (!_players.TryGetValue(id, out var s)) { s = new PlayerStats(id); _players[id] = s; }
        return s;
    }

    /// <summary>セーブ復元専用（issue #507・<see cref="Saves.StatsSaves"/> だけが使う）。ID の枠を作って返す。</summary>
    internal PlayerStats GetOrCreate(int id) => For(id);

    /// <summary>
    /// 1試合ぶんを畳み込む。managerIsAway で自校が先攻/後攻どちらのボックススコアかを選ぶ。
    /// winPid/losePid は勝敗投手の育成選手ID（<see cref="DecisionOfRecord"/> が算出, 該当なしは null）。
    /// </summary>
    public void FoldGame(GameResult r, bool managerIsAway, int? winPid, int? losePid)
    {
        var batting = managerIsAway ? r.AwayBatting : r.HomeBatting;
        var pitching = managerIsAway ? r.AwayPitching : r.HomePitching;
        var fielding = managerIsAway ? r.AwayFielding : r.HomeFielding;

        foreach (var line in batting)
            if (line.SourceId is int id) For(id).Batting.Add(line);

        // 投手成績。登板順の先頭＝先発として GamesStarted を計上。勝敗は決定投手IDと一致するときのみ。
        for (var i = 0; i < pitching.Count; i++)
        {
            var line = pitching[i];
            if (line.SourceId is not int id) continue;
            For(id).Pitching.Add(line, started: i == 0, win: winPid == id, loss: losePid == id);
        }

        foreach (var line in fielding)
            if (line.SourceId is int id) For(id).Fielding.Add(line);
    }
}
