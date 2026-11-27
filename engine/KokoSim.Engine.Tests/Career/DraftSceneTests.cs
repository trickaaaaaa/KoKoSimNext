using System;
using System.Collections.Generic;
using System.Linq;
using KokoSim.Engine.Career.Draft;
using KokoSim.Engine.Nation.Tournaments;
using KokoSim.Engine.Season;
using Xunit;

namespace KokoSim.Engine.Tests.Career;

/// <summary>
/// ドラフトの場面を10月4週目に固定し、1場面にまとめる（issue #1731）。
/// 暦の入口は2つ（共有週が入る／大会の日送りで実月日が入る）で、どちらから来ても年に1回だけ。
/// </summary>
public sealed class DraftSceneTests
{
    private static readonly SeasonCalendar Cal = new();
    private static readonly TournamentSchedule Schedule = new();

    private static DraftNominationGate NewGate() => new(Cal, Schedule);

    private static DraftPick Pick(int id, string name, bool nominated, int round = 0)
        => new(id, name, 60.0, DraftRankBand.UpperRound, nominated, round);

    // ===== 時期（暦の入口と二重防止） =====

    /// <summary>通常の週送り: 第28週に入った回だけ通り、同じ年に二度は通らない。</summary>
    [Fact]
    public void WeekEntered_ClaimsOnlyOnDraftWeek_AndOnlyOncePerYear()
    {
        var gate = NewGate();
        for (var w = 0; w < Cal.WeeksPerYear; w++)
        {
            var claimed = gate.TryClaimOnWeekEntered(1, w);
            Assert.Equal(w == Cal.DraftWeek, claimed);
        }
        Assert.False(gate.TryClaimOnWeekEntered(1, Cal.DraftWeek));   // 同じ年度の二度目
        Assert.True(gate.TryClaimOnWeekEntered(2, Cal.DraftWeek));    // 翌年度はまた通る
    }

    /// <summary>
    /// 大会モードのまま第28週に当たる日を迎えると、その日に1回だけ通る（オーナー決定 2026-11-27 (a)）。
    /// 実月日が10月22日（10月4週目の初日）になった日。以降の日も、大会の後始末で週が進んでも二重に通らない。
    /// </summary>
    [Fact]
    public void TournamentDay_ClaimsOnTheFirstDayOfOctoberFourthWeek_AndNotAgainAfterCleanup()
    {
        var gate = NewGate();
        var claimedDays = new List<int>();
        for (var day = 0; day <= 60; day++)
            if (gate.TryClaimOnTournamentDay(1, TournamentKind.Autumn, day)) claimedDays.Add(day);

        var only = Assert.Single(claimedDays);
        Assert.Equal("10月22日", TournamentDate.Label(Schedule, TournamentKind.Autumn, only));

        // 大会の後始末で共有週が1週ずつ進み、第28週へ入っても二重にならない。
        for (var w = 23; w <= 30; w++)
            Assert.False(gate.TryClaimOnWeekEntered(1, w));
    }

    /// <summary>自校が早く敗退して第28週に入る前に大会モードを抜けた年は、後始末の週送りが第28週で1回だけ通す。</summary>
    [Fact]
    public void EarlyEliminated_CleanupWeekAdvance_ClaimsOnceAtDraftWeek()
    {
        var gate = NewGate();
        // 9月下旬までで敗退（10月22日に届かない）。
        for (var day = 0; day <= 18; day++)
            Assert.False(gate.TryClaimOnTournamentDay(1, TournamentKind.Autumn, day));

        var claims = 0;
        for (var w = 23; w <= 30; w++)
            if (gate.TryClaimOnWeekEntered(1, w)) claims++;
        Assert.Equal(1, claims);
    }

    /// <summary>10月18日開幕の地区大会も、4日目（10月22日）に通る。</summary>
    [Fact]
    public void RegionalTournament_ClaimsOnItsFourthDay()
    {
        var gate = NewGate();
        var day = Enumerable.Range(0, 30).First(d => gate.TryClaimOnTournamentDay(1, TournamentKind.Regional, d));
        Assert.Equal("10月22日", TournamentDate.Label(Schedule, TournamentKind.Regional, day));
    }

    /// <summary>後の時期の大会（11月の神宮・3月のセンバツ・夏）の日送りでは、指名を起こさない（読み込み直後の誤発火も防ぐ）。</summary>
    [Theory]
    [InlineData(TournamentKind.Jingu)]
    [InlineData(TournamentKind.SenbatsuKoshien)]
    [InlineData(TournamentKind.Summer)]
    [InlineData(TournamentKind.SummerKoshien)]
    public void OtherSeasonTournaments_NeverClaim(TournamentKind kind)
    {
        var gate = NewGate();
        for (var day = 0; day <= 40; day++)
            Assert.False(gate.TryClaimOnTournamentDay(1, kind, day));
    }

