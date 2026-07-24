using KokoSim.Balance;
using KokoSim.Config;
using Xunit;

namespace KokoSim.Engine.Tests.Balance;

/// <summary>
/// Phase 2 統計回帰（不変条件#5）。data/coefficients.yaml で多数の試合を回し、
/// 得点分布・試合時間・イニング数が data/balance-targets.yaml の games_10k 帯に収まることを保証する。
/// テスト時間短縮のため2000試合で検証（帯は十分な余裕を持たせている）。
/// </summary>
[Trait("Category", "Heavy")] // 数千試合の統計回帰。日常ループでは --filter "Category!=Heavy" で除外
public sealed class GameRegressionTests
{
    [Theory]
    [InlineData(42UL)]
    [InlineData(2024UL)]
    public void Games_StayWithinTargetBands(ulong seed)
    {
        var coeffPath = BalanceRegressionTests.FindDataFile("coefficients.yaml");
        var targets = BalanceTargetsLoader.LoadGameFromFile(
            BalanceRegressionTests.FindDataFile("balance-targets.yaml"));

        var s = GameSimulation.Run(2000, seed, coeffPath);

        Assert.True(targets.RunsPerTeam.Contains(s.AverageRunsPerTeam),
            $"得点/チーム {s.AverageRunsPerTeam:F2} が帯外");
        Assert.True(targets.PitchesPerGame.Contains(s.AveragePitchesPerGame),
            $"球数/試合 {s.AveragePitchesPerGame:F0} が帯外");
        Assert.True(targets.MinutesPerGame.Contains(s.AverageMinutes),
            $"試合時間 {s.AverageMinutes:F0}分 が帯外");
        Assert.True(targets.InningsPerGame.Contains(s.AverageInnings),
            $"イニング {s.AverageInnings:F2} が帯外");
        // 本塁クロスプレー憤死/試合の参考帯（設計書12 §3 F2, Q9）。広め＝warn 相当。
        Assert.True(targets.HomePlayOutsPerGame.Contains(s.AverageHomePlayOutsPerGame),
            $"本塁憤死/試合 {s.AverageHomePlayOutsPerGame:F3} が参考帯外");
        // 三塁憤死/試合の参考帯（単打の一塁→三塁レース, Issue #89）。広め＝warn 相当。
        Assert.True(targets.ThirdPlayOutsPerGame.Contains(s.AverageThirdPlayOutsPerGame),
            $"三塁憤死/試合 {s.AverageThirdPlayOutsPerGame:F3} が参考帯外");
        // 失策数/試合（両軍計）の目標帯（issue #123: 甲子園実測≈2.1〜2.7）。
        Assert.True(targets.ErrorsPerGame.Contains(s.ErrorsPerGame),
            $"失策数/試合 {s.ErrorsPerGame:F2} が帯外");

        // design-14 第1段（P1）: 采配Brain不要＝無指示でも発生する常時系（野選/振り逃げ/失策連鎖）。
        Assert.True(targets.FieldersChoicePerGame.Contains(s.FieldersChoicePerGame),
            $"野選(FC)/試合 {s.FieldersChoicePerGame:F3} が参考帯外");
        Assert.True(targets.DroppedThirdStrikePerGame.Contains(s.DroppedThirdStrikePerGame),
            $"振り逃げ/試合 {s.DroppedThirdStrikePerGame:F3} が参考帯外");
        Assert.True(targets.ErrorExtraAdvancePerGame.Contains(s.ErrorExtraAdvancePerGame),
            $"失策連鎖/試合 {s.ErrorExtraAdvancePerGame:F3} が参考帯外");
        Assert.True(targets.WildPitchPerGame.Contains(s.WildPitchesPerGame),
            $"暴投・パスボール/試合 {s.WildPitchesPerGame:F3} が参考帯外");
    }

    /// <summary>
    /// design-14 第1段（P1）のうち敬遠・重盗・牽制は采配Brainが盗塁/敬遠を選ばない限り発火しないため、
    /// 両チームに StandardTacticsBrain を付与した専用シミュレーションで検証する（games_10k_tactics）。
    /// </summary>
    [Theory]
    [InlineData(42UL)]
    [InlineData(2024UL)]
    public void TacticsGames_StayWithinTargetBands(ulong seed)
    {
        var coeffPath = BalanceRegressionTests.FindDataFile("coefficients.yaml");
        var targets = BalanceTargetsLoader.LoadGameTacticsFromFile(
            BalanceRegressionTests.FindDataFile("balance-targets.yaml"));

        var s = GameSimulation.Run(2000, seed, coeffPath, useTacticsBrain: true);

        Assert.True(targets.RunsPerTeam.Contains(s.AverageRunsPerTeam),
            $"（Brainつき）得点/チーム {s.AverageRunsPerTeam:F2} が帯外");
        Assert.True(targets.PickoffPerGame.Contains(s.PickoffsPerGame),
            $"牽制アウト/試合 {s.PickoffsPerGame:F3} が参考帯外");
        Assert.True(targets.IntentionalWalkPerGame.Contains(s.IntentionalWalksPerGame),
            $"敬遠/試合 {s.IntentionalWalksPerGame:F3} が参考帯外");
        Assert.True(targets.DoubleStealThirdBreakPerGame.Contains(s.DoubleStealThirdBreaksPerGame),
            $"一・三塁重盗/試合 {s.DoubleStealThirdBreaksPerGame:F3} が参考帯外");

        // 1球采配（設計書15 Phase C-2）: 追い込まれ矯正/3-0待て/決め球切替の影響を最も受ける指標。
        Assert.True(targets.StrikeoutRate.Contains(s.StrikeoutRate),
            $"（Brainつき）三振率 {s.StrikeoutRate:P2} が帯外");
        Assert.True(targets.WalkRate.Contains(s.WalkRate),
            $"（Brainつき）四球率 {s.WalkRate:P2} が帯外");
        Assert.True(targets.HomeRunRate.Contains(s.HomeRunRate),
            $"（Brainつき）本塁打率 {s.HomeRunRate:P2} が帯外");
    }

