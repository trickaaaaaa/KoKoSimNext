using System;
using System.Collections.Generic;
using KokoSim.Engine.Match.Field;
using KokoSim.Engine.Match.Fielding;
using KokoSim.Engine.Match.Game;
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
    /// </summary>
    public static PlayTimeline BuildBattedBall(
        FieldingPlay play,
        IReadOnlyList<Fielder> fielders,
        FieldGeometry field,
        FormationTable? formations,
        bool runnersOn,
        string resultText,
        double closeCallMarginSeconds = 0.15)
    {
        var table = formations ?? FormationTable.Default;
        var landing = new TimelinePoint(play.LandingX, play.LandingZ);

        var ball = new List<BallSegment>
        {
            new()
            {
                Kind = BallSegmentKind.Pitch, T0 = ContactAt - PitchDuration, T1 = ContactAt,
                From = Mound, To = Home,
            },
        };

        // 打球: フライは放物線(Flight)、ゴロは地を這うロール(Roll)で表現（mockのゴロと同じ見た目）。
        var landAt = ContactAt + play.HangTimeSeconds;
        var fieldedAt = play.FieldedAtSeconds is { } f ? ContactAt + f : landAt;
        if (play.IsFly)
        {
            ball.Add(new BallSegment
            {
                Kind = BallSegmentKind.Flight, T0 = ContactAt, T1 = landAt,
                From = Home, To = landing, ApexHeightM = play.ApexHeightM,
            });
        }
        else
        {
            // ゴロ/バウンド（Issue #63 の4分類）: 本塁→処理点まで一本で転がす。
            // 「バウンド」分類は高く弾む Bounce（頂点=最大バウンド頂点）、「ゴロ」は地を這う Roll。
            // 内野を抜けたバウンド（ThroughInfield）は外野の回収点まで届かせる（ボールが内野で死なない）。
            var groundTo = play.ThroughInfield ? FieldPointOf(play) : landing;
            var groundKind = play.Class == BattedBallClass.Bouncer ? BallSegmentKind.Bounce : BallSegmentKind.Roll;
            ball.Add(new BallSegment
            {
                Kind = groundKind, T0 = ContactAt, T1 = Math.Max(landAt, fieldedAt),
                From = Home, To = groundTo, ApexHeightM = play.BounceApexHeightM,
            });
        }

        double resolvedAt;
        var moves = new List<FielderMove>();
        var runners = new List<RunnerLeg>();
        var contactMsg = play.Result == BattedBallResult.HomeRun ? "打った——伸びていく！"
            : play.IsFly ? "打ち上げた！" : "打球が転がる！";
        var captions = new List<TimelineCaption> { new(0.05, "ピッチャー、投げた"), new(ContactAt + 0.01, contactMsg) };

        // 一塁送球（内野ゴロ）。
        if (play.ThrowArriveSeconds is { } throwArrive && play.Result is BattedBallResult.Out or BattedBallResult.Single or BattedBallResult.Error)
        {
            if (!play.IsFly && play.RangeM <= 46.0)
            {
                ball.Add(new BallSegment
                {
                    Kind = BallSegmentKind.Throw,
                    T0 = fieldedAt, T1 = ContactAt + throwArrive,
                    From = landing, To = BasePoint(field, "first"),
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

        // 守備陣形（9人全員に役割を付与, CHANGELOG 33）。
        var zone = FormationTable.Classify(play.BearingDeg, play.RangeM);
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

            foreach (var a in rule.Assignments)
            {
                var isBallFielder = ballFielder is { } bf && a.Position == bf;

                // 実捕球者でない FieldBall はボールへ行かせない（二重突進・誤処理を防ぐ）。
                if (a.Task == FielderTask.FieldBall && ballFielder is not null && !isBallFielder)
                    continue;
                if (a.Task == FielderTask.Hold && !isBallFielder) continue; // 待機は移動なし

                var fielder = Find(fielders, a.Position);
                if (fielder is null) continue;

                var from = new TimelinePoint(fielder.Location.X, fielder.Location.Z);
                // 実捕球者はゾーン役割を上書きしてボールへ（保持＝戻さない）。
                var task = isBallFielder ? FielderTask.FieldBall : a.Task;
                var to = isBallFielder ? landing : ResolveTarget(a, landing, field, from);
                if (isBallFielder) ballFielderMoved = true;

                var dist = Distance(from, to);
                if (dist < 0.5) continue;

                var speed = Math.Max(4.0, fielder.Attributes.SprintSpeedMps);
                var t0 = ContactAt + 0.35; // 反応
                var t1 = t0 + dist / speed;
                moves.Add(new FielderMove { Role = a.Position, T0 = t0, T1 = t1, To = to, Task = task });

                // バックアップ/中継で持ち場を離れた野手は、決着後に定位置へ戻る（"寄ったまま"を防ぐ, #2）。
                // 定位置は再生器の基準 DefaultPositions と同一ソース（FieldDiagramGeometry.MatchFielderPosition）。
                if (task is FielderTask.Backup or FielderTask.Cutoff)
                {
                    var (hx, hz) = FieldDiagramGeometry.MatchFielderPosition(a.Position);
                    var homePost = new TimelinePoint(hx, hz);
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
                    var dist = Distance(from, landing);
                    if (dist >= 0.5)
                    {
                        var speed = Math.Max(4.0, fielder.Attributes.SprintSpeedMps);
                        var t0 = ContactAt + 0.35;
                        moves.Add(new FielderMove { Role = bfr, T0 = t0, T1 = t0 + dist / speed, To = landing, Task = FielderTask.FieldBall });
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
        return new PlayTimeline
        {
            Duration = duration,
            Result = resultText,
            ResolvedAt = resolvedAt,
            Ball = ball,
            Moves = moves,
            Runners = runners,
            Captions = captions,
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
            Ball = new[]
            {
                new BallSegment { Kind = BallSegmentKind.Pitch, T0 = pitchStart, T1 = pitchArrive, From = Mound, To = Home },
                new BallSegment { Kind = BallSegmentKind.Throw, T0 = throwStart, T1 = throwArrive, From = Home, To = second },
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
        PlayTimeline timeline, IReadOnlyList<RunnerMove> moves, FieldGeometry field)
    {
        if (moves.Count == 0) return timeline;

        var legs = new List<RunnerLeg>(timeline.Runners);
        var runnerIndex = 0;
        foreach (var m in moves)
        {
            var speed = Math.Max(4.0, m.Runner.ToFielder().SprintSpeedMps);
            var perBase = field.BaseDistanceM / speed + 0.35; // ベースごとの所要（減速・踏み直し込みの簡略）

            var label = m.FromBase == 0 ? "打" : $"走{++runnerIndex}";
            var startBase = m.FromBase == 0 ? 1 : m.FromBase; // 打者は一塁到達後の続きから
            var t = m.FromBase == 0
                ? FindBatterArrive(timeline)                   // 一塁到達時刻から続ける
                : ContactAt + 0.10;

            if (m.FromBase == 0 && m.ToBase <= 1) continue;    // 打者の一塁までは既定レッグ

            // 塁上の走者は移動開始まで元の塁に立たせる（待機レッグ）。打者走者は本塁の既定レッグがあるため対象外。
            if (m.FromBase >= 1 && m.ToBase > m.FromBase && t > 0)
            {
                var stand = BasePoint(field, BaseName(m.FromBase));
                legs.Add(new RunnerLeg { Label = label, T0 = 0, T1 = t, From = stand, To = stand, OutAtEnd = false });
            }

            for (var b = startBase; b < m.ToBase; b++)
            {
                var from = BaseName(b);
                var to = BaseName(b + 1);
                var t1 = t + perBase;
                legs.Add(new RunnerLeg
                {
                    Label = label,
                    T0 = t,
                    T1 = t1,
                    From = BasePoint(field, from),
                    To = b + 1 >= 4 ? Home : BasePoint(field, to),
                    OutAtEnd = m.Out && b + 1 == m.ToBase,
                    // 判定オーバーレイ（Issue #59）: CloseCall は決着レッグ（この移動の最終区間）にだけ乗せる。
                    CloseCall = m.CloseCall && b + 1 == m.ToBase,
                });
                t = t1;
            }
        }

        var maxT = timeline.Duration;
        foreach (var l in legs)
        {
            if (l.T1 + 1.0 > maxT) maxT = l.T1 + 1.0;
        }
        return timeline with { Runners = legs, Duration = maxT };
    }

    /// <summary>
    /// 打席前に塁上にいて、この打球で動かなかった走者の静止トークンを足す（#3）。
    /// 移動レッグを持つ塁（走者が動いた）は二重に描かない。ラベルは移動走者("打"/"走1"…)と衝突しない
    /// "走一/走二/走三"。T0=0,T1=Duration の0長レッグ＝定点表示。結果不変・乱数不使用の表示専用。
    /// </summary>
    public static PlayTimeline AppendHeldRunners(
        PlayTimeline timeline, bool first, bool second, bool third,
        IReadOnlyList<RunnerMove> moves, FieldGeometry field)
    {
        var movedFrom = new HashSet<int>();
        foreach (var m in moves) movedFrom.Add(m.FromBase); // 1/2/3（0=打者）

        var legs = new List<RunnerLeg>(timeline.Runners);
        void AddHeld(int b, bool occupied, string name, string label)
        {
            if (!occupied || movedFrom.Contains(b)) return;
            var pt = BasePoint(field, name);
            legs.Add(new RunnerLeg { Label = label, T0 = 0, T1 = timeline.Duration, From = pt, To = pt, OutAtEnd = false });
        }
        AddHeld(1, first, "first", "走一");
        AddHeld(2, second, "second", "走二");
        AddHeld(3, third, "third", "走三");

        if (legs.Count == timeline.Runners.Count) return timeline;
        return timeline with { Runners = legs };
    }

    /// <summary>併殺送球の演出用固定値（表示専用・判定/帯に一切干渉しない）。</summary>
    private const double DpThrowSpeedMps = 31.0;   // 送球速度[m/s]
    private const double DpTransferSeconds = 0.15; // 二塁での握り替え[s]

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

        var fieldPoint = new TimelinePoint(play.LandingX, play.LandingZ); // ロール終端＝捕球点
        var second = BasePoint(field, "second");
        var first = BasePoint(field, "first");

        var fieldedAt = existingThrow.T0;                            // 野手処理時刻（＝送球開始）
        var secondArrive = fieldedAt + Distance(fieldPoint, second) / DpThrowSpeedMps;
        var pivotAt = secondArrive + DpTransferSeconds;             // 二塁で握り替え
        // 一塁到達は打者到達へアンカー（＝際どく封殺）。ただし物理的な下限は割らない。
        var firstArrive = Math.Max(batterArriveAnchor, pivotAt + Distance(second, first) / DpThrowSpeedMps);

        ball.Add(new BallSegment
        {
            Kind = BallSegmentKind.Throw, T0 = fieldedAt, T1 = secondArrive,
            From = fieldPoint, To = second,
        });
        ball.Add(new BallSegment
        {
            Kind = BallSegmentKind.Throw, T0 = pivotAt, T1 = firstArrive,
            From = second, To = first,
        });

        var duration = Math.Max(timeline.Duration, firstArrive + 1.0);
        return timeline with { Ball = ball, Duration = duration };
    }

    /// <summary>バックホーム送球の中継しきい値[m]（これ超で中継、以下は直接返球。表示専用）。</summary>
    private const double BackHomeCutoffThresholdM = 60.0;

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
    /// </summary>
    public static PlayTimeline AppendBackHomeThrows(
        PlayTimeline timeline, FieldingPlay play, FieldGeometry field, double homeArriveAnchor, bool runnerOut)
    {
        var fieldPoint = FieldPointOf(play);
        var dist = Distance(fieldPoint, Home);
        var fieldedAt = ContactAt + (play.FieldedAtSeconds ?? play.HangTimeSeconds);
        var homeArrive = Math.Max(homeArriveAnchor, fieldedAt + 0.30); // 送球の物理下限を確保

        var ball = new List<BallSegment>(timeline.Ball);
        if (dist > BackHomeCutoffThresholdM)
        {
            // 中継: カットマン位置は陣形ルールが実際に配置した中継野手と同一座標ソースにする（#136）。
            // 割当が無い場合のみ、本塁〜処理点の中間という従来の近似にフォールバックする。
            var cutoff = FindCutoffFielderPoint(timeline)
                ?? new TimelinePoint(fieldPoint.X * 0.5, fieldPoint.Z * 0.5);
            var pivotAt = fieldedAt + (homeArrive - fieldedAt) * 0.55; // 長い外野送球に多めの時間を割く
            ball.Add(new BallSegment { Kind = BallSegmentKind.Throw, T0 = fieldedAt, T1 = pivotAt, From = fieldPoint, To = cutoff });
            ball.Add(new BallSegment { Kind = BallSegmentKind.Throw, T0 = pivotAt, T1 = homeArrive, From = cutoff, To = Home });
        }
        else
        {
            // 直接返球（浅い当たり・内野からの本塁送球）。
            ball.Add(new BallSegment { Kind = BallSegmentKind.Throw, T0 = fieldedAt, T1 = homeArrive, From = fieldPoint, To = Home });
        }

        var captions = new List<TimelineCaption>(timeline.Captions)
        {
            new(fieldedAt, "本塁へ突っ込む——送球！"),
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
    /// **タイミングは走者到達 runnerArriveAnchor に紐付ける**（併殺送球と同じ思想）。送球到達=走者到達+0.3s
    /// （＝セーフで着く）を下限に、送球物理の下限も割らない。中継役はボールを持って"間"を作り、走者を追い越さない
    /// （三塁打なのに送球が走者より先に三塁へ着く、という計算とUIの矛盾を防ぐ）。
    /// </summary>
    public static PlayTimeline AppendOutfieldReturnThrow(
        PlayTimeline timeline, FieldingPlay play, FieldGeometry field, string leadBaseName, double runnerArriveAnchor)
    {
        if (!IsOutfieldHit(play)) return timeline; // 外野へ抜けた打球のみ（バウンド抜けを含む, Issue #63）
        foreach (var seg in timeline.Ball)
            if (seg.Kind == BallSegmentKind.Throw) return timeline; // 既存送球があれば二重返球しない

        var fieldPoint = FieldPointOf(play);
        var target = BasePoint(field, leadBaseName);
        var fieldedAt = ContactAt + (play.FieldedAtSeconds ?? play.HangTimeSeconds);

        var ball = new List<BallSegment>(timeline.Ball);
        double arrive;
        if (Distance(fieldPoint, target) > BackHomeCutoffThresholdM)
        {
            // 中継: 外野→カットマン(中間)へ速く返し、カットマンがボールを持って間を作り、
            // 走者の到達直後に塁へ投げる（保持中は BallAt がセグメント間で位置を保持）。
            // カットマン位置は陣形ルールが実際に配置した中継野手と同一座標ソースにする（#136）。
            var cutoff = FindCutoffFielderPoint(timeline)
                ?? new TimelinePoint((fieldPoint.X + target.X) * 0.5, (fieldPoint.Z + target.Z) * 0.5);
            var cutoffArrive = fieldedAt + Distance(fieldPoint, cutoff) / DpThrowSpeedMps;
            var finalDur = Distance(cutoff, target) / DpThrowSpeedMps;
            arrive = Math.Max(cutoffArrive + DpTransferSeconds + finalDur, runnerArriveAnchor + 0.3);
            var finalStart = arrive - finalDur;
            ball.Add(new BallSegment { Kind = BallSegmentKind.Throw, T0 = fieldedAt, T1 = cutoffArrive, From = fieldPoint, To = cutoff });
            ball.Add(new BallSegment { Kind = BallSegmentKind.Throw, T0 = finalStart, T1 = arrive, From = cutoff, To = target });
        }
        else
        {
            // 直接返球: 外野手がボールを持って間を作り、走者到達直後に塁へ。
            var throwDur = Distance(fieldPoint, target) / DpThrowSpeedMps;
            arrive = Math.Max(fieldedAt + throwDur, runnerArriveAnchor + 0.3);
            var start = arrive - throwDur;
            ball.Add(new BallSegment { Kind = BallSegmentKind.Throw, T0 = start, T1 = arrive, From = fieldPoint, To = target });
        }

        var duration = Math.Max(timeline.Duration, arrive + 1.0);
        return timeline with { Ball = ball, Duration = duration };
    }

    private static double Distance(TimelinePoint a, TimelinePoint b)
    {
        var dx = a.X - b.X;
        var dz = a.Z - b.Z;
        return Math.Sqrt(dx * dx + dz * dz);
    }

    /// <summary>この打球が外野扱いの安打か（着地が外野／バウンドで内野を抜けた, Issue #63）。表示専用。</summary>
    private static bool IsOutfieldHit(FieldingPlay play) => play.RangeM > OutfieldDepthM || play.ThroughInfield;

    /// <summary>
    /// 送球の起点にする「打球の処理点」。内野を抜けたバウンド（Issue #63）は外野の回収点(FieldedX/Z)、
    /// それ以外は従来どおり着地点(LandingX/Z)。ThroughInfield でない打球では従来と完全に同値。
    /// </summary>
    private static TimelinePoint FieldPointOf(FieldingPlay play) => play.ThroughInfield
        ? new TimelinePoint(play.FieldedX, play.FieldedZ)
        : new TimelinePoint(play.LandingX, play.LandingZ);

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
        FormationAssignment a, TimelinePoint ball, FieldGeometry field, TimelinePoint from)
    {
        switch (a.Task)
        {
            case FielderTask.FieldBall:
                return ball;
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
}
