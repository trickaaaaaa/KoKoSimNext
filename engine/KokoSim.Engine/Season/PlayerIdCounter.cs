using System;
using System.Collections.Generic;
using KokoSim.Engine.Players;
using KokoSim.Engine.Stats;

namespace KokoSim.Engine.Season;

/// <summary>
/// 自校選手の安定ID（成績集計の帰属キー）の採番（issue #535 / #644）。乱数不使用・単調増加。
///
/// <para>成績ストア（<see cref="PlayerStatStore"/>）は「選手ID→成績」で学校に紐づいていない。
/// そのため ID は<b>監督のキャリア全体で一度しか使わない</b>。転任で部員を作り直しても採番を 1 へ戻さない
/// （戻すと、転任先の新入生に旧校の同じIDの選手の成績が出る＝issue #644）。</para>
/// </summary>
public static class PlayerIdCounter
{
    /// <summary>
    /// <paramref name="players"/> へ <paramref name="nextId"/> から昇順で ID を振り、続き番号を返す。
    /// <paramref name="nextId"/> が 1 未満（未採番＝新規ゲーム）なら 1 から振る（0 は「未割当」の番兵）。
    /// </summary>
    public static int AssignSequential(IList<DevelopingPlayer> players, int nextId)
    {
        var id = Math.Max(1, nextId);
        foreach (var p in players) p.Id = id++;
        return id;
    }

    /// <summary>
    /// セーブ復元時の採番の再開値。保存した続き番号・ロスター（在籍＋卒業生）・成績ストアに現れる
    /// ID のうち最大のものの次から振る。成績ストアも見るのは、転任で旧校の部員がロスターから
    /// 消えても成績は残る（＝ロスターの最大Idだけでは小さすぎる）ため。
    /// </summary>
    public static int Resume(int savedNextId, IEnumerable<DevelopingPlayer> roster, PlayerStatStore? stats)
    {
        var maxId = 0;
        foreach (var p in roster) maxId = Math.Max(maxId, p.Id);
        if (stats is not null) maxId = Math.Max(maxId, stats.MaxPlayerId());
        return Math.Max(Math.Max(1, savedNextId), maxId + 1);
    }
}
