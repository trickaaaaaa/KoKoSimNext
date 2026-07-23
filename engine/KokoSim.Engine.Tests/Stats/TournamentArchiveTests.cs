using System.Collections.Generic;
using KokoSim.Engine.Match.Field;
using KokoSim.Engine.Match.Game;
using KokoSim.Engine.Stats;
using Xunit;

namespace KokoSim.Engine.Tests.Stats;

/// <summary>大会別アーカイブ（issue #77）: 学年バケツ振り分け・秋合算・当時の背番号スナップショットの検証。</summary>
public sealed class TournamentArchiveTests
{
    /// <summary>自校=後攻。指定 sourceId の打者が hits 本安打・home 生還した1試合。</summary>
    private static GameResult ManagerHomeGame(int sourceId, int hits, int runs) => new()
    {
        AwayName = "相手", HomeName = "自校", AwayRuns = 0, HomeRuns = runs,
        InningsPlayed = 9, TotalPitches = 0, PitcherChanges = 0,
        HomeBatting = new[]
        {
            new BattingLine(1, FieldPosition.CenterField, "自1", hits + 1, hits + 1, hits, 0, 0, 0, 0, 0, 0,
                SourceId: sourceId, Runs: runs),
        },
    };

    private static IReadOnlyDictionary<int, (int Grade, int UniformNumber)> Info(int id, int grade, int number)
        => new Dictionary<int, (int, int)> { [id] = (grade, number) };

    [Fact]
    public void Archive_RoutesPlayerToOwnGradeBucket_WithUniformSnapshot()
    {
        var store = new PlayerStatStore();
        store.StartTournament();
        store.FoldGame(ManagerHomeGame(sourceId: 1, hits: 2, runs: 1), managerIsAway: false);

        store.ArchiveCurrentTournament(TournamentSlot.SummerPref, Info(id: 1, grade: 2, number: 7));

        var g2 = store.Archive.Get(new TournamentArchiveKey(2, TournamentSlot.SummerPref), 1);
        Assert.NotNull(g2);
        Assert.Equal(2, g2!.Batting.Hits);
        Assert.Equal(1, g2.Batting.Runs);
        Assert.Equal(7, g2.UniformNumber);
        // 別学年の枠には入らない。
        Assert.Null(store.Archive.Get(new TournamentArchiveKey(1, TournamentSlot.SummerPref), 1));
    }

    [Fact]
    public void Archive_Autumn_MergesAcrossSubTournaments()
    {
        // 秋は県/地区/神宮を「n年秋」1枠に合算する（issue #77 決定）。
        var store = new PlayerStatStore();

        store.StartTournament();
        store.FoldGame(ManagerHomeGame(sourceId: 1, hits: 2, runs: 1), managerIsAway: false);
        store.ArchiveCurrentTournament(TournamentSlot.Autumn, Info(1, grade: 1, number: 12));

        store.StartTournament(); // 次の秋の大会（地区）で今大会スコープはクリア
        store.FoldGame(ManagerHomeGame(sourceId: 1, hits: 3, runs: 2), managerIsAway: false);
        store.ArchiveCurrentTournament(TournamentSlot.Autumn, Info(1, grade: 1, number: 12));

        var autumn = store.Archive.Get(new TournamentArchiveKey(1, TournamentSlot.Autumn), 1);
        Assert.NotNull(autumn);
        Assert.Equal(5, autumn!.Batting.Hits);  // 2 + 3 合算
        Assert.Equal(3, autumn.Batting.Runs);    // 1 + 2 合算
        Assert.Equal(2, autumn.Batting.Games);
    }

    [Fact]
    public void Archive_KeepsSeparateSlotsAcrossGrades()
    {
        var store = new PlayerStatStore();

        store.StartTournament();
        store.FoldGame(ManagerHomeGame(1, hits: 1, runs: 0), managerIsAway: false);
        store.ArchiveCurrentTournament(TournamentSlot.SummerPref, Info(1, grade: 1, number: 7));

        store.StartTournament();
        store.FoldGame(ManagerHomeGame(1, hits: 4, runs: 3), managerIsAway: false);
        store.ArchiveCurrentTournament(TournamentSlot.SummerPref, Info(1, grade: 2, number: 1)); // 背番号が変わった

        var y1 = store.Archive.Get(new TournamentArchiveKey(1, TournamentSlot.SummerPref), 1);
        var y2 = store.Archive.Get(new TournamentArchiveKey(2, TournamentSlot.SummerPref), 1);
        Assert.Equal(1, y1!.Batting.Hits);
        Assert.Equal(7, y1.UniformNumber);  // 1年夏の当時の背番号
        Assert.Equal(4, y2!.Batting.Hits);
        Assert.Equal(1, y2.UniformNumber);  // 2年夏の当時の背番号
    }

    [Fact]
    public void Archive_SkipsPlayersWithUnknownGrade()
    {
        var store = new PlayerStatStore();
        store.StartTournament();
        store.FoldGame(ManagerHomeGame(sourceId: 1, hits: 2, runs: 1), managerIsAway: false);

        // 学年情報が無い選手はスキップ（枠が作られない）。
        store.ArchiveCurrentTournament(TournamentSlot.SummerPref,
            new Dictionary<int, (int, int)>());

        Assert.Empty(store.Archive.Keys);
    }
}
