using System;
using System.Collections.Generic;
using KokoSim.Engine.Match.Field;

namespace KokoSim.Engine.Match.Timeline.Playback;

/// <summary>内野手が捕ってから投げるまでの「もたつき」の見せ方（表示専用）。</summary>
public enum HandleBobbleKind
{
    /// <summary>普通に握り替えて投げる（何も足さない）。</summary>
    None,
    /// <summary>握り損ねて、球がグラブの中で小さく跳ねてから掴み直す。</summary>
    Juggle,
    /// <summary>お手玉。グラブで弾いて前へこぼし、拾い直して投げる（記録は失策にならない）。</summary>
    Fumble,
}

/// <summary>もたつきの見せ方の予定（<see cref="HandleBobblePlanner.Plan"/> の結果）。</summary>
public readonly record struct HandleBobble(HandleBobbleKind Kind, FieldPosition Fielder, double CatchT, double ReleaseT)
{
    public static readonly HandleBobble NoneValue = new(HandleBobbleKind.None, FieldPosition.Pitcher, 0, 0);

    /// <summary>捕ってから投げるまで[s]。</summary>
    public double HoldSeconds => ReleaseT - CatchT;
}

/// <summary>
/// 内野ゴロの「捕ってから投げるまで」に芝居を付ける（表示専用・判定と帯には触れない）。
///
/// <para>engine の内野処理は、構え直し＋握り替え＋<b>もたつき</b>（指数分布・内野安打率の校正項。
/// <c>FieldingCoefficients.InfieldHandleJitterMeanSeconds</c>）の和で送球の時刻を決める。
/// 3Dではその間ずっとボールを持って立っているだけなので、長いもたつきを引いた打席
/// （内野安打の大半）は「捕ったまま突っ立っている＝怠慢」に見えていた（2026-11-03 監督報告）。</para>
///
/// <para>捕球時刻・投げ始め・送球の到達は1つも動かさない。保持の長さで見せ方を3段に分けるだけ。
/// 境目は実測（400試合・内野→一塁 6,791本）で、全体の約20%が握り直し・約11%がお手玉
/// （内野安打の約6割がお手玉）になる値。</para>
/// </summary>
public static class HandleBobblePlanner
{
    /// <summary>これより長く持つと「握り直し」。</summary>
    public const double JuggleAboveSeconds = 1.4;
    /// <summary>これより長く持つと「お手玉」。</summary>
    public const double FumbleAboveSeconds = 2.0;
    /// <summary>お手玉でこぼれる距離[m]（球の進行方向へ）。</summary>
    public const double FumbleDeflectM = 0.6;

    /// <summary>保持の長さから見せ方を決める（純関数）。</summary>
    public static HandleBobbleKind KindFor(double holdSeconds)
        => holdSeconds > FumbleAboveSeconds ? HandleBobbleKind.Fumble
            : holdSeconds > JuggleAboveSeconds ? HandleBobbleKind.Juggle
            : HandleBobbleKind.None;

    /// <summary>
    /// このプレーのもたつきの見せ方。対象は「ゴロ（転がり・バウンド）を内野手が捕って、そのまま送球する」打席だけ。
    /// 本物の失策（<see cref="PlaybackPlay.Fumbled"/>）・飛球・外野の返球は対象外。
    /// </summary>
    public static HandleBobble Plan(PlaybackPlay play)
    {
        if (play?.Ball == null || play.Fumbled) return HandleBobble.NoneValue;
        EndpointBallSegment? ground = null;
        foreach (var s in play.Ball)
        {
            if (s is not EndpointBallSegment eb) continue;
            if (eb.Kind == BallSegmentKind.Flight) return HandleBobble.NoneValue;
            if (ground == null)
            {
                if (eb.Kind is BallSegmentKind.Roll or BallSegmentKind.Bounce) ground = eb;
                continue;
            }
            // 打球の直後に来る区間が送球でなければ（エラーのこぼれ・保持の運び等）対象外
            if (eb.Kind != BallSegmentKind.Throw || eb.FromRole is not { } from || !IsInfielder(from))
                return HandleBobble.NoneValue;
            if (eb.T0 < ground.T1 - 1e-6) return HandleBobble.NoneValue;
            var kind = KindFor(eb.T0 - ground.T1);
            return kind == HandleBobbleKind.None
                ? HandleBobble.NoneValue
                : new HandleBobble(kind, from, ground.T1, eb.T0);
        }
        return HandleBobble.NoneValue;
    }

