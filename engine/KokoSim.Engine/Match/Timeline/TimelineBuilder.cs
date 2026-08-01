using System;
using System.Collections.Generic;
using KokoSim.Engine.Match.Batting;
using KokoSim.Engine.Match.Field;
using KokoSim.Engine.Match.Fielding;
using KokoSim.Engine.Match.Game;
using KokoSim.Engine.Match.Pitching;
using KokoSim.Engine.Players;

namespace KokoSim.Engine.Match.Timeline;

/// <summary>
/// タイムライン組み立て（CHANGELOG 32-33）。守備解決が計算済みの幾何・時刻を
/// 「座標＋時刻付きイベント列」へ変換する。**乱数を一切使わない**（既存の判定結果から派生するだけ）。
/// UI は時刻 t を進めて再生するだけ（mock-match-2d-view.html / mock-steal-2d-view.html と同一構造）。
/// </summary>
public static class TimelineBuilder
{
    /// <summary>投球の所要時間[s]（演出用の固定値。実測球速の反映は詳細モードで）。</summary>
    private const double PitchDuration = 0.45;
    /// <summary>投球開始→接触までのリード[s]。</summary>
    private const double ContactAt = 0.85;

    private static readonly TimelinePoint Home = new(0, 0);
    private static readonly TimelinePoint Mound = new(0, 18.44);

    /// <summary>
    /// このプレーの守備開始位置（<see cref="PlayTimeline.FielderStarts"/>）。判定が距離・時間を出したのと
    /// **同じ座標**を再生層へ渡す（表示専用・乱数不使用）。
    /// </summary>
    private static IReadOnlyDictionary<FieldPosition, TimelinePoint> StartsOf(IReadOnlyList<Fielder> fielders)
    {
        var d = new Dictionary<FieldPosition, TimelinePoint>(fielders.Count);
        foreach (var f in fielders) d[f.Position] = new TimelinePoint(f.Location.X, f.Location.Z);
        return d;
    }

    /// <summary>守備陣を受け取らない経路（ファウル・盗塁）用の標準配置。</summary>
    private static IReadOnlyDictionary<FieldPosition, TimelinePoint> StandardStarts()
    {
        var d = new Dictionary<FieldPosition, TimelinePoint>(9);
        foreach (var pos in new[]
                 {
                     FieldPosition.Pitcher, FieldPosition.Catcher, FieldPosition.FirstBase,
                     FieldPosition.SecondBase, FieldPosition.ThirdBase, FieldPosition.Shortstop,
                     FieldPosition.LeftField, FieldPosition.CenterField, FieldPosition.RightField,
                 })
        {
            var (x, z) = FieldGeometry.StandardPosition(pos);
            d[pos] = new TimelinePoint(x, z);
        }
        return d;
    }

    /// <summary>
    /// 塁 <paramref name="fromBase"/> から次塁へ <paramref name="leadM"/> [m] 出たリード位置。
    /// 走者の時計（<see cref="RunnerClock.SegmentSeconds"/>）が距離から引いているのと同じ量を絵にも乗せる。
    /// </summary>
    /// <summary>
    /// 処理野手が実際に体を運ぶ地点（表示専用）。走れる距離 <paramref name="runnableM"/> で届かないぶんは
    /// 守備範囲 <paramref name="reachM"/>（飛びつき・伸ばした腕）が埋める、として手前で止める。
    ///
    /// 守備解決は「半径 <c>InfielderFieldingRadiusM</c> ぶんは届く」で捕れるかを決めているので、
    /// **体を打球の位置まで運ぶ必要がない**。運ばせると、詰まったレッグ（<see cref="ArriveByFieldedAt"/>）で
    /// 体が脚力を超えて動く（実測: 4.5m を 0.12秒＝37 m/s）。埋めてよいのは半径までで、それを超える
    /// ぶんは従来どおり打球の位置まで運ぶ（＝嘘を半径で頭打ちにする）。
    /// </summary>
    private static TimelinePoint StopWithinReach(
        TimelinePoint from, TimelinePoint to, double runnableM, double reachM)
    {
        var dist = Distance(from, to);
        var shortfall = dist - Math.Max(0.0, runnableM);
        if (shortfall <= 0.0 || dist <= 1e-9) return to;
        var pull = Math.Min(shortfall, Math.Max(0.0, reachM));
        var u = (dist - pull) / dist;
        return new TimelinePoint(from.X + (to.X - from.X) * u, from.Z + (to.Z - from.Z) * u);
    }

    private static TimelinePoint LeadPoint(FieldGeometry field, int fromBase, double leadM)
    {
        if (leadM <= 0) return BasePoint(field, BaseName(fromBase));
        var a = BasePoint(field, BaseName(fromBase));
        var b = fromBase + 1 >= 4 ? Home : BasePoint(field, BaseName(fromBase + 1));
        var dx = b.X - a.X;
        var dz = b.Z - a.Z;
        var len = Math.Sqrt(dx * dx + dz * dz);
        if (len <= 1e-9) return a;
        var u = Math.Min(1.0, leadM / len);
        return new TimelinePoint(a.X + dx * u, a.Z + dz * u);
    }

    private static TimelinePoint BasePoint(FieldGeometry field, string name) => name switch
    {
        "first" => new TimelinePoint(field.FirstBase.X, field.FirstBase.Z),
        "second" => new TimelinePoint(field.SecondBase.X, field.SecondBase.Z),
        "third" => new TimelinePoint(field.ThirdBase.X, field.ThirdBase.Z),
        _ => Home,
    };

