using System.Globalization;
using System.Text;
using KokoSim.Config;
using KokoSim.Engine.Core;
using KokoSim.Engine.Match.AtBat;
using KokoSim.Engine.Match.Field;
using KokoSim.Engine.Match.Fielding;
using KokoSim.Engine.Players;

namespace KokoSim.Balance;

/// <summary>1万打席シミュレーションと統計集計（Phase 1 DoD）。</summary>
public static class AtBatSimulation
{
    public sealed record Stats
    {
        public int PlateAppearances { get; init; }
        public int AtBats { get; init; }
        public int Hits { get; init; }
        public int Singles { get; init; }
        public int Doubles { get; init; }
        public int Triples { get; init; }
        public int HomeRuns { get; init; }
        public int Walks { get; init; }
        public int HitByPitches { get; init; }
        public int Strikeouts { get; init; }
        public int ReachedOnError { get; init; }
        public int InPlayOuts { get; init; }

        public double Average => AtBats > 0 ? (double)Hits / AtBats : 0;
        public double StrikeoutRate => PlateAppearances > 0 ? (double)Strikeouts / PlateAppearances : 0;
        public double WalkRate => PlateAppearances > 0 ? (double)Walks / PlateAppearances : 0;
        public double HomeRunRate => PlateAppearances > 0 ? (double)HomeRuns / PlateAppearances : 0;
        public double Babip
        {
            get
            {
                var denom = AtBats - Strikeouts - HomeRuns;
                return denom > 0 ? (double)(Hits - HomeRuns) / denom : 0;
            }
        }
        public double HitByPitchRate => PlateAppearances > 0 ? (double)HitByPitches / PlateAppearances : 0;
        public double OnBase => PlateAppearances > 0 ? (double)(Hits + Walks + HitByPitches) / PlateAppearances : 0;
        public double Slugging => AtBats > 0
            ? (double)(Singles + 2 * Doubles + 3 * Triples + 4 * HomeRuns) / AtBats : 0;
        public double DoubleRate => PlateAppearances > 0 ? (double)Doubles / PlateAppearances : 0;
        public double TripleRate => PlateAppearances > 0 ? (double)Triples / PlateAppearances : 0;
        /// <summary>二塁打 ÷ 本塁打（長打の種類の配分。実野球はおよそ 2〜2.5）。</summary>
        public double DoublesPerHomeRun => HomeRuns > 0 ? (double)Doubles / HomeRuns : 0;

        /// <summary>着地距離[10m刻み]×結果 のヒストグラム（--histogram 指定時のみ。診断用）。</summary>
        public BattedBallHistogram? Histogram { get; init; }
    }

    /// <summary>
    /// 打球の着地距離×結果の分布（Issue #24 の検証用）。塁打数が距離だけで決まっていないこと、
    /// 三塁打が単一バケットに集中していないことを目視で確認するための診断出力。
    /// </summary>
    public sealed class BattedBallHistogram
    {
        public const int Buckets = 14;   // 0-10, 10-20, ..., 130+
        public const int Kinds = 5;      // Single, Double, Triple, Out(+Error), HomeRun

        private static readonly string[] KindNames = { "単打", "二塁打", "三塁打", "凡打", "本塁打" };
        private readonly int[] _fly = new int[Buckets * Kinds];
        private readonly int[] _ground = new int[Buckets * Kinds];

        internal static int BucketOf(double rangeM) => Math.Min(Buckets - 1, Math.Max(0, (int)(rangeM / 10.0)));

        internal void Add(int[] flat)
        {
            for (var i = 0; i < _fly.Length; i++) _fly[i] += flat[i];
            for (var i = 0; i < _ground.Length; i++) _ground[i] += flat[_fly.Length + i];
        }

        public string ToMarkdown()
        {
            var sb = new StringBuilder();
            var c = CultureInfo.InvariantCulture;
            sb.Append("| 打球 \\ 着地[m] |");
            for (var b = 0; b < Buckets; b++) sb.Append(c, $" {b * 10} |");
            sb.AppendLine();
            sb.Append("|---|");
            for (var b = 0; b < Buckets; b++) sb.Append("---|");
            sb.AppendLine();
            foreach (var (name, data) in new[] { ("fly", _fly), ("ground", _ground) })
            {
                for (var k = 0; k < Kinds; k++)
                {
                    var row = new int[Buckets];
                    var sum = 0;
                    for (var b = 0; b < Buckets; b++) { row[b] = data[b * Kinds + k]; sum += row[b]; }
                    if (sum == 0) continue;
                    // 本塁打はフライ判定より前に確定するため fly/ground の区別を持たない。
                    sb.Append(k == 4 ? "| " + KindNames[k] + " |" : "| " + KindNames[k] + "-" + name + " |");
                    foreach (var v in row) sb.Append(c, $" {v} |");
                    sb.AppendLine();
                }
            }
            return sb.ToString();
        }
    }

