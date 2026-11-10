using System;
using System.Collections.Generic;
using KokoSim.Engine.Players;

namespace KokoSim.Engine.Match.Game;

/// <summary>チームの勢い（issue #863）の係数。試合内の効き目はプレッシャー・伝令の落ち着きと同じ経路
/// （実効ミート・制球への倍率。不変条件#1＝確率へ直接足さない）。</summary>
public sealed record MomentumCoefficients
{
    /// <summary>勢いの段階上限（0〜MaxLevel）。</summary>
    public int MaxLevel { get; init; } = 3;

    /// <summary>格上撃破: 相手とのティア差が1段のときの加点。</summary>
    public int UpsetTierGap1Bonus { get; init; } = 1;

    /// <summary>格上撃破: 相手とのティア差が2段以上のときの加点（1段ぶんとの差分ではなくこの値そのものを使う）。</summary>
    public int UpsetTierGap2Bonus { get; init; } = 2;

    /// <summary>サヨナラ勝ちの加点。</summary>
    public int WalkoffBonus { get; init; } = 1;

    /// <summary>終盤の逆転勝ちの加点。</summary>
    public int ComebackBonus { get; init; } = 1;

    /// <summary>逆転判定の「終盤」開始イニング（この回以降に劣勢があったかを見る）。</summary>
    public int ComebackFromInning { get; init; } = 7;

    /// <summary>逆転判定に使う最小点差（この点差以上のビハインドが終盤にあれば対象）。</summary>
    public int ComebackMinDeficit { get; init; } = 3;

    /// <summary>倍率 = 1 + 段階×この値（打者はミート・投手は制球）。最大段階(既定3)で+6%。</summary>
    public double MultiplierPerLevel { get; init; } = 0.02;
}

/// <summary>
/// チームの勢い（issue #863・design-05想定節）: 格上撃破・サヨナラ勝ち・終盤の逆転勝ちで上がり、
/// 普通の勝ちでは維持（減衰させない）。大会の敗退・大会の終了で0に戻す判断は「大会の進行状態」を
/// 知っている呼び出し側（Unity GameSession）の仕事＝このクラスは1試合ぶんの増分と、試合内の
/// 実効能力への適用だけを担う純関数（乱数不使用・不変条件#2）。
/// </summary>
public static class MomentumModel
{
    /// <summary>1試合ぶんの勢い増分（勝利時のみ。敗戦・引き分けは0＝呼び出し側で現状維持）。</summary>
    public static int WinBonus(
        GameResult result, bool managerIsAway, double selfStrength, double opponentStrength, MomentumCoefficients c)
    {
        var won = managerIsAway ? !result.HomeWon && !result.Tied : result.HomeWon;
        if (!won) return 0;

        var bonus = 0;
        var gap = Career.FameUpsetModel.TierGap(selfStrength, opponentStrength);
        if (gap >= 2) bonus += c.UpsetTierGap2Bonus;
        else if (gap == 1) bonus += c.UpsetTierGap1Bonus;

        if (result.WalkoffWin) bonus += c.WalkoffBonus;

        if (LateComeback(result.AwayLineScore, result.HomeLineScore, managerIsAway, c.ComebackFromInning, c.ComebackMinDeficit))
            bonus += c.ComebackBonus;

        return bonus;
    }

    /// <summary>
    /// 勝利試合後の新しい勢い段階（現状＋増分を0〜MaxLevelへ丸める）。増分0（トリガーの無い普通の勝ち）なら
    /// 現状を維持＝減衰しない。敗戦・引き分けも現状維持（大会の敗退・終了での0リセットは呼び出し側が行う）。
    /// </summary>
    public static int NextLevel(
        int currentLevel, GameResult result, bool managerIsAway,
        double selfStrength, double opponentStrength, MomentumCoefficients c)
    {
        var bonus = WinBonus(result, managerIsAway, selfStrength, opponentStrength, c);
        if (bonus <= 0) return currentLevel;
        return Math.Min(c.MaxLevel, currentLevel + bonus);
    }

    /// <summary>
    /// 終盤（<paramref name="fromInning"/>回以降）に自軍が<paramref name="minDeficit"/>点差以上ビハインド
    /// だった局面があったか。各半イニング終了時点の累積得点だけで判定する純関数（イニング別得点配列から
    /// 導出・乱数不使用）。最終的に勝ったかどうかは呼び出し側（<see cref="WinBonus"/>）が別途見る。
    /// </summary>
    internal static bool LateComeback(
        IReadOnlyList<int> awayLine, IReadOnlyList<int> homeLine, bool managerIsAway, int fromInning, int minDeficit)
    {
        var awayCum = 0;
        var homeCum = 0;
        var n = Math.Max(awayLine.Count, homeLine.Count);
        for (var i = 0; i < n; i++)
        {
            var inning = i + 1;
            if (i < awayLine.Count) awayCum += awayLine[i];
            if (inning >= fromInning && Deficit(awayCum, homeCum, managerIsAway) >= minDeficit) return true;
            if (i < homeLine.Count) homeCum += homeLine[i];
            if (inning >= fromInning && Deficit(awayCum, homeCum, managerIsAway) >= minDeficit) return true;
        }
        return false;
    }

    private static int Deficit(int awayCum, int homeCum, bool managerIsAway)
        => managerIsAway ? homeCum - awayCum : awayCum - homeCum;

    /// <summary>実効ミート・制球への倍率（段階0以下なら1.0＝恒等）。</summary>
    public static double Multiplier(int level, MomentumCoefficients c)
        => 1.0 + Math.Max(0, level) * c.MultiplierPerLevel;

    /// <summary>打者へ適用（<see cref="PressureModel.ApplyBatter"/> と同じ経路）。倍率1.0なら恒等。</summary>
    public static BatterAttributes ApplyBatter(BatterAttributes b, int level, MomentumCoefficients c)
    {
        var m = Multiplier(level, c);
        if (m == 1.0) return b;
        return b with { Contact = ClampAbility(b.Contact * m) };
    }

    /// <summary>投手へ適用（<see cref="PressureModel.ApplyPitcher"/> と同じ経路）。倍率1.0なら恒等。</summary>
    public static PitcherAttributes ApplyPitcher(PitcherAttributes p, int level, MomentumCoefficients c)
    {
        var m = Multiplier(level, c);
        if (m == 1.0) return p;
        return p with { Control = ClampAbility(p.Control * m) };
    }

    private static int ClampAbility(double v) => Math.Clamp((int)Math.Round(v), 1, 100);
}
