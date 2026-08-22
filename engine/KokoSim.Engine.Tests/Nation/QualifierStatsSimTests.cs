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
/// 甲子園代表校の県予選成績の作り直し（設計書05 §1.4b）。他県は集計モデルで回すので個人成績が1つも
/// 無く、そのままだと甲子園の大会展望が48校ぶん「—」になる。代表の道のりだけを1球シムで打ち直す。
/// </summary>
public sealed class QualifierStatsSimTests
{
    private static readonly TournamentSchedule Schedule = new();

    private static List<School> Field(int baseId, int count)
        => Enumerable.Range(0, count).Select(i => new School
        {
            Id = baseId + i, Name = $"校{baseId + i}", PrefectureId = baseId / 1000,
            Strength = 44 + (i * 31) % 38, Fame = 45,
        }).ToList();

    /// <summary>優勝校=先頭、3試合勝ち上がった8校区画の結果を手で組む。</summary>
    private static PrefectureResult Result(IReadOnlyList<School> field) => new(
        PrefectureId: 1, PrefectureName: "県1", ChampionName: field[0].Name,
        WinsBySchool: null, ChampionId: field[0].Id,
        ChampionPath: new List<ChampionMatchup>
        {
            new(field[1].Id, 3), new(field[2].Id, 2), new(field[3].Id, 1),
        },
        EntrantCount: field.Count);

    private static (NationTournamentStats Stats, IRandomSource Rng) Replay(ulong seed)
    {
        var field = Field(1000, 8);
        var byId = field.ToDictionary(s => s.Id);
        var stats = new NationTournamentStats();
        var resolver = new BackgroundMatchResolver(
            new NationRosters(new AiRosterDeps()), new GameContext(), yearIndex: 1, stats: null);
        var rng = new Xoshiro256Random(seed);

        QualifierStatsSim.ReplayChampionPath(
            Result(field), id => byId.TryGetValue(id, out var s) ? s : null,
            resolver, stats, Schedule, QualifierStatsSim.RoundsFor(field.Count), yearIndex: 1, rng);
        return (stats, rng);
    }

    /// <summary>並びを固定した digest（決定論回帰の突き合わせ）。</summary>
    private static string Digest(NationTournamentStats stats)
    {
        var sb = new System.Text.StringBuilder();
        foreach (var schoolId in stats.PrefQualifierSchools.OrderBy(x => x))
        {
            var book = stats.PrefQualifierForSchool(schoolId)!;
            sb.Append(schoolId).Append(':');
            foreach (var (pid, line) in book.Players.OrderBy(kv => kv.Key))
                sb.Append(pid).Append('#')
                  .Append(line.Batting.Hits).Append('/').Append(line.Batting.AtBats).Append('/')
                  .Append(line.Pitching.StrikeOuts).Append('/').Append(line.Pitching.Outs).Append(';');
            sb.Append('|');
        }
        return sb.ToString();
    }

    /// <summary>代表校の県予選成績が実際に積まれる（＝展望の「—」が埋まる）。</summary>
    [Fact]
    public void ReplayChampionPath_FillsThePrefQualifierScope()
    {
        var (stats, _) = Replay(42);

        var book = stats.PrefQualifierForSchool(1000);
        Assert.NotNull(book);
        Assert.True(book!.Players.Count > 0);
        // 3試合ぶん＝打席も投球も積まれている。
        Assert.True(book.Players.Values.Sum(p => p.Batting.AtBats) > 0);
        Assert.True(book.Players.Values.Sum(p => p.Pitching.Outs) > 0);
        Assert.Equal(3, book.Players.Values.Max(p => p.Batting.Games));
    }

    /// <summary>積むのは<b>代表校の側だけ</b>（相手校は1試合だけの半端な成績になるので積まない）。</summary>
    [Fact]
    public void ReplayChampionPath_FoldsOnlyTheChampionSide()
    {
        var (stats, _) = Replay(42);

        Assert.Equal(new[] { 1000 }, stats.PrefQualifierSchools.OrderBy(x => x).ToArray());
    }

