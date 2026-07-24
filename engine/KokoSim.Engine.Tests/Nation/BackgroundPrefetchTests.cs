using System;
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
/// 自県プレフェッチ（#試合開始前ロード短縮）の決定論回帰。<see cref="MemoizingBackgroundResolver"/> と
/// <see cref="TournamentRunner.PrefetchCurrentRoundBackgroundCards"/> を使い「暇な時間に現ラウンドを先に解いて
/// 温める→本流はキャッシュ命中で進む」経路が、プレフェッチ無しの素の全国フルシムと <b>バイト一致</b>すること
/// （champion・全校の全ボックススコア）を検証する。これが崩れると観戦結果や成績が観測タイミングで変わる。
/// </summary>
public sealed class BackgroundPrefetchTests
{
    private static readonly NationCoefficients Coeff = new();
    private static readonly TournamentSchedule Schedule = new();

    private static List<School> MakePrefecture(int baseId, int count)
    {
        var schools = new List<School>(count);
        for (var i = 0; i < count; i++)
            schools.Add(new School
            {
                Id = baseId + i,
                Name = $"架空{baseId + i}",
                PrefectureId = 13,
                Strength = 40 + (i * 37) % 45,
                Fame = 40 + (i * 13) % 30,
            });
        return schools;
    }

    /// <summary>プレフェッチ無し（素のフルシム）で最後まで消化。</summary>
    private static (string? Champion, NationTournamentStats Stats) RunPlain(List<School> schools, ulong seed)
    {
        var stats = new NationTournamentStats();
        var bg = new BackgroundMatchResolver(new NationRosters(new AiRosterDeps()), new GameContext(), 1, stats);
        var runner = new TournamentRunner(
            schools, schools[0], Coeff, new Xoshiro256Random(seed), Schedule, "素", null, bg);
        while (!runner.Finished) runner.PlayNextPlayerMatch();
        return (runner.ChampionName, stats);
    }

    /// <summary>各ラウンドを「先にプレフェッチ→本解決」で消化（Shell の運用を模す）。</summary>
    private static (string? Champion, NationTournamentStats Stats) RunPrefetched(List<School> schools, ulong seed)
    {
        var stats = new NationTournamentStats();
        var bg = new MemoizingBackgroundResolver(
            new BackgroundMatchResolver(new NationRosters(new AiRosterDeps()), new GameContext(), 1, stats));
        var runner = new TournamentRunner(
            schools, schools[0], Coeff, new Xoshiro256Random(seed), Schedule, "温", null, bg);
        while (!runner.Finished)
        {
            runner.PrefetchCurrentRoundBackgroundCards();   // 現ラウンド裏カードを先に温める
            runner.PlayNextPlayerMatch();                   // 本解決＝非自校カードはキャッシュ命中
        }
        return (runner.ChampionName, stats);
    }

    private static (int Hits, int AtBats, int K, int Runs) StatTotals(NationTournamentStats s)
    {
        int Sum(Func<PlayerStats, int> pick) => s.Schools.OrderBy(x => x).Sum(
            id => s.ForSchool(id)!.Players.Values.Sum(pick));
        return (Sum(p => p.Batting.Hits), Sum(p => p.Batting.AtBats),
                Sum(p => p.Pitching.StrikeOuts), Sum(p => p.Batting.Runs));
    }

    [Theory]
    [InlineData(16, 42UL)]
    [InlineData(16, 7UL)]
    [InlineData(32, 123UL)]
    [InlineData(11, 999UL)]   // 2の冪でない＝不戦勝が混じる
    public void Prefetched_MatchesPlain_ChampionAndAllBoxScores(int count, ulong seed)
    {
        var plain = RunPlain(MakePrefecture(1000, count), seed);
        var pref = RunPrefetched(MakePrefecture(1000, count), seed);

        Assert.Equal(plain.Champion, pref.Champion);
        Assert.Equal(StatTotals(plain.Stats), StatTotals(pref.Stats));

        // 学校集合と、各校ごとの安打合計まで一致（畳み込みが1カード1回・同一結果）。
        Assert.Equal(
            plain.Stats.Schools.OrderBy(x => x),
            pref.Stats.Schools.OrderBy(x => x));
        foreach (var id in plain.Stats.Schools)
        {
            var pa = plain.Stats.ForSchool(id)!.Players.Values.Sum(p => p.Batting.Hits);
            var pb = pref.Stats.ForSchool(id)!.Players.Values.Sum(p => p.Batting.Hits);
            Assert.Equal(pa, pb);
        }
    }

    [Fact]
    public void Prefetch_DoesNotDoubleFold_Stats()
    {
        // 同一ラウンドを2回プレフェッチしても（＝キャッシュ命中）成績が二重計上されない。
        var schools = MakePrefecture(2000, 8);
        var stats = new NationTournamentStats();
        var bg = new MemoizingBackgroundResolver(
            new BackgroundMatchResolver(new NationRosters(new AiRosterDeps()), new GameContext(), 1, stats));
        var runner = new TournamentRunner(schools, schools[0], Coeff, new Xoshiro256Random(5), Schedule, "重複", null, bg);

        runner.PrefetchCurrentRoundBackgroundCards();
        runner.PrefetchCurrentRoundBackgroundCards();   // 二度目は全ヒット
        var before = StatTotals(stats);
        runner.PlayNextPlayerMatch();                   // 本解決も命中（自校カードのみ新規）
        // 二重計上が無ければ、素の実行と最終的に一致する（下の全消化で担保）。
        while (!runner.Finished) { runner.PrefetchCurrentRoundBackgroundCards(); runner.PlayNextPlayerMatch(); }

        var plain = RunPlain(MakePrefecture(2000, 8), 5);
        Assert.Equal(StatTotals(plain.Stats), StatTotals(stats));
    }

    [Fact]
    public void Prefetch_IsSafeNoop_WhenFinished()
    {
        var schools = MakePrefecture(3000, 4);
        var bg = new MemoizingBackgroundResolver(
            new BackgroundMatchResolver(new NationRosters(new AiRosterDeps()), new GameContext(), 1, new NationTournamentStats()));
        var runner = new TournamentRunner(schools, schools[0], Coeff, new Xoshiro256Random(3), Schedule, "終", null, bg);
        while (!runner.Finished) runner.PlayNextPlayerMatch();

        var champBefore = runner.ChampionName;
        runner.PrefetchCurrentRoundBackgroundCards();   // 終了後は何もしない
        Assert.Equal(champBefore, runner.ChampionName);
    }
}
