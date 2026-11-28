using System.Globalization;
using System.Text;
using KokoSim.Engine.Core;
using KokoSim.Engine.Match.AtBat;
using KokoSim.Engine.Match.Field;
using KokoSim.Engine.Match.Fielding;
using KokoSim.Engine.Match.Game;
using KokoSim.Engine.Players;

namespace KokoSim.Balance;

/// <summary>
/// 一塁手・三塁手の標準守備位置の検討（issue #1640）用の共通ビン。角度は「ファウルラインから何度か」
/// （0=線上, 45=センター方向）。観測専用＝判定・帯には一切使わない。
/// </summary>
internal static class CornerLineBins
{
    public static readonly double[] AngleEdgesDeg = { 0, 5, 10, 15, 20, 25, 32, 45 };
    public static readonly string[] AngleLabels =
    {
        "0-5°(線際)", "5-10°(線際)", "10-15°", "15-20°(間)", "20-25°(間)", "25-32°(間)", "32-45°(中間)",
    };
    public const int AngleBinCount = 7;

    /// <summary>処理点の深さ（本塁からの水平距離）[m]のビン。</summary>
    public static readonly double[] DepthEdgesM = { 0, 15, 20, 25, 30, 35, 40, 999 };
    public static readonly string[] DepthLabels = { "<15m", "15-20m", "20-25m", "25-30m", "30-35m", "35-40m", "40m+" };
    public const int DepthBinCount = 7;

    public const int SideCount = 2; // 0=一塁側(+X), 1=三塁側(-X)
    public const int HandCount = 3; // 0=右打者, 1=左打者, 2=両打（左右未解決）

    public static double OffsetFromLineDeg(double bearingDeg) => 45.0 - Math.Min(45.0, Math.Abs(bearingDeg));

    public static int Bin(double[] edges, double v)
    {
        for (var k = 1; k < edges.Length; k++)
        {
            if (v < edges[k]) return k - 1;
        }
        return edges.Length - 2;
    }

    public static int HandIndex(Handedness h) => h switch { Handedness.Right => 0, Handedness.Left => 1, _ => 2 };
    public static string HandName(int hand) => hand == 0 ? "右打者" : hand == 1 ? "左打者" : "両打";

    public static double Rate(long a, long b) => b > 0 ? (double)a / b : 0;
}

public static partial class AtBatSimulation
{
    /// <summary>
    /// 打席単位の診断（issue #1640）。ゴロ系の着地方位角をファウルラインからの角度でビン分けし、
    /// ビンごとに誰が処理したか・抜けたか・安打/アウト/失策の内訳を打者の左右別に出す。あわせて
    /// 線側の一塁手/三塁手が処理したゴロの処理点（FieldedX/Z）の角度・深さの分布を出す。観測専用。
    /// </summary>
    public sealed record CornerLineProfile
    {
        public required long[] Grounders { get; init; }        // [side,hand,bin]
        public required long[] Outs { get; init; }
        public required long[] Errors { get; init; }
        public required long[] Hits { get; init; }
        public required long[] FieldedByCorner { get; init; }
        public required long[] FieldedByOther { get; init; }
        public required long[] ThroughInfield { get; init; }
        /// <summary>線側の野手が処理したゴロの処理点 [side,hand,angleBin]。</summary>
        public required long[] CornerPointAngle { get; init; }
        /// <summary>線側の野手が処理したゴロの処理点 [side,hand,depthBin]。</summary>
        public required long[] CornerPointDepth { get; init; }
        /// <summary>処理点の角度・深さの和（平均用, ×1000 の整数）[side,hand]。</summary>
        public required long[] CornerPointAngleSumMilli { get; init; }
        public required long[] CornerPointDepthSumMilli { get; init; }
        public int PlateAppearances { get; init; }

        public static int Index(int side, int hand, int bin)
            => (side * CornerLineBins.HandCount + hand) * CornerLineBins.AngleBinCount + bin;
        public static int DepthIndex(int side, int hand, int bin)
            => (side * CornerLineBins.HandCount + hand) * CornerLineBins.DepthBinCount + bin;

