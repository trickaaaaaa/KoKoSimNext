using System;
using KokoSim.Engine.Players;

namespace KokoSim.Engine.Match.Field;

/// <summary>
/// 走者が塁を進むのに要する時間の**文脈**（Issue #316）。同じ走者でも、打席から走り出すのか・
/// 塁上でリードを取ってから走るのかで物理が違う。その違いを式の分岐ではなくパラメータで表す。
/// </summary>
/// <param name="StartDelaySeconds">
/// 走り出すまでの遅延[s]。打席からは打撃動作ぶんが別途 <c>BatterToFirstSeconds</c> に入っているので 0。
/// </param>
/// <param name="LeadDistanceM">
/// 二次リードで前に出ている距離[m]。走る距離から差し引く。打席からは 0。
/// </param>
/// <param name="TopSpeedFactor">
/// 静止スタートでない走者の速度倍率。既に走っている走者はスプリント速度より速く塁間を刻む。
/// </param>
/// <param name="TurnSecondsPerBase">
/// ベースを回るたびの損失[s]（減速・踏み直し）。直線で1つ進むだけなら 0 が正しい。
/// </param>
/// <param name="RoundsStartingBase">
/// 出発塁を「回りながら」通過するか。打者走者は一塁を全速で回るところから計測が始まるので true、
/// 塁上の走者は自分の塁から静止気味に出るので false。これが目標塁の手前で回る塁の数
/// （＝損失を払う回数）を 1 つ変える。
/// </param>
public readonly record struct RunnerContext(
    double StartDelaySeconds,
    double LeadDistanceM,
    double TopSpeedFactor,
    double TurnSecondsPerBase,
    bool RoundsStartingBase);

/// <summary>
/// 走者の時計（Issue #316）。**判定も表示もここだけを呼ぶ**。
///
/// 以前は本塁レース（<c>HomePlayResolver</c>）・塁打数（<c>ExtraBaseResolver</c>）・
/// 俯瞰ビューの走者レッグ（<c>TimelineBuilder</c>）が別々の式を持ち、走力50・走塁50で
/// 0.58〜1.63秒ズレていた。画面が「どの判定にも使われていない第4の時計」で動くため、
/// 際どいと判定されたプレーが際どく見えず、判定と絵の矛盾をガードで塗り潰す必要があった。
///
/// 3つの式はどれも物理的に妥当だったので、消すのではなく1本に畳んで
/// <see cref="RunnerContext"/> で違いを表現している。
/// </summary>
public static class RunnerClock
{
    /// <summary>
    /// <paramref name="fromBase"/> から <paramref name="toBase"/> まで走り切る所要時間[s]。
    /// 塁番号は 0=打席, 1=一塁, 2=二塁, 3=三塁, 4=本塁。
    /// <paramref name="extraStartDelaySeconds"/> は打球を読んでから走り出す等の追加遅延
    /// （ゴロの本塁レースで正値・ヒットでは 0）。
    /// </summary>
    public static double SegmentSeconds(
        double sprintSpeedMps, int fromBase, int toBase, in RunnerContext ctx, FieldGeometry field,
        double extraStartDelaySeconds = 0.0)
    {
        var bases = toBase - fromBase;
        var distance = Math.Max(0.0, bases * field.BaseDistanceM - ctx.LeadDistanceM);
        // 損失を払うのは「回る塁」の数。目標塁は踏んで終わりなので数えない（三塁→本塁は0回）。
        // 打者走者だけは出発塁（一塁）を全速で回るところから始まるので1回ぶん多い。
        var turns = Math.Max(0, bases - 1) + (ctx.RoundsStartingBase ? 1 : 0);
        return distance / (sprintSpeedMps * ctx.TopSpeedFactor)
               + turns * ctx.TurnSecondsPerBase
               + ctx.StartDelaySeconds
               + extraStartDelaySeconds;
    }

    /// <summary>選手から直接引く版（走塁の脚力は守備投影のスプリント速度と同一定義）。</summary>
    public static double SegmentSeconds(
        Player runner, int fromBase, int toBase, in RunnerContext ctx, FieldGeometry field,
        double extraStartDelaySeconds = 0.0)
        => SegmentSeconds(
            runner.ToFielder().SprintSpeedMps, fromBase, toBase, ctx, field, extraStartDelaySeconds);
}
