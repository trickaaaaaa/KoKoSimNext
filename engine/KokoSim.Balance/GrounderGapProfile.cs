using System.Globalization;
using System.Text;
using KokoSim.Engine.Core;
using KokoSim.Engine.Match.AtBat;
using KokoSim.Engine.Match.Batting;
using KokoSim.Engine.Match.Field;
using KokoSim.Engine.Match.Fielding;
using KokoSim.Engine.Players;

namespace KokoSim.Balance;

public static partial class AtBatSimulation
{
    /// <summary>
    /// 内野ゴロ系（Grounder/Bouncer）が、打球の向き（方位角5°刻み・centerが0°）別に
    /// 「内野で止まった／内野を抜けた」のどちらになるか、処理した野手の内訳（特に投手）を
    /// 打者の打席左右で分けて出す（issue #1789）。乱数消費順・結果は <see cref="Run"/> と同じ経路。
    ///
    /// <para>見るもの: 投手が「投げ終わりの遅れ」なしで内野手と同じ守備範囲を持っていたため、
    /// センター線の左右±13°（二遊間の通り道）がマウンドに吸い込まれ、センター前のゴロ安打が
    /// 出なくなっていないか。修正の前後でこの表を取り、センター付近の抜けた数・投手処理の数の
    /// 変化を見る。</para>
    /// </summary>
    public sealed record GrounderGapProfile
    {
        public const int BinCount = 18;
        public const double BinWidthDeg = 5.0;
        public static double BinLowDeg(int index) => -45.0 + index * BinWidthDeg;

        /// <summary>打席の左右（スイッチは投手の逆打席へ解決済み）。0=右打ち, 1=左打ち。</summary>
        public const int HandCount = 2;

        /// <summary>[bin, hand] 内野ゴロ系の総数。</summary>
        public required int[,] Grounders { get; init; }
        /// <summary>[bin, hand] 内野を抜けた数（ホール／頭上越え）。</summary>
        public required int[,] ThroughInfield { get; init; }
        /// <summary>[bin, hand] 安打になった数（内野を抜けた安打＋内野処理後の失策等の安打）。</summary>
        public required int[,] Hits { get; init; }
        /// <summary>[bin, hand, position] 内野で止まった打球の処理野手別件数。</summary>
        public required int[,,] HandledBy { get; init; }
        /// <summary>[bin, hand] 投手が処理した打球の初速合計[km/h]（平均の算出用）。</summary>
        public required double[,] PitcherExitVelocitySum { get; init; }

        private static readonly FieldPosition[] InfieldPositions =
        {
            FieldPosition.Pitcher, FieldPosition.FirstBase, FieldPosition.SecondBase,
            FieldPosition.ThirdBase, FieldPosition.Shortstop,
        };

        public static string HandName(int hand) => hand == 0 ? "右打者" : "左打者";

        public string ToMarkdown()
        {
            var c = CultureInfo.InvariantCulture;
            var sb = new StringBuilder();
            for (var hand = 0; hand < HandCount; hand++)
            {
                sb.AppendLine(c, $"## {HandName(hand)}");
                sb.AppendLine();
                sb.AppendLine("| 方位角（0=センター・＋は一塁側/−は三塁側） | ゴロ系 | 内野で止まった | 抜けた | 抜けた割合 | 安打 | 安打率 | 投手処理 | 投手処理の平均初速[km/h] |");
                sb.AppendLine("|---|---:|---:|---:|---:|---:|---:|---:|---:|");
                for (var bin = 0; bin < BinCount; bin++)
                {
                    var n = Grounders[bin, hand];
                    var through = ThroughInfield[bin, hand];
                    var stopped = n - through;
                    var hits = Hits[bin, hand];
                    var pitcherHandled = HandledBy[bin, hand, 0];
                    var pitcherMeanKmh = pitcherHandled > 0 ? PitcherExitVelocitySum[bin, hand] / pitcherHandled : 0.0;
                    var label = $"{BinLowDeg(bin):F0}〜{BinLowDeg(bin) + BinWidthDeg:F0}°";
                    sb.AppendLine(c, $"| {label} | {n} | {stopped} | {through} | {Rate(through, n):P1} | {hits} | "
                                     + $"{Rate(hits, n):P1} | {pitcherHandled} | {(pitcherHandled > 0 ? pitcherMeanKmh.ToString("F1", c) : "-")} |");
                }
                sb.AppendLine();
                sb.AppendLine("内野で止まった打球の処理野手別件数（投手/一塁/二塁/三塁/遊撃）:");
                sb.AppendLine();
                sb.AppendLine("| 方位角 | 投 | 一 | 二 | 三 | 遊 |");
                sb.AppendLine("|---|---:|---:|---:|---:|---:|");
                for (var bin = 0; bin < BinCount; bin++)
                {
                    var label = $"{BinLowDeg(bin):F0}〜{BinLowDeg(bin) + BinWidthDeg:F0}°";
                    sb.AppendLine(c, $"| {label} | {HandledBy[bin, hand, 0]} | {HandledBy[bin, hand, 1]} | "
                                     + $"{HandledBy[bin, hand, 2]} | {HandledBy[bin, hand, 3]} | {HandledBy[bin, hand, 4]} |");
                }
                sb.AppendLine();
            }
            return sb.ToString();
        }

