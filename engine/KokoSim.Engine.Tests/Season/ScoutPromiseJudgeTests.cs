using System.Collections.Generic;
using System.Linq;
using KokoSim.Engine.Match.Field;
using KokoSim.Engine.Match.Game;
using KokoSim.Engine.Players;
using KokoSim.Engine.Season;
using Xunit;

namespace KokoSim.Engine.Tests.Season;

/// <summary>
/// 入部の条件（約束）の判定（設計書04 §2.4）。公式戦の記録から「守り切った／破った」が決まること、
/// 破りは1回だけであること、怪我の試合は数えないこと、を固定する。
/// </summary>
public sealed class ScoutPromiseJudgeTests
{
    private const int Day = 5000;

    // ── 起用の約束 ─────────────────────────────────────────

    [Fact]
    public void Regular_BrokenWhenBenched_OnlyOnce()
    {
        var p = Promised(ScoutPromiseKind.Regular);
        var first = Judge(p, Batting(), Pitching());
        Assert.Single(first);
        Assert.False(first[0].Kept);
        Assert.Equal(ScoutPromiseStatus.Broken, p.PromiseStatus);

        Assert.Empty(Judge(p, Batting(), Pitching()));   // 2試合目は何も起きない
    }

    [Fact]
    public void Regular_FineWhenStarting_OrWhenInjured_OrAfterFirstYear()
    {
        var starter = Promised(ScoutPromiseKind.Regular);
        Assert.Empty(Judge(starter, Batting(Start(starter, 7)), Pitching()));

        var dhPitcher = Promised(ScoutPromiseKind.Regular);   // DHで打順の外に居る先発投手もスタメン
        Assert.Empty(Judge(dhPitcher, Batting(), Pitching(dhPitcher)));

        var hurt = Promised(ScoutPromiseKind.Regular);
        hurt.Injury = InjurySeverity.Moderate;
        Assert.Empty(Judge(hurt, Batting(), Pitching()));

        var second = Promised(ScoutPromiseKind.Regular);
        second.Grade = 2;
        Assert.Empty(Judge(second, Batting(), Pitching()));
    }

    [Fact]
    public void Regular_NotJudgedOutsideSummerAndAutumn()
    {
        var p = Promised(ScoutPromiseKind.Regular);
        Assert.Empty(Judge(p, Batting(), Pitching(), TournamentKind.SenbatsuKoshien));
        Assert.Single(Judge(p, Batting(), Pitching(), TournamentKind.Regional));
    }

    [Theory]
    [InlineData(4, true)]
    [InlineData(3, false)]
    [InlineData(0, false)]
    public void Cleanup_NeedsTheFourthSpot(int order, bool fine)
    {
        var p = Promised(ScoutPromiseKind.Cleanup);
        var got = Judge(p, order > 0 ? Batting(Start(p, order)) : Batting(), Pitching());
        Assert.Equal(fine, got.Count == 0);
    }

    [Theory]
    [InlineData(1, true)]
    [InlineData(5, true)]
    [InlineData(6, false)]
    public void TopOfOrder_NeedsFirstToFifth(int order, bool fine)
    {
        var p = Promised(ScoutPromiseKind.TopOfOrder);
        Assert.Equal(fine, Judge(p, Batting(Start(p, order)), Pitching()).Count == 0);
    }

    // ── 果たす約束 ─────────────────────────────────────────

    [Fact]
    public void PlayAsFreshman_KeptOnFirstAppearance_EvenAsASub()
    {
        var p = Promised(ScoutPromiseKind.PlayAsFreshman);
        Assert.Empty(Judge(p, Batting(), Pitching()));
        var got = Judge(p, Batting(Sub(p, 9, BoxEntry.DefensiveSub, "先輩")), Pitching());
        Assert.True(Assert.Single(got).Kept);
    }

    [Fact]
    public void PlayAsFreshman_AndStartingPitcher_BrokenAtYearEndIfNeverDone()
    {
        var a = Promised(ScoutPromiseKind.PlayAsFreshman);
        var b = Promised(ScoutPromiseKind.StartingPitcher);
        var got = ScoutPromiseJudge.AtYearEnd(new[] { a, b });
        Assert.Equal(2, got.Count);
        Assert.All(got, o => Assert.False(o.Kept));
    }

    [Fact]
    public void StartingPitcher_KeptOnlyWhenHeStarts()
    {
        var p = Promised(ScoutPromiseKind.StartingPitcher);
        var other = new DevelopingPlayer { Id = 99, Name = "先発" };
        Assert.Empty(Judge(p, Batting(), Pitching(other, p)));        // 救援では果たせない
        Assert.True(Assert.Single(Judge(p, Batting(), Pitching(p))).Kept);
    }

    // ── 避ける約束 ─────────────────────────────────────────