        public string ToMarkdown()
        {
            var c = CultureInfo.InvariantCulture;
            var sb = new StringBuilder();
            sb.AppendLine(c, $"打席 {PlateAppearances}。ゴロ系（Grounder/Bouncer）の着地方位角を、ファウルラインからの角度でビン分け。");
            for (var side = 0; side < CornerLineBins.SideCount; side++)
            {
                var sideName = side == 0 ? "一塁側（+X）" : "三塁側（−X）";
                var cornerName = side == 0 ? "一塁手" : "三塁手";
                for (var hand = 0; hand < 2; hand++)
                {
                    sb.AppendLine();
                    sb.AppendLine(c, $"## {sideName}・{CornerLineBins.HandName(hand)}");
                    sb.AppendLine();
                    sb.AppendLine($"| 角度（線から） | ゴロ | {cornerName}処理 | 他内野手処理 | 抜け | 抜け率 | 安打率 | アウト率 | 失策率 |");
                    sb.AppendLine("|---|---:|---:|---:|---:|---:|---:|---:|---:|");
                    for (var bin = 0; bin < CornerLineBins.AngleBinCount; bin++)
                    {
                        var i = Index(side, hand, bin);
                        var g = Grounders[i];
                        sb.AppendLine(c, $"| {CornerLineBins.AngleLabels[bin]} | {g} | {FieldedByCorner[i]} | {FieldedByOther[i]} | "
                                         + $"{ThroughInfield[i]} | {CornerLineBins.Rate(ThroughInfield[i], g):P1} | {CornerLineBins.Rate(Hits[i], g):P1} | "
                                         + $"{CornerLineBins.Rate(Outs[i], g):P1} | {CornerLineBins.Rate(Errors[i], g):P1} |");
                    }

                    var n = 0L;
                    for (var bin = 0; bin < CornerLineBins.AngleBinCount; bin++) n += CornerPointAngle[Index(side, hand, bin)];
                    var hi = side * CornerLineBins.HandCount + hand;
                    sb.AppendLine();
                    sb.AppendLine(c, $"{cornerName}が処理したゴロの処理点 {n} 本（平均 線から {CornerPointAngleSumMilli[hi] / 1000.0 / Math.Max(1, n):F1}°・"
                                     + $"本塁から {CornerPointDepthSumMilli[hi] / 1000.0 / Math.Max(1, n):F1}m）");
                    sb.AppendLine();
                    sb.Append("| 処理点の角度 |");
                    foreach (var l in CornerLineBins.AngleLabels) sb.Append(c, $" {l} |");
                    sb.AppendLine();
                    sb.Append("|---|");
                    for (var bin = 0; bin < CornerLineBins.AngleBinCount; bin++) sb.Append("---:|");
                    sb.AppendLine();
                    sb.Append("| 割合 |");
                    for (var bin = 0; bin < CornerLineBins.AngleBinCount; bin++)
                        sb.Append(c, $" {CornerLineBins.Rate(CornerPointAngle[Index(side, hand, bin)], n):P1} |");
                    sb.AppendLine();
                    sb.AppendLine();
                    sb.Append("| 処理点の深さ |");
                    foreach (var l in CornerLineBins.DepthLabels) sb.Append(c, $" {l} |");
                    sb.AppendLine();
                    sb.Append("|---|");
                    for (var bin = 0; bin < CornerLineBins.DepthBinCount; bin++) sb.Append("---:|");
                    sb.AppendLine();
                    sb.Append("| 割合 |");
                    for (var bin = 0; bin < CornerLineBins.DepthBinCount; bin++)
                        sb.Append(c, $" {CornerLineBins.Rate(CornerPointDepth[DepthIndex(side, hand, bin)], n):P1} |");
                    sb.AppendLine();
                }
            }
            return sb.ToString();
        }
    }

