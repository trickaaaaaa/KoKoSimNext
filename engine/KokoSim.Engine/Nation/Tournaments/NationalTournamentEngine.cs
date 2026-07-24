using System;
using System.Collections.Generic;
using System.Linq;
using KokoSim.Engine.Core;
using KokoSim.Engine.Match.Game;
using KokoSim.Engine.Nation.Roster;
using KokoSim.Engine.Players;
using KokoSim.Engine.Stats;

namespace KokoSim.Engine.Nation.Tournaments;

/// <summary>1県の大会結果（全国ダイジェスト・新聞用）。</summary>
public sealed record PrefectureResult(int PrefectureId, string PrefectureName, string? ChampionName);

/// <summary>
/// 裏試合（自校非関与の県）の解像度（#試合開始前ロード短縮）。
/// <see cref="Full"/> は全カードを1球単位フルシムして全国通算成績へ積む（重い＝観戦しない試合まで詳細に解く）。
/// <see cref="ChampionsOnly"/> は集計モデル（<see cref="AggregateMatch"/>）で勝敗だけ決めて優勝校を得る（軽い）。
/// 甲子園本戦（Phase 3）や全国成績閲覧UIが未実装の現状、他46県は優勝校しか消費されないため既定はこちらで足りる。
/// 個別県の詳細成績が必要になったら、その県だけ <see cref="Full"/> でオンデマンド再シムする。
/// </summary>
public enum SummerResolution
{
    /// <summary>全カード1球フルシム＋全国成績畳み込み（従来挙動）。</summary>
    Full,
    /// <summary>集計モデルで優勝校のみ即決（1球シムなし・成績畳み込みなし）。</summary>
    ChampionsOnly,
}

/// <summary>
/// 再開可能な夏の全国裏試合（#208 スライス化）。<see cref="Total"/> はシムする区画数（進捗バーの母数）、
/// <see cref="Jobs"/> は1区画（＝1県、北海道・東京は分割区画）を丸ごと解決する遅延ジョブの並び。各ジョブは
/// base 乱数から Fork した独立ストリームだけを消費し base 状態を進めない（Fork は純導出）ため、
/// <b>どの順・何本並列で走らせても結果は不変</b>（不変条件#2）。呼び出し側（Shell）はジョブを1本ずつ、あるいは
/// スロットルに応じて複数スレッドで実行し、合間に <c>Thread.Sleep</c> を挟んで確保レートを抑える
/// ＝ Mono の stop-the-world GC を分散させつつ、静止画面では並列消化で待ち時間を縮める。
/// 全ジョブを実行し終えた結果は <see cref="NationalTournamentEngine.RunSummer"/>（一括版）とバイト一致する。
/// </summary>
public sealed record SummerRun(int Total, IReadOnlyList<Func<PrefectureResult>> Jobs);

/// <summary>
/// 全国の裏試合を一括フルシムするオーケストレータ（設計書05 §1.4 / #43・全国47県）。各県の地方大会を
/// 永続ロスターの GameEngine.Play で最後まで解決し、全4000校のボックススコアを全国通算成績へ積む。
/// 自校が対話的に消化する県は <c>excludePrefectureId</c> で除外する（Shell が別 TournamentRunner
/// で回して同一 <see cref="NationTournamentStats"/> へ積む＝重複しない）。県ごとに Fork した独立乱数で決定論。
/// エンジンは Unity 非依存なので、呼び出し側（Shell）はこれをバックグラウンドスレッドで回してよい。
/// </summary>
public static class NationalTournamentEngine
{
    /// <summary>
    /// 一括版（テスト・ヘッドレス CLI 用）。<see cref="BeginSummer"/> のジョブを順に全実行するのと等価
    /// ＝スライス化・並列化の前後で champion・stats digest が完全一致する（#208 決定論回帰の基準）。
    /// </summary>
    public static IReadOnlyList<PrefectureResult> RunSummer(
        Nation nation, NationRosters rosters, GameContext ctx, NationCoefficients coeff,
        TournamentSchedule schedule, int yearIndex, NationTournamentStats stats, IRandomSource rng,
        int? excludePrefectureId = null, ModernRules? modernRules = null, int? calendarYear = null,
        IEnemyBrainFactory? brains = null)
        => BeginSummer(nation, rosters, ctx, coeff, schedule, yearIndex, stats, rng,
            excludePrefectureId, modernRules, calendarYear, brains).Jobs.Select(j => j()).ToList();

