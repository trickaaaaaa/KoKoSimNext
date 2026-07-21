using System.Linq;
using KokoSim.Config;
using KokoSim.Engine.Core;
using KokoSim.Engine.Debugging;
using KokoSim.Engine.Match.Game;
using KokoSim.Engine.Tests.Match.Game;
using Xunit;

namespace KokoSim.Engine.Tests.Debugging;

/// <summary>
/// 設計書17 F2（注入＝場面ジャンプ）の受け入れ:
///  - 全シナリオが例外なく起動し、宣言した状況（イニング・表裏・アウト・走者・カウント・得点・打順・球数）で始まる
///  - <c>data/debug/</c> の欠損は正常系＝0件で起動し例外を投げない（Q18-2 の (4)）
///  - 注入した試合はトレースヘッダと GameResult に scenario id が刻まれる＝digest・統計から外せる
/// </summary>
public sealed class ScenarioTests
{
    private static ScenarioCatalog Catalog() => DebugScenarioLoader.LoadFromRepoOrEmpty();

    [Fact]
    public void Catalog_LoadsFromRepo()
    {
        var catalog = Catalog();
        Assert.True(catalog.Count >= 5, "data/debug/scenarios.yaml が読めていない");
        Assert.Contains(catalog.All, s => s.Id == "bases-loaded-9th");
    }

    /// <summary>リリースビルドでは data/debug/ が存在しない。欠損は正常系（0件・例外なし）。</summary>
    [Fact]
    public void MissingScenarioFile_IsNormal_NotAnError()
    {
        Assert.Equal(0, DebugScenarioLoader.LoadFromFileOrEmpty(null).Count);
        Assert.Equal(0, DebugScenarioLoader.LoadFromFileOrEmpty("/nonexistent/data/debug/scenarios.yaml").Count);
        Assert.Equal(0, ScenarioCatalog.Empty.Count);
        Assert.False(ScenarioCatalog.Empty.TryGet("bases-loaded-9th", out _));
    }

    /// <summary>壊れたYAMLは黙って0件にしない（編集ミスに気づけなくなるため）。</summary>
    [Fact]
    public void ScenarioWithoutId_Throws()
    {
        Assert.Throws<System.IO.InvalidDataException>(() =>
            DebugScenarioLoader.Parse("scenarios:\n  - name: id無し\n    inning: 3\n"));
    }

    /// <summary>全シナリオが宣言どおりの局面で起動する（設計書17 §9 F2 DoD）。</summary>
    [Fact]
    public void EveryScenario_StartsInTheDeclaredSituation()
    {
        foreach (var def in Catalog().All)
        {
            var sink = new RecordingTraceSink();
            var built = ScenarioBuilder.Build(def, new GameContext { CaptureTrace = true, TraceSink = sink }, 42UL);
            var p = GameEngine.NewProgress(built.Away, built.Home, built.Ctx,
                new Xoshiro256Random(built.Seed), null, built.Start);

            // 打席に入る前から決まっている状態（NewProgress で注ぎ終わっている）。
            Assert.Equal(def.Inning, p.Inning);
            Assert.Equal(def.AwayScore, p.Away.Runs);
            Assert.Equal(def.HomeScore, p.Home.Runs);
            Assert.Equal(def.Batter, p.OffenseOf(def.Top).CurrentBatterOrder);
            Assert.Equal(def.PitcherFatigue, (int)p.OffenseOf(!def.Top).FatiguePitches);
            Assert.Equal(def.Id, built.Ctx.ScenarioId);

            // 半イニングのローカル状態（塁・アウト・カウント）は最初の1球の観測で確認する。
            using var steps = GameEngine.Steps(p).GetEnumerator();
            Assert.True(steps.MoveNext(), $"{def.Id}: 1球も進まなかった");
            steps.MoveNext(); // Pitch 窓の次＝1球目の解決

            var first = Assert.Single(sink.Pitches.Take(1));
            Assert.Equal(def.Inning, first.Inning);
            Assert.Equal(def.Top, first.IsTop);
            Assert.Equal(def.Outs, first.Outs);
            Assert.Equal(def.Balls, first.BallsBefore);
            Assert.Equal(def.Strikes, first.StrikesBefore);
            Assert.Equal(def.PitcherFatigue, first.PitchingFatigue);
            Assert.Equal(def.Id, sink.Header!.ScenarioId);
        }
    }

    /// <summary>走者は宣言した塁だけに置かれる（タイブレークの継続打者と同じ流儀で「直前の打者たち」）。</summary>
    [Fact]
    public void DeclaredBases_AreOccupiedExactly()
    {
        var def = new ScenarioDefinition
        {
            Id = "t", Away = "AI:tier=C", Home = "AI:tier=C",
            Inning = 6, Top = true, Outs = 1, Bases = new[] { 1, 3 },
        };
        var built = ScenarioBuilder.Build(def, new GameContext(), 5UL);
        var p = GameEngine.NewProgress(built.Away, built.Home, built.Ctx, new Xoshiro256Random(5UL), null, built.Start);

        using var steps = GameEngine.Steps(p).GetEnumerator();
        steps.MoveNext();                       // 最初の Pitch 窓（この時点で塁は組み上がっている）
        while (steps.Current.Kind != GameStepKind.PlateAppearance && steps.MoveNext()) { }

        // 打席が確定した時点のスナップショットで確認する（観測seam）。
        Assert.NotNull(p.CurrentBases);
        var log = p.Log[0];
        Assert.True(log.BaseFirstBefore);
        Assert.False(log.BaseSecondBefore);
        Assert.True(log.BaseThirdBefore);
        Assert.Equal(1, log.OutsBefore);
        Assert.Equal(6, log.Inning);
        Assert.True(log.IsTop);
    }

