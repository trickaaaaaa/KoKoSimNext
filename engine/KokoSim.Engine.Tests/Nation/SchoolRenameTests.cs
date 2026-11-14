using System.Collections.Generic;
using System.Linq;
using KokoSim.Engine.Core;
using KokoSim.Engine.Match.Game;
using KokoSim.Engine.Nation;
using KokoSim.Engine.Nation.Roster;
using KokoSim.Engine.Nation.Tournaments;
using KokoSim.Engine.Saves;
using KokoSim.Engine.Stats;
using Xunit;

namespace KokoSim.Engine.Tests.Nation;

/// <summary>
/// 校名の編集（issue #946）。改名表は全国への差分（NationOverlay）に持ち、セーブだけに効く。
/// 取り消すと生成時の名前に戻る・別のセーブを読み直すと改名が残らない・改名で試合結果が変わらない。
/// </summary>
public sealed class SchoolRenameTests
{
    private static List<School> MakePrefecture(int baseId, int count)
        => Enumerable.Range(0, count).Select(i => new School
        {
            Id = baseId + i,
            Name = $"架空{baseId + i}高校",
            PrefectureId = 13,
            Strength = 40 + (i * 37) % 45,
            Fame = 40 + (i * 13) % 30,
        }).ToList();

    private static KokoSim.Engine.Nation.Nation NationOf(List<School> schools)
        => new(new List<Prefecture>(), schools);

    [Fact]
    public void SetRename_RenamesAndEmptyRestoresGeneratedName()
    {
        var schools = MakePrefecture(100, 3);
        var overlay = new NationOverlay();
        overlay.SetRename(schools[1], "水浜商工");
        Assert.Equal("水浜商工高校", schools[1].Name);
        Assert.Equal("架空101高校", schools[1].GeneratedName);
        overlay.SetRename(schools[1], null);
        Assert.Equal("架空101高校", schools[1].Name);
        Assert.Empty(overlay.Renames);
    }

    [Fact]
    public void Apply_UsesRenames_AndAnotherOverlayRestoresNames()
    {
        var schools = MakePrefecture(100, 3);
        var generated = NationOf(schools);
        var a = new NationOverlay();
        a.Renames[102] = "山手学院";
        var applied = a.Apply(generated);
        Assert.Equal("山手学院高校", applied.Schools.Single(s => s.Id == 102).Name);

        // 改名の無いセーブ（別の差分）を当て直すと、同じ実体でも生成時の名前に戻る（前のセーブの改名が漏れない）。
        var b = new NationOverlay();
        var reapplied = b.Apply(generated);
        Assert.Equal("架空102高校", reapplied.Schools.Single(s => s.Id == 102).Name);
    }

    [Fact]
    public void TryNormalizeStem_TrimsStripsSuffixAndLimitsLength()
    {
        Assert.True(SchoolNames.TryNormalizeStem("　水浜商工高校 ", out var s1));
        Assert.Equal("水浜商工", s1);
        Assert.True(SchoolNames.TryNormalizeStem("   ", out var s2));
        Assert.Null(s2);   // 空＝取り消し
        Assert.False(SchoolNames.TryNormalizeStem(new string('あ', SchoolNames.MaxStemLength + 1), out _));
        Assert.True(SchoolNames.TryNormalizeStem(new string('あ', SchoolNames.MaxStemLength), out _));
    }

    [Fact]
    public void Save_RoundTripsRenames_AndOldVersionReadsEmpty()
    {
        var state = new SaveState();
        state.NationOverlay.Renames[7] = "緑ヶ丘";
        state.NationOverlay.Renames[3] = "桜台";
        Assert.True(SaveCodec.TryDeserialize(SaveCodec.Serialize(state), out var back, out var err), err);
        Assert.Equal("緑ヶ丘", back!.NationOverlay.Renames[7]);
        Assert.Equal("桜台", back.NationOverlay.Renames[3]);

        // 旧版（v30）のセーブ: renames キーが無い＝改名なしで読める。
        var json = SaveCodec.Serialize(new SaveState())
            .Replace("\"version\":" + SaveCodec.CurrentVersion, "\"version\":30")
            .Replace(",\"renames\":[]", "");
        Assert.DoesNotContain("renames", json);
        Assert.True(SaveCodec.TryDeserialize(json, out var old, out var err2), err2);
        Assert.Empty(old!.NationOverlay.Renames);
    }

