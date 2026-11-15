using System;
using System.Collections.Generic;
using KokoSim.Engine.Nation;
using KokoSim.Engine.Season;

namespace KokoSim.Engine.Career;

/// <summary>
/// 退任済みの赴任1回ぶんの記録（issue #980・設計書04 §1.3）。在任中の赴任はここに入らない
/// （<see cref="ManagerPostingHistory.CurrentDelta"/> が通算からの差分として都度算出する）。
/// 年はすべて<b>年度</b>（YearIndex・1始まり・4月〜翌3月）で持つ。表記へは表示側で
/// <see cref="ManagerPostingYears.FiscalYearLabel"/> を通す（「年度」と「暦年」を取り違えないため）。
/// </summary>
public sealed class ManagerPosting
{
    public string SchoolName { get; set; } = "";

    /// <summary>就任した年度・退任した年度（どちらも在籍した側に含む。1年で転任なら同じ年度）。</summary>
    public int StartYearIndex { get; set; }
    public int EndYearIndex { get; set; }

    /// <summary>在任年数（就任年度〜退任年度の両端込み）。</summary>
    public int TenureYears => EndYearIndex - StartYearIndex + 1;

    /// <summary>この赴任ぶんの公式戦勝敗・甲子園出場回数（退任時に確定した差分）。</summary>
    public int Wins { get; set; }
    public int Losses { get; set; }
    public int SummerAppearances { get; set; }
    public int SpringAppearances { get; set; }
}

/// <summary>
/// 監督の赴任履歴（issue #980）。<see cref="SchoolRecordBook"/> の自校バケット（校ID固定＝
/// <c>NationService.ManagerSchoolId</c>）は転任をまたいでも単一の累計のまま（design-04 §1.3・issue #641で
/// 自校IDを変えない方式に決まったため）なので、ここでは「今の赴任が始まった時点の累計」を基準値として持ち、
/// 転任のたびに差分を確定して <see cref="Closed"/> へ積む。
/// <para>今の赴任の就任年度（<see cref="CurrentStartYearIndex"/>）は<b>逆算せず保存する</b>。
/// <c>Manager.YearsAtSchool</c> は3月の総括週で増え、転任で0へ戻るため、そこから就任年を引き直すと
/// 1〜3月や総括週以降で1年ずれる（夜間レビュー #1013）。転任の確定時に次の赴任の就任年度も確定する。
/// 校名は今の赴任ぶんは持たない（改名・issue #946 に追従する生きた校名を表示側が読み、退任時にだけ確定する）。</para>
/// </summary>
public sealed class ManagerPostingHistory
{
    private readonly List<ManagerPosting> _closed = new();

    /// <summary>退任済みの赴任（新しい順ではなく発生順）。</summary>
    public IReadOnlyList<ManagerPosting> Closed => _closed;

    /// <summary>今の赴任の就任年度（新規ゲームは年度1）。</summary>
    public int CurrentStartYearIndex { get; private set; } = 1;

    public int BaselineWins { get; private set; }
    public int BaselineLosses { get; private set; }
    public int BaselineSummerAppearances { get; private set; }
    public int BaselineSpringAppearances { get; private set; }

    /// <summary>
    /// セーブ復元用。Shell 側（<c>GameSession.Restore</c>）がセーブDTOの素のリスト・基準値から
    /// このインスタンスへ据える（DTO は <see cref="ManagerPostingHistory"/> 自体を保存しないため）。
    /// </summary>
    public void Restore(
        IEnumerable<ManagerPosting> closed, int currentStartYearIndex,
        int baselineWins, int baselineLosses, int baselineSummerAppearances, int baselineSpringAppearances)
    {
        _closed.Clear();
        _closed.AddRange(closed);
        CurrentStartYearIndex = Math.Max(1, currentStartYearIndex);
        BaselineWins = baselineWins;
        BaselineLosses = baselineLosses;
        BaselineSummerAppearances = baselineSummerAppearances;
        BaselineSpringAppearances = baselineSpringAppearances;
    }

    /// <summary>新規ゲーム開始: 赴任履歴を白紙にする（<see cref="SchoolRecordBook"/> も同時に新品になる前提）。</summary>
    public void ResetForNewGame()
    {
        _closed.Clear();
        CurrentStartYearIndex = 1;
        BaselineWins = BaselineLosses = BaselineSummerAppearances = BaselineSpringAppearances = 0;
    }

