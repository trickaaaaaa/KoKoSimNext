using System.Globalization;
using System.Text;
using KokoSim.Engine.Core;
using KokoSim.Engine.Match.AtBat;
using KokoSim.Engine.Match.Field;
using KokoSim.Engine.Match.Fielding;

namespace KokoSim.Balance;

public static partial class AtBatSimulation
{
    /// <summary>
    /// 内野安打の「中身」の内訳（issue #759）。内野手が処理したゴロ系を物理層の値だけで3型に分け、
    /// 型ごとの件数・内野安打率と、初速・平均球足・最大バウンド頂点の分布を出す。
    /// 型の判定は優先順（チョッパー → ボテボテ → 深い位置 → その他）で1つに決める。
    /// </summary>
    public sealed record InfieldHitProfile
    {
        public const int TypeCount = 4;
        public static readonly string[] TypeNames = { "チョッパー（頂点≥2m）", "ボテボテ（平均球足<15m/s）", "深い位置（定位置より2m以上後方 or 送球≥37m）", "その他" };

        /// <summary>チョッパーとみなす最大バウンド頂点[m]。</summary>
        public const double ChopperApexM = 2.0;
        /// <summary>ボテボテとみなす平均球足（捕球点の本塁からの距離÷捕球時刻）[m/s]。</summary>
        public const double DribblerMeanSpeedMps = 15.0;
        /// <summary>深い位置とみなす「定位置より後方」[m]。</summary>
        public const double DeepBehindM = 2.0;
        /// <summary>深い位置とみなす一塁への送球距離[m]（定位置の遊撃→一塁 ≈ 35m）。</summary>
        public const double DeepThrowM = 37.0;

        public static readonly double[] ExitEdgesKmh = { 0, 60, 80, 100, 120, 140, 160, 999 };
        public static readonly double[] MeanSpeedEdgesMps = { 0, 10, 15, 20, 25, 30, 999 };
        public static readonly double[] ApexEdgesM = { 0, 0.5, 1.2, 2.0, 3.0, 5.0, 999 };

        public required int[] TypeGrounders { get; init; }
        public required int[] TypeHits { get; init; }
        public required int[] ExitAll { get; init; }
        public required int[] ExitHits { get; init; }
        public required int[] SpeedAll { get; init; }
        public required int[] SpeedHits { get; init; }
        public required int[] ApexAll { get; init; }
        public required int[] ApexHits { get; init; }
        public int Grounders { get; init; }
        public int InfieldHits { get; init; }
        public int PlateAppearances { get; init; }

        public string ToMarkdown()
        {
            var c = CultureInfo.InvariantCulture;
            var sb = new StringBuilder();
            sb.AppendLine(c, $"内野手処理のゴロ系 {Grounders}（打席 {PlateAppearances}）／内野安打 {InfieldHits}"
                             + $"（ゴロの {Rate(InfieldHits, Grounders):P1}・打席の {Rate(InfieldHits, PlateAppearances):P2}）");
            sb.AppendLine();
            sb.AppendLine("## 型別");
            sb.AppendLine();
            sb.AppendLine("| 型 | ゴロ | ゴロに占める | 内野安打 | その型の安打率 | 内野安打に占める |");
            sb.AppendLine("|---|---:|---:|---:|---:|---:|");
            for (var k = 0; k < TypeCount; k++)
            {
                sb.AppendLine(c, $"| {TypeNames[k]} | {TypeGrounders[k]} | {Rate(TypeGrounders[k], Grounders):P1} | {TypeHits[k]} | "
                                 + $"{Rate(TypeHits[k], TypeGrounders[k]):P1} | {Rate(TypeHits[k], InfieldHits):P1} |");
            }
            Histogram(sb, "打球初速 [km/h]", ExitEdgesKmh, ExitAll, ExitHits);
            Histogram(sb, "平均球足（捕球点までの距離÷捕球時刻）[m/s]", MeanSpeedEdgesMps, SpeedAll, SpeedHits);
            Histogram(sb, "最大バウンド頂点 [m]", ApexEdgesM, ApexAll, ApexHits);
            return sb.ToString();
        }

        private void Histogram(StringBuilder sb, string title, double[] edges, int[] all, int[] hits)
        {
            var c = CultureInfo.InvariantCulture;
            sb.AppendLine();
            sb.AppendLine(c, $"## {title}");
            sb.AppendLine();
            sb.AppendLine("| 区間 | ゴロ | 割合 | 内野安打 | 区間の安打率 |");
            sb.AppendLine("|---|---:|---:|---:|---:|");
            for (var i = 0; i < all.Length; i++)
            {
                var label = edges[i + 1] >= 999 ? $"{edges[i]:0.#}〜" : $"{edges[i]:0.#}〜{edges[i + 1]:0.#}";
                sb.AppendLine(c, $"| {label} | {all[i]} | {Rate(all[i], Grounders):P1} | {hits[i]} | {Rate(hits[i], all[i]):P1} |");
            }
        }

