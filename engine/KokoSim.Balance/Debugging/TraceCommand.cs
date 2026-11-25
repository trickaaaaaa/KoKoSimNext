using System.Diagnostics;
using System.Globalization;
using KokoSim.Engine.Core;
using KokoSim.Engine.Debugging;
using KokoSim.Engine.Match.Field;
using KokoSim.Engine.Match.Game;
using KokoSim.Engine.Match.Tactics;

namespace KokoSim.Balance.Debugging;

/// <summary>
/// <c>trace</c> サブコマンド（設計書17 §4.4, F1）。試合を回して1球単位の観測を JSONL へ書く。
/// <c>simulate-games</c> と同じチーム生成・同じ Fork 規約を使うので、同じ <c>--seed</c> なら同じ試合になる。
/// </summary>
public static class TraceCommand
{
    public sealed record Options
    {
        public int Games { get; init; } = 1;
        public ulong Seed { get; init; } = 42UL;
        public string OutPath { get; init; } = "out/trace.jsonl";
        public string? CoefficientsPath { get; init; }
        public FieldGeometry? Field { get; init; }
        public bool UseTacticsBrain { get; init; }
        public JsonlTraceSink.Kinds Only { get; init; } = JsonlTraceSink.Kinds.All;
        /// <summary>注入シナリオid（設計書17 §3.4, F2）。null=通常の試合。</summary>
        public string? ScenarioId { get; init; }
        /// <summary>強制発動（設計書17 §6.1, F4）。null=なし。</summary>
        public string? Force { get; init; }
        /// <summary>観測オフの同条件実行と時間を比べてオーバーヘッドを測る（F1 DoD）。</summary>
        public bool MeasureOverhead { get; init; }
    }

    /// <summary>
    /// <paramref name="OverheadRatio"/> は観測オン/オフの塊ごとの比の中央値−1（<c>--measure</c> のときだけ。
    /// 塊を交互に回すので、周りの負荷は両側に同じだけ乗る）。<paramref name="TracedMs"/>/<paramref name="UntracedMs"/> は
    /// 計測した塊の合計で、表示用。
    /// </summary>
    public sealed record Summary(int Games, long Lines, double TracedMs, double UntracedMs, double OverheadRatio);

    /// <summary>計測の塊の試合数。塊が短いほど、周りの負荷の波がオン・オフ両方に同じだけ乗る。</summary>
    internal const int MeasureChunkGames = 5;

    public static Summary Run(Options o)
    {
        var ctx = GameSimulation.BuildContext(o.CoefficientsPath, o.Field);
        var brain = o.UseTacticsBrain ? new StandardTacticsBrain(ctx.Tactics, ctx.Baserunning) : null;

        using var sink = JsonlTraceSink.OpenFile(o.OutPath, o.Only);
        if (!o.MeasureOverhead)
        {
            var sw = Stopwatch.StartNew();
            PlayAll(o, ctx, brain, sink, 0, o.Games);
            return new Summary(o.Games, sink.Lines, sw.Elapsed.TotalMilliseconds, 0, 0);
        }

        // 同じ試合（同じ Fork 添字）を塊ごとに観測オフ／オンで1回ずつ回し、塊ごとの比の中央値を採る。
        // 全試合をオフ→オンと通しで回す方式は、オンの計測中だけ周りのテスト／プロセスが重なると
        // オンだけが何倍も遅く出た（CI の Heavy 並列で 53514ms vs 8475ms＝issue #969）。
        // 交互に細かく回せば負荷の波は両側に乗り、中央値が重なった塊の外れ値を捨てる。
        // 先頭の塊はオフ→オンの順に回して JIT・TrajectoryFeatureTable の初回構築を吸収し、計測から外す
        // （オンの分はそのまま出力へ書く＝出力は全試合ぶんが試合順に並ぶ）。
        // 先頭の塊は捨てるので、塊が1つしか無いと何も計測できない（5試合以下で常に 0.0% になっていた）＝塊は試合数の半分まで縮める。
        var chunk = System.Math.Min(MeasureChunkGames, System.Math.Max(1, o.Games / 2));
        double onMs = 0, offMs = 0;
        var ratios = new List<double>();
        for (var first = 0; first < o.Games; first += chunk)
        {
            var n = System.Math.Min(chunk, o.Games - first);
            var onFirst = first / chunk % 2 == 1;
            double off, on;
            if (onFirst)
            {
                on = Measure(() => PlayAll(o, ctx, brain, sink, first, n));
                off = Measure(() => PlayAll(o, ctx, brain, null, first, n));
            }
            else
            {
                off = Measure(() => PlayAll(o, ctx, brain, null, first, n));
                on = Measure(() => PlayAll(o, ctx, brain, sink, first, n));
            }
            if (first == 0) continue;
            onMs += on; offMs += off;
            if (off > 0) ratios.Add(on / off);
        }

        return new Summary(o.Games, sink.Lines, onMs, offMs, ratios.Count > 0 ? Median(ratios) - 1.0 : double.NaN);
    }

    private static double Median(List<double> values)
    {
        values.Sort();
        var m = values.Count / 2;
        return values.Count % 2 == 1 ? values[m] : (values[m - 1] + values[m]) / 2.0;
    }

