using System.Collections.Generic;
using System.Linq;
using KokoSim.Engine.Core;
using KokoSim.Engine.Match.Game;
using KokoSim.Engine.Nation;
using KokoSim.Engine.Nation.Roster;
using KokoSim.Engine.Nation.Tournaments;
using Xunit;

namespace KokoSim.Engine.Tests.Nation;

/// <summary>
/// 大会を通した日程の統合回帰（issue #583）。
///
/// <para><b>なぜ要るか</b>: 既存の Heavy 帯（<c>games_10k</c> 等）は <see cref="GameEngine"/> を直接回すだけで
/// <see cref="TournamentRunner"/> を1本も通らない。つまり<b>日程・球数台帳の日ウィンドウは帯が測っていない</b>。
/// 日程表モデル（issue #583）は試合日を全面的に変えるので、大会レベルの安全網をここで張る。</para>
///
/// <para>測るのは「日程表が実際の進行にそのまま出ているか」＝<b>試合が行われた日の集合</b>。
/// 投手ごとの登板間隔は測れない（球数台帳の <c>ForPlayer</c> キーが校内選手IDで、
/// AI校をまたいで衝突している＝別issue。<see cref="TournamentPitchLedger"/> 参照）。</para>
/// </summary>
public sealed class TournamentScheduleIntegrationTests
{
    private const int Schools = 64;      // 6ラウンド＝中規模県（63試合）
    private const int Rounds = 6;

    private static readonly TournamentSchedule Schedule = new();

    /// <summary>1大会をフルシムで通し、台帳に残った試合日を返す。</summary>
    private static List<int> RunTournamentDays(ulong seed)
    {
        var schools = new List<School>();
        for (var i = 0; i < Schools; i++)
        {
            schools.Add(new School
            {
                Id = 900 + i,
                Name = $"校{900 + i}",
                PrefectureId = 13,
                Strength = 35 + i % 30,   // Tier は Strength から導出される（読み取り専用）
            });
        }

        var rosters = new NationRosters(new AiRosterDeps());
        var runner = new TournamentRunner(
            schools, schools[0], new NationCoefficients(), new Xoshiro256Random(seed), Schedule,
            "テスト県大会", playerResolver: null,
            backgroundResolver: new BackgroundMatchResolver(rosters, new GameContext(), yearIndex: 1));

        // 自校カードも背景フルシムで解決される（playerResolver 無し）＝全国AI校と同じ経路。
        while (!runner.Finished) runner.PlayNextPlayerMatch();
        runner.DrainBracketResolution();

        return runner.CaptureState().PitchLedger.Select(o => o.MatchDay).ToList();
    }

    /// <summary>日程表が予告する「試合のある日」の集合（ラウンドごとの分散を畳んだもの）。</summary>
    private static SortedSet<int> ExpectedDays()
    {
        var days = new SortedSet<int>();
        for (var round = 0; round < Rounds; round++)
        {
            var cards = 1 << (Rounds - round - 1);
            for (var slot = 0; slot < cards; slot++) days.Add(Schedule.MatchDay(Rounds, round, slot, cards));
        }
        return days;
    }

    /// <summary>
    /// 実際に試合が行われた日が日程表と<b>完全に一致</b>する（余分な日も欠けた日も無い）。
    /// ランナーの5箇所の日参照が1つの表から引かれていることの端から端までの確認。
    /// </summary>
    [Fact]
    [Trait("Category", "Heavy")]
    public void PlayedDays_MatchTheScheduleExactly()
    {
        var expected = ExpectedDays();
        foreach (var seed in new ulong[] { 11, 22, 33 })
        {
            var actual = new SortedSet<int>(RunTournamentDays(seed));
            Assert.Equal(expected, actual);
        }
    }

    /// <summary>
    /// 準々決勝の翌日と準決勝の翌日は<b>1試合も行われない</b>（休養日・確定事項2）。
    /// 6ラウンドなら9日目と11日目。
    /// </summary>
    [Fact]
    [Trait("Category", "Heavy")]
    public void RestDays_HaveNoGamesAtAll()
    {
        var quarterfinalDay = Schedule.RoundStartDay(Rounds, Rounds - 3);
        var semifinalDay = Schedule.RoundStartDay(Rounds, Rounds - 2);
        foreach (var seed in new ulong[] { 11, 22, 33 })
        {
            var days = RunTournamentDays(seed);
            Assert.DoesNotContain(quarterfinalDay + 1, days);
            Assert.DoesNotContain(semifinalDay + 1, days);
        }
    }

    /// <summary>大会の総日数が日程表どおり（6ラウンド＝初戦1日目・決勝12日目）。実プレイの暦消費に直結する。</summary>
    [Fact]
    [Trait("Category", "Heavy")]
    public void TournamentSpansTheScheduledNumberOfDays()
    {
        var days = RunTournamentDays(11);
        Assert.Equal(Schedule.FirstRoundDay, days.Min());
        Assert.Equal(Schedule.FinalDay(Rounds), days.Max());
    }

    /// <summary>1回戦は数日に分散する（確定事項2）＝初日だけで消化されない。</summary>
    [Fact]
    [Trait("Category", "Heavy")]
    public void FirstRound_IsActuallySpreadInAFullRun()
    {
        var days = RunTournamentDays(11);
        var firstRoundDays = days.Where(d => d < Schedule.RoundStartDay(Rounds, 1)).Distinct().ToList();
        Assert.Equal(Schedule.SpreadDaysFor(Rounds), firstRoundDays.Count);
    }

    /// <summary>同じシードなら同じ日程・同じ登板列（不変条件#2）。日程はカード添字から決まる純関数。</summary>
    [Fact]
    [Trait("Category", "Heavy")]
    public void SameSeed_ProducesSameDays()
    {
        var a = RunTournamentDays(77);
        var b = RunTournamentDays(77);
        Assert.Equal(a, b);
    }
}
