using System.Collections.Generic;
using System.Linq;
using KokoSim.Engine.Core;
using KokoSim.Engine.Match.Game;
using KokoSim.Engine.Match.Timeline.Playback;
using KokoSim.Engine.Nation;
using KokoSim.Engine.Nation.Tournaments;
using KokoSim.Engine.Players;
using KokoSim.Engine.Match.Field;
using Xunit;

namespace KokoSim.Engine.Tests.Nation;

/// <summary>
/// 秋季地区予選のブロックリーグ戦（神奈川・issue #772）。ブロック分け（夏の順位でシード分散）・
/// 総当たりの組み方・対話で進める進行体（自校の試合で止まる／観戦しても同じ結果／保存往復）・
/// 集計モデル経路（GroupSplit）を固定する。
/// </summary>
public sealed class BlockLeagueRunnerTests
{
    private static readonly NationCoefficients Coeff = new();
    private static readonly TournamentSchedule Schedule = new();

    private static School Sch(int id, double strength) => new()
    {
        Id = id, Name = $"校{id}", PrefectureId = 0, Strength = strength,
    };

    /// <summary>自校(id=1)＋others 校（約27校の地区を想定）。</summary>
    private static List<School> District(int others = 26, double managerStrength = 60)
    {
        var list = new List<School> { Sch(1, managerStrength) };
        for (var i = 0; i < others; i++) list.Add(Sch(100 + i, 30 + (i * 7) % 41));
        return list;
    }

    private static IReadOnlyList<IReadOnlyList<School>> Blocks(List<School> d, System.Func<School, int?>? seed = null, ulong s = 7)
        => BlockDraw.Split(d, 4, seed, new Xoshiro256Random(s));

    // ── ブロック分け ──

    [Theory]
    [InlineData(27, 7, 4, 3)]
    [InlineData(28, 7, 4, 4)]
    [InlineData(25, 7, 4, 3)]
    [InlineData(9, 3, 3, 3)]
    public void Split_MakesBlocksOfTeamsPerBlockOrOneLess(int n, int blocks, int max, int min)
    {
        var d = District(n - 1);
        var b = Blocks(d);
        Assert.Equal(blocks, b.Count);
        Assert.Equal(max, b.Max(x => x.Count));
        Assert.Equal(min, b.Min(x => x.Count));
        Assert.Equal(n, b.Sum(x => x.Count));
        Assert.Equal(n, b.SelectMany(x => x).Select(s => s.Id).Distinct().Count());   // 重複なし・漏れなし
    }

    [Fact]
    public void Split_SpreadsSummerTopFinishersAcrossBlocks()
    {
        // 夏の上位7校（順位0〜6）はブロック数7と同数＝必ず別々のブロックに入る。次の7校（7〜13）も同じ
        var d = District();
        var rank = d.Skip(1).Take(14).Select((s, i) => (s.Id, i)).ToDictionary(x => x.Id, x => x.i);
        var blocks = Blocks(d, s => rank.TryGetValue(s.Id, out var r) ? r : null);
        foreach (var block in blocks)
        {
            Assert.Equal(1, block.Count(s => rank.TryGetValue(s.Id, out var r) && r < 7));
            Assert.Equal(1, block.Count(s => rank.TryGetValue(s.Id, out var r) && r is >= 7 and < 14));
        }
    }

    [Fact]
    public void Split_IsDeterministic_AndIndependentOfInputOrder()
    {
        var d = District();
        var a = Blocks(d).Select(b => string.Join(",", b.Select(s => s.Id)));
        var shuffled = d.AsEnumerable().Reverse().ToList();
        var b2 = Blocks(shuffled).Select(b => string.Join(",", b.Select(s => s.Id)));
        Assert.Equal(a, b2);
    }

    [Theory]
    [InlineData(4, 3, 2)]
    [InlineData(3, 3, 1)]
    [InlineData(2, 1, 1)]
    public void RoundRobinRounds_EveryPairMeetsExactlyOnce(int teams, int rounds, int gamesPerRound)
    {
        var rr = BlockDraw.RoundRobinRounds(teams);
        Assert.Equal(rounds, rr.Count);
        Assert.All(rr, r => Assert.Equal(gamesPerRound, r.Count));
        var pairs = rr.SelectMany(r => r).Select(p => (System.Math.Min(p.A, p.B), System.Math.Max(p.A, p.B))).ToList();
        Assert.Equal(teams * (teams - 1) / 2, pairs.Distinct().Count());
        Assert.Equal(pairs.Count, pairs.Distinct().Count());
        // 1節に同じ学校が2試合しない
        Assert.All(rr, r => Assert.Equal(r.Count * 2, r.SelectMany(p => new[] { p.A, p.B }).Distinct().Count()));
    }

