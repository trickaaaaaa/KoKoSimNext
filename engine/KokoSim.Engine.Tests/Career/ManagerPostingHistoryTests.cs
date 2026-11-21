using KokoSim.Engine.Career;
using KokoSim.Engine.Nation;
using KokoSim.Engine.Saves;
using Xunit;

namespace KokoSim.Engine.Tests.Career;

/// <summary>
/// 監督の赴任履歴 <see cref="ManagerPostingHistory"/>（issue #980）。<see cref="SchoolRecordBook"/> の
/// 自校バケットは転任をまたいでも単一の累計のままなので、赴任開始時点の基準値からの差分で
/// 1校ぶんの戦績を切り出せることを固定する。
/// </summary>
public sealed class ManagerPostingHistoryTests
{
    private static SchoolRecord RecordWith(int wins, int losses, int summer, int spring, int schoolId = -1)
    {
        var book = new SchoolRecordBook();
        for (var i = 0; i < wins; i++) book.RecordOfficialMatch(winnerId: schoolId, loserId: 999);
        for (var i = 0; i < losses; i++) book.RecordOfficialMatch(winnerId: 999, loserId: schoolId);
        for (var i = 0; i < summer; i++) book.RecordKoshienAppearance(schoolId, KoshienKind.Summer, calendarYear: 2026 + i);
        for (var i = 0; i < spring; i++) book.RecordKoshienAppearance(schoolId, KoshienKind.Spring, calendarYear: 2026 + i);
        return book.For(schoolId);
    }

    [Fact]
    public void CurrentDelta_StartsAtZero_BeforeAnyTransfer()
    {
        var history = new ManagerPostingHistory();
        var record = RecordWith(wins: 24, losses: 9, summer: 1, spring: 0);

        var (wins, losses, summer, spring) = history.CurrentDelta(record);

        Assert.Equal(24, wins);
        Assert.Equal(9, losses);
        Assert.Equal(1, summer);
        Assert.Equal(0, spring);
        Assert.Empty(history.Closed);
    }

    [Fact]
    public void Close_CutsOffDeltaSinceBaseline_AndAdvancesBaselineForNextPosting()
    {
        var history = new ManagerPostingHistory();
        var book = new SchoolRecordBook();
        for (var i = 0; i < 10; i++) book.RecordOfficialMatch(winnerId: -1, loserId: 999);
        book.RecordKoshienAppearance(-1, KoshienKind.Summer, calendarYear: 2027);
        var record = book.For(-1);

        // 1校目: 年度1〜2で10勝0敗・夏1回、年度2の3月に転任。
        history.Close("前任高校", endedYearIndex: 2, current: record);

        Assert.Single(history.Closed);
        var closed = history.Closed[0];
        Assert.Equal("前任高校", closed.SchoolName);
        Assert.Equal(1, closed.StartYearIndex);
        Assert.Equal(2, closed.EndYearIndex);
        Assert.Equal(2, closed.TenureYears);
        // 次の赴任は翌年度から＝退任時に就任年度も確定する（逆算しない）。
        Assert.Equal(3, history.CurrentStartYearIndex);
        Assert.Equal(10, closed.Wins);
        Assert.Equal(0, closed.Losses);
        Assert.Equal(1, closed.SummerAppearances);
        Assert.Equal(0, closed.SpringAppearances);

        // 通算バケットは単一（issue #641）なので転任後もそのまま積み続ける。2校目で3勝だけ積む。
        for (var i = 0; i < 3; i++) book.RecordOfficialMatch(winnerId: -1, loserId: 999);

        // 今の赴任（2校目）ぶんの差分は、通算13勝のうち前任校で確定済みの10勝を引いた3勝だけになる。
        var (wins, losses, summer, spring) = history.CurrentDelta(record);
        Assert.Equal(3, wins);
        Assert.Equal(0, losses);
        Assert.Equal(0, summer);
        Assert.Equal(0, spring);
    }

    [Fact]
    public void ResetForNewGame_ClearsClosedPostingsAndBaseline()
    {
        var history = new ManagerPostingHistory();
        history.Close("前任高校", 3, RecordWith(12, 3, 1, 0));
        Assert.NotEmpty(history.Closed);

        history.ResetForNewGame();

        Assert.Empty(history.Closed);
        Assert.Equal(1, history.CurrentStartYearIndex);
        var (wins, losses, summer, spring) = history.CurrentDelta(RecordWith(0, 0, 0, 0));
        Assert.Equal(0, wins);
        Assert.Equal(0, losses);
        Assert.Equal(0, summer);
        Assert.Equal(0, spring);
    }