        private static double Rate(int a, int b) => b > 0 ? (double)a / b : 0;
    }

    /// <summary>
    /// <see cref="GrounderGapProfile"/> を集計する。
    /// </summary>
    public static GrounderGapProfile RunGrounderGapProfile(
        int atBats, ulong seed, string? coefficientsPath = null, FieldGeometry? field = null)
    {
        var ctx = BuildContext(coefficientsPath, field);
        var root = new Xoshiro256Random(seed);
        var forks = new IRandomSource[atBats];
        for (var i = 0; i < atBats; i++) forks[i] = root.Fork((ulong)i);

        const int bins = GrounderGapProfile.BinCount;
        const int hands = GrounderGapProfile.HandCount;
        const int positions = 5;
        // レイアウト: [0..B*H)=ゴロ系, [B*H..2*B*H)=抜けた, [2*B*H..3*B*H)=安打,
        //            [3*B*H..3*B*H+B*H*5)=処理野手別, 末尾=投手処理の初速合計(double とビット一致させず別配列で集計)
        var binsHands = bins * hands;
        var width = binsHands * 3 + binsHands * positions;
        var totalInt = new int[width];
        var totalPitcherKmh = new double[binsHands];
        var gate = new object();

        Parallel.For(0, atBats,
            () => (new int[width], new double[binsHands]),
            (i, _, local) =>
            {
                var (localInt, localKmh) = local;
                var rng = forks[i];
                var batter = SampleBatter(rng);
                var pitcher = SamplePitcher(rng);
                var detailed = AtBatResolver.ResolveDetailed(batter, pitcher, ctx, rng);
                if (detailed.Play is not { Class: BattedBallClass.Grounder or BattedBallClass.Bouncer } play) return local;

                var bin = (int)((play.BearingDeg + 45.0) / GrounderGapProfile.BinWidthDeg);
                if (bin < 0 || bin >= bins) return local;
                var hand = batter.Bats.EffectiveBats(ctx.PitcherThrows) == Handedness.Left ? 1 : 0;
                var idx = bin * hands + hand;

                localInt[idx]++; // ゴロ系
                var through = play.ThroughInfield;
                if (through) localInt[binsHands + idx]++;
                var hit = play.Result is BattedBallResult.Single or BattedBallResult.Double
                    or BattedBallResult.Triple or BattedBallResult.Error;
                if (hit) localInt[2 * binsHands + idx]++;

                if (!through && play.FielderRole is { } role)
                {
                    var posIdx = role switch
                    {
                        FieldPosition.Pitcher => 0,
                        FieldPosition.FirstBase => 1,
                        FieldPosition.SecondBase => 2,
                        FieldPosition.ThirdBase => 3,
                        FieldPosition.Shortstop => 4,
                        _ => -1,
                    };
                    if (posIdx >= 0)
                    {
                        localInt[3 * binsHands + idx * positions + posIdx]++;
                        if (posIdx == 0)
                        {
                            var ball = detailed.PitchLog is { Count: > 0 } log ? log[^1].BattedBall : null;
                            if (ball is not null) localKmh[idx] += ball.ExitVelocityKmh;
                        }
                    }
                }
                return (localInt, localKmh);
            },
            local =>
            {
                lock (gate)
                {
                    for (var k = 0; k < width; k++) totalInt[k] += local.Item1[k];
                    for (var k = 0; k < binsHands; k++) totalPitcherKmh[k] += local.Item2[k];
                }
            });

        var grounders = new int[bins, hands];
        var through2 = new int[bins, hands];
        var hits2 = new int[bins, hands];
        var handledBy = new int[bins, hands, positions];
        var pitcherKmh = new double[bins, hands];
        for (var bin = 0; bin < bins; bin++)
        {
            for (var hand = 0; hand < hands; hand++)
            {
                var idx = bin * hands + hand;
                grounders[bin, hand] = totalInt[idx];
                through2[bin, hand] = totalInt[binsHands + idx];
                hits2[bin, hand] = totalInt[2 * binsHands + idx];
                pitcherKmh[bin, hand] = totalPitcherKmh[idx];
                for (var p = 0; p < positions; p++)
                    handledBy[bin, hand, p] = totalInt[3 * binsHands + idx * positions + p];
            }
        }

        return new GrounderGapProfile
        {
            Grounders = grounders,
            ThroughInfield = through2,
            Hits = hits2,
            HandledBy = handledBy,
            PitcherExitVelocitySum = pitcherKmh,
        };
    }
}
