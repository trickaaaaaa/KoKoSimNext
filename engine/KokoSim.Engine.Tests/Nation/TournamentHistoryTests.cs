using System.Collections.Generic;
using System.Linq;
using KokoSim.Engine.Nation;
using KokoSim.Engine.Nation.Tournaments;
using KokoSim.Engine.Season;
using Xunit;

namespace KokoSim.Engine.Tests.Nation;

/// <summary>
/// 歴代大会結果（issue #600・設計書06 §3.9）。<see cref="SchoolRecordBook"/> が累計へ畳んで捨てる
/// 「年度ごとの1行」を残す器。乱数を消費しない＝統計帯は不変。
/// </summary>
public sealed class TournamentHistoryTests
{
    private const int ManagerId = 999;

    /// <summary>ブラケット1試合ぶん。roundsRemaining=1 が決勝。</summary>
    private static BracketMatch M(string roundName, int roundsRemaining,
        int winnerId, string winnerName, int loserId, string loserName, bool managerInvolved)
        => new(roundName, roundsRemaining, winnerName, loserName, winnerId, loserId, 5, 2, managerInvolved);

    /// <summary>自校が「3回戦で敗退」した4ラウンドの大会（決勝まで消化済み）。</summary>
    private static List<BracketMatch> LostInThirdRound() => new()
    {
        M("1回戦", 4, ManagerId, "桜丘", 11, "A校", true),
        M("2回戦", 3, ManagerId, "桜丘", 12, "B校", true),
        M("3回戦", 2, 13, "C校", ManagerId, "桜丘", true),
        M("決勝", 1, 20, "弘学学園", 13, "C校", false),
    };

    /// <summary>自校が優勝した3ラウンドの大会。</summary>
    private static List<BracketMatch> WonEverything() => new()
    {
        M("1回戦", 3, ManagerId, "桜丘", 11, "A校", true),
        M("準決勝", 2, ManagerId, "桜丘", 12, "B校", true),
        M("決勝", 1, ManagerId, "桜丘", 13, "C校", true),
    };

    // ── 到達ラウンド ──────────────────────────────────────

    [Fact]
    public void Fold_TakesTheLastRoundTheManagerPlayedIn()
    {
        var history = new TournamentHistory();
        var e = history.Fold(LostInThirdRound(), TournamentKind.Summer, year: 2,
            ManagerId, new TournamentChampion(20, "弘学学園"));

        Assert.Equal("3回戦", e.ReachedRoundName);
        Assert.False(e.ManagerIsChampion);
        Assert.Equal(20, e.ChampionId);
        Assert.Equal("弘学学園", e.ChampionName);
        Assert.Equal(2, e.Year);
        Assert.Equal(TournamentKind.Summer, e.Kind);
        Assert.Single(history.Entries);
    }

    /// <summary>自校が優勝した大会は、到達ラウンド＝決勝で優勝フラグが立つ。</summary>
    [Fact]
    public void Fold_MarksTheManagerAsChampion()
    {
        var e = new TournamentHistory().Fold(WonEverything(), TournamentKind.Summer, year: 3,
            ManagerId, new TournamentChampion(ManagerId, "桜丘"));

        Assert.True(e.ManagerIsChampion);
        Assert.Equal("決勝", e.ReachedRoundName);
    }

    /// <summary>自校が1試合も関与していない大会（有り得ないが器としては壊れない）。</summary>
    [Fact]
    public void Fold_WithNoManagerCard_LeavesTheRoundEmpty()
    {
        var matches = new List<BracketMatch> { M("決勝", 1, 20, "弘学学園", 13, "C校", false) };
        var e = new TournamentHistory().Fold(matches, TournamentKind.Autumn, year: 1,
            ManagerId, new TournamentChampion(20, "弘学学園"));

        Assert.Equal("", e.ReachedRoundName);
        Assert.Equal("不参加", TournamentResultLabel.For(e));
    }

    /// <summary>決勝が未消化（優勝校が確定していない）でも積める＝優勝校は空欄。</summary>
    [Fact]
    public void Fold_WithoutChampion_KeepsChampionEmpty()
    {
        var e = new TournamentHistory().Fold(LostInThirdRound(), TournamentKind.Summer, year: 1,
            ManagerId, champion: null);

        Assert.Equal(0, e.ChampionId);
        Assert.Equal("", e.ChampionName);
        Assert.False(e.ManagerIsChampion);
    }

    // ── 表示文言 ────────────────────────────────────────

    [Theory]
    [InlineData("3回戦", false, "3回戦敗退")]
    [InlineData("準々決勝", false, "準々決勝敗退")]
    [InlineData("決勝", false, "準優勝")]   // 決勝まで行って優勝していない＝準優勝
    [InlineData("決勝", true, "優勝")]
    [InlineData("", false, "不参加")]
    public void ResultLabel_ReadsTheRoundAndTheTitle(string round, bool champion, string expected)
        => Assert.Equal(expected, TournamentResultLabel.For(round, champion));

    // ── 通算戦績との接続（同じ走査を2箇所に書かない） ─────────────

    /// <summary>
    /// <see cref="SchoolRecordBook.FoldTournament"/> が返す優勝校をそのまま渡せる
    /// （歴代大会結果のために決勝カードを走査し直さない）。
    /// </summary>
    [Fact]
    public void RecordBookFold_ReturnsTheChampionForTheHistory()
    {
        var book = new SchoolRecordBook();
        var matches = LostInThirdRound();
        var champion = book.FoldTournament(matches, TournamentKind.Summer, currentYear: 2);

        Assert.NotNull(champion);
        Assert.Equal(20, champion!.Value.Id);
        Assert.Equal("弘学学園", champion.Value.Name);

        var e = new TournamentHistory().Fold(matches, TournamentKind.Summer, 2, ManagerId, champion);
        Assert.Equal(20, e.ChampionId);
        // 通算戦績も従来どおり積まれている（戻り値化で既存の畳み込みが壊れていない）。
        Assert.Equal(2, book.For(ManagerId).OfficialWins);
        Assert.Equal(1, book.For(ManagerId).OfficialLosses);
    }

    /// <summary>決勝が未消化のブラケットでは優勝校を返さない。</summary>
    [Fact]
    public void RecordBookFold_WithoutFinal_ReturnsNull()
    {
        var matches = new List<BracketMatch> { M("1回戦", 4, ManagerId, "桜丘", 11, "A校", true) };
        Assert.Null(new SchoolRecordBook().FoldTournament(matches, TournamentKind.Summer, 1));
    }

    // ── 積み上げ ────────────────────────────────────────

    /// <summary>年度をまたいで積み上がり、積んだ順（古い順）で読める。</summary>
    [Fact]
    public void Entries_AccumulateInChronologicalOrder()
    {
        var history = new TournamentHistory();
        history.Fold(LostInThirdRound(), TournamentKind.Summer, 1, ManagerId, new TournamentChampion(20, "弘学学園"));
        history.Fold(LostInThirdRound(), TournamentKind.Autumn, 1, ManagerId, new TournamentChampion(20, "弘学学園"));
        history.Fold(WonEverything(), TournamentKind.Summer, 2, ManagerId, new TournamentChampion(ManagerId, "桜丘"));

        Assert.Equal(3, history.Count);
        Assert.Equal(new[] { 1, 1, 2 }, history.Entries.Select(e => e.Year));
        Assert.Equal(
            new[] { TournamentKind.Summer, TournamentKind.Autumn, TournamentKind.Summer },
            history.Entries.Select(e => e.Kind));
        Assert.True(history.Entries[2].ManagerIsChampion);
    }
}
