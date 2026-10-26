using System;
using System.Collections.Generic;
using KokoSim.Engine.Match.Field;

namespace KokoSim.Engine.Match.Timeline.Playback;

// ─────────────────────────────────────────────────────────────────────────────
// タイムライン駆動のカメラワーク（design-06/design-12・Issue #119）。
// PlaybackPlay から「再生開始前に」カメラキーフレーム列を生成する純関数（UnityEngine非依存）。
// リアルタイム追跡ではない＝プレーが確定済みであることを前提に、未来のボール位置も先読みして使う。
//
// Viewport は field-space の中心 (Cx,Cy)[m] とズーム倍率 ZoomMult の3値。ZoomMult=1 は
// 「全景（Match2DPlaybackElement の既存固定ビュー＝デザインキャンバス DesignW=760/DesignH=520/S=3.35
// が丸ごと収まる状態）」と定義する（3定数はUnity側と同期。変更時は両方合わせる）。
// Unity側は既存の Scale(k) にこの ZoomMult を掛け、中心を (Cx,Cy) に取り直すだけで描画へ反映できる
// （線の太さ・トークン半径は k 由来のスケールのまま＝画面上で一定、座標だけがズームで動く）。
// ─────────────────────────────────────────────────────────────────────────────

/// <summary>カメラ視野（field-space中心[m]・ズーム倍率）。</summary>
public readonly record struct CameraViewport(double Cx, double Cy, double ZoomMult);

/// <summary>
/// フレーミングの詰め方（ビューごとに変える3つ）。演出の判断（何を追うか・いつ全景へ戻すか）や
/// レート制限（酔い防止）は共有し、**どれだけ寄るか**だけを差し替えるための束。
///
/// 3Dの守備カメラは2Dの俯瞰より深く寄る必要がある: <see cref="CameraFraming.Default"/> の
/// <c>MaxZoom=3.4</c> は縦45.6mに相当し、身長1.75mの野手が480×270のRTで十数pxにしかならない。
/// 外野フライで <c>FitBox</c> が求めるズームは実測5.75で、**上限に頭打ちされている**。
/// </summary>
/// <param name="MaxZoom">ズーム倍率の上限（1=全景）。大きいほど深く寄る。</param>
/// <param name="MarginM">
/// 被写体まわりに残す余白[m]。**寄りの深さを実際に決めているのはこちら**で、<paramref name="MaxZoom"/> ではない。
/// <c>FitBox</c> は箱に 2×Margin を足してからズームを決めるので、箱が点まで縮んでも
/// frameH は 2×Margin より小さくならない（既定 6.0m なら上限 12.9倍で頭打ち）。
/// </param>
/// <param name="PitchZoom">投球区間の固定フレームのズーム倍率。</param>
/// <param name="ThrowFollowsBall">
/// 送球区間の枠の取り方。<c>false</c>（既定・2D俯瞰）＝**始点と終点を両方収める**。
/// 小さな盤面で送球の向きを読ませる作法だが、寄りたい3Dでは「6-3 なら遊撃〜一塁の30mを必ず枠に入れる」
/// ＝どれだけ Margin を詰めても寄れない、という形で効く（実測: 決着の瞬間が frameH 20〜56m ＝
/// そのプレーで最も引いた絵になる）。<c>true</c> なら打球中と同じく**球を先読みで追う**＝
/// 送球が届くにつれ枠が締まり、受け手の捕球で最も寄る（中継カメラの作法）。
/// </param>
/// <param name="LooseBallFollowsChaser">
/// 球が死んで（どのセグメントにも属さず）まだ**野手が拾いに走っている**あいだ、被写体を
/// 「球＋その野手」にする（issue #566）。
///
/// <para><see cref="HeldBallFollowsRunner"/> は「野手が球を<b>持っている</b>あいだ」の作法
/// （振り逃げ＝捕手が本塁で握ったまま、打者走者が一塁へ走る。issue #488）。暴投・捕逸は同じ分岐に
/// 落ちるが**まだ誰も持っていない**——捕手はバックネットへ走っている最中で、走者は25〜40m先に居る。
/// ここで走者を枠へ入れようとすると、枠は 2倍/秒 でしか広がらないのにアイムは 45m/s で走れるため、
/// **枠が狭いまま被写体と被写体のあいだの地面を舐める**（実測 2026-08-10: 0.40秒だれも枠に居ない）。</para>
///
/// <para>拾いに走っている野手は球へ収束するので、この枠は 4〜6m のまま＝寄った絵を保ったまま
/// 「捕手が球を追っている」が読める（実測: 盲点0コマ）。走者を入れる案は枠が最大47.8m必要で、
/// 人が20pxになる（打球処理の3D化で退けた「点が動くだけ」の絵に近づく）。</para>
///
/// <para>2D俯瞰は全景に全部入るので既定オフ（＝ゴールデン不変）。3Dの守備カメラだけが渡す。</para>
/// </param>
/// <param name="ThrowBoxGrowsWithBall">
/// 「始点と終点を両方収める」送球の枠（<paramref name="ThrowFollowsBall"/>=false 側）を、
/// 投げた瞬間に完成形へ切り替えるのではなく<b>投げ手から球へ伸ばす</b>（issue #566）。
///
/// <para>切り替え式は枠が階段状に跳ぶ。2D俯瞰は全景に近いので跳んでも絵が変わらないが、
/// 寄っている3Dでは跳んだ先の枠（送球距離ぶん＝20〜30m）へズームが 2倍/秒 で追いつく前に、
/// アイムだけが 45m/s で箱の中心＝<b>投げ手と受け手のあいだの何も無い地面</b>へ着いてしまう。</para>
///
/// <para>伸ばす式なら t=送球開始で箱は投げ手の一点＝いまカメラが居る所そのもので、球の飛翔と
/// ともに広がって、届いた時にちょうど始点・終点の枠になる。跳ばないのでレート制限が追いつけ、
/// 投げ手は最初から最後まで枠の中に居る（＝中継カメラの「引きながら送球を追う」形）。</para>
///
/// <para>2D俯瞰は既定オフ（＝ゴールデン不変）。3Dの守備カメラだけが渡す。</para>
/// </param>
/// <param name="KeepSubjectFramed">
/// パンとズームを連動させる（issue #566）。<b>枠に人が1人も居ないコマだけ</b>、いちばん近い人が
/// 入るところまで枠を広げる。
///
/// <para>なぜ要るか: パン上限 45m/s に対しズーム上限は 2倍/秒 しかない。狭い枠（4〜6m）のまま
/// 被写体が十数m先へ移ると、アイムは 0.3秒で着くのに枠は 2.5秒かけないと開かない＝
/// <b>枠が狭いまま被写体と被写体のあいだの地面を舐める</b>。「引く→振る→寄る」の"引く"が
/// 間に合っていないのが正体で、パンを遅くしても直らない（実測 2026-08-10: 遅くすると
/// 0.40秒→1.85秒に悪化する。カメラが遅れて後ろの地面を映すだけになる）。</para>
///
/// <para>効き方は片側だけ＝<b>広げるのみ</b>。寄る側のレート制限（ジャーク防止）はそのまま残す。
/// 判定は「生の枠が画面に入っているか」ではなく<b>実際に人が居るか</b>で行う（代用すると、
/// 追いつけているだけで盲点の無いプレーまで巻き込む。実測は
/// <see cref="CameraPlanBuilder"/> の該当メソッドのコメント）。</para>
///
/// <para>全景復帰（<see cref="CameraPlanBuilder.WideStartTime"/> 以降）には効かせない。全景は
/// 「収めるべき被写体」ではなく戻り先なので、ここで連動させると復帰の1コマ目で全景へ飛ぶ。</para>
///
/// <para>2D俯瞰は全景に全部入るので既定オフ（＝ゴールデン不変）。3Dの守備カメラだけが渡す。</para>
/// </param>
/// <param name="ReturnsToWide">
/// <see cref="CameraPlanBuilder.WideStartTime"/> 以降に全景（縦155m）へ戻すか（issue field-view-cut-back-sooner）。
///
/// <para><c>true</c>（既定・2D俯瞰）＝従来どおり全景へ戻す。2Dは全景に全部入るので戻して問題ない。</para>
///
/// <para><c>false</c>（3Dの守備ビュー）＝<b>戻さず、その時刻の枠のまま止める</b>。寄った3Dで全景へ跳ぶと
/// 「用のないところを見ている」ハードカットになり、居残りそのものより<b>そのカットが悪目立ちする</b>
/// （監督指示 2026-08-13）。この挙動は <c>CameraPlan</c> のコメントが以前から書いていたもので、
/// 実装だけが全景へ戻していた＝コメント側が正しかった。</para>
///
/// <para>ただし本塁打の一周のように<b>まだ走っている走者</b>が残るなら、止めた枠から走者が出ていくので
/// 「走者＋向かう塁」を収める枠へ切り替える（1人＋1塁間＝27.43m＋余白。走者を全部入れようとして
/// 47.8m 必要になり人が20pxになった issue #566 の失敗を繰り返さない大きさ）。</para>
/// </param>
public readonly record struct CameraFraming(
    double MaxZoom, double MarginM, double PitchZoom, bool ThrowFollowsBall = false,
    bool HeldBallFollowsRunner = false, bool LooseBallFollowsChaser = false,
    bool ThrowBoxGrowsWithBall = false, bool KeepSubjectFramed = false,
    bool ReturnsToWide = true)
{
    /// <summary>2D俯瞰の既定（Issue #119 で決めた現行値）。渡さなければこれが使われる。</summary>
    public static readonly CameraFraming Default = new(3.4, 6.0, 2.4);
}