    [Fact]
    public void Save_RoundTripsManagerGeneratedName_AndOmitsItWhenNotRenamed()
    {
        var plain = SaveCodec.Serialize(new SaveState());
        Assert.DoesNotContain("managerGeneratedName", plain);

        var state = new SaveState { SchoolName = "港北" };
        state.NationOverlay.ManagerGeneratedName = "桜丘";
        Assert.True(SaveCodec.TryDeserialize(SaveCodec.Serialize(state), out var back, out var err), err);
        Assert.Equal("桜丘", back!.NationOverlay.ManagerGeneratedName);
        Assert.Equal("港北", back.SchoolName);
    }

    [Fact]
    public void BracketView_RenamedResolvesMatchesSlotsAndChampionById()
    {
        var schools = MakePrefecture(300, 4);
        var runner = new TournamentRunner(
            schools, schools[0], new NationCoefficients(), new Xoshiro256Random(11), new TournamentSchedule(), "テスト大会");
        while (!runner.Finished) runner.PlayNextPlayerMatch();
        runner.DrainBracketResolution();

        var view = runner.BuildBracketView();
        Assert.NotNull(view.ChampionName);
        var champion = schools.Single(s => s.Name == view.ChampionName);
        Assert.Equal(champion.Id, view.ChampionId);

        // 記録した校名は ID→「新N」へ引き直される（記録そのものは書き換わらない）。
        var renamed = view.Renamed((id, stored) => "新" + id);
        Assert.Equal("新" + champion.Id, renamed.ChampionName);
        Assert.All(renamed.Matches, m =>
        {
            Assert.Equal("新" + m.WinnerId, m.WinnerName);
            Assert.Equal("新" + m.LoserId, m.LoserName);
        });
        Assert.All(renamed.Rounds.SelectMany(r => r.Cards).SelectMany(c => new[] { c.Top, c.Bottom })
                .Where(x => x.IsDetermined),
            x => Assert.Equal("新" + x.SchoolId, x.TeamName));
        Assert.Equal(view.ChampionName, runner.BuildBracketView().ChampionName);
    }

    [Fact]
    public void Rename_DoesNotChangeTournamentResults()
    {
        Assert.Equal(Run(rename: false), Run(rename: true));
    }

    private static (int? ChampionId, int Hits, int AtBats, int Ks) Run(bool rename)
    {
        var schools = MakePrefecture(200, 8);
        if (rename)
        {
            var overlay = new NationOverlay();
            foreach (var s in schools) overlay.SetRename(s, "改名" + s.Id);
        }
        var rosters = new NationRosters(new AiRosterDeps());
        var stats = new NationTournamentStats();
        var bg = new BackgroundMatchResolver(rosters, new GameContext(), yearIndex: 1, stats: stats);
        var runner = new TournamentRunner(
            schools, schools[0], new NationCoefficients(), new Xoshiro256Random(7), new TournamentSchedule(), "テスト大会",
            playerResolver: null, backgroundResolver: bg);
        while (!runner.Finished) runner.PlayNextPlayerMatch();
        runner.DrainBracketResolution();
        var champion = schools.FirstOrDefault(s => s.Name == runner.ChampionName)?.Id;
        int Sum(System.Func<PlayerStats, int> pick)
            => stats.Schools.OrderBy(x => x).Sum(id => stats.ForSchool(id)!.Players.Values.Sum(pick));
        return (champion, Sum(p => p.Batting.Hits), Sum(p => p.Batting.AtBats), Sum(p => p.Pitching.StrikeOuts));
    }
}
