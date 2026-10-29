using System.Collections.Generic;
using KokoSim.Engine.Match.Timeline;
using KokoSim.Engine.Match.Timeline.Playback;
using UnityEngine;

namespace KokoSim.Unity.Match
{
    /// <summary>
    /// 本塁打の球を外野スタンドに当てる（issue #704）。
    ///
    /// <para>engine の弾道は「地面 y=0 に落ちるまで」で、スタンドを知らない。そのまま描くと球は座席を
    /// すり抜けて地面まで沈み、着弾点が見えない（どの当たりも柵の向こうへ消えるだけ）。
    /// ここでは<b>描いているスタンドのメッシュそのもの</b>に当たり判定を付け、球が最初に当たった点で止めて
    /// 小さく跳ねさせる。形の単一ソースは <c>tools/stadium-gen.py</c> のまま（C# に段の寸法を持ち直さない）。</para>
    ///
    /// <para>表示だけの処理: engine の判定・飛距離・帯には一切触れない。当たり点は再生前に一度だけ求める
    /// （毎フレームのレイキャストにしない＝どの時刻から再生しても同じ絵）。</para>
    /// </summary>
    public sealed partial class DuelStageRig
    {
        /// <summary>球を当てる面（外野の座席・バックスクリーン・スコアボード）。BuildStadiumMesh が登録する。</summary>
        private readonly List<MeshCollider> _standColliders = new();

        /// <summary>当たり判定を付ける部位（OBJ の o グループ名）。</summary>
        private static bool IsBallStopPart(string name)
            => name.StartsWith("stand_outfield") || name == "backscreen" || name == "scoreboard";

        /// <summary>跳ねている時間[s]。**絵で決める値**。</summary>
        public static float StandHopSeconds = 0.6f;
        /// <summary>跳ねの高さ＝入射速さ×この係数（上限・下限つき）[m]。</summary>
        public static float StandHopHeightPerMps = 0.05f;
        /// <summary>跳ねで進む距離＝入射の水平速さ×この係数[m]。座席の段を1〜3段ぶん転がる程度。</summary>
        public static float StandHopRunPerMps = 0.08f;

        private bool _hrHit;
        private float _hrHitT;
        private Vector3 _hrHitPos;    // _root 基準
        private Vector3 _hrRestPos;   // _root 基準
        private float _hrHopH;

        /// <summary>直近の本塁打で球が当たった位置（観測用。柵からの奥行き[m]と高さ[m]。当たらなければ null）。</summary>
        public Vector2? LastStandHit { get; private set; }

        /// <summary>
        /// 本塁打なら、球が最初にスタンドへ当たる時刻と点を求める。再生前に一度だけ呼ぶ。
        /// 柵より手前（フェア地域）の点は見ない＝フェンスの当たり判定はここでは持たない。
        /// </summary>
        private void PlanHomeRunStandHit(PlaybackPlay play)
        {
            _hrHit = false;
            LastStandHit = null;
            if (!IsHomeRun(play) || _standColliders.Count == 0 || play.Ball == null) return;

            EndpointBallSegment flight = null;
            foreach (var s in play.Ball)
                if (s is EndpointBallSegment { Kind: BallSegmentKind.Flight } eb) { flight = eb; break; }
            if (flight == null) return;

            Physics.SyncTransforms();
            const float step = 1f / 240f;
            var root = _root.transform;
            Vector3 prev = BallLocal(play, (float)flight.T0);
            for (float t = (float)flight.T0 + step; t <= (float)flight.T1 + 1e-4f; t += step)
            {
                var cur = BallLocal(play, t);
                float th = Mathf.Atan2(cur.x, cur.z);
                bool beyondFence = new Vector2(cur.x, cur.z).magnitude > FenceR(th);
                if (beyondFence)
                {
                    var a = root.TransformPoint(prev);
                    var b = root.TransformPoint(cur);
                    var ray = new Ray(a, b - a);
                    float len = (b - a).magnitude;
                    if (len > 1e-5f && RaycastStands(ray, len, out var hit))
                    {
                        float frac = hit.distance / len;
                        _hrHit = true;
                        _hrHitT = t - step + step * frac;
                        _hrHitPos = root.InverseTransformPoint(hit.point);
                        var vel = (cur - prev) / step;
                        _hrHopH = Mathf.Clamp(vel.magnitude * StandHopHeightPerMps, 0.3f, 2.2f);
                        var horiz = new Vector3(vel.x, 0f, vel.z);
                        float run = Mathf.Clamp(horiz.magnitude * StandHopRunPerMps, 0.4f, 3.0f);
                        var restXZ = _hrHitPos + horiz.normalized * run;
                        _hrRestPos = SurfaceBelow(restXZ, out var restY) ? new Vector3(restXZ.x, restY, restXZ.z) : _hrHitPos;
                        float fenceR = FenceR(Mathf.Atan2(_hrHitPos.x, _hrHitPos.z));
                        LastStandHit = new Vector2(new Vector2(_hrHitPos.x, _hrHitPos.z).magnitude - fenceR, _hrHitPos.y);
                        return;
                    }
                }
                prev = cur;
            }
        }

        /// <summary>本塁打の球がスタンドに当たった後の位置（_root 基準）。当たる前・当たらない打球は null。</summary>
        private Vector3? StandHitBallPoint(float t)
        {
            if (!_hrHit || t < _hrHitT) return null;
            float u = Mathf.Clamp01((t - _hrHitT) / Mathf.Max(1e-3f, StandHopSeconds));
            var p = Vector3.Lerp(_hrHitPos, _hrRestPos, u);
            p.y += _hrHopH * 4f * u * (1f - u);
            return p;
        }

        private Vector3 BallLocal(PlaybackPlay play, float t)
        {
            var b = PlaybackEvaluator.BallAt(play, t);
            return b.HasValue ? new Vector3((float)b.Value.X, (float)b.Value.H, (float)b.Value.Y) : Vector3.zero;
        }

        private bool RaycastStands(Ray ray, float maxDist, out RaycastHit best)
        {
            best = default;
            bool any = false;
            foreach (var c in _standColliders)
            {
                if (c == null) continue;
                if (c.Raycast(ray, out var h, maxDist) && (!any || h.distance < best.distance))
                {
                    best = h;
                    any = true;
                }
            }
            return any;
        }

        /// <summary>_root 基準の点の真下にあるスタンド面の高さ。</summary>
        private bool SurfaceBelow(Vector3 local, out float y)
        {
            var root = _root.transform;
            var from = root.TransformPoint(new Vector3(local.x, local.y + 30f, local.z));
            var down = root.TransformDirection(Vector3.down);
            if (RaycastStands(new Ray(from, down), 60f, out var h))
            {
                y = root.InverseTransformPoint(h.point).y;
                return true;
            }
            y = 0f;
            return false;
        }
    }
}
