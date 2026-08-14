using KokoSim.Engine.Match.Field;
using KokoSim.Engine.Match.Game;
using KokoSim.Engine.Stats;
using Xunit;

namespace KokoSim.Engine.Tests.Stats;

/// <summary>
/// 成績を (校ID, 校内選手ID) で引く seam（<see cref="SchoolStatsLookup"/>）と、全国ストアの
/// 通算スコープ（issue #634）の検証。
///
/// <para>元の不具合: 永続ロスター（#80）以降は相手校の生成選手も SourceId を持つのに、表示側が
/// 「SourceId が非 null＝自校」の前提で自校ストアを引いていた。選手IDは<b>校ごとに1から</b>振るので
/// 相手校の選手に<b>同じ番号の自校選手の成績</b>が出る（その自校選手が未出場なら「—」＝成績が無いように見える）。</para>
/// </summary>
public sealed class SchoolStatsLookupTests
{
    private const int ManagerSchoolId = -1;
    private const int OpponentSchoolId = 100;
    private const int OtherSchoolId = 200;
    private const int SharedPlayerId = 5;   // 両校が持つ選手ID（校をまたいで一意でない）

    /// <summary>自校=後攻・相手校=先攻。両校とも 5番打者の SourceId が 5（＝ID衝突を再現）。</summary>
    private static GameResult Game(int managerHits, int opponentHits) => new()
    {
        AwayName = "相手", HomeName = "自校", AwayRuns = 2, HomeRuns = 5,
        InningsPlayed = 9, TotalPitches = 0, PitcherChanges = 0,
        HomeBatting = new[]
        {
            new BattingLine(5, FieldPosition.CenterField, "自校の5番", 4, 4, managerHits, 0, 0, 0, 0, 0, 0,
                SourceId: SharedPlayerId),
        },
        AwayBatting = new[]
        {
            new BattingLine(5, FieldPosition.LeftField, "相手の5番", 4, 4, opponentHits, 0, 0, 0, 0, 0, 0,
                SourceId: SharedPlayerId),
        },
        HomePitching = new[] { new PitchingLine("自校投", 27, 0, 0, 0, 0, 0, 0, SourceId: 1) },
        AwayPitching = new[] { new PitchingLine("相手投", 24, 0, 0, 0, 0, 0, 0, SourceId: 1) },
    };

    private static (PlayerStatStore Manager, NationTournamentStats Nation, SchoolStatsLookup Lookup) Setup()
    {
        var manager = new PlayerStatStore();
        var nation = new NationTournamentStats();
        return (manager, nation, new SchoolStatsLookup(ManagerSchoolId, manager, nation));
    }

    [Fact]
    public void Career_OpponentPlayer_DoesNotFallBackToManagerStore()
    {
        var (manager, _, lookup) = Setup();
        manager.FoldGame(Game(managerHits: 3, opponentHits: 1), managerIsAway: false);

        // 自校の5番は3安打。相手校の5番（同じ選手ID）は全国ストアに何も無いので null＝「—」。
        Assert.Equal(3, lookup.Career(ManagerSchoolId, SharedPlayerId)!.Batting.Hits);
        Assert.Null(lookup.Career(OpponentSchoolId, SharedPlayerId));
    }

    [Fact]
    public void Career_DispatchesBySchool_WhenBothStoresHaveTheSameId()
    {
        var (manager, nation, lookup) = Setup();
        var g = Game(managerHits: 3, opponentHits: 1);
        manager.FoldGame(g, managerIsAway: false);
        nation.FoldSchool(OpponentSchoolId, g, schoolIsAway: true);

        Assert.Equal(3, lookup.Career(ManagerSchoolId, SharedPlayerId)!.Batting.Hits);
        Assert.Equal(1, lookup.Career(OpponentSchoolId, SharedPlayerId)!.Batting.Hits);
    }

    [Fact]
    public void FoldSchool_FoldsOnlyThatSide()
    {
        var (_, nation, lookup) = Setup();
        nation.FoldSchool(OpponentSchoolId, Game(3, 1), schoolIsAway: true);

        Assert.Equal(1, lookup.Career(OpponentSchoolId, SharedPlayerId)!.Batting.Hits);   // 先攻＝相手校の行
        Assert.Null(lookup.Career(OtherSchoolId, SharedPlayerId));                        // 相手側は積まない
    }

    [Fact]
    public void StartTournament_ClearsCurrentTournament_ButKeepsCareer()
    {
        var (_, nation, lookup) = Setup();
        nation.FoldSchool(OpponentSchoolId, Game(3, 2), schoolIsAway: true);

        Assert.Equal(2, lookup.CurrentTournament(OpponentSchoolId, SharedPlayerId)!.Batting.Hits);
        nation.StartTournament();

        Assert.Null(lookup.CurrentTournament(OpponentSchoolId, SharedPlayerId));
        Assert.Equal(2, lookup.Career(OpponentSchoolId, SharedPlayerId)!.Batting.Hits);
    }

    [Fact]
    public void Career_AccumulatesAcrossTournaments()
    {
        var (_, nation, lookup) = Setup();
        nation.FoldSchool(OpponentSchoolId, Game(3, 2), schoolIsAway: true);
        nation.StartTournament();
        nation.FoldSchool(OpponentSchoolId, Game(3, 1), schoolIsAway: true);

        Assert.Equal(1, lookup.CurrentTournament(OpponentSchoolId, SharedPlayerId)!.Batting.Hits);   // 今大会だけ
        Assert.Equal(3, lookup.Career(OpponentSchoolId, SharedPlayerId)!.Batting.Hits);              // 2+1
    }

    [Fact]
    public void FoldMatch_FoldsBothSidesIntoBothScopes()
    {
        var (_, nation, lookup) = Setup();
        nation.FoldMatch(OpponentSchoolId, OtherSchoolId, Game(3, 1));

        Assert.Equal(1, lookup.CurrentTournament(OpponentSchoolId, SharedPlayerId)!.Batting.Hits);   // away
        Assert.Equal(3, lookup.CurrentTournament(OtherSchoolId, SharedPlayerId)!.Batting.Hits);      // home
        Assert.Equal(1, lookup.Career(OpponentSchoolId, SharedPlayerId)!.Batting.Hits);
        Assert.Equal(3, lookup.Career(OtherSchoolId, SharedPlayerId)!.Batting.Hits);
    }

    [Fact]
    public void UnknownSchoolOrPlayer_IsNull()
    {
        var (manager, _, lookup) = Setup();
        manager.FoldGame(Game(3, 1), managerIsAway: false);

        Assert.Null(lookup.Career(OtherSchoolId, SharedPlayerId));   // 未登録の校
        Assert.Null(lookup.Career(ManagerSchoolId, 999));            // 未登録の選手
        Assert.Null(lookup.Career(ManagerSchoolId, null));           // 選手IDなし
        Assert.Null(lookup.CurrentTournamentBook(OtherSchoolId));
    }

    [Fact]
    public void IsManagerSchool_SeparatesTheTwoStores()
    {
        var (_, _, lookup) = Setup();
        Assert.True(lookup.IsManagerSchool(ManagerSchoolId));
        Assert.False(lookup.IsManagerSchool(OpponentSchoolId));
    }
}