    /// <summary><see cref="CornerLineProfile"/> を集計する（issue #1640）。乱数消費は <see cref="RunInfieldTiming"/> と同じ経路。</summary>
    public static CornerLineProfile RunCornerLineProfile(
        int atBats, ulong seed, string? coefficientsPath = null, FieldGeometry? field = null)
    {
        var ctx = BuildContext(coefficientsPath, field);
        var root = new Xoshiro256Random(seed);
        var forks = new IRandomSource[atBats];
        for (var i = 0; i < atBats; i++) forks[i] = root.Fork((ulong)i);

        const int sh = CornerLineBins.SideCount * CornerLineBins.HandCount;
        var w = sh * CornerLineBins.AngleBinCount;
        var wd = sh * CornerLineBins.DepthBinCount;
        // レイアウト: [7種×w][角度 w][深さ wd][角度和 sh][深さ和 sh]
        var len = 8 * w + wd + 2 * sh;
        var total = new long[len];

        Parallel.For(0, atBats,
            () => new long[len],
            (i, _, local) =>
            {
                var rng = forks[i];
                var batter = SampleBatter(rng);
                var pitcher = SamplePitcher(rng);
                var detailed = AtBatResolver.ResolveDetailed(batter, pitcher, ctx, rng);
                if (detailed.Play is not { Class: BattedBallClass.Grounder or BattedBallClass.Bouncer } play) return local;
                if (play.Result == BattedBallResult.Foul) return local;

                var bearing = play.BearingDeg;
                var sideIdx = bearing >= 0 ? 0 : 1;
                var binIdx = CornerLineBins.Bin(CornerLineBins.AngleEdgesDeg, CornerLineBins.OffsetFromLineDeg(bearing));
                var handIdx = CornerLineBins.HandIndex(batter.Bats);
                var idx = CornerLineProfile.Index(sideIdx, handIdx, binIdx);

                local[idx]++;
                if (play.Result == BattedBallResult.Out) local[w + idx]++;
                if (play.Result == BattedBallResult.Error) local[2 * w + idx]++;
                if (play.Result is BattedBallResult.Single or BattedBallResult.Double or BattedBallResult.Triple) local[3 * w + idx]++;
                var cornerRole = sideIdx == 0 ? FieldPosition.FirstBase : FieldPosition.ThirdBase;
                if (play.FielderRole == cornerRole)
                {
                    local[4 * w + idx]++;
                    var fx = play.FieldedX ?? play.LandingX;
                    var fz = play.FieldedZ ?? play.LandingZ;
                    var pb = Math.Atan2(fx, fz) * 180.0 / Math.PI;
                    var pAngle = CornerLineBins.OffsetFromLineDeg(pb);
                    var pDepth = Math.Sqrt(fx * fx + fz * fz);
                    var hi = sideIdx * CornerLineBins.HandCount + handIdx;
                    local[7 * w + CornerLineProfile.Index(sideIdx, handIdx, CornerLineBins.Bin(CornerLineBins.AngleEdgesDeg, pAngle))]++;
                    local[7 * w + w + CornerLineProfile.DepthIndex(sideIdx, handIdx, CornerLineBins.Bin(CornerLineBins.DepthEdgesM, pDepth))]++;
                    local[8 * w + wd + hi] += (long)Math.Round(pAngle * 1000);
                    local[8 * w + wd + sh + hi] += (long)Math.Round(pDepth * 1000);
                }
                else if (play.FielderRole is not null) local[5 * w + idx]++;
                if (play.ThroughInfield) local[6 * w + idx]++;
                return local;
            },
            local => { lock (total) { for (var k = 0; k < len; k++) total[k] += local[k]; } });

        long[] Slice(int start, int count) => total.AsSpan(start, count).ToArray();
        return new CornerLineProfile
        {
            Grounders = Slice(0, w),
            Outs = Slice(w, w),
            Errors = Slice(2 * w, w),
            Hits = Slice(3 * w, w),
            FieldedByCorner = Slice(4 * w, w),
            FieldedByOther = Slice(5 * w, w),
            ThroughInfield = Slice(6 * w, w),
            CornerPointAngle = Slice(7 * w, w),
            CornerPointDepth = Slice(8 * w, wd),
            CornerPointAngleSumMilli = Slice(8 * w + wd, sh),
            CornerPointDepthSumMilli = Slice(8 * w + wd + sh, sh),
            PlateAppearances = atBats,
        };
    }
}

public static partial class GameSimulation
{
    /// <summary>
    /// 試合単位の診断（issue #1640）。<see cref="Run"/> と同じ乱数経路・チーム生成で試合を回し、
    /// 打席ログ（BattedClass/BattedBearingDeg/FieldedBy/BatterBats）から、ゴロ系の線からの角度ビン別に
    /// 一/三塁手処理・他内野手処理・内野を抜けた安打（外野手が回収した安打）・内野安打を数える。
    /// あわせて得点・打率・BABIP・二塁打・失策を同じ試合から出す。観測専用。
    /// </summary>
    public sealed record CornerLineGameProfile
    {
        public int Games { get; init; }
        public required long[] Grounders { get; init; }       // [side,hand,bin]
        public required long[] ByCorner { get; init; }
        public required long[] ByOther { get; init; }
        public required long[] ThroughHits { get; init; }     // 外野手が回収した安打
        public required long[] InfieldHits { get; init; }     // 内野手が処理した安打
        public required long[] Errors { get; init; }
        public long Runs { get; init; }
        public long PlateAppearances { get; init; }
        public long AtBats { get; init; }
        public long Hits { get; init; }
        public long Doubles { get; init; }
        public long Triples { get; init; }
        public long HomeRuns { get; init; }
        public long Strikeouts { get; init; }
        public long SacFlies { get; init; }
        public long FieldingErrors { get; init; }