    /// <summary>
    /// 打球プレーのタイムラインを構築。play=守備解決の詳細、fielders=現守備陣、
    /// formations=陣形ルール表（null なら既定表）。時刻は投球開始=0、接触=0.85s。
    /// closeCallMarginSeconds=判定オーバーレイ（Issue #59）の際どさしきい値[s]（既定は
    /// <see cref="BaserunningCoefficients.CloseCallMarginSeconds"/> の初期値と同値）。
    /// closeFlyMarginSeconds=判定オーバーレイ「フェア」（Issue #328②）の際どさしきい値[s]（既定は
    /// <see cref="FieldingCoefficients.CatchCloseCallMarginSeconds"/> の初期値と同値）。
    /// fielderReachM=野手の守備範囲[m]（既定は <see cref="FieldingCoefficients.InfielderFieldingRadiusM"/>
    /// の初期値と同値）。**表示専用**: 守備解決は「半径ぶんは飛びついて届く」で判定しているので、
    /// 体を打球の位置まで運ぶ必要がない。この余白ぶん処理レッグを短くしないと、体が脚力を超えて動く。
    /// </summary>
    public static PlayTimeline BuildBattedBall(
        FieldingPlay play,
        IReadOnlyList<Fielder> fielders,
        FieldGeometry field,
        FormationTable? formations,
        bool runnersOn,
        string resultText,
        double closeCallMarginSeconds = 0.15,
        double closeFlyMarginSeconds = 0.15,
        double fielderReachM = 4.20)
    {
        var table = formations ?? FormationTable.Default;
        var landing = new TimelinePoint(play.LandingX, play.LandingZ);
        // 回収点（エンジンが実際に「誰が・どこで・いつ」処理したかの答え）。表示はここを単一ソースにする
        // （Issue #301①②）。着地点しか見ていなかったため、外野安打でボールが着地点に貼り付いたまま
        // 静止し、野手の居場所ともズレていた。
        var fieldPoint = FieldPointOf(play);

        // 守備陣形（9人全員に役割を付与, CHANGELOG 33）。ゾーン分類は着地点距離(RangeM)ではなく
        // 実際の回収点(fieldPoint)基準にする（Issue #336①）。ゴロは本塁寄りで着地して転がるため、
        // 着地点距離だけでは内野を抜けた打球（ThroughInfield）が内野ゾーンのまま誤分類され、
        // SS/3B(2B)が抜けた打球に一歩も反応しない棒立ちになっていた。方位角も回収点から取り直す
        // （着地点と回収点で通り道が微妙にずれるイレギュラーバウンドがあるため一貫させる）。
        // 内野で処理が完結する打球は回収点が着地点の延長上（同じ46m境界内）なので分類は変わらない。
        // ⚠ ここで先に出すのは、**一塁送球の受け手（Issue E1）を送球を積む時点で知る**ため
        // （送球の生成はこの下、陣形ループはさらに後ろ）。値は fieldPoint だけで決まる。
        var fieldRangeM = Math.Sqrt(fieldPoint.X * fieldPoint.X + fieldPoint.Z * fieldPoint.Z);
        var fieldBearingDeg = Math.Atan2(fieldPoint.X, fieldPoint.Z) * 180.0 / Math.PI;
        var zone = FormationTable.Classify(fieldBearingDeg, fieldRangeM);
        // 一塁カバーは実捕球者から動的に決める（Issue #257）。静的表のゾーン別担当は初期値止まりで
        // 誰が実際に捕ったかを見ていない（例: Bunt/InfieldRight ではファースト以外が捕るとファーストは
        // FieldBall 降格で役割を失い棒立ちになる）。表引き結果の一塁カバーだけ、このマッピングで上書きする
        // （YAML 側の表は初期値として温存。ゾーン×捕球者の全組み合わせを展開しない）。
        var firstBaseCoverer = play.FielderRole == FieldPosition.FirstBase
            ? (zone == BallZone.Bunt ? FieldPosition.SecondBase : FieldPosition.Pitcher)
            : FieldPosition.FirstBase;

        var ball = new List<BallSegment>
        {
            new()
            {
                Kind = BallSegmentKind.Pitch, T0 = ContactAt - PitchDuration, T1 = ContactAt,
                From = Mound, To = Home,
            },
        };

        // 打球: フライは放物線(Flight)、ゴロは地を這うロール(Roll)で表現（mockのゴロと同じ見た目）。
        // 本塁打は IsFly が立たない（FieldingResolver は捕球判定に入る前に確定させて返すため。この
        // フラグはフェンス激突の受傷判定等エンジン側でも参照するので表示のために書き換えない, Issue #300）。
        // 表示だけ Result==HomeRun を見て放物線を選ぶ（着地点は柵の外＝本塁打らしく飛んでいく絵のまま）。
        var landAt = ContactAt + play.HangTimeSeconds;
        var fieldedAt = play.FieldedAtSeconds is { } f ? ContactAt + f : landAt;
        if (play.IsFly || play.Result == BattedBallResult.HomeRun)
        {
            ball.Add(new BallSegment
            {
                Kind = BallSegmentKind.Flight, T0 = ContactAt, T1 = landAt,
                From = Home, To = landing, ApexHeightM = play.ApexHeightM,
            });
            // 抜けた飛球は着地して終わりではない: 着地点→回収点まで転がる（Issue #301②）。
            // 経路・時刻はエンジンの転がり結果（FieldedX/Z・FieldedAtSeconds）をそのまま使い、
            // 表示側で独自の転がりを発明しない。空中捕球（fieldedAt≤landAt）・本塁打・ファウル
            // （回収点＝着地点）は差が出ないので区間を足さない。
            if (fieldedAt > landAt + 1e-9 && Distance(landing, fieldPoint) >= 0.5)
            {
                ball.Add(new BallSegment
                {
                    Kind = BallSegmentKind.Roll, T0 = landAt, T1 = fieldedAt,
                    From = landing, To = fieldPoint,
                });
            }
        }
        else
        {
            // ゴロ/バウンド（Issue #63 の4分類）: 本塁→処理点まで一本で転がす。
            // 「バウンド」分類は高く弾む Bounce（頂点=最大バウンド頂点）、「ゴロ」は地を這う Roll。
            // 処理点はエンジンの回収点（内野なら実捕球点=Issue #261、内野を抜けたなら外野の回収点=#63）。
            // Class 未判定(null)は地を這う Roll 扱い＝分類前の既定と同じ（Issue #315）。
            var groundKind = play.Class == BattedBallClass.Bouncer ? BallSegmentKind.Bounce : BallSegmentKind.Roll;
            ball.Add(new BallSegment
            {
                Kind = groundKind, T0 = ContactAt, T1 = Math.Max(landAt, fieldedAt),
                From = Home, To = fieldPoint, ApexHeightM = play.BounceApexHeightM,
            });
        }

        double resolvedAt;
        var moves = new List<FielderMove>();
        var runners = new List<RunnerLeg>();
        var contactMsg = play.Result == BattedBallResult.HomeRun ? "打った——伸びていく！"
            : play.IsFly ? "打ち上げた！" : "打球が転がる！";
        var captions = new List<TimelineCaption> { new(0.05, "ピッチャー、投げた"), new(ContactAt + 0.01, contactMsg) };

        // 一塁送球（内野ゴロ）。到達時刻(T1)はエンジンの ThrowArriveSeconds のまま固定し、投げ始め(T0)を
        // 「実距離÷肩」の飛行時間から逆算する（Issue #334）。捕球オーバーヘッド＋握り替えは、捕球点で
        // ボールを保持する「間」として自然に吸収される（AddThrowChain と同じ形）。
        if (play.ThrowArriveSeconds is { } throwArrive && play.Result is BattedBallResult.Out or BattedBallResult.Single or BattedBallResult.Error)
        {
            if (!play.IsFly && play.RangeM <= 46.0)
            {
                var firstBase = BasePoint(field, "first");
                var arrive = ContactAt + throwArrive;
                var flight = LegSeconds(Distance(fieldPoint, firstBase), ThrowSpeedOf(play));
                ball.Add(new BallSegment
                {
                    Kind = BallSegmentKind.Throw,
                    T0 = Math.Max(fieldedAt, arrive - flight), T1 = arrive,
                    From = fieldPoint, To = firstBase,
                    FromRole = play.FielderRole, ToRole = firstBaseCoverer, ToBase = "first",
                });
            }
        }

        // 打者走者（常に一塁へ走る。本塁打はダイヤモンド一周の簡略で一塁まで表現）。
        var batterArrive = ContactAt + play.BatterToFirstSeconds;
        var batterOut = play.Result == BattedBallResult.Out && !play.IsFly;
        // 判定オーバーレイ（Issue #59）: 一塁送球が絡んだ判定（force out / 内野安打）のみ際どさを評価。
        var batterCloseCall = play.JudgementMarginSeconds is { } jm && Math.Abs(jm) < closeCallMarginSeconds;
        runners.Add(new RunnerLeg
        {
            Label = "打", T0 = ContactAt + 0.10, T1 = batterArrive,
            From = Home, To = BasePoint(field, "first"), OutAtEnd = batterOut, CloseCall = batterCloseCall,
        });

        // 判定イベント（Issue #260）: 走者レッグの終端に縛られない、確定時刻付きの独立イベント。
        // フライ・ライナー・ファウルフライの捕球アウトは捕球の瞬間（fieldedAt）に確定する。
        // 内野ゴロの一塁フォースは送球到達の瞬間（＝フォース成立。走者到達を待たない, Issue #328①）。
        var judgements = new List<JudgementEvent>();
        if (play.Result == BattedBallResult.HomeRun)
        {
            // 柵越えは着地の瞬間に確定（＝resolvedAt と同じ landAt）。飛距離は判定に使った
            // 着地までの水平距離をそのまま観測データとして載せる（表示専用・Issue #354）。
            judgements.Add(new JudgementEvent
            {
                Label = "打", T = landAt, Kind = JudgementKind.HomeRun, DistanceM = play.RangeM,
            });
        }
        else if (play.IsFly && play.Result == BattedBallResult.Out)
        {
            judgements.Add(new JudgementEvent { Label = "打", T = fieldedAt, Kind = JudgementKind.Out });
        }
        else if (batterOut)
        {
            // 送球が塁に届いた瞬間にアウトが確定する（defenseTime+margin≤runnerTime が保証されているため
            // 常に batterArrive 以前＝走者は判定確定後も一塁まで走り切る絵のまま, Issue #328①）。
            var forceOutAt = play.ThrowArriveSeconds is { } ta ? ContactAt + ta : batterArrive;
            judgements.Add(new JudgementEvent { Label = "打", T = forceOutAt, Kind = JudgementKind.Out });
        }
        else if (batterCloseCall)
        {
            judgements.Add(new JudgementEvent { Label = "打", T = batterArrive, Kind = JudgementKind.Safe, CloseCall = true });
        }
        else if (play.IsFly && play.CatchMarginSeconds is { } catchMargin && Math.Abs(catchMargin) < closeFlyMarginSeconds)
        {
            // 野手が追いついていた際どい飛球が落ちた瞬間に「フェア」を出す（Issue #328②）。
            // 完全に抜けた当たり（|余裕|が閾値超）は対象外＝しきい値がそのままゲート。
            judgements.Add(new JudgementEvent { Label = "打", T = landAt, Kind = JudgementKind.Fair });
        }

        // 結果確定時刻: アウト/送球到達/野手処理/着地のうちプレーを決めた時刻。
        resolvedAt = play.Result switch
        {
            BattedBallResult.HomeRun => landAt,
            BattedBallResult.Foul => landAt,
            BattedBallResult.Out when play.IsFly => fieldedAt,
            BattedBallResult.Out => ContactAt + (play.ThrowArriveSeconds ?? play.FieldedAtSeconds ?? play.HangTimeSeconds),
            BattedBallResult.Single when play.ThrowArriveSeconds is not null => batterArrive, // 内野安打は駆け込み
            _ => fieldedAt,
        };

        var rule = table.Lookup(zone, runnersOn);
        var returnTail = 0.0; // 定位置復帰レッグの最終時刻（余韻を延ばすため）
        if (rule is not null)
        {
            // 実際に処理/捕球する野手（FielderRole）は、ゾーン役割に関わらず必ずこの1人だけがボール(landing)へ
            // 向かい、そこで保持する（戻らない）。他の FieldBall 割当は降格させ、ボールへ突っ込ませない。
            // これで「浅いフライを内野手が捕るのにゾーンが外野扱いで CF がボールへ来る／実捕球者が持ち場へ戻り
            // ボールが放置される」不一致を防ぐ（#8＋セカンドフライ問題）。
            var ballFielder = play.FielderRole;
            var ballFielderMoved = false;
            // 実捕球者がいない打球（本塁打・ファウル等, FielderRole=null）で FieldBall 役に割り当てられた
            // 野手の移動先。フェンスの外までは追わせず、フェンス際で止めて見送らせる（Issue #300）。
            // ボールそのものの描画（Flight/Roll セグメント）は着地点(landing)のまま＝柵越えの絵は保つ。
            var fieldBallTarget = ballFielder is null ? ClampToFence(landing, field, play.BearingDeg) : landing;

            var firstBaseCovered = false;
            // 内野を抜けた打球で最も惜しかった内野手（Issue #336②）。この1人だけ、通常の役割移動の前に
            // 「打球ラインへ寄る/飛びつく」レッグを1本挟む。ThroughInfield でない打球では常に null。
            var missRole = play.ThroughInfield ? play.ClosestInfielderRole : null;

            foreach (var a in rule.Assignments)
            {
                var isBallFielder = ballFielder is { } bf && a.Position == bf;

                // 実捕球者でない FieldBall はボールへ行かせない（二重突進・誤処理を防ぐ）。
                if (a.Task == FielderTask.FieldBall && ballFielder is not null && !isBallFielder)
                    continue;
                if (a.Task == FielderTask.Hold && !isBallFielder) continue; // 待機は移動なし

                // 表の一塁カバー担当が動的な担当と一致しないなら押し出し＝持ち場待機（移動を生成しない）。
                // 実捕球者自身の行（ボールへ向かう）は対象外。
                if (a.Task == FielderTask.CoverBase && a.Target == "first" && !isBallFielder
                    && a.Position != firstBaseCoverer)
                    continue;

                var fielder = Find(fielders, a.Position);
                if (fielder is null) continue;

                var from = new TimelinePoint(fielder.Location.X, fielder.Location.Z);

                // 寄る/飛びつくレッグ（Issue #336②・表示専用）: この野手が最も惜しかった内野手なら、
                // 反応時間（実測 ReactionDelaySeconds）から打球の最接近点へ向かうレッグを先に積む。
                // 変位は CapDisplacement で上限を掛け、実際には届かせない（捕れなかった打球のため）。
                // 通常の役割移動(t0)はこのレッグの後ろへ押し出す＝時系列の逆転（後発レッグが先に
                // 表示される描画バグ）を防ぐ。
                TimelinePoint? diveEnd = null;
                var diveT1 = 0.0;
                if (!isBallFielder && missRole == a.Position)
                {
                    var rawTarget = new TimelinePoint(
                        play.ClosestApproachX ?? from.X, play.ClosestApproachZ ?? from.Z);
                    var diveTarget = CapDisplacement(from, rawTarget, DiveReachCapM);
                    var diveDist = Distance(from, diveTarget);
                    if (diveDist >= 0.5)
                    {
                        var diveSpeed = Math.Max(4.0, fielder.Attributes.SprintSpeedMps);
                        var diveT0 = ContactAt + fielder.Attributes.ReactionDelaySeconds;
                        diveT1 = diveT0 + diveDist / diveSpeed;
                        moves.Add(new FielderMove
                        {
                            Role = a.Position, T0 = diveT0, T1 = diveT1, To = diveTarget, Task = FielderTask.Chase,
                        });
                        diveEnd = diveTarget;
                    }
                }

                // 実捕球者はゾーン役割を上書きしてボールへ（保持＝戻さない）。
                var task = isBallFielder ? FielderTask.FieldBall : a.Task;
                var to = isBallFielder ? fieldPoint : ResolveTarget(a, fieldPoint, field, from, fieldBallTarget);
                if (isBallFielder) ballFielderMoved = true;
                if (task == FielderTask.CoverBase && a.Target == "first") firstBaseCovered = true;

                var moveFrom = diveEnd ?? from;
                var dist = Distance(moveFrom, to);
                if (dist < 0.5) continue;

                var speed = Math.Max(4.0, fielder.Attributes.SprintSpeedMps);
                var t0 = diveEnd is not null ? Math.Max(ContactAt + 0.35, diveT1 + 0.1) : ContactAt + 0.35; // 反応
                var t1 = t0 + dist / speed;
                if (isBallFielder)
                {
                    (t0, t1) = ArriveByFieldedAt(t0, t1, fieldedAt);
                    to = StopWithinReach(moveFrom, to, speed * (t1 - t0), fielderReachM);
                }
                moves.Add(new FielderMove { Role = a.Position, T0 = t0, T1 = t1, To = to, Task = task });

                // バックアップ/中継で持ち場を離れた野手は、決着後に定位置へ戻る（"寄ったまま"を防ぐ, #2）。
                // ⚠ 戻り先は**出発した場所**（＝判定が距離を測った FieldGeometry の守備位置）。以前は
                // FieldDiagramGeometry.MatchFielderPosition（モック由来の表）へ戻しており、出発と帰着が
                // 別の表＝戻りレッグの長さが最大10m嘘になっていた。
                if (task is FielderTask.Backup or FielderTask.Cutoff)
                {
                    var homePost = from;
                    var backDist = Distance(to, homePost);
                    if (backDist >= 0.5)
                    {
                        var rt0 = Math.Max(resolvedAt, t1) + 0.3;
                        var rt1 = rt0 + backDist / speed;
                        moves.Add(new FielderMove { Role = a.Position, T0 = rt0, T1 = rt1, To = homePost, Task = task });
                        if (rt1 + 0.5 > returnTail) returnTail = rt1 + 0.5;
                    }
                }
            }

            // FielderRole がこのゾーンの割当に無い（例: 外野ゾーンで内野手が浅いフライを捕る）→明示的にボールへ。
            if (ballFielder is { } bfr && !ballFielderMoved)
            {
                var fielder = Find(fielders, bfr);
                if (fielder is not null)
                {
                    var from = new TimelinePoint(fielder.Location.X, fielder.Location.Z);
                    var dist = Distance(from, fieldPoint);
                    if (dist >= 0.5)
                    {
                        var speed = Math.Max(4.0, fielder.Attributes.SprintSpeedMps);
                        var (t0, t1) = ArriveByFieldedAt(ContactAt + 0.35, ContactAt + 0.35 + dist / speed, fieldedAt);
                        var stop = StopWithinReach(from, fieldPoint, speed * (t1 - t0), fielderReachM);
                        moves.Add(new FielderMove { Role = bfr, T0 = t0, T1 = t1, To = stop, Task = FielderTask.FieldBall });
                    }
                }
            }

            // 一塁カバーが表に無かった（実捕球者に押し出された）→動的な担当を注入する（Issue #257）。
            // 例: Bunt/InfieldRight でファースト以外が捕った場合、静的表のファーストの行は FieldBall
            // （＝実捕球者でなければ丸ごと降格）なので、このままでは誰も一塁に入らない。
            if (!firstBaseCovered)
            {
                var coverer = Find(fielders, firstBaseCoverer);
                if (coverer is not null)
                {
                    var from = new TimelinePoint(coverer.Location.X, coverer.Location.Z);
                    var to = BasePoint(field, "first");
                    var dist = Distance(from, to);
                    if (dist >= 0.5)
                    {
                        var speed = Math.Max(4.0, coverer.Attributes.SprintSpeedMps);
                        var t0 = ContactAt + 0.35;
                        moves.Add(new FielderMove { Role = firstBaseCoverer, T0 = t0, T1 = t0 + dist / speed, To = to, Task = FielderTask.CoverBase });
                    }
                }
            }

            // 寄る/飛びつくレッグの仕上げ（Issue #336②）: 最も惜しかった内野手は、ゾーン役割が
            // CoverBase 等（自動の定位置復帰を持たない）でも、決着後は必ず定位置へ戻り切る。
            // 既に Backup/Cutoff の自動復帰で定位置に着いている場合は距離0扱いで二重には積まない。
            if (play.ThroughInfield && play.ClosestInfielderRole is { } missRoleFinal)
            {
                FielderMove? lastMove = null;
                foreach (var m in moves)
                {
                    if (m.Role == missRoleFinal) lastMove = m;
                }
                if (lastMove is { } lm)
                {
                    var missFielder = Find(fielders, missRoleFinal);
                    // 戻り先は出発した場所（判定が距離を測ったのと同じ守備位置）。上の Backup/Cutoff と同じ理由。
                    var homePost = missFielder is null
                        ? lm.To
                        : new TimelinePoint(missFielder.Location.X, missFielder.Location.Z);
                    var backDist = Distance(lm.To, homePost);
                    if (backDist >= 0.5)
                    {
                        if (missFielder is not null)
                        {
                            var speed = Math.Max(4.0, missFielder.Attributes.SprintSpeedMps);
                            var rt0 = Math.Max(resolvedAt, lm.T1) + 0.3;
                            var rt1 = rt0 + backDist / speed;
                            moves.Add(new FielderMove
                            {
                                Role = missRoleFinal, T0 = rt0, T1 = rt1, To = homePost, Task = FielderTask.Chase,
                            });
                            if (rt1 + 0.5 > returnTail) returnTail = rt1 + 0.5;
                        }
                    }
                }
            }
        }

        // ゴロ処理の中間実況（捕球→送球/生還の間）。フライ捕球は結果=捕球なので中間は出さない。
        if (!play.IsFly && play.FielderRole is { } captured && fieldedAt < resolvedAt - 0.05)
        {
            captions.Add(new TimelineCaption(fieldedAt, $"{PositionName(captured)}が捕った"));
        }
        captions.Add(new TimelineCaption(resolvedAt, resultText));

        var duration = Math.Max(Math.Max(resolvedAt, batterArrive) + 1.2, returnTail); // 余韻＋復帰
        // 回収がいちばん遅いことがある（打球が野手から離れて転がり、追いついたのが決着より後）。
        // ボール区間を尺の外に置くと再生が途中で切れる＝必ず覆う。
        foreach (var b in ball)
        {
            if (b.T1 + 0.6 > duration) duration = b.T1 + 0.6;
        }
        return new PlayTimeline
        {
            Duration = duration,
            Result = resultText,
            ResolvedAt = resolvedAt,
            FielderStarts = StartsOf(fielders),
            Ball = ball,
            Moves = moves,
            Runners = runners,
            Captions = captions,
            Judgements = judgements,
        };
    }

