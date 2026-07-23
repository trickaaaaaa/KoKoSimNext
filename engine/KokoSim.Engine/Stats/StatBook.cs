using System.Collections.Generic;
using KokoSim.Engine.Match.Game;

namespace KokoSim.Engine.Stats;

/// <summary>
/// 選手ID→累積成績の帳簿（1スコープぶん＝通算 or 今大会）。試合結果の自校側ラインだけを畳み込む。
/// 相手校の生成選手は SourceId=null なのでスキップ（帰属先が無い）。純データ・決定論。
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

    private PlayerStats For(int id)
    {
        if (!_players.TryGetValue(id, out var s)) { s = new PlayerStats(id); _players[id] = s; }
        return s;
    }

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