/// <summary>カメラプランのキーフレーム1点。区間は easeIO で補間する（<see cref="PlaybackEvaluator.EaseIO"/> と同じ形）。</summary>
public readonly record struct CameraKeyframe(double T, double Cx, double Cy, double ZoomMult);

/// <summary>
/// PlaybackPlay からカメラキーフレーム列を生成する（design-06/design-12・Issue #119）。
/// 一定間隔でサンプリングした「あるべき視野」を、ズーム変化率・パン速度の上限でレート制限しながら
/// 追従させて作る（酔い防止）。事前計画型ゆえ、未来の必要点を少し先取り（<see cref="LeadSeconds"/>）
/// して追従させることで、上限があっても実際の被写体からの遅れを抑える。
/// </summary>
public static class CameraPlanBuilder
{
    // 全景の基準（Match2DPlaybackElement.cs の DesignW=760/DesignH=520/S=3.35 と同期）。
    private const double DesignWidthM = 760.0 / 3.35;
    private const double DesignHeightM = 520.0 / 3.35;
    public const double WideCx = 0.0;
    public const double WideCy = (492.0 - 520.0 / 2.0) / 3.35; // ≈69.25m（本塁〜デザインキャンバス中心の縦オフセット）
    public const double WideZoom = 1.0;

    // 数値は完了条件（酔わない・被写体を捉え続ける）を満たすための実装判断値。要調整ならここを直す。
    // MaxZoom / MarginM / PitchZoom は CameraFraming でビューごとに差し替えられる（既定は下の値）。
    public const double LookaheadSeconds = 0.6;         // 「現在のボール位置＋0.6秒先」
    private const double SampleStepSeconds = 0.08;      // キーフレーム間隔（十分密＝easeIO区間はほぼ線形）
    private const double LeadSeconds = 0.25;            // 先取りサンプリング（事前計画ゆえの遅れ対策）
    // ResAt から全景へ戻すまでの間。3Dの守備ビューは全景へ戻さず**この時刻の枠のまま止める**
    // （2Dは全景に全部入るので戻して問題ないが、寄った3Dでは「用のないところを見ている1.5秒」になる）
    // ので、戻し始める時刻を単一ソースとして公開する。
    public const double ReturnToWideDelaySeconds = 0.9;
    // 死んだ球（全ボールセグメント終了後）を見せておく余韻（issue #441。着弾のテロップ #354 を読む間）。
    // これを過ぎたら走者レッグの残り（lastEvent）を待たずに全景へ戻す。
    public const double DeadBallLingerSeconds = 1.0;
    private const double WideSettleBufferSeconds = 3.0;  // 全景へのレート制限付き復帰が収束するまでの生成余白（実再生は play.Dur までしか参照しないため無害）
    private const double MaxPlanSeconds = 120.0;         // キーフレーム生成の上限（#208 暴走・OOM 防止。1プレーは現実に数秒）

