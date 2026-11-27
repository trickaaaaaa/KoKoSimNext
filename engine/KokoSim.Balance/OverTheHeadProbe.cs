using System.Globalization;
using System.Text;
using KokoSim.Engine.Core;
using KokoSim.Engine.Match.AtBat;
using KokoSim.Engine.Match.Field;
using KokoSim.Engine.Match.Fielding;
using KokoSim.Engine.Players;

namespace KokoSim.Balance;

/// <summary>
/// 「頭を越えた！」字幕が付く打球の実測（issue #1738）。**係数も帯も解決式も動かさない計測専用**。
/// 字幕の旧条件（着地の深さ &gt; 処理野手の守備位置の深さ＋1m）に当たる外野の打球を、結果別に
/// 「守備位置から何m後ろか」「空中で最速の野手が何秒遅れたか」「着地から回収まで何秒か」「二塁打になる余裕」で集計する。
/// </summary>
public static partial class AtBatSimulation
{
    private static readonly double[] BehindEdges = { 1, 3, 6, 10, 15, 25 };
    private static readonly double[] LateEdges = { 0.5, 1.0, 1.5, 2.5 };
    private static readonly double[] RetrieveEdges = { 1, 2, 3, 4, 6 };
    private static readonly double[] SlackEdges = { -1.0, -0.5, 0, 0.5, 1.0, 2.0 };

    private sealed class ProbeBin
    {
        public int Count;
        public readonly int[] Behind = new int[BehindEdges.Length + 1];
        public readonly int[] Late = new int[LateEdges.Length + 1];
        public readonly int[] Retrieve = new int[RetrieveEdges.Length + 1];
        public readonly int[] Slack = new int[SlackEdges.Length + 1];
        public int SlackCount, LateCount, RetrieveCount;
        public int RecomputeMismatch;
        public int Fence, OldCaption, NewCaption;
        public void Merge(ProbeBin o)
        {
            OldCaption += o.OldCaption; NewCaption += o.NewCaption;
            Count += o.Count; SlackCount += o.SlackCount; LateCount += o.LateCount; RetrieveCount += o.RetrieveCount;
            RecomputeMismatch += o.RecomputeMismatch; Fence += o.Fence;
            for (var i = 0; i < Behind.Length; i++) Behind[i] += o.Behind[i];
            for (var i = 0; i < Late.Length; i++) Late[i] += o.Late[i];
            for (var i = 0; i < Retrieve.Length; i++) Retrieve[i] += o.Retrieve[i];
            for (var i = 0; i < Slack.Length; i++) Slack[i] += o.Slack[i];
        }
    }

    private static int Bucket(double v, double[] edges)
    {
        for (var i = 0; i < edges.Length; i++) if (v < edges[i]) return i;
        return edges.Length;
    }

