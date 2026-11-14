using System.Collections.Generic;
using KokoSim.Engine.Nation;

namespace KokoSim.Engine.Career;

/// <summary>
/// 退任済みの赴任1回ぶんの記録（issue #980・設計書04 §1.3）。在任中の赴任はここに入らない
/// （<see cref="ManagerPostingHistory.CurrentDelta"/> が通算からの差分として都度算出する）。
/// </summary>
public sealed class ManagerPosting
{
    public string SchoolName { get; set; } = "";

    /// <summary>就任した暦年・退任した暦年（どちらも在籍した側に含む。1年で転任なら同じ年）。</summary>
    public int StartYear { get; set; }
    public int EndYear { get; set; }

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
/// 転任のたびに差分を確定して <see cref="Closed"/> へ積む。校名・在任年数は Shell 側の生きた値
/// （今の校名・<c>Manager.YearsAtSchool</c>）から組むため、ここでは今の赴任ぶんの校名・開始年は持たない
/// （二重管理でずれを作らないため）。
/// </summary>
public sealed class ManagerPostingHistory
{
    private readonly List<ManagerPosting> _closed = new();

    /// <summary>退任済みの赴任（新しい順ではなく発生順）。</summary>
    public IReadOnlyList<ManagerPosting> Closed => _closed;

    public int BaselineWins { get; private set; }
    public int BaselineLosses { get; private set; }
    public int BaselineSummerAppearances { get; private set; }
    public int BaselineSpringAppearances { get; private set; }

    /// <summary>セーブ復元専用（<see cref="Saves.ManagerPostingSaves"/> だけが使う）。</summary>
    internal void Restore(
        IEnumerable<ManagerPosting> closed,
        int baselineWins, int baselineLosses, int baselineSummerAppearances, int baselineSpringAppearances)
    {
        _closed.Clear();
        _closed.AddRange(closed);
        BaselineWins = baselineWins;
        BaselineLosses = baselineLosses;
        BaselineSummerAppearances = baselineSummerAppearances;
        BaselineSpringAppearances = baselineSpringAppearances;
    }

    /// <summary>新規ゲーム開始: 赴任履歴を白紙にする（<see cref="SchoolRecordBook"/> も同時に新品になる前提）。</summary>
    public void ResetForNewGame()
    {
        _closed.Clear();
        BaselineWins = BaselineLosses = BaselineSummerAppearances = BaselineSpringAppearances = 0;
    }

    /// <summary>
    /// 転任: 今の赴任を <paramref name="schoolName"/>（旧校の校名）・<paramref name="startYear"/>〜
    /// <paramref name="endYear"/>（どちらも暦年）で締め、通算からの差分を確定して積む。
    /// 基準値を今の累計まで進め、次の赴任の差分がここから数えられるようにする。
    /// </summary>
    public void Close(string schoolName, int startYear, int endYear, SchoolRecord current)
    {
        _closed.Add(new ManagerPosting
        {
            SchoolName = schoolName,
            StartYear = startYear,
            EndYear = endYear,
            Wins = current.OfficialWins - BaselineWins,
            Losses = current.OfficialLosses - BaselineLosses,
            SummerAppearances = current.SummerAppearances - BaselineSummerAppearances,
            SpringAppearances = current.SpringAppearances - BaselineSpringAppearances,
        });
        BaselineWins = current.OfficialWins;
        BaselineLosses = current.OfficialLosses;
        BaselineSummerAppearances = current.SummerAppearances;
        BaselineSpringAppearances = current.SpringAppearances;
    }

    /// <summary>今の赴任ぶんの差分（勝・敗・夏出場・春出場）。校名・在任年数は呼び出し側の生きた値から組む。</summary>
    public (int Wins, int Losses, int SummerAppearances, int SpringAppearances) CurrentDelta(SchoolRecord current)
        => (current.OfficialWins - BaselineWins,
            current.OfficialLosses - BaselineLosses,
            current.SummerAppearances - BaselineSummerAppearances,
            current.SpringAppearances - BaselineSpringAppearances);
}
