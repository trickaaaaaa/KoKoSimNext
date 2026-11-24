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

        /// <summary>判定の腕が上がるまでの頭出し[s]。判定の瞬間（走者の到達）に腕の動きの山を合わせる。</summary>
        private const float BaseCallLeadSeconds = 0.35f;

        /// <summary>判定の演技を出す塁の範囲[m]。一塁の駆け抜け（オーバーラン3.5m先）まで入る。</summary>
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
                    AttachGear(a, GearKind.Helmet, Vector3.zero, _offenseColors.Cap, layer);
                    CollectTeamMats(a, _offenseColors.Cap);
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
        /// そのプレーの塁審の判定を洗い出す。対象は走者の最終レッグが塁に着くアウトと際どいセーフだけ
        /// （走者がただ進塁しただけの場面で毎回腕を出さない）。判定の時刻は到達時刻 T1＝engine の値。
        /// </summary>
        private void BuildBaseCallPlan(PlaybackPlay play)
        {
            _baseCalls.Clear();
            if (play?.Runners == null) return;
            foreach (var r in play.Runners)
            {
                if (!RunnerFinal(r, out var t1, out var outAtEnd, out var closeCall, out _, out _)) continue;
                if (!outAtEnd && !closeCall) continue;
                var to = new Vector2((float)r.Segs[r.Segs.Count - 1].To.X, (float)r.Segs[r.Segs.Count - 1].To.Y);
                int ump = -1;
                float best = BaseCallRadiusM;
                for (int b = 1; b <= 3; b++)
                {
                    float d = (to - BaseBag(b)).magnitude;
                    if (d < best) { best = d; ump = b == 1 ? 0 : b == 2 ? 1 : 2; }
                }
                if (ump < 0) continue;
                _baseCalls.Add(new BaseCall { Ump = ump, TStart = t1 - BaseCallLeadSeconds, Out = outAtEnd });
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
                    CrossfadeActorToClip(a, clip, Mathf.Max(0f, t - c.TStart), 0f, 0.12f);   // 終わったら待機（IdleClip）へ戻る
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
