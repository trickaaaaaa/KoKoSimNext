using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using KokoSim.Engine.Core;
using KokoSim.Engine.Match.Game;
using KokoSim.Engine.Match.Tactics;
using KokoSim.Engine.Match.Timeline.Playback;
using KokoSim.Engine.Nation;
using KokoSim.Engine.Nation.Roster;
using KokoSim.Engine.Nation.Tournaments;
using KokoSim.Engine.Players;
using KokoSim.Engine.Replay;
using KokoSim.Engine.Saves;
using KokoSim.Engine.Saves.Json;
using KokoSim.Engine.Season;
using KokoSim.Engine.Tests.Match.Game;
using KokoSim.Engine.Tests.Saves;
using Xunit;
using Xunit.Abstractions;

namespace KokoSim.Engine.Tests.Replay;

/// <summary>
/// 注目試合の再生データ（issue #1580）。「入力だけ残して同じエンジンで解き直す」ので、解き直した試合が
/// 元の試合と1ビットも違わないこと（裏試合・プレイヤーの采配つきのライブ観戦の両方）、セーブを往復しても変わらないこと、
/// 版が違えば再生を断ること、保持期間（自校＝全履歴・他校＝直近2年）を固定する。
/// </summary>
public sealed class MatchReplayTests
{
    private readonly ITestOutputHelper _output;

    public MatchReplayTests(ITestOutputHelper output) => _output = output;

    private sealed class Collector : IMatchReplayCollector
    {
        public readonly List<MatchReplayRecord> Records = new();
        public NotableReason Evaluate(School away, School home) => NotableReason.Koshien;
        public void Collect(MatchReplayRecord record) => Records.Add(record);
    }

    private static School Sch(int id, double strength, int pref = 13)
        => new() { Id = id, Name = $"校{id}", PrefectureId = pref, Strength = strength, TacticalSense = 40 + id % 30 };

    private static MatchReplayRecord Roundtrip(MatchReplayRecord r)
    {
        var book = new MatchReplayBook();
        book.Add(r);
        var text = MatchReplaySaves.ToJson(book).ToString();
        var restored = new MatchReplayBook();
        MatchReplaySaves.RestoreInto(restored, JsonValue.Parse(text).AsArray());
        return Assert.Single(restored.Records);
    }

    // ===== 裏試合 =====

    [Theory]
    [InlineData(1UL, 1)]
    [InlineData(2UL, 2)]
    [InlineData(3UL, 3)]
    [InlineData(4UL, 6)]
    [InlineData(5UL, 1)]
    public void BackgroundMatch_ReplaysToTheSameGame_BeforeAndAfterSaving(ulong seed, int roundsRemaining)
    {
        var collector = new Collector();
        var ctx = new GameContext
        {
            Weather = WeatherModel.Base, MercyRuleEnabled = roundsRemaining > 2, TieBreakEnabled = true,
        };
        var bg = new BackgroundMatchResolver(
            new NationRosters(new AiRosterDeps()), ctx, yearIndex: 1, useRegionalStadiumTier: true,
            replays: collector);
        var away = Sch(901, 55);
        var home = Sch(902, 61);
        var context = new TournamentMatchContext(roundsRemaining, MatchDay: 4, new TournamentPitchLedger());

        var original = bg.Resolve(away, home, new Xoshiro256Random(seed), context);

        var record = Assert.Single(collector.Records);
        Assert.Equal(901, record.AwaySchoolId);
        Assert.Equal(902, record.HomeSchoolId);
        Assert.Equal(original.AwayRuns, record.AwayRuns);
        Assert.Equal(roundsRemaining, record.RoundsRemaining);
        Assert.Equal(4, record.MatchDay);
        Assert.Equal(MatchReplayVersion.Current, record.EngineVersion);

        var expected = GameResultDigest.Sha256Of(original);
        Assert.Equal(expected, GameResultDigest.Sha256Of(MatchReplayer.Replay(record)!));
        Assert.Equal(expected, GameResultDigest.Sha256Of(MatchReplayer.Replay(Roundtrip(record))!));
    }