    /// <summary>今大会・通算スコープには一切触らない（甲子園の成績にも通算にも混ざらない）。</summary>
    [Fact]
    public void ReplayChampionPath_DoesNotTouchOtherScopes()
    {
        var (stats, _) = Replay(42);

        Assert.Empty(stats.Schools);
        Assert.Empty(stats.CareerSchools);
    }

    /// <summary>
    /// <b>本編の乱数列を1ビットも進めない</b>（Fork のみ）。ここが崩れると裏試合の展開が変わり
    /// 統計帯まで動く（不変条件#2）。
    /// </summary>
    [Fact]
    public void ReplayChampionPath_DoesNotAdvanceTheBaseStream()
    {
        var untouched = new Xoshiro256Random(42);
        var (_, used) = Replay(42);

        Assert.Equal(
            ((Xoshiro256Random)untouched).CaptureState(),
            ((Xoshiro256Random)used).CaptureState());
    }

    /// <summary>同シード同結果。</summary>
    [Fact]
    public void ReplayChampionPath_IsDeterministic()
        => Assert.Equal(Digest(Replay(7).Stats), Digest(Replay(7).Stats));

    /// <summary>打ち直す順に依存しない（背景の並列消化が順不同でも同じ成績になる）。</summary>
    [Fact]
    public void ReplayChampionPath_IsOrderIndependent()
    {
        var a = Field(1000, 8);
        var b = Field(2000, 8);
        var byId = a.Concat(b).ToDictionary(s => s.Id);
        School? Lookup(int id) => byId.TryGetValue(id, out var s) ? s : null;

        static PrefectureResult R(IReadOnlyList<School> f, int prefId) => new(
            prefId, $"県{prefId}", f[0].Name, null, f[0].Id,
            new List<ChampionMatchup> { new(f[1].Id, 3), new(f[2].Id, 2), new(f[3].Id, 1) }, f.Count);

        string Run(bool reversed)
        {
            var stats = new NationTournamentStats();
            var resolver = new BackgroundMatchResolver(
                new NationRosters(new AiRosterDeps()), new GameContext(), yearIndex: 1, stats: null);
            var rng = new Xoshiro256Random(5);
            var order = reversed
                ? new[] { R(b, 2), R(a, 1) }
                : new[] { R(a, 1), R(b, 2) };
            foreach (var r in order)
                QualifierStatsSim.ReplayChampionPath(
                    r, Lookup, resolver, stats, Schedule, QualifierStatsSim.RoundsFor(8), 1, rng);
            return Digest(stats);
        }

        Assert.Equal(Run(false), Run(true));
    }

    /// <summary>既に実測がある校（自県）は打ち直しの対象から外れる＝実測を上書きしない。</summary>
    [Fact]
    public void PendingRepresentatives_SkipsSchoolsThatAlreadyHaveStats()
    {
        var field = Field(1000, 8);
        var result = Result(field);
        var stats = new NationTournamentStats();

        Assert.Single(QualifierStatsSim.PendingRepresentatives(new[] { result }, stats));

        // 実測を入れると対象から外れる。
        var resolver = new BackgroundMatchResolver(
            new NationRosters(new AiRosterDeps()), new GameContext(), yearIndex: 1, stats: null);
        QualifierStatsSim.ReplayChampionPath(
            result, id => field.FirstOrDefault(s => s.Id == id), resolver, stats, Schedule,
            QualifierStatsSim.RoundsFor(8), 1, new Xoshiro256Random(1));

        Assert.Empty(QualifierStatsSim.PendingRepresentatives(new[] { result }, stats));
    }

    /// <summary>出場校数→ラウンド数（2の冪でない実数の県も切り上げで数える）。</summary>
    [Theory]
    [InlineData(2, 1)]
    [InlineData(8, 3)]
    [InlineData(49, 6)]
    [InlineData(64, 6)]
    [InlineData(65, 7)]
    [InlineData(190, 8)]
    public void RoundsFor_CountsBracketDepth(int entrants, int expected)
        => Assert.Equal(expected, QualifierStatsSim.RoundsFor(entrants));
}
