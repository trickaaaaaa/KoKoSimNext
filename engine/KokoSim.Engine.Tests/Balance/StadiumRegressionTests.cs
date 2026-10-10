using KokoSim.Balance;
using KokoSim.Config;
using KokoSim.Engine.Match.Field;
using Xunit;

namespace KokoSim.Engine.Tests.Balance;

/// <summary>
/// 球場①物理層の統計回帰（設計書13-stadiums §4、不変条件#5）。
/// フェンス距離・高さのパラメータ化が本塁打率・長打率・得点に「狭>標準>広」の順で効くことを保証する。
/// AggregateMatch（大会勝敗）は不変なので、既存 balance-targets の帯には影響しない。
/// </summary>
[Trait("Category", "Heavy")] // 数万打席＋数千試合の統計回帰。日常ループでは --filter "Category!=Heavy" で除外
public sealed class StadiumRegressionTests
{
    private static FieldGeometry Stadium(string id)
    {
        var catalog = StadiumCatalogLoader.LoadCatalog(BalanceRegressionTests.FindDataFile("stadiums.yaml"));
        return catalog[id].ToFieldGeometry();
    }

    [Theory]
    [InlineData(42UL)]
    [InlineData(2024UL)]
    public void NarrowStandardWide_OrderHomeRunsAndSlugging(ulong seed)
    {
        var coeff = BalanceRegressionTests.FindDataFile("coefficients.yaml");
        const int atBats = 40000;

        var narrow = AtBatSimulation.Run(atBats, seed, coeff, Stadium("municipal_small_01"));
        var standard = AtBatSimulation.Run(atBats, seed, coeff, Stadium("standard_01"));
        var wide = AtBatSimulation.Run(atBats, seed, coeff, Stadium("pref_large_01"));

        // HR% は 狭 > 標準 > 広
        Assert.True(narrow.HomeRunRate > standard.HomeRunRate,
            $"狭 HR% {narrow.HomeRunRate:P2} ≤ 標準 {standard.HomeRunRate:P2}");
        Assert.True(standard.HomeRunRate > wide.HomeRunRate,
            $"標準 HR% {standard.HomeRunRate:P2} ≤ 広 {wide.HomeRunRate:P2}");

        // 長打率(SLG)は 狭 > 広（両端）。標準は両端の間（±0.01 の揺れは許す）。
        // issue homerun-rate-explodes-at-high-power（2026-08-18）で本塁打が半減し、広い球場では本塁打が減る一方で
        // 外野の間に落ちる二塁打・三塁打が増えるので、隣接ティア（標準 vs 広）の SLG 差は 40,000 打席でも
        // 揺れ（実測 標準 .357 vs 広 .361）に埋もれる＝厳密な3段順序は本塁打が多かった頃の性質。
        Assert.True(narrow.Slugging > wide.Slugging,
            $"SLG順序が崩れた: 狭{narrow.Slugging:F3} / 標準{standard.Slugging:F3} / 広{wide.Slugging:F3}");
        Assert.InRange(standard.Slugging, wide.Slugging - 0.010, narrow.Slugging + 0.010);

        // 差は現実的な幅（隣接ティアで HR% 差 ≈±1%、全体でも数%に収まる）
        Assert.InRange(narrow.HomeRunRate - wide.HomeRunRate, 0.005, 0.05);
    }