    [Fact]
    public void BackgroundMatch_NotCollectedWhenNoReason_AndCollectingDoesNotChangeTheGame()
    {
        var rosters = new NationRosters(new AiRosterDeps());
        var away = Sch(911, 50);
        var home = Sch(912, 52);
        var plain = new BackgroundMatchResolver(rosters, new GameContext(), yearIndex: 1)
            .Resolve(away, home, new Xoshiro256Random(9UL));

        var none = new NoneCollector();
        var withNone = new BackgroundMatchResolver(rosters, new GameContext(), yearIndex: 1, replays: none)
            .Resolve(away, home, new Xoshiro256Random(9UL));
        var collector = new Collector();
        var withCollector = new BackgroundMatchResolver(rosters, new GameContext(), yearIndex: 1, replays: collector)
            .Resolve(away, home, new Xoshiro256Random(9UL));

        Assert.Empty(none.Records);
        Assert.Single(collector.Records);
        Assert.Equal(GameResultDigest.Sha256Of(plain), GameResultDigest.Sha256Of(withNone));
        Assert.Equal(GameResultDigest.Sha256Of(plain), GameResultDigest.Sha256Of(withCollector));
    }

    private sealed class NoneCollector : IMatchReplayCollector
    {
        public readonly List<MatchReplayRecord> Records = new();
        public NotableReason Evaluate(School away, School home) => NotableReason.None;
        public void Collect(MatchReplayRecord record) => Records.Add(record);
    }

    [Fact]
    public void Record_FromAnOtherEngineVersion_IsRefused()
    {
        var collector = new Collector();
        new BackgroundMatchResolver(new NationRosters(new AiRosterDeps()), new GameContext(), 1, replays: collector)
            .Resolve(Sch(921, 50), Sch(922, 50), new Xoshiro256Random(3UL));
        var record = collector.Records.Single();

        var stale = record with { EngineVersion = "0-000000000000" };

        Assert.True(MatchReplayer.CanReplay(record));
        Assert.False(MatchReplayer.CanReplay(stale));
        Assert.Null(MatchReplayer.Replay(stale));
    }

    [Fact]
    public void EngineVersion_IsStableAndTracksTheSchema()
    {
        Assert.Equal(MatchReplayVersion.Current, MatchReplayVersion.Compute());
        Assert.StartsWith(MatchReplayVersion.Schema + "-", MatchReplayVersion.Current);
    }

    // ===== 自校のライブ観戦（采配つき） =====

    /// <summary>自校らしいチーム（士気・約束・バッテリー・主将・DH制）。</summary>
    private static Team OwnTeam(ulong seed)
    {
        var rng = new Xoshiro256Random(seed);
        var team = StrengthTeamFactory.Create(60.0, "自校", rng.Fork(1), modernRules: new ModernRules(), calendarYear: 2026);
        var ids = team.BenchEntry.Where(p => p.SourceId is not null).Select(p => p.SourceId!.Value).ToList();
        return team with
        {
            ManagerTacticalSense = 62, MomentumLevel = 2, Morale = 57.5, ExtraPressure = 1, CohesionDelta = -3.25,
            Captain = team.BattingOrder[3],
            BatteryInnings = new Dictionary<(int PitcherId, int CatcherId), double> { [(1, 2)] = 12.5, [(3, 4)] = 4.0 },
            PromiseGuard = new MatchPromiseGuard(ids.Take(1), ids.Skip(1).Take(1), ids.Skip(2).Take(1), Array.Empty<int>()),
        };
    }

    private static Team OpponentTeam(ulong seed, out ReplayBrainSpec spec)
    {
        var rng = new Xoshiro256Random(seed);
        var school = Sch(931, 66);
        school.ManagerTraits = new[] { ManagerTrait.BuntHeavy };
        spec = ReplayBrainSpec.FromSchool(school);
        return StrengthTeamFactory.Create(66.0, "相手校", rng.Fork(2)) with { Tactics = spec.Build() };
    }