    /// <summary>キー＝"結果/球種/空中で最速の野手の遅れ"（遅れ＝落ちた打球だけ。Out は捕球）。</summary>
    public static string OverTheHeadProbe(int atBats, ulong seed, FieldGeometry? field = null)
    {
        var ctx = BuildContext(null, field) with { CaptureTimeline = true };
        var root = new Xoshiro256Random(seed);
        var forks = new IRandomSource[atBats];
        for (var i = 0; i < atBats; i++) forks[i] = root.Fork((ulong)i);

        var all = new Dictionary<string, ProbeBin>();
        var inPlayOutfield = new int[1];
        Parallel.For(0, atBats,
            () => new Dictionary<string, ProbeBin>(),
            (i, _, local) =>
            {
                var rng = forks[i];
                var batter = SampleBatter(rng);
                var pitcher = SamplePitcher(rng);
                var d = AtBatResolver.ResolveDetailed(batter, pitcher, ctx, rng);
                if (d.Play is not { } play || play.Result is BattedBallResult.Foul or BattedBallResult.HomeRun) return local;
                if (play.RangeM <= FieldGeometry.OutfieldBoundaryM || play.FielderRole is not { } role) return local;
                Interlocked.Increment(ref inPlayOutfield[0]);
                var fielder = ctx.Fielders.First(f => f.Position == role);
                var startRange = Math.Sqrt(fielder.Location.X * fielder.Location.X + fielder.Location.Z * fielder.Location.Z);
                var behind = play.RangeM - startRange;

                // 字幕の付き方の前後比較（新条件は実際のタイムラインの字幕を読む）
                var capKey = $"字幕/{play.Result}";
                if (!local.TryGetValue(capKey, out var cap)) local[capKey] = cap = new ProbeBin();
                cap.Count++;
                if (behind > 1.0) cap.OldCaption++;
                if (d.Timeline is { } tl && tl.Captions.Any(c => c.Text == "頭を越えた！")) cap.NewCaption++;

                if (behind <= 1.0) return local;

                var kind = play.IsFly ? (play.Class == BattedBallClass.Liner ? "Liner" : "Fly") : "Ground";
                var lateBy = play.CatchMarginSeconds is { } cm && cm < 0 ? -cm : 0.0;
                var lateTag = play.Result == BattedBallResult.Out ? "捕球" : lateBy <= 0.5 ? "遅れ0.5s以内" : lateBy <= 1.5 ? "遅れ0.5〜1.5s" : "遅れ1.5s超";
                var key = $"{play.Result}/{kind}/{lateTag}";
                if (!local.TryGetValue(key, out var bin)) local[key] = bin = new ProbeBin();
                bin.Count++;
                bin.Behind[Bucket(behind, BehindEdges)]++;
                if (play.ReachedFence) bin.Fence++;
                if (play.CatchMarginSeconds is { } m && m < 0)
                {
                    bin.LateCount++;
                    bin.Late[Bucket(-m, LateEdges)]++;
                }
                if (play.Result is BattedBallResult.Single or BattedBallResult.Double or BattedBallResult.Triple
                    && play.FieldedAtSeconds is { } at && play.FieldedX is { } fx && play.FieldedZ is { } fz
                    && play.FielderThrowSpeedMps is { } arm)
                {
                    bin.RetrieveCount++;
                    bin.Retrieve[Bucket(at - play.HangTimeSeconds, RetrieveEdges)]++;
                    var c = ctx.Fielding;
                    var ready = ExtraBaseResolver.ReadySeconds(at, play.FielderFielding, c);
                    var ball = new Vector3D(fx, 0, fz);
                    var arrive2 = ExtraBaseResolver.ThrowArriveSeconds(
                        ready, (ctx.Field.SecondBase - ball).Length, arm, c, play.FielderThrowAccuracy);
                    var perBase = RunnerClock.SegmentSeconds(
                        batter.SpeedToFirstMps(), 1, 2, ExtraBaseResolver.BatterContextOf(c), ctx.Field);
                    var toSecond = play.BatterToFirstSeconds + perBase;
                    var slack = arrive2 - (toSecond + c.ExtraBaseMarginSeconds);
                    bin.SlackCount++;
                    bin.Slack[Bucket(slack, SlackEdges)]++;
                    if ((slack >= 0) != (play.Result != BattedBallResult.Single)) bin.RecomputeMismatch++;
                }
                return local;
            },
            local => { lock (all) foreach (var (k, v) in local) { if (!all.TryGetValue(k, out var b)) all[k] = b = new ProbeBin(); b.Merge(v); } });

        var ci = CultureInfo.InvariantCulture;
        var sb = new StringBuilder();
        sb.AppendLine("# 「頭を越えた！」字幕の対象になる打球の実測（issue #1738）");
        sb.AppendLine();
        sb.AppendLine(ci, $"- 打席数 {atBats} / シード {seed} / 外野へ飛んだインプレー（本塁打・ファウル除く） {inPlayOutfield[0]} 本");
        sb.AppendLine("- 対象＝旧条件（着地の深さ＞処理野手の守備位置の深さ＋1m・着地 46m 超）に当たる打球。キー＝結果/球種/遅れ");
        sb.AppendLine();
        sb.AppendLine("## 結果別の本数");
        sb.AppendLine();
        sb.AppendLine("| 結果/球種 | 本数 | うち放置ならフェンスまで転がる球 |");
        sb.AppendLine("|---|---|---|");
        foreach (var (k, b) in all.Where(p => !p.Key.StartsWith("字幕/")).OrderBy(p => p.Key)) sb.AppendLine(ci, $"| {k} | {b.Count} | {b.Fence} |");

        sb.AppendLine();
        sb.AppendLine("## 字幕「頭を越えた！」の付き方（外野へ飛んだインプレー・結果別。旧＝着地が守備位置の深さ＋1m超／新＝実際のタイムラインの字幕）");
        sb.AppendLine();
        sb.AppendLine("| 結果 | 本数 | 旧条件で字幕 | 新条件で字幕 |");
        sb.AppendLine("|---|---|---|---|");
        foreach (var (k, b) in all.Where(p => p.Key.StartsWith("字幕/")).OrderBy(p => p.Key))
            sb.AppendLine(ci, $"| {k[3..]} | {b.Count} | {b.OldCaption} ({100.0 * b.OldCaption / b.Count:F1}%) | {b.NewCaption} ({100.0 * b.NewCaption / b.Count:F1}%) |");

        void Table(string title, string[] labels, Func<ProbeBin, int[]> pick, Func<ProbeBin, int> total)
        {
            sb.AppendLine();
            sb.AppendLine($"## {title}");
            sb.AppendLine();
            sb.AppendLine("| 結果/球種 | 母数 | " + string.Join(" | ", labels) + " |");
            sb.AppendLine("|---|---|" + string.Concat(labels.Select(_ => "---|")));
            foreach (var (k, b) in all.Where(p => !p.Key.StartsWith("字幕/")).OrderBy(p => p.Key))
            {
                var n = total(b);
                if (n == 0) continue;
                var arr = pick(b);
                sb.AppendLine($"| {k} | {n} | " + string.Join(" | ", arr.Select(v => (100.0 * v / n).ToString("F1", ci) + "%")) + " |");
            }
        }

        Table("守備位置からどれだけ後ろに落ちたか（着地の深さ − 処理野手の守備位置の深さ・m）",
            new[] { "〜1", "1〜3", "3〜6", "6〜10", "10〜15", "15〜25", "25〜" }, b => b.Behind, b => b.Count);
        Table("空中で最速の野手の遅れ（−CatchMargin・秒。落ちた打球だけ）",
            new[] { "〜0.5", "0.5〜1.0", "1.0〜1.5", "1.5〜2.5", "2.5〜" }, b => b.Late, b => b.LateCount);
        Table("着地から回収までの秒（安打だけ）",
            new[] { "〜1", "1〜2", "2〜3", "3〜4", "4〜6", "6〜" }, b => b.Retrieve, b => b.RetrieveCount);
        Table("二塁打になる余裕（二塁送球の到達 − 打者走者の二塁到達−マージン・秒。≥0 で二塁打以上。安打だけ）",
            new[] { "〜-1.0", "-1.0〜-0.5", "-0.5〜0", "0〜0.5", "0.5〜1.0", "1.0〜2.0", "2.0〜" }, b => b.Slack, b => b.SlackCount);

        var mismatch = all.Values.Sum(b => b.RecomputeMismatch);
        sb.AppendLine();
        sb.AppendLine(ci, $"- 余裕の再計算と実際の判定（単打か否か）が食い違った本数: {mismatch}（0 でなければ再計算の入力がずれている＝表を信用しない）");
        return sb.ToString();
    }
}
