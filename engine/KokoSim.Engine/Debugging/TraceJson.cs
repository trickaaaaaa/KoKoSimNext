using System.Globalization;
using System.Text;
using KokoSim.Engine.Match.Game;

namespace KokoSim.Engine.Debugging;

/// <summary>
/// 観測レコード → JSONL 1行（設計書17 §4.3）。<b>フィールド名の単一ソースはここ</b>で、
/// Balance CLI の <c>JsonlTraceSink</c> も Unity の <c>DebugBridge</c> も同じ関数を通す
/// （二重実装すると <c>jq</c> のレシピが片方だけ壊れる）。
///
/// <para>外部依存なし（<c>System.Text.Json</c> も使わない）＝Unity からもそのまま呼べる。
/// 非ASCIIはエスケープせずそのまま出す（UTF-8 の JSON として正当で、目でも読める）。</para>
/// </summary>
public static class TraceJson
{
    public static string Game(GameTraceHeader h)
    {
        var sb = new StringBuilder(256);
        sb.Append("{\"t\":\"game\",\"rng\":").Append(Str(h.RngStateHex))
          .Append(",\"fixture\":").Append(Str(h.FixtureFingerprint))
          .Append(",\"away\":").Append(Str(h.AwayName))
          .Append(",\"home\":").Append(Str(h.HomeName))
          .Append(",\"scenario\":").Append(h.ScenarioId is null ? "null" : Str(h.ScenarioId))
          .Append(",\"reg\":").Append(Int(h.RegulationInnings))
          .Append(",\"tb\":").Append(Bool(h.TieBreakEnabled))
          .Append(",\"mercy\":").Append(Bool(h.MercyRuleEnabled))
          .Append('}');
        return sb.ToString();
    }

    public static string Pitch(PitchTrace t)
    {
        var sb = new StringBuilder(512);
        Pitch(sb, t);
        return sb.ToString();
    }

    /// <summary>既存の <see cref="StringBuilder"/> へ直接書く（1行ごとの中間文字列を作らない）。</summary>
    public static void Pitch(StringBuilder sb, PitchTrace t)
    {
        sb.Append("{\"t\":\"pitch\",\"i\":").Append(Int(t.Inning))
          .Append(",\"top\":").Append(Bool(t.IsTop))
          .Append(",\"o\":").Append(Int(t.Outs))
          .Append(",\"b\":").Append(Str(t.BatterId))
          .Append(",\"p\":").Append(Str(t.PitcherId))
          .Append(",\"cnt\":\"").Append(t.BallsBefore).Append('-').Append(t.StrikesBefore).Append('"')
          .Append(",\"n\":").Append(Int(t.PitchNoInPa))
          .Append(",\"N\":").Append(Int(t.PitchNoInGame));

        sb.Append(",\"plan\":{\"ty\":").Append(Str(t.PlanType.ToString()))
          .Append(",\"ax\":").Append(Num(t.PlanAimX, 3))
          .Append(",\"ay\":").Append(Num(t.PlanAimY, 3))
          .Append(",\"kmh\":").Append(Num(t.PlanVelocityKmh, 1))
          .Append(",\"stuff\":").Append(Num(t.PlanStuff, 2)).Append('}');

        sb.Append(",\"act\":{\"x\":").Append(Num(t.ActualX, 3))
          .Append(",\"y\":").Append(Num(t.ActualY, 3))
          .Append(",\"kmh\":").Append(Num(t.ActualKmh, 1))
          .Append(",\"ft\":").Append(Num(t.FlightTimeSeconds, 4))
          .Append(",\"ivb\":").Append(Num(t.InducedVerticalBreakM, 4))
          .Append(",\"ihb\":").Append(Num(t.InducedHorizontalBreakM, 4))
          .Append(",\"zone\":").Append(Bool(t.InZone)).Append('}');

        sb.Append(",\"bat\":{\"pSw\":").Append(Num(t.SwingProbability, 4))
          .Append(",\"sw\":").Append(Bool(t.Swung)).Append('}');

        sb.Append(",\"res\":{\"k\":").Append(Str(t.Kind.ToString()));
        if (t.ExitVelocityKmh is { } ev)
        {
            sb.Append(",\"ev\":").Append(Num(ev, 1))
              .Append(",\"la\":").Append(Num(t.LaunchAngleDeg ?? 0, 1))
              .Append(",\"sa\":").Append(Num(t.SprayAngleDeg ?? 0, 1));
        }
        sb.Append('}');

        sb.Append(",\"st\":{\"press\":").Append(Int(t.PressureIndex))
          .Append(",\"rat\":").Append(Bool(t.Rattled))
          .Append(",\"pf\":").Append(Int(t.PitchingFatigue))
          .Append(",\"gear\":").Append(Str(t.Gear.ToString()))
          .Append(",\"pol\":").Append(Str(t.Policy.ToString())).Append('}');

        if (t.ChosenSign is not null || t.SignCandidatesCsv is not null)
        {
            sb.Append(",\"ai\":{\"sign\":").Append(t.ChosenSign is null ? "null" : Str(t.ChosenSign))
              .Append(",\"cand\":").Append(t.SignCandidatesCsv is null ? "null" : Str(t.SignCandidatesCsv))
              .Append('}');
        }

        sb.Append(",\"rng\":{\"sid\":").Append(t.RngStreamId.ToString(CultureInfo.InvariantCulture))
          .Append(",\"d\":").Append(Int(t.RngDrawsInPitch)).Append('}')
          .Append(",\"forced\":").Append(Bool(t.Forced))
          .Append('}');
    }

