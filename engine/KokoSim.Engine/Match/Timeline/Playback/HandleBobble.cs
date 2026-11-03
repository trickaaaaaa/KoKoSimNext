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
    /// <summary>グラブで弾いた球がグラブから外へ跳ねる最小の幅[m]（歩く時間が無いとき）。</summary>
    public const double PopM = 0.3;
    /// <summary>歩いて拾いに行ける距離の上限[m]。</summary>
    public const double MaxWalkM = 1.5;
    /// <summary>拾いに行く歩きの速さ[m/s]（こぼれ幅を歩く時間から決める）。</summary>
    public const double WalkSpeedMps = 1.5;
    /// <summary>拾いに寄る一歩に最低限かける時間[s]（これより短いと体が滑って見える）。</summary>
    public const double MinStepSeconds = 0.5;

    /// <summary>
    /// お手玉の球と野手の動きを足したプレーを返す（表示専用）。
    ///
    /// <para>球は<b>野手の体から見てグラブの向き</b>（＝野手から打球の止まる点への向き）へこぼす。
    /// 弾く瞬間の球はグラブの位置（野手から <paramref name="gloveReachM"/>）に居て、そこから
    /// <see cref="PopM"/>＋歩ける距離だけ外へ転がって止まる。野手はその球へ寄り、
    /// 拾う収録のグラブの距離（<paramref name="pickupReachM"/>）まで近づいて拾う＝
    /// <b>拾う瞬間に球を手へ引き寄せない</b>（2026-11-03 監督指摘「残像付きで吸い込まれる」）。</para>
    ///
    /// <para>従来は打球の終点から進行方向へこぼしていたが、engine の終点は野手から 1.1m 横
    /// （実測）＝球がグラブに触れずに通り過ぎ、拾う瞬間に 1.4m 吸い寄せていた。</para>
    ///
    /// 送球の区間・時刻はそのまま。お手玉でない予定ならそのまま返す。
    /// </summary>
    public static PlaybackPlay ApplyFumble(PlaybackPlay play, HandleBobble bobble, double recoverSeconds,
        double gloveReachM, double pickupReachM)
    {
        if (bobble.Kind != HandleBobbleKind.Fumble) return play;
        var pickupT = bobble.ReleaseT - PickupLeadSeconds;
        if (pickupT <= bobble.CatchT + 0.1) return play;
        var stepEnd = pickupT - 0.1;
        // 歩き出しは弾いた芝居が終わってから。ただし一歩ぶんの時間は必ず残す（弾いた直後は動かない）
        var stepStart = Math.Max(bobble.CatchT + 0.3,
            Math.Min(bobble.CatchT + Math.Max(0.0, recoverSeconds), stepEnd - MinStepSeconds));
        var walk = Math.Max(0.0, stepEnd - (bobble.CatchT + Math.Max(0.0, recoverSeconds)));
        var walkM = Math.Min(MaxWalkM, walk * WalkSpeedMps);

        var balls = new List<PlaybackBallSegment>(play.Ball.Count + 1);
        TimelinePoint? stand = null;
        var fp = PlaybackEvaluator.FielderAt(play, bobble.Fielder, bobble.CatchT).Pos;
        foreach (var s in play.Ball)
        {
            balls.Add(s);
            if (stand != null || s is not EndpointBallSegment eb || Math.Abs(eb.T1 - bobble.CatchT) > 1e-6
                || eb.Kind is not (BallSegmentKind.Roll or BallSegmentKind.Bounce)) continue;
            var end = eb.At(eb.T1 - eb.T0);
            var dx = end.X - fp.X;
            var dy = end.Y - fp.Y;
            var len = Math.Sqrt(dx * dx + dy * dy);
            if (len < 1e-6)
            {
                // 野手の真上で止まる打球＝向きが決まらない。打球の進行方向へ落とす
                var back = eb.At(Math.Max(0.0, eb.T1 - eb.T0 - 0.1));
                dx = end.X - back.X; dy = end.Y - back.Y;
                len = Math.Sqrt(dx * dx + dy * dy);
                if (len < 1e-9) { dx = 0; dy = 1; len = 1; }
            }
            dx /= len; dy /= len;
            var glove = new TimelinePoint(fp.X + dx * gloveReachM, fp.Y + dy * gloveReachM);
            var restR = gloveReachM + PopM + walkM;
            var rest = new TimelinePoint(fp.X + dx * restR, fp.Y + dy * restR);
            balls.Add(EndpointBallSegment.From(new BallSegment
            {
                Kind = BallSegmentKind.Roll,
                T0 = bobble.CatchT, T1 = pickupT,
                From = glove, To = rest,
            }));
            var standR = Math.Max(0.0, restR - pickupReachM);
            stand = new TimelinePoint(fp.X + dx * standR, fp.Y + dy * standR);
        }
        if (stand is not { } st) return play;

        var moves = new Dictionary<FieldPosition, IReadOnlyList<PlaybackMove>>();
        if (play.Moves != null) foreach (var kv in play.Moves) moves[kv.Key] = kv.Value;
        var legs = new List<PlaybackMove>();
        if (moves.TryGetValue(bobble.Fielder, out var had))
            foreach (var m in had) if (m.T1 <= stepStart + 1e-9) legs.Add(m);   // 後ろのレッグは拾いに行く間は捨てる
        legs.Add(new PlaybackMove(stepStart, stepEnd, new PlaybackVec(st.X, st.Z)));
        moves[bobble.Fielder] = legs;
        return play with { Ball = balls, Moves = moves };
    }

    private static bool IsInfielder(FieldPosition p)
        => p is FieldPosition.Pitcher or FieldPosition.Catcher or FieldPosition.FirstBase
            or FieldPosition.SecondBase or FieldPosition.ThirdBase or FieldPosition.Shortstop;
}