        private static double Rate(int a, int b) => b > 0 ? (double)a / b : 0;
    }

    /// <summary>内野安打の型別内訳を集計する（issue #759）。乱数消費順・結果は <see cref="RunInfieldTiming"/> と同じ経路。</summary>
    public static InfieldHitProfile RunInfieldHitProfile(
        int atBats, ulong seed, string? coefficientsPath = null, FieldGeometry? field = null)
    {
        var ctx = BuildContext(coefficientsPath, field);
        var root = new Xoshiro256Random(seed);
        var forks = new IRandomSource[atBats];
        for (var i = 0; i < atBats; i++) forks[i] = root.Fork((ulong)i);

        var nT = InfieldHitProfile.TypeCount;
        var nE = InfieldHitProfile.ExitEdgesKmh.Length - 1;
        var nS = InfieldHitProfile.MeanSpeedEdgesMps.Length - 1;
        var nA = InfieldHitProfile.ApexEdgesM.Length - 1;
        // レイアウト: [型ゴロ nT][型安打 nT][初速 nE×2][球足 nS×2][頂点 nA×2][ゴロ][安打]
        var oTH = nT;
        var oE = 2 * nT;
        var oS = oE + 2 * nE;
        var oA = oS + 2 * nS;
        var oG = oA + 2 * nA;
        var width = oG + 2;
        var total = new int[width];
        var firstBase = ctx.Field.FirstBase;

        Parallel.For(0, atBats,
            () => new int[width],
            (i, _, local) =>
            {
                var rng = forks[i];
                var batter = SampleBatter(rng);
                var pitcher = SamplePitcher(rng);
                var detailed = AtBatResolver.ResolveDetailed(batter, pitcher, ctx, rng);
                if (detailed.Play is not { Class: BattedBallClass.Grounder or BattedBallClass.Bouncer } play) return local;
                if (play.ThroughInfield || play.JudgementMarginSeconds is null) return local;
                if (play.FieldedX is not { } fx || play.FieldedZ is not { } fz || play.FielderRole is not { } role) return local;
                var ball = detailed.PitchLog is { Count: > 0 } log ? log[^1].BattedBall : null;
                if (ball is null) return local;

                var hit = play.Result == BattedBallResult.Single;
                var catchR = Math.Sqrt(fx * fx + fz * fz);
                var (sx, sz) = FieldGeometry.StandardPosition(role);
                var behind = catchR - Math.Sqrt(sx * sx + sz * sz);
                var throwDist = Math.Sqrt((firstBase.X - fx) * (firstBase.X - fx) + (firstBase.Z - fz) * (firstBase.Z - fz));
                var meanSpeed = play.FieldedAtSeconds is { } ft && ft > 0 ? catchR / ft : 999.0;

                var type = play.BounceApexHeightM >= InfieldHitProfile.ChopperApexM ? 0
                    : meanSpeed < InfieldHitProfile.DribblerMeanSpeedMps ? 1
                    : behind >= InfieldHitProfile.DeepBehindM || throwDist >= InfieldHitProfile.DeepThrowM ? 2
                    : 3;

                local[type]++;
                local[oG]++;
                var e = Bin(InfieldHitProfile.ExitEdgesKmh, ball.ExitVelocityKmh);
                var s = Bin(InfieldHitProfile.MeanSpeedEdgesMps, meanSpeed);
                var a = Bin(InfieldHitProfile.ApexEdgesM, play.BounceApexHeightM);
                local[oE + e]++;
                local[oS + s]++;
                local[oA + a]++;
                if (hit)
                {
                    local[oTH + type]++;
                    local[oG + 1]++;
                    local[oE + nE + e]++;
                    local[oS + nS + s]++;
                    local[oA + nA + a]++;
                }
                return local;
            },
            local => { lock (total) { for (var k = 0; k < width; k++) total[k] += local[k]; } });

        return new InfieldHitProfile
        {
            TypeGrounders = total[0..nT],
            TypeHits = total[oTH..(oTH + nT)],
            ExitAll = total[oE..(oE + nE)],
            ExitHits = total[(oE + nE)..(oE + 2 * nE)],
            SpeedAll = total[oS..(oS + nS)],
            SpeedHits = total[(oS + nS)..(oS + 2 * nS)],
            ApexAll = total[oA..(oA + nA)],
            ApexHits = total[(oA + nA)..(oA + 2 * nA)],
            Grounders = total[oG],
            InfieldHits = total[oG + 1],
            PlateAppearances = atBats,
        };

        static int Bin(double[] edges, double v)
        {
            for (var k = 1; k < edges.Length; k++)
            {
                if (v < edges[k]) return k - 1;
            }
            return edges.Length - 2;
        }
    }
}
