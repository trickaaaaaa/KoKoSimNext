using System.Collections.Generic;
using System.Globalization;
using KokoSim.Engine.Match.Field;
using KokoSim.Engine.Match.Game;
using KokoSim.Engine.Players;
using KokoSim.Engine.Stats;

namespace KokoSim.Engine.Match.Timeline.Playback;

/// <summary>能力ビューアの一覧の区分（打線／DH制の現投手／控え）。</summary>
public enum ScoutSection
{
    Lineup,
    Pitcher,
    Reserves,
}

/// <summary>
/// 試合中の能力ビューア（issue #602/#923）の一覧1行。
/// </summary>
/// <param name="Player">選手（試合用の実体）。</param>
/// <param name="Section">区分。</param>
/// <param name="Order">打順（1〜9）。打線以外は 0。</param>
/// <param name="Position">
/// <b>いま試合で就いている位置</b>だけ（DH の枠は <see cref="FieldPosition.DesignatedHitter"/>・DH制の現投手は投手）。
/// 控えは試合に就いていないので null。ゲームが選手に付けた本来の守備位置（DH の打者の <see cref="Player.Position"/>）は
/// ここに入らない（CLAUDE.md UI原則「守備位置・適性はゲームが提示しない」・issue #931）。
/// </param>
/// <param name="IsNow">いま出ている選手（攻撃中＝次打者・守備中＝現投手）。</param>
/// <param name="IsUsed">もう使えない控え（出場済み・登板済み）。</param>
public sealed record ScoutRosterRow(
    Player Player, ScoutSection Section, int Order, FieldPosition? Position, bool IsNow, bool IsUsed);

/// <summary>詳細欄の成績表の1行（指標名・今日・通算）。値は表示用に整形済み（記録が無ければ「—」）。</summary>
public sealed record ScoutStatRow(string Label, string Today, string Career);

/// <summary>能力ビューアの詳細欄（右）の中身。何を出すか／出さないかはここで決まる。</summary>
public sealed class ScoutDetail
{
    public required Player Player { get; init; }

    /// <summary>いま試合で就いている位置（控えは null）。<see cref="ScoutRosterRow.Position"/> の写し。</summary>
    public FieldPosition? Position { get; init; }

    /// <summary>投手能力の見方か（false＝野手能力）。能力の行・成績の指標・持ち球チャートがこれで切り替わる。</summary>
    public bool PitcherView { get; init; }

    /// <summary>特殊能力を出すか。自校の選手だけ（相手校は試合中に開示しない＝2026-11-02 監督決定）。</summary>
    public bool ShowSkills { get; init; }

    /// <summary>持ち球チャートを出すか。投手能力の見方のときだけ（両校とも。役割ではゲートしない・#93）。</summary>
    public bool ShowPitchChart { get; init; }

    /// <summary>成績表の列（今日・通算）。</summary>
    public IReadOnlyList<string> StatScopes => MatchScoutView.StatScopes;

    /// <summary>成績表の行（野手の見方＝打撃、投手の見方＝投球）。</summary>
    public required IReadOnlyList<ScoutStatRow> Stats { get; init; }
}

/// <summary>
/// 試合中の能力ビューア（issue #923・監督決定B案）の中身の組み立て。UI から切り離した純粋なロジックで、
/// 一覧の並び・<b>表示してよい守備位置</b>・詳細欄に何を出すかを決める。
///
/// <para><b>読むだけ</b>＝<see cref="SubstitutionOptions"/> と成績の観測データを並べ替えるだけで、
/// 試合の状態を変えず乱数も引かない（不変条件#2）。守備適性（AptitudeStrip）はここに項目を持たない
/// ＝この画面は守備適性を出してよい判断画面の例外に入っていない（CLAUDE.md・issue #923）。</para>
/// </summary>
public static class MatchScoutView
{
    /// <summary>成績表の列。通算はこの試合の前までの集計（今日の分は今日の列にある）。</summary>
    public static readonly IReadOnlyList<string> StatScopes = new[] { "今日", "通算" };

    private const string None = "—";

    /// <summary>
    /// 打順 <paramref name="slot"/>（0〜8）の選手が<b>いま試合で就いている位置</b>。DH の枠だけは
    /// <see cref="Player.Position"/> に本来の守備位置が残る（守備適性の計算・DH解除の引き継ぎ用。TeamState）ので、
    /// 表示用に DH へ読み替える（<see cref="TeamState.LiveLineup"/> と同じ規則・issue #931）。
    /// </summary>
    public static FieldPosition LineupPosition(SubstitutionOptions opt, int slot)
        => opt.UsesDh && slot == opt.DhSlot ? FieldPosition.DesignatedHitter : opt.Lineup[slot].Position;