    private static double Measure(System.Action action)
    {
        var sw = Stopwatch.StartNew();
        action();
        return sw.Elapsed.TotalMilliseconds;
    }

    private static void PlayAll(Options o, GameContext ctx, ITacticsBrain? brain, JsonlTraceSink? sink, int firstGame, int count)
    {
        // 観測は逐次実行（JSONL の行順を試合順に保つため）。統計シムのような並列化はしない。
        // ヘッダの seed / coeff（設計書17 §4.3）。係数の識別子は歩くのが高いので1回だけ計算する。
        var coeffId = sink is null ? null : CoefficientFingerprint.Of(ctx);
        GameContext Traced(GameContext c, ulong matchSeed) => sink is null
            ? c
            : c with { CaptureTrace = true, TraceSink = sink, TraceSeed = matchSeed, TraceCoefficientsId = coeffId };

        // 場面ジャンプ（設計書17 §3.4, F2）: --scenario 指定時は data/debug/scenarios.yaml から組む。
        ScenarioBuilder.Built? scenario = null;
        ScenarioDefinition? scenarioDef = null;
        if (o.ScenarioId is not null)
        {
            var catalog = KokoSim.Config.DebugScenarioLoader.LoadFromRepoOrEmpty();
            if (!catalog.TryGet(o.ScenarioId, out var def))
            {
                throw new System.ArgumentException(
                    $"未知のシナリオid: {o.ScenarioId}（{KokoSim.Config.DebugScenarioLoader.DefaultRelativePath} に {catalog.Count} 件）");
            }
            scenarioDef = def;
            scenario = ScenarioBuilder.Build(def, ctx, o.Seed);
        }

        // 強制発動（設計書17 §6.1）。--force が優先し、無ければシナリオ側の force 指定を使う。
        var forced = ForcedOutcome.None;
        if (o.Force is not null)
        {
            if (!ForcedOutcomes.TryParse(o.Force, out forced))
            {
                throw new System.ArgumentException(
                    $"未知の --force 値: {o.Force}（{string.Join("|", System.Enum.GetNames(typeof(ForcedOutcome)))}）");
            }
        }
        else if (scenarioDef is not null)
        {
            forced = scenarioDef.ResolveForce();
        }

        var seed = scenario?.Seed ?? o.Seed;
        var root = new Xoshiro256Random(seed);
        for (var i = firstGame; i < firstGame + count; i++)
        {
            // simulate-games と同じ Fork 規約（i→ストリーム）。同じシードなら同じ対戦・同じ展開になる。
            var g = root.Fork((ulong)i);
            var (a, h, c, start) = scenario is { } sc
                ? (sc.Away, sc.Home, Traced(sc.Ctx, seed), sc.Start)
                : (GameSimulation.GenerateTeam(g, "遠征校") with { Tactics = brain },
                   GameSimulation.GenerateTeam(g, "地元校") with { Tactics = brain },
                   Traced(ctx, seed), null);

            var p = GameEngine.NewProgress(a, h, c, g, scenarioStart: start);
            // 強制発動（設計書17 §6.1, F4）: 最初の1打席に効かせる。--scenario の force 指定も同じ経路。
            if (forced != ForcedOutcome.None) p.ForceNext(forced);
            foreach (var _ in GameEngine.Steps(p)) { /* drain */ }
            GameEngine.BuildResult(p);
        }
    }

    /// <summary><c>--only</c> の値（"pitch" / "pa" / "game,end" …）を解釈する。</summary>
    public static JsonlTraceSink.Kinds ParseKinds(string value)
    {
        var kinds = (JsonlTraceSink.Kinds)0;
        foreach (var part in value.Split(','))
        {
            kinds |= part.Trim().ToLowerInvariant() switch
            {
                "game" => JsonlTraceSink.Kinds.Game,
                "pitch" => JsonlTraceSink.Kinds.Pitch,
                "pa" => JsonlTraceSink.Kinds.Pa,
                "end" => JsonlTraceSink.Kinds.End,
                "all" => JsonlTraceSink.Kinds.All,
                _ => throw new System.ArgumentException($"未知の --only 値: {part}（game|pitch|pa|end|all）"),
            };
        }
        return kinds;
    }

    public static string Report(Summary s, Options o)
    {
        var c = CultureInfo.InvariantCulture;
        var sb = new System.Text.StringBuilder();
        sb.AppendLine(string.Create(c, $"トレースを書き出しました: {o.OutPath}"));
        sb.AppendLine(string.Create(c, $"  試合数 {s.Games} / 行数 {s.Lines} / シード {o.Seed}"));
        if (o.MeasureOverhead)
        {
            var overhead = double.IsNaN(s.OverheadRatio)
                ? "計測不能（2試合以上が要る）"
                : s.OverheadRatio.ToString("P1", c) + "（塊ごとの比の中央値）";
            sb.AppendLine(string.Create(c,
                $"  観測オン {s.TracedMs:F0}ms / オフ {s.UntracedMs:F0}ms（先頭の塊を除く計測分）/ オーバーヘッド {overhead}"));
        }
        return sb.ToString();
    }
}
