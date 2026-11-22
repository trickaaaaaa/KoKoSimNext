using System.Collections.Generic;
using KokoSim.Engine.Match.AtBat;
using KokoSim.Engine.Match.Field;
using KokoSim.Engine.Match.Game;
using KokoSim.Engine.Season;

namespace KokoSim.Engine.Stats;

/// <summary>打撃成績の1行（試合結果画面が描く項目だけ。<see cref="BattingLine"/> から四死球を合算して持つ）。</summary>
public sealed record MatchRecordBatter(
    int Order, FieldPosition Position, BoxEntry Entry, FieldPosition? FieldedAt, string Name,
    int AtBats, int Hits, int Rbi, int HomeRuns, int Triples, int Doubles,
    int WalksHbp, int StrikeOuts, int StolenBases);

/// <summary>投手成績の1行（登板順）。投球回はアウト数で持つ。</summary>
public sealed record MatchRecordPitcher(
    string Name, int Outs, int Pitches, int Hits, int StrikeOuts, int WalksHbp, int Runs)
{
    public string InningsText => InningsFormat.Text(Outs);
}

/// <summary>打席の札1枚。併殺は <see cref="BoxScoreGridCell.OutsThisPa"/> が2以上のもの。</summary>
public readonly record struct MatchRecordPa(
    int PaIndex, PlateAppearanceResult Result, bool SacFly, bool SacBunt, bool DoublePlay);

/// <summary>打席の札の1行（打順枠×打者）。<see cref="MatchRecordBatter"/> と (Order, Name) で対応する。</summary>
public sealed record MatchRecordPaRow(int Order, string BatterName, IReadOnlyList<MatchRecordPa> Cells);

/// <summary>片チームぶん（イニングスコア・打撃成績・投手成績・打席の札）。</summary>
public sealed record MatchRecordSide(
    int Runs, int Hits, int Errors, IReadOnlyList<int> LineScore,
    IReadOnlyList<MatchRecordBatter> Batting, IReadOnlyList<MatchRecordPitcher> Pitching,
    IReadOnlyList<MatchRecordPaRow> PaRows);

/// <summary>
/// 自校の公式戦1試合ぶんの記録（issue #1413）。試合結果画面が描くのに要るものだけを持ち、
/// 重い <see cref="GameResult"/>（打席ログ・1球単位の観測）はセーブに積まない。
///
/// <para>自校の公式戦は詳細シムで解決されるので通常は <see cref="Away"/>／<see cref="Home"/> が入る。
/// 詳細が無い試合（抽象シム）はスコアだけの行＝両方 null で、一覧には出るが開けない。</para>
///
/// <para><see cref="SchoolId"/> は<b>この試合を戦った自校</b>のキー。現在の自校は <see cref="MatchRecordBook.ManagerKey"/>、
/// 転任で去った学校は全国での校IDへ付け替える（成績・配色と同じ「学校に残る記録」・issue #1261）。</para>
/// </summary>
public sealed record MatchRecord
{
    public int SchoolId { get; init; } = MatchRecordBook.ManagerKey;
    /// <summary>年度（YearIndex・1始まり）。暦年への変換は Unity 側の担当（不変条件#3）。</summary>
    public int Year { get; init; }
    public TournamentKind Kind { get; init; }
    /// <summary>試合日の月日。0＝不明。</summary>
    public int Month { get; init; }
    public int Day { get; init; }
    public string RoundName { get; init; } = "";
    public int OpponentId { get; init; } = -1;
    /// <summary>自校の表記（幹）。その試合当時の名前を焼く。</summary>
    public string OwnName { get; init; } = "";
    public string OpponentName { get; init; } = "";
    public bool ManagerIsAway { get; init; }
    public int OwnRuns { get; init; }
    public int OpponentRuns { get; init; }
    public bool MercyEnded { get; init; }
    public MatchRecordSide? Away { get; init; }
    public MatchRecordSide? Home { get; init; }

    public bool HasDetail => Away is not null && Home is not null;
    public bool Won => OwnRuns > OpponentRuns;
    public bool Lost => OwnRuns < OpponentRuns;
    public string AwayName => ManagerIsAway ? OwnName : OpponentName;
    public string HomeName => ManagerIsAway ? OpponentName : OwnName;
}