    /// <summary>「裏から始める」宣言のとき、その回の表は飛ぶ（次の回からは通常どおり表→裏）。</summary>
    [Fact]
    public void StartingInBottom_SkipsThatInningsTop()
    {
        var def = new ScenarioDefinition
        {
            Id = "t", Away = "AI:tier=C", Home = "AI:tier=C",
            Inning = 4, Top = false, AwayScore = 1,
        };
        var built = ScenarioBuilder.Build(def, new GameContext(), 8UL);
        var p = GameEngine.NewProgress(built.Away, built.Home, built.Ctx, new Xoshiro256Random(8UL), null, built.Start);
        foreach (var _ in GameEngine.Steps(p)) { }
        var r = GameEngine.BuildResult(p);

        Assert.Equal(4, r.Log[0].Inning);
        Assert.False(r.Log[0].IsTop);           // 最初の打席は4回裏
        Assert.Contains(r.Log, e => e.Inning == 5 && e.IsTop); // 5回からは表も打つ
    }

    /// <summary>途中カウントからの開始は投球ループの挙動を変えない（カウントは状態でしかない）。</summary>
    [Fact]
    public void StartingCountAppliesToTheFirstBatterOnly()
    {
        var sink = new RecordingTraceSink();
        var def = new ScenarioDefinition
        {
            Id = "t", Away = "AI:tier=C", Home = "AI:tier=C",
            Inning = 3, Top = true, Balls = 3, Strikes = 2,
        };
        var built = ScenarioBuilder.Build(def, new GameContext { CaptureTrace = true, TraceSink = sink }, 4UL);
        var p = GameEngine.NewProgress(built.Away, built.Home, built.Ctx, new Xoshiro256Random(4UL), null, built.Start);
        foreach (var _ in GameEngine.Steps(p)) { }

        Assert.Equal(3, sink.Pitches[0].BallsBefore);
        Assert.Equal(2, sink.Pitches[0].StrikesBefore);
        // 最初の打席は1球で必ず決着する（3-2 から Ball/CalledStrike/SwingingStrike のいずれでも確定）か、
        // ファウルで粘るかのどちらか。次の打者は必ず 0-0 から始まる。
        var secondPa = sink.PlateAppearances[1];
        var pitchesInFirstPa = sink.PlateAppearances[0].Pitches;
        Assert.Equal(0, sink.Pitches[pitchesInFirstPa].BallsBefore);
        Assert.Equal(0, sink.Pitches[pitchesInFirstPa].StrikesBefore);
        Assert.True(secondPa.Pitches > 0);
    }

    [Fact]
    public void UnknownTeamSpec_Throws()
    {
        var def = new ScenarioDefinition { Id = "t", Away = "AI:tier=Z", Home = "player" };
        Assert.Throws<System.ArgumentException>(() => ScenarioBuilder.Build(def, new GameContext(), 1UL));

        var def2 = new ScenarioDefinition { Id = "t", Away = "とある高校", Home = "player" };
        Assert.Throws<System.ArgumentException>(() => ScenarioBuilder.Build(def2, new GameContext(), 1UL));
    }

    [Fact]
    public void InvalidScenario_IsRejectedEarly()
    {
        Assert.Throws<System.ArgumentException>(() =>
            ScenarioBuilder.Build(new ScenarioDefinition { Id = "t", Away = "AI:tier=C", Outs = 3 }, new GameContext(), 1UL));
        Assert.Throws<System.ArgumentException>(() =>
            ScenarioBuilder.Build(new ScenarioDefinition { Id = "t", Away = "AI:tier=C", Balls = 4 }, new GameContext(), 1UL));
        Assert.Throws<System.ArgumentException>(() =>
            ScenarioBuilder.Build(new ScenarioDefinition { Id = "t", Away = "AI:tier=C", Batter = 10 }, new GameContext(), 1UL));
    }

    /// <summary>注入しない試合は従来と1ビットも変わらない（ScenarioStart=null が既定パス）。</summary>
    [Fact]
    public void NoScenario_MatchesBaseline()
    {
        var baseline = GameResultDigest.Sha256Of(DeterminismCards.Run("avg", 3UL));
        var p = GameEngine.NewProgress(
            DeterminismCards.Team("A"), DeterminismCards.Team("H"), new GameContext(), new Xoshiro256Random(3UL),
            null, scenarioStart: null);
        foreach (var _ in GameEngine.Steps(p)) { }
        Assert.Equal(baseline, GameResultDigest.Sha256Of(GameEngine.BuildResult(p)));
    }

}
