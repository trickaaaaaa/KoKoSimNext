using System;
using System.Collections.Generic;
using System.Linq;
using KokoSim.Engine.Match.Batting;
using KokoSim.Engine.Match.Field;
using KokoSim.Engine.Match.Fielding;
using KokoSim.Engine.Match.Game;
using KokoSim.Engine.Match.Pitching;
using KokoSim.Engine.Players;

namespace KokoSim.Engine.Match.Timeline;

public static partial class TimelineBuilder
{
    /// <summary>
    /// <see cref="BuildBattedBall"/> の段間の受け渡し（中間結果）。回収点・捕球/着地時刻・一塁カバー役・失策球の行き先など
    /// 前段で決まる値をフィールドに持ち、段（打球の飛行→一塁送球→打者走者→判定→守備の寄り→失策の追い→キャプション）を
    /// 書いた順に呼ぶ。<b>段の呼び出し順が挙動そのもの</b>（後段は前段が決めた値を読む）。乱数は引かない。
    /// </summary>
    private sealed class BattedBallBuild
    {
        // ---- 入力
        private readonly FieldingPlay play;
        private readonly IReadOnlyList<Fielder> fielders;
        private readonly FieldGeometry field;
        private readonly FormationTable table;
        private readonly bool runnersOn;
        private readonly string resultText;
        private readonly double closeCallMarginSeconds;
        private readonly double closeFlyMarginSeconds;
        private readonly double fielderReachM;
        private readonly Player? batter;
        private readonly FieldingCoefficients? fieldingCoeff;
        private readonly double fairLineThresholdM;

        // ---- 打球の行方（コンストラクタで確定）
        private readonly TimelinePoint landing;
        private readonly TimelinePoint fieldPoint;
        private readonly double fieldBearingDeg;
        private readonly BallZone zone;
        private readonly FieldPosition firstBaseCoverer;
        private readonly double landAt;
        private readonly double fieldedAt;

        // ---- 出力（各段が足していく）
        private readonly List<BallSegment> ball;
        private readonly List<FielderMove> moves = new List<FielderMove>();
        private readonly List<RunnerLeg> runners = new List<RunnerLeg>();
        private readonly List<TimelineCaption> captions = new List<TimelineCaption>();
        private readonly List<JudgementEvent> judgements = new List<JudgementEvent>();

        // ---- 段をまたぐ値
        private double resolvedAt;
        private double returnTail = 0.0; // 定位置復帰レッグの最終時刻（余韻を延ばすため）
        private double batterArrive;
        private bool batterOut;
        private bool batterCloseCall;

        // 一塁送球（内野ゴロ）段が決め、後段（判定・追いかけレッグ注入）が読む値。
        // エラーでこぼれた/逸れた球の行き先（Rest）と死ぬ時刻・追いかける野手は、後段の追いかけレッグ注入（issue #439 ①）が読む。
        private TimelinePoint? errorBallRest = null;
        private double errorBallDeadAt = 0.0;
        private FieldPosition? errorChaser = null;
        // 横へ逸れた悪送球で一塁手が塁を離れる一歩（issue #812）。追いかけレッグの直前に積む。
        private (TimelinePoint To, double T0, double T1)? wildStep = null;
        // 失策球を拾い上げる瞬間（issue #629 ③。返球を積むのは GameEngine 側）。
        private double? errorPickupAt = null;
        private TimelinePoint? errorPickupPoint = null;
        private FieldPosition? errorPickupRole = null;
        // 一塁送球が実際に着く時刻（絵）。判定が決めた ThrowArriveSeconds に「受け手を待つ間」を足した値で、
        // 一塁送球の枝でだけ入る。アウト表示・結果確定時刻はこちらへ揃える（球が着く瞬間に「アウト」を出す）。
        private double? firstThrowArriveAt = null;
        // 悪送球を拾い直してアウト（issue #815）: 逸れた球の止まる点・止まる時刻・拾う役と時刻・アウトの時刻。
        private TimelinePoint? recoveredLanding = null;
        private double recoveredBallDeadAt = 0.0;
        private FieldPosition? recoveredRole = null;
        private double recoveredPickupAt = 0.0;
        private double recoveredOutAt = 0.0;
        // 受け手（一塁カバー役）が一塁へ着く見込み時刻（issue #672）。カバーの移動レッグと同じ式
        // （接触＋反応0.35s＋距離÷脚力）。投手の一塁カバーは全力で走っても約3.85秒かかるが、一塁手が
        // 処理する早い送球は 2.7〜3.5 秒で着く＝**投手が来る前に球だけが無人の一塁へ着いてアウトになる**
        // （実測: 実試合の一塁送球 4151 本のうち 53 本。全部「一塁手→投手」。遅れの中央値 0.45s・最大 1.15s）。
        private double? coverReadyAt = null;

        public BattedBallBuild(
            FieldingPlay play, IReadOnlyList<Fielder> fielders, FieldGeometry field, FormationTable table, bool runnersOn,
            string resultText, double closeCallMarginSeconds, double closeFlyMarginSeconds, double fielderReachM,
            Player? batter, FieldingCoefficients? fieldingCoeff, double fairLineThresholdM)
        {
            this.play = play;
            this.fielders = fielders;
            this.field = field;
            this.table = table;
            this.runnersOn = runnersOn;
            this.resultText = resultText;
            this.closeCallMarginSeconds = closeCallMarginSeconds;
            this.closeFlyMarginSeconds = closeFlyMarginSeconds;
            this.fielderReachM = fielderReachM;
            this.batter = batter;
            this.fieldingCoeff = fieldingCoeff;
            this.fairLineThresholdM = fairLineThresholdM;

            landing = new TimelinePoint(play.LandingX, play.LandingZ);
            // 回収点（エンジンが実際に「誰が・どこで・いつ」処理したかの答え）。表示はここを単一ソースにする
            // （Issue #301①②）。着地点しか見ていなかったため、外野安打でボールが着地点に貼り付いたまま
            // 静止し、野手の居場所ともズレていた。
            fieldPoint = FieldPointOf(play);
            // フェンス直撃の跳ね返り（issue fence-carom-and-over-the-head-cues）。判定はフェンス際で
            // 回収した扱いのままだが、**絵では球が戻ってくる**ので、回収の瞬間に球が居る場所は内野寄りになる。
            // 表示の回収点をそこへ差し替える＝野手の到達先・送球の起点・球の終端が1つの点で揃う
            // （バラバラに持つと「野手が戻ってくる球を通り過ごす」絵になる）。
            // 送球の**到達時刻は判定どおり**（AppendBackHomeThrows 等が判定の秒で組む）＝起点が近づいたぶんは
            // 投げ始めが遅れる＝返球前にボールを保持する「間」に吸収される（Issue #334 と同じ形）。
            if (play.Carom is { } caromForPoint && play.FieldedAtSeconds is { } fieldedForPoint)
            {
                var meet = caromForPoint.PositionAt(fieldedForPoint);
                fieldPoint = new TimelinePoint(meet.X, meet.Z);
            }

            // 守備陣形（9人全員に役割を付与, CHANGELOG 33）。ゾーン分類は着地点距離(RangeM)ではなく
            // 実際の回収点(fieldPoint)基準にする（Issue #336①）。ゴロは本塁寄りで着地して転がるため、
            // 着地点距離だけでは内野を抜けた打球（ThroughInfield）が内野ゾーンのまま誤分類され、
            // SS/3B(2B)が抜けた打球に一歩も反応しない棒立ちになっていた。方位角も回収点から取り直す
            // （着地点と回収点で通り道が微妙にずれるイレギュラーバウンドがあるため一貫させる）。
            // 内野で処理が完結する打球は回収点が着地点の延長上（同じ46m境界内）なので分類は変わらない。
            // ⚠ ここで先に出すのは、**一塁送球の受け手（Issue E1）を送球を積む時点で知る**ため
            // （送球の生成はこの下、陣形ループはさらに後ろ）。値は fieldPoint だけで決まる。
            var fieldRangeM = Math.Sqrt(fieldPoint.X * fieldPoint.X + fieldPoint.Z * fieldPoint.Z);
            fieldBearingDeg = Math.Atan2(fieldPoint.X, fieldPoint.Z) * 180.0 / Math.PI;
            zone = FormationTable.Classify(fieldBearingDeg, fieldRangeM);
            // 一塁カバーは実捕球者から動的に決める（Issue #257）。静的表のゾーン別担当は初期値止まりで
            // 誰が実際に捕ったかを見ていない（例: Bunt/InfieldRight ではファースト以外が捕るとファーストは
            // FieldBall 降格で役割を失い棒立ちになる）。表引き結果の一塁カバーだけ、このマッピングで上書きする
            // （YAML 側の表は初期値として温存。ゾーン×捕球者の全組み合わせを展開しない）。
            // アンアシスト刺殺（3U, Issue #400）: 一塁手が自分でベースを踏むと判定が決めたなら、カバー担当は
            // 一塁手自身＝投手（や二塁手）のカバーを生成しない。それ以外は従来どおり（ファースト処理なら投手カバー、
            // バントのみ二塁手）。判定は engine が済ませており、ここは結果を写すだけ（乱数不使用）。
            firstBaseCoverer = play.FielderRole == FieldPosition.FirstBase
                ? (play.UnassistedPutout ? FieldPosition.FirstBase
                    : zone == BallZone.Bunt ? FieldPosition.SecondBase : FieldPosition.Pitcher)
                : FieldPosition.FirstBase;

            ball = new List<BallSegment>
            {
                new()
                {
                    Kind = BallSegmentKind.Pitch, T0 = ContactAt - PitchDuration, T1 = ContactAt,
                    From = Mound, To = Home,
                },
            };

            landAt = ContactAt + play.HangTimeSeconds;
            fieldedAt = play.FieldedAtSeconds is { } f ? ContactAt + f : landAt;
        }

