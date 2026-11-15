using System.Globalization;
using System.Text;
using KokoSim.Engine.Core;
using KokoSim.Engine.Match.AtBat;
using KokoSim.Engine.Match.Field;

namespace KokoSim.Balance;

public static partial class AtBatSimulation
{
    /// <summary>
    /// 打席シム（<see cref="Run"/>）と<b>同じ打席</b>（同じ Fork・同じ打者/投手のサンプリング）を解き直し、
    /// 本塁打の打球の上側（初速・角度・フェンス上の余裕）を集計する（issue #1014）。観測だけ＝<see cref="Run"/> の
    /// 結果も乱数列も変えない（別パスで回す）。
    /// </summary>
    public static HomeRunProfile RunHomeRunProfile(int atBats, ulong seed, string? coefficientsPath = null,
        FieldGeometry? field = null)
    {
        var ctx = BuildContext(coefficientsPath, field);
        var root = new Xoshiro256Random(seed);
        var forks = new IRandomSource[atBats];
        for (var i = 0; i < atBats; i++) forks[i] = root.Fork((ulong)i);

        var total = new HomeRunProfile();
        Parallel.For(0, atBats,
            () => new HomeRunProfile(),
            (i, _, local) =>
            {
                var rng = forks[i];
                var batter = SampleBatter(rng);
                var pitcher = SamplePitcher(rng);
                var detailed = AtBatResolver.ResolveDetailed(batter, pitcher, ctx, rng);
                if (detailed.Result != PlateAppearanceResult.HomeRun) return local;
                var ball = detailed.PitchLog is { Count: > 0 } pl ? pl[^1].BattedBall : null;
                local.AddHomeRunBall(ball?.ExitVelocityKmh, ball?.LaunchAngleDeg,
                    detailed.Play?.FenceCrossHeightM is { } h ? h - ctx.Field.FenceHeightM : null);
                return local;
            },
            local => { lock (total) total.Merge(local); });
        return total;
    }

    /// <summary>
    /// 打席シムには「試合」が無いので、1試合（両軍計）の本塁打数は<b>独立打席の二項近似</b>で示す
    /// （<paramref name="plateAppearancesPerGame"/> 打席・各打席 <paramref name="homeRunRate"/>）。
    /// 実際の試合（<c>simulate-games</c>）の分布はチーム力・球場・気温のばらつきの分だけこれより裾が太い。
    /// </summary>
    public static string PerGameBinomialMarkdown(double homeRunRate, int plateAppearancesPerGame)
    {
        var c = CultureInfo.InvariantCulture;
        var sb = new StringBuilder();
        sb.AppendLine(c, $"独立打席の二項近似（{plateAppearancesPerGame} 打席/試合・本塁打率 {homeRunRate:P2}）。"
            + $"試合の実測は `simulate-games` を見る。");
        sb.AppendLine();
        sb.AppendLine("| 1試合の本塁打（両軍計） | 0本 | 1本 | 2本 | 3本 | 4本 | 5本以上 | 10本以上 | 平均 |");
        sb.AppendLine("|---|---:|---:|---:|---:|---:|---:|---:|---:|");
        sb.Append("| 割合 |");
        for (var k = 0; k <= 4; k++) sb.Append(c, $" {BinomialExactly(plateAppearancesPerGame, homeRunRate, k):P1} |");
        sb.AppendLine(c, $" {BinomialAtLeast(plateAppearancesPerGame, homeRunRate, 5):P2} | {BinomialAtLeast(plateAppearancesPerGame, homeRunRate, 10):P4} "
            + $"| {plateAppearancesPerGame * homeRunRate:F2} |");
        return sb.ToString();
    }

    /// <summary>二項分布 B(n, p) で X = k の確率。</summary>
    public static double BinomialExactly(int n, double p, int k)
    {
        if (k < 0 || k > n) return 0;
        if (p <= 0) return k == 0 ? 1 : 0;
        if (p >= 1) return k == n ? 1 : 0;
        double logC = 0;
        for (var i = 1; i <= k; i++) logC += Math.Log(n - k + i) - Math.Log(i);
        return Math.Exp(logC + k * Math.Log(p) + (n - k) * Math.Log(1 - p));
    }

    /// <summary>二項分布 B(n, p) で X ≥ k の確率。</summary>
    public static double BinomialAtLeast(int n, double p, int k)
    {
        double below = 0;
        for (var i = 0; i < k; i++) below += BinomialExactly(n, p, i);
        return Math.Max(0, 1 - below);
    }
}