    public const double ZoomRateCapPerSecond = 2.0;  // ズーム変化は毎秒2倍以内
    public const double PanSpeedCapMPerSecond = 45.0; // パン速度上限（実測球速に追従しつつ急旋回を避ける値）

    /// <param name="framing">
    /// 寄りの深さ。省略＝<see cref="CameraFraming.Default"/>＝2D俯瞰の現行値で、
    /// **渡さなければ出力は1キーフレームも変わらない**（既存の呼び出しとゴールデンは無傷）。
    /// 3Dの守備カメラは <c>MaxZoom</c> を上げた別セットを渡す。
    /// </param>
    /// <param name="startSeconds">
    /// 再生を始める時刻[秒]。**0以外を渡すのは「その手前を見せない」プレーだけ**（打席途中の盗塁は
    /// 投球を対決ビューで見せ終わってから守備ビューへ切り替わる＝投球区間を二度描かない）。
    /// レート制限は「前のキーフレームからの変化量」に効くので、t=0 から組んだプランを途中から
    /// 読むと**頭が全景のまま**になる（実測 2026-08-06: 盗塁は頭の1.5秒が枠64.7m＝人18px、
    /// ズーム上限 2倍/秒では 3.7秒のプレー中に寄り切れない）。ここから組めば最初のキーフレームが
    /// レート制限なしで採られる＝頭から寄った絵で始まる。既定 0＝従来と1キーフレームも変わらない。
    /// </param>
    public static IReadOnlyList<CameraKeyframe> Build(PlaybackPlay play, CameraFraming? framing = null,
                                                      double startSeconds = 0.0)
    {
        var fr = framing ?? CameraFraming.Default;
        // 「まだ見るものがある」最後の瞬間（決着／球が動き終わる／実際に動く走者レッグの終わり）を
        // 全景復帰の起点にする（issue #406②）。ResAt は「打球が処理された時刻」でしかなく、犠飛の
        // タッチアップ〜生還のように決着後にプレーの本番が続く場合はそこで戻すと早すぎる。
        // ResAt が +∞（結果チップを出さない投球のみのプレー＝PitchPlaybackFactory）なら
        // LastEventTime も +∞ のまま＝下の IsFinite ガードは無傷（#208 の OOM 対策を維持）。
        var lastEvent = LastEventTime(play);
        var resEnd = double.IsFinite(lastEvent) ? lastEvent + ReturnToWideDelaySeconds : 0.0;
        var dur = double.IsFinite(play.Dur) ? play.Dur : 0.0;
        // 1プレーの再生は現実的に数秒。異常に長い（or 非有限）継続時間でもキーフレーム暴走で OOM しないよう上限を設ける。
        var end = Math.Min(Math.Max(dur, resEnd) + WideSettleBufferSeconds, MaxPlanSeconds);
        var wideStart = WideStartTime(play);
        var kfs = new List<CameraKeyframe>();

        double cx = WideCx, cy = WideCy, zoom = WideZoom;
        // 開始時刻より手前のサンプルは打たない（レート制限の履歴も持ち込まない＝頭は生の目標値）。
        var start = double.IsFinite(startSeconds) && startSeconds > 0.0 ? Math.Min(startSeconds, end) : 0.0;
        var prevT = start;
        var first = true;
        foreach (var clampedT in SampleTimes(play, end, start))
        {
            if (clampedT < start) continue;
            var raw = RawTargetAt(play, clampedT, Math.Min(clampedT + LeadSeconds, end), fr, wideStart);
            if (first)
            {
                cx = raw.Cx; cy = raw.Cy; zoom = raw.ZoomMult;
                first = false;
            }
            else
            {
                // 実際に経過した時間（境界サンプルは間隔が SampleStepSeconds より短くなり得る）でレート制限する。
                var dt = clampedT - prevT;
                zoom = ClampZoomRate(zoom, raw.ZoomMult, dt);
                (cx, cy) = ClampPan(cx, cy, raw.Cx, raw.Cy, dt);
                // パンとズームの連動（issue #566）。上の2つは互いを知らないので、パンだけが先に走って
                // 「狭い枠が被写体のあいだの地面を舐める」区間ができる。遅れたぶんだけ枠を広げて塞ぐ。
                // 全景復帰は戻り先であって被写体ではないので効かせない（1コマ目で全景へ飛ぶ）。
                if (fr.KeepSubjectFramed && clampedT < wideStart)
                    zoom = ZoomKeepingSomeoneFramed(play, clampedT, cx, cy, zoom, fr);
            }
            kfs.Add(new CameraKeyframe(clampedT, cx, cy, zoom));
            prevT = clampedT;
        }
        return kfs;
    }