    /// <summary>
    /// ファウル打球の短い再生タイムライン（Issue #313）。物理層（初速・角度・方向）が実際に生成された
    /// ファウルだけが対象（<see cref="AtBat.PitchRecord.BattedBall"/> 非null）。守備側は一切解決しない
    /// （ボールデッド＝アウト・進塁・失策は起こり得ない）ので、送球・守備陣形・判定オーバーレイは
    /// 一切生成しない「投球→滞空/転がり→静止」だけの最小タイムライン。
    ///
    /// 着地点・滞空時間・頂点高さは <see cref="BallisticIntegrator.IntegrateToGround"/> を打球資料に
    /// そのまま適用して求める（守備解決＝<see cref="FieldingResolver.ResolveDetailed"/> と同じ積分の使い回し。
    /// 乱数は一切消費しない＝決定論・帯に無関係、不変条件#2）。塁上の走者は（動かないので）その場に
    /// 静止表示する（<see cref="PitchPlaybackFactory.PitchOnly"/> と同じ扱い）。
    /// </summary>
    public static PlayTimeline BuildFoulFlight(
        BattedBall ball, Aerodynamics aero, FieldingCoefficients coeff, FieldGeometry field,
        bool runnerOnFirst, bool runnerOnSecond, bool runnerOnThird)
    {
        var ground = BallisticIntegrator.IntegrateToGround(
            ball.ContactPoint(), ball.InitialVelocity(), ball.SpinVector(), aero);
        var landing = new TimelinePoint(ground.LandingPosition.X, ground.LandingPosition.Z);
        var isFly = ground.ApexHeightM >= coeff.FlyApexThresholdM;
        var landAt = ContactAt + ground.HangTimeSeconds;
        var duration = landAt + 0.6; // 着地後の一呼吸（対決ビュー復帰までの間）

        var segments = new List<BallSegment>
        {
            new()
            {
                Kind = BallSegmentKind.Pitch, T0 = ContactAt - PitchDuration, T1 = ContactAt,
                From = Mound, To = Home,
            },
            new()
            {
                Kind = isFly ? BallSegmentKind.Flight : BallSegmentKind.Roll,
                T0 = ContactAt, T1 = landAt, From = Home, To = landing, ApexHeightM = ground.ApexHeightM,
            },
        };

        var runners = new List<RunnerLeg>();
        void Held(bool occupied, string baseName, string label)
        {
            if (!occupied) return;
            var pt = BasePoint(field, baseName);
            runners.Add(new RunnerLeg { Label = label, T0 = 0, T1 = duration, From = pt, To = pt, OutAtEnd = false });
        }
        Held(runnerOnFirst, "first", "走一");
        Held(runnerOnSecond, "second", "走二");
        Held(runnerOnThird, "third", "走三");

        return new PlayTimeline
        {
            Duration = duration,
            Result = "ファウル",
            ResolvedAt = landAt,
            FielderStarts = StandardStarts(),
            Ball = segments,
            Runners = runners,
            Captions = new[] { new TimelineCaption(ContactAt + 0.01, "ファウル！") },
        };
    }

