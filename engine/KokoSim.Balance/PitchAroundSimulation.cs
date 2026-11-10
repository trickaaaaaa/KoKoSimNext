using System.Globalization;
using System.Text;
using KokoSim.Config;
using KokoSim.Engine.Core;
using KokoSim.Engine.Match.AtBat;
using KokoSim.Engine.Match.Field;
using KokoSim.Engine.Match.Tactics;
using KokoSim.Engine.Players;

namespace KokoSim.Balance;

/// <summary>
/// くさい所（issue #872）の効果測定。同じ強打者×同じ投手で「方針なし」と「くさい所」を打席単位で比べる。
/// 制球は高・中・低の3段。さらに「際の球は芯を外しやすい」補正（<c>ContactQualityPerEdge</c>）を切った場合も並べ、
/// 補正の寄与とくさい所の寄与を分けて読めるようにする。
/// </summary>
public static class PitchAroundSimulation
{
    public sealed record Cell(
        string Label, int PlateAppearances, int AtBats, int Hits, int ExtraBaseHits, int HomeRuns, int Walks,
        int Strikeouts, long Pitches, double MeanAimEdgeProximity)
    {
        private double Pa => PlateAppearances;
        public double WalkRate => Walks / Pa;
        public double StrikeoutRate => Strikeouts / Pa;
        public double Average => AtBats > 0 ? (double)Hits / AtBats : 0;
        public double ExtraBaseRate => ExtraBaseHits / Pa;
        public double HomeRunRate => HomeRuns / Pa;
        public double PitchesPerPa => Pitches / Pa;
    }

    public static readonly int[] ControlLevels = { 20, 50, 80 };

    private static PitcherAttributes Pitcher(int control) => new()
    {
        MaxVelocityKmh = 132,
        Control = control,
        PitchRank = 50,
        StaminaPitches = 90.0,
        Repertoire = new[]
        {
            PitchSlot.FastballOf(50),
            new PitchSlot { Type = PitchType.Slider, Power = 50, Sharpness = 50 },
        },
    };

    /// <summary>強打者（ミート60・パワー80・弾道60）。左右は混ぜず右打者。</summary>
    private static BatterAttributes SlugBatter() => new()
    {
        Contact = 60, Power = 80, LaunchTendency = 60, Discipline = 50, Speed = 50, Bats = Handedness.Right,
    };

    public static Cell Measure(string label, int atBats, ulong seed, int control, bool pitchAround,
        string? coefficientsPath, double? qualityPerEdgeOverride)
    {
        var ctx = AtBatSimulation.BuildContext(coefficientsPath);
        if (qualityPerEdgeOverride is { } q) ctx = ctx with { Batting = ctx.Batting with { ContactQualityPerEdge = q } };
        if (pitchAround)
            ctx = ctx with { Directive = PitchDirective.Identity with { Edge = new TacticsCoefficients().EdgeAimFor(Handedness.Right) } };

        var root = new Xoshiro256Random(seed);
        var forks = new IRandomSource[atBats];
        for (var i = 0; i < atBats; i++) forks[i] = root.Fork((ulong)i);

        var total = new long[9];
        var zone = ctx.StrikeZone;
        var pitcher = Pitcher(control);
        var batter = SlugBatter();
        Parallel.For(0, atBats, () => new long[9], (i, _, local) =>
        {
            var rng = forks[i];
            var r = AtBatResolver.ResolveDetailed(batter, pitcher, ctx, rng);
            local[0]++;
            if (r.Result.IsAtBat()) local[1]++;
            if (r.Result.IsHit()) local[2]++;
            if (r.Result is PlateAppearanceResult.Double or PlateAppearanceResult.Triple or PlateAppearanceResult.HomeRun) local[3]++;
            if (r.Result == PlateAppearanceResult.HomeRun) local[4]++;
            if (r.Result == PlateAppearanceResult.Walk) local[5]++;
            if (r.Result == PlateAppearanceResult.Strikeout) local[6]++;
            local[7] += r.Pitches;
            // 狙い位置の縁への近さ（ゾーン内なら EdgeProximity、外なら1）。狙いが縁に寄ったかの確認用。
            foreach (var p in r.PitchLog!)
            {
                local[8] += (long)Math.Round(zone.EdgeProximity(p.AimX, p.AimY) * 1_000_000.0);
            }
            return local;
        }, local =>
        {
            lock (total) { for (var k = 0; k < total.Length; k++) total[k] += local[k]; }
        });
        return new Cell(label, (int)total[0], (int)total[1], (int)total[2], (int)total[3], (int)total[4], (int)total[5],
            (int)total[6], total[7], total[7] > 0 ? total[8] / 1_000_000.0 / total[7] : 0);
    }

    public static string Report(int atBats, ulong seed, string? coefficientsPath)
    {
        var c = CultureInfo.InvariantCulture;
        var sb = new StringBuilder();
        sb.AppendLine("# くさい所（縁狙い）の効果測定（issue #872）");
        sb.AppendLine();
        sb.AppendLine($"強打者（ミート60/パワー80/弾道60・右打）× 投手（球速132km/h・キレ50・直球＋スライダー・制球を3段）。各セル {atBats} 打席・seed {seed}。");
        sb.AppendLine("「補正なし」＝際の球は芯を外しやすい補正（contact_quality_per_edge）を0にした旧挙動。");
        sb.AppendLine();
        sb.AppendLine("| 制球 | 条件 | 四球率 | 三振率 | 被打率 | 長打/PA | 本塁打/PA | 球数/PA | 狙いの縁近さ |");
        sb.AppendLine("|---:|---|---:|---:|---:|---:|---:|---:|---:|");
        foreach (var control in ControlLevels)
        {
            var cells = new[]
            {
                Measure("方針なし（補正なし）", atBats, seed, control, false, coefficientsPath, 0.0),
                Measure("方針なし（補正後）", atBats, seed, control, false, coefficientsPath, null),
                Measure("くさい所（補正後）", atBats, seed, control, true, coefficientsPath, null),
            };
            foreach (var m in cells)
            {
                sb.AppendLine(string.Create(c,
                    $"| {control} | {m.Label} | {m.WalkRate * 100:F1}% | {m.StrikeoutRate * 100:F1}% | {m.Average:F3} | {m.ExtraBaseRate * 100:F1}% | {m.HomeRunRate * 100:F2}% | {m.PitchesPerPa:F2} | {m.MeanAimEdgeProximity:F2} |"));
            }
        }
        return sb.ToString();
    }
}