    /// <summary>
    /// 高校野球リーグを模した能力分布から打者・投手を毎打席サンプリングして N 打席回す。
    /// 単一の平均選手ではなく母集団の総和として現実的なリーグ統計を得る。
    /// </summary>
    public static Stats Run(int atBats, ulong seed, string? coefficientsPath = null, FieldGeometry? field = null,
        bool histogram = false)
    {
        var ctx = BuildContext(coefficientsPath, field);
        var root = new Xoshiro256Random(seed);

        // 決定論を保った並列化（GameSimulation と同方式）:
        // 打席ごとに事前 Fork した独立ストリーム＋整数部分和のマージ → 同シード同結果。
        var forks = new IRandomSource[atBats];
        for (var i = 0; i < atBats; i++)
        {
            forks[i] = root.Fork((ulong)i);
        }

        // pa, ab, h, s1, s2, s3, hr, bb, so, roe, outs, hbp（＋ヒストグラム指定時は着地距離×結果のビン）
        const int Counters = 12;
        var histBins = histogram ? BattedBallHistogram.Buckets * BattedBallHistogram.Kinds * 2 : 0;
        var width = Counters + histBins;
        var total = new int[width];
        Parallel.For(0, atBats,
            () => new int[width],
            (i, _, local) =>
            {
                var rng = forks[i];
                var batter = SampleBatter(rng);
                var pitcher = SamplePitcher(rng);
                var detailed = AtBatResolver.ResolveDetailed(batter, pitcher, ctx, rng);
                var r = detailed.Result;

                if (histogram && detailed.Play is { } play && play.Result != BattedBallResult.Foul)
                {
                    var kind = play.Result switch
                    {
                        BattedBallResult.Single => 0,
                        BattedBallResult.Double => 1,
                        BattedBallResult.Triple => 2,
                        BattedBallResult.HomeRun => 4,
                        _ => 3,
                    };
                    var bin = BattedBallHistogram.BucketOf(play.RangeM) * BattedBallHistogram.Kinds + kind;
                    local[Counters + (play.IsFly ? 0 : histBins / 2) + bin]++;
                }

                local[0]++;
                if (r.IsAtBat()) local[1]++;
                if (r.IsHit()) local[2]++;
                switch (r)
                {
                    case PlateAppearanceResult.Single: local[3]++; break;
                    case PlateAppearanceResult.Double: local[4]++; break;
                    case PlateAppearanceResult.Triple: local[5]++; break;
                    case PlateAppearanceResult.HomeRun: local[6]++; break;
                    case PlateAppearanceResult.Walk: local[7]++; break;
                    case PlateAppearanceResult.Strikeout: local[8]++; break;
                    case PlateAppearanceResult.ReachedOnError: local[9]++; break;
                    case PlateAppearanceResult.InPlayOut: local[10]++; break;
                    case PlateAppearanceResult.HitByPitch: local[11]++; break;
                }
                return local;
            },
            local =>
            {
                lock (total)
                {
                    for (var k = 0; k < total.Length; k++) total[k] += local[k];
                }
            });

        BattedBallHistogram? hist = null;
        if (histogram)
        {
            hist = new BattedBallHistogram();
            hist.Add(total[Counters..]);
        }

        return new Stats
        {
            Histogram = hist,
            PlateAppearances = total[0],
            AtBats = total[1],
            Hits = total[2],
            Singles = total[3],
            Doubles = total[4],
            Triples = total[5],
            HomeRuns = total[6],
            Walks = total[7],
            Strikeouts = total[8],
            ReachedOnError = total[9],
            InPlayOuts = total[10],
            HitByPitches = total[11],
        };
    }

    /// <summary>
    /// ゴロ（Grounder/Bouncer 分類）の処理野手の分布（Issue #261 診断・回帰）。守備位置ごとに
    /// 「ゴロ全体」と「ゴロアウト（Result==Out）」を数える。母集団サンプリング・決定論並列は本体 Run と同一。
    /// </summary>
    public sealed record GroundFielderTally
    {
        /// <summary>FieldPosition ごとのゴロ全体件数（添字＝(int)FieldPosition）。</summary>
        public required int[] All { get; init; }
        /// <summary>FieldPosition ごとのゴロアウト件数。</summary>
        public required int[] Outs { get; init; }
        /// <summary>ゴロ（Grounder/Bouncer）の総数（処理野手が確定したもの）。</summary>
        public int GroundBalls { get; init; }
        /// <summary>ゴロアウトの総数。</summary>
        public int GroundOuts { get; init; }

