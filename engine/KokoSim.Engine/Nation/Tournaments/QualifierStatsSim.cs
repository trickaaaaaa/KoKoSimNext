using System;
using System.Collections.Generic;
using KokoSim.Engine.Core;
using KokoSim.Engine.Stats;

namespace KokoSim.Engine.Nation.Tournaments;

/// <summary>
/// 甲子園代表校の「県予選成績」を後から作り直す（設計書05 §1.4b）。
///
/// <para><b>なぜ要るか</b>: 他県の地方大会は集計モデル（<see cref="SummerResolution.ChampionsOnly"/>）で
/// 優勝校だけを決めており、選手個人の成績を1つも積んでいない（全県を1球フルシムすると
/// 試合開始前ロードが背景約5分に戻る＝#208 で削った負荷）。そのままだと甲子園の大会展望で
/// 「選手一人一人の県予選成績」が48校ぶん全部「—」になる。</para>
///
/// <para><b>やること</b>: 代表校が<b>実際に勝ち上がった対戦相手の並び</b>
/// （<see cref="PrefectureResult.Path"/>）だけを1球シムで打ち直し、<b>代表校の側だけ</b>を
/// 県予選スコープへ積む。全カード（約4,000試合）ではなく代表の道のり（49校×約6試合＝約300試合）で済む。</para>
///
/// <para><b>正直さの割り切り</b>: 打ち直した試合は集計モデルが決めた勝敗と食い違うことがある
/// （代表校が負ける結果もあり得る）。他県のブラケットは1画面も表示していないので見た目の矛盾は出ないが、
/// <b>この帳簿から勝敗（勝利数・敗戦投手）を表示してはいけない</b>。展望が出すのは打者＝打率/本塁打/打点、
/// 投手＝防御率/投球回/奪三振 で、どちらも勝敗を含まない。</para>
///
/// <para><b>決定論</b>: 引く乱数は <see cref="StreamId"/> から Fork した専用ストリームだけで、
/// 渡された <c>rng</c> の状態を進めない（Fork は純導出）。よって本編の乱数列・統計帯は1ビットも動かない
/// （不変条件#2）。どの順・何本並列で走らせても結果は同じ。</para>
/// </summary>
public static class QualifierStatsSim
{
    /// <summary>専用ストリームの識別子（'KSQP'＝KoShien Qualifier Pass）。本編と衝突しない値。</summary>
    public const ulong StreamId = 0x4B53_5150UL;

    /// <summary>
    /// 代表校1校ぶんの勝ち上がりを打ち直し、その校の県予選成績を <paramref name="stats"/> へ積む。
    /// 既に成績がある校（自県＝実測がある）を渡さないのは呼び出し側の責任。
    /// </summary>
    /// <param name="result">区画の結果（代表校IDと勝ち上がりの並びを持つ）。</param>
    /// <param name="schoolById">校ID→School。見つからない相手はその1試合だけ飛ばす。</param>
    /// <param name="resolver">1球シムの解決器（<b>成績を自前で畳まない</b>設定で渡すこと）。</param>
    /// <param name="stats">積み先（県予選スコープ）。</param>
    /// <param name="schedule">試合日の算出（エース温存・球数台帳の入力）。</param>
    /// <param name="totalRounds">その区画の総ラウンド数（試合日を引く母数）。</param>
    /// <param name="yearIndex">年度（先攻/後攻の決定論シードに入る）。</param>
    /// <param name="rng">基準乱数。<b>状態は進まない</b>（Fork のみ）。</param>
    /// <param name="afterMatch">1試合解決するたびに呼ぶ任意フック（スロットル用。結果・乱数に触れない）。</param>
    public static void ReplayChampionPath(
        PrefectureResult result,
        Func<int, School?> schoolById,
        IBackgroundMatchResolver resolver,
        NationTournamentStats stats,
        TournamentSchedule schedule,
        int totalRounds,
        int yearIndex,
        IRandomSource rng,
        Action? afterMatch = null)
    {
        if (!result.HasChampion || result.Path.Count == 0) return;
        var champion = schoolById(result.ChampionId);
        if (champion is null) return;

        // 球数台帳は代表校ぶんだけを新しく持つ（本大会の台帳とは別＝甲子園の投手起用に影響させない）。
        var ledger = new TournamentPitchLedger();

        foreach (var step in result.Path)
        {
            var opponent = schoolById(step.OpponentId);
            if (opponent is null) continue;

            // 先攻/後攻は本大会と同じ決定論規則で決める（同じ組み合わせなら常に同じ側）。
            var championIsAway = HomeAwayAssignment.ManagerIsAway(
                champion.Id, opponent.Id, yearIndex, step.RoundsRemaining);
            var away = championIsAway ? champion : opponent;
            var home = championIsAway ? opponent : champion;

            var roundIndex = Math.Max(0, totalRounds - step.RoundsRemaining);
            var cards = Math.Max(1, 1 << Math.Max(0, totalRounds - roundIndex - 1));
            var matchDay = schedule.MatchDay(totalRounds, roundIndex, 0, cards);

            // 試合ごとに独立した Fork（校ID＋ラウンド）＝走らせる順に依存しない。
            var matchRng = rng.Fork(StreamId
                ^ (ulong)(long)champion.Id
                ^ ((ulong)(long)opponent.Id << 20)
                ^ ((ulong)(long)step.RoundsRemaining << 44));

            var game = resolver.Resolve(away, home, matchRng,
                new TournamentMatchContext(step.RoundsRemaining, matchDay, ledger));

            // 代表校の側だけを積む（相手校は「その1試合だけ出てくる」ので県予選成績としては半端になる）。
            stats.FoldPrefQualifier(champion.Id, game, championIsAway);
            afterMatch?.Invoke();
        }
    }

    /// <summary>
    /// 区画の出場校数から総ラウンド数（ブラケットの段数）を数える。試合日を引く母数。
    /// </summary>
    public static int RoundsFor(int entrants)
    {
        var rounds = 0;
        var size = 1;
        while (size < Math.Max(1, entrants)) { size <<= 1; rounds++; }
        return Math.Max(1, rounds);
    }

    /// <summary>
    /// 打ち直す代表校の一覧を作る（既に県予選成績を持つ校＝自県ぶんは除く）。
    /// 並びは区画Id順に整える＝背景の並列消化が順不同でも同じ列になる。
    /// </summary>
    public static IReadOnlyList<PrefectureResult> PendingRepresentatives(
        IEnumerable<PrefectureResult> results, NationTournamentStats stats)
    {
        var pending = new List<PrefectureResult>();
        foreach (var r in results)
        {
            if (!r.HasChampion || r.Path.Count == 0) continue;
            var book = stats.PrefQualifierForSchool(r.ChampionId);
            if (book is not null && book.Players.Count > 0) continue;   // 既に実測がある（自県）
            pending.Add(r);
        }
        pending.Sort((a, b) =>
        {
            var c = a.PrefectureId.CompareTo(b.PrefectureId);
            return c != 0 ? c : string.CompareOrdinal(a.PrefectureName, b.PrefectureName);
        });
        return pending;
    }
}
