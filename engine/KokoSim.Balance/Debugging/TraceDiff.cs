using System.Collections.Generic;
using System.Globalization;
using System.Text;
using System.Text.Json;

namespace KokoSim.Balance.Debugging;

/// <summary>
/// <c>trace-diff</c> サブコマンド（設計書17 §4.4, F1）。2本のトレースを突き合わせ、
/// <b>「最初に食い違った球」</b>と分布サマリの差を出す。
///
/// <para>存在理由は「回帰の原因特定を二分探索から1コマンドへ落とす」こと。係数を1つ変えたときに、
/// どのイニング・どのカウント・どのフィールドから結果が割れ始めたのかが1行でわかる状態にする。</para>
/// </summary>
public static class TraceDiff
{
    /// <summary>比較対象のフィールド（JSONL の短縮キー。設計書17 §4.3 を単一ソースとする）。</summary>
    private static readonly (string Path, string Label)[] ComparedFields =
    {
        ("plan.ty", "球種"),
        ("plan.ax", "狙いX"),
        ("plan.ay", "狙いY"),
        ("plan.kmh", "球速"),
        ("plan.stuff", "球威"),
        ("act.x", "着弾X"),
        ("act.y", "着弾Y"),
        ("act.zone", "ゾーン内外"),
        ("bat.pSw", "スイング確率"),
        ("bat.sw", "スイング"),
        ("res.k", "球結果"),
        ("rng.d", "乱数消費数"),
    };

    public sealed record PitchLine(int Index, JsonElement Json)
    {
        public string Where
        {
            get
            {
                var c = CultureInfo.InvariantCulture;
                var i = Json.GetProperty("i").GetInt32();
                var top = Json.GetProperty("top").GetBoolean() ? "表" : "裏";
                var cnt = Json.GetProperty("cnt").GetString();
                var n = Json.GetProperty("n").GetInt32();
                return string.Create(c, $"{i}回{top} {cnt} {n}球目（通算{Json.GetProperty("N").GetInt32()}球目）");
            }
        }
    }

    public sealed record Distribution
    {
        public long Pitches { get; init; }
        public long Swings { get; init; }
        public long InZone { get; init; }
        public double MeanVelocityKmh { get; init; }
        public double MeanSwingProbability { get; init; }
        public Dictionary<string, long> PitchTypes { get; init; } = new();

        public double SwingRate => Pitches > 0 ? (double)Swings / Pitches : 0;
        public double ZoneRate => Pitches > 0 ? (double)InZone / Pitches : 0;
    }

    public sealed record Result(
        PitchLine? FirstDivergence, string? DivergedField, string? ValueA, string? ValueB,
        int CountA, int CountB, Distribution DistA, Distribution DistB)
    {
        public bool Identical => FirstDivergence is null && CountA == CountB;
    }

    public static Result Compare(string pathA, string pathB)
    {
        var a = ReadPitches(pathA);
        var b = ReadPitches(pathB);

        PitchLine? first = null;
        string? field = null, va = null, vb = null;
        var n = System.Math.Min(a.Count, b.Count);
        for (var i = 0; i < n && first is null; i++)
        {
            foreach (var (path, label) in ComparedFields)
            {
                var x = Read(a[i].Json, path);
                var y = Read(b[i].Json, path);
                if (x == y) continue;
                first = a[i];
                field = label + "（" + path + "）";
                va = x;
                vb = y;
                break;
            }
        }
        // 球数そのものが違えば、短い側の末尾を食い違い位置として指す。
        if (first is null && a.Count != b.Count)
        {
            first = a.Count < b.Count ? (a.Count > 0 ? a[a.Count - 1] : null) : (b.Count > 0 ? b[b.Count - 1] : null);
            field = "球数（トレースの長さ）";
            va = a.Count.ToString(CultureInfo.InvariantCulture);
            vb = b.Count.ToString(CultureInfo.InvariantCulture);
        }

        return new Result(first, field, va, vb, a.Count, b.Count, Summarize(a), Summarize(b));
    }

    private static List<PitchLine> ReadPitches(string path)
    {
        var list = new List<PitchLine>();
        var idx = 0;
        foreach (var line in System.IO.File.ReadLines(path))
        {
            if (line.Length == 0) continue;
            using var doc = JsonDocument.Parse(line);
            if (!doc.RootElement.TryGetProperty("t", out var t) || t.GetString() != "pitch") continue;
            list.Add(new PitchLine(idx++, doc.RootElement.Clone()));
        }
        return list;
    }

    /// <summary>"plan.ty" のようなドット区切りで値を引き、比較用の文字列にする（無ければ "-"）。</summary>
    private static string Read(JsonElement root, string path)
    {
        var cur = root;
        foreach (var seg in path.Split('.'))
        {
            if (cur.ValueKind != JsonValueKind.Object || !cur.TryGetProperty(seg, out cur)) return "-";
        }
        return cur.ValueKind switch
        {
            JsonValueKind.String => cur.GetString() ?? "-",
            JsonValueKind.True => "true",
            JsonValueKind.False => "false",
            JsonValueKind.Null => "null",
            _ => cur.GetRawText(),
        };
    }