        /// <summary>ゴロ全体に占める当該守備位置の割合。</summary>
        public double AllFraction(FieldPosition p) => GroundBalls > 0 ? (double)All[(int)p] / GroundBalls : 0;
        /// <summary>ゴロアウトに占める当該守備位置の割合。</summary>
        public double OutFraction(FieldPosition p) => GroundOuts > 0 ? (double)Outs[(int)p] / GroundOuts : 0;
    }

    /// <summary>ゴロの処理野手分布を集計する（Issue #261）。乱数消費順・結果は Run と同じ経路。</summary>
    public static GroundFielderTally RunGroundFielders(
        int atBats, ulong seed, string? coefficientsPath = null, FieldGeometry? field = null)
    {
        var ctx = BuildContext(coefficientsPath, field);
        var root = new Xoshiro256Random(seed);
        var forks = new IRandomSource[atBats];
        for (var i = 0; i < atBats; i++) forks[i] = root.Fork((ulong)i);

        const int Positions = 10; // FieldPosition の数
        // レイアウト: [0..P)=ゴロ全体, [P..2P)=ゴロアウト, [2P]=ゴロ数, [2P+1]=ゴロアウト数
        var width = Positions * 2 + 2;
        var total = new int[width];
        Parallel.For(0, atBats,
            () => new int[width],
            (i, _, local) =>
            {
                var rng = forks[i];
                var batter = SampleBatter(rng);
                var pitcher = SamplePitcher(rng);
                var detailed = AtBatResolver.ResolveDetailed(batter, pitcher, ctx, rng);
                if (detailed.Play is { Class: BattedBallClass.Grounder or BattedBallClass.Bouncer } play
                    && play.FielderRole is { } role)
                {
                    local[(int)role]++;
                    local[Positions * 2]++;
                    if (play.Result == BattedBallResult.Out)
                    {
                        local[Positions + (int)role]++;
                        local[Positions * 2 + 1]++;
                    }
                }
                return local;
            },
            local => { lock (total) { for (var k = 0; k < width; k++) total[k] += local[k]; } });

        return new GroundFielderTally
        {
            All = total[0..Positions],
            Outs = total[Positions..(Positions * 2)],
            GroundBalls = total[Positions * 2],
            GroundOuts = total[Positions * 2 + 1],
        };
    }

    /// <summary>
    /// 打球方向の分布と外野の捕球者分布（issue #262 の診断・回帰）。方位角は10°刻み9ビン（−45〜45°）。
    /// 母集団サンプリング・決定論並列は本体 <see cref="Run"/> と同一。
    /// </summary>
    public sealed record BearingProfile
    {
        /// <summary>方位角ビン（−45°から10°刻み・9ビン）のインプレー打球数。−＝左翼側／＋＝右翼側。</summary>
        public required int[] Bins { get; init; }
        /// <summary>外野手のフライ・ライナー捕球数（[0]=左 [1]=中 [2]=右）。</summary>
        public required int[] Outfield { get; init; }
        /// <summary>ビンに入ったインプレー打球の総数。</summary>
        public int InPlay { get; init; }
        /// <summary>外野手が捕ったフライ・ライナーの総数。</summary>
        public int OutfieldCatches { get; init; }

        public const int BinCount = 9;
        public const double BinWidthDeg = 10.0;
        public static double BinLowDeg(int index) => -45.0 + index * BinWidthDeg;

        public double LeftShare => Share(0);
        public double CenterShare => Share(1);
        public double RightShare => Share(2);
        private double Share(int i) => OutfieldCatches > 0 ? (double)Outfield[i] / OutfieldCatches : 0;

        /// <summary>中央3分の1（|方位| &lt; 15°）に入るインプレー打球の割合。</summary>
        public double CenterThirdShare
            => InPlay > 0 ? (double)(Bins[3] + Bins[4] + Bins[5]) / InPlay : 0;

        /// <summary>方位角の平均[deg]（ビン中央での近似）。負＝左翼側へ寄っている。</summary>
        public double MeanBearingDeg
        {
            get
            {
                if (InPlay <= 0) return 0;
                var sum = 0.0;
                for (var i = 0; i < BinCount; i++) sum += (BinLowDeg(i) + BinWidthDeg / 2.0) * Bins[i];
                return sum / InPlay;
            }
        }

