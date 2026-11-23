using KokoSim.Engine.Core;
using KokoSim.Engine.Players;

namespace KokoSim.Engine.Season;

/// <summary>
/// 投打の利きの生成（設計書01 §1.1c「左のプレミア」・issue #1475）。自校の新入生（<see cref="ProspectGenerator"/>）・
/// AI校の永続ロスター（<see cref="Nation.Roster.AiRosterFactory"/>）・試合用のAI校（<see cref="Nation.StrengthTeamFactory"/>）
/// の3経路が共有する唯一の式。
///
/// <para><b>乱数は先に引いて、判定は後で下す</b>。一様乱数（投げる手・両打ち・打つ手）を従来と同じ位置・同じ回数で
/// 引いておき（<see cref="Draw"/>）、投手らしさ・学校の強さ・打者の型が分かってから閾値と比べる。
/// 乱数の消費列は旧実装（定数の確率）と1ビットも変わらない＝能力など後続の抽選は動かない（不変条件#2）。</para>
///
/// <para>投手らしさは創発（<see cref="PitcherRoleResolver.RoleWeight"/>・判定境界で 0.5）で読む。
/// 「投手」属性は持たない（設計書01 §1.1a）。</para>
/// </summary>
public static class HandednessModel
{
    /// <summary>先に引いた一様乱数。両打ちを引いた選手は打つ手の乱数を引かない（旧実装と同じ消費数）。</summary>
    public readonly record struct Draw(double Throw, double Switch, double Bats);

    /// <summary>旧実装と同じ順・同じ回数で一様乱数を引く（投げる手 → 両打ち → 打つ手（両打ちでなければ））。</summary>
    public static Draw Roll(IRandomSource rng, RosterCoefficients c)
    {
        var t = rng.NextDouble();
        var s = rng.NextDouble();
        var b = s < c.SwitchProb ? double.NaN : rng.NextDouble();
        return new Draw(t, s, b);
    }

    /// <summary>
    /// 左投げの確率。素の確率（左利き人口相当）に、投手らしさ <paramref name="roleWeight"/> に比例した上乗せを足す。
    /// 上乗せは学校の強さ（新入生の能力中心 <paramref name="schoolCenter"/>）で増減する＝強豪ほど左腕を集める。
    /// 野手（roleWeight≈0）は素の確率のまま。
    /// </summary>
    public static double LeftThrowProbability(double roleWeight, double schoolCenter, RosterCoefficients c)
    {
        var w = MathUtil.Clamp(roleWeight, 0.0, 1.0);
        var bonus = c.LeftThrowPitcherBonus
                    + c.LeftThrowStrengthSlope * (schoolCenter - c.LeftThrowStrengthPivot);
        var p = c.ThrowLeftProb + w * System.Math.Max(0.0, bonus);
        return MathUtil.Clamp(p, 0.0, c.LeftThrowMax);
    }

    /// <summary>
    /// 打者の型の点（0 が平均的）。巧打（ミート−パワー）と俊足（走力−肩）の平均。どちらも同じ系統どうしの差なので
    /// 学校の強さに依らず平均がほぼ0になる。正＝俊足・巧打寄り、負＝長打・強肩寄り。
    /// </summary>
    public static double BatterTypeScore(int contact, int power, int speed, int armStrength)
        => ((contact - power) + (speed - armStrength)) / 2.0;

    /// <summary>右投げの選手が左打ちになる確率。俊足・巧打寄りほど高い。</summary>
    public static double LeftBatGivenRightThrowProbability(double typeScore, RosterCoefficients c)
        => MathUtil.Clamp(c.BatLeftGivenRightThrow + c.BatLeftTypeSlope * typeScore,
            c.BatLeftGivenRightThrowMin, c.BatLeftGivenRightThrowMax);

    /// <summary>投げる手を決める。</summary>
    public static Handedness Throws(Draw d, double roleWeight, double schoolCenter, RosterCoefficients c)
        => d.Throw < LeftThrowProbability(roleWeight, schoolCenter, c) ? Handedness.Left : Handedness.Right;

    /// <summary>打つ手を決める（投げる手からの条件付き）。</summary>
    public static Handedness Bats(Draw d, Handedness throws, double typeScore, RosterCoefficients c)
    {
        if (double.IsNaN(d.Bats)) return Handedness.Switch;
        if (throws == Handedness.Left) return d.Bats < c.BatLeftGivenLeftThrow ? Handedness.Left : Handedness.Right;
        return d.Bats < LeftBatGivenRightThrowProbability(typeScore, c) ? Handedness.Left : Handedness.Right;
    }
}
