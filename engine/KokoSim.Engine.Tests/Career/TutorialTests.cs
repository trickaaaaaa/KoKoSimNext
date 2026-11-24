using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using KokoSim.Engine.Career;
using KokoSim.Engine.Saves;
using KokoSim.Engine.Season;
using KokoSim.Engine.Season.Events;
using KokoSim.Engine.Tests.Saves;
using Xunit;

namespace KokoSim.Engine.Tests.Career;

/// <summary>
/// 赴任イベントとチュートリアル（issue #1496・design-04 §3.4）。何を積むかの判定・進み具合・セーブ往復・会話の表。
/// </summary>
public sealed class TutorialTests
{
    private static IEnumerable<TutorialScene> AllScenes()
        => Enum.GetValues(typeof(TutorialScene)).Cast<TutorialScene>();

    // ===== 何を積むか =====

    [Fact]
    public void NewSchoolStart_QueuesNewSchoolArrival_ThenBasics()
    {
        var p = TutorialProgress.Fresh();
        var ids = Tutorial.OnNewSchoolStarted(p);
        Assert.Equal(new[] { Tutorial.ArrivalNewSchoolEventId, Tutorial.EventId(TutorialScene.Basics) }, ids);
        Assert.True(p.HasSeen(TutorialScene.Basics));
    }

    [Fact]
    public void Transfer_QueuesTransferArrivalOnly_NoTutorial()
    {
        var p = TutorialProgress.Fresh();
        var ids = Tutorial.OnTransferred(p);
        Assert.Equal(new[] { Tutorial.ArrivalTransferEventId }, ids);
        Assert.DoesNotContain(ids, Tutorial.IsTutorialEvent);
        Assert.Empty(p.Seen);   // 転任は見た印も付けない（説明を消費しない）
    }

    [Fact]
    public void Scene_IsShownOnce()
    {
        var p = TutorialProgress.Fresh();
        foreach (var scene in AllScenes())
        {
            Assert.Equal(Tutorial.EventId(scene), Tutorial.OnScene(p, scene));
            Assert.Null(Tutorial.OnScene(p, scene));
        }
    }

    // 「初めての試合」は公式戦・練習試合を問わず最初の1試合でだけ出る（2026-11-23 オーナー決定）。
    [Fact]
    public void FirstMatch_PracticeFirst_ThenOfficialOpenerShowsNothing()
    {
        var p = TutorialProgress.Fresh();
        Assert.Equal(Tutorial.EventId(TutorialScene.FirstMatch), Tutorial.BeforeMatch(p, Tutorial.MatchKind.Practice));
        Assert.Null(Tutorial.BeforeMatch(p, Tutorial.MatchKind.Official));
        Assert.Null(Tutorial.BeforeMatch(p, Tutorial.MatchKind.Practice));
    }

    [Fact]
    public void FirstMatch_OfficialFirst_ThenPracticeShowsNothing()
    {
        var p = TutorialProgress.Fresh();
        Assert.Equal(Tutorial.EventId(TutorialScene.FirstMatch), Tutorial.BeforeMatch(p, Tutorial.MatchKind.Official));
        Assert.Null(Tutorial.BeforeMatch(p, Tutorial.MatchKind.Practice));
    }

    [Fact]
    public void FirstMatch_NotShownAfterDismiss()
    {
        var p = TutorialProgress.Fresh();
        p.Dismiss();
        Assert.Null(Tutorial.BeforeMatch(p, Tutorial.MatchKind.Practice));
        Assert.Null(Tutorial.BeforeMatch(p, Tutorial.MatchKind.Official));
    }

    [Fact]
    public void Dismissed_StopsEveryRemainingScene()
    {
        var p = TutorialProgress.Fresh();
        Tutorial.OnNewSchoolStarted(p);
        p.Dismiss();
        foreach (var scene in AllScenes()) Assert.Null(Tutorial.OnScene(p, scene));
        // 赴任イベント自体は出る（説明ではない）が、最初の説明は付かない。
        var again = Tutorial.OnNewSchoolStarted(p);
        Assert.Equal(new[] { Tutorial.ArrivalNewSchoolEventId }, again);
    }

    [Fact]
    public void CanBuySomething_ComparesCheapestNextTierWithFunds()
    {
        var catalog = FacilityCatalog.Default;
        var set = new FacilitySet();
        var cheapest = catalog.Facilities.Where(d => d.MaxLevel > 0).Min(d => d.Tiers[1].Cost);
        Assert.True(Tutorial.CanBuySomething(cheapest, catalog, set));
        Assert.False(Tutorial.CanBuySomething(cheapest - 1, catalog, set));
    }