        public string ToMarkdown()
        {
            var sb = new StringBuilder();
            var c = CultureInfo.InvariantCulture;
            sb.AppendLine("| 方位角 | 件数 | 割合 |");
            sb.AppendLine("|---|---|---|");
            for (var i = 0; i < BinCount; i++)
            {
                var share = InPlay > 0 ? (double)Bins[i] / InPlay : 0;
                sb.AppendLine(c, $"| {BinLowDeg(i):F0}〜{BinLowDeg(i) + BinWidthDeg:F0}° | {Bins[i]} | {share:P1} |");
            }
            sb.AppendLine(c, $"\n外野捕球: 左 {LeftShare:P1} / 中 {CenterShare:P1} / 右 {RightShare:P1}"
                             + $"（計 {OutfieldCatches}）");
            sb.AppendLine(c, $"中央3分の1（|方位| < 15°）: {CenterThirdShare:P1}（インプレー {InPlay}）");
            return sb.ToString();
        }
    }

    /// <summary>
    /// 打球方向と外野捕球者の分布を集計する（issue #262）。乱数消費順・結果は Run と同じ経路。
    /// <paramref name="batsOverride"/> を渡すと母集団の打席左右を固定できる（引っ張り方向の検証用）。
    /// </summary>
    public static BearingProfile RunBearingProfile(
        int atBats, ulong seed, string? coefficientsPath = null, FieldGeometry? field = null,
        Handedness? batsOverride = null)
    {
        var ctx = BuildContext(coefficientsPath, field);
        var root = new Xoshiro256Random(seed);
        var forks = new IRandomSource[atBats];
        for (var i = 0; i < atBats; i++) forks[i] = root.Fork((ulong)i);

        // レイアウト: [0..9)=方位角ビン, [9..12)=外野捕球(左/中/右), [12]=インプレー数, [13]=外野捕球数
        const int Bins = BearingProfile.BinCount;
        var width = Bins + 3 + 2;
        var total = new int[width];
        Parallel.For(0, atBats,
            () => new int[width],
            (i, _, local) =>
            {
                var rng = forks[i];
                var batter = SampleBatter(rng);
                if (batsOverride is { } bats) batter = batter with { Bats = bats };
                var pitcher = SamplePitcher(rng);
                var detailed = AtBatResolver.ResolveDetailed(batter, pitcher, ctx, rng);
                if (detailed.Play is not { } play || play.Result == BattedBallResult.Foul) return local;

                var bin = (int)((play.BearingDeg + 45.0) / BearingProfile.BinWidthDeg);
                if (bin >= 0 && bin < Bins)
                {
                    local[bin]++;
                    local[Bins + 3]++;
                }

                if (play.IsFly && play.FielderRole is { } role)
                {
                    var of = role switch
                    {
                        FieldPosition.LeftField => 0,
                        FieldPosition.CenterField => 1,
                        FieldPosition.RightField => 2,
                        _ => -1,
                    };
                    if (of >= 0)
                    {
                        local[Bins + of]++;
                        local[Bins + 4]++;
                    }
                }
                return local;
            },
            local => { lock (total) { for (var k = 0; k < width; k++) total[k] += local[k]; } });

        return new BearingProfile
        {
            Bins = total[0..Bins],
            Outfield = total[Bins..(Bins + 3)],
            InPlay = total[Bins + 3],
            OutfieldCatches = total[Bins + 4],
        };
    }

    private static int SampleAbility(IRandomSource rng, double mean = 50, double sd = 12, int min = 20, int max = 88)
        => (int)MathUtil.Clamp(Math.Round(rng.NextGaussian(mean, sd)), min, max);

    private static BatterAttributes SampleBatter(IRandomSource rng) => new()
    {
        Contact = SampleAbility(rng),
        Power = SampleAbility(rng),
        LaunchTendency = SampleAbility(rng),
        Discipline = SampleAbility(rng),
        Speed = SampleAbility(rng),
        Bats = SampleBats(rng),
    };