    /// <summary>盗塁プレーのタイムライン（mock-steal-2d-view.html と同構造）。乱数不使用。</summary>
    public static PlayTimeline BuildSteal(
        Player runner, Player catcher, StealResult result, BaserunningCoefficients c, FieldGeometry field)
    {
        var runnerTime = StealResolver.RunnerTimeSeconds(runner, c);
        var defenseTime = StealResolver.DefenseTimeSeconds(catcher, c);

        var first = BasePoint(field, "first");
        var second = BasePoint(field, "second");
        var safe = result == StealResult.Safe;

        var pitchStart = 0.0;
        var pitchArrive = c.PitcherQuickSeconds;                       // クイック（投球が捕手へ）
        var throwStart = pitchArrive + c.PopTransferSeconds;           // 握り替え
        var throwArrive = defenseTime - c.TagSeconds;                  // 二塁到達
        var runnerArrive = runnerTime;

        var resolvedAt = Math.Max(runnerArrive, defenseTime);
        return new PlayTimeline
        {
            Duration = resolvedAt + 1.2,
            Result = safe ? "盗塁成功" : "盗塁死",
            ResolvedAt = resolvedAt,
            FielderStarts = StandardStarts(),
            Ball = new[]
            {
                new BallSegment { Kind = BallSegmentKind.Pitch, T0 = pitchStart, T1 = pitchArrive, From = Mound, To = Home },
                // 受け手は下の Moves で二塁カバーに入る遊撃手と同一（Issue E1・単一ソース）
                new BallSegment
                {
                    Kind = BallSegmentKind.Throw, T0 = throwStart, T1 = throwArrive, From = Home, To = second,
                    FromRole = FieldPosition.Catcher, ToRole = FieldPosition.Shortstop, ToBase = "second",
                },
            },
            Moves = new[]
            {
                // 遊撃手が二塁ベースカバーへ（陣形ルールの最小適用）。
                new FielderMove { Role = FieldPosition.Shortstop, T0 = 0.3, T1 = Math.Max(0.9, throwArrive - 0.2), To = second, Task = FielderTask.CoverBase },
            },
            Runners = new[]
            {
                new RunnerLeg { Label = "走", T0 = 0.0, T1 = runnerArrive, From = first, To = second, OutAtEnd = !safe },
            },
            // 判定イベント（Issue #260）: 盗塁死は当該レッグ終端で確定。セーフは際どさを未評価のため
            // 積まない（表示条件は #59 のまま維持＝従来もセーフの盗塁は判定オーバーレイの対象外）。
            Judgements = safe
                ? Array.Empty<JudgementEvent>()
                : new[] { new JudgementEvent { Label = "走", T = runnerArrive, Kind = JudgementKind.Out } },
            Captions = new[]
            {
                new TimelineCaption(0.05, "スタートを切った！"),
                new TimelineCaption(throwStart, "キャッチャー、二塁へ送球——"),
                new TimelineCaption(resolvedAt, safe ? "セーフ！ 盗塁成功" : "タッチアウト！ 盗塁死"),
            },
        };
    }

    /// <summary>
    /// 走塁結果（BaserunningModel.ApplyDetailed）をタイムラインの走者レッグへ変換して合成する。
    /// 打者走者は既定レッグ（本塁→一塁）があるため、二塁打以降の続きのレッグだけ追加する。乱数不使用。
    /// 塁上から動く走者には、移動開始までその塁に立たせる待機レッグ（T0=0→移動開始, From=To=元の塁）を
    /// 先頭に足す（#25）。これが無いと再生層 <c>PlaybackEvaluator.RunnerAt</c> は最初のレッグ開始前に
    /// null を返し、かつ <see cref="AppendHeldRunners"/> も「動いた塁」を除外するため、フォース進塁する
    /// 走者が接触時刻まで盤面から消える（＝塁上に居ないように見える）。表示専用・結果不変。
    /// </summary>
    public static PlayTimeline AppendRunnerLegs(
        PlayTimeline timeline, IReadOnlyList<RunnerMove> moves, FieldGeometry field,
        BaserunningCoefficients baserunning, FieldingCoefficients fielding)
    {
        if (moves.Count == 0) return timeline;

        var legs = new List<RunnerLeg>(timeline.Runners);
        var judgements = new List<JudgementEvent>(timeline.Judgements);
        var runnerIndex = 0;
        foreach (var m in moves)
        {
            // 走者の時計は判定と共通（Issue #316）。打席から走り出す打者走者と、塁上でリードを
            // 取ってから走る走者は物理が違うので、式ではなく文脈で分ける。以前はここだけ
            // 「27.43/速度 + 0.35」という第4の式を持っていて、判定より 0.58〜1.63 秒遅かった。
            var fromBox = m.FromBase == 0;
            var speed = m.Runner.ToFielder().SprintSpeedMps;
            var ctx = fromBox
                ? ExtraBaseResolver.BatterContextOf(fielding)
                : HomePlayResolver.RunnerContextOf(m.Runner, baserunning);

            var label = fromBox ? "打" : $"走{++runnerIndex}";
            var startBase = fromBox ? 1 : m.FromBase;          // 打者は一塁到達後の続きから
            var t = fromBox
                ? FindBatterArrive(timeline)                   // 一塁到達時刻から続ける
                : ContactAt + ctx.StartDelaySeconds + m.ExtraStartDelaySeconds;

            if (fromBox && m.ToBase <= 1) continue;            // 打者の一塁までは既定レッグ

            // 二次リード（<see cref="RunnerContext.LeadDistanceM"/>）。時計は塁間からリードぶんを**引いた**
            // 距離で回っているので、絵でも次塁側へ出しておかないと「引いた距離ぶんの時間で満距離を走る」
            // ことになる（実測: 塁間27.43mを2.87秒＝9.56 m/s。リードを描くと 8.34 m/s＝脚力×1.15 ちょうど）。
            // 引くのは移動全体で1回（RunnerClock が出発塁からの累積で引くのと同じ）＝最初のレッグだけ。
            var lead = fromBox ? 0.0 : Math.Max(0.0, ctx.LeadDistanceM);

            // 塁上の走者は移動開始まで元の塁に立たせる（待機レッグ）。打者走者は本塁の既定レッグがあるため対象外。
            // 投球の間にリードを取るので、待機レッグの終端はリード位置にする。
            if (m.FromBase >= 1 && m.ToBase > m.FromBase && t > 0)
            {
                var stand = BasePoint(field, BaseName(m.FromBase));
                var leadPt = LeadPoint(field, m.FromBase, lead);
                legs.Add(new RunnerLeg { Label = label, T0 = 0, T1 = t, From = stand, To = leadPt, OutAtEnd = false });
            }

            for (var b = startBase; b < m.ToBase; b++)
            {
                var from = BaseName(b);
                var to = BaseName(b + 1);
                // 打者走者は塁打数判定と同じ「1塁ぶんを積む」書き方、塁上の走者は本塁レースと同じ
                // 「出発塁からの累積」。どちらも判定側とまったく同じ数式・同じ順序なので、
                // 最終レッグの終端がそのまま判定に使われた到達時刻になる。
                // （累積で書くのは二次リードを塁ごとに二重に引かないため）
                var t1 = fromBox
                    ? t + RunnerClock.SegmentSeconds(speed, b, b + 1, ctx, field)
                    : ContactAt + RunnerClock.SegmentSeconds(
                        speed, m.FromBase, b + 1, ctx, field, m.ExtraStartDelaySeconds);
                legs.Add(new RunnerLeg
                {
                    Label = label,
                    T0 = t,
                    T1 = t1,
                    // 最初のレッグだけリード位置から（2本目以降は塁を踏んで出るので満距離）
                    From = b == startBase && lead > 0
                        ? LeadPoint(field, b, lead)
                        : BasePoint(field, from),
                    To = b + 1 >= 4 ? Home : BasePoint(field, to),
                    OutAtEnd = m.Out && b + 1 == m.ToBase,
                    // 判定オーバーレイ（Issue #59）: CloseCall は決着レッグ（この移動の最終区間）にだけ乗せる。
                    CloseCall = m.CloseCall && b + 1 == m.ToBase,
                });
                // 判定イベント（Issue #260）: 走塁死・本塁クロスプレー等は決着レッグの終端で確定。
                // セーフは際どい判定のときだけ積む（表示条件は #59 のまま維持）。
                if (b + 1 == m.ToBase && (m.Out || m.CloseCall))
                {
                    judgements.Add(new JudgementEvent
                    {
                        Label = label, T = t1,
                        Kind = m.Out ? JudgementKind.Out : JudgementKind.Safe,
                        CloseCall = m.CloseCall,
                    });
                }
                t = t1;
            }
        }

        var maxT = timeline.Duration;
        foreach (var l in legs)
        {
            if (l.T1 + 1.0 > maxT) maxT = l.T1 + 1.0;
        }
        return timeline with { Runners = legs, Duration = maxT, Judgements = judgements };
    }