    [Theory]
    [InlineData(42UL)]
    [InlineData(2024UL)]
    public void NarrowStandardWide_OrderRunsPerTeam(ulong seed)
    {
        var coeff = BalanceRegressionTests.FindDataFile("coefficients.yaml");
        const int games = 3000;

        var narrow = GameSimulation.Run(games, seed, coeff, Stadium("municipal_small_01"));
        var standard = GameSimulation.Run(games, seed, coeff, Stadium("standard_01"));
        var wide = GameSimulation.Run(games, seed, coeff, Stadium("pref_large_01"));

        // 差の大きい両端（狭 vs 標準／狭 vs 広）は頑健。標準 vs 広は SLG と同じ理由（本塁打半減,
        // issue homerun-rate-explodes-at-high-power）で差が薄く、3000試合ではノイズで符号が
        // 割れうる（実測 issue game-sim-all-right-handed-batters 修正時: seed42で標準4.456 vs 広4.565と
        // 逆転。10000/30000試合では標準>広に収束するので実効果自体は本物）。標準は許容帯で見る。
        Assert.True(narrow.AverageRunsPerTeam > standard.AverageRunsPerTeam,
            $"狭 得点 {narrow.AverageRunsPerTeam:F2} ≤ 標準 {standard.AverageRunsPerTeam:F2}");
        Assert.True(narrow.AverageRunsPerTeam > wide.AverageRunsPerTeam,
            $"狭 得点 {narrow.AverageRunsPerTeam:F2} ≤ 広 {wide.AverageRunsPerTeam:F2}");
        Assert.InRange(standard.AverageRunsPerTeam,
            wide.AverageRunsPerTeam - 0.15, narrow.AverageRunsPerTeam);
    }

    [Theory]
    [InlineData(42UL)]
    public void Koshien_ShortWings_ProducesMoreHomeRunsThanWideRegional(ulong seed)
    {
        // 甲子園(95/118)は実寸では両翼が狭い変則球場。地方の広い決勝球場(100/122)より本塁打が出やすい。
        // ※設計書13-stadiums §4 の旧記述「甲子園は本塁打が出にくい」は実寸と食い違う（OPEN-QUESTIONS Q11）。
        var coeff = BalanceRegressionTests.FindDataFile("coefficients.yaml");
        const int atBats = 40000;

        var koshien = AtBatSimulation.Run(atBats, seed, coeff, Stadium("koshien"));
        var wideRegional = AtBatSimulation.Run(atBats, seed, coeff, Stadium("niigata_final"));

        Assert.True(koshien.HomeRunRate > wideRegional.HomeRunRate,
            $"甲子園 HR% {koshien.HomeRunRate:P2} ≤ 地方広め {wideRegional.HomeRunRate:P2}");
    }

    /// <summary>
    /// 実ゲームで甲子園の寸法を挿したときに<b>実際どれだけ動くか</b>を固定する（設計書05 §1.1a）。
    ///
    /// <para>地方大会は球場割当が未配線で <see cref="FieldGeometry"/> の初期値のまま走っている。
    /// その初期値は<b>距離が甲子園と同じ</b>（95/95/118）でフェンスだけ高い（4.0m）ので、
    /// 甲子園で効くのは<b>フェンス高 1.4m ぶんだけ</b>。差が消えたら「聖地に来た実感」も消えるし、
    /// 逆に跳ね上がったらフェンス判定（issue homerun-fence-height-and-ev-cap）の回帰なので、
    /// どちらにも気づけるよう上下を挟む。</para>
    /// </summary>
    [Theory]
    [InlineData(42UL)]
    [InlineData(2024UL)]
    public void Koshien_LowerFence_LiftsHomeRunsOverTheDefaultField(ulong seed)
    {
        var coeff = BalanceRegressionTests.FindDataFile("coefficients.yaml");
        const int atBats = 40000;

        var koshien = AtBatSimulation.Run(atBats, seed, coeff, Stadium("koshien"));
        var current = AtBatSimulation.Run(atBats, seed, coeff, new FieldGeometry());

        // 実測（40,000打席・seed 42 / 2024）: 甲子園 1.645% / 1.773% に対し既定 1.330% / 1.493%
        // ＝ +0.28〜0.32pt（約+21%・1試合あたり 約+0.2本）。聖地で一発が増えるが乱打戦にはならない幅。
        var lift = koshien.HomeRunRate - current.HomeRunRate;
        Assert.InRange(lift, 0.0015, 0.0060);
    }
}