    [Theory]
    [InlineData(11UL)]
    [InlineData(12UL)]
    [InlineData(13UL)]
    public void LiveMatchWithManagerDecisions_ReplaysToTheSameGame_BeforeAndAfterSaving(ulong seed)
    {
        var own = OwnTeam(seed);
        var opp = OpponentTeam(seed, out var brain);
        var ctx = new GameContext
        {
            CaptureTimelines = true, CapturePitches = true, PlayerControlsPitcherChanges = true,
            Weather = WeatherModel.Base, MercyRuleEnabled = false, PressureStageBonus = 2,
        };
        var recovery = own.BenchEntry.Distinct().ToDictionary(p => p, p => p.Pitching.StaminaPitches * 0.9);
        var prog = new MatchProgression(own, opp, ctx, new Xoshiro256Random(seed).Fork(2),
            awayPitchRecovery: recovery, awayPriorWeekPitches: new Dictionary<Player, int> { [own.BattingOrder[0]] = 20 });

        // 打席の頭と打席の途中（Pitch 窓）の両方で采配を入れ、最後は委任AIへスキップする。
        for (var i = 0; i < 12; i++) Assert.NotEqual(AdvancePitchResult.Finished, prog.AdvancePitch());
        var sub = prog.SubstitutionOptions(teamIsAway: true).Reserves.FirstOrDefault();
        if (sub is not null) prog.PinchHit(teamIsAway: true, sub);
        for (var i = 0; i < 30 && prog.AdvancePitch() != AdvancePitchResult.Finished; i++) { }
        var original = prog.SkipDelegateToAi(managerIsAway: true, managerTacticalSense: 62);

        var inputs = prog.CaptureReplayInputs()!;
        Assert.Contains(inputs.Decisions, d => d.Kind == GameDecisionKind.DelegateToAi);
        var record = MatchReplayFactory.Create(inputs, awayBrain: null, homeBrain: brain, original,
            NotableReason.OwnSchool, awaySchoolId: 1, homeSchoolId: 931)!;
        var expected = GameResultDigest.Sha256Of(original);

        Assert.Equal(expected, GameResultDigest.Sha256Of(MatchReplayer.Replay(record)!));
        // 決定列を落とすと別の試合になる（この検査が空振りしていない＝決定列が再生に効いている）。
        var withoutDecisions = record with { Decisions = System.Array.Empty<GameDecision>() };
        Assert.NotEqual(expected, GameResultDigest.Sha256Of(MatchReplayer.Replay(withoutDecisions)!));
        var restored = Roundtrip(record);
        Assert.Equal(expected, GameResultDigest.Sha256Of(MatchReplayer.Replay(restored)!));
        Assert.True(restored.IsOwn);
        Assert.Equal(57.5, restored.Away.Team.Morale);
        Assert.Equal(own.Captain!.Name, restored.Away.Team.Captain!.Name);
        Assert.Same(restored.Away.Team.BattingOrder[3], restored.Away.Team.Captain);
        _output.WriteLine($"JSON {MatchReplaySaves.ToJson(Book(record)).ToString().Length} chars, decisions {record.Decisions.Count}");
    }

    private static MatchReplayBook Book(params MatchReplayRecord[] rs)
    {
        var b = new MatchReplayBook();
        foreach (var r in rs) b.Add(r);
        return b;
    }

    [Fact]
    public void ForcedOutcomeMatch_IsNotRecorded()
    {
        var own = OwnTeam(1UL);
        var opp = OpponentTeam(1UL, out var brain);
        var prog = new MatchProgression(own, opp, new GameContext(), new Xoshiro256Random(1UL));
        prog.ForceNext(KokoSim.Engine.Debugging.ForcedOutcome.HomeRun);
        var inputs = prog.CaptureReplayInputs()!;
        var result = prog.SkipDelegateToAi(true, 50);

        Assert.Contains(inputs.Decisions, d => d.Forced is not null);
        Assert.Null(MatchReplayFactory.Create(inputs, null, brain, result, NotableReason.OwnSchool, 1, 2));
    }

    // ===== セーブ =====

    [Fact]
    public void SaveState_Roundtrips_TheReplayBook()
    {
        var collector = new Collector();
        var bg = new BackgroundMatchResolver(new NationRosters(new AiRosterDeps()), new GameContext(), 1, replays: collector);
        for (ulong s = 1; s <= 3; s++)
            bg.Resolve(Sch(940 + (int)s, 50), Sch(950 + (int)s, 58), new Xoshiro256Random(s));
        var state = SaveTestFixtures.BuildRichState();
        foreach (var r in collector.Records) state.MatchReplays.Add(r with { Year = 2, Reasons = NotableReason.Koshien });
        state.MatchReplays.Add(collector.Records[0] with { Id = "own", Year = 1, Reasons = NotableReason.OwnSchool });

        var json1 = SaveCodec.Serialize(state);
        Assert.True(SaveCodec.TryDeserialize(json1, out var restored, out var err), err);

        Assert.Equal(4, restored.MatchReplays.Count);
        Assert.Equal(json1, SaveCodec.Serialize(restored));
        var first = restored.MatchReplays.Records[0];
        Assert.Equal(
            GameResultDigest.Sha256Of(MatchReplayer.Replay(collector.Records[0])!),
            GameResultDigest.Sha256Of(MatchReplayer.Replay(first)!));
    }

    [Fact]
    public void OlderSaves_WithoutReplays_LoadAsAnEmptyBook()
    {
        var state = SaveTestFixtures.BuildRichState();
        var json = SaveCodec.Serialize(state)
            .Replace($"\"version\":{SaveCodec.CurrentVersion}", "\"version\":52")
            .Replace("\"matchReplays\":[]", "\"x\":[]");

        Assert.True(SaveCodec.TryDeserialize(json, out var restored, out var err), err);

        Assert.Equal(0, restored.MatchReplays.Count);
    }