    /// <summary>
    /// 打席前に塁上にいて、この打球で動かなかった走者の静止トークンを足す（#3）。
    /// 移動レッグを持つ塁（走者が動いた）は二重に描かない。ラベルは移動走者("打"/"走1"…)と衝突しない
    /// "走一/走二/走三"。T0=0,T1=Duration の0長レッグ＝定点表示。結果不変・乱数不使用の表示専用。
    ///
    /// 2アウトのフライアウト（#299）はタッチアップ不要のため例外: 静止させず、接触直後
    /// （<see cref="AppendRunnerLegs"/> と同じ ContactAt+0.10）から走者速度で次塁方向へスタートさせ、
    /// 捕球時刻（イニング終了）でその位置に止める（帰塁の描画はしない）。0/1アウトのフライ・
    /// ゴロアウトは従来どおり静止のまま（issue の「判断が要る点」によりフライの2アウトだけに限定）。
    /// </summary>
    public static PlayTimeline AppendHeldRunners(
        PlayTimeline timeline, bool first, bool second, bool third,
        Player? firstPlayer, Player? secondPlayer, Player? thirdPlayer,
        IReadOnlyList<RunnerMove> moves, FieldGeometry field,
        int outsBefore, FieldingPlay? play, BaserunningCoefficients baserunning)
    {
        var movedFrom = new HashSet<int>();
        foreach (var m in moves) movedFrom.Add(m.FromBase); // 1/2/3（0=打者）

        var autoRun = outsBefore == 2 && play is { IsFly: true, Result: BattedBallResult.Out };
        var catchAt = autoRun ? ContactAt + (play!.FieldedAtSeconds ?? play.HangTimeSeconds) : 0.0;

        var legs = new List<RunnerLeg>(timeline.Runners);
        void AddHeld(int b, bool occupied, Player? runner, string name, string nextName, string label)
        {
            if (!occupied || movedFrom.Contains(b)) return;
            var pt = BasePoint(field, name);
            if (autoRun && runner is not null)
            {
                // 走者の時計は判定と共通（Issue #316）。塁上からのスタートなので
                // AppendRunnerLegs の「塁上から」文脈とまったく同じ扱いにする。
                var ctx = HomePlayResolver.RunnerContextOf(runner, baserunning);
                var t0 = ContactAt + ctx.StartDelaySeconds;
                // 二次リードは AppendRunnerLegs と同じ扱い（時計が塁間から引いている距離を絵にも乗せる）。
                var leadPt = LeadPoint(field, b, Math.Max(0.0, ctx.LeadDistanceM));
                legs.Add(new RunnerLeg { Label = label, T0 = 0, T1 = t0, From = pt, To = leadPt, OutAtEnd = false });
                if (catchAt > t0)
                {
                    var speed = runner.ToFielder().SprintSpeedMps;
                    var perBase = RunnerClock.SegmentSeconds(speed, b, b + 1, ctx, field) - ctx.StartDelaySeconds;
                    var frac = Math.Clamp((catchAt - t0) / perBase, 0.0, 1.0);
                    var next = BasePoint(field, nextName);
                    // 進むのは「リード位置から次塁まで」＝時計が持っている距離。塁からの満距離で刻むと
                    // リードぶんだけ速く走ることになる。
                    var stop = new TimelinePoint(
                        leadPt.X + (next.X - leadPt.X) * frac, leadPt.Z + (next.Z - leadPt.Z) * frac);
                    legs.Add(new RunnerLeg { Label = label, T0 = t0, T1 = catchAt, From = leadPt, To = stop, OutAtEnd = false });
                }
                return;
            }
            legs.Add(new RunnerLeg { Label = label, T0 = 0, T1 = timeline.Duration, From = pt, To = pt, OutAtEnd = false });
        }
        AddHeld(1, first, firstPlayer, "first", "second", "走一");
        AddHeld(2, second, secondPlayer, "second", "third", "走二");
        AddHeld(3, third, thirdPlayer, "third", "home", "走三");

        if (legs.Count == timeline.Runners.Count) return timeline;
        return timeline with { Runners = legs };
    }

    /// <summary>処理野手の肩が不明なとき（FielderRole なし等）に使う送球速度[m/s]。表示専用のフォールバック。</summary>
    private const double FallbackThrowSpeedMps = 31.0;
    /// <summary>併殺の二塁での握り替え[s]（表示専用の演出値。判定/帯に一切干渉しない）。</summary>
    private const double DpTransferSeconds = 0.15;

    /// <summary>
    /// 送球を描く速度[m/s]＝処理野手の肩（<see cref="FieldingPlay.FielderThrowSpeedMps"/>）。
    /// 従来は定数31m/s固定で、能力値がタイムラインに一切反映されていなかった（Issue #301③）。
    /// </summary>
    private static double ThrowSpeedOf(FieldingPlay play)
        => play.FielderThrowSpeedMps is { } v && v > 1.0 ? v : FallbackThrowSpeedMps;

    /// <summary>
    /// 判定との矛盾を防ぐために送球到達をずらす最小の間[s]（Issue #301③ 方針「物理優先＋矛盾ガード」）。
    /// </summary>
    private const double VerdictGuardLagSeconds = 0.20;

    /// <summary>
    /// 送球到達時刻を「物理どおり」を基本に、**エンジンの確定判定と矛盾するときだけ**ずらす
    /// （Issue #301③ で選択した方針）。判定はロジスティック抽選なので、物理レースの勝敗と
    /// 確定結果が食い違うことがある（＝際どい場面で運が動いた）。そのときだけ絵を譲る。
    /// - セーフ確定: 送球が走者より先に着く絵にしない（走者到達＋わずかな間へ後ろへずらす）
    /// - アウト確定: 送球が走者より後に着く絵にしない（タッチの瞬間まで前へ詰める）
    /// どちらも <paramref name="earliestArrive"/>（「間」ゼロで飛ばしきった最速）より前には詰めない。
    /// </summary>
    private static double GuardAgainstVerdict(
        double physicalArrive, double earliestArrive, double runnerArriveAnchor, bool runnerOut)
    {
        if (runnerOut)
        {
            // アウトなのに送球が遅れて見える → タッチの瞬間に合わせる（「間」ゼロより前へは詰めない）。
            return physicalArrive <= runnerArriveAnchor
                ? physicalArrive
                : Math.Max(earliestArrive, runnerArriveAnchor);
        }
        // セーフなのに送球が先着して見える → 走者到達の直後へ回す。
        return Math.Max(physicalArrive, runnerArriveAnchor + VerdictGuardLagSeconds);
    }

    /// <summary>送球1本の所要時間[s]（実際に描く距離÷その送球の速度＝画面上の速度が肩どおりになる）。</summary>
    private static double LegSeconds(double distanceM, double speedMps) => distanceM / Math.Max(1.0, speedMps);