    [Fact]
    public void EventIds_AreDistinct_AndRecognized()
    {
        var ids = AllScenes().Select(Tutorial.EventId).ToList();
        Assert.Equal(ids.Count, ids.Distinct().Count());
        Assert.All(ids, id => Assert.True(Tutorial.IsTutorialEvent(id)));
        Assert.False(Tutorial.IsTutorialEvent(Tutorial.ArrivalNewSchoolEventId));
        Assert.True(Tutorial.IsArrivalEvent(Tutorial.ArrivalTransferEventId));
    }

    // ===== セーブ =====

    [Fact]
    public void Progress_RoundTripsThroughSave()
    {
        var state = SaveTestFixtures.BuildRichState();
        state.Tutorial = TutorialProgress.Fresh();
        state.Tutorial.TryBegin(TutorialScene.Basics);
        state.Tutorial.TryBegin(TutorialScene.Scouting);

        Assert.True(SaveCodec.TryDeserialize(SaveCodec.Serialize(state), out var s, out var err), err);
        Assert.Equal(new[] { TutorialScene.Basics, TutorialScene.Scouting }, s.Tutorial.Seen);
        Assert.False(s.Tutorial.Dismissed);
        Assert.True(s.Tutorial.ShouldShow(TutorialScene.FirstMatch));

        state.Tutorial.Dismiss();
        Assert.True(SaveCodec.TryDeserialize(SaveCodec.Serialize(state), out var d, out var err2), err2);
        Assert.True(d.Tutorial.Dismissed);
        Assert.False(d.Tutorial.ShouldShow(TutorialScene.FirstMatch));
    }

    [Fact]
    public void OldSave_IsTreatedAsAllSeen()
    {
        var json = SaveCodec.Serialize(SaveTestFixtures.BuildRichState());
        var start = json.IndexOf(",\"tutorial\":", StringComparison.Ordinal);
        Assert.True(start >= 0, "tutorial が直列化されていない");
        var end = json.IndexOf('}', start) + 1;   // {"seen":[...],"dismissed":false} の閉じ
        var v41 = json.Remove(start, end - start)
            .Replace($"\"version\":{SaveCodec.CurrentVersion}", "\"version\":41");
        Assert.DoesNotContain("\"tutorial\"", v41);

        Assert.True(SaveCodec.TryDeserialize(v41, out var s, out var err), err);
        foreach (var scene in AllScenes()) Assert.False(s.Tutorial.ShouldShow(scene));
    }

    // ===== 会話の表（data/events.yaml） =====

    [Fact]
    public void Table_HasArrivalAndEveryScene_AsSystemEvents_SpokenByTheTeamManager()
    {
        var events = EventTableParser.Parse(File.ReadAllText(FindDataFile("events.yaml")));
        var ids = AllScenes().Select(Tutorial.EventId)
            .Concat(new[] { Tutorial.ArrivalNewSchoolEventId, Tutorial.ArrivalTransferEventId, Tutorial.DismissedEventId });
        foreach (var id in ids)
        {
            var e = events.SingleOrDefault(x => x.Id == id);
            Assert.True(e != null, $"{id} が events.yaml に無い");
            Assert.Equal(EventKind.System, e!.Kind);
            Assert.False(e.HasChoices);   // 「分かった／もう説明はいらない」は提示層が足す
            Assert.All(e.Variants, v => Assert.Contains(v.Lines, l => l.Who == EventRoles.TeamManager));
        }
        // 赴任イベントは新設校用・転任用ともに複数の会話を持つ。
        Assert.True(events.Single(x => x.Id == Tutorial.ArrivalNewSchoolEventId).Variants.Count >= 2);
        Assert.True(events.Single(x => x.Id == Tutorial.ArrivalTransferEventId).Variants.Count >= 2);
    }

    [Fact]
    public void NewSchoolArrival_EveryVariant_SaysFirstYearsOnly_AndTeacherAppointment_WithoutManagerSpeaking()
    {
        var events = EventTableParser.Parse(File.ReadAllText(FindDataFile("events.yaml")));
        var e = events.Single(x => x.Id == Tutorial.ArrivalNewSchoolEventId);
        Assert.Contains("教員", e.Text);
        foreach (var v in e.Variants)
        {
            var all = string.Concat(v.Lines.Select(l => l.Say));
            Assert.Contains("1年", all);
            Assert.True(all.Contains("先生") || all.Contains("教員"), "教員として赴任したことが出てこない");
            Assert.DoesNotContain(v.Lines, l => l.Who == EventRoles.Manager);
        }
    }

    private static string FindDataFile(string fileName)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var candidate = Path.Combine(dir.FullName, "data", fileName);
            if (File.Exists(candidate)) return candidate;
            dir = dir.Parent;
        }
        throw new FileNotFoundException($"data/{fileName} がリポジトリ内に見つかりません。");
    }
}
