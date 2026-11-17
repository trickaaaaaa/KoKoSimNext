using System.Collections.Generic;
using KokoSim.Engine.Match.Field;
using KokoSim.Engine.Match.Game;
using KokoSim.Engine.Stats;
using Xunit;

namespace KokoSim.Engine.Tests.Stats;

/// <summary>
/// 転任での個人成績の帰属分離（issue #1112）。<see cref="PlayerStatStore.CloseForTransfer"/> が
/// 「新しい赴任先の記録タブに前任校の選手成績が混ざらない。前任校の成績はそのまま失わない」を満たすことを固定する。
/// </summary>
public sealed class PlayerStatStoreTests
{
    /// <summary>自校=後攻。指定 sourceId の打者が hits 本安打した1試合。</summary>
    private static GameResult ManagerHomeGame(int sourceId, int hits) => new()
    {
        AwayName = "相手", HomeName = "自校", AwayRuns = 0, HomeRuns = hits > 0 ? 1 : 0,
        InningsPlayed = 9, TotalPitches = 0, PitcherChanges = 0,
        HomeBatting = new[]
        {
            new BattingLine(1, FieldPosition.CenterField, "自1", hits + 1, hits + 1, hits, 0, 0, 0, 0, 0, 0,
                SourceId: sourceId),
        },
    };

    private static IReadOnlyDictionary<int, (int Grade, int UniformNumber)> Info(int id, int grade, int number)
        => new Dictionary<int, (int, int)> { [id] = (grade, number) };

    [Fact]
    public void CloseForTransfer_ClearsTheLiveStore_SoTheNewSchoolStartsEmpty()
    {
        var store = new PlayerStatStore();
        store.StartTournament();
        store.FoldGame(ManagerHomeGame(sourceId: 1, hits: 2), managerIsAway: false);
        store.ArchiveCurrentTournament(TournamentSlot.SummerPref, Info(1, grade: 1, number: 7));

        store.CloseForTransfer("前任校", startYearIndex: 1, endYearIndex: 2);

        // 新しい赴任先（＝現本体）は選手1の記録を一切持たない＝校歴代記録に前任校の選手が混ざらない。
        Assert.Null(store.Career.Get(1));
        Assert.Empty(store.Archive.Keys);
        Assert.Null(store.Official.Get(1));
        Assert.Null(store.CurrentTournament.Get(1));
    }

    [Fact]
    public void CloseForTransfer_KeepsThePreviousSchoolsRecordsInClosedSchools()
    {
        var store = new PlayerStatStore();
        store.StartTournament();
        store.FoldGame(ManagerHomeGame(sourceId: 1, hits: 2), managerIsAway: false);
        store.ArchiveCurrentTournament(TournamentSlot.SummerPref, Info(1, grade: 1, number: 7));

        store.CloseForTransfer("前任校", startYearIndex: 1, endYearIndex: 2);

        // 前任校ぶんの成績はそのまま失わない＝ClosedSchools に残る。
        Assert.Single(store.ClosedSchools);
        var closed = store.ClosedSchools[0];
        Assert.Equal("前任校", closed.SchoolName);
        Assert.Equal(1, closed.StartYearIndex);
        Assert.Equal(2, closed.EndYearIndex);
        Assert.Equal(2, closed.Career.Get(1)!.Batting.Hits);
        Assert.NotNull(closed.Archive.Get(new TournamentArchiveKey(1, TournamentSlot.SummerPref), 1));
    }

    [Fact]
    public void CloseForTransfer_NewSchoolAccumulatesIndependently_OfThePreviousSchool()
    {
        var store = new PlayerStatStore();
        store.StartTournament();
        store.FoldGame(ManagerHomeGame(sourceId: 1, hits: 2), managerIsAway: false);
        store.CloseForTransfer("前任校", 1, 2);

        // 転任先では選手2としてプレーする（issue #644: ID は監督のキャリア全体で再利用しない）。
        store.StartTournament();
        store.FoldGame(ManagerHomeGame(sourceId: 2, hits: 3), managerIsAway: false);

        Assert.Equal(3, store.Career.Get(2)!.Batting.Hits);
        Assert.Null(store.Career.Get(1)); // 前任校の選手1は新しい学校の記録に出ない
    }

    // issue #644: 選手IDの採番は監督のキャリア全体で一度しか使わない。退任済みの赴任に移した成績も
    // MaxPlayerId が見ないと、転任後に古いIDへ採番が巻き戻って成績が混ざる。
    [Fact]
    public void MaxPlayerId_StillCountsIdsMovedIntoClosedSchools()
    {
        var store = new PlayerStatStore();
        store.StartTournament();
        store.FoldGame(ManagerHomeGame(sourceId: 5, hits: 1), managerIsAway: false);
        store.ArchiveCurrentTournament(TournamentSlot.SummerPref, Info(5, grade: 1, number: 9));

        store.CloseForTransfer("前任校", 1, 2);

        Assert.Empty(store.Career.Players);         // ライブ帳簿は空
        Assert.Equal(5, store.MaxPlayerId());        // それでも選手5はまだ「使用済みID」として数えられる
    }

    [Fact]
    public void CloseForTransfer_TwiceAccumulatesTwoSeparateClosedSchools()
    {
        var store = new PlayerStatStore();
        store.StartTournament();
        store.FoldGame(ManagerHomeGame(sourceId: 1, hits: 1), managerIsAway: false);
        store.CloseForTransfer("1校目", 1, 1);

        store.StartTournament();
        store.FoldGame(ManagerHomeGame(sourceId: 2, hits: 4), managerIsAway: false);
        store.CloseForTransfer("2校目", 2, 3);

        Assert.Equal(2, store.ClosedSchools.Count);
        Assert.Equal("1校目", store.ClosedSchools[0].SchoolName);
        Assert.Equal(1, store.ClosedSchools[0].Career.Get(1)!.Batting.Hits);
        Assert.Equal("2校目", store.ClosedSchools[1].SchoolName);
        Assert.Equal(4, store.ClosedSchools[1].Career.Get(2)!.Batting.Hits);
    }
}