    /// <summary>
    /// 一覧の行（打線1〜9 → DH制なら現投手 → 控え（使える→使い切った））。
    /// 非DH制では現投手は打順の中に居るので投手の区分を作らない（同じ選手が2行に出ない）。
    /// </summary>
    public static IReadOnlyList<ScoutRosterRow> Roster(SubstitutionOptions opt)
    {
        var rows = new List<ScoutRosterRow>(opt.Lineup.Count + 1 + opt.Reserves.Count + opt.UsedReserves.Count);
        for (var i = 0; i < opt.Lineup.Count; i++)
        {
            var p = opt.Lineup[i];
            // 守備側の打順は回っていないので、守備中は次打者でなく現投手を点ける。
            var isNow = opt.IsOffense ? i == opt.UpcomingBatterSlot : ReferenceEquals(p, opt.CurrentPitcher);
            rows.Add(new ScoutRosterRow(p, ScoutSection.Lineup, i + 1, LineupPosition(opt, i), isNow, IsUsed: false));
        }

        if (opt.PitcherSlot < 0 && opt.CurrentPitcher is not null)
            rows.Add(new ScoutRosterRow(opt.CurrentPitcher, ScoutSection.Pitcher, 0, FieldPosition.Pitcher,
                IsNow: !opt.IsOffense, IsUsed: false));

        foreach (var p in opt.Reserves)
            rows.Add(new ScoutRosterRow(p, ScoutSection.Reserves, 0, null, IsNow: false, IsUsed: false));
        foreach (var p in opt.UsedReserves)
            rows.Add(new ScoutRosterRow(p, ScoutSection.Reserves, 0, null, IsNow: false, IsUsed: true));
        return rows;
    }

    /// <summary>
    /// 何も当てていないときに詳細欄へ出す行＝1番打者（オーダー画面と同じ既定・issue #923）。
    /// 打線が空なら先頭の行（それも無ければ null）。
    /// </summary>
    public static ScoutRosterRow? DefaultRow(IReadOnlyList<ScoutRosterRow> rows)
    {
        if (rows is null || rows.Count == 0) return null;
        foreach (var r in rows)
            if (r.Section == ScoutSection.Lineup && r.Order == 1) return r;
        return rows[0];
    }

    /// <summary>
    /// 詳細欄の中身。<paramref name="own"/>＝自校の選手か（特殊能力の開示に使う）。
    /// 成績は今日（この試合の観測データ）と通算（この試合より前の集計）を並べる。
    /// </summary>
    public static ScoutDetail Detail(
        ScoutRosterRow row, bool own, bool pitcherView,
        BattingLine? todayBatting, PitchingLine? todayPitching,
        BattingStatLine? careerBatting, PitchingStatLine? careerPitching)
    {
        return new ScoutDetail
        {
            Player = row.Player,
            Position = row.Position,
            PitcherView = pitcherView,
            ShowSkills = own,
            ShowPitchChart = pitcherView,
            Stats = pitcherView
                ? PitchingStats(todayPitching, careerPitching)
                : BattingStats(todayBatting, careerBatting),
        };
    }

    // ── 成績表 ─────────────────────────────────────────────

    /// <summary>
    /// 打撃の指標（打数・安打・本塁打・打点・打率）。今日は打席に立っていなければ全部「—」、
    /// 通算は試合数0なら全部「—」。今日の行は <see cref="BattingStatLine.Add"/> で通算と同じ型に畳み、
    /// 両列を同じ関数で整形する＝書式が列でぶれない。
    /// </summary>
    public static IReadOnlyList<ScoutStatRow> BattingStats(BattingLine? today, BattingStatLine? career)
    {
        BattingStatLine? t = null;
        if (today is { PlateAppearances: > 0 })
        {
            t = new BattingStatLine();
            t.Add(today);
        }
        var c = career is { Games: > 0 } ? career : null;

        return new[]
        {
            Row("打数", t, c, b => N(b.AtBats)),
            Row("安打", t, c, b => N(b.Hits)),
            Row("本塁打", t, c, b => N(b.HomeRuns)),
            Row("打点", t, c, b => N(b.Rbi)),
            Row("打率", t, c, b => b.AtBats > 0 ? Rate3(b.Average) : None),
        };
    }

    /// <summary>
    /// 投球の指標（投球回・球数・奪三振・与四死球・自責点・防御率）。今日は登板していなければ全部「—」、
    /// 通算は登板0なら全部「—」。
    /// </summary>
    public static IReadOnlyList<ScoutStatRow> PitchingStats(PitchingLine? today, PitchingStatLine? career)
    {
        PitchingStatLine? t = null;
        if (today is not null && (today.BattersFaced > 0 || today.Pitches > 0 || today.Outs > 0))
        {
            t = new PitchingStatLine();
            t.Add(today, started: false);
        }
        var c = career is { Games: > 0 } ? career : null;

        return new[]
        {
            Row("投球回", t, c, p => p.InningsText),
            Row("球数", t, c, p => N(p.Pitches)),
            Row("奪三振", t, c, p => N(p.StrikeOuts)),
            Row("与四死球", t, c, p => N(p.Walks + p.HitBatters)),
            Row("自責点", t, c, p => N(p.EarnedRuns)),
            Row("防御率", t, c, p => p.Outs > 0 ? p.Era.ToString("0.00", Ci) : None),
        };
    }

    private static ScoutStatRow Row<T>(string label, T? today, T? career, System.Func<T, string> value)
        where T : class
        => new(label, today is null ? None : value(today), career is null ? None : value(career));

    private static readonly CultureInfo Ci = CultureInfo.InvariantCulture;

    private static string N(int v) => v.ToString(Ci);

    /// <summary>率の3桁（.342・先頭の0を落とす。1.000 はそのまま）。</summary>
    public static string Rate3(double v)
    {
        var t = v.ToString("0.000", Ci);
        return t.StartsWith("0", System.StringComparison.Ordinal) ? t.Substring(1) : t;
    }
}
