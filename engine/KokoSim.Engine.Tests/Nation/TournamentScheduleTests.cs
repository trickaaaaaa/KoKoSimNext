using System.Collections.Generic;
using System.Linq;
using KokoSim.Engine.Nation.Tournaments;
using KokoSim.Engine.Season;
using Xunit;

namespace KokoSim.Engine.Tests.Nation;

/// <summary>
/// 大会日程表（issue #583・設計書05 §1.1）。「1回戦は数日に分散」「準々決勝・準決勝は同日」
/// 「準々決勝後・準決勝後に休養日」の形と総日数を固定する。
/// 試合日は <c>AceRestSelector</c> の温存抽選シードに入るので、ここが動く＝帯が動く。
/// </summary>
public sealed class TournamentScheduleTests
{
    private static readonly TournamentSchedule S = new();

    /// <summary>ラウンド r（0基点）のカード数（残りラウンド数から導く）。</summary>
    private static int CardsIn(int totalRounds, int round) => 1 << (totalRounds - round - 1);

    /// <summary>そのラウンドの全カードの試合日（カード添字順）。</summary>
    private static List<int> DaysOfRound(int totalRounds, int round)
    {
        var cards = CardsIn(totalRounds, round);
        return Enumerable.Range(0, cards)
            .Select(i => S.MatchDay(totalRounds, round, i, cards))
            .ToList();
    }

    // ── 形（確定事項2） ──────────────────────────────────────

    /// <summary>1回戦は数日に分散する（＝自校の初戦日がブラケット位置で変わる）。</summary>
    [Fact]
    public void FirstRound_IsSpreadAcrossDays()
    {
        var days = DaysOfRound(totalRounds: 8, round: 0);
        Assert.Equal(128, days.Count);
        Assert.Equal(3, days.Distinct().Count());          // 既定＝序盤3日分散
        Assert.Equal(S.FirstRoundDay, days[0]);            // 上から順に消化
        Assert.Equal(S.FirstRoundDay + 2, days[^1]);
        // 単調非減少（球数台帳が「試合日は単調非減少で呼ばれる」前提を置いている）。
        for (var i = 1; i < days.Count; i++) Assert.True(days[i] >= days[i - 1], "試合日が逆行した");
    }

    /// <summary>各日にほぼ均等にカードが割れる（1日だけ極端に薄い/厚いにならない）。</summary>
    [Fact]
    public void FirstRound_SplitsCardsEvenly()
    {
        var days = DaysOfRound(totalRounds: 8, round: 0);
        var perDay = days.GroupBy(d => d).Select(g => g.Count()).ToList();
        Assert.Equal(3, perDay.Count);
        Assert.True(perDay.Max() - perDay.Min() <= 1, $"割り方が偏っている: {string.Join(",", perDay)}");
    }

    /// <summary>準々決勝（残り3）・準決勝（残り2）・決勝（残り1）は全カード同日。</summary>
    [Theory]
    [InlineData(3)]   // 準々決勝＝4試合
    [InlineData(2)]   // 準決勝＝2試合
    [InlineData(1)]   // 決勝＝1試合
    public void LateRounds_ArePlayedOnASingleDay(int roundsRemaining)
    {
        const int total = 8;
        var days = DaysOfRound(total, total - roundsRemaining);
        Assert.Single(days.Distinct());
    }

    /// <summary>準々決勝の後と準決勝の後にだけ休養日が入る（他のラウンド間は空かない）。</summary>
    [Fact]
    public void RestDays_OnlyAfterQuarterfinalAndSemifinal()
    {
        const int total = 8;
        for (var round = 0; round < total - 1; round++)
        {
            var rr = total - round;
            var lastDayOfRound = DaysOfRound(total, round).Max();
            var nextRoundStart = S.RoundStartDay(total, round + 1);
            var gap = nextRoundStart - lastDayOfRound - 1;   // あいだに空いた日数
            var expected = rr == 3 ? S.RestDaysAfterQuarterfinal
                : rr == 2 ? S.RestDaysAfterSemifinal
                : 0;
            Assert.Equal(expected, gap);
        }
    }

    // ── 総日数（ラウンド数別） ────────────────────────────────

    /// <summary>
    /// ラウンド数ごとの総日数を固定する（A案）。ここが変わる＝投手の実効休養が変わる＝帯が動く。
    /// 5ラウンド県（32校規模）は旧「中1日の等差」と同じ9日で、8ラウンド県（神奈川規模）は18日。
    /// </summary>
    [Theory]
    [InlineData(5, 9)]
    [InlineData(6, 12)]
    [InlineData(7, 15)]
    [InlineData(8, 18)]
    [InlineData(9, 21)]
    public void FinalDay_MatchesTheAgreedTable(int totalRounds, int expectedFinalDay)
        => Assert.Equal(expectedFinalDay, S.FinalDay(totalRounds));

    /// <summary>ラウンドが進むほど日は必ず後ろへ動く（据え置きも逆行もしない）。</summary>
    [Fact]
    public void RoundStartDays_AreStrictlyIncreasing()
    {
        for (var total = 5; total <= 9; total++)
            for (var round = 1; round < total; round++)
                Assert.True(
                    S.RoundStartDay(total, round) > S.RoundStartDay(total, round - 1),
                    $"total={total} round={round} で日が進んでいない");
    }

