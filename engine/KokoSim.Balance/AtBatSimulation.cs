using System.Globalization;
using System.Text;
using KokoSim.Config;
using KokoSim.Engine.Core;
using KokoSim.Engine.Match.AtBat;
using KokoSim.Engine.Match.Field;
using KokoSim.Engine.Match.Fielding;
using KokoSim.Engine.Nation;
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
        /// <summary>三振のうち見逃し（最終球が振らずに入った CalledStrike）だったもの。見逃し三振率の調査用。</summary>
        public int LookingStrikeouts { get; init; }

        public double Average => AtBats > 0 ? (double)Hits / AtBats : 0;
        public double StrikeoutRate => PlateAppearances > 0 ? (double)Strikeouts / PlateAppearances : 0;
        /// <summary>見逃し三振率（打席あたり）。K% の内訳。</summary>
        public double LookingStrikeoutRate => PlateAppearances > 0 ? (double)LookingStrikeouts / PlateAppearances : 0;
        /// <summary>三振に占める見逃しの割合（実野球はおよそ3割前後が参考値。design未定義のためこのゲームの目標帯は無い）。</summary>
        public double LookingStrikeoutShare => Strikeouts > 0 ? (double)LookingStrikeouts / Strikeouts : 0;
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

        // pa, ab, h, s1, s2, s3, hr, bb, so, roe, outs, hbp, looking_so（＋ヒストグラム指定時は着地距離×結果のビン）
        const int Counters = 13;
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
                if (r.IsLookingStrikeout(detailed.PitchLog)) local[12]++;
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
            LookingStrikeouts = total[12],
        };
    }

    /// <summary>
    /// 投手ランク（投手総合＝球速.40/制球.25/スタミナ.15/キレ.20 の等級 G〜S）別の三振率・被打率。
    /// issue #402 の監督観測「E・F投手でも三振を取りすぎる」の是正確認用。母集団サンプリング・決定論並列は
    /// 本体 <see cref="Run"/> と同一（同シードで同じ投手・打者を引く）。
    /// </summary>
    public sealed record PitcherRankStats
    {
        /// <summary>添字＝(int)Tier（G=0..S=7）。</summary>
        public required int[] PlateAppearances { get; init; }
        public required int[] AtBats { get; init; }
        public required int[] Hits { get; init; }
        public required int[] Strikeouts { get; init; }
        /// <summary>四球（issue walk-rate-floor-vs-elite-pitcher の検収: 高制球投手の与四球/9）。</summary>
        public required int[] Walks { get; init; }
        /// <summary>アウト（打席のうち出塁しなかったもの＝ 打席 − 安打 − 四死球 − 失策出塁）。与四球/9 の分母。</summary>
        public required int[] Outs { get; init; }

        public double StrikeoutRate(Tier t) =>
            PlateAppearances[(int)t] > 0 ? (double)Strikeouts[(int)t] / PlateAppearances[(int)t] : 0;
        public double Average(Tier t) => AtBats[(int)t] > 0 ? (double)Hits[(int)t] / AtBats[(int)t] : 0;
        public double WalkRate(Tier t) =>
            PlateAppearances[(int)t] > 0 ? (double)Walks[(int)t] / PlateAppearances[(int)t] : 0;
        /// <summary>与四球/9（打席シムの近似: 四球 ÷ アウト × 27）。</summary>
        public double WalksPer9(Tier t) => Outs[(int)t] > 0 ? 27.0 * Walks[(int)t] / Outs[(int)t] : 0;
    }

    /// <summary>サンプル投手の総合等級（投手総合＝TeamStrengthCoefficients の重みで G〜S に量子化）。</summary>
    private static Tier PitcherTierOf(PitcherAttributes p, TeamStrengthCoefficients c)
    {
        var velLevel = PitcherAttributes.LevelFromVelocityKmh(p.MaxVelocityKmh);
        var staminaLevel = PitcherAttributes.LevelFromStaminaPitches(p.StaminaPitches);
        var wsum = c.VelocityWeight + c.ControlWeight + c.StaminaWeight + c.PitchRankWeight;
        var score = (velLevel * c.VelocityWeight + p.Control * c.ControlWeight
                     + staminaLevel * c.StaminaWeight + p.PitchRank * c.PitchRankWeight) / wsum;
        return Tiers.FromStrength(score);
    }

    /// <summary>投手ランク別の三振率・被打率を集計する（issue #402）。乱数消費順・結果は <see cref="Run"/> と同じ経路。</summary>
    public static PitcherRankStats RunByPitcherRank(
        int atBats, ulong seed, string? coefficientsPath = null, FieldGeometry? field = null)
    {
        var ctx = BuildContext(coefficientsPath, field);
        var tsc = new TeamStrengthCoefficients();
        var root = new Xoshiro256Random(seed);
        var forks = new IRandomSource[atBats];
        for (var i = 0; i < atBats; i++) forks[i] = root.Fork((ulong)i);

        const int TierCount = 8;  // G..S
        const int Counters = 6;   // pa, ab, h, so, bb, outs
        var width = TierCount * Counters;
        var total = new int[width];
        Parallel.For(0, atBats,
            () => new int[width],
            (i, _, local) =>
            {
                var rng = forks[i];
                var batter = SampleBatter(rng);
                var pitcher = SamplePitcher(rng);
                var b = (int)PitcherTierOf(pitcher, tsc) * Counters;
                var r = AtBatResolver.ResolveDetailed(batter, pitcher, ctx, rng).Result;
                local[b]++;                          // PA
                if (r.IsAtBat()) local[b + 1]++;     // AB
                if (r.IsHit()) local[b + 2]++;       // H
                if (r == PlateAppearanceResult.Strikeout) local[b + 3]++;
                if (r == PlateAppearanceResult.Walk) local[b + 4]++;
                if (!r.IsHit() && r is not (PlateAppearanceResult.Walk or PlateAppearanceResult.HitByPitch
                        or PlateAppearanceResult.ReachedOnError)) local[b + 5]++;
                return local;
            },
            local => { lock (total) { for (var k = 0; k < width; k++) total[k] += local[k]; } });

        var pa = new int[TierCount]; var ab = new int[TierCount]; var h = new int[TierCount]; var so = new int[TierCount];
        var bb = new int[TierCount]; var outs = new int[TierCount];
        for (var t = 0; t < TierCount; t++)
        {
            pa[t] = total[t * Counters];
            ab[t] = total[t * Counters + 1];
            h[t] = total[t * Counters + 2];
            so[t] = total[t * Counters + 3];
            bb[t] = total[t * Counters + 4];
            outs[t] = total[t * Counters + 5];
        }
        return new PitcherRankStats
            { PlateAppearances = pa, AtBats = ab, Hits = h, Strikeouts = so, Walks = bb, Outs = outs };
    }

    /// <summary>投手ランク別 三振率・被打率のMarkDownレポート（issue #402 検証出力）。</summary>
    public static string ReportByPitcherRank(PitcherRankStats s, ulong seed)
    {
        var sb = new StringBuilder();
        var c = CultureInfo.InvariantCulture;
        sb.AppendLine("## 投手ランク別 三振率・被打率（issue #402 検証: E・F投手の奪三振是正）");
        sb.AppendLine();
        sb.AppendLine(c, $"投手総合＝球速.40/制球.25/スタミナ.15/キレ.20 の等級（G〜S）で層別（シード {seed}）。");
        sb.AppendLine();
        sb.AppendLine("| 投手ランク | 打席(PA) | 三振率 K% | 被打率 AVG | 四球率 BB% | 与四球/9（近似） |");
        sb.AppendLine("|---|---:|---:|---:|---:|---:|");
        foreach (var t in new[] { Tier.S, Tier.A, Tier.B, Tier.C, Tier.D, Tier.E, Tier.F, Tier.G })
        {
            if (s.PlateAppearances[(int)t] == 0) continue;
            sb.AppendLine(c, $"| {t} | {s.PlateAppearances[(int)t]} | {s.StrikeoutRate(t):P1} | {s.Average(t):F3} | {s.WalkRate(t):P1} | {s.WalksPer9(t):F2} |");
        }
        return sb.ToString();
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
    /// 内野ゴロの「間に合うか」の分布（issue #358 の診断・回帰）。守備所要（捕球→握り替え→送球）と
    /// 打者走者の一塁到達を秒で突き合わせた結果を、判定margin（<see cref="FieldingPlay.JudgementMarginSeconds"/>）
    /// のヒストグラムで見る。帯（balance-targets.yaml）に内野安打の指標が無いため、係数を動かす前後の
    /// 比較はこのプローブで行う（#261 が守備位置分布を実測したのと同じやり方）。
    /// </summary>
    public sealed record InfieldTiming
    {
        /// <summary>判定margin[s]のビン幅。</summary>
        public const double BinWidthSeconds = 0.1;
        /// <summary>ビン数（−1.0秒未満／−1.0〜+1.0を0.1刻み／+1.0秒以上 の 22）。</summary>
        public const int BinCount = 22;
        /// <summary>ビン下限[s]（先頭・末尾は外れ値バケット）。</summary>
        public static double BinLowSeconds(int index) => -1.0 + (index - 1) * BinWidthSeconds;

        /// <summary>内野手が処理した内野ゴロの総数（内野を抜けた打球は含まない）。</summary>
        public int Grounders { get; init; }
        /// <summary>そのうちアウト。</summary>
        public int Outs { get; init; }
        /// <summary>そのうち内野安打（送球が間に合わなかった）。</summary>
        public int InfieldHits { get; init; }
        /// <summary>そのうち失策（送球は間に合ったが弾いた/逸れた）。</summary>
        public int Errors { get; init; }
        /// <summary>集計した打席数（打席あたりの率の分母）。</summary>
        public int PlateAppearances { get; init; }
        /// <summary>判定margin[s]のヒストグラム（正＝セーフ寄り）。</summary>
        public required int[] MarginBins { get; init; }
        /// <summary>捕球時刻の合計[s]（平均の算出用。ミリ秒を整数化して積む）。</summary>
        public int FieldTimeMsSum { get; init; }
        /// <summary>送球到達時刻の合計[s]（＝守備所要）。</summary>
        public int DefenseTimeMsSum { get; init; }
        /// <summary>打者走者の一塁到達時刻の合計[s]。</summary>
        public int RunnerTimeMsSum { get; init; }

        /// <summary>内野ゴロに占める内野安打の割合。</summary>
        public double InfieldHitShare => Grounders > 0 ? (double)InfieldHits / Grounders : 0;
        /// <summary>内野ゴロに占めるアウトの割合。</summary>
        public double OutShare => Grounders > 0 ? (double)Outs / Grounders : 0;
        /// <summary>打席あたりの内野安打率。</summary>
        public double InfieldHitPerPa => PlateAppearances > 0 ? (double)InfieldHits / PlateAppearances : 0;
        public double MeanFieldSeconds => Grounders > 0 ? FieldTimeMsSum / 1000.0 / Grounders : 0;
        public double MeanDefenseSeconds => Grounders > 0 ? DefenseTimeMsSum / 1000.0 / Grounders : 0;
        public double MeanRunnerSeconds => Grounders > 0 ? RunnerTimeMsSum / 1000.0 / Grounders : 0;

        /// <summary>判定margin の分位点[s]（ビン下限での近似）。</summary>
        public double MarginQuantile(double q)
        {
            if (Grounders <= 0) return 0;
            var target = q * Grounders;
            var acc = 0;
            for (var i = 0; i < BinCount; i++)
            {
                acc += MarginBins[i];
                if (acc >= target) return BinLowSeconds(i);
            }
            return BinLowSeconds(BinCount - 1);
        }

        public string ToMarkdown()
        {
            var sb = new StringBuilder();
            var c = CultureInfo.InvariantCulture;
            sb.AppendLine(c, $"内野ゴロ {Grounders}（打席 {PlateAppearances}）: "
                             + $"アウト {OutShare:P1} / 内野安打 {InfieldHitShare:P1} / 失策 {(Grounders > 0 ? (double)Errors / Grounders : 0):P1}");
            sb.AppendLine(c, $"打席あたり内野安打 {InfieldHitPerPa:P2}");
            sb.AppendLine(c, $"平均: 捕球 {MeanFieldSeconds:F3}s → 送球到達 {MeanDefenseSeconds:F3}s / 走者 {MeanRunnerSeconds:F3}s");
            sb.AppendLine(c, $"判定margin 分位点[s]: p10 {MarginQuantile(0.10):F2} / p25 {MarginQuantile(0.25):F2} / "
                             + $"p50 {MarginQuantile(0.50):F2} / p75 {MarginQuantile(0.75):F2} / p90 {MarginQuantile(0.90):F2}");
            sb.AppendLine();
            sb.AppendLine("| 判定margin[s] | 件数 | 割合 |");
            sb.AppendLine("|---|---|---|");
            for (var i = 0; i < BinCount; i++)
            {
                var label = i == 0 ? "〜−1.0"
                    : i == BinCount - 1 ? "+1.0〜"
                    : $"{BinLowSeconds(i):+0.0;−0.0;0.0}〜{BinLowSeconds(i) + BinWidthSeconds:+0.0;−0.0;0.0}";
                var share = Grounders > 0 ? (double)MarginBins[i] / Grounders : 0;
                sb.AppendLine(c, $"| {label} | {MarginBins[i]} | {share:P1} |");
            }
            return sb.ToString();
        }
    }

    /// <summary>
    /// 内野ゴロの判定margin分布を集計する（issue #358）。乱数消費順・結果は <see cref="Run"/> と同じ経路。
    /// </summary>
    public static InfieldTiming RunInfieldTiming(
        int atBats, ulong seed, string? coefficientsPath = null, FieldGeometry? field = null)
    {
        var ctx = BuildContext(coefficientsPath, field);
        var root = new Xoshiro256Random(seed);
        var forks = new IRandomSource[atBats];
        for (var i = 0; i < atBats; i++) forks[i] = root.Fork((ulong)i);

        // レイアウト: [0..B)=marginビン, [B]=内野ゴロ数, [B+1]=アウト, [B+2]=内野安打, [B+3]=失策,
        //             [B+4]=捕球時刻計[ms], [B+5]=守備所要計[ms], [B+6]=走者所要計[ms]
        const int B = InfieldTiming.BinCount;
        var width = B + 7;
        var total = new int[width];
        Parallel.For(0, atBats,
            () => new int[width],
            (i, _, local) =>
            {
                var rng = forks[i];
                var batter = SampleBatter(rng);
                var pitcher = SamplePitcher(rng);
                var detailed = AtBatResolver.ResolveDetailed(batter, pitcher, ctx, rng);
                if (detailed.Play is not { Class: BattedBallClass.Grounder or BattedBallClass.Bouncer } play) return local;
                if (play.ThroughInfield || play.JudgementMarginSeconds is not { } margin) return local;

                var bin = (int)Math.Floor((margin + 1.0) / InfieldTiming.BinWidthSeconds) + 1;
                local[Math.Clamp(bin, 0, B - 1)]++;
                local[B]++;
                switch (play.Result)
                {
                    case BattedBallResult.Out: local[B + 1]++; break;
                    case BattedBallResult.Single: local[B + 2]++; break;
                    case BattedBallResult.Error: local[B + 3]++; break;
                }
                local[B + 4] += (int)Math.Round((play.FieldedAtSeconds ?? 0) * 1000);
                local[B + 5] += (int)Math.Round((play.ThrowArriveSeconds ?? 0) * 1000);
                local[B + 6] += (int)Math.Round(play.BatterToFirstSeconds * 1000);
                return local;
            },
            local => { lock (total) { for (var k = 0; k < width; k++) total[k] += local[k]; } });

        return new InfieldTiming
        {
            MarginBins = total[0..B],
            Grounders = total[B],
            Outs = total[B + 1],
            InfieldHits = total[B + 2],
            Errors = total[B + 3],
            FieldTimeMsSum = total[B + 4],
            DefenseTimeMsSum = total[B + 5],
            RunnerTimeMsSum = total[B + 6],
            PlateAppearances = atBats,
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

    /// <summary>
    /// 打球の頂点(ApexHeightM)・滞空(HangTimeSeconds)・到達距離(RangeM)・4分類(Class)の分布（issue #451）。
    /// 「フライがフライっぽく見えない＝エンジンが出す頂点が実物より低い」を before/after で比較するための診断。
    /// 距離帯（内野&lt;40 / 中間40-70 / 外野≥70）別に、フライ打球の頂点・滞空を分位点で見る。
    /// 母集団サンプリング・決定論並列・解決経路（<see cref="AtBatResolver.ResolveDetailed"/>）は本体 <see cref="Run"/> と同一
    /// （＝同シードで <c>simulate</c> と同じ打者・投手・打球を引く）。帯（balance-targets.yaml）に頂点の指標は無いため、
    /// 打角・空力を動かす前後の比較はこのプローブで行う（#261/#358 が守備分布を実測したのと同じやり方）。
    /// </summary>
    public sealed record ApexProfile
    {
        /// <summary>頂点[m] のヒストグラム: ビン幅とビン数（0〜50m、最終ビンは 49.75m 以上を集約）。</summary>
        public const double ApexBinWidthM = 0.25;
        public const int ApexBinCount = 200;
        /// <summary>滞空[s] のヒストグラム: ビン幅とビン数（0〜10s）。</summary>
        public const double HangBinWidthSeconds = 0.05;
        public const int HangBinCount = 200;

        /// <summary>距離帯の数（内野&lt;40 / 中間40-70 / 外野≥70）。</summary>
        public const int BandCount = 3;
        public static readonly string[] BandNames = { "内野 (range<40m)", "中間 (40-70m)", "外野 (range≥70m)" };
        /// <summary>到達距離[m]→距離帯の添字（0=内野, 1=中間, 2=外野）。</summary>
        public static int BandOf(double rangeM) => rangeM < 40.0 ? 0 : rangeM < 70.0 ? 1 : 2;

        /// <summary>4分類の件数（添字＝(int)BattedBallClass: Grounder/Bouncer/Liner/Fly）。</summary>
        public required int[] ClassCounts { get; init; }
        /// <summary>フライの距離帯ごとの頂点ヒストグラム [band][bin]。</summary>
        public required int[][] ApexBinsByBand { get; init; }
        /// <summary>フライの距離帯ごとの滞空ヒストグラム [band][bin]。</summary>
        public required int[][] HangBinsByBand { get; init; }
        /// <summary>集計した打席数（分母）。</summary>
        public int PlateAppearances { get; init; }

        public int FlyCount(int band) => Sum(ApexBinsByBand[band]);
        public int FlyTotal { get { var n = 0; for (var b = 0; b < BandCount; b++) n += FlyCount(b); return n; } }

        private static int Sum(int[] a) { var s = 0; foreach (var v in a) s += v; return s; }

        /// <summary>ヒストグラムの分位点[単位]（ビン内を線形補間）。</summary>
        private static double Quantile(int[] hist, double binWidth, double q)
        {
            var n = Sum(hist);
            if (n == 0) return 0;
            var target = q * n;
            var acc = 0;
            for (var i = 0; i < hist.Length; i++)
            {
                var prev = acc;
                acc += hist[i];
                if (acc >= target)
                {
                    var frac = hist[i] > 0 ? (target - prev) / hist[i] : 0.0;
                    return (i + frac) * binWidth;
                }
            }
            return hist.Length * binWidth;
        }

        /// <summary>距離帯（-1=フライ全体）の頂点[m]分位点。</summary>
        public double ApexQuantile(int band, double q) => Quantile(BandOrTotalApex(band), ApexBinWidthM, q);
        /// <summary>距離帯（-1=フライ全体）の滞空[s]分位点。</summary>
        public double HangQuantile(int band, double q) => Quantile(BandOrTotalHang(band), HangBinWidthSeconds, q);

        private int[] BandOrTotalApex(int band) => band >= 0 ? ApexBinsByBand[band] : SumBands(ApexBinsByBand);
        private int[] BandOrTotalHang(int band) => band >= 0 ? HangBinsByBand[band] : SumBands(HangBinsByBand);
        private static int[] SumBands(int[][] byBand)
        {
            var total = new int[byBand[0].Length];
            foreach (var band in byBand)
                for (var i = 0; i < total.Length; i++) total[i] += band[i];
            return total;
        }

        public string ToMarkdown()
        {
            var sb = new StringBuilder();
            var c = CultureInfo.InvariantCulture;
            var fly = ClassCounts[(int)BattedBallClass.Fly];
            var liner = ClassCounts[(int)BattedBallClass.Liner];
            var grounder = ClassCounts[(int)BattedBallClass.Grounder];
            var bouncer = ClassCounts[(int)BattedBallClass.Bouncer];
            sb.AppendLine(c, $"打球分類: フライ={fly} ライナー={liner} ゴロ={grounder} バウンド={bouncer}（打席 {PlateAppearances}）");
            sb.AppendLine();
            sb.AppendLine("| 区分 | n | 頂点p25[m] | 頂点p50[m] | 頂点p75[m] | 頂点p90[m] | 滞空p50[s] |");
            sb.AppendLine("|---|---:|---:|---:|---:|---:|---:|");
            void Row(string name, int band)
            {
                var n = band >= 0 ? FlyCount(band) : FlyTotal;
                sb.AppendLine(c, $"| {name} | {n} | {ApexQuantile(band, 0.25):F1} | {ApexQuantile(band, 0.50):F1} | "
                                 + $"{ApexQuantile(band, 0.75):F1} | {ApexQuantile(band, 0.90):F1} | {HangQuantile(band, 0.50):F2} |");
            }
            Row("フライ全体", -1);
            for (var b = 0; b < BandCount; b++) Row(BandNames[b], b);
            return sb.ToString();
        }
    }

    /// <summary>
    /// 打球の頂点/滞空/分類の分布を集計する（issue #451）。乱数消費順・結果は <see cref="Run"/> と同じ経路。
    /// </summary>
    public static ApexProfile RunApexProfile(
        int atBats, ulong seed, string? coefficientsPath = null, FieldGeometry? field = null)
    {
        var ctx = BuildContext(coefficientsPath, field);
        var root = new Xoshiro256Random(seed);
        var forks = new IRandomSource[atBats];
        for (var i = 0; i < atBats; i++) forks[i] = root.Fork((ulong)i);

        // レイアウト: [0..4)=4分類件数 / 各距離帯 b で [4 + b*Stride .. ) に 頂点ヒスト(A) + 滞空ヒスト(H)。
        const int Classes = 4;
        const int A = ApexProfile.ApexBinCount;
        const int H = ApexProfile.HangBinCount;
        const int Stride = A + H;
        var width = Classes + ApexProfile.BandCount * Stride;
        var total = new int[width];
        Parallel.For(0, atBats,
            () => new int[width],
            (i, _, local) =>
            {
                var rng = forks[i];
                var batter = SampleBatter(rng);
                var pitcher = SamplePitcher(rng);
                var detailed = AtBatResolver.ResolveDetailed(batter, pitcher, ctx, rng);
                if (detailed.Play is not { } play || play.Result == BattedBallResult.Foul || play.Class is not { } cls)
                    return local;

                local[(int)cls]++;
                if (cls != BattedBallClass.Fly) return local;

                var band = ApexProfile.BandOf(play.RangeM);
                var baseIdx = Classes + band * Stride;
                var apexBin = (int)Math.Clamp(play.ApexHeightM / ApexProfile.ApexBinWidthM, 0, A - 1);
                var hangBin = (int)Math.Clamp(play.HangTimeSeconds / ApexProfile.HangBinWidthSeconds, 0, H - 1);
                local[baseIdx + apexBin]++;
                local[baseIdx + A + hangBin]++;
                return local;
            },
            local => { lock (total) { for (var k = 0; k < width; k++) total[k] += local[k]; } });

        var apexByBand = new int[ApexProfile.BandCount][];
        var hangByBand = new int[ApexProfile.BandCount][];
        for (var b = 0; b < ApexProfile.BandCount; b++)
        {
            var baseIdx = Classes + b * Stride;
            apexByBand[b] = total[baseIdx..(baseIdx + A)];
            hangByBand[b] = total[(baseIdx + A)..(baseIdx + A + H)];
        }
        return new ApexProfile
        {
            ClassCounts = total[0..Classes],
            ApexBinsByBand = apexByBand,
            HangBinsByBand = hangByBand,
            PlateAppearances = atBats,
        };
    }

    // ===== 投打差の格子（issue matchup-gap-saturation-k-rate） =====

    /// <summary>
    /// 「投手レベル×打者レベル」の1対1格子。投手は球速/制球/キレを同レベル、打者はミート/選球眼を同レベルに置き、
    /// 各マスを <paramref name="atBatsPerCell"/> 打席回して K% / BB% / 被打率 を出す。
    /// 統計帯 <c>atbat_10k</c>（平均同士）では見えない A/S 投手 vs E/F 打線の裾（24奪三振試合）を検収する。
    /// 決定論並列（マス×打席の事前 Fork）。
    /// </summary>
    public sealed record MatchupGrid(
        int[] PitcherLevels, int[] BatterLevels,
        int[,] Pa, int[,] Ab, int[,] H, int[,] So, int[,] Bb)
    {
        public double StrikeoutRate(int pi, int bi) => Pa[pi, bi] > 0 ? (double)So[pi, bi] / Pa[pi, bi] : 0;
        public double WalkRate(int pi, int bi) => Pa[pi, bi] > 0 ? (double)Bb[pi, bi] / Pa[pi, bi] : 0;
        public double Average(int pi, int bi) => Ab[pi, bi] > 0 ? (double)H[pi, bi] / Ab[pi, bi] : 0;

        public string ToMarkdown()
        {
            var sb = new StringBuilder();
            sb.AppendLine("| 投手＼打者 | " + string.Join(" | ", BatterLevels.Select(b => $"B{b}")) + " |");
            sb.AppendLine("|---|" + string.Concat(BatterLevels.Select(_ => "---|")));
            for (var pi = 0; pi < PitcherLevels.Length; pi++)
            {
                var kmh = PitcherAttributes.VelocityKmhFromLevel(PitcherLevels[pi]);
                sb.Append($"| P{PitcherLevels[pi]} ({kmh:F0}km/h) |");
                for (var bi = 0; bi < BatterLevels.Length; bi++)
                    sb.Append($" {StrikeoutRate(pi, bi):P0} / {WalkRate(pi, bi):P0} / {Average(pi, bi):.000} |");
                sb.AppendLine();
            }
            sb.AppendLine();
            sb.AppendLine("各マス: K% / BB% / 被打率");
            return sb.ToString();
        }
    }

    public static readonly int[] DefaultGridPitcherLevels = { 35, 50, 65, 80, 90 };
    public static readonly int[] DefaultGridBatterLevels = { 30, 45, 60, 75 };

    /// <summary>格子の投手（球速/制球/キレ＝level・スタミナは打席単位で無関係・ストレート＋スライダー）。</summary>
    public static PitcherAttributes GridPitcher(int level) => new()
    {
        MaxVelocityKmh = PitcherAttributes.VelocityKmhFromLevel(level),
        Control = level,
        PitchRank = level,
        StaminaPitches = 90.0,
        Repertoire = new[]
        {
            PitchSlot.FastballOf(level),
            new PitchSlot { Type = PitchType.Slider, Power = level, Sharpness = level },
        },
    };

    /// <summary>格子の打者（ミート/選球眼＝level・他は50・右打ち）。</summary>
    public static BatterAttributes GridBatter(int level) => new()
    {
        Contact = level, Discipline = level,
        Power = 50, LaunchTendency = 50, Speed = 50, Bats = Handedness.Right,
    };

    public static MatchupGrid RunMatchupGrid(int atBatsPerCell, ulong seed, string? coefficientsPath = null,
        int[]? pitcherLevels = null, int[]? batterLevels = null)
    {
        var pl = pitcherLevels ?? DefaultGridPitcherLevels;
        var bl = batterLevels ?? DefaultGridBatterLevels;
        var ctx = BuildContext(coefficientsPath);
        var root = new Xoshiro256Random(seed);
        var cells = pl.Length * bl.Length;
        var forks = new IRandomSource[cells * atBatsPerCell];
        for (var i = 0; i < forks.Length; i++) forks[i] = root.Fork((ulong)i);

        const int Counters = 5; // pa, ab, h, so, bb
        var total = new int[cells * Counters];
        Parallel.For(0, forks.Length,
            () => new int[cells * Counters],
            (i, _, local) =>
            {
                var cell = i / atBatsPerCell;
                var pi = cell / bl.Length;
                var bi = cell % bl.Length;
                var r = AtBatResolver.ResolveDetailed(GridBatter(bl[bi]), GridPitcher(pl[pi]), ctx, forks[i]).Result;
                var b = cell * Counters;
                local[b]++;
                if (r.IsAtBat()) local[b + 1]++;
                if (r.IsHit()) local[b + 2]++;
                if (r == PlateAppearanceResult.Strikeout) local[b + 3]++;
                if (r == PlateAppearanceResult.Walk) local[b + 4]++;
                return local;
            },
            local => { lock (total) { for (var k = 0; k < total.Length; k++) total[k] += local[k]; } });

        var pa = new int[pl.Length, bl.Length]; var ab = new int[pl.Length, bl.Length];
        var h = new int[pl.Length, bl.Length]; var so = new int[pl.Length, bl.Length]; var bb = new int[pl.Length, bl.Length];
        for (var pi = 0; pi < pl.Length; pi++)
            for (var bi = 0; bi < bl.Length; bi++)
            {
                var b = (pi * bl.Length + bi) * Counters;
                pa[pi, bi] = total[b]; ab[pi, bi] = total[b + 1]; h[pi, bi] = total[b + 2];
                so[pi, bi] = total[b + 3]; bb[pi, bi] = total[b + 4];
            }
        return new MatchupGrid(pl, bl, pa, ab, h, so, bb);
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
    /// <see cref="GameSimulation"/> も同じ式を使う（左右の分布式は1箇所に集約する）。
    /// </summary>
    internal static Handedness SampleBats(IRandomSource rng)
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
        sb.AppendLine(c, $"| 打率 AVG | {s.Average:F3} | .235–.290 |");
        sb.AppendLine(c, $"| 三振率 K% | {s.StrikeoutRate:P1} | 15.0–23.0% |");
        sb.AppendLine(c, $"| うち見逃し三振率（打席あたり） | {s.LookingStrikeoutRate:P1} | 参考（目標帯未設定） |");
        sb.AppendLine(c, $"| 見逃し三振の割合（三振に占める） | {s.LookingStrikeoutShare:P1} | 参考（実野球はおよそ3割前後） |");
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
        sb.AppendLine(c, $"- 四球 {s.Walks} / 三振 {s.Strikeouts}（見逃し {s.LookingStrikeouts} / 空振り {s.Strikeouts - s.LookingStrikeouts}） / 失策出塁 {s.ReachedOnError} / インプレー凡打 {s.InPlayOuts}");
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