    // ── 進行体 ──

    private static BlockLeagueRunner New(List<School> d, IPlayerMatchResolver? res = null, ulong seed = 42)
        => new(Blocks(d), d[0], 2, Coeff, new Xoshiro256Random(seed), Schedule, "地区予選", res);

    [Fact]
    public void Runner_PlaysEveryBlockGame_AndAdvancesTopTwoPerBlock()
    {
        var d = District();
        var r = New(d);
        var playerGames = 0;
        while (!r.Finished)
        {
            Assert.NotNull(r.NextOpponent);
            r.PlayNextPlayerMatch();
            playerGames++;
        }
        var myBlock = Blocks(d)[r.ManagerBlockIndex];
        Assert.Equal(myBlock.Count - 1, playerGames);   // 自校はブロックの全校と1回ずつ（負けても終わらない）

        var views = r.BuildBlockViews();
        var expectedGames = views.Sum(v => v.Standings.Count * (v.Standings.Count - 1) / 2);
        Assert.Equal(expectedGames, r.BuildBracketView().Matches.Count);
        Assert.All(views.SelectMany(v => v.Games), g => Assert.True(g.Played));
        Assert.All(views, v => Assert.All(v.Standings, s => Assert.Equal(v.Standings.Count - 1, s.Played)));

        var q = r.Qualifiers();
        Assert.Equal(views.Count * 2, q.Count);
        foreach (var v in views)
            Assert.Equal(v.Standings.Take(2).Select(s => s.SchoolId), q.Where(s => v.Standings.Any(x => x.SchoolId == s.Id)).Select(s => s.Id));
        Assert.Equal(q.Any(s => s.Id == 1), r.ManagerQualified);
        Assert.False(r.IsChampion);
        Assert.False(r.BracketResolutionPending);   // 終わった時点で全試合が消化済み
    }

    [Fact]
    public void Runner_MatchDays_AdvanceByLeagueSpread()
    {
        var d = District(27);   // 28校＝全ブロック4校＝自校に休みの節が無い
        var r = New(d);
        var days = new List<int>();
        while (!r.Finished) { days.Add(r.NextMatchDay); r.PlayNextPlayerMatch(); }
        Assert.Equal(new[] { Schedule.LeagueMatchDay(0), Schedule.LeagueMatchDay(1), Schedule.LeagueMatchDay(2) }, days);
        Assert.Equal(new[] { 1, 3, 5 }, days);
    }

    [Fact]
    public void Runner_IsDeterministic()
    {
        string Run()
        {
            var d = District();
            var r = New(d);
            while (!r.Finished) r.PlayNextPlayerMatch();
            return Digest(r);
        }
        Assert.Equal(Run(), Run());
    }

    [Fact]
    public void Live_And_Auto_ProduceTheSameLeague()
    {
        var auto = New(District(), new LiveConsistentResolver());
        while (!auto.Finished) auto.PlayNextPlayerMatch();

        var live = New(District(), new LiveConsistentResolver());
        while (!live.Finished)
        {
            var m = live.BeginNextPlayerMatch();
            while (m.Progression.Advance()) { }
            live.CompleteNextPlayerMatch(m.Progression.BuildResult());
        }
        Assert.Equal(Digest(auto), Digest(live));
    }

    [Fact]
    public void SaveRoundTrip_ContinuesIdentically()
    {
        var d = District();
        var want = New(d);
        while (!want.Finished) want.PlayNextPlayerMatch();

        var r = New(d);
        r.PlayNextPlayerMatch();
        var state = r.CaptureState();
        Assert.NotNull(state.League);
        var back = (BlockLeagueRunner)TournamentRunners.FromSaved(state, d, d[0], Coeff, Schedule);
        Assert.Equal(r.NextOpponent?.Id, back.NextOpponent?.Id);
        Assert.Equal(r.NextMatchDay, back.NextMatchDay);
        while (!back.Finished) back.PlayNextPlayerMatch();
        Assert.Equal(Digest(want), Digest(back));
    }

