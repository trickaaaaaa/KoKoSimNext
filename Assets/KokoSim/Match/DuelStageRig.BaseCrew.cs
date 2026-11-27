using System.Collections.Generic;
using UnityEngine;
using KokoSim.Engine.Match.Field;
using KokoSim.Engine.Match.Timeline.Playback;

namespace KokoSim.Unity.Match
{
    /// <summary>
    /// 守備ビューの塁審（一塁・二塁・三塁）とランナーコーチ（一塁・三塁）。表示専用（issue #1595）。
    /// <para>塁審は待機の立ち姿で居て、その塁での「アウト／際どいセーフ」の瞬間に判定の動きを出す。
    /// 判定の種類と時刻は engine のタイムライン（走者の最終レッグの <c>OutAtEnd</c>/<c>CloseCall</c>/<c>T1</c>）から取る
    /// ＝絵が判定を作らない。コーチは攻撃側の控えが立っているだけ（動きの収録が無い・オーナー判断 2026-11-24）。</para>
    /// <para>居る場所は打球・送球・走者の通り道の外（ファウルグラウンド・塁の後方）。当たる絵にならない位置を選ぶ。</para>
    /// </summary>
    public sealed partial class DuelStageRig
    {
        public const string BaseUmpireIdleClipName = "AA_Baseball_Umpire_Standing_Idle";
        public const string BaseUmpireOutClipName = "AA_Baseball_Umpire_Standing_Out_Type01";
        public const string BaseUmpireSafeClipName = "AA_Baseball_Umpire_Standing_Safe_Type01";

        /// <summary>判定の腕が上がり始める時刻の、判定の瞬間より前の余裕[s]。</summary>
        private const float BaseCallLeadSeconds = 0.2f;

        // 収録の頭には腕を動かさない長い待ち（アウト約2.5s・セーフ約2.0s）が入っている＝そこを飛ばして
        // 腕が上がり始める所から流す（右手首の高さの実測 2026-11-25: アウトは2.40→2.67s、セーフは1.87→2.13s で跳ね上がる）。
        // 守備ビューの再生は判定の後1秒足らずで閉じるので、頭から流すと腕が上がる前に終わる。
        private const float BaseCallOutOnsetSeconds = 2.5f;
        private const float BaseCallSafeOnsetSeconds = 2.0f;

        /// <summary>走者のレッグの行き先がこの範囲[m]にある塁を、その判定の塁とみなす（一塁の駆け抜けまで入る）。</summary>
        private const float BaseCallRadiusM = 5f;

        private static readonly Vector2 BaseUnitAlongLine1B = new Vector2(0.70711f, 0.70711f);
        private static readonly Vector2 BaseUnitFoul1B = new Vector2(0.70711f, -0.70711f);

        /// <summary>塁審の定位置（stage-local の x,z）。0=一塁・1=二塁・2=三塁。一・三塁はファウルラインの外で塁の後方、二塁は外野芝の手前。</summary>
        private static Vector2 BaseUmpireSpot(int baseIdx)
        {
            switch (baseIdx)
            {
                case 0: return BaseBag(1) + 5.0f * BaseUnitAlongLine1B + 2.8f * BaseUnitFoul1B;
                case 2: return Mirror(BaseBag(1) + 5.0f * BaseUnitAlongLine1B + 2.8f * BaseUnitFoul1B);
                default: return new Vector2(4.5f, 43.5f);
            }
        }

        /// <summary>ランナーコーチの定位置。0=一塁側・1=三塁側。塁と並ぶ高さでファウルラインの外（コーチャーズボックス）。</summary>
        private static Vector2 CoachSpot(int side)
        {
            var p = BaseBag(1) + 4.5f * BaseUnitFoul1B;
            return side == 0 ? p : Mirror(p);
        }

        private static Vector2 Mirror(Vector2 p) => new Vector2(-p.x, p.y);

        /// <summary>塁の中心。1=一塁・2=二塁・3=三塁（engine の FieldDiagramGeometry と同じ座標）。</summary>
        private static Vector2 BaseBag(int n)
        {
            var b = n == 1 ? FieldDiagramGeometry.First : n == 2 ? FieldDiagramGeometry.Second : FieldDiagramGeometry.Third;
            return new Vector2((float)b.X, (float)b.Y);
        }

        private readonly Actor[] _baseUmps = new Actor[3];
        private readonly Actor[] _coaches = new Actor[2];

