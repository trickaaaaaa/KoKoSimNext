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

/// <summary>盗塁1回分の判定と、<b>その判定が使った秒</b>（＝そのまま絵になる, issue verdict-margin-into-physical-layer）。</summary>
public readonly record struct StealVerdict(StealResult Result, double RunnerSeconds, double DefenseSeconds)
{
    public bool Safe => Result == StealResult.Safe;
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
/// 守備: 投手クイック ＋ 捕手ポップ(肩＝送球速度 ＋ 立ち上がり ＋ 握り替え) ＋ タッチ。
/// 読み切り(ピッチアウト)で短縮＝優位（G3）。けん制・投法補正は後続（設計書02 §2.2の投法は現フェーズ非適用）。
///
/// <para><b>勝敗はこの2つの秒の比較そのもの</b>（2026-08-14）。運は「ゆらぎ[s]」として引き、
/// 走者の秒・守備の秒へ振り分ける＝<see cref="ResolveVerdict"/> が返す秒がそのまま絵になるので、
/// 「送球が先に届いた絵なのにセーフ」が構造的に起きない。ゆらぎの分布は
/// logistic(0, <see cref="BaserunningCoefficients.StealMarginScale"/>) で、
/// <see cref="SuccessProbability"/> はその解析解（＝従来の式のまま。采配AIの読みも不変）。</para>
/// </summary>
public static class StealResolver
{
    /// <summary>ゆらぎのうち走者側（ジャンプの良し悪し）／守備側（握り替え・送球の乱れ）が引き受ける上限[s]。
    ///
    /// <para>表示専用（合計は変えないので判定に影響しない）。走者が勝ちすぎた分は<b>捕手のもたつき</b>へ、
    /// 守備が勝ちすぎた分は<b>走者の出遅れ</b>へ回す＝裾が長いのは「送球が乱れる」「スタートで躓く」側で、
    /// 逆側（人間より速く走る・肩より速い送球）は物理的に有り得ないため上限を置く。</para></summary>
    private const double LuckSplitCapSeconds = 0.20;

    /// <summary>確定結果と絵が食い違わないための最小の勝ち幅[s]（表示専用）。
    ///
    /// <para><see cref="SuccessProbability"/> は 1%/99% でクランプされるので、そこが効く極端な場面
    /// （実質は本盗だけ）に限り、引いたゆらぎが結果と食い違う。その時だけ結果に合う最小の秒へ寄せる。
    /// 通常の場面では一切効かない＝従来の「表示側ガード」はここに畳み込まれて消えた。</para></summary>
    private const double MinMarginSeconds = 0.05;

    /// <summary>走者所要時間[s]。ギャンブル始動(<see cref="StartType.Gamble"/>)は好ジャンプで短縮（G3）。
    /// 塁間距離は二盗・三盗・本盗のいずれも同一と近似（<see cref="BaserunningCoefficients.StealLeadDistanceM"/>）。</summary>
    public static double RunnerTimeSeconds(Player runner, BaserunningCoefficients c, StartType startType = StartType.Normal,
        StealTarget target = StealTarget.Second)
    {
        var startDelay = Math.Max(0.10, c.StealReactionIntercept - c.AbilityEffect.Running(runner.Steal) * c.StealReactionSlope);
        if (startType == StartType.Gamble) startDelay = Math.Max(0.05, startDelay - c.GambleJumpBonusSeconds);
        // ジャンプ（投手の始動を読んで既に動き出している分）。床の**後**に引く＝全走者から同じ秒だけ
        // 引かれる（理由は StealJumpCreditSeconds のコメント）。
        startDelay -= c.StealJumpCreditSeconds;
        return c.StealLeadDistanceM / runner.ToFielder(c.AbilityEffect).SprintSpeedMps + startDelay;
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
            var catcherFielder = catcher.ToFielder(c.AbilityEffect);
            var throwTime = throwDistance / catcherFielder.ThrowSpeedMps;
            // 捕手の握り替え（ポップ）を守備力で伸縮（Issue #36）。守備50で×1.0＝現行と恒等。
            var popTransfer = c.PopTransferSeconds * catcherFielder.TransferFactor(c.TransferFieldingSlope, c.TransferFactorMin);
            // 立ち上がり（守備力で縮まない固定部分）＋握り替え（守備力で伸縮）＋送球。
            var popTime = c.CatcherPopRiseSeconds + popTransfer + throwTime;
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
        => ResolveVerdict(runner, catcher, c, rng, pitchout, startType, target).Result;

    /// <summary>
    /// 盗塁1回を解決し、<b>判定に使った秒</b>（ゆらぎ込み）も返す。絵はこの秒をそのまま描く。
    ///
    /// <para>乱数の消費は<b>1つだけ</b>で、その1つが「ゆらぎ[s]」と勝敗の両方を決める
    /// （逆関数法: u→logistic ゆらぎ。<c>safe ⟺ u &lt; SuccessProbability</c> は従来の
    /// <c>MathUtil.Chance</c> と同一なので、結果は1ビットも変わらない＝帯・決定論ダイジェスト不変）。</para>
    /// </summary>
    public static StealVerdict ResolveVerdict(
        Player runner, Player catcher, BaserunningCoefficients c, IRandomSource rng,
        bool pitchout = false, StartType startType = StartType.Normal, StealTarget target = StealTarget.Second)
    {
        var p = SuccessProbability(runner, catcher, c, pitchout, startType, target);
        var u = rng.NextDouble();   // ⚠ 1回だけ引く（従来 MathUtil.Chance と同じ消費）
        return Times(
            RunnerTimeSeconds(runner, c, startType, target),
            DefenseTimeSeconds(catcher, c, pitchout, startType, target),
            LuckSeconds(u, c), u < p);
    }

    /// <summary>
    /// 抽選せずに結果を与えて秒を組む（強制発動＝デバッグ注入と、判定を持たない絵づくり用）。
    /// ゆらぎ0から始め、与えられた結果と食い違うときだけ最小の秒へ寄せる。
    /// </summary>
    public static StealVerdict Forced(
        Player runner, Player catcher, BaserunningCoefficients c, StealResult result,
        bool pitchout = false, StartType startType = StartType.Normal, StealTarget target = StealTarget.Second)
        => Times(
            RunnerTimeSeconds(runner, c, startType, target),
            DefenseTimeSeconds(catcher, c, pitchout, startType, target),
            0.0, result == StealResult.Safe);

    /// <summary>ゆらぎ[s]（+で走者有利）。logistic(0, StealMarginScale) の逆関数法。</summary>
    private static double LuckSeconds(double u, BaserunningCoefficients c)
    {
        var uu = MathUtil.Clamp(u, 1e-12, 1.0 - 1e-12);
        return c.StealMarginScale * Math.Log((1.0 - uu) / uu);
    }

    /// <summary>ゆらぎを2人へ振り分けて、勝敗と一致する走者/守備の秒を作る。</summary>
    private static StealVerdict Times(double runnerTime, double defenseTime, double luck, bool safe)
    {
        var margin = defenseTime - runnerTime + luck;   // >0 なら走者が先着＝セーフ
        // 確率のクランプ(1%/99%)が効く極端な場面だけ、引いたゆらぎが結果と食い違う。結果に合う
        // 最小の秒へ寄せる（通常は不発）。
        if (safe && margin <= 0.0) luck += MinMarginSeconds - margin;
        else if (!safe && margin >= 0.0) luck -= margin + MinMarginSeconds;

        double toRunner, toDefense;
        if (luck >= 0.0)
        {
            // 走者有利のゆらぎ: まず好スタート、それを超える分は捕手のもたつき（送球の乱れ）。
            toRunner = Math.Min(luck, LuckSplitCapSeconds);
            toDefense = luck - toRunner;
        }
        else
        {
            // 守備有利のゆらぎ: まず速いポップ、それを超える分は走者の出遅れ（躓き）。
            toDefense = Math.Max(luck, -LuckSplitCapSeconds);
            toRunner = luck - toDefense;
        }
        return new StealVerdict(
            safe ? StealResult.Safe : StealResult.CaughtStealing,
            runnerTime - toRunner, defenseTime + toDefense);
    }
}