    /// <summary>
    /// 送球連鎖のボール区間を組む（直接返球1本 or 中継2本）。各レッグは実距離÷実速度なので画面上の
    /// 速度は常に肩どおり。ガードで到達が後ろへずれた分の「間」は、中継なら**カットマンがボールを持つ
    /// 時間**、直接返球なら**処理野手が持つ時間**として吸収する（保持中は BallAt が位置を保つ）。
    /// </summary>
    private static void AddThrowChain(
        List<BallSegment> ball, TimelinePoint from, TimelinePoint target, TimelinePoint? cutoff,
        double releaseAt, double arriveAt, double leg1Seconds, double leg2Seconds,
        FieldPosition? fromRole = null, FieldPosition? cutoffRole = null,
        FieldPosition? toRole = null, string? toBase = null)
    {
        if (cutoff is { } cut)
        {
            ball.Add(new BallSegment
            {
                Kind = BallSegmentKind.Throw, T0 = releaseAt, T1 = releaseAt + leg1Seconds,
                From = from, To = cut,
                FromRole = fromRole, ToRole = cutoffRole,   // 中継地点は塁ではないので ToBase は付けない
            });
            ball.Add(new BallSegment
            {
                Kind = BallSegmentKind.Throw, T0 = arriveAt - leg2Seconds, T1 = arriveAt,
                From = cut, To = target,
                FromRole = cutoffRole, ToRole = toRole, ToBase = toBase,
            });
            return;
        }
        ball.Add(new BallSegment
        {
            Kind = BallSegmentKind.Throw, T0 = arriveAt - leg1Seconds, T1 = arriveAt,
            From = from, To = target,
            FromRole = fromRole, ToRole = toRole, ToBase = toBase,
        });
    }

    /// <summary>
    /// タイムラインに既に置かれている中継(カットオフ)野手の**役**（<see cref="FielderTask.Cutoff"/>）。
    /// 座標側の <see cref="FindCutoffFielderPoint"/> と同じ割当を見る＝位置と役が食い違わない（Issue E1）。
    /// </summary>
    private static FieldPosition? FindCutoffFielderRole(PlayTimeline timeline)
    {
        foreach (var m in timeline.Moves)
        {
            if (m.Task == FielderTask.Cutoff) return m.Role;
        }
        return null;
    }

    /// <summary>
    /// その地点の塁カバーに入る野手の役（Issue E1）。陣形が実際に積んだ <see cref="FielderTask.CoverBase"/>
    /// の割当を単一ソースにし、割当が無い（既にその場に居て移動が発生しない）ときだけ
    /// <paramref name="fallback"/> の定位置担当へ落とす。
    /// </summary>
    private static FieldPosition CoverRoleAt(PlayTimeline timeline, TimelinePoint at, FieldPosition fallback)
    {
        foreach (var m in timeline.Moves)
        {
            if (m.Task == FielderTask.CoverBase && Distance(m.To, at) < 1.0) return m.Role;
        }
        return fallback;
    }

    /// <summary>
    /// 併殺(6-4-3型)の送球連鎖を描く（設計書12 §2, F1 Slice 2）。既存の「野手→一塁」1本 Throw を、
    /// 「野手処理点→二塁→一塁」の2本 Throw に差し替える。結果不変・乱数不使用の表示専用。
    /// 内野ゴロ(!IsFly かつ既存の一塁送球あり)以外は何もせず元のタイムラインをそのまま返す。
    /// batterArriveAnchor=打者走者の一塁到達時刻[s]（2本目の到達をここへ合わせる。物理下限は保持）。
    /// </summary>
    public static PlayTimeline AppendDoublePlayThrows(
        PlayTimeline timeline, FieldingPlay play, FieldGeometry field, double batterArriveAnchor)
    {
        if (play.IsFly || play.RangeM > 46.0) return timeline; // 内野ゴロ以外は対象外

        // 既存の送球（野手→一塁）を1本だけ除去。無ければ差し替え不能なので元を返す。
        BallSegment? existingThrow = null;
        var ball = new List<BallSegment>(timeline.Ball.Count + 1);
        foreach (var seg in timeline.Ball)
        {
            if (seg.Kind == BallSegmentKind.Throw && existingThrow is null)
            {
                existingThrow = seg;
                continue;
            }
            ball.Add(seg);
        }
        if (existingThrow is null) return timeline;

        var fieldPoint = FieldPointOf(play);                        // ロール終端＝実捕球点（Issue #301①）
        var second = BasePoint(field, "second");
        var first = BasePoint(field, "first");

        var arm = ThrowSpeedOf(play);                              // 処理野手の肩（Issue #301③）
        // 野手処理時刻は play から直接出す（Issue #334: 差し替え前の Throw の T0 はもう捕球時刻と
        // 一致しない＝一塁向け送球の投げ始めに変わったので、existingThrow.T0 を流用できない）。
        var fieldedAt = ContactAt + (play.FieldedAtSeconds ?? play.HangTimeSeconds);
        var releaseAt = fieldedAt + DpTransferSeconds;              // 処理点で握り替えてから投げ始める
        var secondArrive = releaseAt + Distance(fieldPoint, second) / arm;
        var pivotAt = secondArrive + DpTransferSeconds;             // 二塁で握り替え
        // 一塁到達は打者到達へアンカー（＝際どく封殺）。ただし物理的な下限は割らない。
        var firstArrive = Math.Max(batterArriveAnchor, pivotAt + Distance(second, first) / arm);

        // 送球の投げ手／受け手（Issue E1）。**併殺はここが最近傍マッチの壊れる場所**＝
        // 二塁の周りに遊撃手・二塁手・走者が同時に居るので、座標だけでは中継役を取り違える。
        var pivotRole = CoverRoleAt(timeline, second, FieldPosition.SecondBase);
        var firstRole = CoverRoleAt(timeline, first, FieldPosition.FirstBase);
        ball.Add(new BallSegment
        {
            Kind = BallSegmentKind.Throw, T0 = releaseAt, T1 = secondArrive,
            From = fieldPoint, To = second,
            FromRole = play.FielderRole, ToRole = pivotRole, ToBase = "second",
        });
        ball.Add(new BallSegment
        {
            Kind = BallSegmentKind.Throw, T0 = pivotAt, T1 = firstArrive,
            From = second, To = first,
            FromRole = pivotRole, ToRole = firstRole, ToBase = "first",
        });

        // 二塁封殺の判定時刻を走者到達ではなく実際の送球到達へ差し替える（Issue #328①）。
        // 送球のほうが先に着くのが通常（DP成立の前提）なので、走者レッグもその時刻・位置で止め、
        // 判定確定後に走者だけ塁上まで走り切る絵にしない（送球が後着の稀なケースは物理無矛盾の
        // 保証が無いため、判定時刻だけ差し替えてレッグは弄らない）。
        var runners = timeline.Runners;
        var judgements = timeline.Judgements;
        foreach (var leg in timeline.Runners)
        {
            if (!leg.OutAtEnd || leg.Label == "打" || Distance(leg.To, second) >= 0.5) continue;

            if (secondArrive < leg.T1 - 1e-9)
            {
                var frac = leg.T1 > leg.T0 ? Math.Clamp((secondArrive - leg.T0) / (leg.T1 - leg.T0), 0.0, 1.0) : 0.0;
                var stop = new TimelinePoint(
                    leg.From.X + (leg.To.X - leg.From.X) * frac, leg.From.Z + (leg.To.Z - leg.From.Z) * frac);
                var updatedRunners = new List<RunnerLeg>(runners.Count);
                foreach (var r in runners)
                    updatedRunners.Add(ReferenceEquals(r, leg) ? r with { T1 = Math.Max(leg.T0, secondArrive), To = stop } : r);
                runners = updatedRunners;
            }

            var updatedJudgements = new List<JudgementEvent>(judgements.Count);
            foreach (var j in judgements)
            {
                updatedJudgements.Add(
                    j.Label == leg.Label && j.Kind == JudgementKind.Out && Math.Abs(j.T - leg.T1) < 1e-9
                        ? j with { T = secondArrive }
                        : j);
            }
            judgements = updatedJudgements;
            break;
        }

        var duration = Math.Max(timeline.Duration, firstArrive + 1.0);
        return timeline with { Ball = ball, Duration = duration, Runners = runners, Judgements = judgements };
    }

    /// <summary>
    /// タイムラインに既に置かれている中継(カットオフ)野手の位置（<see cref="FielderTask.Cutoff"/>）を返す。
    /// 中継送球のボール経由点と野手の立ち位置を**単一の座標ソース**にするために使う（#136）。
    /// 陣形ルールにカットオフ割当が無い/野手が既にその場にいて移動が発生しない場合は null。
    /// </summary>
    private static TimelinePoint? FindCutoffFielderPoint(PlayTimeline timeline)
    {
        foreach (var m in timeline.Moves)
        {
            if (m.Task == FielderTask.Cutoff) return m.To;
        }
        return null;
    }