        private struct BaseCall
        {
            public int Ump;        // 0=一塁・1=二塁・2=三塁
            public float TStart;   // 演技の開始時刻（判定の時刻 − リード）
            public bool Out;       // true=アウト／false=際どいセーフ
            public bool Fired;
        }

        private readonly List<BaseCall> _baseCalls = new();

        /// <summary>このリグが使う塁審のクリップ名（ビルド同梱の索引が読む）。</summary>
        private static IEnumerable<string> BaseCrewClipNames()
        {
            yield return BaseUmpireIdleClipName;
            yield return BaseUmpireOutClipName;
            yield return BaseUmpireSafeClipName;
        }

        /// <summary>塁審3人とコーチ2人を生成する（2回目以降は何もしない）。伏せた状態で作る。</summary>
        private void EnsureBaseCrew(GameObject playerBody, bool useVendor, int layer)
        {
            if (_baseUmps[0] != null) return;

            var umpIdle = LoadClip(BaseUmpireIdleClipName);
            var umpBody = LoadBody();
            if (umpIdle != null && umpBody != null)
            {
                for (int i = 0; i < _baseUmps.Length; i++)
                {
                    var spot = BaseUmpireSpot(i);
                    var faceTo = i == 0 ? BaseBag(1) : i == 1 ? BaseBag(2) : BaseBag(3);
                    var a = SpawnActor(umpBody, umpIdle, new Vector3(spot.x, 0f, spot.y), YawToward(spot, faceTo),
                        UniformPalette.Umpire, 0f, layer, rootMotion: false, tightBounds: true, umpireBody: true);
                    a.Go.name = "base-umpire-" + (i + 1);
                    a.IdleClip = umpIdle;
                    StepActor(a, 0.001f);
                    GroundFix(a);
                    AttachGear(a, GearKind.Cap, Vector3.zero, UniformPalette.Umpire.Cap, layer);
                    a.Go.SetActive(false);
                    a.Enabled = false;
                    _baseUmps[i] = a;
                }
            }
            else Debug.LogWarning("[DuelStage] 塁審の待機クリップ／素体が無いので塁審を置かない");

            var coachIdle = LoadClip(OutfielderIdleClipName);
            if (coachIdle != null && playerBody != null)
            {
                for (int i = 0; i < _coaches.Length; i++)
                {
                    var spot = CoachSpot(i);
                    var a = SpawnActor(playerBody, coachIdle, new Vector3(spot.x, 0f, spot.y), YawFacingHome(spot.x, spot.y),
                        _offenseColors, 0f, layer, useVendor, rootMotion: false, tightBounds: true);
                    a.Go.name = "base-coach-" + (i == 0 ? "1B" : "3B");
                    a.IdleClip = coachIdle;
                    StepActor(a, 0.001f);
                    GroundFix(a);
                    // コーチはヘルメット（走者・打者と同じ）。購入素体の素の帽子は落としてから被せる
                    if (useVendor) SetChildActive(a.Go, VendorCapMeshName, false);
                    a.WearsHelmet = true;
                    AttachGear(a, GearKind.Helmet, Vector3.zero, _offenseColors.Helmet, layer);
                    CollectTeamMats(a, _offenseColors.Helmet);
                    a.Go.SetActive(false);
                    a.Enabled = false;
                    _coaches[i] = a;
                }
            }
        }

        private void SetBaseCrewVisible(bool on)
        {
            foreach (var a in _baseUmps) SetActorShown(a, on);
            foreach (var a in _coaches) SetActorShown(a, on);
        }

        private static void SetActorShown(Actor a, bool on)
        {
            if (a == null || a.Go == null) return;
            a.Go.SetActive(on);
            a.Enabled = on;
        }

