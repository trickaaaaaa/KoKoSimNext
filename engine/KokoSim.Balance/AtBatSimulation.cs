using System.Globalization;
using System.Text;
using KokoSim.Config;
using KokoSim.Engine.Core;
using KokoSim.Engine.Match.AtBat;
using KokoSim.Engine.Match.Field;
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
    }

    /// <summary>
    /// 高校野球リーグを模した能力分布から打者・投手を毎打席サンプリングして N 打席回す。
    /// 単一の平均選手ではなく母集団の総和として現実的なリーグ統計を得る。
    /// </summary>
    public static Stats Run(int atBats, ulong seed, string? coefficientsPath = null, FieldGeometry? field = null)
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

        var total = new int[12]; // pa, ab, h, s1, s2, s3, hr, bb, so, roe, outs, hbp
        Parallel.For(0, atBats,
            () => new int[12],
            (i, _, local) =>
            {
                var rng = forks[i];
                var batter = SampleBatter(rng);
                var pitcher = SamplePitcher(rng);
                var r = AtBatResolver.Resolve(batter, pitcher, ctx, rng);

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

        return new Stats
        {
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

    private static int SampleAbility(IRandomSource rng, double mean = 50, double sd = 12, int min = 20, int max = 88)
        => (int)MathUtil.Clamp(Math.Round(rng.NextGaussian(mean, sd)), min, max);

    private static BatterAttributes SampleBatter(IRandomSource rng) => new()
    {
        Contact = SampleAbility(rng),
        Power = SampleAbility(rng),
        LaunchTendency = SampleAbility(rng),
        Discipline = SampleAbility(rng),
        Speed = SampleAbility(rng),
    };

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
        sb.AppendLine("## 内訳");
        sb.AppendLine(c, $"- 単打 {s.Singles} / 二塁打 {s.Doubles} / 三塁打 {s.Triples} / 本塁打 {s.HomeRuns}");
        sb.AppendLine(c, $"- 四球 {s.Walks} / 三振 {s.Strikeouts} / 失策出塁 {s.ReachedOnError} / インプレー凡打 {s.InPlayOuts}");
        return sb.ToString();
    }
}