    [Fact]
    public void SaveJson_RoundTripsLeagueState()
    {
        var d = District();
        var r = New(d);
        r.PlayNextPlayerMatch();
        var save = new KokoSim.Engine.Saves.TournamentSaveState { Runner = r.CaptureState(), Title = "x" };
        var json = KokoSim.Engine.Saves.TournamentSaves.ToJson(save);
        var back = KokoSim.Engine.Saves.TournamentSaves.FromJson(KokoSim.Engine.Saves.Json.JsonValue.Parse(json.ToString()))!;
        var league = back.Runner.League!;
        Assert.Equal(r.CaptureState().League!.Blocks, league.Blocks);
        Assert.Equal(r.CaptureState().League!.Games, league.Games);
        Assert.Equal(2, league.AdvancePerBlock);

        // 勝ち抜き戦の状態は league を書かない＝旧版と同じ形
        var ko = new TournamentRunner(d, d[0], Coeff, new Xoshiro256Random(1), Schedule, "t");
        var koJson = KokoSim.Engine.Saves.TournamentSaves.ToJson(new KokoSim.Engine.Saves.TournamentSaveState { Runner = ko.CaptureState() });
        Assert.DoesNotContain("\"league\"", koJson.ToString());
    }

    /// <summary>
    /// v22 以前のセーブ（地区予選が勝ち抜き戦だった頃の途中）は league を持たない＝勝ち抜き戦として組み直し、
    /// そのまま続きを消化できる（完了条件「旧セーブ（地区予選の途中）を読み込んで進められる」）。
    /// </summary>
    [Fact]
    public void OldKnockoutSave_RestoresAsTournamentRunner_AndContinues()
    {
        var d = District();
        var ko = new TournamentRunner(d, d[0], Coeff, new Xoshiro256Random(9), Schedule, "旧・地区予選");
        ko.PlayNextPlayerMatch();
        var json = KokoSim.Engine.Saves.TournamentSaves.ToJson(new KokoSim.Engine.Saves.TournamentSaveState { Runner = ko.CaptureState() });
        var state = KokoSim.Engine.Saves.TournamentSaves.FromJson(KokoSim.Engine.Saves.Json.JsonValue.Parse(json.ToString()))!.Runner;
        Assert.Null(state.League);
        var back = TournamentRunners.FromSaved(state, d, d[0], Coeff, Schedule);
        Assert.IsType<TournamentRunner>(back);
        var guard = 0;
        while (!back.Finished && guard++ < 10) back.PlayNextPlayerMatch();
        Assert.True(back.Finished);
    }

    [Fact]
    public void CaptureState_Throws_WhileLiveMatchIsPending()
    {
        var r = New(District(), new LiveConsistentResolver());
        r.BeginNextPlayerMatch();
        Assert.False(r.CanCaptureState);
        Assert.Throws<System.InvalidOperationException>(() => r.CaptureState());
    }

    [Fact]
    public void Spectated_ResolvesOneRoundPerStep()
    {
        var d = District();
        var r = BlockLeagueRunner.Spectated(Blocks(d), 2, Coeff, new Xoshiro256Random(5), Schedule, "他地区");
        Assert.True(r.Finished);
        Assert.Equal(3, r.RemainingBracketRounds);
        r.StepBracketResolution();
        Assert.Equal(2, r.RemainingBracketRounds);
        r.DrainBracketResolution();
        Assert.False(r.BracketResolutionPending);
        Assert.Equal(14, r.Qualifiers().Count);
    }

    [Fact]
    public void Standings_TieOnWins_HeadToHeadWinnerRanksHigher()
    {
        var d = District();
        var r = New(d);
        while (!r.Finished) r.PlayNextPlayerMatch();
        foreach (var v in r.BuildBlockViews())
        {
            for (var k = 0; k + 1 < v.Standings.Count; k++)
            {
                var a = v.Standings[k];
                var b = v.Standings[k + 1];
                Assert.True(a.Wins >= b.Wins);
                if (a.Wins != b.Wins) continue;
                var h2h = v.Games.Single(g => (g.AId == a.SchoolId && g.BId == b.SchoolId) || (g.AId == b.SchoolId && g.BId == a.SchoolId));
                var aBeatB = h2h.AId == a.SchoolId ? h2h.AWon : !h2h.AWon;
                // 2校だけの同率なら直接対決の勝者が上。3校以上の同率は得失点差の並びが先に効く（既存規則）
                if (v.Standings.Count(s => s.Wins == a.Wins) == 2) Assert.True(aBeatB);
            }
        }
    }

