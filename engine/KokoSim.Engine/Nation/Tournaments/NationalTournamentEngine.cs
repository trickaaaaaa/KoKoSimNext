using System;
using System.Collections.Generic;
using System.Linq;
using KokoSim.Engine.Core;
using KokoSim.Engine.Match.Game;
using KokoSim.Engine.Nation.Roster;
using KokoSim.Engine.Players;
using KokoSim.Engine.Stats;

namespace KokoSim.Engine.Nation.Tournaments;

/// <summary>
/// 代表校が勝ち上がる途中で戦った1カード（設計書05 §1.4b）。<see cref="OpponentId"/> は倒した相手の校ID、
/// <see cref="RoundsRemaining"/> は決勝から数えたラウンド位置（1＝決勝）。
///
/// <para>用途は「代表校の県予選成績だけを後から1球シムで作り直す」こと。集計モデル（ChampionsOnly）で
/// 回した県は選手個人の成績が1つも積まれないので、甲子園の大会展望で県予選成績を出すために
/// <b>代表校が実際に勝ち上がった対戦相手の並び</b>だけを残しておく。</para>
/// </summary>
public readonly record struct ChampionMatchup(int OpponentId, int RoundsRemaining);

/// <summary>
/// 1県の大会結果（全国ダイジェスト・新聞用）。<see cref="WinsBySchool"/> は区画内の校別勝数
/// （issue wire-ai-school-evolve-into-game: 年次進化 <see cref="AiSchoolModel.Evolve"/> の材料。
/// ChampionsOnly でも集計モデルの勝敗はブラケットに残るので数えられる）。
///
/// <para><see cref="ChampionId"/> は甲子園（49代表）の入力。<b>校名だけでは学校を同定できない</b>
/// （同名校を作らない保証が無く、成績の帰属キーも校ID）ので ID で持つ。区画に2校未満で
/// 大会が成立しなかったときだけ 0 未満（<see cref="NoChampion"/>）。</para>
/// </summary>
public sealed record PrefectureResult(
    int PrefectureId, string PrefectureName, string? ChampionName,
    IReadOnlyDictionary<int, int>? WinsBySchool = null,
    int ChampionId = PrefectureResult.NoChampion,
    IReadOnlyList<ChampionMatchup>? ChampionPath = null,
    int EntrantCount = 0)
{
    /// <summary>代表校なし（区画に2校未満）。</summary>
    public const int NoChampion = int.MinValue;

    private static readonly IReadOnlyDictionary<int, int> Empty = new Dictionary<int, int>();
    private static readonly IReadOnlyList<ChampionMatchup> NoPath = new List<ChampionMatchup>();

    /// <summary>校別勝数（未集計なら空）。</summary>
    public IReadOnlyDictionary<int, int> Wins => WinsBySchool ?? Empty;

    /// <summary>代表校の勝ち上がり（決勝が先頭ではなく、ブラケットに積まれた順＝初戦から）。</summary>
    public IReadOnlyList<ChampionMatchup> Path => ChampionPath ?? NoPath;

    /// <summary>その区画の出場校数（<see cref="QualifierStatsSim.RoundsFor"/> の入力＝試合日を引く母数）。</summary>
    public int Entrants => EntrantCount;

    /// <summary>代表校が決まったか。</summary>
    public bool HasChampion => ChampionId != NoChampion;
}

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
/// で回して同一 <see cref="NationTournamentStats"/> へ積む＝重複しない）。分割県（北海道/東京）は
/// <c>excludeSplit</c> を添えて片方の区画だけを除外する＝もう片方は通常どおり背景で消化され独自の代表を出す
/// （null のときは両区画とも一致扱い＝非分割県の1区画のみを除外する従来どおりの挙動）。県ごとに Fork した独立乱数で決定論。
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
        int? excludePrefectureId = null, int? excludeSplit = null, ModernRules? modernRules = null,
        int? calendarYear = null, IEnemyBrainFactory? brains = null,
        SummerResolution resolution = SummerResolution.Full, bool useRegionalStadiumTier = false)
        => BeginSummer(nation, rosters, ctx, coeff, schedule, yearIndex, stats, rng,
            excludePrefectureId, excludeSplit, modernRules, calendarYear, brains, resolution: resolution,
            useRegionalStadiumTier: useRegionalStadiumTier)
            .Jobs.Select(j => j()).ToList();

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
    /// <param name="useRegionalStadiumTier">
    /// 1球フルシムの裏試合で県大会の汎用ティア球場を使うか（issue #636。<see cref="BackgroundMatchResolver"/> へ渡す）。
    /// 既定 false＝従来どおり（統計シム・テスト）。集計モデル（ChampionsOnly）は球場を使わないので効かない。
    /// </param>
    public static SummerRun BeginSummer(
        Nation nation, NationRosters rosters, GameContext ctx, NationCoefficients coeff,
        TournamentSchedule schedule, int yearIndex, NationTournamentStats stats, IRandomSource rng,
        int? excludePrefectureId = null, int? excludeSplit = null, ModernRules? modernRules = null,
        int? calendarYear = null, IEnemyBrainFactory? brains = null, Action? afterMatch = null,
        SummerResolution resolution = SummerResolution.Full, bool useRegionalStadiumTier = false)
    {
        var regions = SummerRegions.Build(nation.Prefectures);
        // excludeSplit=null は「区画を問わず県ごと除外」（従来どおり・分割県も両区画とも除外）。
        // 値ありは「その区画だけ除外」（分割県のもう片方は残す＝issue 北海道・東京分割）。
        var jobs = regions
            .Where(r => !(excludePrefectureId is int ex && r.PrefectureId == ex
                          && (excludeSplit is null || r.Split == excludeSplit)))
            .Select(region => (Func<PrefectureResult>)(() => RunPrefecture(
                nation, rosters, ctx, coeff, schedule, yearIndex, stats, rng,
                region, modernRules, calendarYear, brains, afterMatch, resolution, useRegionalStadiumTier)))
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
        Action? afterMatch = null, SummerResolution resolution = SummerResolution.Full,
        bool useRegionalStadiumTier = false)
    {
        var field = SummerRegions.Entrants(nation, region).ToList();
        if (field.Count < 2)
        {
            // 1校しか居ない区画は無条件で代表（試合が無いので勝ち上がりも空）。
            var sole = field.FirstOrDefault();
            return new PrefectureResult(
                region.PrefectureId, region.Name, sole?.Name,
                ChampionId: sole?.Id ?? PrefectureResult.NoChampion, EntrantCount: field.Count);
        }

        // ChampionsOnly は集計モデル（backgroundResolver=null）＝1球シムも成績畳み込みもしない軽量経路。
        IBackgroundMatchResolver? bg = resolution == SummerResolution.ChampionsOnly
            ? null
            : new BackgroundMatchResolver(rosters, ctx, yearIndex, stats, modernRules, calendarYear, brains,
                afterMatch: afterMatch, useRegionalStadiumTier: useRegionalStadiumTier);
        // 北海道・東京の分割区画は forkキーへ分割位置を足して別ストリーム化（無分割の県は従来キーのまま不変）。
        var splitBit = region.Split is { } sp ? (uint)(sp + 1) << 24 : 0u;
        var prefRng = rng.Fork(0xF00D_0000UL ^ (uint)region.PrefectureId ^ splitBit);
        // 自校非関与の区画なので nominal manager（field[0]）に playerResolver は付けない＝全カード裏試合として解く。
        var runner = new TournamentRunner(
            field, field[0], coeff, prefRng, schedule, $"{region.Name}大会",
            playerResolver: null, backgroundResolver: bg);

        while (!runner.Finished) runner.PlayNextPlayerMatch();
        // issue #529: 敗退直後は残りブラケットが未消化（TournamentRunner.StepBracketResolution へ
        // スライス駆動化された）。この県は自校非関与＝画面遷移を待たせる懸念がなく、この呼び出し自体が
        // 既に NationBackgroundSim のジョブ単位（1県丸ごと）で背景スレッド上を走っているので、ここで
        // 同期に最後まで消化して優勝校を確定してよい。
        runner.DrainBracketResolution();
        var matches = runner.BuildBracketView().Matches;
        var championId = ChampionIdOf(matches);
        return new PrefectureResult(region.PrefectureId, region.Name, runner.ChampionName,
            WinsBySchoolOf(matches), championId, ChampionPathOf(matches, championId), field.Count);
    }

    /// <summary>ブラケット全カードから校別勝数を数える（AI校の年次進化の材料）。乱数不使用。</summary>
    public static IReadOnlyDictionary<int, int> WinsBySchoolOf(IReadOnlyList<BracketMatch> matches)
    {
        var wins = new Dictionary<int, int>();
        foreach (var m in matches) wins[m.WinnerId] = (wins.TryGetValue(m.WinnerId, out var v) ? v : 0) + 1;
        return wins;
    }

    /// <summary>
    /// 優勝校の校ID（決勝＝残りラウンド1 の勝者）。決勝が未消化なら <see cref="PrefectureResult.NoChampion"/>。
    /// 乱数不使用。
    /// </summary>
    public static int ChampionIdOf(IReadOnlyList<BracketMatch> matches)
    {
        foreach (var m in matches)
            if (m.RoundsRemaining == 1) return m.WinnerId;
        return PrefectureResult.NoChampion;
    }

    /// <summary>
    /// 優勝校が勝ち上がる途中で戦った全カード（ブラケットに積まれた順＝初戦から決勝へ）。乱数不使用。
    ///
    /// <para>不戦勝（Bye）はカードとしてブラケットに積まれないので自然に落ちる＝
    /// 「実際に試合をした相手」だけが並ぶ（県予選成績の再シムはこの並びをそのまま打ち直す）。</para>
    /// </summary>
    public static IReadOnlyList<ChampionMatchup> ChampionPathOf(IReadOnlyList<BracketMatch> matches, int championId)
    {
        var path = new List<ChampionMatchup>();
        if (championId == PrefectureResult.NoChampion) return path;
        foreach (var m in matches)
            if (m.WinnerId == championId) path.Add(new ChampionMatchup(m.LoserId, m.RoundsRemaining));
        return path;
    }
}