        /// <summary>
        /// 段を書いた順に呼んでタイムラインを組む。<b>段の順序が挙動そのもの</b>（後段は前段が決めた回収点・時刻・
        /// 失策球の行き先を読む）。1インスタンスにつき1回だけ呼ぶ。
        /// </summary>
        public PlayTimeline Build()
        {
            AddFlight();
            AddOpeningCaptions();
            AddFirstBaseThrow();
            AddBatterRunnerLegs();
            AddJudgements();
            DetermineResolvedAt();
            AddFielderMoves();
            AddFlyDropError();
            AddErrorChase();
            AddRecoveredPickup();
            AddResultCaptions();
            return ToTimeline();
        }

        /// <summary>段1 打球の飛行: 打球（フライ/本塁打の放物線・抜けた球の転がり・フェンス直撃の跳ね返り・ゴロ/バウンド）の区間を積む。</summary>
        private void AddFlight()
        {
            // 打球: フライは放物線(Flight)、ゴロは地を這うロール(Roll)で表現（mockのゴロと同じ見た目）。
            // 本塁打は IsFly が立たない（FieldingResolver は捕球判定に入る前に確定させて返すため。この
            // フラグはフェンス激突の受傷判定等エンジン側でも参照するので表示のために書き換えない, Issue #300）。
            // 表示だけ Result==HomeRun を見て放物線を選ぶ（着地点は柵の外＝本塁打らしく飛んでいく絵のまま）。
            if (play.IsFly || play.Result == BattedBallResult.HomeRun)
            {
                // 本塁打は柵上の点（判定に使った高さ）を必ず通す（issue #743）。対称な放物線のままだと、空気抵抗で
                // 後半ほど急に落ちる実軌道より柵の位置で低く描かれ、ぎりぎりの当たりが柵の手前に落ちて見える。
                var landRange = Distance(Home, landing);
                double? pass = play.Result == BattedBallResult.HomeRun && play.FenceCrossRangeM is { } cr && landRange > 1e-6
                    ? Math.Clamp(cr / landRange, 0.0, 1.0)
                    : null;
                ball.Add(new BallSegment
                {
                    Kind = BallSegmentKind.Flight, T0 = ContactAt, T1 = landAt,
                    From = Home, To = landing, ApexHeightM = play.ApexHeightM, LaunchHeightM = play.LaunchHeightM,
                    PassFraction = pass, PassHeightM = play.FenceCrossHeightM ?? 0.0,
                });
                // 抜けた飛球は着地して終わりではない: 着地点→回収点まで転がる（Issue #301②）。
                // 経路・時刻はエンジンの転がり結果（FieldedX/Z・FieldedAtSeconds）をそのまま使い、
                // 表示側で独自の転がりを発明しない。空中捕球（fieldedAt≤landAt）・本塁打・ファウル
                // （回収点＝着地点）は差が出ないので区間を足さない。
                if (play.Carom is { } carom)
                {
                    // フェンス直撃（issue fence-carom-and-over-the-head-cues）: 転がりを**当たる瞬間で区切り**、
                    // 残速×反発係数から出した跳ね返りを内野側へ足す。「0.40秒の無」だった区間に絵が付く。
                    var hitPt = new TimelinePoint(carom.HitPoint.X, carom.HitPoint.Z);
                    var hitAt = ContactAt + carom.HitAtSeconds;
                    if (hitAt > landAt + 1e-9 && Distance(landing, hitPt) >= 0.5)
                    {
                        ball.Add(new BallSegment
                        {
                            Kind = BallSegmentKind.Roll, T0 = landAt, T1 = hitAt, From = landing, To = hitPt,
                        });
                    }
                    if (fieldedAt > hitAt + 1e-9 && Distance(hitPt, fieldPoint) >= 0.5)
                    {
                        ball.Add(new BallSegment
                        {
                            Kind = BallSegmentKind.Roll, T0 = hitAt, T1 = fieldedAt, From = hitPt, To = fieldPoint,
                        });
                    }
                }
                else if (fieldedAt > landAt + 1e-9 && Distance(landing, fieldPoint) >= 0.5)
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
                    Hops = GroundSegmentHops(play),
                });
            }
        }

        /// <summary>段2 打った瞬間の実況: 投球・打球種別の実況と、打球の行方の手がかり（頭を越えた／フェンス直撃）。</summary>
        private void AddOpeningCaptions()
        {
            // 打球種別ごとの実況（Issue #401③）: engine の4分類(BattedBallClass)をそのまま文言に写す。
            // 新しい判定は作らない。null（ファウル等・未分類）は既定の Grounder 文言にフォールバック
            // （Class 未判定は地を這う Roll 扱いと同じ既定, Issue #315）。本塁打は分類より優先。
            var contactMsg = play.Result == BattedBallResult.HomeRun ? "打った——伸びていく！"
                : play.Class switch
                {
                    BattedBallClass.Liner => "痛烈なライナー！",
                    BattedBallClass.Fly => "高々と上がった！",
                    BattedBallClass.Bouncer => "大きく弾んだ！",
                    _ => "打球が転がる！",
                };
            captions.Add(new(0.05, "ピッチャー、投げた"));
            captions.Add(new(ContactAt + 0.01, contactMsg));

            // 打球の行方の手がかり（issue fence-carom-and-over-the-head-cues ③）。絵だけでは
            // 「アウトじゃない＝じゃあ長打か」と結果から逆算するしかなかったので、言葉でも出す。
            // 判定は増やさない＝engine が既に持っている観測（着地点の深さ・ReachedFence）を写すだけ。
            if (play.Result != BattedBallResult.HomeRun && play.RangeM > FieldGeometry.OutfieldBoundaryM
                && play.FielderRole is { } overRole && Find(fielders, overRole) is { } overFielder)
            {
                var startRange = Math.Sqrt(
                    overFielder.Location.X * overFielder.Location.X + overFielder.Location.Z * overFielder.Location.Z);
                if (play.RangeM > startRange + 1.0) captions.Add(new TimelineCaption(landAt, "頭を越えた！"));
            }
            if (play.Carom is { } caromCaption)
                captions.Add(new TimelineCaption(ContactAt + caromCaption.HitAtSeconds, "フェンス直撃！"));
        }

        /// <summary>段3 一塁送球（内野ゴロ）: 送球・悪送球・お手玉・拾い直し・アンアシストの運びを積み、失策球の行き先・一塁送球の到達時刻など後段が読む値を決める。</summary>
        private void AddFirstBaseThrow()
        {
            // 一塁送球（内野ゴロ）。到達時刻(T1)はエンジンの ThrowArriveSeconds のまま固定し、投げ始め(T0)を
            // 「実距離÷肩」の飛行時間から逆算する（Issue #334）。捕球オーバーヘッド＋握り替えは、捕球点で
            // ボールを保持する「間」として自然に吸収される（AddThrowChain と同じ形）。
            if (!play.UnassistedPutout && firstBaseCoverer != play.FielderRole && play.FielderRole is not null)
            {
                var coverFielder = Find(fielders, firstBaseCoverer);
                if (coverFielder is not null)
                {
                    var coverFrom = new TimelinePoint(coverFielder.Location.X, coverFielder.Location.Z);
                    var coverSpeed = Math.Max(4.0, coverFielder.Attributes.SprintSpeedMps);
                    coverReadyAt = ContactAt + 0.35
                        + Distance(coverFrom, FirstBaseCoverPoint(field, fieldPoint)) / coverSpeed;
                }
            }
            if (play.ThrowArriveSeconds is { } throwArrive && play.Result is BattedBallResult.Out or BattedBallResult.Single or BattedBallResult.Error)
            {
                if (!play.IsFly && play.RangeM <= FieldGeometry.OutfieldBoundaryM)
                {
                    var firstBase = BasePoint(field, "first");
                    var physicalArrive = ContactAt + throwArrive;
                    // 判定と絵の矛盾ガード（issue match-picture-vs-safe-out-verdict）。
                    // 一塁の封殺は `defenseTime + ForceOutMarginSeconds(0.60) <= runnerTime` でアウトなので、
                    // `runnerTime − 0.60 < defenseTime <= runnerTime` の帯は<b>絵では送球が先着しているのに
                    // 内野安打</b>になる（実測: 内野安打 1,374 件中 1,147 件＝83.5%・平均 0.37 秒先着＝走者が
                    // 塁の 2.4〜3.0m 手前。最大 0.60 秒＝約4m）。本塁クロスプレー・外野返球と同じガードを通す。
                    // ⚠ ずれた分は捕球点でボールを保持する「間」に吸収される（Issue #334 と同じ形）＝
                    // 送球そのものの見た目の速度は肩どおりのまま。アウト側は必ず 0.60 秒以上先着しているので不発。
                    var runnerAnchor = ContactAt + play.BatterToFirstSeconds;
                    var firstOut = play.Result == BattedBallResult.Out;
                    var arrive = physicalArrive;   // エラーの枝は一塁へ正着しない＝ガードの対象外
                    if (play.Result == BattedBallResult.Error
                        && play.ErrorKind == FieldingErrorKind.Throw && !play.UnassistedPutout)
                    {
                        // 悪送球（issue #439）: 一塁へ向かうが逸れて外へ抜ける。投げ始めは通常送球と同じ逆算、
                        // 一塁の脇を「届くはずだった時刻」ごろに通過し、先へ抜けて転がる。
                        // ⚠ 一塁に正着する絵（ToBase="first"・受け手あり）にしない＝「完璧な送球が先着したのに
                        // 出塁」という矛盾がこの issue の症状そのもの。受け手なし（ToRole=null）で誰も捕らない。
                        // 逸れ方（issue #812）は判定で引いた逸れ幅から決める（WildThrowStyles）。どれも球は
                        // 一塁手の後ろへ逃げる＝失策連鎖（約9割）の追加進塁と食い違わない。
                        var wild = BuildWildThrow(play, fieldPoint, firstBase, arrive, fieldedAt, firstBaseCoverer);
                        ball.AddRange(wild.Segments);
                        errorBallRest = wild.Rest;
                        errorBallDeadAt = wild.DeadAt;
                        errorChaser = firstBaseCoverer;
                        wildStep = wild.Step;
                    }
                    else if (play.Result == BattedBallResult.Error
                             && play.ErrorKind != FieldingErrorKind.Bobble && play.ErrorKind != FieldingErrorKind.Receive)
                    {
                        // 捕球エラー（お手玉。issue #439）: 送球は存在しない。打球の進行方向へ数m弾く絵。
                        // アンアシスト刺殺（3U）には悪送球が起きない（issue #1260）ので、ここへ落ちる悪送球は無い。
                        // FromRole は「弾いた野手」＝頭上のびっくりマーク（3D）が対象を特定するのに使う。
                        // 拾い直せず確定した捕球ミス（issue #816）は、判定が引いたこぼれ幅と拾い直しを終える時刻で描く
                        // （球は拾い直しの瞬間に止まる）。判定の無い失策（固定の弾き）は従来の定数。
                        var deflectM = play.CatchErrorScatterM ?? ErrorDeflectM;
                        var deflectSeconds = CatchErrorReadyAt(play) is { } catchReady
                            ? Math.Max(0.1, catchReady - fieldedAt) : ErrorDeflectSeconds;
                        var rest = Overshoot(Home, fieldPoint, deflectM);
                        ball.Add(new BallSegment
                        {
                            Kind = BallSegmentKind.Roll,
                            T0 = fieldedAt, T1 = fieldedAt + deflectSeconds,
                            From = fieldPoint, To = rest,
                            FromRole = play.FielderRole,
                        });
                        // こぼした本人が拾い直しに行く（①）
                        errorBallRest = rest;
                        errorBallDeadAt = fieldedAt + deflectSeconds;
                        errorChaser = play.FielderRole;
                    }
                    else if (play.Result == BattedBallResult.Out && play.RecoveredThrowScatterM is { } scatterM
                             && play.RecoveredThrowOutSeconds is { } recoveredOut && !play.UnassistedPutout && !runnersOn)
                    {
                        // 走者なしの打席だけ。走者がいると GameEngine が併殺・二塁封殺で「最初の送球」を差し替える
                        // （AppendDoublePlayThrows）ので、逸れた一塁送球と拾いに走るレッグが行き場を失う＝従来の絵へ落とす
                        // 悪送球を拾い直してアウト（issue #815・表示専用）: 送球は一塁を逸れて塁の先へ抜け、
                        // 一塁手自身かバックアップ（右翼手）が拾い直す。アウトの時刻は判定が決めた
                        // defenseTime + 回収ロス（RecoveredThrowOutSeconds）。逸れ方は回収レースの値の写し
                        // （ThrowRecovery.LandingPoint と同じ「塁から送球方向へ逸れ幅ぶん延長した点」）。
                        var errantRest = Overshoot(fieldPoint, firstBase, scatterM);
                        var flightToFirst = LegSeconds(Distance(fieldPoint, firstBase), ThrowSpeedOf(play));
                        var t0 = Math.Max(fieldedAt, physicalArrive - flightToFirst);
                        var deadAt = t0 + LegSeconds(Distance(fieldPoint, errantRest), ThrowSpeedOf(play));
                        ball.Add(new BallSegment
                        {
                            Kind = BallSegmentKind.Throw,
                            T0 = t0, T1 = deadAt,
                            From = fieldPoint, To = errantRest,
                            FromRole = play.FielderRole,
                        });
                        var outAt = Math.Max(ContactAt + recoveredOut, deadAt);
                        recoveredLanding = errantRest;
                        recoveredBallDeadAt = deadAt;
                        recoveredOutAt = outAt;
                        if (play.RecoveredThrowByBackup)
                        {
                            // バックアップが拾って一塁へ返す: 一塁で待つ一塁カバーへの短い送球の到達＝アウト
                            var backupRole = ThrowRecovery.BackupRole(1);
                            var backupArm = Find(fielders, backupRole)?.Attributes.ThrowSpeedMps ?? ThrowSpeedOf(play);
                            var relayT0 = Math.Max(deadAt, outAt - LegSeconds(Distance(errantRest, firstBase), backupArm));
                            ball.Add(new BallSegment
                            {
                                Kind = BallSegmentKind.Throw,
                                T0 = relayT0, T1 = outAt,
                                From = errantRest, To = firstBase,
                                FromRole = backupRole, ToRole = firstBaseCoverer, ToBase = "first",
                            });
                            recoveredRole = backupRole;
                            recoveredPickupAt = relayT0;
                        }
                        else
                        {
                            // 一塁手自身が拾い直す（返球不要＝拾った瞬間がアウト。design-12 §3.8 の回収モデル）
                            recoveredRole = firstBaseCoverer;
                            recoveredPickupAt = outAt;
                        }
                        firstThrowArriveAt = outAt;
                    }
                    else if (play.Result == BattedBallResult.Out && play.RecoveredCatchScatterM is { } fumbleScatterM
                             && play.RecoveredCatchOutSeconds is { } fumbleOutSeconds && !play.UnassistedPutout && !runnersOn)
                    {
                        // 捕球ミスを拾い直してアウト（issue #817・表示専用）: 捕球点で弾いた球を処理野手自身が
                        // 拾い直し、そこから一塁へ送球してアウトになる。「弾いたのに一塁アウト」が絵でも成立する。
                        // 弾く方向はお手玉の失策（上の FieldingErrorKind.Catch 分岐）と同じ（打球の進行方向へ
                        // Overshoot）。アウトの時刻は判定が決めた recoveredCatchOutSeconds（絵は逆算で合わせる。
                        // issue #815 の悪送球回収と同じ作法）。
                        var fumbleRest = Overshoot(Home, fieldPoint, fumbleScatterM);
                        var flightToFirst = LegSeconds(Distance(fumbleRest, firstBase), ThrowSpeedOf(play));
                        var outAt = Math.Max(ContactAt + fumbleOutSeconds, fieldedAt + flightToFirst);
                        // 受け手が一塁へ着くまで送球を持って待つ（issue #672 と同じ扱い。上限は走者到達の少し前）。
                        // 抜けていると「一塁手→投手」のトスが投手より先に無人の一塁へ着く（実測 最大0.24s）。
                        if (coverReadyAt is { } fumbleReady)
                            outAt = Math.Max(outAt, Math.Min(fumbleReady + ReceiverSetSeconds, runnerAnchor - ReceiverSetSeconds));
                        var pickupAt = Math.Max(fieldedAt, outAt - flightToFirst);
                        ball.Add(new BallSegment
                        {
                            Kind = BallSegmentKind.Roll,
                            T0 = fieldedAt, T1 = pickupAt,
                            From = fieldPoint, To = fumbleRest,
                            FromRole = play.FielderRole,
                        });
                        ball.Add(new BallSegment
                        {
                            Kind = BallSegmentKind.Throw,
                            T0 = pickupAt, T1 = outAt,
                            From = fumbleRest, To = firstBase,
                            FromRole = play.FielderRole, ToRole = firstBaseCoverer, ToBase = "first",
                        });
                        firstThrowArriveAt = outAt;
                    }
                    else if (play.UnassistedPutout)
                    {
                        // アンアシスト（3U, Issue #400）: 送球せず一塁手がボールをグラブに収めたまま運ぶ。
                        // Carry セグメント（保持しての移動）で表現し、山なりの Throw は積まない。到達(T1)は判定を
                        // 決めた刺殺時刻のまま＝一塁手のカバー移動レッグ（下の注入）と同時にベースへ着く。
                        // 走って運ぶので「間」で詰められない＝最速も物理そのもの（ガードはセーフ側でしか効かない）。
                        arrive = GuardAgainstVerdict(physicalArrive, physicalArrive, runnerAnchor, firstOut);
                        ball.Add(new BallSegment
                        {
                            Kind = BallSegmentKind.Carry,
                            T0 = fieldedAt, T1 = arrive,
                            From = fieldPoint, To = firstBase,
                            FromRole = play.FielderRole, ToRole = firstBaseCoverer, ToBase = "first",
                        });
                    }
                    else
                    {
                        var flight = LegSeconds(Distance(fieldPoint, firstBase), ThrowSpeedOf(play));
                        arrive = GuardAgainstVerdict(physicalArrive, fieldedAt + flight, runnerAnchor, firstOut);
                        // 受け手が一塁へ着くまで送球を持って待つ（issue #672）。「間」で吸収＝送球の見た目の速度は
                        // 肩どおりのまま（T0 を後ろへずらすだけ）。アウトは判定の「走者到達の0.60秒前以前」を
                        // 破らないよう走者到達の少し前を上限にする（それでも受け手が間に合わないほど遅い走者は、
                        // 判定どおり先着させる＝判定を絵で覆さない）。セーフは走者より後ろなので上限なし。
                        // お手玉の失策（Bobble）・一塁手の捕球ミス（Receive, issue #810）は送球が正しく
                        // 一塁へ届く（Bobbleは遅いだけ／Receiveは届いた後に弾く）＝内野安打と同じ扱い
                        if (coverReadyAt is { } ready
                            && (play.Result != BattedBallResult.Error
                                || play.ErrorKind == FieldingErrorKind.Bobble || play.ErrorKind == FieldingErrorKind.Receive))
                        {
                            var wait = ready + ReceiverSetSeconds;
                            arrive = firstOut
                                ? Math.Max(arrive, Math.Min(wait, runnerAnchor - ReceiverSetSeconds))
                                : Math.Max(arrive, wait);
                        }
                        firstThrowArriveAt = arrive;
                        ball.Add(new BallSegment
                        {
                            Kind = BallSegmentKind.Throw,
                            T0 = Math.Max(fieldedAt, arrive - flight), T1 = arrive,
                            From = fieldPoint, To = firstBase,
                            FromRole = play.FielderRole, ToRole = firstBaseCoverer, ToBase = "first",
                        });
                        if (play.ErrorKind == FieldingErrorKind.Receive)
                        {
                            // 一塁手の捕球ミス（issue #810）: 送球自体は正確に一塁へ届くが、受け手がグラブで
                            // 弾いてこぼす。弾む距離・時間・拾い直す野手の決め方はお手玉（Catch）と共有する。
                            var dropRest = Overshoot(fieldPoint, firstBase, ErrorDeflectM);
                            ball.Add(new BallSegment
                            {
                                Kind = BallSegmentKind.Roll,
                                T0 = arrive, T1 = arrive + ErrorDeflectSeconds,
                                From = firstBase, To = dropRest,
                                FromRole = firstBaseCoverer,
                            });
                            errorBallRest = dropRest;
                            errorBallDeadAt = arrive + ErrorDeflectSeconds;
                            errorChaser = firstBaseCoverer;
                        }
                    }
                }
            }
        }

        /// <summary>段4 打者走者: 一塁へのレッグと一塁駆け抜け（オーバーラン）。</summary>
        private void AddBatterRunnerLegs()
        {
            // 打者走者（常に一塁へ走る。本塁打はダイヤモンド一周の簡略で一塁まで表現）。
            batterArrive = ContactAt + play.BatterToFirstSeconds;
            batterOut = play.Result == BattedBallResult.Out && !play.IsFly;
            // 判定オーバーレイ（Issue #59）: 一塁送球が絡んだ判定（force out / 内野安打）のみ際どさを評価。
            batterCloseCall = play.JudgementMarginSeconds is { } jm && Math.Abs(jm) < closeCallMarginSeconds;
            runners.Add(new RunnerLeg
            {
                Label = "打", T0 = ContactAt + 0.10, T1 = batterArrive,
                From = Home, To = BasePoint(field, "first"), OutAtEnd = batterOut, CloseCall = batterCloseCall,
                // 打者走者の一塁は常にフォース（issue #489）: 一塁手は塁を踏むだけで良く、タッチの芝居は出さない。
                Force = true,
                Runner = batter,
            });

            // 一塁駆け抜け（オーバーラン, issue #443・監督決定2026-08-04）: 内野ゴロで一塁へ向かう打者走者は
            // 塁上で即停止させず、塁の数m先まで走り抜けてから減速して止まる。アウト/セーフを問わず常に
            // このレッグを足す（オーバーランは失格にならないルール＝#417 と同じ前提）。表示専用の追加レッグ
            // ＝判定・帯には無関係（不変条件#2。乱数不使用）。
            // 外野安打/フライは対象外: 打者が一塁を回って進塁する場合は AppendRunnerLegs が続きのレッグを
            // 同じ Label "打" で足す（FindBatterArrive がこのレッグより先に積んだ本レッグの T1 を拾う）ので、
            // 続きのレッグの From（一塁）・T0（到達時刻）がこのレッグと一致し、時刻が重なる区間は
            // リストの後ろ（続きのレッグ）が RunnerAt の上書きで自然に勝つ＝無害だが、対象外にして
            // 余計なレッグを増やさない。
            if (!play.IsFly && !IsOutfieldHit(play))
            {
                var firstBase = BasePoint(field, "first");
                var approachSeconds = Math.Max(0.05, batterArrive - (ContactAt + 0.10));
                var approachSpeed = Distance(Home, firstBase) / approachSeconds;
                var overrunSeconds = Math.Max(0.3, 2.0 * FirstBaseOverrunDistanceM / Math.Max(1.0, approachSpeed));
                runners.Add(new RunnerLeg
                {
                    Label = "打", T0 = batterArrive, T1 = batterArrive + overrunSeconds,
                    From = firstBase, To = Overshoot(Home, firstBase, FirstBaseOverrunDistanceM),
                    // 判定は既に確定済み（真の到達＝上のレッグの T1）なので、ここでは常に false。
                    // 2D俯瞰の hideAt は OutAtEnd=true のレッグの T1 を拾う（PlayTimelineAdapter）ため、
                    // true にすると「アウト後もしばらく走り続けてから消える」絵に変わってしまう。
                    OutAtEnd = false, CloseCall = false,
                    Runner = batter,
                });
            }
        }

        /// <summary>段5 判定イベント: 本塁打・アウト・失策・セーフ・フェアの判定オーバーレイ。</summary>
        private void AddJudgements()
        {
            // 判定オーバーレイ「フェア」のライン際条件（issue #1179）。着地点からファウルラインまでの
            // 垂直距離[m]（正＝フェア側、負＝ファウル側）。本塁付近（r≈0）は符号が定まらないので
            // NaN を返し、しきい値比較がすべて false になる（フェアもファウルも出ない無害な既定）。
            var lineDistanceM = DistanceToFoulLineM(play.LandingX, play.LandingZ);

            // 判定イベント（Issue #260）: 走者レッグの終端に縛られない、確定時刻付きの独立イベント。
            // フライ・ライナー・ファウルフライの捕球アウトは捕球の瞬間（fieldedAt）に確定する。
            // 内野ゴロの一塁フォースは送球到達の瞬間（＝フォース成立。走者到達を待たない, Issue #328①）。
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
                var forceOutAt = firstThrowArriveAt ?? (play.ThrowArriveSeconds is { } ta ? ContactAt + ta : batterArrive);
                judgements.Add(new JudgementEvent { Label = "打", T = forceOutAt, Kind = JudgementKind.Out });
            }
            else if (play.Result == BattedBallResult.Error)
            {
                // 失策出塁（issue #439）: エラー確定の瞬間に「エラー」を必ず1枚出す。
                // 捕球エラー＝処理点で弾いた時刻／悪送球＝送球が一塁に届くはずだった時刻／
                // トンネル（issue #632）＝内野手が抜かれた瞬間（ErrorAtSeconds。処理時刻は外野手が
                // 球を回収する時刻＝ずっと後なので、fieldedAt を使うと絵とテロップが数秒ずれる）／
                // 一塁手の捕球ミス（Receive, issue #810）＝送球が実際に一塁へ着いた瞬間（firstThrowArriveAt。
                // ErrorAtSeconds は判定の defenseTime の写しで、GuardAgainstVerdict による絵の調整を含まない
                // ＝球側の弾きロール（下で追加）の T0 とずれるので、絵と一致する firstThrowArriveAt を優先する）。
                // セーフと違い際どさ条件を課さない＝「アウトの絵に見えるのに黙って出塁」を防ぐのが目的。
                var errAt = play.ErrorKind == FieldingErrorKind.Receive && firstThrowArriveAt is { } receiveArrive
                    ? receiveArrive
                    : play.ErrorAtSeconds is { } ea
                        ? ContactAt + ea
                        : play.ErrorKind == FieldingErrorKind.Throw && play.ThrowArriveSeconds is { } et
                            ? ContactAt + et
                            : fieldedAt;
                // テロップは記録として失策が確定する瞬間＝打者が一塁でセーフになった瞬間に出す（issue #806）。
                // T（取り損ないの瞬間）は「！」マーク・寄りカメラが使うので動かさない。打者が先に一塁を
                // 越えている（外野の落球等）ときも「！」（T＋ErrorMarkDelaySeconds）より前には出さない
                var errDisplayAt = Math.Max(batterArrive, errAt + JudgementEvent.ErrorMarkDelaySeconds);
                judgements.Add(new JudgementEvent { Label = "打", T = errAt, Kind = JudgementKind.Error, DisplayT = errDisplayAt });
            }
            else if (play.ErrorKind == FieldingErrorKind.Fumble)
            {
                // 外野手のファンブル（issue #811）: 安打の記録（Result）は変えないが、弾いた瞬間に「エラー」の
                // 「！」マーク・テロップを出す＝観測者に「安打＋失策」の両方を見せる。ErrorAtSeconds は
                // 遅延前（弾いた瞬間）の回収時刻の写し。
                var fumbleAt = play.ErrorAtSeconds is { } fa ? ContactAt + fa : fieldedAt;
                var fumbleDisplayAt = Math.Max(batterArrive, fumbleAt + JudgementEvent.ErrorMarkDelaySeconds);
                judgements.Add(new JudgementEvent { Label = "打", T = fumbleAt, Kind = JudgementKind.Error, DisplayT = fumbleDisplayAt });
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
            else if (lineDistanceM >= 0.0 && lineDistanceM <= fairLineThresholdM)
            {
                // ライン際のフェア（issue #1179）: 着地点がファウルラインからしきい値以内のフェア打球
                // （本塁打は上の分岐で既に確定済み・対象外）。上の「際どい飛球」と同じ判定オーバーレイを
                // 共有する＝「フェア」は2つの条件のOR（このelse-ifでフライ/ゴロを問わず拾う）。
                // 完全に抜けた普通の安打（ラインから十分離れている）・ファウル（ライン外＝距離が負）は対象外。
                judgements.Add(new JudgementEvent { Label = "打", T = landAt, Kind = JudgementKind.Fair });
            }
        }

        /// <summary>段6 結果確定時刻: アウト/送球到達/野手処理/着地のうちプレーを決めた時刻。</summary>
        private void DetermineResolvedAt()
        {
            // 結果確定時刻: アウト/送球到達/野手処理/着地のうちプレーを決めた時刻。
            resolvedAt = play.Result switch
            {
                BattedBallResult.HomeRun => landAt,
                BattedBallResult.Foul => landAt,
                BattedBallResult.Out when play.IsFly => fieldedAt,
                BattedBallResult.Out => firstThrowArriveAt ?? ContactAt + (play.ThrowArriveSeconds ?? play.FieldedAtSeconds ?? play.HangTimeSeconds),
                BattedBallResult.Single when play.ThrowArriveSeconds is not null => batterArrive, // 内野安打は駆け込み
                _ => fieldedAt,
            };
        }

        /// <summary>段7 守備の寄り: 陣形表に沿った移動（処理野手・打球を追う内野手・一塁カバー・アンアシストの運び・定位置戻り）。</summary>
        private void AddFielderMoves()
        {
            var rule = table.Lookup(zone, runnersOn);
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
                // 実捕球者の体の行き先（issue #771）。フェンスまで転がった球は回収点がフェンスちょうど
                // （ExtraBaseResolver の rollDist = toFence）なので、体の中心をそこへ置くと半身がフェンスに入る。
                // 体の分だけ内側で止める（手は届く＝StopWithinReach と同じ考え方）。球・送球の起点は回収点のまま
                // ＝表示専用で判定は読まない。
                var ballFielderTarget = ClampToFence(fieldPoint, field, fieldBearingDeg, FenceBodyMarginM);

                var firstBaseCovered = false;
                // アンアシスト（3U, Issue #400）で一塁手がボールを捕った実位置（FieldBall レッグの終端）。
                // その後の「捕球点→ベースへ運ぶ」カバーレッグの出発点にする。守備範囲(reach)で届いた捕球は体が
                // 捕球点まで来ていない（StopWithinReach で手前に止まる）ので、ここを起点にしないと運びレッグが
                // 実距離を過小評価して脚力を超える（LegSpeedTests）。既定は捕球点＝体が捕球点まで来た場合。
                var firstBaseFieldEnd = fieldPoint;
                // 内野を抜けた打球で最も惜しかった内野手（Issue #336②）と、打球ラインを挟んで反対側で次に惜しかった
                // 内野手（issue #1591）。この最大2人だけ、通常の役割移動の前に「打球を追って走る」レッグを1本挟む。
                // ThroughInfield でない打球では常に null。
                var missRole = play.ThroughInfield ? play.ClosestInfielderRole : null;
                var missRole2 = play.ThroughInfield ? play.SecondInfielderRole : null;
                var chaseLegAdded = new HashSet<FieldPosition>();

                foreach (var a in rule.Assignments)
                {
                    var isBallFielder = ballFielder is { } bf && a.Position == bf;

                    var fielder = Find(fielders, a.Position);
                    if (fielder is null) continue;

                    var from = new TimelinePoint(fielder.Location.X, fielder.Location.Z);

                    // 打球を追うレッグ（Issue #336②→#1591・表示専用）: 抜けた打球に最も近かった内野手（と反対側の2人目）は、
                    // 反応時間（実測 ReactionDelaySeconds）の後、打球の最接近点を越えて打球の進行方向へ数歩流れる。
                    // 捕れない打球なので判定は動かさず、体だけが打球を追う。表の役割（待機・塁カバー等）が何であれ
                    // 追うレッグは必ず積む（先に積むので下の skip の影響を受けない）。
                    // 通常の役割移動(t0)はこのレッグの後ろへ押し出す＝時系列の逆転（後発レッグが先に
                    // 表示される描画バグ）を防ぐ。
                    TimelinePoint? diveEnd = null;
                    var diveT1 = 0.0;
                    var chaseApproach = !isBallFielder && missRole == a.Position
                        ? (play.ClosestApproachX, play.ClosestApproachZ)
                        : !isBallFielder && missRole2 == a.Position
                            ? (play.SecondApproachX, play.SecondApproachZ)
                            : ((double?)null, (double?)null);
                    if (chaseApproach.Item1 is { } approachX && chaseApproach.Item2 is { } approachZ
                        && chaseLegAdded.Add(a.Position))
                    {
                        var approachLen = Math.Sqrt(approachX * approachX + approachZ * approachZ);
                        var follow = approachLen < 1e-6 ? 0.0 : ChaseFollowThroughM / approachLen;
                        var diveTarget = new TimelinePoint(approachX * (1.0 + follow), approachZ * (1.0 + follow));
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

                    // 実捕球者でない FieldBall はボールへ行かせない（二重突進・誤処理を防ぐ）。
                    if (a.Task == FielderTask.FieldBall && ballFielder is not null && !isBallFielder)
                        continue;
                    if (a.Task == FielderTask.Hold && !isBallFielder) continue; // 待機は移動なし

                    // 表の一塁カバー担当が動的な担当と一致しないなら押し出し＝持ち場待機（移動を生成しない）。
                    // 実捕球者自身の行（ボールへ向かう）は対象外。
                    if (a.Task == FielderTask.CoverBase && a.Target == "first" && !isBallFielder
                        && a.Position != firstBaseCoverer)
                        continue;

                    // 実捕球者はゾーン役割を上書きしてボールへ（保持＝戻さない）。
                    var task = isBallFielder ? FielderTask.FieldBall : a.Task;
                    var to = isBallFielder ? ballFielderTarget : ResolveTarget(a, fieldPoint, field, from, fieldBallTarget);
                    if (isBallFielder) ballFielderMoved = true;
                    if (task == FielderTask.CoverBase && a.Target == "first") firstBaseCovered = true;

                    var moveFrom = diveEnd ?? from;
                    var dist = Distance(moveFrom, to);
                    if (dist < 0.5) continue;

                    var speed = Math.Max(4.0, fielder.Attributes.SprintSpeedMps);
                    // 反応 0.35s。処理野手だけは判定の反応遅延（守備力で縮む・F80=0.28s）より遅く出さない
                    // （issue fielder-body-far-from-ball-at-catch）: 判定は「体が実効リーチまで寄れた時刻」で処理点を
                    // 採るので、絵の出足が判定より遅いと守備の巧い野手ほど体が球に届かない絵になる。
                    var reaction = isBallFielder ? Math.Min(0.35, fielder.Attributes.ReactionDelaySeconds) : 0.35;
                    // 走路取りの巧さ（issue fielder-body-far-from-ball-at-catch 案C）: 判定は守備力>50 の野手に
                    // 滞空時間比例の到達猶予（CatchTimeBudget の routeFactor）を与える＝「脚力より長く走れる」ことにして
                    // いるので、絵の出足を接触＋反応にしたままだと体が落下点に届かない（守備80で 2%・最大 3.4m）。
                    // 猶予ぶんだけ出足を早める（打った瞬間の読み＝接触の少し前まで）。判定は不変・表示専用。
                    if (isBallFielder && play.IsFly && play.Result == BattedBallResult.Out && fieldingCoeff is { } fc)
                    {
                        var routeBudget = Math.Min(play.HangTimeSeconds * fc.CatchReachFactor, fc.CatchReachCapSeconds);
                        var routeBonus = routeBudget * (fielder.Attributes.Fielding - 50) * fc.CatchReachFieldingSlope;
                        if (routeBonus > 0) reaction = Math.Max(-FlyJumpAnticipationSeconds, reaction - routeBonus);
                    }
                    var t0 = diveEnd is not null ? Math.Max(ContactAt + reaction, diveT1 + 0.1) : ContactAt + reaction;

                    // 急ぎ度（issue fielder-leg-speed-by-distance）: 全レッグを「距離÷脚力」で切ると、2m 寄るだけの
                    // 二塁手も送球を受けに塁へ寄る一塁手も余裕のある飛球の中堅手も全員が全力疾走になる
                    // （実測 501 レッグの巡航が全部 8.3〜8.8 m/s）。急がない移動＝塁カバー／バックアップ／中継／
                    // 余裕のあるフライは<b>期限から逆算した必要速度</b>で走らせる（上限は脚力＝間に合わない移動は
                    // 従来どおり全力、下限は小走り）。着く時刻は期限＝判定の秒より前で、判定はこのレッグを読まない
                    // （表示専用・帯不変）。期限: 塁カバー＝その塁への送球到達の少し前／バックアップ・中継＝決着／
                    // 余裕のあるフライ＝捕球の少し前（落下点で待ち構える）。
                    double? paceDeadline = null;
                    if (isBallFielder)
                    {
                        if (play.IsFly && play.Result == BattedBallResult.Out) paceDeadline = fieldedAt - FlySettleSeconds;
                    }
                    else if (task == FielderTask.CoverBase)
                    {
                        paceDeadline = (ThrowArriveAtBase(ball, a.Target) ?? resolvedAt) - CoverSetSeconds;
                    }
                    else if (task is FielderTask.Backup or FielderTask.Cutoff)
                    {
                        paceDeadline = resolvedAt;
                    }
                    var pace = speed;
                    if (paceDeadline is { } dl && dl - t0 > 1e-6)
                    {
                        var need = dist / (dl - t0);
                        // 歩き（≤EasyPaceMinMps）と小走り（≥EasyJogMps）の間は小走りへ切り上げる（早く着いて待つ）。
                        // 3D の収録は歩き 0.73 m/s と走り 4.28 m/s の間に何も無く、間の速さは足が滑る
                        // （実測 2026-08-18: 間の速さのレッグは尺が長くコマ数を稼ぐので「目立つ」が 7%→15〜18%）。
                        pace = need <= EasyPaceMinMps ? EasyPaceMinMps
                            : need < EasyJogMps ? Math.Min(speed, EasyJogMps)
                            : Math.Min(speed, need);
                    }
                    var t1 = t0 + dist / pace;
                    IReadOnlyList<FielderMove>? afterLegs = null;
                    var chaseProfile = MoveProfile.Trapezoid;
                    if (isBallFielder && PlanMissedFlyChase(
                            play, landAt, fieldedAt, landing, ClampToFence(landing, field, play.BearingDeg, FenceBodyMarginM),
                            ballFielderTarget, moveFrom, speed, t0, ReachMFor(fielder, fieldingCoeff, fielderReachM), a.Position,
                            ExtraBaseResolver.ReadySeconds(fieldedAt, play.FielderFielding, fieldingCoeff ?? new FieldingCoefficients()),
                            IsNearWall(landing, field, play.BearingDeg, fieldingCoeff ?? new FieldingCoefficients()))
                            is { } chase)
                    {
                        // 落とした飛球（issue #1075）: 落下点へ追う→着地後に拾う点へ向かい直す、の2段。
                        // 絵の到着を判定の遅れぶん着地より後へ置く＝「追いついたのに落ちた」絵を作らない。
                        // 追う段は全力のまま（issue #1084）。着地の瞬間に拾う打球は球を弾ませて拾い直す
                        (t0, t1, to) = (chase.Chase.T0, chase.Chase.T1, chase.Chase.To);
                        chaseProfile = chase.Chase.Profile;
                        afterLegs = chase.After;
                        if (chase.Hop is { } hop) ball.Add(hop);
                    }
                    else if (isBallFielder)
                    {
                        (t0, t1) = ArriveByFieldedAt(t0, t1, fieldedAt);
                        to = StopWithinReach(moveFrom, to, pace * (t1 - t0), ReachMFor(fielder, fieldingCoeff, fielderReachM));
                        if (a.Position == FieldPosition.FirstBase) firstBaseFieldEnd = to; // アンアシスト運びの起点（#400）
                    }
                    moves.Add(new FielderMove { Role = a.Position, T0 = t0, T1 = t1, To = to, Task = task, Profile = chaseProfile });
                    if (afterLegs is not null) moves.AddRange(afterLegs);

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
                        var dist = Distance(from, ballFielderTarget);
                        if (dist >= 0.5)
                        {
                            var speed = Math.Max(4.0, fielder.Attributes.SprintSpeedMps);
                            var (t0, t1) = ArriveByFieldedAt(ContactAt + 0.35, ContactAt + 0.35 + dist / speed, fieldedAt);
                            var stop = StopWithinReach(from, ballFielderTarget, speed * (t1 - t0), ReachMFor(fielder, fieldingCoeff, fielderReachM));
                            if (bfr == FieldPosition.FirstBase) firstBaseFieldEnd = stop; // アンアシスト運びの起点（#400）
                            moves.Add(new FielderMove { Role = bfr, T0 = t0, T1 = t1, To = stop, Task = FielderTask.FieldBall });
                        }
                    }
                }

                // アンアシスト刺殺（3U, Issue #400）: 一塁手が捕球点から自分でベースへ運んで踏む移動レッグを積む。
                // 出発は捕球した実位置(firstBaseFieldEnd)、到達は判定に使った刺殺時刻(ThrowArriveSeconds)へ合わせる
                // （ボールの Carry セグメントと同時にベースへ着く）。投手カバーは firstBaseCoverer=一塁手自身なので
                // 上のループで押し出し済み＝生成されない。体が刺殺時刻までに走り切れない残り（ベースへ踏み込む脚）は
                // reach ぶんで詰める（StopWithinReach。他の運びレッグと同型で脚力を超えさせない, LegSpeedTests）。
                if (play.UnassistedPutout && play.FielderRole == FieldPosition.FirstBase)
                {
                    var firstBaseman = Find(fielders, FieldPosition.FirstBase);
                    if (firstBaseman is not null && play.ThrowArriveSeconds is { } putoutArrive)
                    {
                        var basePt = BasePoint(field, "first");
                        var t0 = fieldedAt;
                        var t1 = Math.Max(t0 + 0.05, ContactAt + putoutArrive);
                        var speed = Math.Max(4.0, firstBaseman.Attributes.SprintSpeedMps);
                        var to = StopWithinReach(firstBaseFieldEnd, basePt, speed * (t1 - t0), ReachMFor(firstBaseman, fieldingCoeff, fielderReachM));
                        if (Distance(firstBaseFieldEnd, to) >= 0.5)
                        {
                            moves.Add(new FielderMove
                            {
                                Role = FieldPosition.FirstBase, T0 = t0, T1 = t1, To = to, Task = FielderTask.CoverBase,
                            });
                        }
                    }
                }
                // 一塁カバーが表に無かった（実捕球者に押し出された）→動的な担当を注入する（Issue #257）。
                // 例: Bunt/InfieldRight でファースト以外が捕った場合、静的表のファーストの行は FieldBall
                // （＝実捕球者でなければ丸ごと降格）なので、このままでは誰も一塁に入らない。
                else if (!firstBaseCovered)
                {
                    var coverer = Find(fielders, firstBaseCoverer);
                    if (coverer is not null)
                    {
                        var from = new TimelinePoint(coverer.Location.X, coverer.Location.Z);
                        var to = FirstBaseCoverPoint(field, fieldPoint);
                        var dist = Distance(from, to);
                        var t0 = ContactAt + 0.35;
                        var speed = Math.Max(4.0, coverer.Attributes.SprintSpeedMps);
                        var t1 = t0 + dist / speed;
                        // ⚠ 既に別の持ち場へ走っている野手には注入しない（体は2箇所に居られない）。
                        // 表の担当（例: 右方向の打球で投手は本塁バックアップ）と、この動的注入が同じ時刻に
                        // 重なると、FielderAt は重なった区間で**両方の行き先へ同時に寄せる**ので合成速度が
                        // 脚力を超える（実測: 右前へのフライを一塁手が捕った seed182 で投手 11.26 m/s。
                        // 一塁へ球は来ておらず、カバー自体が要らない場面だった＝LegSpeedTests が検出）。
                        // 絵では「向きが少し変」としか見えないので、重なりそのものを作らせない。
                        if (dist >= 0.5 && !IsBusy(moves, firstBaseCoverer, t0, t1))
                            moves.Add(new FielderMove { Role = firstBaseCoverer, T0 = t0, T1 = t1, To = to, Task = FielderTask.CoverBase });
                    }
                }

                // 寄る/飛びつくレッグの仕上げ（Issue #336②）: 最も惜しかった内野手は、ゾーン役割が
                // CoverBase 等（自動の定位置復帰を持たない）でも、決着後は必ず定位置へ戻り切る。
                // 既に Backup/Cutoff の自動復帰で定位置に着いている場合は距離0扱いで二重には積まない。
                foreach (var missRoleFinal in new[] { missRole, missRole2 })
                {
                    if (missRoleFinal is null) continue;
                    FielderMove? lastMove = null;
                    foreach (var m in moves)
                    {
                        if (m.Role == missRoleFinal.Value) lastMove = m;
                    }
                    if (lastMove is { } lm)
                    {
                        var missFielder = Find(fielders, missRoleFinal.Value);
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
                                    Role = missRoleFinal.Value, T0 = rt0, T1 = rt1, To = homePost, Task = FielderTask.Chase,
                                });
                                if (rt1 + 0.5 > returnTail) returnTail = rt1 + 0.5;
                            }
                        }
                    }
                }
            }
        }

        /// <summary>段8 フライ落球: 落とした球を足元へ転がし、落とした本人を追いかけ役にする。</summary>
        private void AddFlyDropError()
        {
            // フライ落球（issue #629 ④）: 送球が存在しない（ThrowArriveSeconds=null）ため上の失策球ブロックへ
            // 一切入らず、球が着地点に貼り付いて静止していた＝外野失策で走者が還る場面がいちばん
            // 「野手がぼーっとしている」ように見えていた原因。落とした球は足元へ転がり、落とした本人が拾い直す。
            // ゴロの捕球エラー（お手玉）とまったく同じ形＝弾む距離・時間・追いかける野手の決め方を共有する。
            if (play.IsFly && play.Result == BattedBallResult.Error && errorBallRest is null)
            {
                var dropRest = Overshoot(Home, fieldPoint, ErrorDeflectM);
                ball.Add(new BallSegment
                {
                    Kind = BallSegmentKind.Roll,
                    T0 = fieldedAt, T1 = fieldedAt + ErrorDeflectSeconds,
                    From = fieldPoint, To = dropRest,
                    FromRole = play.FielderRole,
                });
                errorBallRest = dropRest;
                errorBallDeadAt = fieldedAt + ErrorDeflectSeconds;
                errorChaser = play.FielderRole;
            }
        }

        /// <summary>段9 失策球の追いかけ: 悪送球で塁を離れる一歩と、こぼれた/逸れた球を拾いに行くレッグ。</summary>
        private void AddErrorChase()
        {
            // エラーでこぼれた/逸れた球は野手が追いかけて拾いに行く（issue #439 ①）。
            // 捕球エラー＝弾いた本人が拾い直す／悪送球＝一塁カバーが振り返って追う。
            // 表示専用の定点計算のみ（乱数不使用）。3D側はエラープレーの決着後レッグを剥がさない
            // （StripPostResolutionReturnMoves の例外）ことで、このレッグが絵に出る。
            if (wildStep is { } step && errorChaser is { } stepRole)
            {
                // 自分の直前のレッグ（一塁カバーへの走り込み）が終わってからでないと離れない＝走行中に重ねない
                var busyUntil = moves.Where(m => m.Role == stepRole).Select(m => m.T1).DefaultIfEmpty(double.MinValue).Max();
                if (busyUntil <= step.T0)
                    moves.Add(new FielderMove { Role = stepRole, T0 = step.T0, T1 = step.T1, To = step.To, Task = FielderTask.CoverBase });
            }
            if (errorBallRest is { } chaseTo && errorChaser is { } chaserRole)
            {
                var chaser = Find(fielders, chaserRole);
                if (chaser is not null)
                {
                    var speed = Math.Max(4.0, chaser.Attributes.SprintSpeedMps);
                    // 一塁手の捕球ミス（Receive, issue #810）は球が止まる基準が「送球が一塁に着いた瞬間」
                    // （firstThrowArriveAt。弾きロールの開始＝errorBallDeadAt の起点と同じ値）。
                    // 拾い直せず確定した捕球ミスは反応の秒を判定側（回収レース）が持つ＝追いかけは拾い直しの時刻に着くよう逆算する
                    var catchReadyAt = CatchErrorReadyAt(play);
                    var t0 = (play.ErrorKind == FieldingErrorKind.Receive && firstThrowArriveAt is { } receiveChaseFrom
                            ? receiveChaseFrom
                            : play.ErrorKind == FieldingErrorKind.Throw ? ContactAt + (play.ThrowArriveSeconds ?? 0.0) : fieldedAt)
                             + (catchReadyAt is null ? ErrorChaseReactionSeconds : 0.0);
                    // ⚠ 自分の直前のレッグ（一塁カバーへの走り込み等）が終わってから追いかけ始め、
                    // 出発点はそのレッグの行き先＝**実際に立っている位置**から距離を出す。
                    // 走行中に重ねたり出発点を推測すると2本の移動が合成され、表示速度が人間の脚力を超える
                    // （CI実測: 投手が一塁カバーの途中から追いかけて 19.9 m/s／一塁手のアンアシスト系で
                    // 出発点の推測がずれて 22.3 m/s。LegSpeedTests が検出）
                    var chaseFrom = new TimelinePoint(chaser.Location.X, chaser.Location.Z);
                    var lastT1 = double.MinValue;
                    foreach (var m in moves)
                    {
                        if (m.Role != chaserRole) continue;
                        if (m.T1 > t0) t0 = m.T1;
                        if (m.T1 > lastT1) { lastT1 = m.T1; chaseFrom = m.To; }
                    }
                    // 球が止まる前に着かない（先回りして待つ絵にしない）
                    var travelSeconds = Distance(chaseFrom, chaseTo) / speed;
                    if (catchReadyAt is { } readyAt) t0 = Math.Max(t0, readyAt - travelSeconds);
                    var t1 = Math.Max(t0 + travelSeconds, errorBallDeadAt);
                    moves.Add(new FielderMove
                    {
                        Role = chaserRole, T0 = t0, T1 = t1, To = chaseTo, Task = FielderTask.Chase,
                    });
                    if (t1 + 0.5 > returnTail) returnTail = t1 + 0.5;
                    // 拾い上げる瞬間を明示的に控える（issue #629 ③）。返球はこの後 GameEngine が
                    // AppendErrorReturnThrow で積む＝本塁送球を描いた打席では積まない、という判断を
                    // 「本塁へ投げるか」を知っている側（GameEngine）に残すため、ここでは投げない。
                    errorPickupAt = t1;
                    errorPickupPoint = chaseTo;
                    errorPickupRole = chaserRole;
                }
            }
        }

        /// <summary>段10 悪送球を拾い直してアウト: 拾う役が逸れた球へ走るレッグ。</summary>
        private void AddRecoveredPickup()
        {
            // 悪送球を拾い直してアウト（issue #815）: 拾う役が逸れた球へ走り、拾い上げる時刻にちょうど着く。
            // 出発は自分の直前のレッグ（一塁カバー・バックアップの寄り）の行き先から＝走行中に重ねない
            // （エラーの追いかけと同じ作法。LegSpeedTests の脚力上限を守る）。
            if (recoveredLanding is { } pickTo && recoveredRole is { } pickRole && Find(fielders, pickRole) is { } picker)
            {
                var speed = Math.Max(4.0, picker.Attributes.SprintSpeedMps);
                var from = new TimelinePoint(picker.Location.X, picker.Location.Z);
                var t1 = Math.Max(recoveredPickupAt, recoveredBallDeadAt);
                // 起点は拾う時刻より前に始まったレッグだけから採る（決着後の定位置戻りを起点にしない）
                var lastT1 = ContactAt;
                foreach (var m in moves)
                {
                    if (m.Role != pickRole || m.T0 >= t1 || m.T1 < lastT1) continue;
                    lastT1 = m.T1; from = m.To;
                }
                var t0 = Math.Max(lastT1, t1 - Distance(from, pickTo) / speed);
                t1 = Math.Max(t1, t0 + Distance(from, pickTo) / speed);
                moves.Add(new FielderMove { Role = pickRole, T0 = t0, T1 = t1, To = pickTo, Task = FielderTask.Chase });
                if (t1 + 0.5 > returnTail) returnTail = t1 + 0.5;
                if (recoveredOutAt + 0.5 > returnTail) returnTail = recoveredOutAt + 0.5;
            }
        }

        /// <summary>段11 結果の実況: ゴロ処理の中間実況と結果文言。</summary>
        private void AddResultCaptions()
        {
            // ゴロ処理の中間実況（捕球→送球/生還の間）。フライ捕球は結果=捕球なので中間は出さない。
            if (!play.IsFly && play.FielderRole is { } captured && fieldedAt < resolvedAt - 0.05)
            {
                captions.Add(new TimelineCaption(fieldedAt, $"{PositionName(captured)}が捕った"));
            }
            captions.Add(new TimelineCaption(resolvedAt, resultText));
        }

        /// <summary>段12 尺（余韻・定位置復帰・ボール区間を覆う）を決めて <see cref="PlayTimeline"/> にまとめる。</summary>
        private PlayTimeline ToTimeline()
        {
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
                // 表示専用の観測（既に決まった値の写し＝新規計算・乱数消費ゼロ）
                CatchMarginSeconds = play.CatchMarginSeconds,
                // 拾い直してアウトに戻った打席（issue #817）も実際には弾いている＝真のまま広げる。
                FumbledCatch = play.ErrorKind == FieldingErrorKind.Catch || play.RecoveredCatchScatterM is not null,
                BobbleError = play.ErrorKind == FieldingErrorKind.Bobble,
                HomeRunStyle = play.HomeRunStyle,
                Ball = ball,
                Moves = moves,
                Runners = runners,
                Captions = captions,
                Judgements = judgements,
                ErrorPickupAt = errorPickupAt,
                ErrorPickupPoint = errorPickupPoint,
                ErrorPickupRole = errorPickupRole,
            };
        }
    }
}
