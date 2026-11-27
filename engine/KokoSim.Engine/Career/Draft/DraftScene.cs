using System;
using System.Collections.Generic;
using System.Linq;
using KokoSim.Engine.Nation.Tournaments;
using KokoSim.Engine.Season;

namespace KokoSim.Engine.Career.Draft;

/// <summary>
/// 指名の確定を起こす暦上の瞬間と、年に1回だけの歯止め（issue #1731）。
///
/// <para>指名は10月4週目（<see cref="SeasonCalendar.DraftWeek"/>）に確定する。入口は2つ:
/// ①共有週がその週に入る（通常の週送り・大会の後始末でまとめて進むときも1週ずつ通る）、
/// ②大会モードの日送りで、大会の実月日がその週に入る（大会の間は共有週が止まっているので、
/// ①だけだと大会が終わるまで指名が来ない）。どちらから来ても、同じ年度では1回しか通さない。</para>
///
/// <para>純粋な暦の判定だけで乱数を引かない＝決定論ベースラインに触れない。</para>
/// </summary>
public sealed class DraftNominationGate
{
    private readonly SeasonCalendar _calendar;
    private readonly TournamentSchedule _schedule;

    public DraftNominationGate(SeasonCalendar calendar, TournamentSchedule schedule)
    {
        _calendar = calendar;
        _schedule = schedule;
    }

    /// <summary>指名を確定した最後の年度（1始まり・0＝まだ）。セーブへ載せて、読み込み後の二重確定を防ぐ。</summary>
    public int LastYearIndex { get; set; }

    /// <summary>共有週がドラフトの週に入った。この年度でまだなら true（以後は false）。</summary>
    public bool TryClaimOnWeekEntered(int yearIndex, int week)
        => _calendar.IsDraftWeek(week) && Claim(yearIndex);

    /// <summary>
    /// 大会の日送りで、その日の実月日がドラフトの週に入った。この年度でまだなら true（以後は false）。
    /// 日付の週が一致する日だけを見る＝3月のセンバツなど、後の時期の大会では起きない。
    /// </summary>
    public bool TryClaimOnTournamentDay(int yearIndex, TournamentKind kind, int tournamentDay)
    {
        var (month, day) = TournamentDate.DateOf(_schedule, kind, tournamentDay);
        return _calendar.WeekOf(month, day) == _calendar.DraftWeek && Claim(yearIndex);
    }

    private bool Claim(int yearIndex)
    {
        if (yearIndex <= LastYearIndex) return false;
        LastYearIndex = yearIndex;
        return true;
    }
}

/// <summary>場面の型（指名の結果の組み合わせ）。</summary>
public enum DraftSceneShape
{
    /// <summary>3年生に候補が1人もいない。</summary>
    NoCandidates,
    /// <summary>候補の全員が指名された。</summary>
    AllNominated,
    /// <summary>候補の全員が指名されなかった。</summary>
    AllMissed,
    /// <summary>指名された者と、されなかった者がいる。</summary>
    Mixed,
}

/// <summary>
/// ドラフトの会話1場面ぶんの中身（issue #1731）。<see cref="EventId"/> の場面に <see cref="Values"/> を差し込み、
/// 最終行に <see cref="ResultSentence"/> を出す。
/// </summary>
/// <param name="Shape">場面の型。</param>
/// <param name="EventId">data/events.yaml の場面ID。</param>
/// <param name="LeadName">場面の主役の名前（{player}）。指名された者のうち一番上位、いなければ最初の指名漏れ。候補なしは空。</param>
/// <param name="Values">差し込み語（{drafted}＝指名された者／{missed}＝指名されなかった者）。</param>
/// <param name="ResultSentence">結果の最終行（候補なしは空＝結果行を出さない）。</param>
/// <param name="ResultIsGood">結果行の色（1人でも指名されたら吉報、全員漏れなら凶報）。</param>
public sealed record DraftSceneContent(
    DraftSceneShape Shape,
    string EventId,
    string LeadName,
    IReadOnlyDictionary<string, string> Values,
    string ResultSentence,
    bool ResultIsGood);

