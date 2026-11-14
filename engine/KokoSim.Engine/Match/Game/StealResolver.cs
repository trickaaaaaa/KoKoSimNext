using System;
using KokoSim.Engine.Core;
using KokoSim.Engine.Match.Fielding;
using KokoSim.Engine.Match.Tactics;
using KokoSim.Engine.Players;

namespace KokoSim.Engine.Match.Game;

/// <summary>盗塁の結果。</summary>
public enum StealResult
{
    Safe,
    CaughtStealing,
}

/// <summary>盗塁の狙い塁（design-14 P1-4）。既定は二盗。三盗は末尾オプション引数のため
/// 既存呼び出し（target省略）の挙動・シグネチャに一切影響しない。
/// <see cref="Home"/> は<b>一・三塁の重盗で本塁を突く三塁走者の到達時間</b>（<see cref="StealResolver.RunnerTimeSeconds"/>）
/// にだけ使う。単独の本盗は採用しない（issue #788・2026-11-08 監督決定）＝判定・絵・采配には出てこない。</summary>
public enum StealTarget
{
    Second,
    Third,
    Home,
}

/// <summary>盗塁1回分の判定と、<b>その判定が使った秒</b>（＝そのまま絵になる, issue verdict-margin-into-physical-layer）。</summary>
/// <param name="ThrowScatter">捕手の送球が実際に逸れた量（issue #859）。判定の遅れ・悪送球に使った値そのもの。
/// 抽選しない経路（<see cref="StealResolver.Forced"/>）は null。</param>
public readonly record struct StealVerdict(StealResult Result, double RunnerSeconds, double DefenseSeconds,
    KokoSim.Engine.Match.Fielding.ThrowScatterModel.Scatter? ThrowScatter = null)
{
    public bool Safe => Result == StealResult.Safe;
}