    /// <summary>
    /// キーフレームを打つ時刻の列（昇順・重複なし）。一定間隔のサンプリングに加えて、
    /// ボールセグメントの境界時刻（投球→打球等の切り替わり）も必ず打つ。等間隔サンプリングだけだと
    /// 区間の変わり目が2キーフレームの間（easeIO補間区間）に埋もれ、実際の切り替わりより早く
    /// 視野が動き出してしまう（Issue #258: 投球中に打つ前からカメラが動く症状の残存要因）。
    ///
    /// 等間隔グリッドの累積誤差とセグメント境界の実数値は、同じ時刻を指していてもビット単位では
    /// 一致しないことがある（例: 0.08*45 と literal 3.60）。<c>SortedSet&lt;double&gt;</c> はビット等価
    /// でしか重複を除けないため、ほぼ同時刻の2キーフレームが極小の dt（浮動小数の丸め誤差オーダー）で
    /// 並んでしまう。<see cref="ClampPan"/> の「dist&lt;1e-9 なら素通し」救済と噛み合うと、
    /// 直前キーフレームがまだ収束し切っていない残差がこの極小 dt に対して「速度上限超過」に見える
    /// （issue #448 回帰）。実時間として意味のある間隔だけを残す。
    /// </summary>
    private static IEnumerable<double> SampleTimes(PlaybackPlay play, double end, double start = 0.0)
    {
        const double MinGapSeconds = 1e-6; // 浮動小数の丸め誤差より十分大きく、演出上は無視できる間隔

        var raw = new SortedSet<double>();
        for (var t = 0.0; t < end; t += SampleStepSeconds)
            raw.Add(t);
        raw.Add(end);
        // 途中から組むときは開始時刻ちょうどにも必ず1本打つ（等間隔グリッドが跨いだままだと、
        // 頭のキーフレームが実際の開始より後ろに来て、その手前は先頭値へクランプされる）。
        if (start > 0 && start < end) raw.Add(start);

        foreach (var seg in play.Ball)
        {
            if (seg.T0 > 0 && seg.T0 < end) raw.Add(seg.T0);
            if (seg.T1 > 0 && seg.T1 < end) raw.Add(seg.T1);
        }

        var times = new List<double>(raw.Count);
        foreach (var t in raw)
        {
            if (times.Count > 0 && t - times[^1] < MinGapSeconds) continue;
            times.Add(t);
        }
        return times;
    }

    /// <summary>
    /// 「まだ見るものがある」最後の瞬間: 決着(<see cref="PlaybackPlay.ResAt"/>)・球が動き終わる時刻・
    /// 実際に動く走者レッグの終わりのうち最大値（issue #406②。MatchLiveController.PlayEndTime と
    /// 同じ作法で、全景へ戻す時刻の単一ソースにする＝engine のプラン（このメソッド経由）と3D側の
    /// カメラカットの両方がここを見る。片方だけ直すとプランと実カットがずれる）。
    ///
    /// 決着後だけに起きる野手の定位置復帰（Backup/Cutoff の戻り等）は <see cref="PlaybackPlay.Moves"/>
    /// を見ないこの式には自然と入らない＝含めると①「投手が帰る絵」を待つあいだ全景復帰が遅れて再発する。
    /// </summary>
    public static double LastEventTime(PlaybackPlay play)
    {
        var lastEvent = play.ResAt;
        foreach (var seg in play.Ball)
            if (seg.T1 > lastEvent) lastEvent = seg.T1;
        foreach (var runner in play.Runners)
            foreach (var seg in runner.Segs)
            {
                if (seg.From.X == seg.To.X && seg.From.Y == seg.To.Y) continue; // その場に居るだけ
                if (seg.T1 > lastEvent) lastEvent = seg.T1;
            }
        return lastEvent;
    }

    /// <summary>
    /// 全景へ戻し始める時刻（issue #406②/#441 の単一ソース。engine のプランと3D側のカット時刻の
    /// 両方がここを見る＝片方だけ変えるとプランと実カットがずれる）。
    /// 通常は lastEvent + <see cref="ReturnToWideDelaySeconds"/>。死んだ球（全ボールセグメント終了後）に
    /// 走者レッグだけが残るプレー（本塁打の一周など）では、着弾のテロップ（#354）を読む余韻
    /// （<see cref="DeadBallLingerSeconds"/>）明けへ前倒しする（issue #441: 着弾点のアップが生還まで
    /// 続きフリーズに見えた）。ResAt=+∞（投球のみのプレー）は従来どおり戻さない。
    /// </summary>
    public static double WideStartTime(PlaybackPlay play)
    {
        var lastEvent = LastEventTime(play);
        if (!double.IsFinite(lastEvent)) return double.PositiveInfinity;
        return Math.Min(lastEvent + ReturnToWideDelaySeconds, LastBallEndTime(play) + DeadBallLingerSeconds);
    }

    /// <summary>全ボールセグメントが終わる時刻（セグメントが無ければ 0）。死んだ球の判定（issue #441）用。</summary>
    private static double LastBallEndTime(PlaybackPlay play)
    {
        var last = 0.0;
        foreach (var seg in play.Ball)
            if (seg.T1 > last) last = seg.T1;
        return last;
    }

    private static double ClampZoomRate(double prev, double target, double dt)
    {
        var maxRatio = Math.Pow(ZoomRateCapPerSecond, dt);
        var lo = prev / maxRatio;
        var hi = prev * maxRatio;
        return Math.Clamp(target, lo, hi);
    }