/// <summary>
/// 指名の確定結果から、ドラフトの会話を<b>1場面</b>にまとめて組む（issue #1731）。
/// 指名が何人いても場面は1つで、会議の様子から指名の結果まで続け、結果は最終行に全員ぶん並べる。
/// プロ入りOBの祝い金も別の場面にせず、同じ結果の行へ「部費 ＋N 万」で入れる。
/// </summary>
public static class DraftScene
{
    public const string NoCandidatesEventId = "draft_scene_none";
    public const string AllNominatedEventId = "draft_scene_nominated";
    public const string AllMissedEventId = "draft_scene_missed";
    public const string MixedEventId = "draft_scene_mixed";

    /// <summary>名前を並べる区切り。</summary>
    private const string NameSeparator = "、";

    /// <summary>結果の行で人と人を区切る記号。</summary>
    private const string ResultSeparator = "／";

    public static DraftSceneShape ShapeOf(DraftResult result)
    {
        if (result.Picks.Count == 0) return DraftSceneShape.NoCandidates;
        var nominated = result.Picks.Count(p => p.Nominated);
        if (nominated == 0) return DraftSceneShape.AllMissed;
        return nominated == result.Picks.Count ? DraftSceneShape.AllNominated : DraftSceneShape.Mixed;
    }

    public static string EventIdOf(DraftSceneShape shape) => shape switch
    {
        DraftSceneShape.NoCandidates => NoCandidatesEventId,
        DraftSceneShape.AllNominated => AllNominatedEventId,
        DraftSceneShape.AllMissed => AllMissedEventId,
        _ => MixedEventId,
    };

    /// <summary>この場面IDの全部（データ検査・イベント表の存在確認用）。</summary>
    public static IReadOnlyList<string> AllEventIds { get; } = new[]
    {
        NoCandidatesEventId, AllNominatedEventId, AllMissedEventId, MixedEventId,
    };

    /// <param name="result">指名の確定結果。</param>
    /// <param name="donations">指名された者ごとのOB会の祝い金（部費へ入れた額・万円）。指名が無ければ空。</param>
    public static DraftSceneContent Build(
        DraftResult result, IReadOnlyList<(DraftPick Pick, double Amount)> donations)
    {
        var shape = ShapeOf(result);
        var nominated = result.Nominated;   // 順位の若い順
        var missed = result.Picks.Where(p => !p.Nominated).ToList();

        var values = new Dictionary<string, string>
        {
            ["drafted"] = string.Join(NameSeparator, nominated.Select(p => p.Name)),
            ["missed"] = string.Join(NameSeparator, missed.Select(p => p.Name)),
        };
        var lead = nominated.Count > 0 ? nominated[0].Name : missed.Count > 0 ? missed[0].Name : "";

        return new DraftSceneContent(
            shape, EventIdOf(shape), lead, values,
            ResultSentence(nominated, missed, donations), nominated.Count > 0);
    }

    /// <summary>
    /// 結果の最終行。指名された者（順位順）→ 指名されなかった者 → 祝い金の順に「／」でつなぐ。
    /// 例: 「田中 NPB 2位指名！／佐藤 指名なし／部費 ＋70 万」。候補なしは空文字。
    /// </summary>
    public static string ResultSentence(
        IReadOnlyList<DraftPick> nominated, IReadOnlyList<DraftPick> missed,
        IReadOnlyList<(DraftPick Pick, double Amount)> donations)
    {
        var parts = new List<string>();
        foreach (var p in nominated) parts.Add(p.Name + " NPB " + p.Round + "位指名！");
        foreach (var p in missed) parts.Add(p.Name + " 指名なし");

        var total = donations.Sum(d => d.Amount);
        if (total > 0) parts.Add("部費 ＋" + Math.Round(total).ToString("0") + " 万");
        return string.Join(ResultSeparator, parts);
    }
}