    [Fact]
    public void RestFirstSummer_BrokenByASummerAppearance_KeptOnceAutumnComes()
    {
        var used = Promised(ScoutPromiseKind.RestFirstSummer);
        Assert.False(Assert.Single(Judge(used, Batting(Sub(used, 9, BoxEntry.PinchRun, "先輩")), Pitching())).Kept);

        var rested = Promised(ScoutPromiseKind.RestFirstSummer);
        Assert.Empty(Judge(rested, Batting(), Pitching()));
        Assert.True(Assert.Single(Judge(rested, Batting(), Pitching(), TournamentKind.Autumn)).Kept);
    }

    [Fact]
    public void NoPinchHitter_BrokenWhenAStarterIsPinchHitFor()
    {
        var p = Promised(ScoutPromiseKind.NoPinchHitter);
        var sub = new DevelopingPlayer { Id = 50, Name = "代打" };
        Assert.Empty(Judge(p, Batting(Start(p, 6)), Pitching()));
        Assert.False(Assert.Single(
            Judge(p, Batting(Start(p, 6), Sub(sub, 6, BoxEntry.PinchHit, p.Name)), Pitching())).Kept);
    }

    [Fact]
    public void NoPinchHitter_NotBrokenByADefensiveSub()
    {
        var p = Promised(ScoutPromiseKind.NoPinchHitter);
        var sub = new DevelopingPlayer { Id = 50, Name = "守備固め" };
        Assert.Empty(Judge(p, Batting(Start(p, 6), Sub(sub, 6, BoxEntry.DefensiveSub, p.Name)), Pitching()));
    }

    [Fact]
    public void NoBunt_BrokenByASacrificeHit()
    {
        var p = Promised(ScoutPromiseKind.NoBunt);
        Assert.Empty(Judge(p, Batting(Start(p, 3)), Pitching()));
        Assert.False(Assert.Single(Judge(p, Batting(Start(p, 3) with { SacrificeHits = 1 }), Pitching())).Kept);
    }

    [Fact]
    public void NoConsecutiveDays_BrokenByPitchingTheNextDay_NotByADayOff()
    {
        var p = Promised(ScoutPromiseKind.NoConsecutiveDays);
        p.Grade = 2;   // 在学中ずっと続く
        Assert.Empty(Judge(p, Batting(), Pitching(p), stamp: Day));
        Assert.Empty(Judge(p, Batting(), Pitching(p), stamp: Day + 2));
        Assert.False(Assert.Single(Judge(p, Batting(), Pitching(p), stamp: Day + 3)).Kept);
    }

    [Fact]
    public void PitchDayStamp_IsConsecutiveOnlyWithinTheSameTournament()
    {
        var a = ScoutPromiseJudge.PitchDayStamp(3, TournamentKind.Summer, 4);
        Assert.Equal(a + 1, ScoutPromiseJudge.PitchDayStamp(3, TournamentKind.Summer, 5));
        Assert.True(System.Math.Abs(ScoutPromiseJudge.PitchDayStamp(3, TournamentKind.SummerKoshien, 0) - a) > 1);
        Assert.True(System.Math.Abs(ScoutPromiseJudge.PitchDayStamp(4, TournamentKind.Summer, 5) - a) > 1);
        Assert.NotEqual(0, ScoutPromiseJudge.PitchDayStamp(0, TournamentKind.Summer, 0));
    }

    // ── 節目の約束 ─────────────────────────────────────────

    [Fact]
    public void YearEnd_KeepsWhatWasNotBroken_AndGraduatesTheRest()
    {
        var regular = Promised(ScoutPromiseKind.Regular);
        var graduating = Promised(ScoutPromiseKind.NoConsecutiveDays);
        graduating.Grade = 3;
        var midway = Promised(ScoutPromiseKind.NoConsecutiveDays);   // 1年の連投の約束は続く
        var got = ScoutPromiseJudge.AtYearEnd(new[] { regular, graduating, midway });

        Assert.Equal(2, got.Count);
        Assert.All(got, o => Assert.True(o.Kept));
        Assert.Equal(ScoutPromiseStatus.Active, midway.PromiseStatus);
    }

    [Theory]
    [InlineData(9, true)]
    [InlineData(10, false)]
    [InlineData(0, false)]
    public void SingleDigitNumber_DecidedWhenNumbersAreSet(int number, bool kept)
    {
        var p = Promised(ScoutPromiseKind.SingleDigitNumber);
        p.UniformNumber = number;
        Assert.Equal(kept, Assert.Single(ScoutPromiseJudge.AfterNumbersDecided(new[] { p })).Kept);
    }

