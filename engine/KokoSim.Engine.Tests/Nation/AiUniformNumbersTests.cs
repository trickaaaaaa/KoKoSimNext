using System.Collections.Generic;
using System.Linq;
using KokoSim.Engine.Match.Field;
using KokoSim.Engine.Match.Game;
using KokoSim.Engine.Nation;
using KokoSim.Engine.Nation.Roster;
using Xunit;
using Xunit.Abstractions;

namespace KokoSim.Engine.Tests.Nation;

/// <summary>
/// AI校の背番号採番（<see cref="AiUniformNumbers"/>・design-06 §3.3b）。
/// 二番手投手＝10 固定、3枚目以降は校ごとに散らす、控え野手は残り8つ。
/// </summary>
public class AiUniformNumbersTests
{
    private readonly ITestOutputHelper _out;

    public AiUniformNumbersTests(ITestOutputHelper output) => _out = output;

    [Fact]
    public void Layout_CoversOneToTwenty_WithoutDuplicates()
    {
        for (var schoolId = 1; schoolId <= 200; schoolId++)
        {
            var n = AiUniformNumbers.For(schoolId, yearIndex: 1);

            Assert.Equal(AiUniformNumbers.AceNumber, n.Mound[0]);
            Assert.Equal(AiUniformNumbers.SecondPitcherNumber, n.Mound[1]);
            Assert.Equal(1 + AiUniformNumbers.ReservePitcherSlots, n.Mound.Count);
            Assert.Equal(AiUniformNumbers.BenchFielderSlots, n.BenchFielders.Count);

            // 3枚目以降は11〜20の空き番号から。若番ほど上位＝控えの並び順が投手能力の順位と一致する。
            Assert.All(n.Mound.Skip(2), x => Assert.InRange(x, 11, AiUniformNumbers.BenchSize));
            Assert.True(n.Mound[2] < n.Mound[3], $"校{schoolId}: {n.Mound[2]} < {n.Mound[3]}");
            // 控え野手は若い順。
            Assert.Equal(n.BenchFielders.OrderBy(x => x), n.BenchFielders);

            // 10〜20 の11番号が「控え投手3＋控え野手8」でちょうど埋まる（重複も欠落もない）。
            var all = n.Mound.Skip(1).Concat(n.BenchFielders).OrderBy(x => x).ToList();
            Assert.Equal(Enumerable.Range(AiUniformNumbers.SecondPitcherNumber, 11), all);
        }
    }

    [Fact]
    public void Layout_IsDeterministic_PerSchoolAndYear()
    {
        var a = AiUniformNumbers.For(4321, yearIndex: 3);
        var b = AiUniformNumbers.For(4321, yearIndex: 3);
        Assert.Equal(a.Mound, b.Mound);
        Assert.Equal(a.BenchFielders, b.BenchFielders);
    }

    [Fact]
    public void ThirdPitcherNumber_VariesAcrossSchools()
    {
        // 旧実装は全校 18・19 固定だった。校ごとに散らばること（＝単調さの解消）を固定する。
        var third = new HashSet<int>();
        for (var schoolId = 1; schoolId <= 400; schoolId++)
            third.Add(AiUniformNumbers.For(schoolId, yearIndex: 1).Mound[2]);
        _out.WriteLine($"3枚目の背番号のばらつき: {third.Count}種類（{string.Join(",", third.OrderBy(x => x))}）");
        Assert.True(third.Count >= 5, $"種類={third.Count}");
    }

    [Fact]
    public void BuiltTeam_NeverSkipsTen_AndPacksNumbersFromTheTop()
    {
        var deps = new AiRosterDeps();
        var second10 = 0; var secondInField = 0; var teams = 0;
        for (var id = 1; id <= 150; id++)
        {
            var school = new School
            {
                Id = id, Name = $"校{id}", PrefectureId = 13, Strength = 30.0 + id % 40, Fame = 45,
            };
            var roster = AiRosterFactory.Bootstrap(school, 1, deps);
            AiRosterCycle.RetireGraduates(roster);
            var team = AiTeamBuilder.Build(roster, school, 1, deps);
            teams++;

            var numbers = AiUniformNumbers.For(school.Id, 1);
            var all = StrengthTeamFactory.AllPlayers(team).ToList();

            // 背番号は1〜20で一意（0＝ベンチ外は出ない）。
            var used = all.Select(p => p.UniformNumber).ToList();
            Assert.All(used, x => Assert.InRange(x, 1, AiUniformNumbers.BenchSize));
            Assert.Equal(used.Count, used.Distinct().Count());

            // 控え投手が1人でも居れば背番号10は必ず居る（欠番にしない）＝番号は詰めて配る。
            var reservePitchers = team.Reserves.Where(p => p.Position == FieldPosition.Pitcher).ToList();
            var ten = all.SingleOrDefault(p => p.UniformNumber == AiUniformNumbers.SecondPitcherNumber);
            if (reservePitchers.Count > 0)
            {
                Assert.NotNull(ten);
                Assert.Equal(FieldPosition.Pitcher, ten!.Position);
                second10++;
            }

            // 二番手（マウンド順位2位）が守備で先発する日は守備位置番号（2〜9）を着ける＝
            // 10 はその下の投手へ繰り上がる。
            if (team.BattingOrder.Any(p => p.Position != FieldPosition.Pitcher
                    && p.Pitching.ReliefScore() > (ten?.Pitching.ReliefScore() ?? double.NegativeInfinity)))
                secondInField++;

            // 控え投手の番号は採番表の上から詰めて使う（1＝温存で控えへ回ったエースを含む）。
            var expected = numbers.Mound.Take(1 + reservePitchers.Count).ToHashSet();
            Assert.All(reservePitchers, p => Assert.Contains(p.UniformNumber, expected));

            // 欠番は末尾だけ＝登録人数ぶんの番号が1から連続して埋まる。
            Assert.Equal(Enumerable.Range(1, all.Count), used.OrderBy(x => x));
        }
        _out.WriteLine($"背番号10が居る校: {second10}/{teams}、二番手が守備で先発（2〜9を着用）: {secondInField}/{teams}");
        Assert.True(second10 > 0);
    }
}
