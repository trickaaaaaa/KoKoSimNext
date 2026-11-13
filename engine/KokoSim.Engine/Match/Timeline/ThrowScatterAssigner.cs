using System;
using System.Collections.Generic;
using System.Linq;
using KokoSim.Engine.Core;
using KokoSim.Engine.Match.Field;
using KokoSim.Engine.Match.Fielding;

namespace KokoSim.Engine.Match.Timeline;

/// <summary>
/// 打席の絵（<see cref="PlayTimeline"/>）の送球へ逸れ（<see cref="BallSegment.ThrowScatter"/>）を載せる（issue #859）。
///
/// <para>送球の区間は30か所近い組み立て関数がそれぞれ作るので、各関数へ逸れを通すと漏れが出る。そこで絵を
/// 組み終えた後にここ1か所で載せる。①判定が引いた逸れ（<see cref="JudgedThrow"/>）は、同じ塁へ向かう送球へ
/// 時刻の早い順に1本ずつ割り当てる ②判定の無い送球（外野の返球・中継の1本目・投手への返球など）は、
/// 試合の乱数を進めない別の流れ（<paramref name="displayRng"/>＝<see cref="IRandomSource.Fork"/>）から
/// 送球した野手の送球精度で引く。後者は判定に関わらないので、悪送球に見える大きさにはしない
/// （<see cref="DisplayRadialCapSigma"/>σで頭打ち）。</para>
///
/// <para>悪送球の区間（<see cref="BallSegment.MissedBy"/>）は外れ方を別に持つので触らない。乱数は
/// <paramref name="displayRng"/> だけを使い、試合の結果には一切影響しない（不変条件#2）。</para>
/// </summary>
public static class ThrowScatterAssigner
{
    /// <summary>判定が引いた送球1本の逸れ。<paramref name="Base"/> は送球先（1〜3＝各塁、4＝本塁）。</summary>
    public readonly record struct JudgedThrow(int Base, ThrowScatterModel.Scatter Scatter);

    /// <summary>判定の無い送球の逸れの上限（σの倍数）。精度50の悪送球の閾値（約2.7σ）より内側に収める。</summary>
    public const double DisplayRadialCapSigma = 2.5;

    /// <summary>送球の終点がこの距離[m]以内なら、その塁への送球とみなす（一塁カバーの立ち位置の寄せを含む）。</summary>
    private const double BaseMatchRadiusM = 4.0;

    public static PlayTimeline Assign(
        PlayTimeline timeline, IEnumerable<JudgedThrow> judged, FieldGeometry field,
        Func<FieldPosition?, int> throwAccuracyOf, IRandomSource displayRng)
    {
        var queue = judged.ToList();
        var used = new bool[queue.Count];
        var ball = timeline.Ball.ToList();
        var changed = false;
        // 時刻の早い送球から割り当てる（判定の順＝送球の順）。
        foreach (var i in Enumerable.Range(0, ball.Count).OrderBy(i => ball[i].T0))
        {
            var seg = ball[i];
            if (seg.Kind != BallSegmentKind.Throw || seg.MissedBy is not null || seg.ThrowScatter is not null) continue;
            var target = BaseOf(seg.To, field);
            ThrowScatterModel.Scatter? s = null;
            if (target is { } b)
            {
                for (var k = 0; k < queue.Count; k++)
                {
                    if (used[k] || queue[k].Base != b) continue;
                    used[k] = true;
                    s = queue[k].Scatter;
                    break;
                }
            }
            s ??= DisplayScatter(throwAccuracyOf(seg.FromRole), displayRng);
            ball[i] = seg with { ThrowScatter = s };
            changed = true;
        }
        return changed ? timeline with { Ball = ball } : timeline;
    }

    /// <summary>判定の無い送球の逸れ（絵のためだけ）。送球精度のσで引き、<see cref="DisplayRadialCapSigma"/>σで頭打ちにする。</summary>
    public static ThrowScatterModel.Scatter DisplayScatter(int throwAccuracy, IRandomSource displayRng)
    {
        var sigma = ThrowScatterModel.SigmaCmForAccuracy(throwAccuracy);
        var s = ThrowScatterModel.Sample(sigma, displayRng);
        var cap = DisplayRadialCapSigma * sigma;
        if (s.RadialCm <= cap) return s;
        var k = cap / s.RadialCm;
        return new ThrowScatterModel.Scatter(s.LateralCm * k, s.VerticalCm * k);
    }

    /// <summary>送球の終点が塁なら塁番号（1〜3、本塁は4）。塁でなければ null（中継の1本目・投手への返球など）。</summary>
    public static int? BaseOf(TimelinePoint to, FieldGeometry field)
    {
        int? best = null;
        var bestD = BaseMatchRadiusM;
        void Try(int b, double x, double z)
        {
            var d = Math.Sqrt((to.X - x) * (to.X - x) + (to.Z - z) * (to.Z - z));
            if (d <= bestD) { bestD = d; best = b; }
        }
        Try(1, field.FirstBase.X, field.FirstBase.Z);
        Try(2, field.SecondBase.X, field.SecondBase.Z);
        Try(3, field.ThirdBase.X, field.ThirdBase.Z);
        Try(4, 0, 0);
        return best;
    }

    /// <summary>
    /// 打球の打席で判定が引いた送球を、判定の順に並べる。走者へのプレー（<see cref="Game.RunnerMove.ThrowScatter"/>）・
    /// 一塁への転送（<see cref="Game.RunnerMove.RelayThrowScatter"/>）を先に、内野ゴロの一塁送球
    /// （<see cref="FieldingPlay.FirstThrowScatter"/>）を最後に置く（併殺では一塁送球の代わりに転送が一塁へ行く）。
    /// </summary>
    public static List<JudgedThrow> FromBattedBall(FieldingPlay? play, IEnumerable<Game.RunnerMove> moves)
    {
        var list = new List<JudgedThrow>();
        foreach (var m in moves)
        {
            if (m.ThrowScatter is { } s) list.Add(new JudgedThrow(Math.Min(m.ToBase, 4), s));
            if (m.RelayThrowScatter is { } r) list.Add(new JudgedThrow(1, r));
        }
        if (play?.FirstThrowScatter is { } f) list.Add(new JudgedThrow(1, f));
        return list;
    }
}
