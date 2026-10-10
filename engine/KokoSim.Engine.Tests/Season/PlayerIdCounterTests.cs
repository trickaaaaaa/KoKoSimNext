using System.Collections.Generic;
using System.Linq;
using KokoSim.Engine.Match.Field;
using KokoSim.Engine.Match.Game;
using KokoSim.Engine.Players;
using KokoSim.Engine.Saves;
using KokoSim.Engine.Season;
using KokoSim.Engine.Stats;
using Xunit;

namespace KokoSim.Engine.Tests.Season;

/// <summary>
/// issue #644: 選手IDの採番は転任をまたいでも単調増加にする。成績ストアは学校に紐づかず転任後も残るので、
/// 転任で 1 から振り直すと、転任先の新入生に旧校の同じIDの選手の成績が出る。
/// Shell の RosterService は採番をこのクラスにだけ頼る（Build＝AssignSequential／Restore＝Resume）。
/// </summary>
public sealed class PlayerIdCounterTests
{
    private static List<DevelopingPlayer> Players(int n)
        => Enumerable.Range(0, n).Select(i => new DevelopingPlayer { Name = "P" + i, Grade = 1 }).ToList();

    /// <summary>指定IDの選手が1試合出場した成績を積む（通算スコープ）。</summary>
    private static PlayerStatStore StatsFor(IEnumerable<int> ids)
    {
        var store = new PlayerStatStore();
        var lines = ids.Select((id, i) => new BattingLine(
            i + 1, FieldPosition.LeftField, "P" + id, 4, 4, 1, 0, 0, 0, 0, 0, 1, SourceId: id)).ToList();
        store.Career.FoldGame(new GameResult
        {
            AwayName = "A", HomeName = "H", AwayRuns = 0, HomeRuns = 0,
            InningsPlayed = 9, TotalPitches = 0, PitcherChanges = 0,
            HomeBatting = lines,
        }, managerIsAway: false);
        return store;
    }

    [Fact]
    public void AssignSequential_NewGameStartsAtOne()
    {
        var list = Players(3);
        var next = PlayerIdCounter.AssignSequential(list, 0);
        Assert.Equal(new[] { 1, 2, 3 }, list.Select(p => p.Id));
        Assert.Equal(4, next);
    }

    /// <summary>
    /// 再現: 旧校で成績を残した部員（Id 1〜30）がいる状態で転任し、転任先の1年生を作り直す。
    /// 修正前は採番が 1 に戻り、新入生 Id 1〜10 に旧校の成績が出ていた。
    /// </summary>
    [Fact]
    public void Transfer_RebuiltFreshmen_HaveNoInheritedStats()
    {
        var oldSchool = Players(30);
        var next = PlayerIdCounter.AssignSequential(oldSchool, 0);
        var stats = StatsFor(oldSchool.Select(p => p.Id));

        var freshmen = Players(10);
        next = PlayerIdCounter.AssignSequential(freshmen, next);   // 転任：カウンタは戻さない

        Assert.All(freshmen, p => Assert.Null(stats.Career.Get(p.Id)));
        Assert.Equal(41, next);
    }

    /// <summary>
    /// 転任後のセーブ→ロード→年度替わりでも、新入生のIdが過去のどのIdとも衝突しない。
    /// ロスターには旧校の部員がいない（最大Id=40）が、成績ストアには 1〜30 が残る。
    /// </summary>
    [Fact]
    public void Transfer_SaveLoad_ThenNewYear_IdsNeverCollide()
    {
        var oldSchool = Players(30);
        var next = PlayerIdCounter.AssignSequential(oldSchool, 0);
        var freshmen = Players(10);
        next = PlayerIdCounter.AssignSequential(freshmen, next);
        var stats = StatsFor(oldSchool.Select(p => p.Id).Concat(freshmen.Take(2).Select(p => p.Id)));

        var json = SaveCodec.Serialize(new SaveState { Roster = freshmen, Stats = stats, NextPlayerId = next });
        Assert.True(SaveCodec.TryDeserialize(json, out var loaded, out var err), err);
        Assert.Equal(next, loaded.NextPlayerId);

        var resumed = PlayerIdCounter.Resume(loaded.NextPlayerId, loaded.Roster, loaded.Stats);
        var newcomers = Players(8);
        PlayerIdCounter.AssignSequential(newcomers, resumed);

        var used = new HashSet<int>(oldSchool.Select(p => p.Id).Concat(freshmen.Select(p => p.Id)));
        Assert.All(newcomers, p => Assert.DoesNotContain(p.Id, used));
    }

    /// <summary>
    /// 続き番号を持たない旧セーブ（または採番が戻ってしまった転任済みセーブ）は、ロスターと成績ストアの
    /// 最大Idの大きい方の次から再開する＝少なくとも以後の新入生は衝突しない。
    /// </summary>
    [Fact]
    public void Resume_WithoutSavedCounter_UsesMaxOfRosterAndStats()
    {
        var roster = Players(10);
        PlayerIdCounter.AssignSequential(roster, 0);         // 転任済み旧セーブ: ロスターは Id 1〜10
        var stats = StatsFor(Enumerable.Range(1, 30));        // 成績は旧校の 1〜30 が残っている

        Assert.Equal(31, PlayerIdCounter.Resume(0, roster, stats));
        Assert.Equal(11, PlayerIdCounter.Resume(0, roster, new PlayerStatStore()));
        Assert.Equal(50, PlayerIdCounter.Resume(50, roster, stats));   // 保存値が最大ならそれを使う
        Assert.Equal(1, PlayerIdCounter.Resume(0, new List<DevelopingPlayer>(), null));
    }

    [Fact]
    public void MaxPlayerId_SeesEveryScope()
    {
        var store = StatsFor(new[] { 3 });
        Assert.Equal(3, store.MaxPlayerId());
        store.CurrentTournament.FoldGame(new GameResult
        {
            AwayName = "A", HomeName = "H", AwayRuns = 0, HomeRuns = 0,
            InningsPlayed = 9, TotalPitches = 0, PitcherChanges = 0,
            HomeBatting = new[] { new BattingLine(1, FieldPosition.Catcher, "x", 1, 1, 0, 0, 0, 0, 0, 0, 0, SourceId: 77) },
        }, managerIsAway: false);
        store.ArchiveCurrentTournament(TournamentSlot.SummerPref,
            new Dictionary<int, (int, int)> { [77] = (2, 10) });
        store.StartTournament();   // 今大会を消してもアーカイブに残る
        Assert.Equal(77, store.MaxPlayerId());
    }
}