    /// <summary>
    /// 「枠に人が1人も居ない」コマだけ、いちばん近い人が入るまで枠を広げる（issue #566）。
    /// 誰か1人でも枠に居れば <paramref name="zoom"/> をそのまま返す＝<b>何もしない</b>。
    ///
    /// <para>判定を「生の枠が画面に入っているか」で代用してはいけない。それだと**追いつけて
    /// いるだけで盲点の無いプレーまで巻き込んで広がる**（実測 2026-08-10: 生の箱ぜんぶを要求すると
    /// 中継バックホームの枠が 64.7m→123.5m、振り逃げが 8.9m→40.3m。2点のどちらかへ緩めても
    /// 中継が最大6.9倍に膨らんだ。どちらも元から盲点0コマ＝広げる必要がまったく無いプレー）。
    /// 野手・走者の位置は <see cref="PlaybackPlay"/> が持っているので、<b>実際の条件そのもの</b>で
    /// 判定できる。代用を挟まないぶん、必要なコマだけに効く。</para>
    ///
    /// <para>球は数えない。「球だけが映っていて人が居ない」は issue #566 が直したい絵そのもの
    /// （＝空の地面の上を球が飛んでいく）。</para>
    ///
    /// <para>この平面の枠は3Dの実カメラより<b>狭い</b>（斜めに見下ろすぶん地面は奥へ伸びる。
    /// 実測 2026-08-10: 実カメラに写っている人が平面の枠の最大7.2倍先に居ることがある）。
    /// つまりこの判定は安全側＝「平面の枠に居る＝実カメラにも必ず写っている」。逆は言えないので、
    /// 広げすぎないように判定側の余白は0にしてある。</para>
    /// </summary>
    private static double ZoomKeepingSomeoneFramed(PlaybackPlay play, double t,
        double cx, double cy, double zoom, CameraFraming fr)
    {
        var nearest = 0.0;
        foreach (var p in ActorsAt(play, t))
        {
            // 判定は余白なし＝**枠の中に居るか**そのもの。ここに余白を足すと厳しすぎて、
            // 盲点の無いプレーまで広がる（実測 2026-08-10: 余白2mを足すと振り逃げが 8.9m→20.9m、
            // 中継バックホームが最大4.6倍に膨らんだ。どちらも元から盲点0コマ）。
            if (ZoomKeeping(cx, cy, p, fr, 0.0) >= zoom) return zoom;
            // 広げる先には余白を足す（画面の端に張り付いた人は「映っている」と言い難い）。
            var z = ZoomKeeping(cx, cy, p, fr, fr.MarginM);
            if (z > nearest) nearest = z;
        }
        return nearest > 0.0 ? nearest : zoom;   // 誰も居ない＝いちばん近い人が入るところまで広げる
    }

    /// <summary>点 <paramref name="p"/> が <paramref name="margin"/> ぶん内側に入る最大のズーム倍率。</summary>
    private static double ZoomKeeping(double cx, double cy, PlaybackVec p, CameraFraming fr, double margin)
    {
        var halfW = Math.Abs(p.X - cx) + margin;
        var halfH = Math.Abs(p.Y - cy) + margin;
        var zoom = Math.Min(DesignWidthM / (halfW * 2.0), DesignHeightM / (halfH * 2.0));
        return Math.Clamp(zoom, WideZoom, fr.MaxZoom);
    }

    /// <summary>
    /// 時刻 t に盤面に居る、<b>そのプレーに関与している</b>人（動き出した野手＋走者）。
    /// 列挙順は <see cref="FieldPosition"/> の宣言順→走者の並び順＝辞書の並びに依らず決定論（不変条件#2）。
    ///
    /// <para>定位置に立ったまま一度も動かない野手は候補にしない（issue
    /// fence-camera-zoom-and-outfielder-facing）。本塁打・長打で誰も追いつけない打球では、
    /// タイムラインが動かしていない無関係な野手（例: 反対方向の外野手・動かないバッテリー）を
    /// 画面へ収めようとして <see cref="ZoomKeepingSomeoneFramed"/> が不要にズームアウトする（実測:
    /// センター方向へ直線的に飛ぶ本塁打サンプルで、絞り込み前はズームが 6.88→3.72 倍のあいだで
    /// 振動し続け、絞り込み後は振動が消えて着地まで滑らかに最大10.72倍まで寄った）。
    /// バッテリー（投手・捕手）を無条件の例外にすると、本塁打のように誰も動かない打球で
    /// 静止したバッテリー自身が「遠い候補」として残り振動が消えなかった（実測で確認）ため、
    /// 例外を設けず全ポジションを同じ基準（Moves登録＋動き出し）で扱う。暴投・振り逃げ等で
    /// バッテリーが唯一の関与者になる場面（issue #566 の想定）は、その捕手・投手自身が
    /// Movesへ登録されて動くのでこの基準でも拾える。</para>
    /// </summary>
    private static IEnumerable<PlaybackVec> ActorsAt(PlaybackPlay play, double t)
    {
        foreach (FieldPosition pos in Enum.GetValues(typeof(FieldPosition)))
        {
            if (pos == FieldPosition.DesignatedHitter) continue;   // 守備に就かない
            var at = PlaybackEvaluator.FielderAt(play, pos, t).Pos;
            // 球を持っている／すぐそばに居る野手も関与者（issue #689）。三塁手のように定位置で捕る野手は
            // Moves に載らないので、動き出しだけで判定すると保持中に「誰も居ない」と見なされて枠が広がる。
            if (!HasStartedMoving(play, pos, t) && !IsAtBall(play, at, t)) continue;
            yield return at;
        }
        foreach (var runner in play.Runners)
            if (PlaybackEvaluator.RunnerAt(runner, t) is { } p)
                yield return p;
    }

    private const double AtBallRadiusM = 1.5;

    /// <summary>野手が時刻 t の球のすぐそば（<see cref="AtBallRadiusM"/> 以内）に居るか。</summary>
    private static bool IsAtBall(PlaybackPlay play, PlaybackVec at, double t)
    {
        var ball = PlaybackEvaluator.BallAt(play, t);
        if (!ball.HasValue) return false;
        var dx = at.X - ball.Value.X;
        var dy = at.Y - ball.Value.Y;
        return dx * dx + dy * dy <= AtBallRadiusM * AtBallRadiusM;
    }

    /// <summary>時刻 t までに、この野手の予定された動き（<see cref="PlaybackPlay.Moves"/>）が始まっているか。</summary>
    private static bool HasStartedMoving(PlaybackPlay play, FieldPosition pos, double t)
    {
        if (play.Moves == null || !play.Moves.TryGetValue(pos, out var moves) || moves.Count == 0) return false;
        foreach (var m in moves)
            if (t >= m.T0) return true;
        return false;
    }

