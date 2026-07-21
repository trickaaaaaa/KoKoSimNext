using System.Diagnostics;
using System.Globalization;
using KokoSim.Engine.Core;
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

    public sealed record Summary(int Games, long Lines, double TracedMs, double UntracedMs)
    {
        public double OverheadRatio => UntracedMs > 0 ? TracedMs / UntracedMs - 1.0 : 0.0;
    }

    public static Summary Run(Options o)
    {
        var ctx = GameSimulation.BuildContext(o.CoefficientsPath, o.Field);
        var brain = o.UseTacticsBrain ? new StandardTacticsBrain(ctx.Tactics, ctx.Baserunning) : null;

        double untracedMs = 0, tracedRep = double.MaxValue;
        if (o.MeasureOverhead)
        {
            // ウォームアップ（JIT・TrajectoryFeatureTable の初回構築）を計測から外す。
            // これを省くと1回目の実行だけ桁違いに遅く、オーバーヘッド比が意味を成さない。
            PlayAll(o with { Games = 1 }, ctx, brain, sink: null);

            // 観測オフ／オンを交互に2回ずつ回して各々の最小値を採る。単発計測は GC とスレッド割り当ての
            // ゆらぎが観測コスト（数%想定）より大きく、符号が反転することすらあるため。
            untracedMs = Measure(() => PlayAll(o, ctx, brain, sink: null));
            using (var probe = JsonlTraceSink.OpenFile(o.OutPath, o.Only))
            {
                tracedRep = Measure(() => PlayAll(o, ctx, brain, probe));
            }
            untracedMs = System.Math.Min(untracedMs, Measure(() => PlayAll(o, ctx, brain, sink: null)));
        }

        using var sink = JsonlTraceSink.OpenFile(o.OutPath, o.Only);
        var sw = Stopwatch.StartNew();
        PlayAll(o, ctx, brain, sink);
        var tracedMs = System.Math.Min(sw.Elapsed.TotalMilliseconds, tracedRep);

        return new Summary(o.Games, sink.Lines, tracedMs, untracedMs);
    }

    private static double Measure(System.Action action)
    {
        var sw = Stopwatch.StartNew();
        action();
        return sw.Elapsed.TotalMilliseconds;
    }

    private static void PlayAll(Options o, GameContext ctx, ITacticsBrain? brain, JsonlTraceSink? sink)
    {
        // 観測は逐次実行（JSONL の行順を試合順に保つため）。統計シムのような並列化はしない。
        var traced = sink is null
            ? ctx
            : ctx with { CaptureTrace = true, TraceSink = sink, ScenarioId = o.ScenarioId };

        var root = new Xoshiro256Random(o.Seed);
        for (var i = 0; i < o.Games; i++)
        {
            // simulate-games と同じ Fork 規約（i→ストリーム）。同じシードなら同じ対戦・同じ展開になる。
            var g = root.Fork((ulong)i);
            var away = GameSimulation.GenerateTeam(g, "遠征校") with { Tactics = brain };
            var home = GameSimulation.GenerateTeam(g, "地元校") with { Tactics = brain };
            GameEngine.Play(away, home, traced, g);
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
            sb.AppendLine(string.Create(c,
                $"  観測オン {s.TracedMs:F0}ms / オフ {s.UntracedMs:F0}ms / オーバーヘッド {s.OverheadRatio:P1}"));
        }
        return sb.ToString();
    }
}