/// <summary>
/// 塁状況から「単独盗塁で狙える塁」を出す（issue #622）。
///
/// <para><b>判定式はここにしか置かない</b>。<see cref="GameEngine"/> の解決分岐も、采配カードの
/// 可否・ラベルもこの1か所を引く＝「押せたのに engine が解決しない指示」が構造的に作れない。</para>
///
/// <para>単独の本盗（三塁のみ在塁）は<b>採用しない</b>（issue #788・2026-11-08 監督決定）。野球人生で1回あるか
/// ないかのプレーで、成功率も下限近く（走力50≈1.6%）＝押すとアウトになるだけのボタンだった。三塁のみ在塁は null。
/// 一・三塁の重盗で三塁走者が本塁を突くプレーは二盗に付随する作戦なので残している（GameEngine の二盗分岐）。</para>
///
/// <para>2つの条件は排他なので狙う塁は塁状況で一意に決まる（＝プレイヤーに選ばせる余地が無い）。
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
        // 三塁のみ（単独の本盗）は採用しない（issue #788）。
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
    /// （極端な能力差）に限り、引いたゆらぎが結果と食い違う。その時だけ結果に合う最小の秒へ寄せる。
    /// 通常の場面では一切効かない＝従来の「表示側ガード」はここに畳み込まれて消えた。</para></summary>
    private const double MinMarginSeconds = 0.05;

    /// <summary>走者所要時間[s]。ギャンブル始動(<see cref="StartType.Gamble"/>)は好ジャンプで短縮（G3）。
    /// 塁間距離は二盗・三盗を同一と近似（<see cref="BaserunningCoefficients.StealLeadDistanceM"/>）。<see cref="StealTarget.Home"/>
    /// （一・三塁の重盗で本塁を突く三塁走者）は三塁からの距離（<see cref="BaserunningCoefficients.StealHomeDistanceM"/>・issue #637）。</summary>
    public static double RunnerTimeSeconds(Player runner, BaserunningCoefficients c, StartType startType = StartType.Normal,
        StealTarget target = StealTarget.Second)
    {
        var startDelay = Math.Max(0.10, c.StealReactionIntercept - c.AbilityEffect.Running(runner.Steal) * c.StealReactionSlope);
        if (startType == StartType.Gamble) startDelay = Math.Max(0.05, startDelay - c.GambleJumpBonusSeconds);
        // ジャンプ（投手の始動を読んで既に動き出している分）。床の**後**に引く＝全走者から同じ秒だけ
        // 引かれる（理由は StealJumpCreditSeconds のコメント）。
        startDelay -= c.StealJumpCreditSeconds;
        var distance = target == StealTarget.Home ? c.StealHomeDistanceM : c.StealLeadDistanceM;
        return distance / runner.ToFielder(c.AbilityEffect).SprintSpeedMps + startDelay;
    }

    /// <summary>守備所要時間[s]（投手クイック＋捕手ポップ＋タッチ＋送球の逸れの期待遅れ）。ピッチアウト成立で
    /// 捕手優位＝短縮（G3）。ギャンブル走者は無防備でさらに短縮（読まれると最も刺されやすい）。
    /// 単独の本盗は採用しない（issue #788）。
    ///
    /// <para>送球の逸れ（issue #841）は<b>期待値のうち、送球精度50・受け手Catching50（リーグ平均）からの
    /// 差分だけ</b>を加える（<see cref="CenteredExpectedScatterDelaySeconds"/>）＝AIの読み（<see cref="SuccessProbability"/>）
    /// と絵の基準線（<see cref="Forced"/>）が使う値。平均的な捕手・受け手では加算がちょうど0＝帯不変
    /// （不変条件#5、TransferFactor等と同じ「50が恒等」の作法）。実際の1本ごとの判定
    /// （<see cref="ResolveVerdict"/>）は、ここに<b>サンプリングした逸れの実際の期待値からの差分</b>を
    /// 別途乗せる＝平均はこの関数の値のまま、本番だけ本物のばらつきを持つ。</para></summary>
    /// <param name="pitcherQuickOffsetSeconds">投手ごとのクイックの差[s]（クイック○×・issue #907。
    /// <see cref="SkillModel.QuickDeliveryOffsetSeconds"/>）。既定0＝従来と同じ。</param>
    public static double DefenseTimeSeconds(
        Player catcher, BaserunningCoefficients c, bool pitchout = false, StartType startType = StartType.Normal,
        StealTarget target = StealTarget.Second, Player? receiver = null, double pitcherQuickOffsetSeconds = 0.0)
    {
        if (target == StealTarget.Home)
            throw new ArgumentException("単独の本盗は採用しない（issue #788）。", nameof(target));
        double defense;
        {
            var throwDistance = target == StealTarget.Third ? c.CatchThrowToThirdDistanceM : c.CatchThrowDistanceM;
            var catcherFielder = catcher.ToFielder(c.AbilityEffect);
            var throwTime = throwDistance / catcherFielder.ThrowSpeedMps;
            // 捕手の握り替え（ポップ）を守備力で伸縮（Issue #36）。守備50で×1.0＝現行と恒等。
            var popTransfer = c.PopTransferSeconds * catcherFielder.TransferFactor(c.TransferFieldingSlope, c.TransferFactorMin);
            // 立ち上がり（守備力で縮まない固定部分）＋握り替え（守備力で伸縮）＋送球。
            var popTime = c.CatcherPopRiseSeconds + popTransfer + throwTime;
            var receiverCatching = receiver?.ToFielder(c.AbilityEffect).Catching ?? 50;
            defense = c.PitcherQuickSeconds + pitcherQuickOffsetSeconds + popTime + c.TagSeconds
                      + CenteredExpectedScatterDelaySeconds(catcherFielder.ThrowScatterSigmaCm, receiverCatching, c);
        }
        if (pitchout)
        {
            defense -= c.PitchoutDefenseBonusSeconds;
            if (startType == StartType.Gamble) defense -= c.GamblePitchoutExtraBonusSeconds;
        }
        return defense;
    }

    /// <summary>リーグ平均（送球精度50→σ22cm, <see cref="FielderAttributes.LeagueAverage"/>）での
    /// 逸れの期待遅れ[s]。これ自体は正値だが、<see cref="CenteredExpectedScatterDelaySeconds"/> が
    /// 差分をとって打ち消す＝平均的な捕手・受け手ではタッチ秒が変わらない（帯不変）。</summary>
    private static double LeagueAverageExpectedScatterDelaySeconds(BaserunningCoefficients c)
        => RawExpectedScatterDelaySeconds(FielderAttributes.LeagueAverage.ThrowScatterSigmaCm, receiverCatching: 50, c);

    /// <summary>受け手のCatchingで縮む、送球の逸れの期待遅れ[s]（サンプリングなしの解析解）。</summary>
    private static double RawExpectedScatterDelaySeconds(double sigmaCm, int receiverCatching, BaserunningCoefficients c)
        => ThrowScatterModel.ExpectedRecoverySeconds(
            sigmaCm, receiverCatching,
            c.StealThrowScatterLateralSecPerCm, c.StealThrowScatterHighSecPerCm, c.StealThrowScatterShortHopSecPerCm,
            c.StealThrowScatterCatchingReliefSlope, c.StealThrowScatterCatchingReliefMinFactor);

    /// <summary>リーグ平均（送球精度50・受け手Catching50）からの差分だけの期待遅れ[s]（issue #841）。
    /// <see cref="DefenseTimeSeconds"/>・<see cref="SuccessProbability"/>・<see cref="Forced"/> が使う
    /// ＝平均的な能力では加算が0＝帯不変（不変条件#5）。送球精度が高い／受け手の捕球が高いほど負（短縮）、
    /// 低いほど正（延長）になる。</summary>
    private static double CenteredExpectedScatterDelaySeconds(double sigmaCm, int receiverCatching, BaserunningCoefficients c)
        => RawExpectedScatterDelaySeconds(sigmaCm, receiverCatching, c) - LeagueAverageExpectedScatterDelaySeconds(c);

    /// <summary>盗塁成功確率（走塁と同式のlogistic）。pitchout=読み切り, startType=始動種別（G3）。
    /// 三盗は<see cref="BaserunningCoefficients.StealThirdSuccessBias"/> で成功率を下方補正する（design-14「三盗は成功率下方」）。
    /// <paramref name="receiver"/>（二塁カバー＝遊撃手／三塁カバー＝三塁手）の送球精度・捕球の効きを
    /// 判定と揃える（issue #841「判定と読みを一致させる」）。</summary>
    public static double SuccessProbability(
        Player runner, Player catcher, BaserunningCoefficients c,
        bool pitchout = false, StartType startType = StartType.Normal, StealTarget target = StealTarget.Second,
        Player? receiver = null, double pitcherQuickOffsetSeconds = 0.0)
    {
        var bias = target == StealTarget.Third ? c.StealSuccessBias + c.StealThirdSuccessBias : c.StealSuccessBias;
        var margin = DefenseTimeSeconds(catcher, c, pitchout, startType, target, receiver, pitcherQuickOffsetSeconds)
                     - RunnerTimeSeconds(runner, c, startType, target) + bias;
        return MathUtil.Clamp(MathUtil.Logistic(margin / c.StealMarginScale), 0.01, 0.99);
    }

    /// <summary>
    /// 守備に読まれる（ピッチアウト）確率まで込みの成功見込み（issue #838・采配AIの損益判断用）。
    /// <see cref="StealReadModel.PitchoutProbability"/> で読まれる確率 q を出し、
    /// (1−q)×読まれない場合 ＋ q×読まれた場合 の期待値を返す＝実際の判定（GameEngine が RollPitchout→
    /// <see cref="ResolveVerdict"/> の順に解く）の平均と一致する。
    /// </summary>
    public static double ExpectedSuccessProbability(
        Player runner, Player catcher, Player pitcher, BaserunningCoefficients c,
        StartType startType = StartType.Normal, StealTarget target = StealTarget.Second, Player? receiver = null,
        double pitcherQuickOffsetSeconds = 0.0)
    {
        var q = StealReadModel.PitchoutProbability(
            catcher, pitcher, StealReadModel.Expectedness(runner, startType, c), c);
        var plain = SuccessProbability(runner, catcher, c, pitchout: false, startType, target, receiver, pitcherQuickOffsetSeconds);
        var read = SuccessProbability(runner, catcher, c, pitchout: true, startType, target, receiver, pitcherQuickOffsetSeconds);
        return (1.0 - q) * plain + q * read;
    }

    public static StealResult Resolve(
        Player runner, Player catcher, BaserunningCoefficients c, IRandomSource rng,
        bool pitchout = false, StartType startType = StartType.Normal, StealTarget target = StealTarget.Second,
        Player? receiver = null, double pitcherQuickOffsetSeconds = 0.0)
        => ResolveVerdict(runner, catcher, c, rng, pitchout, startType, target, receiver, pitcherQuickOffsetSeconds).Result;

    /// <summary>
    /// 盗塁1回を解決し、<b>判定に使った秒</b>（ゆらぎ込み）も返す。絵はこの秒をそのまま描く。
    ///
    /// <para>乱数の消費: 送球の逸れ（<see cref="ThrowScatterModel.Sample"/>、横・縦2軸）で2つ（Box-Muller
    /// のキャッシュにより実質1組の一様乱数）＋ロジスティックの「ゆらぎ」用に1つ（issue #841で1→実質2系統に拡張。
    /// 従来の「ちょうど1つ」という前提は崩れるため決定論ベースラインは再生成が必要）。
    /// ゆらぎ用の1つは <c>u→logistic ゆらぎ</c> の逆関数法で、<c>safe ⟺ u &lt; p</c>（p は送球の逸れ込みの
    /// margin から出す）。逸れが悪送球閾値以上（<see cref="ThrowScatterModel.IsWild"/>）なら、送球が抜けて
    /// 守備側に正規のプレーが無い＝無条件セーフにする（ロジスティックの1%キャップでは起きえない「抜けたのに
    /// アウト」を構造的に塞ぐ）。</para>
    /// </summary>
    public static StealVerdict ResolveVerdict(
        Player runner, Player catcher, BaserunningCoefficients c, IRandomSource rng,
        bool pitchout = false, StartType startType = StartType.Normal, StealTarget target = StealTarget.Second,
        Player? receiver = null, double pitcherQuickOffsetSeconds = 0.0)
    {
        var catcherFielder = catcher.ToFielder(c.AbilityEffect);
        var sigmaCm = catcherFielder.ThrowScatterSigmaCm;
        var receiverCatching = receiver?.ToFielder(c.AbilityEffect).Catching ?? 50;

        // 送球1本ぶんの実際の逸れ（issue #841）。「この捕手・この受け手自身の期待値」からの差分だけを
        // 乗せる＝DefenseTimeSeconds（リーグ平均からの差分込みの期待値）はそのまま保ち、個々の本だけ
        // 本物のばらつきが出る（ゼロ平均のノイズ。AIの読み=平均と実際の判定の平均が一致する）。
        var scatter = ThrowScatterModel.Sample(sigmaCm, rng);
        var sampledDelay = ThrowScatterModel.RecoverySeconds(
            scatter, receiverCatching,
            c.StealThrowScatterLateralSecPerCm, c.StealThrowScatterHighSecPerCm, c.StealThrowScatterShortHopSecPerCm,
            c.StealThrowScatterCatchingReliefSlope, c.StealThrowScatterCatchingReliefMinFactor);
        var ownExpectedDelay = RawExpectedScatterDelaySeconds(sigmaCm, receiverCatching, c);
        var defenseSeconds = DefenseTimeSeconds(catcher, c, pitchout, startType, target, receiver, pitcherQuickOffsetSeconds)
                              + (sampledDelay - ownExpectedDelay);
        var wild = ThrowScatterModel.IsWild(scatter, c.StealThrowWildThresholdCm);

        var runnerSeconds = RunnerTimeSeconds(runner, c, startType, target);
        var bias = target == StealTarget.Third ? c.StealSuccessBias + c.StealThirdSuccessBias : c.StealSuccessBias;
        var p = MathUtil.Clamp(MathUtil.Logistic((defenseSeconds - runnerSeconds + bias) / c.StealMarginScale), 0.01, 0.99);

        var u = rng.NextDouble();   // ゆらぎ＋勝敗（逆関数法）
        var safe = wild || u < p;
        return Times(runnerSeconds, defenseSeconds, LuckSeconds(u, c), safe) with { ThrowScatter = scatter };
    }

    /// <summary>
    /// 抽選せずに結果を与えて秒を組む（強制発動＝デバッグ注入と、判定を持たない絵づくり用）。
    /// ゆらぎ0から始め、与えられた結果と食い違うときだけ最小の秒へ寄せる。送球の逸れは期待値のみ
    /// （<see cref="DefenseTimeSeconds"/>）＝サンプリングしない（抽選しない呼び出しのため）。
    /// </summary>
    public static StealVerdict Forced(
        Player runner, Player catcher, BaserunningCoefficients c, StealResult result,
        bool pitchout = false, StartType startType = StartType.Normal, StealTarget target = StealTarget.Second,
        Player? receiver = null, double pitcherQuickOffsetSeconds = 0.0)
        => Times(
            RunnerTimeSeconds(runner, c, startType, target),
            DefenseTimeSeconds(catcher, c, pitchout, startType, target, receiver, pitcherQuickOffsetSeconds),
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