    /// <summary>序盤・中盤・終盤の分散日数（残りラウンド数で決まる＝ラウンド数に依らない）。</summary>
    [Theory]
    [InlineData(9, 3)]
    [InlineData(6, 3)]
    [InlineData(5, 2)]
    [InlineData(4, 2)]
    [InlineData(3, 1)]
    [InlineData(1, 1)]
    public void SpreadDays_DependOnRoundsRemainingOnly(int roundsRemaining, int expected)
        => Assert.Equal(expected, S.SpreadDaysFor(roundsRemaining));

    // ── 勝ち上がる1校から見た試合間隔（投手の休養＝帯に効く） ──────

    /// <summary>
    /// ブラケット位置は勝ち上がっても保たれる（ラウンド r のカード i の勝者は r+1 のカード i/2）ので、
    /// 1校の全試合日を初戦のカード添字から辿れる。
    /// </summary>
    private static List<int> DaysAlongPath(int totalRounds, int firstRoundCard)
    {
        var days = new List<int>();
        var card = firstRoundCard;
        for (var round = 0; round < totalRounds; round++)
        {
            days.Add(S.MatchDay(totalRounds, round, card, CardsIn(totalRounds, round)));
            card /= 2;
        }
        return days;
    }

    /// <summary>同じ日に2試合はしない（勝ち上がりのどの経路でも試合日は必ず前へ進む）。</summary>
    [Fact]
    public void WinningTeam_NeverPlaysTwiceOnTheSameDay()
    {
        for (var total = 5; total <= 9; total++)
        {
            for (var card = 0; card < CardsIn(total, 0); card++)
            {
                var days = DaysAlongPath(total, card);
                for (var i = 1; i < days.Count; i++)
                    Assert.True(days[i] > days[i - 1],
                        $"total={total} card={card} で同日/逆行: {string.Join(",", days)}");
            }
        }
    }

    /// <summary>
    /// 準々決勝→準決勝・準決勝→決勝は<b>ブラケットのどの位置でも必ず中1日以上</b>空く（休養日の効果）。
    /// ここが崩れると終盤の連投が起きて投手の消耗モデルの意味が変わる＝帯が動く。
    /// </summary>
    [Fact]
    public void RestDays_GuaranteeAGapBeforeSemifinalAndFinal()
    {
        for (var total = 5; total <= 9; total++)
        {
            for (var card = 0; card < CardsIn(total, 0); card++)
            {
                var days = DaysAlongPath(total, card);
                // 残り3（準々決勝）→残り2（準決勝）と、残り2→残り1（決勝）。
                for (var rr = 3; rr >= 2; rr--)
                {
                    var from = days[total - rr];
                    var to = days[total - rr + 1];
                    Assert.True(to - from >= 2,
                        $"total={total} card={card} 残り{rr}→{rr - 1} が中0日: {from}→{to}");
                }
            }
        }
    }

    /// <summary>
    /// 序盤〜中盤は<b>中0日（連日）が起こりうる</b>のを記録する。分散日数が減るラウンド境
    /// （例 2日分散→同日開催）でブラケット下側の校が翌日に試合を持つ＝現実の大会でも起きる形で、
    /// 球数制限と終盤の休養日が意味を持つ理由でもある。ここを0にしたいなら休養日を足す（帯が動く）。
    /// </summary>
    [Fact]
    public void EarlyRounds_CanHaveBackToBackDays()
    {
        const int total = 8;
        var backToBack = 0;
        for (var card = 0; card < CardsIn(total, 0); card++)
        {
            var days = DaysAlongPath(total, card);
            for (var i = 1; i < days.Count; i++)
                if (days[i] - days[i - 1] == 1) backToBack++;
        }
        Assert.True(backToBack > 0, "連日が一度も起きない＝分散が効いていない");
    }

    // ── 実月日（issue #583「日付表示は実月日」） ───────────────

    /// <summary>開幕日そのもの（経過0日）。</summary>
    [Fact]
    public void RealDate_StartsAtTheOpeningDay()
    {
        Assert.Equal((7, 10), TournamentDate.DateOf(S, TournamentKind.Summer, 0));
        Assert.Equal((9, 12), TournamentDate.DateOf(S, TournamentKind.Autumn, 0));
    }

    /// <summary>月を跨いで繰り上がる（7月10日＋25日＝8月4日。7月は31日）。</summary>
    [Fact]
    public void RealDate_RollsOverMonthEnd()
    {
        Assert.Equal((7, 31), TournamentDate.AddDays(7, 10, 21));
        Assert.Equal((8, 1), TournamentDate.AddDays(7, 10, 22));
        Assert.Equal((8, 4), TournamentDate.AddDays(7, 10, 25));
        // 30日の月（9月）も正しく繰り上がる。
        Assert.Equal((9, 30), TournamentDate.AddDays(9, 12, 18));
        Assert.Equal((10, 1), TournamentDate.AddDays(9, 12, 19));
    }

    /// <summary>2か月ぶん跨いでも壊れない（保険。実際の大会は最長27日）。</summary>
    [Fact]
    public void RealDate_RollsOverMultipleMonths()
        => Assert.Equal((9, 9), TournamentDate.AddDays(7, 10, 61));

    /// <summary>A案の夏（8ラウンド18日）は7月中に収まる＝決勝が7月28日。</summary>
    [Fact]
    public void SummerFinal_StaysInJuly()
        => Assert.Equal((7, 28), TournamentDate.DateOf(S, TournamentKind.Summer, S.FinalDay(8)));

    /// <summary>表示文字列の書式（画面ごとに組み立てない）。</summary>
    [Fact]
    public void Label_IsMonthAndDay()
        => Assert.Equal("7月13日", TournamentDate.Label(S, TournamentKind.Summer, 3));
}
