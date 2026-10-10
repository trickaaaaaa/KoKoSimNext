using System.Linq;
using KokoSim.Engine.Match.Field;
using KokoSim.Engine.Nation;
using KokoSim.Engine.Nation.Roster;
using KokoSim.Engine.Players;
using KokoSim.Engine.Season;
using Xunit;

namespace KokoSim.Engine.Tests.Nation;

/// <summary>
/// AI校の配置を選抜で決める（issue #384 / #366②）。選手が持つのは適性と能力だけで、
/// 誰がどこを守り誰がマウンドに立つかは <see cref="AiTeamBuilder"/> が毎回決める。
/// 旧実装ではコホート編成（守備8枠1人ずつ＋投手プール）が構造的に担保していた
/// **守備網羅**と**投手供給の下限**を、選抜側で保証し直したことを固定する。
/// </summary>
public sealed class AiPlacementSelectionTests
{
    private static School MakeSchool(int id = 7, double strength = 55, double fame = 45)
        => new() { Id = id, Name = "架空東", PrefectureId = 13, Strength = strength, Fame = fame };

    private static AiRosterDeps NoPhenomDeps() => new()
    {
        Persistent = new PersistentRosterCoefficients
        {
            Phenom = new PhenomCoefficients { SpikeRatePerSchoolYear = 0, AllRoundRatePerSchoolYear = 0 },
        },
    };

    // ===== 選手は配置を持たない =====

    [Fact]
    public void AiPlayer_HasAptitudes_AndNoPosition()
    {
        var roster = AiRosterFactory.Bootstrap(MakeSchool(), 1, NoPhenomDeps());
        var p = roster.Players.First();

        // 9守備位置ぶんの適性を持つ（自校 DevelopingPlayer と同じ AptitudeRoller のロール）。
        foreach (var pos in AptitudeRoller.AllPositions)
            Assert.InRange(p.Aptitude(pos), 1, 99);

        // DH は守備位置でないのでロールされない（既定50のまま）＝適性を素朴に走査させない担保。
        Assert.Equal(50, p.Aptitude(FieldPosition.DesignatedHitter));

        // 「この選手の配置」を問い合わせる口は無い（コンパイル時に保証。ここは意図の記録）。
        Assert.Equal(AptitudeRoller.AllPositions.Length, AptitudeRoller.AllPositions.Distinct().Count());
    }

    // ===== 守備網羅（選抜側の保証） =====

    [Theory]
    [InlineData(20.0)]
    [InlineData(55.0)]
    [InlineData(92.0)]
    public void EveryCohort_CoversAllNinePositions(double strength)
    {
        var deps = NoPhenomDeps();
        for (var id = 1; id <= 60; id++)
        {
            var school = MakeSchool(id, strength);
            var roster = AiRosterFactory.Bootstrap(school, 1, deps);
            var team = AiTeamBuilder.Build(roster, school, 1, deps);

            var covered = team.BattingOrder.Select(p => p.Position).ToHashSet();
            Assert.True(covered.Count == 9,
                $"校{id}(強さ{strength}) の打順が9ポジを網羅していない: " +
                string.Join("/", team.BattingOrder.Select(p => p.Position)));
            Assert.Contains(FieldPosition.Pitcher, covered);
        }
    }

    [Fact]
    public void AfterGraduation_StillCoversAllNinePositions()
    {
        // 3年引退直後（2学年ぶんしか居ない）でも守備が組めること。人数が最も薄くなる瞬間。
        var deps = NoPhenomDeps();
        for (var id = 1; id <= 40; id++)
        {
            var school = MakeSchool(id);
            var roster = AiRosterFactory.Bootstrap(school, 1, deps);
            AiRosterCycle.RetireGraduates(roster);

            var team = AiTeamBuilder.Build(roster, school, 1, deps);
            Assert.Equal(9, team.BattingOrder.Select(p => p.Position).ToHashSet().Count);
        }
    }

    // ===== 投手供給の下限（選抜側の保証） =====

    [Fact]
    public void NoPitcherAptitudeInCohort_StillPutsSomeoneOnTheMound()
    {
        // 投手適性が誰も野手適性を上回らない＝旧実装なら「適性クリア0人」になるコホート。
        // 下限保証（PitcherCountFloor）で投手能力の最良者がマウンドに立つ＝投手0人化しない。
        var deps = NoPhenomDeps() with
        {
            // 投手適性へのバイアスを大きく負にして、誰も投手に寄らない状況を作る。
            Roster = new RosterCoefficients { PitcherAptitudeBias = -99.0 },
        };
        for (var id = 1; id <= 30; id++)
        {
            var school = MakeSchool(id);
            var roster = AiRosterFactory.Bootstrap(school, 1, deps);
            var team = AiTeamBuilder.Build(roster, school, 1, deps);

            var mound = team.BattingOrder.Count(p => p.Position == FieldPosition.Pitcher);
            Assert.True(mound == 1, $"校{id}: マウンドに立つ選手が {mound} 人（1人であるべき）");
            Assert.Equal(9, team.BattingOrder.Select(p => p.Position).ToHashSet().Count);
        }
    }

