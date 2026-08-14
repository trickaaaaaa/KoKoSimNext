using System;
using KokoSim.Engine.Core;
using KokoSim.Engine.Match.Tactics;
using KokoSim.Engine.Players;

namespace KokoSim.Engine.Match.Game;

/// <summary>盗塁の結果。</summary>
public enum StealResult
{
    Safe,
    CaughtStealing,
}

/// <summary>盗塁の狙い塁（design-14 P1-4）。既定は二盗。三盗・本盗は末尾オプション引数のため
/// 既存呼び出し（target省略）の挙動・シグネチャに一切影響しない。</summary>
public enum StealTarget
{
    Second,
    Third,
    Home,
}

/// <summary>
/// 塁状況から「単独盗塁で狙える塁」を出す（issue #622）。
///
/// <para><b>判定式はここにしか置かない</b>。<see cref="GameEngine"/> の解決分岐も、采配カードの
/// 可否・ラベルもこの1か所を引く＝「押せたのに engine が解決しない指示」が構造的に作れない。</para>
///
/// <para>3つの条件は排他なので狙う塁は塁状況で一意に決まる（＝プレイヤーに選ばせる余地が無い）。
/// 一・二塁や満塁のように<b>どれにも当たらない塁状況では null</b>＝単独盗塁は走らない
/// （重盗は別経路・design-14 P1-4）。</para>
/// </summary>
public static class StealTargets
{
    /// <summary>狙える塁（null＝単独盗塁が成立しない塁状況）。</summary>
    public static StealTarget? FromBases(bool onFirst, bool onSecond, bool onThird)
    {
        // 二盗: 一塁に走者・二塁が空（一・三塁を含む＝三塁走者の有無は問わない）。
        if (onFirst && !onSecond) return StealTarget.Second;
        // 三盗: 二塁のみ。
        if (onSecond && !onFirst && !onThird) return StealTarget.Third;
        // 本盗: 三塁のみ。
        if (onThird && !onFirst && !onSecond) return StealTarget.Home;
        return null;
    }
}

/// <summary>
/// 盗塁の解決（設計書02 §4.2）。走塁と同じ「時間の勝負」で幾何・秒換算する。
/// 走者: スプリント速度(走力) ＋ スタート遅延(盗塁パラメータ)。ギャンブル始動は好スタート＝短縮（G3）。
/// 守備: 投手クイック ＋ 捕手ポップ(肩＝送球速度 ＋ 握り替え) ＋ タッチ。読み切り(ピッチアウト)で短縮＝優位（G3）。
/// けん制・投法補正は後続（設計書02 §2.2の投法は現フェーズ非適用）。
/// </summary>
public static class StealResolver
{
    /// <summary>走者所要時間[s]。ギャンブル始動(<see cref="StartType.Gamble"/>)は好ジャンプで短縮（G3）。
    /// 塁間距離は二盗・三盗・本盗のいずれも同一と近似（<see cref="BaserunningCoefficients.StealLeadDistanceM"/>）。</summary>
    public static double RunnerTimeSeconds(Player runner, BaserunningCoefficients c, StartType startType = StartType.Normal,
        StealTarget target = StealTarget.Second)
    {
        var startDelay = Math.Max(0.10, c.StealReactionIntercept - runner.Steal * c.StealReactionSlope);
        if (startType == StartType.Gamble) startDelay = Math.Max(0.05, startDelay - c.GambleJumpBonusSeconds);
        return c.StealLeadDistanceM / runner.ToFielder().SprintSpeedMps + startDelay;
    }

    /// <summary>守備所要時間[s]（投手クイック＋捕手ポップ＋タッチ）。ピッチアウト成立で捕手優位＝短縮（G3）。
    /// ギャンブル走者は無防備でさらに短縮（読まれると最も刺されやすい）。本盗は捕手からの送球ではなく
    /// 投球そのものが送球を兼ねるため送球区間を持たない（<see cref="BaserunningCoefficients.HomeTagSeconds"/>のみ）。</summary>
    public static double DefenseTimeSeconds(
        Player catcher, BaserunningCoefficients c, bool pitchout = false, StartType startType = StartType.Normal,
        StealTarget target = StealTarget.Second)
    {
        double defense;
        if (target == StealTarget.Home)
        {
            defense = c.PitcherQuickSeconds + c.HomeTagSeconds;
        }
        else
        {
            var throwDistance = target == StealTarget.Third ? c.CatchThrowToThirdDistanceM : c.CatchThrowDistanceM;
            var catcherFielder = catcher.ToFielder();
            var throwTime = throwDistance / catcherFielder.ThrowSpeedMps;
            // 捕手の握り替え（ポップ）を守備力で伸縮（Issue #36）。守備50で×1.0＝現行と恒等。
            var popTransfer = c.PopTransferSeconds * catcherFielder.TransferFactor(c.TransferFieldingSlope, c.TransferFactorMin);
            var popTime = popTransfer + throwTime;
            defense = c.PitcherQuickSeconds + popTime + c.TagSeconds;
        }
        if (pitchout)
        {
            defense -= c.PitchoutDefenseBonusSeconds;
            if (startType == StartType.Gamble) defense -= c.GamblePitchoutExtraBonusSeconds;
        }
        return defense;
    }

    /// <summary>盗塁成功確率（走塁と同式のlogistic）。pitchout=読み切り, startType=始動種別（G3）。
    /// 三盗・本盗は<see cref="BaserunningCoefficients.StealThirdSuccessBias"/>/<see cref="BaserunningCoefficients.StealHomeSuccessBias"/>
    /// で成功率を下方補正する（design-14「三盗は成功率下方、本盗はさらに下方」）。</summary>
    public static double SuccessProbability(
        Player runner, Player catcher, BaserunningCoefficients c,
        bool pitchout = false, StartType startType = StartType.Normal, StealTarget target = StealTarget.Second)
    {
        var bias = target switch
        {
            StealTarget.Third => c.StealSuccessBias + c.StealThirdSuccessBias,
            StealTarget.Home => c.StealSuccessBias + c.StealHomeSuccessBias,
            _ => c.StealSuccessBias,
        };
        var margin = DefenseTimeSeconds(catcher, c, pitchout, startType, target)
                     - RunnerTimeSeconds(runner, c, startType, target) + bias;
        return MathUtil.Clamp(MathUtil.Logistic(margin / c.StealMarginScale), 0.01, 0.99);
    }

    public static StealResult Resolve(
        Player runner, Player catcher, BaserunningCoefficients c, IRandomSource rng,
        bool pitchout = false, StartType startType = StartType.Normal, StealTarget target = StealTarget.Second)
        => MathUtil.Chance(SuccessProbability(runner, catcher, c, pitchout, startType, target), rng)
            ? StealResult.Safe
            : StealResult.CaughtStealing;
}
