using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using KokoSim.Engine.Core;
using KokoSim.Engine.Match.Field;
using KokoSim.Engine.Match.Game;
using KokoSim.Engine.Nation;
using KokoSim.Engine.Nation.Roster;
using KokoSim.Engine.Nation.Tournaments;
using KokoSim.Engine.Players;
using Xunit;
using Xunit.Abstractions;

namespace KokoSim.Engine.Tests.Nation;

/// <summary>
/// 敵校のエースと注目選手が本格派に偏らないこと（issue #908）。
/// 旧実装は ①エース選びが4能力の素の合計（型の上乗せの合計が 本格+6/技巧−2/軟投−5）だったため、
/// エースは 本格35%/技巧18%/軟投4.7%（ベンチ入り投手は 29/21/7）に偏っていた。
/// ②大会展望の注目選手は、打者と比べてエースを選ぶ経路で型の看板の値を素のまま比べていた（球速の上乗せ +10 に対し
/// 制球 +3）ため、この経路で選ばれるエースは9割が本格派だった（ただしこの経路は展望の格付け校ではまれで、
/// 展望に載るエースの偏りの主因は①）。
/// 試合での強さは型によらず揃っている（PitcherArchetypeTests.Archetypes_AreBalanceNeutral_InActualGames）ので、
/// エースも展望に載るエースも、型の割合が大きくずれないのが正しい。
/// </summary>
public sealed class AceArchetypeShareTests
{
    private readonly ITestOutputHelper _out;
    public AceArchetypeShareTests(ITestOutputHelper o) { _out = o; }

    // 全国生成 seed 2026 の先頭 Schools 校を学年3で実体化（重いので1回だけ・校ごとに独立なので並列）。
    private const int Schools = 1500;
    private static readonly Lazy<(List<PitcherArchetype> Pool, List<PitcherArchetype> Aces)> Measured = new(Measure);

    private static (List<PitcherArchetype>, List<PitcherArchetype>) Measure()
    {
        var nation = NationGenerator.Generate(new SchoolNameVocab(), new NationCoefficients(), new Xoshiro256Random(2026));
        var deps = new AiRosterDeps();
        var schools = nation.Schools.Take(Schools).ToList();
        var per = new (List<PitcherArchetype> Pool, PitcherArchetype? Ace)[schools.Count];
        Parallel.For(0, schools.Count, i =>
        {
            var school = schools[i];
            var roster = AiRosterFactory.Bootstrap(school, 3, deps);
            var team = AiTeamBuilder.Build(roster, school, 3, deps);
            var pitchers = StrengthTeamFactory.AllPlayers(team).Where(p => p.Position == FieldPosition.Pitcher).ToList();
            var pool = pitchers.Where(p => p.Pitching.Archetype is not null).Select(p => p.Pitching.Archetype!.Value).ToList();
            var ace = pitchers.FirstOrDefault(p => p.UniformNumber == AceRestSelector.AceUniformNumber);
            per[i] = (pool, ace?.Pitching.Archetype);
        });
        return (per.SelectMany(x => x.Pool).ToList(),
            per.Where(x => x.Ace is not null).Select(x => x.Ace!.Value).ToList());
    }

    private static double Share(List<PitcherArchetype> xs, PitcherArchetype a) => (double)xs.Count(x => x == a) / xs.Count;

    private void Print(string label, List<PitcherArchetype> xs)
        => _out.WriteLine($"{label,-8} n={xs.Count,5} " + string.Join(" ",
            Enum.GetValues<PitcherArchetype>().Select(a => $"{PitcherArchetypes.Label(a)} {100 * Share(xs, a),5:F1}%")));

    [Fact]
    public void Aces_FollowTheArchetypeSharesOfTheMound()
    {
        var (pool, aces) = Measured.Value;
        Print("ベンチ投手", pool);
        Print("エース", aces);
        // 実測（2026-11-14）: 修正前 本格35.0/技巧18.3/軟投4.7%（ベンチ投手 29.3/20.9/6.9）→ 修正後 22.6/23.7/8.7%
        // （ベンチ投手 23.4/23.5/8.8）。帯は「エース選びが型で底上げ・底下げしない」ことを見る。
        foreach (var a in Enum.GetValues<PitcherArchetype>())
            Assert.True(Math.Abs(Share(aces, a) - Share(pool, a)) < 0.04,
                $"{PitcherArchetypes.Label(a)}: エース {Share(aces, a):P1} vs ベンチ投手 {Share(pool, a):P1}");
    }

    /// <summary>
    /// 展望に実際に載る注目選手（47都道府県の格付け校・学年3〜7年目）のうち、エースとして紹介された投手の型。
    /// 紹介文の成績行の先頭が型の表示名（TournamentPreview.PitcherStatLine）。
    /// </summary>
    private static readonly Lazy<List<PitcherArchetype>> PreviewAces = new(() =>
    {
        var nation = NationGenerator.Generate(new SchoolNameVocab(), new NationCoefficients(), new Xoshiro256Random(2026));
        var deps = new AiRosterDeps();
        var groups = nation.Schools.GroupBy(s => s.PrefectureId).ToList();
        var years = new[] { 3, 4, 5, 6, 7 };
        var labels = Enum.GetValues<PitcherArchetype>();
        var per = new List<PitcherArchetype>[groups.Count * years.Length];
        Parallel.For(0, per.Length, k =>
        {
            var entrants = groups[k / years.Length].ToList();
            var year = years[k % years.Length];
            var preview = TournamentPreviewBuilder.Build("県大会", entrants, 1, "地区大会", year,
                teamProvider: (school, y) => AiTeamBuilder.Build(AiRosterFactory.Bootstrap(school, y, deps), school, y, deps));
            per[k] = preview.NotablePlayers.Where(n => n.PitcherLed)
                .Select(n => labels.First(a => n.StatLine.StartsWith(PitcherArchetypes.Label(a), StringComparison.Ordinal)))
                .ToList();
        });
        return per.SelectMany(x => x).ToList();
    });

    [Fact]
    public void PreviewNotableAces_AreNotAlmostAllPowerPitchers()
    {
        var (_, aces) = Measured.Value;
        var notable = PreviewAces.Value;
        Print("エース", aces);
        Print("展望のエース", notable);
        Assert.True(notable.Count >= 100, $"展望で紹介されたエースが少なすぎて判定できない: {notable.Count}");
        // 展望が「エースの中でさらに型を選り好みしない」ことを見る（実測 2026-11-14: 本格23.0/技巧20.4/軟投10.2%）。
        // エース選び自体の偏りは上の Aces_FollowTheArchetypeSharesOfTheMound が見る。
        foreach (var a in Enum.GetValues<PitcherArchetype>())
            Assert.True(Math.Abs(Share(notable, a) - Share(aces, a)) < 0.10,
                $"{PitcherArchetypes.Label(a)}: 展望のエース {Share(notable, a):P1} vs エース全体 {Share(aces, a):P1}");
    }
}