    private static (double Cx, double Cy) ClampPan(double px, double py, double tx, double ty, double dt)
    {
        var maxDist = PanSpeedCapMPerSecond * dt;
        var dx = tx - px;
        var dy = ty - py;
        var dist = Math.Sqrt(dx * dx + dy * dy);
        if (dist <= maxDist || dist < 1e-9) return (tx, ty);
        var k = maxDist / dist;
        return (px + dx * k, py + dy * k);
    }

    /// <summary>
    /// 時刻 t で「本来映すべき」視野（レート制限をかける前の生の目標値）。
    /// <paramref name="actualT"/>=実際の経過時刻（区間判定用）、<paramref name="leadT"/>=先取りサンプリング時刻
    /// （<see cref="LeadSeconds"/> 分先。ボール追従の目標算出だけに使う）。区間判定を leadT でやると、
    /// 投球区間の終盤（接触の LeadSeconds 秒前）で先取りが接触後の区間へ食い込み、打つ前からパンが
    /// 始まってしまう（Issue #258）。固定フレーム区間中かどうかは常に actualT で判定する。
    /// </summary>
    private static CameraViewport RawTargetAt(PlaybackPlay play, double actualT, double leadT, CameraFraming fr, double wideStart)
    {
        if (leadT >= wideStart)
        {
            if (fr.ReturnsToWide) return Wide();
            // 全景へ戻さない（issue field-view-cut-back-sooner ④）。まだ走っている走者が居るなら
            // その走者が走っている**塁間まるごと**を収める（本塁打の一周。止めた枠のままだと枠外へ出ていく）。
            // ⚠ 枠を「走者の現在地＋向かう塁」にしてはいけない。塁に近づくほど箱が点へ縮んで**ズームが
            // 脈打ち**、塁を踏んだ瞬間に 27m へ跳ね返る（実測: 上限40倍まで寄ってから戻れず走者が枠外へ出た）。
            // 塁間（27.4m）で固定なら、レッグが変わってもズームは変わらず中心が寄るだけ＝穏やかに追える。
            // ⚠ 追う走者を探すのは actualT（先取りしない）。leadT で探すと、レッグが終わる
            // LeadSeconds 秒前に「もう誰も走っていない」と判断して枠が離れ始める＝
            // <b>走者が塁を踏む直前に画面から外れる</b>（実測: 本塁到達 0.25 秒前に枠が外れた）。
            if (RunningRunnerLegAt(play, actualT) is { } trailing)
                return FitBox(trailing.From, trailing.To, fr);
            // 追っていた走者が着いた直後は、その塁間の枠のまま据える（着いた瞬間に別の場所へ跳ばない）。
            if (LastRunnerLegEndedAfter(play, actualT, wideStart) is { } settled)
                return FitBox(settled.From, settled.To, fr);
            // 誰も動いていないなら**戻し始める時刻の枠のまま**止める（wideStart を +∞ にして再入を断つ）。
            return RawTargetAt(play, wideStart, wideStart, fr, double.PositiveInfinity);
        }

        var (covering, nextUpcoming) = FindSegmentAt(play, actualT);

        // 投球区間は固定フレーム。ビュー側の忠実移植モデル（PitchSegment）とエンジン実出力
        // （EndpointBallSegment＋Kind=Pitch。#258）の両方を拾う。
        if (IsKind(covering, BallSegmentKind.Pitch)
            || (covering is null && IsKind(nextUpcoming, BallSegmentKind.Pitch)))
            return FitBox(new PlaybackVec(0, 0), new PlaybackVec(FieldDiagramGeometry.Mound.X, FieldDiagramGeometry.Mound.Y), fr, fr.PitchZoom);

        // 送球区間は始点・終点を収めるフレーム。同じく両モデルを拾う（#258）。
        // ThrowFollowsBall なら下の「ボールを追う」へ落とす＝送球も打球と同じ扱いになる。
        if (!fr.ThrowFollowsBall && IsKind(covering, BallSegmentKind.Throw))
        {
            var span = covering!.T1 - covering.T0;
            var from = covering.At(0);
            // 既定は「投げた瞬間に始点・終点の枠へ切り替える」＝**枠が階段状に跳ぶ**。3Dのように
            // 寄っていると、跳んだ先の枠（24m）へズームが 2倍/秒 で追いつく前にアイムだけが
            // 箱の中心（＝投げ手と受け手のあいだの何も無い地面）へ着く（issue #566。実測 2026-08-10:
            // 暴投の投げ返しで枠 7.7m のまま 12m 移動＝誰も映らないコマが出た）。
            // ThrowBoxGrowsWithBall なら箱を**投げ手から球へ伸ばす**＝t=T0 で投げ手の一点から始まり
            // 球の飛翔とともに広がって、届いた時にちょうど始点・終点の枠になる。跳ばないので
            // レート制限が追いつけ、投げ手は最初から最後まで枠の中に居る。
            var to = fr.ThrowBoxGrowsWithBall
                ? covering.At(Math.Clamp(leadT - covering.T0, 0.0, span))
                : covering.At(span);
            return FitBox(new PlaybackVec(from.X, from.Y), new PlaybackVec(to.X, to.Y), fr);
        }

        // 前セグメント終了〜送球開始（球は野手の手の中）は、目標を保持位置に留める。
        // leadT で判定すると、実際にはまだ投げていないのに送球区間の未来位置（一塁寄り）を
        // 先取りしてしまう（投球区間の #258 と同型のガード。issue #448①）。この分岐は
        // 下の critical（差し迫った走塁）より先に判定する＝力の入る封殺の打者走者レッグは
        // 接触直後からずっと OutAtEnd=true で有効なので、先に判定しないと critical 側の
        // ballNow=BallAt(leadT) が同じ理由で一塁側へ先取りしてしまう（実測で確認済み）。
        if (covering is null && IsKind(nextUpcoming, BallSegmentKind.Throw))
        {
            var held = PlaybackEvaluator.BallAt(play, actualT);
            if (held.HasValue)
            {
                var heldV = new PlaybackVec(held.Value.X, held.Value.Y);
                // まだ**誰も持っていない**（野手が拾いに走っている最中）なら、被写体はその野手（issue #566）。
                // 走者は25〜40m先に居るので、ここで走者を入れると枠が追いつかず地面を舐める。下の
                // HeldBallFollowsRunner は「持っている」あいだの作法なので、走っている間は先に抜ける。
                if (fr.LooseBallFollowsChaser && ChasingFielderAt(play, actualT, heldV) is { } chaser)
                    return FitBox(heldV, chaser, fr);
                // 球が止まっている間も走っている走者が居れば一緒に収める（issue #488 実機確認 2026-08-06）。
                // 振り逃げは「捕手が本塁付近で球を持っている 2.4秒」がまるごとこの分岐で、球だけを
                // 追うと**一塁へ走る打者走者が一度も枠に入らない**（実測: 注視y が 2.4秒ずっと 1.0）。
                // 送球前の先取りパンを禁じた issue #448① の意図（未来の送球先を先に見ない）は
                // 保たれる＝見るのは走者の**現在位置**だけ。2D俯瞰は全部入るので既定オフ。
                if (fr.HeldBallFollowsRunner && RunningRunnerAt(play, actualT) is { } runner)
                    return FitBox(heldV, runner, fr);
                return FitBox(heldV, heldV, fr);
            }
        }

        // 打球（滞空/転がり）中、またはボール保持中の狭間 ＝ ボールを追う。
        // ただし走塁の結末に絡む区間（盗塁・走塁死・本塁クロスプレー）は「ボールと目標塁の中点」を追う。
        var critical = FindCriticalRunAt(play, leadT);
        if (critical != null)
        {
            var ballNow = PlaybackEvaluator.BallAt(play, leadT);
            var ballPos = ballNow.HasValue ? new PlaybackVec(ballNow.Value.X, ballNow.Value.Y) : critical.To;
            return FitBox(ballPos, critical.To, fr);
        }

        var cur = PlaybackEvaluator.BallAt(play, leadT);
        if (!cur.HasValue) return Wide();
        var curV = new PlaybackVec(cur.Value.X, cur.Value.Y);
        var ahead = PlaybackEvaluator.BallAt(play, leadT + LookaheadSeconds);
        var aheadV = ahead.HasValue ? new PlaybackVec(ahead.Value.X, ahead.Value.Y) : curV;
        return FitBox(curV, aheadV, fr);
    }

