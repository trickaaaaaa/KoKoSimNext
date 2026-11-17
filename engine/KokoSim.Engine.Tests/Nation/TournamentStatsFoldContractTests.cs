using System.Collections.Generic;
using System.Linq;
using KokoSim.Engine.Core;
using KokoSim.Engine.Match.Game;
using KokoSim.Engine.Nation;
using KokoSim.Engine.Nation.Roster;
using KokoSim.Engine.Nation.Tournaments;
using KokoSim.Engine.Stats;
using Xunit;

namespace KokoSim.Engine.Tests.Nation;

/// <summary>
/// 他校どうしの成績の積み方の契約（issue #1107）。Shell（Assets）に単体テストの口が無いので、
/// Shell の各 Entry が頼る engine 側の約束をここで固定する:
/// <list type="bullet">
/// <item>裏カードは <c>onBackgroundMatch</c> を渡したときだけ全国の成績に積まれる（渡さないと他校の成績は空）。</item>
/// <item>大会の頭の <see cref="NationTournamentStats.StartTournament"/> で今大会だけ消え、通算は残る。</item>
/// <item>大会の途中で保存→復元しても、同じ畳み込み口を渡せば成績は保存しなかった場合と一致する。</item>
/// </list>
/// </summary>
public sealed class TournamentStatsFoldContractTests
{
    private static readonly NationCoefficients Coeff = new();
    private static readonly TournamentSchedule Schedule = new();

    private static List<School> MakeField(int baseId, int count)
    {
        var schools = new List<School>(count);
        for (var i = 0; i < count; i++)
            schools.Add(new School
            {
                Id = baseId + i, Name = $"架空{baseId + i}", PrefectureId = 8 + i % 3,
                Strength = 40 + (i * 37) % 45, Fame = 40 + (i * 13) % 30,
            });
        return schools;
    }

    private static IBackgroundMatchResolver NewResolver()
        => new MemoizingBackgroundResolver(
            new BackgroundMatchResolver(new NationRosters(new AiRosterDeps()), new GameContext(), 1, stats: null));

    private static TournamentRunner NewRunner(
        List<School> field, ulong seed, IBackgroundMatchResolver bg, NationTournamentStats? foldInto)
        => new(field, field[0], Coeff, new Xoshiro256Random(seed), Schedule, "契約", null, bg,
            onBackgroundMatch: foldInto == null ? null : (a, h, g) => foldInto.FoldMatch(a, h, g));

    private static void Finish(TournamentRunner r)
    {
        while (!r.Finished) r.PlayNextPlayerMatch();
        r.DrainBracketResolution();
    }

    private static int Hits(NationTournamentStats s, int id)
        => s.ForSchool(id)?.Players.Values.Sum(p => p.Batting.Hits) ?? -1;

    [Fact]
    public void BackgroundMatches_AreFolded_OnlyWhenCallbackIsGiven()
    {
        var field = MakeField(7000, 8);

        var folded = new NationTournamentStats();
        Finish(NewRunner(field, 11, NewResolver(), folded));
        // 自校（field[0]）が出ない裏カードの両校に今大会成績がある＝他校どうしの成績が積まれている。
        Assert.True(folded.Schools.Count >= field.Count - 1);
        Assert.All(field.Skip(1), s => Assert.NotNull(folded.ForSchool(s.Id)));

        var notFolded = new NationTournamentStats();
        Finish(NewRunner(field, 11, NewResolver(), null));
        Assert.Empty(notFolded.Schools);
    }

    [Fact]
    public void StartTournament_ClearsTodayScope_ButKeepsCareer()
    {
        var field = MakeField(7100, 8);
        var stats = new NationTournamentStats();
        Finish(NewRunner(field, 21, NewResolver(), stats));
        var firstSchools = stats.Schools.ToList();
        Assert.NotEmpty(firstSchools);

        stats.StartTournament();
        Assert.Empty(stats.Schools);
        Assert.All(firstSchools, id => Assert.NotNull(stats.CareerForSchool(id)));

        // 次の大会は前の大会の数字を引きずらない（同じ大会を2回回しても今大会は1回ぶん）。
        var secondField = MakeField(7100, 8);
        var once = new NationTournamentStats();
        Finish(NewRunner(secondField, 22, NewResolver(), once));
        Finish(NewRunner(secondField, 22, NewResolver(), stats));
        foreach (var id in once.Schools)
            Assert.Equal(Hits(once, id), Hits(stats, id));
    }

    [Fact]
    public void SaveAndLoad_MidTournament_FoldsLikeAnUninterruptedRun()
    {
        var field = MakeField(7200, 16);

        var straight = new NationTournamentStats();
        Finish(NewRunner(field, 31, NewResolver(), straight));

        // 1試合消化→保存→同じ畳み込み口で組み直して残りを消化。
        var resumed = new NationTournamentStats();
        var first = NewRunner(field, 31, NewResolver(), resumed);
        first.PlayNextPlayerMatch();
        var back = TournamentRunner.FromSaved(first.CaptureState(), field, field[0], Coeff, Schedule,
            playerResolver: null, backgroundResolver: NewResolver(),
            onBackgroundMatch: (a, h, g) => resumed.FoldMatch(a, h, g));
        Finish(back);

        Assert.Equal(straight.Schools.OrderBy(x => x), resumed.Schools.OrderBy(x => x));
        foreach (var id in straight.Schools)
            Assert.Equal(Hits(straight, id), Hits(resumed, id));
    }

    [Fact]
    public void SaveAndLoad_WithoutFoldCallback_LosesTheRemainingMatches()
    {
        var field = MakeField(7300, 16);

        var straight = new NationTournamentStats();
        Finish(NewRunner(field, 41, NewResolver(), straight));

        var resumed = new NationTournamentStats();
        var first = NewRunner(field, 41, NewResolver(), resumed);
        first.PlayNextPlayerMatch();
        var back = TournamentRunner.FromSaved(first.CaptureState(), field, field[0], Coeff, Schedule,
            playerResolver: null, backgroundResolver: NewResolver(), onBackgroundMatch: null);
        Finish(back);

        // 復元側に畳み込み口が無いと、保存後の他校戦が全国の成績に入らない（成績の積み方がセーブで変わる）。
        Assert.NotEqual(
            straight.Schools.Sum(id => Hits(straight, id)),
            resumed.Schools.Sum(id => Hits(resumed, id)));
    }
}
