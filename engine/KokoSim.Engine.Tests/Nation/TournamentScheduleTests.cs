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
        var spread = S.SpreadDays(8, 0);
        Assert.True(spread >= 3);
        Assert.Equal(spread, days.Distinct().Count());     // 全日に割れる
        Assert.Equal(S.FirstRoundDay, days.Min());
        Assert.Equal(S.FirstRoundDay + spread - 1, days.Max());
        Assert.Equal(S.RoundLastDay(8, 0), days.Max());
    }

    /// <summary>各日にほぼ均等にカードが割れる（1日だけ極端に薄い/厚いにならない）。</summary>
    [Fact]
    public void FirstRound_SplitsCardsEvenly()
    {
        var days = DaysOfRound(totalRounds: 8, round: 0);
        var perDay = days.GroupBy(d => d).Select(g => g.Count()).ToList();
        Assert.Equal(S.SpreadDays(8, 0), perDay.Count);
        Assert.True(perDay.Max() - perDay.Min() <= 1, $"割り方が偏っている: {string.Join(",", perDay)}");
    }

    // ── 1日の上限（issue #723） ───────────────────────────────

    /// <summary>出場校が多いほど1回戦にかかる日数が延びる（1日の上限で割った日数より詰めない）。</summary>
    [Theory]
    [InlineData(60, 6, 3)]     // 60校: 1回戦28試合＝分散日数の3日のまま
    [InlineData(140, 8, 3)]    // 140校: 1回戦12試合
    [InlineData(200, 8, 5)]    // 神奈川規模: 1回戦72試合 ÷ 16 → 5日
    public void FirstRound_LengthGrowsWithFieldSize(int field, int totalRounds, int expectedDays)
        => Assert.Equal(expectedDays, S.SpreadDays(totalRounds, 0, field));

    /// <summary>不戦勝の無い満杯の表では、どの日も上限を超えない。</summary>
    [Theory]
    [InlineData(7)]
    [InlineData(8)]
    [InlineData(9)]
    public void NoDay_ExceedsTheDailyCap_WhenTheBracketIsFull(int totalRounds)
    {
        for (var round = 0; round < totalRounds - 3; round++)
        {
            var perDay = DaysOfRound(totalRounds, round).GroupBy(d => d).Select(g => g.Count());
            Assert.True(perDay.Max() <= S.MaxGamesPerDay, $"total={totalRounds} round={round} で上限超え");
        }
    }

    /// <summary>1回戦の実試合数＝出場校数 − 2回戦の枠（不戦勝は1回戦だけ）。</summary>
    [Theory]
    [InlineData(8, 0, 200, 72)]
    [InlineData(8, 1, 200, 64)]
    [InlineData(6, 0, 49, 17)]
    [InlineData(6, 0, 0, 32)]    // 0＝満杯の表
    public void GamesInRound_ExcludesByes(int totalRounds, int round, int field, int expected)
        => Assert.Equal(expected, S.GamesInRound(totalRounds, round, field));

    /// <summary>上限0は従来どおり（残りラウンド数で決まる分散日数だけ）。</summary>
    [Fact]
    public void CapZero_KeepsTheOldSpread()
    {
        var old = S with { MaxGamesPerDay = 0 };
        Assert.Equal(3, old.SpreadDays(8, 0, 200));
        Assert.Equal(18, old.FinalDay(8, 200));
    }

    /// <summary>カードの消化順はラウンド内の全単射（どの日にも均等に割れる前提）。</summary>
    [Fact]
    public void DayOrder_IsAPermutation()
    {
        for (var k = 0; k <= 8; k++)
        {
            var cards = 1 << k;
            for (var round = 0; round < 9; round++)
            {
                var seen = Enumerable.Range(0, cards).Select(i => TournamentSchedule.DayOrder(i, cards, round)).ToHashSet();
                Assert.Equal(cards, seen.Count);
                Assert.All(seen, o => Assert.InRange(o, 0, cards - 1));
            }
        }
    }

    /// <summary>
    /// 勝ち上がった校の次戦までの間隔が毎回同じにならない（issue #723 の症状）。
    /// 神奈川規模（200校・8ラウンド）で、1回戦→2回戦の間隔が何通りにも散る。
    /// </summary>
    [Fact]
    public void GapsToNextGame_Vary()
    {
        const int total = 8, field = 200;
        var firstGaps = new HashSet<int>();
        var pathsWithVaryingGaps = 0;
        for (var card = 0; card < CardsIn(total, 0); card++)
        {
            var days = DaysAlongPath(total, card, field);
            firstGaps.Add(days[1] - days[0]);
            var earlyGaps = Enumerable.Range(1, 4).Select(i => days[i] - days[i - 1]).Distinct().Count();
            if (earlyGaps > 1) pathsWithVaryingGaps++;
        }
        Assert.True(firstGaps.Count >= 4, $"1回戦→2回戦の間隔が {string.Join(",", firstGaps)} しかない");
        Assert.True(pathsWithVaryingGaps > CardsIn(total, 0) * 3 / 4, $"間隔が揃ったままの経路が多い: {pathsWithVaryingGaps}");
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
    /// 出場校数ごとの総日数を固定する。ここが変わる＝投手の実効休養が変わる＝帯が動く。
    /// 〜100校の県は #583 の A案と同じ長さ。大きい県だけ1日の上限（issue #723）で延びる
    /// （神奈川200校: 18日→21日）。
    /// </summary>
    [Theory]
    [InlineData(5, 32, 9)]
    [InlineData(6, 49, 12)]
    [InlineData(6, 60, 12)]
    [InlineData(7, 100, 15)]
    [InlineData(8, 170, 19)]
    [InlineData(8, 200, 21)]
    public void FinalDay_MatchesTheAgreedTable(int totalRounds, int field, int expectedFinalDay)
        => Assert.Equal(expectedFinalDay, S.FinalDay(totalRounds, field));

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

    /// <summary>序盤・中盤・終盤の最低分散日数（残りラウンド数で決まる。1日の上限で延びる分は含まない）。</summary>
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
    private static List<int> DaysAlongPath(int totalRounds, int firstRoundCard, int fieldSize = 0)
    {
        var days = new List<int>();
        var card = firstRoundCard;
        for (var round = 0; round < totalRounds; round++)
        {
            days.Add(S.MatchDay(totalRounds, round, card, CardsIn(totalRounds, round), fieldSize));
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

    /// <summary>最大の県（神奈川200校・8ラウンド21日）でも夏は7月中に収まる＝決勝が7月31日。</summary>
    [Fact]
    public void SummerFinal_StaysInJuly()
        => Assert.Equal((7, 31), TournamentDate.DateOf(S, TournamentKind.Summer, S.FinalDay(8, 200)));

    /// <summary>表示文字列の書式（画面ごとに組み立てない）。</summary>
    [Fact]
    public void Label_IsMonthAndDay()
        => Assert.Equal("7月13日", TournamentDate.Label(S, TournamentKind.Summer, 3));

    /// <summary>夏の甲子園は8月6日開幕（設計書05 §1.1）。県大会・秋とは別の月日を引く。</summary>
    [Fact]
    public void RealDate_Koshien_OpensInAugust()
    {
        Assert.Equal((8, 6), TournamentDate.DateOf(S, TournamentKind.SummerKoshien, 0));
        Assert.Equal("8月13日", TournamentDate.Label(S, TournamentKind.SummerKoshien, 7));
    }

    /// <summary>
    /// 49代表＝6ラウンドの甲子園は全13日で8月中に収まる（8月6日開幕・決勝8月18日）。
    /// 日程表は県大会と同じものを引く（大会ごとに別の日割りを持たない）ので、実際の選手権（約17日）より
    /// 数日短い。大会が消費する週数は最終日から出す（13日＝1週）。
    /// </summary>
    [Fact]
    public void KoshienFinal_StaysInAugust()
    {
        Assert.Equal(12, S.FinalDay(6));
        Assert.Equal((8, 18), TournamentDate.DateOf(S, TournamentKind.SummerKoshien, S.FinalDay(6)));
    }
}