    /// <summary>確定済みの年度を復元すると（セーブ→ロード）、同じ年度の大会中の日送りで二度目が通らない。</summary>
    [Fact]
    public void RestoredLastYear_BlocksADoubleNominationAfterLoad()
    {
        var gate = NewGate();
        gate.LastYearIndex = 3;
        for (var day = 0; day <= 60; day++)
            Assert.False(gate.TryClaimOnTournamentDay(3, TournamentKind.Autumn, day));
        Assert.False(gate.TryClaimOnWeekEntered(3, Cal.DraftWeek));
    }

    // ===== 1つの場面にまとめる =====

    [Fact]
    public void Shape_CoversEveryCombination()
    {
        Assert.Equal(DraftSceneShape.NoCandidates, DraftScene.ShapeOf(new DraftResult(1, Array.Empty<DraftPick>())));
        Assert.Equal(DraftSceneShape.AllNominated,
            DraftScene.ShapeOf(new DraftResult(1, new[] { Pick(1, "A", true, 2), Pick(2, "B", true, 4) })));
        Assert.Equal(DraftSceneShape.AllMissed,
            DraftScene.ShapeOf(new DraftResult(1, new[] { Pick(1, "A", false), Pick(2, "B", false) })));
        Assert.Equal(DraftSceneShape.Mixed,
            DraftScene.ShapeOf(new DraftResult(1, new[] { Pick(1, "A", false), Pick(2, "B", true, 3) })));
    }

    /// <summary>指名・指名漏れが何人でも場面は1つ（場面IDは1つ）で、結果の行には全員ぶんが並ぶ。</summary>
    [Fact]
    public void ManyPicks_AreOneScene_WithEveryoneInTheResultLine()
    {
        var picks = new[]
        {
            Pick(1, "田中", true, 4), Pick(2, "佐藤", false), Pick(3, "鈴木", true, 2), Pick(4, "高橋", false),
        };
        var content = DraftScene.Build(new DraftResult(1, picks), Array.Empty<(DraftPick, double)>());

        Assert.Equal(DraftScene.MixedEventId, content.EventId);
        foreach (var p in picks) Assert.Contains(p.Name, content.ResultSentence);
        // 指名者は順位順（鈴木2位 → 田中4位）→ 漏れ。
        Assert.True(content.ResultSentence.IndexOf("鈴木", StringComparison.Ordinal)
                    < content.ResultSentence.IndexOf("田中", StringComparison.Ordinal));
        Assert.Contains("鈴木 NPB 2位指名！", content.ResultSentence);
        Assert.Contains("佐藤 指名なし", content.ResultSentence);
        Assert.Equal("鈴木、田中", content.Values["drafted"]);
        Assert.Equal("佐藤、高橋", content.Values["missed"]);
        Assert.Equal("鈴木", content.LeadName);
        Assert.True(content.ResultIsGood);
    }

    [Fact]
    public void AllMissed_IsABadResult_WithoutDonation()
    {
        var content = DraftScene.Build(
            new DraftResult(1, new[] { Pick(1, "佐藤", false) }), Array.Empty<(DraftPick, double)>());
        Assert.Equal(DraftScene.AllMissedEventId, content.EventId);
        Assert.Equal("佐藤 指名なし", content.ResultSentence);
        Assert.False(content.ResultIsGood);
        Assert.Equal("佐藤", content.LeadName);
    }

    /// <summary>候補なしの年は、候補なしの短い場面だけ（結果の行も無い）。</summary>
    [Fact]
    public void NoCandidates_IsTheShortSceneOnly()
    {
        var content = DraftScene.Build(new DraftResult(1, Array.Empty<DraftPick>()), Array.Empty<(DraftPick, double)>());
        Assert.Equal(DraftScene.NoCandidatesEventId, content.EventId);
        Assert.Equal("", content.ResultSentence);
        Assert.Equal("", content.LeadName);
    }

    /// <summary>
    /// 指名ありの年は、OB会の祝い金（部費）が同じ場面の結果の行に入る＝別の場面（draft_ob_greeting）は積まれない。
    /// 場面IDは型の1つだけで、祝い金の専用IDは存在しない。
    /// </summary>
    [Fact]
    public void Donation_GoesIntoTheSameResultLine_NotASeparateScene()
    {
        var a = Pick(1, "田中", true, 1);
        var b = Pick(2, "鈴木", true, 3);
        var result = new DraftResult(1, new[] { a, b });
        var donations = new List<(DraftPick, double)> { (a, 100.0), (b, 70.0) };

        var content = DraftScene.Build(result, donations);

        Assert.EndsWith("部費 ＋170 万", content.ResultSentence);
        Assert.Contains("田中 NPB 1位指名！", content.ResultSentence);
        Assert.Equal(DraftScene.AllNominatedEventId, content.EventId);
        Assert.DoesNotContain("draft_ob_greeting", DraftScene.AllEventIds);
    }

    /// <summary>祝い金が無い（指名者がいない／0万）ときは、結果の行に部費を書かない。</summary>
    [Fact]
    public void NoDonation_NoFundsPart()
    {
        var content = DraftScene.Build(
            new DraftResult(1, new[] { Pick(1, "田中", true, 5) }), Array.Empty<(DraftPick, double)>());
        Assert.DoesNotContain("部費", content.ResultSentence);
    }
}