    // ===== 範囲と保持期間 =====

    [Fact]
    public void Policy_PicksOwnMatches_KoshienMatches_AndHomePrefectureTopTierMatches()
    {
        var top = Sch(1, 72, pref: 13);
        var weak = Sch(2, 69, pref: 13);   // C（70未満）は上位ティアではない
        var otherPrefTop = Sch(3, 85, pref: 27);
        const int me = 99, pref = 13;

        Assert.Equal(NotableReason.None,
            MatchReplayPolicy.Evaluate(TournamentKind.Summer, weak, otherPrefTop, me, pref));
        Assert.Equal(NotableReason.PrefectureTop,
            MatchReplayPolicy.Evaluate(TournamentKind.Summer, weak, top, me, pref));
        Assert.Equal(NotableReason.None,
            MatchReplayPolicy.Evaluate(TournamentKind.Autumn, otherPrefTop, Sch(4, 30, pref: 27), me, pref));
        Assert.Equal(NotableReason.Koshien,
            MatchReplayPolicy.Evaluate(TournamentKind.SummerKoshien, weak.AtPref(27), otherPrefTop, me, pref));
        Assert.Equal(NotableReason.Koshien,
            MatchReplayPolicy.Evaluate(TournamentKind.SenbatsuKoshien, weak.AtPref(27), otherPrefTop, me, pref));
        Assert.Equal(NotableReason.Koshien | NotableReason.PrefectureTop,
            MatchReplayPolicy.Evaluate(TournamentKind.SummerKoshien, top, otherPrefTop, me, pref));
        Assert.Equal(NotableReason.OwnSchool,
            MatchReplayPolicy.Evaluate(TournamentKind.Summer, Sch(me, 50, 5), otherPrefTop, me, pref));
    }

    [Fact]
    public void Retention_KeepsOwnMatchesForever_AndOtherMatchesForTwoYears()
    {
        var book = new MatchReplayBook();
        MatchReplayRecord R(string id, int year, NotableReason why) => new() { Id = id, Year = year, Reasons = why };
        book.Add(R("own-old", 1, NotableReason.OwnSchool));
        book.Add(R("own-and-koshien-old", 1, NotableReason.OwnSchool | NotableReason.Koshien));
        book.Add(R("koshien-y1", 1, NotableReason.Koshien));
        book.Add(R("top-y2", 2, NotableReason.PrefectureTop));
        book.Add(R("koshien-y3", 3, NotableReason.Koshien));

        var dropped = book.Prune(currentYear: 3);

        Assert.Equal(1, dropped);
        Assert.Equal(
            new[] { "own-old", "own-and-koshien-old", "top-y2", "koshien-y3" },
            book.Records.Select(r => r.Id).ToArray());
        Assert.Equal(1, book.Prune(currentYear: 4));
        Assert.DoesNotContain(book.Records, r => r.Id == "top-y2");
    }

    [Fact]
    public void Transfer_SwapsTheManagerSchoolKeyOnBothSides()
    {
        var book = new MatchReplayBook();
        book.Add(new MatchReplayRecord { Id = "own", AwaySchoolId = -1, HomeSchoolId = 77 });
        book.Add(new MatchReplayRecord { Id = "arriving", AwaySchoolId = 500, HomeSchoolId = 78 });

        book.HandOverSchool(departingSchoolId: 900, arrivingSchoolId: 500);

        var own = book.Find("own")!;
        var arriving = book.Find("arriving")!;
        Assert.Equal((900, 77), (own.AwaySchoolId, own.HomeSchoolId));
        Assert.Equal((-1, 78), (arriving.AwaySchoolId, arriving.HomeSchoolId));
    }

    [Fact]
    public void Book_AddingTheSameMatchTwice_KeepsOneRecord()
    {
        var book = new MatchReplayBook();
        book.Add(new MatchReplayRecord { Id = "a", AwayRuns = 1 });
        book.Add(new MatchReplayRecord { Id = "a", AwayRuns = 2 });

        Assert.Equal(2, Assert.Single(book.Records).AwayRuns);
    }

    // ===== 取りこぼしの見張り（Player / Team に項目が増えたら、保存に入れるか意図して外すかを選ばせる） =====