    /// <summary>
    /// 夏の全国裏試合を「区画ごとの独立ジョブ列」として構築する（#208）。区画数（進捗の母数）を先に確定させ、
    /// 各ジョブは呼ばれた時に1区画をフルシムして結果を返す（遅延＝Jobs 構築時点では重い処理は走らない）。
    /// 各区画は base <paramref name="rng"/> から Fork した独立ストリームだけを消費し base 状態を進めないため、
    /// どの順・何本並列で実行しても結果は不変＝一括版と完全一致する（不変条件#2）。
    /// </summary>
    /// <param name="afterMatch">
    /// 各裏試合1つを解決するたびに呼ぶ任意フック（#208）。Shell の背景ワーカーが試合の合間でスロットル
    /// （Paused=停止）を反映するために渡す。null＝素通り（テスト・CLI）。結果・乱数には触れない＝決定論不変。
    /// </param>
    public static SummerRun BeginSummer(
        Nation nation, NationRosters rosters, GameContext ctx, NationCoefficients coeff,
        TournamentSchedule schedule, int yearIndex, NationTournamentStats stats, IRandomSource rng,
        int? excludePrefectureId = null, ModernRules? modernRules = null, int? calendarYear = null,
        IEnemyBrainFactory? brains = null, Action? afterMatch = null,
        SummerResolution resolution = SummerResolution.Full)
    {
        var regions = SummerRegions.Build(nation.Prefectures);
        var jobs = regions
            .Where(r => !(excludePrefectureId is int ex && r.PrefectureId == ex))
            .Select(region => (Func<PrefectureResult>)(() => RunPrefecture(
                nation, rosters, ctx, coeff, schedule, yearIndex, stats, rng,
                region, modernRules, calendarYear, brains, afterMatch, resolution)))
            .ToList();
        return new SummerRun(jobs.Count, jobs);
    }

    /// <summary>
    /// 1区画（県 or 分割区画）の地方大会を解決し優勝校を返す。base rng は Fork のみで不変。
    /// <paramref name="resolution"/>＝Full は全カード1球フルシム＋全国成績畳み込み、ChampionsOnly は
    /// 集計モデルで勝敗だけ決める（<see cref="TournamentRunner"/> を backgroundResolver=null で構築＝内蔵 AggregateMatch）。
    /// </summary>
    private static PrefectureResult RunPrefecture(
        Nation nation, NationRosters rosters, GameContext ctx, NationCoefficients coeff,
        TournamentSchedule schedule, int yearIndex, NationTournamentStats stats, IRandomSource rng,
        SummerRegion region, ModernRules? modernRules, int? calendarYear, IEnemyBrainFactory? brains,
        Action? afterMatch = null, SummerResolution resolution = SummerResolution.Full)
    {
        var field = SummerRegions.Entrants(nation, region).ToList();
        if (field.Count < 2)
            return new PrefectureResult(region.PrefectureId, region.Name, field.FirstOrDefault()?.Name);

        // ChampionsOnly は集計モデル（backgroundResolver=null）＝1球シムも成績畳み込みもしない軽量経路。
        IBackgroundMatchResolver? bg = resolution == SummerResolution.ChampionsOnly
            ? null
            : new BackgroundMatchResolver(rosters, ctx, yearIndex, stats, modernRules, calendarYear, brains,
                afterMatch: afterMatch);
        // 北海道・東京の分割区画は forkキーへ分割位置を足して別ストリーム化（無分割の県は従来キーのまま不変）。
        var splitBit = region.Split is { } sp ? (uint)(sp + 1) << 24 : 0u;
        var prefRng = rng.Fork(0xF00D_0000UL ^ (uint)region.PrefectureId ^ splitBit);
        // 自校非関与の区画なので nominal manager（field[0]）に playerResolver は付けない＝全カード裏試合として解く。
        var runner = new TournamentRunner(
            field, field[0], coeff, prefRng, schedule, $"{region.Name}大会",
            playerResolver: null, backgroundResolver: bg);

        while (!runner.Finished) runner.PlayNextPlayerMatch();
        return new PrefectureResult(region.PrefectureId, region.Name, runner.ChampionName);
    }
}
