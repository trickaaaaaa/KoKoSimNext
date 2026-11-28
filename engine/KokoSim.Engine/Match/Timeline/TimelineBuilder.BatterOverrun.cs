using System;
using System.Collections.Generic;
using System.Linq;
using KokoSim.Engine.Match.Field;
using KokoSim.Engine.Match.Fielding;

namespace KokoSim.Engine.Match.Timeline;

public static partial class TimelineBuilder
{
    /// <summary>一塁を回る弧の長さの下限・上限[m]（打球が深く走力が高いほど大きい。表示専用の係数）。</summary>
    private const double RoundFirstMinM = 4.0, RoundFirstMaxM = 8.0;

    /// <summary>外野への単打の打球が「深い」とみなす飛距離の幅[m]（外野境界からこれだけ先で弧が最大側へ振り切る）。</summary>
    private const double RoundFirstDepthSpanM = 50.0;

    /// <summary>打者走者の一塁到達の速さ[m/s]がこの幅で上がるほど弧が大きくなる（下端＝遅い走者・上端＝速い走者）。</summary>
    private const double RoundFirstSpeedLowMps = 6.0, RoundFirstSpeedSpanMps = 2.5;

    /// <summary>一塁へ歩いて戻る速さ[m/s]。走者の歩きクリップ（1.14m/s×倍率0.6〜1.45）に収まる値。</summary>
    private const double RoundFirstWalkBackMps = 1.4;

    /// <summary>回って止まってから戻り始めるまでの最短の間[s]・返球を待つ間の上限[s]。</summary>
    private const double RoundFirstPauseMinSeconds = 0.4, RoundFirstPauseMaxSeconds = 3.0;

    /// <summary>返球の絵が無い単打で、止まってから戻り始めるまでの間[s]。</summary>
    private const double RoundFirstNoThrowPauseSeconds = 1.0;

    /// <summary>
    /// 外野への単打で一塁に止まる打者走者に、一塁を回って（オーバーラン）返球が内野へ戻ったら一塁へ歩いて戻る絵を足す
    /// （issue #1765）。弧は短い直線3本（一塁→二塁側へふくらむ）＋戻りの直線1本（<see cref="RunnerLeg.WalkBack"/>）。
    /// 表示専用の追加レッグ＝判定・進塁・乱数は動かさず、一塁へ着いた時刻（主到達レッグの T1）も変えない。
    /// 打者が一塁を回って続きを走る打球（二塁打以上・失策の進塁）・内野ゴロの駆け抜け（#443）には足さない。
    /// </summary>
    public static PlayTimeline AppendBatterOverrunRound(PlayTimeline timeline, FieldingPlay play, FieldGeometry field)
    {
        if (play.Result != BattedBallResult.Single || !IsOutfieldHit(play)) return timeline;

        var firstBase = BasePoint(field, "first");
        RunnerLeg? primary = null;
        foreach (var leg in timeline.Runners)
        {
            if (leg.Label != "打") continue;
            if (leg.OutAtEnd || Distance(leg.To, firstBase) > 0.5) return timeline;   // 一塁で止まる打者だけ
            if (primary is null || leg.T1 > primary.T1) primary = leg;
        }
        if (primary is null || Distance(primary.From, Home) > 0.5) return timeline;   // 本塁→一塁の主到達レッグがある前提

        var secondBase = BasePoint(field, "second");
        var arriveAt = primary.T1;
        var approachSeconds = Math.Max(0.05, arriveAt - primary.T0);
        var approachSpeed = Distance(Home, firstBase) / approachSeconds;

        var depth = Math.Clamp((play.RangeM - FieldGeometry.OutfieldBoundaryM) / RoundFirstDepthSpanM, 0.0, 1.0);
        var speed = Math.Clamp((approachSpeed - RoundFirstSpeedLowMps) / RoundFirstSpeedSpanMps, 0.0, 1.0);
        var d = RoundFirstMinM + (RoundFirstMaxM - RoundFirstMinM) * 0.5 * (depth + speed);

        // 弧: 一塁を踏んだ勢い（本塁→一塁の向き u）で出て、二塁の方（s）へふくらんで曲がる 2次ベジェ。
        var ux = (firstBase.X - Home.X) / Distance(Home, firstBase);
        var uz = (firstBase.Z - Home.Z) / Distance(Home, firstBase);
        var sLen = Distance(firstBase, secondBase);
        var sx = (secondBase.X - firstBase.X) / sLen;
        var sz = (secondBase.Z - firstBase.Z) / sLen;
        var control = new TimelinePoint(firstBase.X + ux * d * 0.6, firstBase.Z + uz * d * 0.6);
        var apex = new TimelinePoint(firstBase.X + (ux + sx) * d * 0.5, firstBase.Z + (uz + sz) * d * 0.5);
        var path = new TimelinePoint[4];
        path[0] = firstBase;
        for (var i = 1; i <= 3; i++)
        {
            var t = i / 3.0;
            var a = (1 - t) * (1 - t);
            var b = 2 * (1 - t) * t;
            var c = t * t;
            path[i] = new TimelinePoint(
                a * firstBase.X + b * control.X + c * apex.X,
                a * firstBase.Z + b * control.Z + c * apex.Z);
        }
        var chord = new double[4];
        for (var i = 1; i <= 3; i++) chord[i] = chord[i - 1] + Distance(path[i - 1], path[i]);
        var arcLength = chord[3];

        // 等減速で止まる（一塁駆け抜け #443 と同じ作法）: 走る速さ approachSpeed から 0 へ。
        var arcSeconds = Math.Max(0.8, 2.0 * arcLength / Math.Max(1.0, approachSpeed));
        var legs = new List<RunnerLeg>(timeline.Runners);
        var prevT = arriveAt;
        for (var i = 1; i <= 3; i++)
        {
            var frac = chord[i] / arcLength;
            var t1 = arriveAt + arcSeconds * (1.0 - Math.Sqrt(Math.Max(0.0, 1.0 - frac)));
            legs.Add(new RunnerLeg
            {
                Label = "打", T0 = prevT, T1 = t1, From = path[i - 1], To = path[i],
                Runner = primary.Runner,
            });
            prevT = t1;
        }
        var stopAt = prevT;

        // 戻り始め: 外野からの返球が内野（中継・二塁・投手）へ着く時刻。返球の絵が無ければ少し間を置く。
        double backAt;
        var throwArrive = timeline.Ball
            .Where(s => s.Kind == BallSegmentKind.Throw && s.ToBase != "first"
                        && Distance(Home, s.From) > FieldGeometry.OutfieldBoundaryM)
            .OrderBy(s => s.T0)
            .Select(s => (double?)s.T1)
            .FirstOrDefault();
        if (throwArrive is { } arrive)
            backAt = Math.Clamp(arrive, stopAt + RoundFirstPauseMinSeconds, stopAt + RoundFirstPauseMaxSeconds);
        else
            backAt = stopAt + RoundFirstNoThrowPauseSeconds;
        var backEnd = backAt + Distance(apex, firstBase) / RoundFirstWalkBackMps;
        legs.Add(new RunnerLeg
        {
            Label = "打", T0 = backAt, T1 = backEnd, From = apex, To = firstBase,
            WalkBack = true,
            Runner = primary.Runner,
        });

        return timeline with { Runners = legs, Duration = Math.Max(timeline.Duration, backEnd + 0.3) };
    }
}