    // ===== 保存 =====

    [Fact]
    public void ManagerPostings_RoundTripThroughSave_AndOldSaveIsEmpty()
    {
        var state = new SaveState
        {
            ManagerPostings = new System.Collections.Generic.List<ManagerPosting>
            {
                new() { SchoolName = "前任高校", StartYearIndex = 1, EndYearIndex = 3, Wins = 24, Losses = 9, SummerAppearances = 1, SpringAppearances = 0 },
            },
            ManagerPostingStartYearIndex = 4,
            ManagerPostingBaselineWins = 24,
            ManagerPostingBaselineLosses = 9,
            ManagerPostingBaselineSummerAppearances = 1,
            ManagerPostingBaselineSpringAppearances = 0,
        };

        Assert.True(SaveCodec.TryDeserialize(SaveCodec.Serialize(state), out var s, out var err), err);

        Assert.Single(s.ManagerPostings);
        Assert.Equal("前任高校", s.ManagerPostings[0].SchoolName);
        Assert.Equal(1, s.ManagerPostings[0].StartYearIndex);
        Assert.Equal(3, s.ManagerPostings[0].EndYearIndex);
        Assert.Equal(4, s.ManagerPostingStartYearIndex);
        Assert.Equal(24, s.ManagerPostings[0].Wins);
        Assert.Equal(24, s.ManagerPostingBaselineWins);
        Assert.Equal(9, s.ManagerPostingBaselineLosses);
        Assert.Equal(1, s.ManagerPostingBaselineSummerAppearances);

        // 旧セーブ（v31以前）には歴任校の区切りが存在しえない＝空配列・基準値0（＝通算そのものが今の赴任ぶん）で補う。
        var json = SaveCodec.Serialize(new SaveState()).Replace($"\"version\":{SaveCodec.CurrentVersion}", "\"version\":31")
            .Replace(",\"managerPostings\":[]", "")
            .Replace(",\"managerPostingStartYearIndex\":1", "")
            .Replace(",\"managerPostingBaselineWins\":0", "")
            .Replace(",\"managerPostingBaselineLosses\":0", "")
            .Replace(",\"managerPostingBaselineSummerApp\":0", "")
            .Replace(",\"managerPostingBaselineSpringApp\":0", "");
        Assert.True(SaveCodec.TryDeserialize(json, out var old, out var err2), err2);
        Assert.Empty(old.ManagerPostings);
        Assert.Equal(1, old.ManagerPostingStartYearIndex);
        Assert.Equal(0, old.ManagerPostingBaselineWins);
        Assert.Equal(0, old.ManagerPostingBaselineLosses);
        Assert.Equal(0, old.ManagerPostingBaselineSummerAppearances);
        Assert.Equal(0, old.ManagerPostingBaselineSpringAppearances);
    }

    [Theory]
    [InlineData(3, 40, 2, 1)]   // 3年目の1月（総括前）: 在任2年ぶん＝就任は年度1
    [InlineData(3, 47, 3, 1)]   // 3年目の総括後: 在任3年ぶん＝就任は年度1
    [InlineData(3, 47, 0, 4)]   // 3年目の総括で転任済み: 次の学校は年度4から
    [InlineData(4, 10, 0, 4)]   // 転任した翌年度の途中
    public void OldSave_EstimatesCurrentStartYearIndexFromYearsAtSchool(int yearIndex, int week, int yearsAtSchool, int expected)
    {
        var state = new SaveState { YearIndex = yearIndex, Week = week, ManagerPostingStartYearIndex = 99 };
        state.Manager.YearsAtSchool = yearsAtSchool;
        var json = SaveCodec.Serialize(state).Replace($"\"version\":{SaveCodec.CurrentVersion}", "\"version\":31")
            .Replace(",\"managerPostingStartYearIndex\":99", "");
        Assert.DoesNotContain("managerPostingStartYearIndex", json);

        Assert.True(SaveCodec.TryDeserialize(json, out var old, out var err), err);
        Assert.Equal(expected, old.ManagerPostingStartYearIndex);
    }
}