    /// <summary>
    /// 打席の左右。issue #262 で打球方向が左右で反転するようになったため、母集団の左右比を
    /// 実ロスター（<see cref="Season.ProspectGenerator"/> / <see cref="Nation.StrengthTeamFactory"/>）と揃える。
    /// それ以前は全員右打ちだったので、引っ張り方向が片側へ寄って分布が実態とずれる。
    /// 確率は生成器の既定と同値（スイッチ3% ／ 左投12% ／ 左投の98%が左打 ／ 右投の40%が左打）。
    /// </summary>
    private static Handedness SampleBats(IRandomSource rng)
    {
        if (rng.NextDouble() < 0.03) return Handedness.Switch;
        var throwsLeft = rng.NextDouble() < 0.12;
        return rng.NextDouble() < (throwsLeft ? 0.98 : 0.40) ? Handedness.Left : Handedness.Right;
    }

    private static PitcherAttributes SamplePitcher(IRandomSource rng)
    {
        var pitchRank = SampleAbility(rng);
        return new PitcherAttributes
        {
            MaxVelocityKmh = MathUtil.Clamp(rng.NextGaussian(132, 6), 118, 150),
            Control = SampleAbility(rng),
            PitchRank = pitchRank,
            StaminaPitches = 90.0, // 打席単位のシムでは疲労なし
            Repertoire = new[]
            {
                PitchSlot.FastballOf(pitchRank),
                new PitchSlot { Type = PitchType.Slider, Power = pitchRank, Sharpness = pitchRank },
            },
        };
    }

    public static AtBatContext BuildContext(string? coefficientsPath, FieldGeometry? field = null)
    {
        var geom = field ?? new FieldGeometry();
        if (coefficientsPath is null)
        {
            return new AtBatContext { Field = geom, Fielders = geom.StandardAlignment() };
        }

        var bundle = CoefficientsLoader.LoadFromFile(coefficientsPath);
        return new AtBatContext
        {
            Aerodynamics = bundle.Aerodynamics,
            Pitching = bundle.Pitching,
            Batting = bundle.Batting,
            Fielding = bundle.Fielding,
            Field = geom,
            Fielders = geom.StandardAlignment(),
        };
    }

    public static string Report(Stats s, ulong seed)
    {
        var sb = new StringBuilder();
        var c = CultureInfo.InvariantCulture;
        sb.AppendLine("# KokoSim 打席解決 統計レポート（Phase 1）");
        sb.AppendLine();
        sb.AppendLine(c, $"- シード: {seed}");
        sb.AppendLine(c, $"- 打席数(PA): {s.PlateAppearances} / 打数(AB): {s.AtBats}");
        sb.AppendLine();
        sb.AppendLine("| 指標 | 値 | 目標帯 |");
        sb.AppendLine("|---|---|---|");
        sb.AppendLine(c, $"| 打率 AVG | {s.Average:F3} | .240–.290 |");
        sb.AppendLine(c, $"| 三振率 K% | {s.StrikeoutRate:P1} | 15.0–22.0% |");
        sb.AppendLine(c, $"| 四球率 BB% | {s.WalkRate:P1} | 6.0–10.0% |");
        sb.AppendLine(c, $"| 死球率 HBP% | {s.HitByPitchRate:P2} | 参考 0.5–2.0% |");
        sb.AppendLine(c, $"| 本塁打率 HR% | {s.HomeRunRate:P2} | 2.0–4.0% |");
        sb.AppendLine(c, $"| BABIP | {s.Babip:F3} | 参考 .290–.320 |");
        sb.AppendLine(c, $"| 出塁率 OBP | {s.OnBase:F3} | 参考 |");
        sb.AppendLine(c, $"| 長打率 SLG | {s.Slugging:F3} | 参考 |");
        sb.AppendLine();
        sb.AppendLine(c, $"| 二塁打率 2B% | {s.DoubleRate:P2} | 参考 3.5–5.5% |");
        sb.AppendLine(c, $"| 三塁打率 3B% | {s.TripleRate:P2} | 参考 0.2–0.9% |");
        sb.AppendLine(c, $"| 二塁打÷本塁打 | {s.DoublesPerHomeRun:F2} | 参考 1.5–3.0 |");
        sb.AppendLine();
        sb.AppendLine("## 内訳");
        sb.AppendLine(c, $"- 単打 {s.Singles} / 二塁打 {s.Doubles} / 三塁打 {s.Triples} / 本塁打 {s.HomeRuns}");
        sb.AppendLine(c, $"- 四球 {s.Walks} / 三振 {s.Strikeouts} / 失策出塁 {s.ReachedOnError} / インプレー凡打 {s.InPlayOuts}");
        if (s.Histogram is { } h)
        {
            sb.AppendLine();
            sb.AppendLine("## 着地距離 × 結果（Issue #24 診断）");
            sb.AppendLine();
            sb.Append(h.ToMarkdown());
        }
        return sb.ToString();
    }
}