    [Fact]
    public void EveryPlayerProperty_IsEitherSavedOrIntentionallyDerived()
    {
        // 保存する（PlayerSaves＋MatchReplaySaves の追加3つ）か、保存しなくてよい（導出・観測専用）と判断済みのもの。
        var covered = new HashSet<string>
        {
            "Name", "Position", "SourceId", "UniformNumber", "Grade", "Throws", "Bats", "LeftExperiencePa",
            "Contact", "Power", "LaunchTendency", "Discipline", "Speed", "ArmStrength", "ThrowAccuracy", "Fielding",
            "FieldingByPosition", "PositionAptitudes", "Catching", "Lead", "PitcherSense", "Bunt", "Steal", "Baserunning",
            "Mental", "Leadership", "Receptiveness", "Diligence", "Selflessness", "SelfGrowthJitter", "BuntSuccessBonus",
            "ChanceHitFactor", "Skills", "Phenom", "BatterArchetype", "BatterArchetypeFocus", "BatterArchetypeIntensity",
            "Condition", "ConditionValue", "Injury", "InjuryResistance", "Pitching",
        };
        // 導出（Personality は4軸から）・試合の解決が読まない隠しの表示用（StyleOffset）。
        var derivedOrUnread = new HashSet<string> { "Personality", "StyleOffset" };

        var actual = typeof(Player).GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(p => p.GetIndexParameters().Length == 0).Select(p => p.Name).ToHashSet();

        Assert.Empty(actual.Except(covered).Except(derivedOrUnread));
        Assert.Empty(covered.Except(actual));
    }

    [Fact]
    public void EveryTeamProperty_IsEitherSavedOrIntentionallyDerived()
    {
        var covered = new HashSet<string>
        {
            "Name", "BattingOrder", "PitcherSlot", "Reserves", "ManagerTacticalSense", "Captain", "DhSlot",
            "StartingPitcher", "RelieveMarginFactor", "MomentumLevel", "Morale", "ExtraPressure", "BatteryInnings",
            "CohesionDelta", "PromiseGuard",
            "Tactics",   // 敵AIは ReplayBrainSpec から組み直す。自校は決定列
        };
        var derived = new HashSet<string> { "BenchEntry", "UsesDh" };

        var actual = typeof(Team).GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(p => p.GetIndexParameters().Length == 0).Select(p => p.Name).ToHashSet();

        Assert.Empty(actual.Except(covered).Except(derived));
        Assert.Empty(covered.Except(actual));
    }

    [Fact]
    public void EveryPerMatchContextValue_IsSavedInTheSpec()
    {
        // 試合ごとに Shell が変える GameContext の項目は ReplayContextSpec が持つ。係数のブロックは持たない（版が守る）。
        var perMatch = new HashSet<string>
        {
            "Field", "StadiumId", "StadiumName", "RegulationInnings", "MaxInnings", "MercyRuleEnabled", "TieBreakEnabled",
            "TieBreakStartInning", "WeeklyPitchLimit", "PressureStageBonus", "RetirementOnLine",
            "PlayerControlsPitcherChanges", "Weather",
        };
        var ctx = new GameContext();
        var spec = ReplayContextSpec.From(ctx with
        {
            MercyRuleEnabled = true, TieBreakEnabled = true, PressureStageBonus = 3, RetirementOnLine = true,
            PlayerControlsPitcherChanges = true, StadiumId = "x", StadiumName = "y", WeeklyPitchLimit = 500,
            RegulationInnings = 7, MaxInnings = 12, TieBreakStartInning = 8,
            Field = new KokoSim.Engine.Match.Field.FieldGeometry { LeftFenceM = 90, RightFenceM = 91, CenterFenceM = 120, FenceHeightM = 3.5 },
            Weather = new WeatherCoefficients { MeanTemperatureC = 20, RainProb = 0.3 },
        });

        var back = spec.ApplyTo(new GameContext());

        Assert.True(back.MercyRuleEnabled && back.TieBreakEnabled && back.RetirementOnLine && back.PlayerControlsPitcherChanges);
        Assert.Equal(3, back.PressureStageBonus);
        Assert.Equal("x", back.StadiumId);
        Assert.Equal(500, back.WeeklyPitchLimit);
        Assert.Equal(7, back.RegulationInnings);
        Assert.Equal(12, back.MaxInnings);
        Assert.Equal(8, back.TieBreakStartInning);
        Assert.Equal(90, back.Field.LeftFenceM);
        Assert.Equal(3.5, back.Field.FenceHeightM);
        Assert.Equal(0.3, back.Weather!.RainProb);
        Assert.Equal(20, back.Weather.MeanTemperatureC);
        Assert.Equal(perMatch.Count, perMatch.Intersect(typeof(GameContext).GetProperties().Select(p => p.Name)).Count());
    }
}

internal static class SchoolTestExt
{
    public static School AtPref(this School s, int pref)
        => new() { Id = s.Id, Name = s.Name, PrefectureId = pref, Strength = s.Strength };
}
