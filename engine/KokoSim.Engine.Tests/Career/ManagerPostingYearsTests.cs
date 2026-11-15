using System.Collections.Generic;
using KokoSim.Engine.Career;
using KokoSim.Engine.Nation;
using KokoSim.Engine.Season;
using Xunit;

namespace KokoSim.Engine.Tests.Career;

/// <summary>
/// 歴任校の年の計算（issue #980・夜間レビュー #1013）。年度（YearIndex＝4月に繰り上がる）・暦年（1月に繰り上がる）・
/// 在任年数（<c>Manager.YearsAtSchool</c>＝3月の総括週で増え転任で0へ戻る）の3つの時計がずれる週
/// （1〜3月＝週38〜49）を含めて、実ゲームの週送り（<c>GameClock.EnterWeek</c> の総括週で成長→転任判定→
/// その週のうちに確定）を整数だけで再現し、表示される就任年・退任年・在任年数を全週で固定する。
/// </summary>
public sealed class ManagerPostingYearsTests
{
    private const int BaseYear = 2026;   // Shell の SeasonClock.SeasonBaseYear と同じ（年度1＝2026年度）
    private static readonly SeasonCalendar Calendar = new();

    /// <summary>週送りの再現。総括週に入った瞬間に在任年数を+1し、転任を予定した年度ならその週に確定する。</summary>
    private sealed class Timeline
    {
        public int YearIndex = 1;
        public int Week;
        public int YearsAtSchool;
        public readonly ManagerPostingHistory History = new();
        public readonly SchoolRecord Record = new SchoolRecordBook().For(-1);
        private readonly HashSet<int> _transferYears;

        public Timeline(params int[] transferAtEndOfYears) => _transferYears = new HashSet<int>(transferAtEndOfYears);

        public int StartLabel => ManagerPostingYears.FiscalYearLabel(BaseYear, History.CurrentStartYearIndex);
        public int Tenure => ManagerPostingYears.CurrentTenureYears(YearIndex, History.CurrentStartYearIndex);

        public void AdvanceWeek()
        {
            Week++;
            if (Week >= Calendar.WeeksPerYear) { Week = 0; YearIndex++; }
            if (Calendar.IsProspectConfirmWeek(Week))
            {
                YearsAtSchool++;   // ManagerSeasonUpdate.Apply（ApplyYearlyGrowth(YearIndex)）
                if (_transferYears.Contains(YearIndex))
                {
                    // ManagerTransferService.Confirm（総括週のうちにモーダルで確定）。
                    History.Close("前任高校", ManagerPostingYears.EndedYearIndexAtConfirm(YearIndex, Week, Calendar), Record);
                    YearsAtSchool = 0;   // ManagerTransferModel.ApplyDecision
                }
            }
        }

        /// <summary>(年度, 週) に着くまで進める。</summary>
        public void AdvanceTo(int yearIndex, int week)
        {
            while (YearIndex < yearIndex || (YearIndex == yearIndex && Week < week)) AdvanceWeek();
        }
    }

    [Fact]
    public void NoTransfer_StartYearAndTenureFollowFiscalYear_InEveryWeekIncludingJanuaryToMarch()
    {
        var t = new Timeline();
        for (var y = 1; y <= 3; y++)
            for (var w = 0; w < Calendar.WeeksPerYear; w++)
            {
                t.AdvanceTo(y, w);
                // 1〜3月（週38〜49）も、総括週の後（週46〜49）も、就任は2026・在任は年度どおり。
                Assert.Equal(2026, t.StartLabel);
                Assert.Equal(y, t.Tenure);
            }
    }