/// <summary><see cref="GameResult"/> → <see cref="MatchRecord"/>。乱数を使わない純関数（試合の結果・帯に影響しない）。</summary>
public static class MatchRecordFactory
{
    /// <summary>
    /// 試合の中身を <paramref name="meta"/>（年度・大会・相手・先攻後攻など）へ詰める。スコアは <paramref name="r"/> から取る。
    /// </summary>
    public static MatchRecord From(GameResult r, MatchRecord meta)
    {
        var away = Side(r, isAway: true);
        var home = Side(r, isAway: false);
        return meta with
        {
            OwnRuns = meta.ManagerIsAway ? r.AwayRuns : r.HomeRuns,
            OpponentRuns = meta.ManagerIsAway ? r.HomeRuns : r.AwayRuns,
            MercyEnded = r.MercyEnded,
            Away = away,
            Home = home,
        };
    }

    private static MatchRecordSide Side(GameResult r, bool isAway)
    {
        var batting = new List<MatchRecordBatter>();
        foreach (var b in isAway ? r.AwayBatting : r.HomeBatting)
            batting.Add(new MatchRecordBatter(
                b.Order, b.Position, b.Entry, b.FieldedAt, b.Name,
                b.AtBats, b.Hits, b.Rbi, b.HomeRuns, b.Triples, b.Doubles,
                b.Walks + b.HitByPitches, b.StrikeOuts, b.StolenBases));

        var pitching = new List<MatchRecordPitcher>();
        foreach (var p in isAway ? r.AwayPitching : r.HomePitching)
            pitching.Add(new MatchRecordPitcher(
                p.Name, p.Outs, p.Pitches, p.Hits, p.StrikeOuts, p.Walks + p.HitBatters, p.Runs));

        var paRows = new List<MatchRecordPaRow>();
        foreach (var g in BoxScoreGrid.Build(r.Log, isAway))
        {
            var cells = new List<MatchRecordPa>(g.Cells.Count);
            foreach (var c in g.Cells)
                cells.Add(new MatchRecordPa(c.PaIndex, c.Result, c.IsSacFly, c.IsSacBunt, c.OutsThisPa >= 2));
            paRows.Add(new MatchRecordPaRow(g.Order, g.BatterName, cells));
        }

        return new MatchRecordSide(
            isAway ? r.AwayRuns : r.HomeRuns, isAway ? r.AwayHits : r.HomeHits, isAway ? r.AwayErrors : r.HomeErrors,
            new List<int>(isAway ? r.AwayLineScore : r.HomeLineScore), batting, pitching, paRows);
    }
}

/// <summary>
/// 自校の公式戦の戦績台帳（issue #1413）。<b>全部残す</b>（年数で切らない）。積んだ順＝古い順。
/// 主語は学校＝<see cref="MatchRecord.SchoolId"/> で引く。
/// </summary>
public sealed class MatchRecordBook
{
    /// <summary>いま率いている学校のキー（<c>NationService.ManagerSchoolId</c> と同じ値）。</summary>
    public const int ManagerKey = -1;

    private readonly List<MatchRecord> _records = new();

    public IReadOnlyList<MatchRecord> Records => _records;
    public int Count => _records.Count;

    public void Add(MatchRecord record) => _records.Add(record);

    /// <summary>セーブ復元専用。</summary>
    public void Clear() => _records.Clear();

    /// <summary>ある学校の戦績（古い順）。</summary>
    public IEnumerable<MatchRecord> ForSchool(int schoolId)
    {
        foreach (var r in _records)
            if (r.SchoolId == schoolId) yield return r;
    }

    /// <summary>
    /// 転任で学校のキーを付け替える（issue #1261と同じ「学校に残る記録」）。去る学校の戦績は全国での校ID
    /// （<paramref name="departingSchoolId"/>）へ、転任先の学校がかつて戦った戦績は自校のキーへ戻す。
    /// </summary>
    public void HandOverSchool(int departingSchoolId, int arrivingSchoolId)
    {
        for (var i = 0; i < _records.Count; i++)
        {
            var r = _records[i];
            if (r.SchoolId == ManagerKey) _records[i] = r with { SchoolId = departingSchoolId };
            else if (r.SchoolId == arrivingSchoolId) _records[i] = r with { SchoolId = ManagerKey };
        }
    }
}