        public double RunsPerTeam => (double)Runs / (2.0 * Games);
        public double BattingAverage => CornerLineBins.Rate(Hits, AtBats);
        public double Babip => CornerLineBins.Rate(Hits - HomeRuns, AtBats - Strikeouts - HomeRuns + SacFlies);
        public double DoubleRate => CornerLineBins.Rate(Doubles, PlateAppearances);
        public double ErrorsPerGame => (double)FieldingErrors / Games;

        public static int Index(int side, int hand, int bin)
            => (side * CornerLineBins.HandCount + hand) * CornerLineBins.AngleBinCount + bin;

        /// <summary>抜けた安打のうち、線から [lo,hi) 度の範囲にあるもの（side 指定・全打者）。</summary>
        public (long through, long grounders) Range(int side, int hand, int loBin, int hiBin)
        {
            long t = 0, g = 0;
            for (var b = loBin; b < hiBin; b++) { t += ThroughHits[Index(side, hand, b)]; g += Grounders[Index(side, hand, b)]; }
            return (t, g);
        }

        public string ToMarkdown()
        {
            var c = CultureInfo.InvariantCulture;
            var sb = new StringBuilder();
            sb.AppendLine(c, $"試合 {Games}。得点/チーム {RunsPerTeam:F3}・打率 {BattingAverage:F3}・BABIP {Babip:F3}・"
                             + $"二塁打/打席 {DoubleRate:P2}・失策/試合 {ErrorsPerGame:F2}・打席 {PlateAppearances}");
            sb.AppendLine();
            sb.AppendLine("### 要約（ゴロ系・抜けた安打＝外野手が回収した安打）");
            sb.AppendLine();
            sb.AppendLine("| 側・打者 | 線際0-10° 抜け率 | 間10-32° 抜け率 | 抜け安打のうち線際の割合 | 抜け安打のうち間の割合 | 線側野手の処理割合(全ゴロ) |");
            sb.AppendLine("|---|---:|---:|---:|---:|---:|");
            for (var side = 0; side < 2; side++)
            {
                for (var hand = 0; hand < 2; hand++)
                {
                    var (tl, gl) = Range(side, hand, 0, 2);
                    var (tg, gg) = Range(side, hand, 2, 6);
                    var (ta, ga) = Range(side, hand, 0, CornerLineBins.AngleBinCount);
                    long corner = 0;
                    for (var b = 0; b < CornerLineBins.AngleBinCount; b++) corner += ByCorner[Index(side, hand, b)];
                    sb.AppendLine(c, $"| {(side == 0 ? "一塁側" : "三塁側")}・{CornerLineBins.HandName(hand)} | {CornerLineBins.Rate(tl, gl):P1} | "
                                     + $"{CornerLineBins.Rate(tg, gg):P1} | {CornerLineBins.Rate(tl, ta):P1} | {CornerLineBins.Rate(tg, ta):P1} | {CornerLineBins.Rate(corner, ga):P1} |");
                }
            }
            for (var side = 0; side < 2; side++)
            {
                var cornerName = side == 0 ? "一塁手" : "三塁手";
                for (var hand = 0; hand < 2; hand++)
                {
                    sb.AppendLine();
                    sb.AppendLine(c, $"#### {(side == 0 ? "一塁側" : "三塁側")}・{CornerLineBins.HandName(hand)}");
                    sb.AppendLine();
                    sb.AppendLine($"| 角度（線から） | ゴロ | {cornerName}処理 | 他内野手処理 | 抜け安打 | 抜け率 | 内野安打 | 失策 |");
                    sb.AppendLine("|---|---:|---:|---:|---:|---:|---:|---:|");
                    for (var b = 0; b < CornerLineBins.AngleBinCount; b++)
                    {
                        var i = Index(side, hand, b);
                        sb.AppendLine(c, $"| {CornerLineBins.AngleLabels[b]} | {Grounders[i]} | {ByCorner[i]} | {ByOther[i]} | {ThroughHits[i]} | "
                                         + $"{CornerLineBins.Rate(ThroughHits[i], Grounders[i]):P1} | {InfieldHits[i]} | {Errors[i]} |");
                    }
                }
            }
            return sb.ToString();
        }
    }