    /// <summary>
    /// バックホーム（本塁クロスプレー）の送球連鎖を描く（設計書12 §3, F2 Slice D）。
    /// 外野処理点→(中継 or 直接)→本塁の Throw と「本塁へ突っ込む／タッチアウト」実況を追加する。
    /// 結果不変・乱数不使用の表示専用。homeArriveAnchor=本塁での決着時刻（憤死走者レッグの終端）。
    ///
    /// 中継しきい値・投げ始め・理論カットマン位置・握り替えは**エンジンが本塁の生死を決めたのと同じ
    /// <see cref="HomePlayResolver.ResolveThrowPath"/>・同じ係数**から取り、各レッグの所要は**実際に描く
    /// 距離÷肩**で出す（＝画面上の送球速度が常に肩どおり, Issue #298-2/#301③）。従来は距離に依らない
    /// <c>fieldedAt + 0.30</c> を下限にしていたため、外野深部（80〜110m）からの返球が 0.30 秒で本塁に
    /// 着く＝200〜370 m/s の絵になっていた。描くカットマン座標は陣形が配置した野手（#136）なので総所要は
    /// エンジンの理論値と厳密一致はしない。判定との食い違いは <see cref="GuardAgainstVerdict"/> が吸収する。
    /// coefficients=null なら既定係数（Unity 実プレーが使う C# 既定と同一）。
    /// </summary>
    public static PlayTimeline AppendBackHomeThrows(
        PlayTimeline timeline, FieldingPlay play, FieldGeometry field, double homeArriveAnchor, bool runnerOut,
        BaserunningCoefficients? coefficients = null)
    {
        var c = coefficients ?? new BaserunningCoefficients();
        var fieldPoint = FieldPointOf(play);
        var fieldedAt = ContactAt + (play.FieldedAtSeconds ?? play.HangTimeSeconds);

        // エンジンの本塁レースと同一の判断・同一の係数（中継しきい値／理論カットマン位置／握り替え）。
        var situation = new HomePlaySituation(
            new Core.Vector3D(fieldPoint.X, 0, fieldPoint.Z), fieldedAt, ThrowSpeedOf(play), play.FielderFielding);
        var path = HomePlayResolver.ResolveThrowPath(situation, new Core.Vector3D(0, 0, 0), c);
        var transferFactor = new Players.FielderAttributes { Fielding = play.FielderFielding }
            .TransferFactor(c.TransferFieldingSlope, c.TransferFactorMin);
        var releaseAt = path.ReleaseAtSeconds;

        // 中継: カットマン位置は陣形ルールが実際に配置した中継野手と同一座標ソースにする（#136）。
        // 割当が無い場合のみ、エンジン式の理論カットマン位置へフォールバックする。
        TimelinePoint? cutoff = path.HasRelay
            ? FindCutoffFielderPoint(timeline) ?? new TimelinePoint(path.CutoffPoint.X, path.CutoffPoint.Z)
            : null;

        var arm = ThrowSpeedOf(play);
        var leg1 = LegSeconds(Distance(fieldPoint, cutoff ?? Home), arm);
        var leg2 = cutoff is { } cut1 ? LegSeconds(Distance(cut1, Home), c.RelayThrowSpeedMps) : 0.0;
        var relayHold = cutoff is not null ? c.RelayTransferSeconds * transferFactor : 0.0;
        var physicalArrive = releaseAt + leg1 + relayHold + leg2;
        var homeArrive = GuardAgainstVerdict(
            physicalArrive, releaseAt + leg1 + leg2, homeArriveAnchor, runnerOut);

        var ball = new List<BallSegment>(timeline.Ball);
        AddThrowChain(ball, fieldPoint, Home, cutoff, releaseAt, homeArrive, leg1, leg2,
            play.FielderRole, FindCutoffFielderRole(timeline),
            CoverRoleAt(timeline, Home, FieldPosition.Catcher), "home");

        var captions = new List<TimelineCaption>(timeline.Captions)
        {
            new(ball[ball.Count - (cutoff is null ? 1 : 2)].T0, "本塁へ突っ込む——送球！"),
            new(homeArrive, runnerOut ? "タッチアウト！ 本塁で憤死" : "際どいセーフ、還った！"),
        };

        var duration = Math.Max(timeline.Duration, homeArrive + 1.0);
        return timeline with { Ball = ball, Captions = captions, Duration = duration };
    }

    /// <summary>
    /// 外野安打でボールを内野へ返す送球連鎖（設計書12, #6）。外野処理点→(中継 or 直接)→先頭走者の到達塁。
    /// 本塁クロスプレーが無い外野安打専用（バックホームと二重に描かない）。結果不変・乱数不使用の表示専用。
    /// leadBaseName は "second"（単打/二塁打）/"third"（三塁打）。ボールが外野で死ぬのを防ぐ。
    ///
    /// 到達時刻は**エンジンが塁打数を決めたのと同じ式**（<see cref="ExtraBaseResolver.ThrowArriveSeconds"/>）
    /// で出す（Issue #301③）。従来は定数31m/s固定で、かつ到達を常に「走者到達+0.3s」へ紐付けていたため、
    /// 外野手が数秒ボールを持ったまま待つ＝返球が異様に遅く見える絵になっていた。走者アンカーは判定と
    /// 矛盾するときだけ効く（<see cref="GuardAgainstVerdict"/>）。
    /// coefficients=null なら既定係数（Unity 実プレーが使う C# 既定と同一）。
    /// </summary>
    public static PlayTimeline AppendOutfieldReturnThrow(
        PlayTimeline timeline, FieldingPlay play, FieldGeometry field, string leadBaseName, double runnerArriveAnchor,
        FieldingCoefficients? coefficients = null)
    {
        if (!IsOutfieldHit(play)) return timeline; // 外野へ抜けた打球のみ（バウンド抜けを含む, Issue #63）
        foreach (var seg in timeline.Ball)
            if (seg.Kind == BallSegmentKind.Throw) return timeline; // 既存送球があれば二重返球しない

        var c = coefficients ?? new FieldingCoefficients();
        var fieldPoint = FieldPointOf(play);
        var target = BasePoint(field, leadBaseName);
        var fieldedAt = ContactAt + (play.FieldedAtSeconds ?? play.HangTimeSeconds);
        var arm = ThrowSpeedOf(play);
        var straightDist = Distance(fieldPoint, target);

        // 塁打数を決めたのと同一の式で「この塁へ返球が着く時刻」を出す（＝絵と判定が同じ物理で動く）。
        // 失速項（長い送球ほど山なり）が入るので、深い外野からの返球は自然に時間がかかる。
        var readyAt = ExtraBaseResolver.ReadySeconds(fieldedAt, play.FielderFielding, c);
        var physicalArrive = ExtraBaseResolver.ThrowArriveSeconds(readyAt, straightDist, arm, c);

        // 中継: カットマン位置は陣形ルールが実際に配置した中継野手と同一座標ソースにする（#136）。
        TimelinePoint? cutoff = straightDist > RelayThresholdM
            ? FindCutoffFielderPoint(timeline)
              ?? new TimelinePoint((fieldPoint.X + target.X) * 0.5, (fieldPoint.Z + target.Z) * 0.5)
            : null;

        // 2本目（カットマン→塁）も内野手の送球なので同じ肩で描く（従来の定数1本化と同じ扱い）。
        var leg1 = LegSeconds(Distance(fieldPoint, cutoff ?? target), arm);
        var leg2 = cutoff is { } cut ? LegSeconds(Distance(cut, target), arm) : 0.0;
        var arrive = GuardAgainstVerdict(
            physicalArrive, readyAt + leg1 + leg2, runnerArriveAnchor, runnerOut: false);

        var ball = new List<BallSegment>(timeline.Ball);
        AddThrowChain(ball, fieldPoint, target, cutoff, readyAt, arrive, leg1, leg2,
            play.FielderRole, FindCutoffFielderRole(timeline),
            CoverRoleAt(timeline, target, DefaultBaseGuard(leadBaseName)), leadBaseName);

        var duration = Math.Max(timeline.Duration, arrive + 1.0);
        return timeline with { Ball = ball, Duration = duration };
    }

    /// <summary>
    /// 陣形が誰もカバーに付けなかったときの塁の既定の持ち主（Issue E1 のフォールバック）。
    /// 二塁だけは遊撃／二塁の両方が入り得るが、陣形表の既定（<see cref="DefensiveFormation"/>）に合わせる。
    /// </summary>
    private static FieldPosition DefaultBaseGuard(string baseName) => baseName switch
    {
        "first" => FieldPosition.FirstBase,
        "second" => FieldPosition.Shortstop,
        "third" => FieldPosition.ThirdBase,
        _ => FieldPosition.Catcher,
    };

    /// <summary>
    /// 表示上の中継しきい値[m]（これ超で中継、以下は直接返球）。
    /// <see cref="BaserunningCoefficients.CutoffDistanceThresholdM"/>（本塁レースの判定で使う値）と
    /// 同値に保つ＝本塁送球と外野返球で中継の出方が食い違わないようにする。
    /// </summary>
    private const double RelayThresholdM = 60.0;

    private static double Distance(TimelinePoint a, TimelinePoint b)
    {
        var dx = a.X - b.X;
        var dz = a.Z - b.Z;
        return Math.Sqrt(dx * dx + dz * dz);
    }

    /// <summary>この打球が外野扱いの安打か（着地が外野／バウンドで内野を抜けた, Issue #63）。表示専用。</summary>
    private static bool IsOutfieldHit(FieldingPlay play) => play.RangeM > OutfieldDepthM || play.ThroughInfield;