    [Fact]
    public void Captain_DecidedInTheSecondYear_NotBefore()
    {
        var p = Promised(ScoutPromiseKind.Captain);
        Assert.Empty(ScoutPromiseJudge.AfterCaptainNamed(new[] { p }));   // 1年の秋はまだ

        p.Grade = 2;
        Assert.False(Assert.Single(ScoutPromiseJudge.AfterCaptainNamed(new[] { p })).Kept);

        var named = Promised(ScoutPromiseKind.Captain);
        named.Grade = 2;
        named.IsCaptain = true;
        Assert.True(Assert.Single(ScoutPromiseJudge.AfterCaptainNamed(new[] { named })).Kept);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Dorm_DecidedAtJoin(bool hasDorm)
    {
        var p = Promised(ScoutPromiseKind.Dorm);
        Assert.Equal(hasDorm, Assert.Single(ScoutPromiseJudge.AtJoin(new[] { p }, hasDorm)).Kept);
    }

    // ── オーダーを組む時点の見張り ─────────────────────────────

    [Fact]
    public void WouldBreak_FlagsTheOrderBeforeTheGame_WithoutChangingState()
    {
        var cleanup = Promised(ScoutPromiseKind.Cleanup);
        var rest = Promised(ScoutPromiseKind.RestFirstSummer);
        rest.Id = 2;
        var arm = Promised(ScoutPromiseKind.NoConsecutiveDays);
        arm.Id = 3;
        arm.LastOfficialPitchStamp = Day;
        var roster = new[] { cleanup, rest, arm };

        var order = Enumerable.Range(0, 9).Select(i => new DevelopingPlayer { Id = 100 + i, Name = "部員" + i }).ToList();
        order[5] = cleanup;   // 6番
        order[8] = rest;      // 夏に出す

        var bad = ScoutPromiseJudge.WouldBreak(roster, order, arm, TournamentKind.Summer, Day + 1);
        Assert.Equal(3, bad.Count);
        Assert.All(roster, p => Assert.Equal(ScoutPromiseStatus.Active, p.PromiseStatus));

        order[5] = order[3];
        order[3] = cleanup;                                   // 4番へ
        order[8] = new DevelopingPlayer { Id = 200, Name = "代わり" };
        Assert.Empty(ScoutPromiseJudge.WouldBreak(roster, order, null, TournamentKind.Summer, Day + 1));
    }

    // ── 試合中の交代の見張り ─────────────────────────────────

    [Fact]
    public void BreaksOnEntry_RestFirstSummer_OnlyInSummer()
    {
        var p = Promised(ScoutPromiseKind.RestFirstSummer);
        Assert.True(ScoutPromiseJudge.BreaksOnEntry(p, pitches: false, TournamentKind.Summer, Day));
        Assert.False(ScoutPromiseJudge.BreaksOnEntry(p, pitches: false, TournamentKind.Autumn, Day));
    }

    [Fact]
    public void BreaksOnEntry_NoConsecutiveDays_OnlyWhenPitchingTheNextDay()
    {
        var p = Promised(ScoutPromiseKind.NoConsecutiveDays);
        p.LastOfficialPitchStamp = Day;
        Assert.True(ScoutPromiseJudge.BreaksOnEntry(p, pitches: true, TournamentKind.Summer, Day + 1));
        Assert.False(ScoutPromiseJudge.BreaksOnEntry(p, pitches: false, TournamentKind.Summer, Day + 1));   // 守備固めは投げない
        Assert.False(ScoutPromiseJudge.BreaksOnEntry(p, pitches: true, TournamentKind.Summer, Day + 2));
    }

    [Fact]
    public void BreaksWhenPinchHitFor_MatchesTheGameJudgement()
    {
        var p = Promised(ScoutPromiseKind.NoPinchHitter);
        Assert.True(ScoutPromiseJudge.BreaksWhenPinchHitFor(p, TournamentKind.Summer));
        Assert.False(ScoutPromiseJudge.BreaksWhenPinchHitFor(p, TournamentKind.SenbatsuKoshien));
        p.PromiseStatus = ScoutPromiseStatus.Broken;
        Assert.False(ScoutPromiseJudge.BreaksWhenPinchHitFor(p, TournamentKind.Summer));
    }

    // ── 補助 ────────────────────────────────────────────────

    private static DevelopingPlayer Promised(ScoutPromiseKind kind) => new()
    {
        Id = 1, Name = "新人", Grade = 1, Promise = kind, PromiseStatus = ScoutPromiseStatus.Active,
    };

    private static IReadOnlyList<ScoutPromiseOutcome> Judge(
        DevelopingPlayer p, IReadOnlyList<BattingLine> batting, IReadOnlyList<PitchingLine> pitching,
        TournamentKind kind = TournamentKind.Summer, int stamp = Day)
        => ScoutPromiseJudge.AfterOfficialGame(new[] { p }, batting, pitching, kind, stamp);

    private static IReadOnlyList<BattingLine> Batting(params BattingLine[] lines) => lines;

    private static BattingLine Start(DevelopingPlayer p, int order)
        => new(order, FieldPosition.FirstBase, p.Name, 4, 4, 1, 0, 0, 0, 0, 0, 0, p.Id);

    private static BattingLine Sub(DevelopingPlayer p, int order, BoxEntry entry, string forName)
        => new BattingLine(order, FieldPosition.FirstBase, p.Name, 1, 1, 0, 0, 0, 0, 0, 0, 0, p.Id)
        {
            Entry = entry, EnteredFor = forName,
        };

    private static IReadOnlyList<PitchingLine> Pitching(params DevelopingPlayer[] pitchers)
        => pitchers.Select(p => new PitchingLine(p.Name, 9, 30, 5, 2, 6, 2, 100, p.Id)).ToList();
}