    /// <summary>拾い上げてから投げ始めるまで[s]（拾った球を握り替えて投げる間）。</summary>
    public const double PickupLeadSeconds = 0.45;
    /// <summary>歩み寄る時間がほとんど無いときのこぼれ幅[m]（その場で拾い直す）。</summary>
    public const double MinDeflectM = 0.6;
    /// <summary>こぼれ幅の上限[m]。</summary>
    public const double MaxDeflectM = 2.0;
    /// <summary>拾いに行く歩きの速さ[m/s]（こぼれ幅を歩く時間から決める）。</summary>
    public const double WalkSpeedMps = 1.5;
    /// <summary>拾うとき、野手が球の手前に立つ距離[m]（グラブが届く距離）。</summary>
    public const double PickupStandOffM = 0.5;

    /// <summary>
    /// お手玉の球と野手の動きを足したプレーを返す（表示専用）。
    /// 捕球点で弾いた球は進行方向へこぼれて転がり、野手は弾いた芝居（<paramref name="recoverSeconds"/>）の後に
    /// 歩み寄って、投げ始めの <see cref="PickupLeadSeconds"/> 前に拾う。<b>投げ始めまで動きが途切れない</b>よう、
    /// こぼれ幅は歩く時間から決める（歩く時間が無ければその場で拾い直す）。
    /// 送球の区間・時刻はそのまま。お手玉でない予定ならそのまま返す。
    /// </summary>
    public static PlaybackPlay ApplyFumble(PlaybackPlay play, HandleBobble bobble, double recoverSeconds)
    {
        if (bobble.Kind != HandleBobbleKind.Fumble) return play;
        var pickupT = bobble.ReleaseT - PickupLeadSeconds;
        if (pickupT <= bobble.CatchT + 0.1) return play;
        var walkStart = bobble.CatchT + Math.Max(0.0, recoverSeconds);
        var walkEnd = pickupT - 0.1;
        var walk = walkEnd - walkStart;
        var deflect = walk >= 0.25 ? Math.Min(MaxDeflectM, MinDeflectM + walk * WalkSpeedMps) : MinDeflectM;

        var balls = new List<PlaybackBallSegment>(play.Ball.Count + 1);
        TimelinePoint? stand = null;
        foreach (var s in play.Ball)
        {
            balls.Add(s);
            if (stand != null || s is not EndpointBallSegment eb || Math.Abs(eb.T1 - bobble.CatchT) > 1e-6
                || eb.Kind is not (BallSegmentKind.Roll or BallSegmentKind.Bounce)) continue;
            var dur = eb.T1 - eb.T0;
            var end = eb.At(dur);
            var back = eb.At(Math.Max(0.0, dur - 0.1));
            var dx = end.X - back.X;
            var dy = end.Y - back.Y;
            var len = Math.Sqrt(dx * dx + dy * dy);
            if (len < 1e-9) { dx = 0; dy = 1; len = 1; }
            dx /= len; dy /= len;
            var rest = new TimelinePoint(end.X + dx * deflect, end.Y + dy * deflect);
            balls.Add(EndpointBallSegment.From(new BallSegment
            {
                Kind = BallSegmentKind.Roll,
                T0 = bobble.CatchT, T1 = pickupT,
                From = new TimelinePoint(end.X, end.Y), To = rest,
            }));
            stand = new TimelinePoint(rest.X - dx * PickupStandOffM, rest.Z - dy * PickupStandOffM);
        }
        if (stand is not { } st) return play;

        var moves = new Dictionary<FieldPosition, IReadOnlyList<PlaybackMove>>();
        if (play.Moves != null) foreach (var kv in play.Moves) moves[kv.Key] = kv.Value;
        if (walk >= 0.25)
        {
            var legs = new List<PlaybackMove>();
            if (moves.TryGetValue(bobble.Fielder, out var had))
                foreach (var m in had) if (m.T1 <= walkStart + 1e-9) legs.Add(m);   // 後ろのレッグは拾いに行く間は捨てる
            legs.Add(new PlaybackMove(walkStart, walkEnd, new PlaybackVec(st.X, st.Z)));
            moves[bobble.Fielder] = legs;
        }
        return play with { Ball = balls, Moves = moves };
    }

    private static bool IsInfielder(FieldPosition p)
        => p is FieldPosition.Pitcher or FieldPosition.Catcher or FieldPosition.FirstBase
            or FieldPosition.SecondBase or FieldPosition.ThirdBase or FieldPosition.Shortstop;
}