    /// <summary>
    /// 打球の処理点＝エンジンの回収点(FieldedX/Z)。ボールの終端・実捕球者の移動先・送球の起点を
    /// すべてこの1点に揃える（Issue #301①②）。回収を解かない経路（ファウル・本塁打・空中捕球）は
    /// <c>null</c> なので着地点へフォールバックする＝着地点しか意味を持たない打球では従来と完全に同値
    /// （Issue #315）。以前は ThroughInfield のときだけ回収点を見ていたため、外野飛球の安打と
    /// 内野ゴロ（実捕球点が着地点とズレる Issue #261）が取り残されていた。
    /// </summary>
    private static TimelinePoint FieldPointOf(FieldingPlay play)
        => new(play.FieldedX ?? play.LandingX, play.FieldedZ ?? play.LandingZ);

    /// <summary>
    /// 実捕球者の移動レッグを「回収時刻までに必ずボールへ着く」ように詰める（Issue #301①）。
    /// エンジンの回収時刻(<c>FieldedAtSeconds</c>)は判定を決めた確定値なので、表示側の走力計算
    /// （反応0.35s＋距離÷走力）が食い違ったときは**表示側を譲る**。これで「野手が到達する前に
    /// ボールが飛び出す＝誰も触っていないのに返球が返る」絵が構造的に消える。
    /// 間に合わないときは反応時間を削ってでも到達させる（t0 も前へずらす）。0長レッグ＝瞬間移動を
    /// 避けるため、最低 <see cref="MinFieldLegSeconds"/> は走る形にする（接触より前には遡らせない）。
    /// </summary>
    private static (double T0, double T1) ArriveByFieldedAt(double t0, double t1, double fieldedAt)
    {
        if (t1 <= fieldedAt) return (t0, t1);   // 既に間に合っている（フライを待つ等）
        var start = Math.Min(t0, Math.Max(ContactAt, fieldedAt - MinFieldLegSeconds));
        return (start, Math.Max(start, fieldedAt));
    }

    /// <summary>実捕球者の移動レッグの最短表示長[s]（瞬間移動に見せないための下限）。</summary>
    private const double MinFieldLegSeconds = 0.12;

    /// <summary>守備位置の日本語呼称（実況・結果表示用）。</summary>
    private static string PositionName(FieldPosition p) => p switch
    {
        FieldPosition.Pitcher => "ピッチャー",
        FieldPosition.Catcher => "キャッチャー",
        FieldPosition.FirstBase => "ファースト",
        FieldPosition.SecondBase => "セカンド",
        FieldPosition.ThirdBase => "サード",
        FieldPosition.Shortstop => "ショート",
        FieldPosition.LeftField => "レフト",
        FieldPosition.CenterField => "センター",
        FieldPosition.RightField => "ライト",
        _ => "",
    };

    /// <summary>内野/外野の「表示上の」境界[m]。表示専用の定数で、FieldingResolver の守備閾値
    /// （InfieldDepthM）とは意図的に分離する（キャプション用の値を判定へ流用しないため）。</summary>
    private const double OutfieldDepthM = 46.0;

    /// <summary>
    /// 安打の方向名。内野を抜けた打球（着地が OutfieldDepthM 超）は着地方位で左中右に分類し、
    /// 内野内なら処理野手のポジション名を使う。頭上を抜けた打球を「セカンド前」等と誤表示しない。
    /// FormationTable.Classify と同じ外野境界(±15°)を使う。判定・乱数には影響しない。
    /// </summary>
    private static string AreaName(FieldingPlay play)
    {
        if (IsOutfieldHit(play))
        {
            if (play.BearingDeg < -15.0) return "レフト";
            if (play.BearingDeg > 15.0) return "ライト";
            return "センター";
        }
        return play.FielderRole is { } r ? PositionName(r) : "";
    }

    /// <summary>
    /// 守備結果を守備位置・打球種別から具体的な実況文にする（"ショートゴロ" "レフト前ヒット" 等）。
    /// 表示専用。判定・乱数には一切影響しない（既存の FieldingPlay から派生するだけ）。
    /// 凡打・失策は処理/失策した野手名（FielderRole）、安打は着地方向（AreaName）で命名する。
    /// </summary>
    public static string DescribeResult(FieldingPlay play)
    {
        var pos = play.FielderRole is { } r ? PositionName(r) : "";
        return play.Result switch
        {
            BattedBallResult.HomeRun => "ホームラン",
            BattedBallResult.Foul => "ファウル",
            BattedBallResult.Error => pos.Length > 0 ? $"{pos}のエラー" : "エラー",
            BattedBallResult.Out => play.IsFly ? $"{pos}フライ" : $"{pos}ゴロ",
            // 内野ゴロで間に合わなかった安打（送球あり）＝内野安打。
            BattedBallResult.Single when !play.IsFly && play.ThrowArriveSeconds is not null => "内野安打",
            BattedBallResult.Single => AreaName(play) is { Length: > 0 } a ? $"{a}前ヒット" : "ヒット",
            BattedBallResult.Double => AreaName(play) is { Length: > 0 } a ? $"{a}への二塁打" : "二塁打",
            BattedBallResult.Triple => AreaName(play) is { Length: > 0 } a ? $"{a}への三塁打" : "三塁打",
            _ => "ヒット",
        };
    }

    private static string BaseName(int b) => b switch { 1 => "first", 2 => "second", 3 => "third", _ => "home" };

    private static double FindBatterArrive(PlayTimeline timeline)
    {
        foreach (var r in timeline.Runners)
        {
            if (r.Label == "打") return r.T1;
        }
        return ContactAt + 4.3;
    }

    private static Fielder? Find(IReadOnlyList<Fielder> fielders, FieldPosition pos)
    {
        foreach (var f in fielders)
        {
            if (f.Position == pos) return f;
        }
        return null;
    }

    private static TimelinePoint ResolveTarget(
        FormationAssignment a, TimelinePoint ball, FieldGeometry field, TimelinePoint from, TimelinePoint fieldBallTarget)
    {
        switch (a.Task)
        {
            case FielderTask.FieldBall:
                return fieldBallTarget;
            case FielderTask.CoverBase:
                return BasePoint(field, a.Target);
            case FielderTask.Cutoff:
            {
                // 中継: 打球と送球先の中間点に立つ。
                var target = BasePoint(field, a.Target);
                return new TimelinePoint((ball.X + target.X) / 2.0, (ball.Z + target.Z) / 2.0);
            }
            case FielderTask.Backup:
            {
                // バックアップ: 対象（塁 or 打球）の後方へ回る。
                var anchor = a.Target == "ball" ? ball : BasePoint(field, a.Target);
                var dx = anchor.X - from.X;
                var dz = anchor.Z - from.Z;
                var len = Math.Max(1.0, Math.Sqrt(dx * dx + dz * dz));
                var to = new TimelinePoint(anchor.X + dx / len * 6.0, anchor.Z + dz / len * 6.0);
                if (IsOutfield(a.Position))
                {
                    // 外野手は打球の前（内野側）へ突っ込まない（#1）。持ち場の深さより前に出さず、
                    // 寄る距離も抑える（内野ゴロで外野がグッと前進して見えるのを防ぐ）。
                    var z = Math.Max(to.Z, from.Z - 4.0);
                    to = CapDisplacement(from, new TimelinePoint(to.X, z), 8.0);
                }
                return to;
            }
            default:
                return from;
        }
    }

    private static bool IsOutfield(FieldPosition p) =>
        p is FieldPosition.LeftField or FieldPosition.CenterField or FieldPosition.RightField;

    /// <summary>from→to の変位を最大 maxDist[m] に丸める（方向は保持）。表示演出。</summary>
    private static TimelinePoint CapDisplacement(TimelinePoint from, TimelinePoint to, double maxDist)
    {
        var dx = to.X - from.X;
        var dz = to.Z - from.Z;
        var d = Math.Sqrt(dx * dx + dz * dz);
        if (d <= maxDist || d < 1e-9) return to;
        var s = maxDist / d;
        return new TimelinePoint(from.X + dx * s, from.Z + dz * s);
    }

    /// <summary>フェンス際の見送り余白[m]（フェンスへめり込ませない）。</summary>
    private const double FenceApproachMarginM = 1.0;

    /// <summary>
    /// 抜けた打球へ「寄る/飛びつく」演出の最大変位[m]（Issue #336②・表示専用）。
    /// FieldingCoefficients.InfielderFieldingRadiusM の既定値(4.20)と同じ意味を持つ表示専用の複製
    /// （判定側の実値とは意図的に分離する。OutfieldDepthM と同じ設計）。捕れない打球なので
    /// 実際の打球位置までは届かせない。
    /// </summary>
    private const double DiveReachCapM = 4.2;

    /// <summary>
    /// 実捕球者がいない打球（本塁打・ファウル等, FielderRole=null）で FieldBall 役の移動先をフェンス
    /// 内側へ丸める（Issue #300）。本塁より外側へ着地する打球（本塁打）はフェンス際で止めて見送らせ、
    /// フェンスの外まで追いかけさせない。着地点がフェンス内側ならそのまま返す（従来どおり）。
    /// </summary>
    private static TimelinePoint ClampToFence(TimelinePoint landing, FieldGeometry field, double bearingDeg)
    {
        var range = Math.Sqrt(landing.X * landing.X + landing.Z * landing.Z);
        if (range < 1e-6) return landing;
        var bearingRad = bearingDeg * Math.PI / 180.0;
        var limit = Math.Max(0.0, field.FenceDistance(bearingRad) - FenceApproachMarginM);
        if (range <= limit) return landing;
        var s = limit / range;
        return new TimelinePoint(landing.X * s, landing.Z * s);
    }
}
