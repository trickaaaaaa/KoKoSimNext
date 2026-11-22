using System;
using System.Collections;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using KokoSim.Engine.Match.Game;

namespace KokoSim.Engine.Debugging;

/// <summary>
/// 試合が走った係数の識別子（設計書17 §4.3 のトレースヘッダ <c>coeff</c>）。<c>trace-diff</c> の2本が
/// 同じ係数で走ったか、どの係数セットで走ったかを辿るための目印で、<b>観測専用</b>（試合の解決は読まない）。
///
/// <para><see cref="GameContext"/> の係数ブロックとルール値を、プロパティ名順に不変カルチャで文字列化して
/// SHA256 の先頭 <see cref="Length"/> 桁にする。係数の中身が1つでも違えば変わり、同じ中身なら
/// プロセスをまたいでも同じ（<c>GetHashCode</c> は文字列で揺れるので使わない）。</para>
///
/// <para>リフレクションで木を歩くので1回あたり安くない。<b>試合ごとではなく呼び出し側が1回だけ</b>計算して
/// <see cref="GameContext.TraceCoefficientsId"/> へ載せること（観測オーバーヘッドの帯を守る）。</para>
/// </summary>
public static class CoefficientFingerprint
{
    /// <summary>識別子の桁数（SHA256 先頭）。</summary>
    public const int Length = 8;

    private const int MaxDepth = 16;

    public static string Of(GameContext ctx)
    {
        var sb = new StringBuilder(8192);
        Append(sb, "aero", ctx.Aerodynamics);
        Append(sb, "mound", ctx.Mound);
        Append(sb, "zone", ctx.StrikeZone);
        Append(sb, "field", ctx.Field);
        Append(sb, "pitching", ctx.Pitching);
        Append(sb, "batting", ctx.Batting);
        Append(sb, "fielding", ctx.Fielding);
        Append(sb, "baserunning", ctx.Baserunning);
        Append(sb, "fatigue", ctx.Fatigue);
        Append(sb, "recovery", ctx.PitchRecovery);
        Append(sb, "weather", ctx.Weather);
        Append(sb, "form", ctx.Form);
        Append(sb, "skills", ctx.Skills);
        Append(sb, "pressure", ctx.Pressure);
        Append(sb, "momentum", ctx.Momentum);
        Append(sb, "injury", ctx.MatchInjury);
        Append(sb, "tactics", ctx.Tactics);
        Append(sb, "personalities", ctx.Personalities);
        Append(sb, "chemistry", ctx.Chemistry);
        Append(sb, "cohesion", ctx.Cohesion);
        Append(sb, "relief", ctx.ReliefBackfill);
        Append(sb, "rules", new object[]
        {
            ctx.RegulationInnings, ctx.MaxInnings, ctx.MercyRuleEnabled, ctx.TieBreakEnabled,
            ctx.TieBreakStartInning, ctx.WeeklyPitchLimit, ctx.PressureStageBonus,
        });

        using var sha = SHA256.Create();
        var bytes = sha.ComputeHash(Encoding.UTF8.GetBytes(sb.ToString()));
        var hex = new StringBuilder(Length);
        for (var i = 0; i < Length / 2; i++) hex.Append(bytes[i].ToString("x2", CultureInfo.InvariantCulture));
        return hex.ToString();
    }

    private static void Append(StringBuilder sb, string name, object? value)
    {
        sb.Append(name).Append('=');
        Walk(sb, value, 0);
        sb.Append('\n');
    }

    private static void Walk(StringBuilder sb, object? v, int depth)
    {
        switch (v)
        {
            case null:
                sb.Append("null");
                return;
            case string s:
                sb.Append('"').Append(s).Append('"');
                return;
            case bool b:
                sb.Append(b ? '1' : '0');
                return;
            case double d:
                sb.Append(d.ToString("R", CultureInfo.InvariantCulture));
                return;
            case float f:
                sb.Append(f.ToString("R", CultureInfo.InvariantCulture));
                return;
            case Enum e:
                sb.Append(e.ToString());
                return;
            case IFormattable fmt when v.GetType().IsPrimitive || v is decimal:
                sb.Append(fmt.ToString(null, CultureInfo.InvariantCulture));
                return;
        }

        if (depth >= MaxDepth)
        {
            sb.Append('~');
            return;
        }

        if (v is IDictionary dict)
        {
            // 並びが挿入順に依らないよう、キーの文字列で整列する。
            var entries = dict.Keys.Cast<object>()
                .Select(k => (Key: Convert.ToString(k, CultureInfo.InvariantCulture) ?? "", Value: dict[k]))
                .OrderBy(x => x.Key, StringComparer.Ordinal);
            sb.Append('{');
            foreach (var (key, value) in entries)
            {
                sb.Append(key).Append(':');
                Walk(sb, value, depth + 1);
                sb.Append(',');
            }
            sb.Append('}');
            return;
        }

        if (v is IEnumerable seq)
        {
            sb.Append('[');
            foreach (var item in seq)
            {
                Walk(sb, item, depth + 1);
                sb.Append(',');
            }
            sb.Append(']');
            return;
        }

        var t = v.GetType();
        sb.Append(t.Name).Append('{');
        foreach (var p in t.GetProperties(BindingFlags.Public | BindingFlags.Instance)
                     .Where(p => p.GetIndexParameters().Length == 0 && p.CanRead)
                     .OrderBy(p => p.Name, StringComparer.Ordinal))
        {
            sb.Append(p.Name).Append(':');
            Walk(sb, p.GetValue(v), depth + 1);
            sb.Append(',');
        }
        sb.Append('}');
    }
}