    private static Distribution Summarize(List<PitchLine> lines)
    {
        long swings = 0, inZone = 0;
        double velo = 0, pSw = 0;
        var types = new Dictionary<string, long>();
        foreach (var l in lines)
        {
            if (Read(l.Json, "bat.sw") == "true") swings++;
            if (Read(l.Json, "act.zone") == "true") inZone++;
            if (double.TryParse(Read(l.Json, "plan.kmh"), NumberStyles.Float, CultureInfo.InvariantCulture, out var v)) velo += v;
            if (double.TryParse(Read(l.Json, "bat.pSw"), NumberStyles.Float, CultureInfo.InvariantCulture, out var p)) pSw += p;
            var ty = Read(l.Json, "plan.ty");
            types[ty] = types.TryGetValue(ty, out var c) ? c + 1 : 1;
        }
        var n = lines.Count;
        return new Distribution
        {
            Pitches = n,
            Swings = swings,
            InZone = inZone,
            MeanVelocityKmh = n > 0 ? velo / n : 0,
            MeanSwingProbability = n > 0 ? pSw / n : 0,
            PitchTypes = types,
        };
    }

    public static string Report(Result r, string pathA, string pathB)
    {
        var c = CultureInfo.InvariantCulture;
        var sb = new StringBuilder();
        sb.AppendLine("# トレース差分（設計書17 §4.4）");
        sb.AppendLine();
        sb.AppendLine(string.Create(c, $"- A: `{pathA}`（{r.CountA}球）"));
        sb.AppendLine(string.Create(c, $"- B: `{pathB}`（{r.CountB}球）"));
        sb.AppendLine();

        sb.AppendLine("## 最初の食い違い");
        sb.AppendLine();
        if (r.Identical)
        {
            sb.AppendLine("**なし**（2本のトレースは比較対象フィールドの範囲で完全一致）。");
        }
        else if (r.FirstDivergence is null)
        {
            sb.AppendLine(string.Create(c, $"球数が違う（A={r.CountA} / B={r.CountB}）が、共通部分には食い違いなし。"));
        }
        else
        {
            sb.AppendLine(string.Create(c, $"- 位置: **{r.FirstDivergence.Where}**（pitch行 #{r.FirstDivergence.Index}）"));
            sb.AppendLine(string.Create(c, $"- フィールド: **{r.DivergedField}**"));
            sb.AppendLine(string.Create(c, $"- A = `{r.ValueA}` / B = `{r.ValueB}`"));
        }
        sb.AppendLine();

        sb.AppendLine("## 分布サマリ");
        sb.AppendLine();
        sb.AppendLine("| 指標 | A | B | 差 |");
        sb.AppendLine("|---|---:|---:|---:|");
        Row(sb, c, "球数", r.DistA.Pitches, r.DistB.Pitches);
        RowP(sb, c, "スイング率", r.DistA.SwingRate, r.DistB.SwingRate);
        RowP(sb, c, "ゾーン率", r.DistA.ZoneRate, r.DistB.ZoneRate);
        RowF(sb, c, "平均球速(km/h)", r.DistA.MeanVelocityKmh, r.DistB.MeanVelocityKmh);
        RowF(sb, c, "平均スイング確率", r.DistA.MeanSwingProbability, r.DistB.MeanSwingProbability);
        sb.AppendLine();

        sb.AppendLine("### 球種比");
        sb.AppendLine();
        sb.AppendLine("| 球種 | A | B | 差 |");
        sb.AppendLine("|---|---:|---:|---:|");
        var keys = new SortedSet<string>(r.DistA.PitchTypes.Keys);
        keys.UnionWith(r.DistB.PitchTypes.Keys);
        foreach (var k in keys)
        {
            r.DistA.PitchTypes.TryGetValue(k, out var x);
            r.DistB.PitchTypes.TryGetValue(k, out var y);
            Row(sb, c, k, x, y);
        }
        return sb.ToString();
    }

    private static void Row(StringBuilder sb, CultureInfo c, string label, long a, long b)
        => sb.AppendLine(string.Create(c, $"| {label} | {a} | {b} | {b - a:+#;-#;0} |"));

    private static void RowP(StringBuilder sb, CultureInfo c, string label, double a, double b)
        => sb.AppendLine(string.Create(c, $"| {label} | {a:P2} | {b:P2} | {b - a:+0.00%;-0.00%;0.00%} |"));

    private static void RowF(StringBuilder sb, CultureInfo c, string label, double a, double b)
        => sb.AppendLine(string.Create(c, $"| {label} | {a:F3} | {b:F3} | {b - a:+0.000;-0.000;0.000} |"));
}
