using System;
using KokoSim.Engine.Core;
using KokoSim.Engine.Players;

namespace KokoSim.Engine.Match.Game;

/// <summary>フライを見た走者の始動（issue #1130・設計書12 §4.10）。</summary>
public enum FlyRunnerStart
{
    /// <summary>打った瞬間に走る（落ちると読んだ・2アウト・エンドランや盗塁で既に走っていた）。</summary>
    Go,
    /// <summary>塁間の途中（ハーフウェー）で待ち、落ちたら走る・捕られたら戻る。</summary>
    Halfway,
    /// <summary>塁に付いて待つ（三塁走者・浅いフライ・タッチアップを狙う深いフライ）。</summary>
    Hold,
}

/// <summary>
/// 走者が読む打球の材料（1打球ぶん・物理層）。落下点（捕球点）・落ちる（捕られる）時刻・捕球の余裕
/// （<see cref="Fielding.FieldingPlay.CatchMarginSeconds"/>。正＝捕れる、負＝落ちる）。
/// </summary>
public readonly record struct FlyReadSituation(Vector3D LandingPoint, double LandAtSeconds, double CatchMarginSeconds);

/// <summary>走者1人の読みの結果。<paramref name="HoldLeadM"/>＝待つ位置（元の塁からの距離[m]。Hold は 0）。</summary>
public readonly record struct FlyRunnerRead(FlyRunnerStart Start, double HoldLeadM);

/// <summary>
/// フライの読み（issue #1130・設計書12 §4.10）。走者は打球が落ちるか捕られるかを打った瞬間には知らない。
/// 2アウト未満のフライでは、際どさ（捕球の余裕＝物理層）を見て「打った瞬間に走る」か「待つ」かを決める。
/// 読みの鋭さは走塁能力（<see cref="AbilityEffectCoefficients.Running"/> 経由）で小さく補正する（不変条件#1）。
///
/// <para>待った走者は、安打なら落ちた時刻から塁間の途中で走り出す（＝追加スタート遅延。走者の時計は直線なので、
/// 途中で止まって待つのと出発を遅らせるのとで到達時刻は同じ）。捕られたら、ハーフウェーから元の塁へ戻る勝負になる。
/// 走ると読んだ走者が捕られたら、エンドランと同じ飛び出しの勝負（<see cref="DoubledOffResolver"/>）に乗る
/// ＝同じ打球で「読み」と「ライナー併殺」を別々に引かない。</para>
/// </summary>
public static class FlyReadModel
{
    /// <summary>走者の読みの幅[s]。走塁能力が高いほど狭い（際どい打球でだけ読み違える）。</summary>
    public static double ScaleSeconds(Player runner, BaserunningCoefficients c)
    {
        var running = c.AbilityEffect.Running(runner.Baserunning);
        var factor = Math.Max(c.FlyReadScaleMinFactor, 1.0 - c.FlyReadAbilitySlope * (running - 50) / 50.0);
        return c.FlyReadScaleSeconds * factor;
    }

    /// <summary>打った瞬間に走る確率。捕球の余裕が小さい（落ちそう）ほど高い。乱数は引かない。</summary>
    public static double GoProbability(double catchMarginSeconds, Player runner, BaserunningCoefficients c)
        => MathUtil.Logistic(-(catchMarginSeconds - c.FlyReadGoBiasSeconds) / ScaleSeconds(runner, c));

    /// <summary>
    /// 打った瞬間に走るか。2アウトは常に走る（乱数を引かない）。明らかに落ちる／捕れる打球
    /// （確率が <see cref="BaserunningCoefficients.FlyReadCertainProbability"/> の外）も乱数を引かずに決める。
    /// 際どい打球でだけ乱数を1つ引く。
    /// </summary>
    public static bool ReadGo(double catchMarginSeconds, Player runner, int outsAtContact,
        BaserunningCoefficients c, IRandomSource rng)
    {
        if (outsAtContact >= 2) return true;
        var p = GoProbability(catchMarginSeconds, runner, c);
        if (p >= 1.0 - c.FlyReadCertainProbability) return true;
        if (p <= c.FlyReadCertainProbability) return false;
        return MathUtil.Chance(p, rng);
    }

    /// <summary>待った走者が走り出す時刻[s]（接触基準）＝落ちた時刻＋反応。</summary>
    public static double ResumeAtSeconds(in FlyReadSituation s, BaserunningCoefficients c)
        => s.LandAtSeconds + c.FlyReadResumeSeconds;

    /// <summary>
    /// 待った走者の追加スタート遅延[s]（<see cref="HomePlayResolver.RunnerTimeSeconds(Player,int,int,Field.FieldGeometry,BaserunningCoefficients,double)"/>
    /// の extraStartDelaySeconds に渡す）。走者は通常の時計で <paramref name="holdLeadM"/> の位置まで来て待ち、
    /// <paramref name="resumeAtSeconds"/> から残りを走る＝到達時刻は resume＋(残りの距離)÷速度。
    /// 落ちるのが早く、待つ位置に着く前に走り出せるなら 0（止まらない）。
    /// </summary>
    public static double HoldExtraDelaySeconds(
        Player runner, double holdLeadM, double resumeAtSeconds, BaserunningCoefficients c)
    {
        var ctx = HomePlayResolver.RunnerContextOf(runner, c);
        var v = runner.ToFielder(c.AbilityEffect).SprintSpeedMps * ctx.TopSpeedFactor;
        var reachHold = ctx.StartDelaySeconds + (holdLeadM - ctx.LeadDistanceM) / v;
        return Math.Max(0.0, resumeAtSeconds - reachHold);
    }

    /// <summary>ハーフウェーの走者が、捕球の時点で元の塁から離れている距離[m]（待つ位置に着く前に捕られたらその途中）。</summary>
    public static double LeadAtCatchM(Player runner, double holdLeadM, double catchAtSeconds, BaserunningCoefficients c)
    {
        var ctx = HomePlayResolver.RunnerContextOf(runner, c);
        var v = runner.ToFielder(c.AbilityEffect).SprintSpeedMps * ctx.TopSpeedFactor;
        var run = Math.Max(0.0, catchAtSeconds - ctx.StartDelaySeconds) * v;
        return Math.Min(holdLeadM, ctx.LeadDistanceM + run);
    }
}