    /// <summary>
    /// 夏場の天候（気温）モデル（Issue #120）。GameSimulation を applyWeather:true で回し、
    /// (1) 天候ありでも決定論（同シード同結果）が保たれること、
    /// (2) 夏場は点が動きやすい＝平時（天候オフ）以上の得点になること（創発・方向性）、
    /// (3) 決定3: まず係数だけ入れて実測 → 一般帯（games_10k）をわずかに外れたため夏場帯（games_10k_summer）を
    ///     単独追加した。その夏場帯に収まること。
    /// を固定する。
    /// </summary>
    [Theory]
    [InlineData(42UL)]
    [InlineData(2024UL)]
    public void SummerWeatherGames_StayWithinTargetBands(ulong seed)
    {
        var coeffPath = BalanceRegressionTests.FindDataFile("coefficients.yaml");
        var targetsPath = BalanceRegressionTests.FindDataFile("balance-targets.yaml");
        var summerTargets = BalanceTargetsLoader.LoadGameSummerFromFile(targetsPath);

        var baseline = GameSimulation.Run(2000, seed, coeffPath);                    // 天候オフ＝平時
        var summer = GameSimulation.Run(2000, seed, coeffPath, applyWeather: true);   // 天候オン＝夏場

        // (2) 夏場は点が動きやすい（空気密度低下で打球が伸び、暑さで終盤の投手が早く落ちる）。
        Assert.True(summer.AverageRunsPerTeam >= baseline.AverageRunsPerTeam,
            $"夏場 得点/チーム {summer.AverageRunsPerTeam:F3} は平時 {baseline.AverageRunsPerTeam:F3} 以上のはず");

        // (3) 夏場帯（games_10k_summer, 一般帯を土台に runs のみ上書き）に収まること。
        Assert.True(summerTargets.RunsPerTeam.Contains(summer.AverageRunsPerTeam),
            $"夏場 得点/チーム {summer.AverageRunsPerTeam:F2} が夏場帯 [{summerTargets.RunsPerTeam.Min}, {summerTargets.RunsPerTeam.Max}] 外");
        Assert.True(summerTargets.PitchesPerGame.Contains(summer.AveragePitchesPerGame),
            $"夏場 球数/試合 {summer.AveragePitchesPerGame:F0} が帯外");
        Assert.True(summerTargets.InningsPerGame.Contains(summer.AverageInnings),
            $"夏場 イニング {summer.AverageInnings:F2} が帯外");
    }

    [Fact]
    public void SummerWeatherSimulation_IsDeterministic()
    {
        // 気温を専用Forkで引いても同シード同結果（不変条件#2・DoD）。
        var coeffPath = BalanceRegressionTests.FindDataFile("coefficients.yaml");
        var a = GameSimulation.Run(500, 7, coeffPath, applyWeather: true);
        var b = GameSimulation.Run(500, 7, coeffPath, applyWeather: true);
        Assert.Equal(a.TotalTeamRuns, b.TotalTeamRuns);
        Assert.Equal(a.TotalPitches, b.TotalPitches);
        Assert.Equal(a.HomeWins, b.HomeWins);
    }

    [Fact]
    public void WeatherOff_MatchesLegacyBaseline_Exactly()
    {
        // 天候オフ（既定）は weather ブロックの有無に関係なく従来と1ビットも変わらない（帯不変の担保）。
        var coeffPath = BalanceRegressionTests.FindDataFile("coefficients.yaml");
        var withWeatherStripped = GameSimulation.Run(500, 7, coeffPath);                    // applyWeather:false
        var summer = GameSimulation.Run(500, 7, coeffPath, applyWeather: true);
        Assert.NotEqual(withWeatherStripped.TotalTeamRuns, summer.TotalTeamRuns);           // 天候は確かに効いている
    }

    [Fact]
    public void Simulation_IsDeterministic()
    {
        var coeffPath = BalanceRegressionTests.FindDataFile("coefficients.yaml");
        var a = GameSimulation.Run(500, 7, coeffPath);
        var b = GameSimulation.Run(500, 7, coeffPath);
        Assert.Equal(a.TotalTeamRuns, b.TotalTeamRuns);
        Assert.Equal(a.TotalPitches, b.TotalPitches);
        Assert.Equal(a.HomeWins, b.HomeWins);
    }

    [Fact]
    public void HomeWinRate_IsNearFiftyPercent()
    {
        // 両チームは対称なランダム生成なので後攻勝率は概ね5割（サヨナラ規定の妥当性確認）。
        var coeffPath = BalanceRegressionTests.FindDataFile("coefficients.yaml");
        var s = GameSimulation.Run(2000, 99, coeffPath);
        Assert.InRange(s.HomeWinRate, 0.44, 0.58);
    }
}
