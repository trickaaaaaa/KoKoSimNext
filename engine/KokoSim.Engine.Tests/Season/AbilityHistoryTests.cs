using System.Linq;
using KokoSim.Engine.Saves;
using KokoSim.Engine.Season;
using Xunit;

namespace KokoSim.Engine.Tests.Season;

/// <summary>成長推移の記録（設計書06 §3.2・issue #1027）。</summary>
public sealed class AbilityHistoryTests
{
    private static readonly SeasonCalendar Cal = new();

    private static DevelopingPlayer Player(int grade = 1, int contact = 40)
    {
        var p = new DevelopingPlayer { Name = "記録" };
        p.Grade = grade;
        p.SetLevel(AbilityKind.Contact, contact);
        return p;
    }

    [Fact]
    public void SampleWeeks_AreFirstWeekOfEachMonth_TwelvePerYear()
    {
        var weeks = Enumerable.Range(0, Cal.WeeksPerYear).Where(w => AbilityHistory.IsSampleWeek(w, Cal)).ToList();
        Assert.Equal(12, weeks.Count);
        Assert.Equal(0, weeks[0]);                       // 4月第1週
        Assert.All(weeks, w => Assert.Equal(1, Cal.DateOf(w).WeekOfMonth));
    }

    [Fact]
    public void OnWeekEntered_RecordsOnlyAtMonthStart_AfterTheFirstPoint()
    {
        var p = Player();
        AbilityHistory.OnWeekEntered(new[] { p }, 0, Cal);      // 4月第1週＝始点
        p.SetLevel(AbilityKind.Contact, 41);
        AbilityHistory.OnWeekEntered(new[] { p }, 1, Cal);      // 4月第2週＝記録しない
        AbilityHistory.OnWeekEntered(new[] { p }, 2, Cal);
        AbilityHistory.OnWeekEntered(new[] { p }, 3, Cal);
        p.SetLevel(AbilityKind.Contact, 45);
        AbilityHistory.OnWeekEntered(new[] { p }, 4, Cal);      // 5月第1週

        Assert.Equal(new[] { 0, 4 }, p.History.Select(s => s.Week));
        Assert.Equal(new[] { 40, 45 }, p.History.Select(s => s.Level(AbilityKind.Contact)));
    }

    [Fact]
    public void OnWeekEntered_StartsMidMonth_WhenPlayerHasNoHistory()
    {
        // ゲーム開始直後・旧セーブ・転任先の部員は、月の頭を待たずにその週から記録を始める。
        var p = Player();
        AbilityHistory.OnWeekEntered(new[] { p }, 2, Cal);
        Assert.Single(p.History);
        Assert.Equal(2, p.History[0].Week);
    }

    [Fact]
    public void RetiredPlayers_GetOneClosingPoint_ThenStop()
    {
        var p = Player(grade: 3);
        AbilityHistory.Record(p, 13);
        p.SetLevel(AbilityKind.Contact, 44);   // 7月頭〜引退までの伸び
        p.IsRetired = true;
        AbilityHistory.OnWeekEntered(new[] { p }, 18, Cal);   // 引退の週＝引退時の値で1点
        AbilityHistory.OnWeekEntered(new[] { p }, 22, Cal);   // 以後は値が動かない＝増えない
        AbilityHistory.OnWeekEntered(new[] { p }, 26, Cal);

        Assert.Equal(new[] { 13, 18 }, p.History.Select(s => s.Week));
        Assert.Equal(44, p.History[^1].Level(AbilityKind.Contact));
        Assert.Equal(2, AbilityHistory.SeriesWithCurrent(p, 26).Count);   // 「今」の点も足さない
    }

    [Fact]
    public void Record_SameGradeAndWeek_ReplacesInsteadOfAppending()
    {
        var p = Player();
        AbilityHistory.Record(p, 4);
        p.SetLevel(AbilityKind.Contact, 50);
        AbilityHistory.Record(p, 4);
        Assert.Single(p.History);
        Assert.Equal(50, p.History[0].Level(AbilityKind.Contact));
    }

    [Fact]
    public void SeriesWithCurrent_AppendsTodayValue_WithoutTouchingHistory()
    {
        var p = Player();
        AbilityHistory.Record(p, 0);
        p.SetLevel(AbilityKind.Contact, 47);

        var series = AbilityHistory.SeriesWithCurrent(p, 2);
        Assert.Equal(2, series.Count);
        Assert.Equal(47, series[^1].Level(AbilityKind.Contact));
        Assert.Single(p.History);

        // 今週が最後の記録と同じ週なら、記録を今の値で置き換えた1点だけ。
        var same = AbilityHistory.SeriesWithCurrent(p, 0);
        Assert.Single(same);
        Assert.Equal(47, same[0].Level(AbilityKind.Contact));
    }

    [Fact]
    public void CareerFraction_SpansThreeYears()
    {
        Assert.Equal(0.0, AbilityHistory.CareerFraction(1, 0, Cal));
        Assert.Equal(1.0 / 3, AbilityHistory.CareerFraction(2, 0, Cal), 9);
        Assert.Equal(2.0 / 3, AbilityHistory.CareerFraction(3, 0, Cal), 9);
        Assert.Equal(1.0, AbilityHistory.CareerFraction(4, 10, Cal));   // 卒業後は右端に貼り付く
    }

    [Fact]
    public void Clone_CopiesHistory_Independently()
    {
        var p = Player();
        AbilityHistory.Record(p, 0);
        var c = p.Clone();
        Assert.Equal(p.History, c.History);
        AbilityHistory.Record(c, 4);
        Assert.Single(p.History);
    }

    [Fact]
    public void Save_RoundTripsHistory()
    {
        var p = Player(grade: 2, contact: 52);
        AbilityHistory.Record(p, 0);
        p.SetLevel(AbilityKind.Contact, 58);
        p.SetLevel(AbilityKind.Velocity, 61);
        AbilityHistory.Record(p, 4);

        var back = DevelopingPlayerSaves.FromJson(DevelopingPlayerSaves.ToJson(p));
        Assert.Equal(2, back.History.Count);
        for (var i = 0; i < 2; i++)
        {
            Assert.Equal(p.History[i].Grade, back.History[i].Grade);
            Assert.Equal(p.History[i].Week, back.History[i].Week);
            Assert.Equal(p.History[i].Levels, back.History[i].Levels);
        }
    }

    [Fact]
    public void Save_PlayerWithoutHistory_WritesNoKey_AndReadsEmpty()
    {
        // AI校の選手は記録を持たない＝キーを書かない（数万人ぶんのセーブを膨らませない）。旧セーブも同じ形で読める。
        var json = DevelopingPlayerSaves.ToJson(Player());
        Assert.False(json.Has("hist"));
        Assert.Empty(DevelopingPlayerSaves.FromJson(json).History);
    }
}
