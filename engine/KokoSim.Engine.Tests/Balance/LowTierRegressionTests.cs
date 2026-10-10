using System.Collections.Generic;
using KokoSim.Balance;
using KokoSim.Config;
using KokoSim.Engine.Season;
using Xunit;

namespace KokoSim.Engine.Tests.Balance;

/// <summary>
/// 低ティア・非対称マッチの統計回帰（issue #536 / #578 Step 4・不変条件#5）。
///
/// <para><b>既存4帯はここを守れない。</b> <c>games_10k</c> の入力は全能力 N(50,12) の
/// 「平均的なチーム同士」で、ティアも非対称マッチアップも入力に無い。実際 #536 では
/// 「開始直後の自校が1〜2ティア上に一方的にやられる（完封され率 21〜40%）」が
/// <b>帯を全部緑にしたまま</b>起きていた。しかも <see cref="Nation.StrengthTeamFactory"/> と
/// <see cref="ProspectGenerator"/> はどちらも既存4帯の経路を通らない
/// （メモ「帯はロスター生成を測っていない」）。</para>
///
/// <para>ここが守るのは<b>体験そのもの</b>＝実プレイと同じ組み合わせ（開始直後の自校 vs 相手校）での
/// 完封され率と、表示1ティアあたりの得点の落ち方。校正前（#578 着手前）の値は
/// D(50) 2.50点・21.2% ／ C(60) 1.39点・40.3% ／ 勾配0.42 で、下の帯を<b>3指標とも</b>外れる
/// ＝ティア圧縮を戻したり球速の価値を上げ過ぎたりすれば赤くなる。</para>
/// </summary>
[Trait("Category", "Heavy")]   // 自校 vs 3強度 × 400試合のフルエンジン実測（約20秒）
public sealed class LowTierRegressionTests
{
    /// <summary>ほぼ互角(40) / 1ティア上(50) / 2ティア上(60)。勾配は 40→50→60 の比で見る。</summary>
    private static readonly double[] Ladder = { 40.0, 50.0, 60.0 };

    [Theory]
    [InlineData(42UL)]
    [InlineData(2024UL)]
    public void LowTierMatchups_StayWithinTargetBands(ulong seed)
    {
        var targets = BalanceTargetsLoader.LoadLowTierFromFile(
            BalanceRegressionTests.FindDataFile("balance-targets.yaml"));
        var coeffPath = BalanceRegressionTests.FindDataFile("coefficients.yaml");

        // レポート（out/tier-matchup-*.md）と同じ経路で測る＝帯が守るものと目で見るものを一致させる。
        var cells = TierMatchupSimulation.MeasureManagerLadder(
            games: 400, seed, coeffPath, new RosterCoefficients(), Ladder);

        var e40 = cells[0];
        var d50 = cells[1];
        var c60 = cells[2];

        Assert.True(targets.ManagerRunsVsE40.Contains(e40.AwayRunsPerGame),
            $"自校の得点 vs E(40) {e40.AwayRunsPerGame:F2} が帯外");
        Assert.True(targets.ManagerRunsVsD50.Contains(d50.AwayRunsPerGame),
            $"自校の得点 vs D(50) {d50.AwayRunsPerGame:F2} が帯外");
        Assert.True(targets.ManagerRunsVsC60.Contains(c60.AwayRunsPerGame),
            $"自校の得点 vs C(60) {c60.AwayRunsPerGame:F2} が帯外");

        // 体験の主指標。校正前は 21.2% / 40.3% で、どちらも帯の上限を大きく超える。
        Assert.True(targets.ManagerShutoutRateVsD50.Contains(d50.AwayShutoutRate),
            $"自校の完封され率 vs D(50) {d50.AwayShutoutRate:P1} が帯外");
        Assert.True(targets.ManagerShutoutRateVsC60.Contains(c60.AwayShutoutRate),
            $"自校の完封され率 vs C(60) {c60.AwayShutoutRate:P1} が帯外");

        // 勾配＝表示1ティア（強度10ポイント）ごとの得点比。40→50 と 50→60 の平均で見る
        // （片方だけだと、その1マスの乱数の癖と勾配の変化が区別できない）。
        var ratio = 0.5 * (d50.AwayRunsPerGame / e40.AwayRunsPerGame
                           + c60.AwayRunsPerGame / d50.AwayRunsPerGame);
        Assert.True(targets.RunsRatioPerTier.Contains(ratio),
            $"1ティアあたりの得点比 {ratio:F3} が帯外（校正前 0.42・校正後 0.61–0.67）");
    }

    /// <summary>
    /// 帯の意味を固定する: <b>表示→物理の効き（設計書21）を恒等に戻すと赤くなる</b>こと。帯を置いただけで実は何も
    /// 守っていない（どんな係数でも通る幅だった）という失敗を防ぐ回帰。
    /// 2026-08-19 以前は「ティア圧縮を外す」で同じことを確かめていたが、圧縮は恒等が既定になり、勾配を寝かせる役は
    /// <c>ability_effect</c>（全員対称）へ移ったので、寝かせない世界＝効き 1.0 で測る。
    /// </summary>
    [Fact]
    public void Bands_WouldCatch_TheUnmutedGradient()
    {
        var targets = BalanceTargetsLoader.LoadLowTierFromFile(
            BalanceRegressionTests.FindDataFile("balance-targets.yaml"));
        var coeffPath = BalanceRegressionTests.FindDataFile("coefficients.yaml");

        // 効き 1.0（寝かせない）＝設計書21 以前の物理層に戻した世界。生成は既定（圧縮恒等）のまま。
        var cells = TierMatchupSimulation.MeasureManagerLadder(
            games: 400, 42UL, coeffPath, new RosterCoefficients(), Ladder,
            effect: KokoSim.Engine.Players.AbilityEffectCoefficients.Identity);

        var failures = new List<string>();
        if (!targets.ManagerRunsVsD50.Contains(cells[1].AwayRunsPerGame)) failures.Add("得点vsD50");
        if (!targets.ManagerShutoutRateVsD50.Contains(cells[1].AwayShutoutRate)) failures.Add("完封され率vsD50");
        if (!targets.ManagerShutoutRateVsC60.Contains(cells[2].AwayShutoutRate)) failures.Add("完封され率vsC60");

        Assert.True(failures.Count >= 2,
            "効きを恒等に戻しても帯が緑のまま＝帯が広すぎて何も守っていない。"
            + $"実測: 得点vsD50={cells[1].AwayRunsPerGame:F2} / 完封vsD50={cells[1].AwayShutoutRate:P1}"
            + $" / 完封vsC60={cells[2].AwayShutoutRate:P1}");
    }
}
