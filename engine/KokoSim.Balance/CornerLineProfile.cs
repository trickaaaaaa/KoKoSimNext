using System.Globalization;
using System.Text;
using KokoSim.Engine.Core;
using KokoSim.Engine.Match.AtBat;
using KokoSim.Engine.Match.Field;
using KokoSim.Engine.Match.Fielding;
using KokoSim.Engine.Players;

namespace KokoSim.Balance;

public static partial class AtBatSimulation
{
    /// <summary>
    /// 一塁手・三塁手の標準守備位置の検討（issue #1640）用の診断。ゴロ系の着地方位角を
    /// 「ファウルラインから何度か」でビン分けし、ビンごとに誰が処理したか（線側の一/三塁手・
    /// それ以外の内野手・内野を抜けた）と、安打/アウトの内訳を、打者の左右別に出す。
    /// 線際（0〜6°）の抜け率と三遊間/一二塁間（18〜25°）の抜け率が、標準守備位置を動かす前後で
    /// どう変わるかを比較するための実測値であって、判定・帯には一切使わない（観測専用）。
    /// </summary>
    public sealed record CornerLineProfile
    {
        /// <summary>ファウルラインからの角度[deg]のビン境界（0=線上, 45=センター方向）。</summary>
        public static readonly double[] BinEdgesDeg = { 0, 3, 6, 9, 12, 18, 25, 45 };
        public static readonly string[] BinLabels =
        {
            "0-3°(線上)", "3-6°", "6-9°", "9-12°", "12-18°", "18-25°(間)", "25-45°(中間)",
        };
        public const int BinCount = 7;
        public const int SideCount = 2; // 0=一塁側(+X), 1=三塁側(-X)
        public const int HandCount = 3; // 0=右打者, 1=左打者, 2=両打（このCLIは投手の左右を持たないため解決しない）

        public required int[] Grounders { get; init; }        // [side,hand,bin] フラット化
        public required int[] Outs { get; init; }
        public required int[] Errors { get; init; }
        public required int[] FieldedByCorner { get; init; }  // 線側の一塁手/三塁手が処理
        public required int[] FieldedByOther { get; init; }   // それ以外の内野手が処理
        public required int[] ThroughInfield { get; init; }   // 内野を抜けた
        public int PlateAppearances { get; init; }

        public static int Index(int side, int hand, int bin) => (side * HandCount + hand) * BinCount + bin;

        public string ToMarkdown()
        {
            var c = CultureInfo.InvariantCulture;
            var sb = new StringBuilder();
            sb.AppendLine(c, $"打席 {PlateAppearances}。ゴロ系（Grounder/Bouncer）の着地方位角を、ファウルラインからの角度でビン分け。");
            sb.AppendLine("線側の守備位置＝一塁側ビンは一塁手、三塁側ビンは三塁手。");
            for (var side = 0; side < SideCount; side++)
            {
                var sideName = side == 0 ? "一塁側（+X）" : "三塁側（−X）";
                var cornerName = side == 0 ? "一塁手" : "三塁手";
                for (var hand = 0; hand < HandCount; hand++)
                {
                    var handName = hand == 0 ? "右打者" : hand == 1 ? "左打者" : "両打";
                    sb.AppendLine();
                    sb.AppendLine(c, $"## {sideName}・{handName}");
                    sb.AppendLine();
                    sb.AppendLine($"| 角度（線から） | ゴロ | {cornerName}処理 | 他内野手処理 | 抜け | 抜け率 | アウト率 | 失策率 |");
                    sb.AppendLine("|---|---:|---:|---:|---:|---:|---:|---:|");
                    for (var bin = 0; bin < BinCount; bin++)
                    {
                        var i = Index(side, hand, bin);
                        var g = Grounders[i];
                        sb.AppendLine(c, $"| {BinLabels[bin]} | {g} | {FieldedByCorner[i]} | {FieldedByOther[i]} | "
                                         + $"{ThroughInfield[i]} | {Rate(ThroughInfield[i], g):P1} | {Rate(Outs[i], g):P1} | {Rate(Errors[i], g):P1} |");
                    }
                }
            }
            return sb.ToString();
        }

        private static double Rate(int a, int b) => b > 0 ? (double)a / b : 0;
    }

    /// <summary>
    /// <see cref="CornerLineProfile"/> を集計する（issue #1640）。乱数消費順・結果は <see cref="RunInfieldTiming"/> と同じ経路。
    /// </summary>
    public static CornerLineProfile RunCornerLineProfile(
        int atBats, ulong seed, string? coefficientsPath = null, FieldGeometry? field = null)
    {
        var ctx = BuildContext(coefficientsPath, field);
        var root = new Xoshiro256Random(seed);
        var forks = new IRandomSource[atBats];
        for (var i = 0; i < atBats; i++) forks[i] = root.Fork((ulong)i);

        const int side = CornerLineProfile.SideCount;
        const int hand = CornerLineProfile.HandCount;
        const int bin = CornerLineProfile.BinCount;
        var width = side * hand * bin;

        var gTotal = new int[width];
        var outTotal = new int[width];
        var errTotal = new int[width];
        var cornerTotal = new int[width];
        var otherTotal = new int[width];
        var throughTotal = new int[width];

        Parallel.For(0, atBats,
            () => new int[width * 6],
            (i, _, local) =>
            {
                var rng = forks[i];
                var batter = SampleBatter(rng);
                var pitcher = SamplePitcher(rng);
                var detailed = AtBatResolver.ResolveDetailed(batter, pitcher, ctx, rng);
                if (detailed.Play is not { Class: BattedBallClass.Grounder or BattedBallClass.Bouncer } play) return local;

                var bearing = play.BearingDeg;
                var sideIdx = bearing >= 0 ? 0 : 1;
                var offsetFromLine = 45.0 - Math.Min(45.0, Math.Abs(bearing));
                var binIdx = Bin(CornerLineProfile.BinEdgesDeg, offsetFromLine);
                var handIdx = batter.Bats switch { Handedness.Right => 0, Handedness.Left => 1, _ => 2 };
                var idx = CornerLineProfile.Index(sideIdx, handIdx, binIdx);

                local[idx]++; // g
                if (play.Result == BattedBallResult.Out) local[width + idx]++;
                if (play.Result == BattedBallResult.Error) local[2 * width + idx]++;
                var cornerRole = sideIdx == 0 ? FieldPosition.FirstBase : FieldPosition.ThirdBase;
                if (play.FielderRole == cornerRole) local[3 * width + idx]++;
                else if (play.FielderRole is not null) local[4 * width + idx]++;
                if (play.ThroughInfield) local[5 * width + idx]++;
                return local;
            },
            local =>
            {
                lock (gTotal)
                {
                    for (var k = 0; k < width; k++)
                    {
                        gTotal[k] += local[k];
                        outTotal[k] += local[width + k];
                        errTotal[k] += local[2 * width + k];
                        cornerTotal[k] += local[3 * width + k];
                        otherTotal[k] += local[4 * width + k];
                        throughTotal[k] += local[5 * width + k];
                    }
                }
            });

        return new CornerLineProfile
        {
            Grounders = gTotal,
            Outs = outTotal,
            Errors = errTotal,
            FieldedByCorner = cornerTotal,
            FieldedByOther = otherTotal,
            ThroughInfield = throughTotal,
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