    public static CornerLineGameProfile RunCornerLineGames(int games, ulong seed, string? coefficientsPath = null,
        FieldGeometry? field = null, bool applyWeather = false)
    {
        var ctx = BuildContext(coefficientsPath, field);
        if (!applyWeather) ctx = ctx with { Weather = null };
        var root = new Xoshiro256Random(seed);
        var forks = new IRandomSource[games];
        for (var i = 0; i < games; i++) forks[i] = root.Fork((ulong)i);

        const int w = CornerLineBins.SideCount * CornerLineBins.HandCount * CornerLineBins.AngleBinCount;
        // [6種×w][runs, pa, ab, h, 2b, 3b, hr, k, sf, err]
        const int len = 6 * w + 10;
        var total = new long[len];

        Parallel.For(0, games,
            () => new long[len],
            (i, _, local) =>
            {
                var g = forks[i];
                var away = GenerateTeam(g, "遠征校");
                var home = GenerateTeam(g, "地元校");
                var r = GameEngine.Play(away, home, ctx, g);
                local[6 * w + 0] += r.AwayRuns + r.HomeRuns;
                local[6 * w + 9] += r.AwayErrors + r.HomeErrors;
                foreach (var e in r.Log)
                {
                    local[6 * w + 1]++;
                    if (e.Result.IsAtBat() && !e.IsSacFly && !e.IsSacBunt) local[6 * w + 2]++;
                    if (e.Result.IsHit()) local[6 * w + 3]++;
                    if (e.Result == PlateAppearanceResult.Double) local[6 * w + 4]++;
                    if (e.Result == PlateAppearanceResult.Triple) local[6 * w + 5]++;
                    if (e.Result == PlateAppearanceResult.HomeRun) local[6 * w + 6]++;
                    if (e.Result == PlateAppearanceResult.Strikeout) local[6 * w + 7]++;
                    if (e.IsSacFly) local[6 * w + 8]++;

                    if (e.BattedClass is not (BattedBallClass.Grounder or BattedBallClass.Bouncer)) continue;
                    if (e.BattedBearingDeg is not { } bearing || e.IsSacBunt) continue;
                    if (!e.Result.IsBattedBall()) continue;
                    var side = bearing >= 0 ? 0 : 1;
                    var bin = CornerLineBins.Bin(CornerLineBins.AngleEdgesDeg, CornerLineBins.OffsetFromLineDeg(bearing));
                    var idx = CornerLineGameProfile.Index(side, CornerLineBins.HandIndex(e.BatterBats), bin);
                    local[idx]++;
                    var corner = side == 0 ? FieldPosition.FirstBase : FieldPosition.ThirdBase;
                    var isOf = e.FieldedBy is FieldPosition.LeftField or FieldPosition.CenterField or FieldPosition.RightField;
                    if (e.FieldedBy == corner) local[w + idx]++;
                    else if (e.FieldedBy is not null && !isOf) local[2 * w + idx]++;
                    if (e.Result.IsHit() && isOf) local[3 * w + idx]++;
                    if (e.Result.IsHit() && !isOf && e.FieldedBy is not null) local[4 * w + idx]++;
                    if (e.Result == PlateAppearanceResult.ReachedOnError) local[5 * w + idx]++;
                }
                return local;
            },
            local => { lock (total) { for (var k = 0; k < len; k++) total[k] += local[k]; } });

        long[] Slice(int start) => total.AsSpan(start, w).ToArray();
        return new CornerLineGameProfile
        {
            Games = games,
            Grounders = Slice(0),
            ByCorner = Slice(w),
            ByOther = Slice(2 * w),
            ThroughHits = Slice(3 * w),
            InfieldHits = Slice(4 * w),
            Errors = Slice(5 * w),
            Runs = total[6 * w + 0],
            PlateAppearances = total[6 * w + 1],
            AtBats = total[6 * w + 2],
            Hits = total[6 * w + 3],
            Doubles = total[6 * w + 4],
            Triples = total[6 * w + 5],
            HomeRuns = total[6 * w + 6],
            Strikeouts = total[6 * w + 7],
            SacFlies = total[6 * w + 8],
            FieldingErrors = total[6 * w + 9],
        };
    }
}