        /// <summary>
        /// そのプレーの塁審の判定を洗い出す。対象は塁で出たアウトと際どいセーフだけ（走者がただ進塁しただけの場面で
        /// 毎回腕を出さない）。判定の種類と時刻は engine の判定イベント（<see cref="PlaybackPlay.Judgements"/>）の値。
        /// 「どの塁か」は、アウト／際どいセーフの印が付いた走者のレッグの行き先から引く
        /// （飛球の捕球アウトは走者のレッグに印が付かない＝球審・外野の仕事なので対象外）。
        /// 内野ゴロの一塁フォースは送球が着いた瞬間に確定し、走者はその後に着く＝判定の時刻が走者の到達より早い。
        /// </summary>
        private void BuildBaseCallPlan(PlaybackPlay play)
        {
            _baseCalls.Clear();
            if (play?.Runners == null || play.Judgements == null) return;

            var legs = new List<(int Ump, float T1, bool Out)>();
            foreach (var r in play.Runners)
            {
                if (r?.Segs == null) continue;
                foreach (var seg in r.Segs)
                {
                    if (!seg.OutAtEnd && !seg.CloseCall) continue;
                    if (PlaybackDisplaySync.IsStationaryLeg(seg)) continue;
                    var to = new Vector2((float)seg.To.X, (float)seg.To.Y);
                    int ump = -1;
                    float best = BaseCallRadiusM;
                    for (int b = 1; b <= 3; b++)
                    {
                        float d = (to - BaseBag(b)).magnitude;
                        if (d < best) { best = d; ump = b - 1; }
                    }
                    if (ump >= 0) legs.Add((ump, (float)seg.T1, seg.OutAtEnd));
                }
            }
            if (legs.Count == 0) return;

            var used = new bool[legs.Count];
            var judgements = new List<PlaybackJudgement>(play.Judgements);
            judgements.Sort((x, y) => x.T.CompareTo(y.T));
            foreach (var j in judgements)
            {
                bool isOut = j.Kind == KokoSim.Engine.Match.Timeline.JudgementKind.Out;
                bool closeSafe = j.Kind == KokoSim.Engine.Match.Timeline.JudgementKind.Safe && j.CloseCall;
                if (!isOut && !closeSafe) continue;
                int pick = -1;
                float bestDt = float.MaxValue;
                for (int i = 0; i < legs.Count; i++)
                {
                    if (used[i] || legs[i].Out != isOut) continue;
                    float dt = legs[i].T1 - (float)j.T;
                    if (dt < -0.5f || dt >= bestDt) continue;
                    bestDt = dt; pick = i;
                }
                if (pick < 0) continue;
                used[pick] = true;
                _baseCalls.Add(new BaseCall { Ump = legs[pick].Ump, TStart = (float)j.T - BaseCallLeadSeconds, Out = isOut });
            }
        }

        /// <summary>予定の時刻が来た判定を流す。絶対時刻で判定する＝途中から再生を始めても同じ絵になる。</summary>
        private void FireDueBaseCalls(float t)
        {
            for (int i = 0; i < _baseCalls.Count; i++)
            {
                var c = _baseCalls[i];
                if (c.Fired || t < c.TStart) continue;
                var a = _baseUmps[c.Ump];
                var clip = LoadClip(c.Out ? BaseUmpireOutClipName : BaseUmpireSafeClipName);
                if (a != null && a.Enabled && clip != null)
                {
                    float seek = (c.Out ? BaseCallOutOnsetSeconds : BaseCallSafeOnsetSeconds) - BaseCallLeadSeconds;
                    CrossfadeActorToClip(a, clip, seek + Mathf.Max(0f, t - c.TStart), 0f, 0.1f);   // 終わったら待機（IdleClip）へ戻る
                    a.Speed = 1f;
                }
                c.Fired = true;
                _baseCalls[i] = c;
            }
        }

        /// <summary>撮影レシピの probes 用（issue #1595）: 塁審3人の再生中のクリップと再生位置、予定した判定の数。</summary>
        public string BaseCrewForCapture
        {
            get
            {
                var sb = new System.Text.StringBuilder();
                sb.Append("calls=").Append(_baseCalls.Count);
                foreach (var c in _baseCalls)
                    sb.Append(" [").Append(c.Ump).Append(c.Out ? " out@" : " safe@").Append(c.TStart.ToString("0.00")).Append(c.Fired ? " fired]" : "]");
                for (int i = 0; i < _baseUmps.Length; i++)
                {
                    var a = _baseUmps[i];
                    sb.Append(" | ").Append(i == 0 ? "1B " : i == 1 ? "2B " : "3B ");
                    if (a == null || !a.Enabled || a.Clip == null) { sb.Append('-'); continue; }
                    sb.Append(a.Clip.name.Replace("AA_Baseball_Umpire_", "")).Append('@').Append(a.Time.ToString("0.00"));
                }
                return sb.ToString();
            }
        }

        private bool _umpHeadCloseDone;