    public static string Pa(PaTrace t)
    {
        var sb = new StringBuilder(192);
        sb.Append("{\"t\":\"pa\",\"i\":").Append(Int(t.Inning))
          .Append(",\"top\":").Append(Bool(t.IsTop))
          .Append(",\"b\":").Append(Str(t.BatterId))
          .Append(",\"res\":").Append(Str(t.Result.ToString()))
          .Append(",\"rbi\":").Append(Int(t.Rbi))
          .Append(",\"outsAfter\":").Append(Int(t.OutsAfter))
          .Append(",\"pitches\":").Append(Int(t.Pitches))
          .Append(",\"bases\":").Append(Str(t.RunnerSummary))
          .Append(",\"forced\":").Append(Bool(t.Forced))
          .Append('}');
        return sb.ToString();
    }

    public static string End(GameResult r)
    {
        var sb = new StringBuilder(128);
        sb.Append("{\"t\":\"end\",\"away\":").Append(Int(r.AwayRuns))
          .Append(",\"home\":").Append(Int(r.HomeRuns))
          .Append(",\"innings\":").Append(Int(r.InningsPlayed))
          .Append(",\"pitches\":").Append(Int(r.TotalPitches))
          .Append(",\"forced\":").Append(Bool(r.HasForcedOutcomes))
          .Append('}');
        return sb.ToString();
    }

    // ===== 最小限の JSON プリミティブ =====

    /// <summary>JSON文字列リテラル（引用符込み）。制御文字と <c>" \</c> だけ逃がす。</summary>
    public static string Str(string? s)
    {
        var sb = new StringBuilder((s?.Length ?? 0) + 2);
        sb.Append('"');
        foreach (var c in s ?? "")
        {
            switch (c)
            {
                case '"': sb.Append("\\\""); break;
                case '\\': sb.Append("\\\\"); break;
                case '\n': sb.Append("\\n"); break;
                case '\r': sb.Append("\\r"); break;
                case '\t': sb.Append("\\t"); break;
                default:
                    if (c < 0x20) sb.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
                    else sb.Append(c);
                    break;
            }
        }
        sb.Append('"');
        return sb.ToString();
    }

    public static string Int(int v) => v.ToString(CultureInfo.InvariantCulture);
    public static string Bool(bool v) => v ? "true" : "false";

    /// <summary>桁を固定して書く（差分比較のノイズを減らす）。NaN/∞ は JSON にできないので null。</summary>
    public static string Num(double v, int digits)
        => double.IsNaN(v) || double.IsInfinity(v)
            ? "null"
            : System.Math.Round(v, digits).ToString("0.####", CultureInfo.InvariantCulture);
}
