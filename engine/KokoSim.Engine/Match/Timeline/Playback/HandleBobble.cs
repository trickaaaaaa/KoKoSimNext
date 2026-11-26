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
    /// <summary>お手玉。グラブで弾いて前へこぼし、拾い直して投げる（お手玉の失策＝<see cref="PlaybackPlay.BobbleError"/> か、お手玉しても一塁アウトのとき）。</summary>
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
/// <para>捕球時刻・投げ始め・送球の到達は1つも動かさない。保持の長さで握り直しを出し、
/// お手玉は engine が「お手玉の失策」（<c>FieldingErrorKind.Bobble</c>）と記録した打席だけに出す
/// （2026-11-03: お手玉の絵で内野安打＝記録と絵の矛盾、を解消。それまでは保持2秒超で一律お手玉だった）。</para>
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
        if (!TryFindHold(play, out var from, out var catchT, out var releaseT)) return HandleBobble.NoneValue;
        // お手玉の芝居は「お手玉の失策」か「お手玉しても一塁アウト」のときだけ。
        // 内野安打（セーフ）でお手玉は記録と絵の矛盾（2026-11-03 監督報告）なので握り直しまで＝
        // 「握り直す間に足で稼いだ内野安打」に見える。お手玉＋アウトは矛盾しない（issue #815）。
        var kind = play.BobbleError ? HandleBobbleKind.Fumble : KindFor(releaseT - catchT);
        if (!play.BobbleError && kind == HandleBobbleKind.Fumble && !BatterOut(play)) kind = HandleBobbleKind.Juggle;
        return kind == HandleBobbleKind.None
            ? HandleBobble.NoneValue
            : new HandleBobble(kind, from, catchT, releaseT);
    }

    /// <summary>
    /// 「ゴロを内野手が捕って、そのまま送球する」区間を探す（<see cref="Plan"/> と <see cref="ApplyStep"/> の共通の入口）。
    /// 本物の失策・飛球・外野の返球・打球の直後が送球でないプレーは false。
    /// </summary>
    public static bool TryFindHold(PlaybackPlay play, out FieldPosition fielder, out double catchT, out double releaseT)
    {
        fielder = FieldPosition.Pitcher;
        catchT = releaseT = 0;
        if (play?.Ball == null || play.Fumbled) return false;
        EndpointBallSegment? ground = null;
        foreach (var s in play.Ball)
        {
            if (s is not EndpointBallSegment eb) continue;
            if (eb.Kind == BallSegmentKind.Flight) return false;
            if (ground == null)
            {
                if (eb.Kind is BallSegmentKind.Roll or BallSegmentKind.Bounce) ground = eb;
                continue;
            }
            // 打球の直後に来る区間が送球でなければ（エラーのこぼれ・保持の運び等）対象外
            if (eb.Kind != BallSegmentKind.Throw || eb.FromRole is not { } from || !IsInfielder(from)) return false;
            if (eb.T0 < ground.T1 - 1e-6) return false;
            fielder = from;
            catchT = ground.T1;
            releaseT = eb.T0;
            return true;
        }
        return false;
    }

    // ── 送球へのステップ（2026-11-26 監督報告「二塁手が捕ってからぼーっとしてセーフ」） ──────────
    //
    // engine のもたつき（内野安打率の校正項）が長い打席は、捕った形の据え置きが終わってから送球の収録が
    // 始まるまで、球を持ったまま突っ立つ。握り直し（Juggle）の芝居は球を一度浮かせるだけなので、守備の低い
    // 野手の長い保持（2〜3秒）では立ち止まりの大半が残っていた。
    // 表示側はまず捕球の収録を流し切り（球を胸に収めて起き上がる約1秒。DuelStageRig.ApplyStepBeforeThrow）、
    // それでも余る間に**送球の的へ横歩き（サイドステップ）で寄る**＝ここが足すのはその横歩きの一歩
    // （前歩きは散歩に見えた＝2026-11-26 監督見比べ）。捕球時刻・投げ始め・送球の到達は1つも動かさない（表示専用・帯不変）。
    // 長く持つこと自体をやめる根本対応は issue #1685。

    /// <summary>立ち止まりがこれより短ければ足さない[s]（一歩にもならない）。</summary>
    public const double StepMinSeconds = 0.3;
    /// <summary>寄る速さ[m/s]（区間の平均）。台形の山がシャッフルの収録（1.35m/s）に乗る速さ。</summary>
    public const double StepSpeedMps = 1.0;
    /// <summary>寄る距離の上限[m]（サイドステップ2〜3歩ぶん）。</summary>
    public const double StepMaxM = 1.0;
    /// <summary>寄る距離は送球距離のこの割合まで（近い送球で相手へ歩いて渡しに行かない）。</summary>
    public const double StepMaxThrowShare = 0.15;
    /// <summary>これより短い寄りは足さない[m]（足踏みにしかならない）。</summary>
    public const double StepMinM = 0.3;

    /// <summary>
    /// 捕ってから送球の収録が始まるまでの立ち止まりに、送球の的へ寄る一歩を足したプレーを返す（表示専用）。
    ///
    /// <para>寄るのは <paramref name="settleSeconds"/>（捕球の収録の尻尾・握り直しの芝居）の後から、
    /// 送球の収録が始まる <paramref name="throwLeadSeconds"/>（投げ始めの何秒前から収録を流すか）手前まで。
    /// 寄り終わりを収録の頭にそろえ、寄り始めは距離÷<see cref="StepSpeedMps"/> だけ前＝足が地面を滑らない速さを保つ。</para>
    ///
    /// <para>送球の時刻・終点はそのまま。始点だけ寄ったぶんずらす（球は手の中にあるので一緒に動く）。
    /// その野手に保持の間の移動予定（engine の運び等）が既にあれば触らない。</para>
    /// </summary>
    public static PlaybackPlay ApplyStep(PlaybackPlay play, double settleSeconds, double throwLeadSeconds)
        => ApplyStep(play, settleSeconds, throwLeadSeconds, out _, out _);

    /// <summary><see cref="ApplyStep(PlaybackPlay,double,double)"/> と同じ。足した一歩（無ければ null）と、その野手を返す。</summary>
    public static PlaybackPlay ApplyStep(PlaybackPlay play, double settleSeconds, double throwLeadSeconds,
        out PlaybackMove? step, out FieldPosition stepper)
    {
        step = null;
        stepper = FieldPosition.Pitcher;
        if (!TryFindHold(play, out var fielder, out var catchT, out var releaseT)) return play;
        if (play.BobbleError) return play;   // お手玉の失策は ApplyFumble の拾い直しが同じ時間を使う
        var windowStart = catchT + Math.Max(0.0, settleSeconds);
        var stepEnd = releaseT - Math.Max(0.0, throwLeadSeconds);
        if (stepEnd - windowStart < StepMinSeconds) return play;

        IReadOnlyList<PlaybackMove>? had = null;
        if (play.Moves != null && play.Moves.TryGetValue(fielder, out var existing) && existing != null)
            had = existing;
        if (had != null)
            foreach (var m in had)
                if (m.T1 > catchT + 1e-6 && m.T0 < releaseT - 1e-6) return play;

        EndpointBallSegment? throwSeg = null;
        int throwIdx = -1;
        for (int i = 0; i < play.Ball.Count; i++)
            if (play.Ball[i] is EndpointBallSegment { Kind: BallSegmentKind.Throw } th && th.FromRole == fielder
                && Math.Abs(th.T0 - releaseT) < 1e-6)
            {
                throwSeg = th;
                throwIdx = i;
                break;
            }
        if (throwSeg == null) return play;

        var from = throwSeg.At(0);
        var to = throwSeg.At(throwSeg.T1 - throwSeg.T0);
        var dx = to.X - from.X;
        var dz = to.Y - from.Y;
        var throwM = Math.Sqrt(dx * dx + dz * dz);
        if (throwM < 1e-6) return play;
        var stepM = Math.Min(StepMaxM, Math.Min((stepEnd - windowStart) * StepSpeedMps, throwM * StepMaxThrowShare));
        if (stepM < StepMinM) return play;
        dx /= throwM; dz /= throwM;

        var fp = PlaybackEvaluator.FielderAt(play, fielder, catchT).Pos;
        var stepStart = stepEnd - stepM / StepSpeedMps;
        var legs = new List<PlaybackMove>();
        if (had != null) legs.AddRange(had);
        step = new PlaybackMove(stepStart, stepEnd, new PlaybackVec(fp.X + dx * stepM, fp.Y + dz * stepM));
        stepper = fielder;
        legs.Add(step);
        legs.Sort((a, b) => a.T0.CompareTo(b.T0));
        var moves = new Dictionary<FieldPosition, IReadOnlyList<PlaybackMove>>();
        if (play.Moves != null) foreach (var kv in play.Moves) moves[kv.Key] = kv.Value;
        moves[fielder] = legs;

        var balls = new List<PlaybackBallSegment>(play.Ball);
        balls[throwIdx] = throwSeg.WithStart(new TimelinePoint(from.X + dx * stepM, from.Y + dz * stepM));
        return play with { Ball = balls, Moves = moves };
    }

    /// <summary>打者走者がアウトになるプレーか（判定イベント「打」のアウトがある）。</summary>
    private static bool BatterOut(PlaybackPlay play)
    {
        foreach (var j in play.Judgements)
            if (j.Label == "打" && j.Kind == JudgementKind.Out) return true;
        return false;
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
        TimelinePoint? restPt = null;
        var fp = PlaybackEvaluator.FielderAt(play, bobble.Fielder, bobble.CatchT).Pos;
        foreach (var s in play.Ball)
        {
            // 拾い直した球が止まっていた点から投げ始める（元の捕球点へ球が跳ばない）。送球の時刻・終点は動かさない。
            if (restPt is { } rp && s is EndpointBallSegment { Kind: BallSegmentKind.Throw } th && th.FromRole == bobble.Fielder)
            {
                balls.Add(th.WithStart(rp));
                restPt = null;
                continue;
            }
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
            restPt = rest;
            var standR = Math.Max(0.0, restR - pickupReachM);
            stand = new TimelinePoint(fp.X + dx * standR, fp.Y + dy * standR);
        }
        if (stand is not { } st) return play;

        var moves = new Dictionary<FieldPosition, IReadOnlyList<PlaybackMove>>();
        if (play.Moves != null) foreach (var kv in play.Moves) moves[kv.Key] = kv.Value;
        var legs = new List<PlaybackMove>();
        if (moves.TryGetValue(bobble.Fielder, out var had))
            foreach (var m in had) if (m.T1 <= stepStart + 1e-9) legs.Add(m);   // 拾いに行く間のレッグは寄る一歩に置き換える
        legs.Add(new PlaybackMove(stepStart, stepEnd, new PlaybackVec(st.X, st.Z)));
        // 拾い終わったあとのレッグ（定位置復帰など）は戻す。寄る一歩にまたがるものは終点を保ち、拾い終わりから始める。
        if (had != null)
            foreach (var m in had)
            {
                if (m.T1 <= stepStart + 1e-9 || m.T1 <= stepEnd + 1e-9) continue;
                legs.Add(m.T0 >= stepEnd - 1e-9 ? m : m with { T0 = stepEnd });
            }
        moves[bobble.Fielder] = legs;
        return play with { Ball = balls, Moves = moves };
    }

    private static bool IsInfielder(FieldPosition p)
        => p is FieldPosition.Pitcher or FieldPosition.Catcher or FieldPosition.FirstBase
            or FieldPosition.SecondBase or FieldPosition.ThirdBase or FieldPosition.Shortstop;
}