        /// <summary>
        /// 撮影レシピの probes 用（issue #1740）: 一塁塁審の頭まわりの装備の寸法（頭ボーン枠・実寸[m]）を返し、
        /// 初回だけ正面と横の寄りを screenshots/_umpire-head-*.png へ撮る。
        /// </summary>
        internal string BaseUmpHeadCloseForCapture
        {
            get
            {
                var a = _baseUmps[0];
                if (a == null || !a.Enabled || a.Animator == null) return "-";
                var head = a.Animator.GetBoneTransform(HumanBodyBones.Head);
                if (head == null) return "no-head";
                var sb = new System.Text.StringBuilder();
                foreach (var r in head.GetComponentsInChildren<Renderer>(true))
                {
                    var bnd = r.bounds;
                    sb.Append(r.name).Append(" c=").Append(head.InverseTransformPoint(bnd.center).ToString("F3")).Append(' ');
                }
                if (!_umpHeadCloseDone)
                {
                    _umpHeadCloseDone = true;
                    sb.Append(SkullExtentsText(a, head));
                    var dir = System.IO.Path.Combine(Application.dataPath, "..", "screenshots");
                    var face = head.TransformDirection(a.HeadFwdLocal).normalized;
                    var up = head.TransformDirection(a.HeadUpLocal).normalized;
                    var side = Vector3.Cross(up, face).normalized;
                    var focus = head.position + up * 0.09f;
                    ShootHeadClose(focus + face * 0.7f, focus, System.IO.Path.Combine(dir, "_umpire-head-front.png"));
                    ShootHeadClose(focus + side * 0.7f, focus, System.IO.Path.Combine(dir, "_umpire-head-side.png"));
                }
                return sb.ToString();
            }
        }

        /// <summary>素体の頭まわりの頂点（頭ボーンの0.3m以内）を、頭の枠（+Y=頭蓋上・+Z=顔前）で見た範囲。</summary>
        private string SkullExtentsText(Actor a, Transform head)
        {
            var up = head.TransformDirection(a.HeadUpLocal).normalized;
            var fwd = head.TransformDirection(a.HeadFwdLocal).normalized;
            var right = Vector3.Cross(up, fwd).normalized;
            var lo = new Vector3(9f, 9f, 9f); var hi = new Vector3(-9f, -9f, -9f);
            var verts = new List<Vector3>();
            foreach (var smr in a.Go.GetComponentsInChildren<SkinnedMeshRenderer>())
            {
                var m = new Mesh();
                smr.BakeMesh(m, true);
                m.GetVertices(verts);
                foreach (var v in verts)
                {
                    var w = smr.transform.position + smr.transform.rotation * v - head.position;
                    var l = new Vector3(Vector3.Dot(w, right), Vector3.Dot(w, up), Vector3.Dot(w, fwd));
                    if (l.y < -0.02f || l.magnitude > 0.3f) continue;
                    lo = Vector3.Min(lo, l); hi = Vector3.Max(hi, l);
                }
                Object.Destroy(m);
            }
            return " skull lo=" + lo.ToString("F3") + " hi=" + hi.ToString("F3") + " ";
        }

        private void ShootHeadClose(Vector3 eye, Vector3 focus, string path)
        {
            var go = new GameObject("head-close-cam");
            var cam = go.AddComponent<Camera>();
            cam.CopyFrom(_camera);
            cam.fieldOfView = 18f;
            cam.transform.position = eye;
            cam.transform.LookAt(focus);
            var rt = new RenderTexture(512, 512, 24, RenderTextureFormat.ARGB32);
            cam.targetTexture = rt;
            cam.Render();
            var prev = RenderTexture.active;
            RenderTexture.active = rt;
            var tex = new Texture2D(512, 512, TextureFormat.RGB24, false);
            tex.ReadPixels(new Rect(0, 0, 512, 512), 0, 0);
            tex.Apply();
            RenderTexture.active = prev;
            System.IO.File.WriteAllBytes(path, tex.EncodeToPNG());
            Object.Destroy(tex);
            cam.targetTexture = null;
            rt.Release();
            Object.Destroy(rt);
            Object.Destroy(go);
        }

        /// <summary>プレーの終わり: 判定の演技を畳んで待機へ戻す（次のプレーへ持ち越さない）。</summary>
        private void ResetBaseCrew()
        {
            _baseCalls.Clear();
            foreach (var a in _baseUmps)
            {
                if (a == null || a.Go == null || a.IdleClip == null) continue;
                SetActorClip(a, a.IdleClip, 0f, 0f);
                a.Speed = 1f;
            }
        }
    }
}