    // ── 集計モデル経路（GroupSplit） ──

    [Fact]
    public void GroupSplit_BlockLeague_AdvancesTwoPerBlockPerDistrict()
    {
        var schools = new List<School>();
        for (var d = 0; d < 7; d++)
            for (var i = 0; i < 27; i++)
                schools.Add(new School { Id = d * 100 + i, Name = $"d{d}s{i}", PrefectureId = 0, Strength = 30 + (i * 11) % 50, DistrictId = d });
        var stage = new StageFormat
        {
            Type = StageType.GroupSplit, Groups = 7, Grouping = GroupingMode.Geographic,
            Child = new ChildStage(StageType.RoundRobin, 4, 2),
        };
        var adv = GroupSplit.Run(schools, stage, Coeff, new Xoshiro256Random(3), s => s.DistrictId);
        Assert.Equal(7 * 7 * 2, adv.Count);   // 7地区×7ブロック×2校
        Assert.All(Enumerable.Range(0, 7), d => Assert.Equal(14, adv.Count(s => s.DistrictId == d)));
        var again = GroupSplit.Run(schools, stage, Coeff, new Xoshiro256Random(3), s => s.DistrictId);
        Assert.Equal(adv.Select(s => s.Id), again.Select(s => s.Id));
    }

    // ── 補助 ──

    private static string Digest(BlockLeagueRunner r)
    {
        var sb = new System.Text.StringBuilder();
        foreach (var m in r.BuildBracketView().Matches)
            sb.Append(m.RoundName).Append(' ').Append(m.WinnerId).Append('>').Append(m.LoserId)
              .Append(' ').Append(m.WinnerScore).Append('-').Append(m.LoserScore).Append('\n');
        foreach (var v in r.BuildBlockViews())
            sb.Append(v.Block).Append(':').Append(string.Join(",", v.Standings.Select(s => $"{s.SchoolId}/{s.Wins}-{s.Losses}/{s.RunMargin}"))).Append('\n');
        return sb.ToString();
    }

    /// <summary>Resolve と BeginLive が同じ試合になる resolver（TournamentRunnerTests と同形）。</summary>
    private sealed class LiveConsistentResolver : IPlayerMatchResolver
    {
        public PlayerMatchDetail Resolve(School manager, School opponent, IRandomSource rng, bool mercyRuleEnabled,
            TournamentMatchContext? context = null)
            => new(GameEngine.Play(MakeTeam(opponent.Name, opponent.Strength), MakeTeam(manager.Name, manager.Strength),
                new GameContext { MercyRuleEnabled = mercyRuleEnabled }, rng.Fork(2)), ManagerIsAway: false);

        public PlayerMatchLive BeginLive(School manager, School opponent, IRandomSource rng, bool mercyRuleEnabled,
            TournamentMatchContext? context = null)
            => new(new MatchProgression(MakeTeam(opponent.Name, opponent.Strength), MakeTeam(manager.Name, manager.Strength),
                new GameContext { CaptureTimelines = true, MercyRuleEnabled = mercyRuleEnabled }, rng.Fork(2)),
                ManagerIsAway: false);

        private static Team MakeTeam(string name, double strength)
        {
            var ab = (int)System.Math.Clamp(strength, 20, 90);
            Player Pos(FieldPosition p) => new()
            {
                Position = p, Contact = ab, Power = ab, LaunchTendency = 50, Discipline = ab,
                Speed = ab, ArmStrength = ab, Fielding = ab, Catching = ab,
            };
            var order = new List<Player>
            {
                Pos(FieldPosition.Catcher), Pos(FieldPosition.FirstBase), Pos(FieldPosition.SecondBase),
                Pos(FieldPosition.ThirdBase), Pos(FieldPosition.Shortstop), Pos(FieldPosition.LeftField),
                Pos(FieldPosition.CenterField), Pos(FieldPosition.RightField),
                Pos(FieldPosition.Pitcher) with { Name = name + "P", Pitching = PitcherAttributes.LeagueAverage },
            };
            return new Team { Name = name, BattingOrder = order, PitcherSlot = 8 };
        }
    }
}