    /// <summary>
    /// 転任: 今の赴任を <paramref name="schoolName"/>（旧校の校名）で、就任年度
    /// <see cref="CurrentStartYearIndex"/>〜退任年度 <paramref name="endedYearIndex"/> として締め、
    /// 通算からの差分を確定して積む。次の赴任は翌年度（<paramref name="endedYearIndex"/>+1）から始まる＝
    /// その就任年度と基準値をここで確定する。退任年度は <see cref="ManagerPostingYears.EndedYearIndexAtConfirm"/> で求める。
    /// </summary>
    public void Close(string schoolName, int endedYearIndex, SchoolRecord current)
    {
        var end = Math.Max(CurrentStartYearIndex, endedYearIndex);
        _closed.Add(new ManagerPosting
        {
            SchoolName = schoolName,
            StartYearIndex = CurrentStartYearIndex,
            EndYearIndex = end,
            Wins = current.OfficialWins - BaselineWins,
            Losses = current.OfficialLosses - BaselineLosses,
            SummerAppearances = current.SummerAppearances - BaselineSummerAppearances,
            SpringAppearances = current.SpringAppearances - BaselineSpringAppearances,
        });
        CurrentStartYearIndex = end + 1;
        BaselineWins = current.OfficialWins;
        BaselineLosses = current.OfficialLosses;
        BaselineSummerAppearances = current.SummerAppearances;
        BaselineSpringAppearances = current.SpringAppearances;
    }

    /// <summary>今の赴任ぶんの差分（勝・敗・夏出場・春出場）。</summary>
    public (int Wins, int Losses, int SummerAppearances, int SpringAppearances) CurrentDelta(SchoolRecord current)
        => (current.OfficialWins - BaselineWins,
            current.OfficialLosses - BaselineLosses,
            current.SummerAppearances - BaselineSummerAppearances,
            current.SpringAppearances - BaselineSpringAppearances);
}

/// <summary>
/// 歴任校まわりの年の計算（issue #980）。核心は<b>年度（YearIndex）と暦年の取り違え</b>を起こさないこと:
/// 年度は4月（週0）に繰り上がり、暦年は1月（週38付近）に繰り上がり、監督の在任年数
/// （<c>Manager.YearsAtSchool</c>）は3月の総括週（<see cref="SeasonCalendar.ProspectConfirmWeek"/>）で増える。
/// 歴任校の年はすべて年度で数え、表記だけ「年度の開始暦年」（年度1＝2026）で出す。
/// </summary>
public static class ManagerPostingYears
{
    /// <summary>
    /// 転任を確定した時点の「終わる年度」。転任判定は3月の総括週（週46）に
    /// <c>ApplyYearlyGrowth(YearIndex)</c> の中で開く＝週46〜49に確定するならまだ終わる年度の中にいる。
    /// 総括週を跨がずに年度替わりで開いた（取りこぼし経路）か、選ばないまま4月を越えて確定したなら、
    /// 年度はもう繰り上がっている＝前年度が終わった年度。
    /// </summary>
    public static int EndedYearIndexAtConfirm(int yearIndex, int week, SeasonCalendar calendar)
        => week >= calendar.ProspectConfirmWeek ? yearIndex : yearIndex - 1;

    /// <summary>
    /// 今の赴任の在任年数（1始まり）。転任を総括週に確定した直後（週46〜49）は次の赴任が翌年度から
    /// 始まる＝まだ就任前なので1年目と数える。
    /// </summary>
    public static int CurrentTenureYears(int currentYearIndex, int startYearIndex)
        => Math.Max(1, currentYearIndex - startYearIndex + 1);

    /// <summary>年度の表記（年度の開始暦年）。年度1＝<paramref name="baseYear"/>（4月始まり）。</summary>
    public static int FiscalYearLabel(int baseYear, int yearIndex) => baseYear + yearIndex - 1;

    /// <summary>
    /// 旧セーブ（v31以前）の移行用: 今の赴任の就任年度を <c>Manager.YearsAtSchool</c> から推定する。
    /// 在任年数は総括週に増える（その年度の総括を済ませた後なら今年度ぶんも数えてある）ので、
    /// 総括週以降は1を足して戻す。転任を総括週に確定した直後（在任0・週46〜49）は翌年度が就任年度になる。
    /// </summary>
    public static int EstimateCurrentStartYearIndex(int yearIndex, int week, int yearsAtSchool, SeasonCalendar calendar)
    {
        var start = yearIndex - yearsAtSchool + (week >= calendar.ProspectConfirmWeek ? 1 : 0);
        return Math.Clamp(start, 1, Math.Max(1, yearIndex + 1));
    }
}