    [Fact]
    public void EveryoneLeansPitcher_StillFieldsEightDefenders()
    {
        // 逆に全員が投手適性クリアになる状況。マウンド組の上限（守備8枠を割り当てられる人数）が
        // 効いて守備が崩れないこと。
        var deps = NoPhenomDeps() with
        {
            Roster = new RosterCoefficients { PitcherAptitudeBias = 99.0 },
        };
        for (var id = 1; id <= 30; id++)
        {
            var school = MakeSchool(id);
            var roster = AiRosterFactory.Bootstrap(school, 1, deps);
            var team = AiTeamBuilder.Build(roster, school, 1, deps);

            Assert.Equal(9, team.BattingOrder.Select(p => p.Position).ToHashSet().Count);
        }
    }

    // ===== 割当が「位置ごとに最良」でなく総和最適であること =====

    [Fact]
    public void Assignment_MaximisesTotalEffectiveFielding_NotPerPositionGreedy()
    {
        var deps = NoPhenomDeps();
        var school = MakeSchool();
        var roster = AiRosterFactory.Bootstrap(school, 1, deps);
        var team = AiTeamBuilder.Build(roster, school, 1, deps);

        // 打順の野手8人について、2人の守備位置を入れ替えると実効守備力の和が悪化する（＝総和最適）。
        var coeff = deps.Fielding;
        var starters = team.BattingOrder.Where(p => p.Position != FieldPosition.Pitcher).ToList();
        Assert.Equal(8, starters.Count);

        // Player は適性を持たないので、ロスター側の適性で測り直す（SourceId で突き合わせる）。
        var byId = roster.Players.ToDictionary(p => p.Id);
        double Eff(Player pl, FieldPosition pos)
            => EffectiveFielding.Raw(pl.Fielding, byId[pl.SourceId!.Value].Aptitude(pos), coeff);

        // ⚠ 比べるのは**規則上あり得る入替**だけ（issue ai-lefty-not-at-c-2b-3b-ss）。左投げを
        // 捕手・二塁・三塁・遊撃へ動かす入替は守備力が上がっても採らない＝ソルバの目的関数は
        // 「実効守備力 − 左投げの罰」になった。ここで除外しないと「改善する入替がある」と誤検出する。
        bool Legal(Player pl, FieldPosition pos)
            => pl.Throws != Handedness.Left || !FieldPositions.IsRightThrowOnly(pos);

        for (var a = 0; a < starters.Count; a++)
            for (var b = a + 1; b < starters.Count; b++)
            {
                if (!Legal(starters[a], starters[b].Position) || !Legal(starters[b], starters[a].Position)) continue;
                var current = Eff(starters[a], starters[a].Position) + Eff(starters[b], starters[b].Position);
                var swapped = Eff(starters[a], starters[b].Position) + Eff(starters[b], starters[a].Position);
                Assert.True(swapped <= current + 1e-9,
                    $"守備位置を入れ替えると改善する＝総和最適でない: {starters[a].Name}↔{starters[b].Name}");
            }
    }

    // ===== 二刀流が構造的に存在する =====

    [Fact]
    public void TwoWayPlayers_ExistStructurally_InAiSchools()
    {
        var deps = NoPhenomDeps();
        var all = Enumerable.Range(1, 200)
            .SelectMany(id => AiRosterFactory.Bootstrap(MakeSchool(id), 1, deps).Players)
            .ToList();

        double Bat(AiPlayer p) => (p.Snapshot.Contact + p.Snapshot.Power) / 2.0;
        double Pit(AiPlayer p) => p.Snapshot.Pitching.ReliefScore();

        var batBar = all.Select(Bat).OrderByDescending(v => v).ElementAt(all.Count / 5);
        var pitBar = all.Select(Pit).OrderByDescending(v => v).ElementAt(all.Count / 5);

        var twoWay = all.Count(p => Bat(p) >= batBar && Pit(p) >= pitBar);
        Assert.True(twoWay > 0, "投打とも上位2割のAI校選手が1人も出ていない");
    }

    // ===== 決定論 =====

    [Fact]
    public void SelectionIsDeterministic_SameSchoolSameYear()
    {
        var deps = NoPhenomDeps();
        var school = MakeSchool();
        var a = AiTeamBuilder.Build(AiRosterFactory.Bootstrap(school, 1, deps), school, 1, deps);
        var b = AiTeamBuilder.Build(AiRosterFactory.Bootstrap(school, 1, deps), school, 1, deps);

        Assert.Equal(a.BattingOrder.Select(p => (p.Name, p.Position, p.UniformNumber)),
                     b.BattingOrder.Select(p => (p.Name, p.Position, p.UniformNumber)));
        Assert.Equal(a.Reserves.Select(p => (p.Name, p.Position, p.UniformNumber)),
                     b.Reserves.Select(p => (p.Name, p.Position, p.UniformNumber)));
    }
}