    /// <summary>
    /// セグメントが指定種別か（ビュー側の忠実移植モデル＝固定4型と、エンジン実出力の
    /// <see cref="EndpointBallSegment"/>＋<see cref="BallSegmentKind"/> の両方を同一視して判定する。#258）。
    /// </summary>
    private static bool IsKind(PlaybackBallSegment? seg, BallSegmentKind kind) => (seg, kind) switch
    {
        (PitchSegment, BallSegmentKind.Pitch) => true,
        (ThrowSegment, BallSegmentKind.Throw) => true,
        (EndpointBallSegment eb, _) => eb.Kind == kind,
        _ => false,
    };

    private static (PlaybackBallSegment? Covering, PlaybackBallSegment? NextUpcoming) FindSegmentAt(PlaybackPlay play, double t)
    {
        foreach (var s in play.Ball)
        {
            if (t < s.T0) return (null, s);
            if (t <= s.T1) return (s, null);
        }
        return (null, null);
    }

    /// <summary>
    /// 時刻 t に**実際に動いている**走者の現在位置（複数なら最も遠くまで進む予定のレッグ＝
    /// そのプレーの主役）。その場に居るだけのレッグ（From==To）は数えない。
    /// </summary>
    /// <summary>
    /// 時刻 t に**実際に動いている**走者が走っているレッグの両端（issue field-view-cut-back-sooner）。
    /// 選び方は <see cref="RunningRunnerAt"/> と同じ（最も遠くまで進む予定のレッグ＝そのプレーの主役）。
    /// 全景へ戻さない守備ビューで「その塁間まるごと」を収めるのに使う＝走者は必ず枠の中に居る。
    /// </summary>
    private static (PlaybackVec From, PlaybackVec To)? RunningRunnerLegAt(PlaybackPlay play, double t)
    {
        (PlaybackVec From, PlaybackVec To)? found = null;
        var best = double.NegativeInfinity;
        foreach (var runner in play.Runners)
            foreach (var seg in runner.Segs)
            {
                if (seg.From.X == seg.To.X && seg.From.Y == seg.To.Y) continue;
                if (t < seg.T0 || t > seg.T1) continue;
                var span = Math.Abs(seg.To.X - seg.From.X) + Math.Abs(seg.To.Y - seg.From.Y);
                if (span <= best) continue;
                best = span;
                found = (seg.From, seg.To);
            }
        return found;
    }

    /// <summary>
    /// <paramref name="t"/> より前に終わった走者レッグのうち、<paramref name="after"/> 以降に終わった
    /// 最後のもの（issue field-view-cut-back-sooner）。全景へ戻さない守備ビューで「追っていた走者が
    /// 着いた直後」に枠を据え置くために使う。<paramref name="after"/> より前にしか無ければ null
    /// ＝そもそも走者を追っていないプレー（枠は戻し始める時刻のまま止める）。
    /// </summary>
    private static (PlaybackVec From, PlaybackVec To)? LastRunnerLegEndedAfter(
        PlaybackPlay play, double t, double after)
    {
        (PlaybackVec From, PlaybackVec To)? found = null;
        var latest = after;
        foreach (var runner in play.Runners)
            foreach (var seg in runner.Segs)
            {
                if (seg.From.X == seg.To.X && seg.From.Y == seg.To.Y) continue;
                if (seg.T1 > t || seg.T1 < latest) continue;
                latest = seg.T1;
                found = (seg.From, seg.To);
            }
        return found;
    }