    [Fact]
    public void TransferInWeek46OfThirdYear_ClosesAs2026To2028_ThreeYears_AndNextPostingStarts2029()
    {
        var t = new Timeline(3);
        t.AdvanceTo(3, 45);
        Assert.Empty(t.History.Closed);
        Assert.Equal(3, t.Tenure);

        t.AdvanceTo(3, 46);   // 3年目の3月総括週で転任
        var closed = Assert.Single(t.History.Closed);
        Assert.Equal(2026, ManagerPostingYears.FiscalYearLabel(BaseYear, closed.StartYearIndex));
        Assert.Equal(2028, ManagerPostingYears.FiscalYearLabel(BaseYear, closed.EndYearIndex));
        Assert.Equal(3, closed.TenureYears);

        // 新しい学校は翌年度（2029年度）から。年度内の残り（週46〜49）は就任前＝1年目。
        for (var w = 46; w < Calendar.WeeksPerYear; w++)
        {
            t.AdvanceTo(3, w);
            Assert.Equal(2029, t.StartLabel);
            Assert.Equal(1, t.Tenure);
        }
        for (var w = 0; w < Calendar.WeeksPerYear; w++)
        {
            t.AdvanceTo(4, w);
            Assert.Equal(2029, t.StartLabel);
            Assert.Equal(1, t.Tenure);
        }
        t.AdvanceTo(5, 0);
        Assert.Equal(2, t.Tenure);
    }

    [Fact]
    public void TransferAfterOneYear_ClosesAs2026To2026_OneYear()
    {
        var t = new Timeline(1);
        t.AdvanceTo(1, 46);
        var closed = Assert.Single(t.History.Closed);
        Assert.Equal(2026, ManagerPostingYears.FiscalYearLabel(BaseYear, closed.StartYearIndex));
        Assert.Equal(2026, ManagerPostingYears.FiscalYearLabel(BaseYear, closed.EndYearIndex));
        Assert.Equal(1, closed.TenureYears);
        Assert.Equal(2027, t.StartLabel);
    }

    [Fact]
    public void TwoTransfers_ChainPostingsWithoutGapOrOverlap()
    {
        var t = new Timeline(2, 5);
        t.AdvanceTo(6, 10);
        Assert.Equal(2, t.History.Closed.Count);
        Assert.Equal((1, 2), (t.History.Closed[0].StartYearIndex, t.History.Closed[0].EndYearIndex));
        Assert.Equal((3, 5), (t.History.Closed[1].StartYearIndex, t.History.Closed[1].EndYearIndex));
        Assert.Equal(6, t.History.CurrentStartYearIndex);
        Assert.Equal(1, t.Tenure);
    }

    [Theory]
    [InlineData(46, 3)]   // 総括週のうちに確定（通常経路）＝まだ終わる年度の中
    [InlineData(49, 3)]   // 総括週の後、年度末までに確定
    public void EndedYearIndexAtConfirm_InsideTheEndingYear(int week, int expected)
        => Assert.Equal(expected, ManagerPostingYears.EndedYearIndexAtConfirm(3, week, Calendar));

    [Theory]
    [InlineData(0)]    // 年度替わり側で開いた取りこぼし経路（総括週を跨がずに4月へ来た）
    [InlineData(20)]   // 選ばないまま4月を越えてから確定した
    public void EndedYearIndexAtConfirm_AfterYearRollover_IsPreviousYear(int week)
        => Assert.Equal(3, ManagerPostingYears.EndedYearIndexAtConfirm(4, week, Calendar));

    [Fact]
    public void ConfirmAfterYearRollover_StillClosesTheEndedYear()
    {
        var history = new ManagerPostingHistory();
        var record = new SchoolRecordBook().For(-1);
        // 年度3の総括で開いたが、年度4の4月に入ってから確定した。
        history.Close("前任高校", ManagerPostingYears.EndedYearIndexAtConfirm(4, 0, Calendar), record);
        Assert.Equal((1, 3), (history.Closed[0].StartYearIndex, history.Closed[0].EndYearIndex));
        Assert.Equal(4, history.CurrentStartYearIndex);
    }

    [Fact]
    public void OldSaveEstimate_MatchesStoredStartYear_InEveryWeek()
    {
        // 旧セーブ（v31以前）の移行で使う推定が、保存方式の値と全週で一致すること（転任の前後も）。
        var t = new Timeline(2);
        for (var y = 1; y <= 4; y++)
            for (var w = 0; w < Calendar.WeeksPerYear; w++)
            {
                t.AdvanceTo(y, w);
                Assert.Equal(t.History.CurrentStartYearIndex,
                    ManagerPostingYears.EstimateCurrentStartYearIndex(t.YearIndex, t.Week, t.YearsAtSchool, Calendar));
            }
    }
}