    private static PlaybackVec? RunningRunnerAt(PlaybackPlay play, double t)
    {
        PlaybackVec? found = null;
        var best = double.NegativeInfinity;
        foreach (var runner in play.Runners)
            foreach (var seg in runner.Segs)
            {
                if (seg.From.X == seg.To.X && seg.From.Y == seg.To.Y) continue;
                if (t < seg.T0 || t > seg.T1) continue;
                var span = Math.Abs(seg.To.X - seg.From.X) + Math.Abs(seg.To.Y - seg.From.Y);
                if (span <= best) continue;
                var at = PlaybackEvaluator.RunnerAt(runner, t);
                if (!at.HasValue) continue;
                best = span;
                found = at.Value;
            }
        return found;
    }

    /// <summary>
    /// 球を拾いに出た野手（issue #566）。居なければ null。
    ///
    /// <para><b>走り終わっても返し続ける</b>のが要点。「走っている間だけ」にすると、拾った瞬間に
    /// 被写体が走者へ移り、結局そこで枠が追いつかないパンが起きる（実測: 盲点が 0.40秒→2.15秒 に
    /// 悪化した）。拾った後は球のところに立っているので、球との箱は点に近い＝寄った枠のまま
    /// 「捕手が拾って投げ返す」が読める。送球が始まればこの分岐自体を抜ける。</para>
    ///
    /// <para>誰を選ぶかは球にいちばん近い野手（issue #689。宣言順の先頭だと内野ゴロで捕手が選ばれ引いていく）。
    /// 同距離は <see cref="FieldPosition"/> の宣言順＝決定論（不変条件#2）。</para>
    /// </summary>
    private static PlaybackVec? ChasingFielderAt(PlaybackPlay play, double t, PlaybackVec ball)
    {
        // 球にいちばん近い野手（issue #689）。宣言順の先頭の「動いた野手」を取ると、内野ゴロでも毎回
        // 本塁へ動く捕手が選ばれ、遊撃手が球を持っている間ずっと「遊撃手＋本塁」(40m級)の枠になって
        // 引いていく。球を拾いに走る野手も、拾って持っている野手も、球のいちばん近くに居る。
        // 定位置のまま捕る野手（三塁手など）は Moves に載らないので、候補は全野手にする。
        // 同距離は宣言順（strict <）＝辞書の並びに依らず決定論。
        PlaybackVec? found = null;
        var best = double.PositiveInfinity;
        foreach (FieldPosition pos in Enum.GetValues(typeof(FieldPosition)))
        {
            if (pos == FieldPosition.DesignatedHitter) continue;   // 守備に就かない
            var at = PlaybackEvaluator.FielderAt(play, pos, t).Pos;
            var d = (at.X - ball.X) * (at.X - ball.X) + (at.Y - ball.Y) * (at.Y - ball.Y);
            if (d >= best) continue;
            best = d;
            found = at;
        }
        return found;
    }

    private static PlaybackRun? FindCriticalRunAt(PlaybackPlay play, double t)
    {
        PlaybackRun? found = null;
        foreach (var runner in play.Runners)
            foreach (var seg in runner.Segs)
                if ((seg.OutAtEnd || seg.CloseCall) && t >= seg.T0 && t <= seg.T1)
                    found = seg;
        return found;
    }

    private static CameraViewport Wide() => new(WideCx, WideCy, WideZoom);

    private static CameraViewport FitBox(PlaybackVec a, PlaybackVec b, CameraFraming fr, double? fixedZoom = null)
    {
        var cx = (a.X + b.X) / 2.0;
        var cy = (a.Y + b.Y) / 2.0;
        if (fixedZoom.HasValue) return new CameraViewport(cx, cy, Math.Clamp(fixedZoom.Value, WideZoom, fr.MaxZoom));

        var w = Math.Abs(a.X - b.X) + fr.MarginM * 2.0;
        var h = Math.Abs(a.Y - b.Y) + fr.MarginM * 2.0;
        var zoom = Math.Min(DesignWidthM / w, DesignHeightM / h);
        return new CameraViewport(cx, cy, Math.Clamp(zoom, WideZoom, fr.MaxZoom));
    }
}

/// <summary>キーフレーム列から時刻 t の視野を求める（easeIO 補間。区間外はクランプ）。</summary>
public static class CameraEvaluator
{
    public static CameraViewport ViewportAt(IReadOnlyList<CameraKeyframe> plan, double t)
    {
        if (plan == null || plan.Count == 0) return new CameraViewport(CameraPlanBuilder.WideCx, CameraPlanBuilder.WideCy, CameraPlanBuilder.WideZoom);
        if (t <= plan[0].T) return AsViewport(plan[0]);
        if (t >= plan[^1].T) return AsViewport(plan[^1]);

        for (var i = 1; i < plan.Count; i++)
        {
            if (t > plan[i].T) continue;
            var prev = plan[i - 1];
            var next = plan[i];
            var span = next.T - prev.T;
            var u = span > 0 ? PlaybackEvaluator.EaseIO((t - prev.T) / span) : 1.0;
            var cx = prev.Cx + (next.Cx - prev.Cx) * u;
            var cy = prev.Cy + (next.Cy - prev.Cy) * u;
            // ズームは対数空間で補間（倍率変化を等速に見せる。区間が短いので実質線形に近い）。
            var logZoom = Math.Log(prev.ZoomMult) + (Math.Log(next.ZoomMult) - Math.Log(prev.ZoomMult)) * u;
            return new CameraViewport(cx, cy, Math.Exp(logZoom));
        }
        return AsViewport(plan[^1]);
    }

    private static CameraViewport AsViewport(CameraKeyframe k) => new(k.Cx, k.Cy, k.ZoomMult);
}
