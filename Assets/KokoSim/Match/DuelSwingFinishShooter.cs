#if UNITY_EDITOR
using System.Collections;
using System.IO;
using System.Text;
using UnityEngine;

namespace KokoSim.Unity.Match
{
    /// <summary>
    /// 【開発用・エディタ限定】振り切りフィニッシュ（<c>SwingFinishSec</c> で打ち切って
    /// <c>SwingFinishHoldSeconds</c> 据え置くポーズ）の**寄り**を撮るハーネス（issue #368-②）。
    ///
    /// このポーズは画面に最も長く映るので、腕・袖の破綻があればいちばん目に付く。
    /// 腕（骨の向き）が原因か袖（メッシュ）が原因かは全景では切り分けられないので、
    /// **上腕・前腕それぞれの軸から**直交2方向で撮る（斜めの絵では手と道具・腕と袖の前後が
    /// 分離できない＝design-18 §3.3 の教訓）。
    ///
    /// あわせて据え置き区間の骨の回り方（度/ポーズコマ）を出す。据え置きは
    /// <c>Graph.Evaluate</c> を呼ばないので、ここが 0 でなければポーズ補正の二度当てを疑う。
    ///
    /// 出力: screenshots/{Prefix}-{条件}-{視点}.png
    /// </summary>
    public sealed class DuelSwingFinishShooter : MonoBehaviour
    {
        private const string Dir = "screenshots";
        private const int W = 1280;
        private const int H = 960;

        private const string PrefixKey = "KokoSim.DuelSwingFinishShooter.Prefix";
        private const string RunsKey = "KokoSim.DuelSwingFinishShooter.Runs";
        private const string SpawnFlag = "KokoSim.DuelSwingFinishShooter.Spawn";

        /// <summary>据え置きへ入ってからこの秒だけ進めた所を撮る（据え置きの真ん中あたり）。</summary>
        private const float IntoHoldSec = 0.30f;

        private DuelStageRig _rig;
        private Transform _stageRoot;

        public static void Arm(string prefix, string runs)
        {
            UnityEditor.EditorPrefs.SetString(PrefixKey, prefix);
            UnityEditor.EditorPrefs.SetString(RunsKey, runs);
            UnityEditor.EditorPrefs.SetBool(SpawnFlag, true);
        }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void AutoSpawn()
        {
            if (!UnityEditor.EditorPrefs.GetBool(SpawnFlag, false)) return;
            UnityEditor.EditorPrefs.SetBool(SpawnFlag, false);
            new GameObject("~SwingFinishAuto").AddComponent<DuelSwingFinishShooter>();
        }

        private IEnumerator Start()
        {
            Application.runInBackground = true;
            Directory.CreateDirectory(Dir);

            _rig = new DuelStageRig();
            if (!_rig.Available) { Debug.LogError("[Finish] rig が組めない"); yield break; }
            _rig.EnsureSize(W, H);
            for (int i = 0; i < 12; i++) { _rig.Tick(1f / 60f); yield return null; }

            var bat = Find("body-" + DuelStageRig.BatterClipName);
            var pit = Find("body-" + DuelStageRig.PitcherClipName);
            var root = Find("~DuelStageLive");
            if (bat == null || pit == null || root == null)
            {
                Debug.LogError("[Finish] 打者/投手/ステージ根が見つからない");
                yield break;
            }
            _stageRoot = root.transform;
            var anim = bat.GetComponentInChildren<Animator>();

            Vector3 toPitcher = pit.transform.position - bat.transform.position;
            toPitcher.y = 0f;
            toPitcher.Normalize();

            string prefix = UnityEditor.EditorPrefs.GetString(PrefixKey, "finish");
            foreach (var spec in UnityEditor.EditorPrefs.GetString(RunsKey, "1.4|miss|R").Split(','))
            {
                var parts = spec.Trim().Split('|');
                if (parts.Length < 3) continue;
                float k = float.Parse(parts[0], System.Globalization.CultureInfo.InvariantCulture);
                bool left = parts[2].Trim().ToUpperInvariant() == "L";
                yield return ShootRun(prefix, anim, toPitcher, k, parts[1], left);
            }

            DuelStageRig.CalibrationOverride = null;
            Debug.Log("[Finish] DONE");
        }

        private static DuelPitchOutcome OutcomeOf(string name)
        {
            switch (name)
            {
                case "inplay": return DuelPitchOutcome.InPlayContact;
                case "foul": return DuelPitchOutcome.FoulContact;
                default: return DuelPitchOutcome.SwingMiss;
            }
        }

        private IEnumerator ShootRun(string prefix, Animator anim, Vector3 toPitcher, float k,
            string outName, bool left)
        {
            var cal = DuelCalibration.Upgrade(DuelCalibration.Default);
            cal.SwingSpeed = k;
            DuelStageRig.CalibrationOverride = cal;
            _rig.ApplyCalibration(cal);
            // 前の条件で組んだ寄りの枠を必ず外す（ApplyCalibration はカメラ位置しか戻さない＝
            // 画角が寄ったままだと「全景のはずが望遠」になる。design-18 の実害と同じ罠）
            _rig.ClearPreviewFrame();
            _rig.SetHandedness(left, false);
            _rig.ResetForNewPlateAppearance();
            for (int i = 0; i < 12; i++) { _rig.Tick(1f / 60f); yield return null; }

            string run = string.Format("k{0:F1}-{1}-{2}", k, outName, left ? "L" : "R");
            _rig.OnPitchWindup();
            for (int i = 0; i < 4; i++) { _rig.Tick(1f / 60f); yield return null; }
            _rig.OnPitchReleased(0f, OutcomeOf(outName));

            // 据え置きへ入るまで流す（クリップ時刻が動かなくなった所＝打ち切り）。
            // 秒で決め打ちすると速度・結果ごとに入る時刻が違って外すので、実測で待つ
            var sb = new StringBuilder();
            sb.Append("[Finish] run=").Append(run).Append("\n据え置き中の回り方 t[s]\tclip[s]\t")
              .Append("胸\t左上腕\t左前腕\t左手\t右上腕\t右前腕\t右手（度/コマ）\n");
            float t = 0f, holdFrom = -1f;
            var prev = new Quaternion[Bones.Length];
            bool havePrev = false;
            while (t < 4f)
            {
                _rig.Tick(1f / 60f); t += 1f / 60f;
                yield return null;
                float clip = _rig.BatterClock.x;
                if (holdFrom < 0f)
                {
                    // 「クリップ時刻が動かない」では判定できない（12fps量子化で毎フレーム止まって見える）
                    if (_rig.BatterHolding) holdFrom = t;
                    continue;
                }
                if (t - holdFrom > IntoHoldSec + 0.5f) break;
                sb.Append(t.ToString("F2")).Append('\t').Append(clip.ToString("F3"));
                for (int b = 0; b < Bones.Length; b++)
                {
                    var tr = anim.GetBoneTransform(Bones[b]);
                    var rot = tr != null ? tr.rotation : Quaternion.identity;
                    sb.Append('\t').Append(havePrev && tr != null
                        ? Quaternion.Angle(prev[b], rot).ToString("F0") : "-");
                    prev[b] = rot;
                }
                sb.Append('\n');
                havePrev = true;
                if (t - holdFrom >= IntoHoldSec && !_shot)
                {
                    _shot = true;
                    yield return CaptureSet(prefix + "-" + run, anim, toPitcher);
                }
            }
            _shot = false;
            Debug.Log(sb.ToString());
        }

        private bool _shot;

        private static readonly HumanBodyBones[] Bones =
        {
            HumanBodyBones.Chest,
            HumanBodyBones.LeftUpperArm, HumanBodyBones.LeftLowerArm, HumanBodyBones.LeftHand,
            HumanBodyBones.RightUpperArm, HumanBodyBones.RightLowerArm, HumanBodyBones.RightHand,
        };

        /// <summary>全景＋腕2本×（軸に直交する2方向）。時間は進めない＝同じコマを撮る。</summary>
        private IEnumerator CaptureSet(string name, Animator anim, Vector3 toPitcher)
        {
            // ⓪ 試合の構図そのまま（FramePreview を呼ぶ前に撮る）
            yield return Capture(name + "-0game");
            yield return Still(name + "-1body", Center(anim), 2.6f, 2.0f, toPitcher);

            // 腕は**肩から手まで1本**を収める。上腕だけ・前腕だけに寄ると袖と肌の境目が
            // 画面外へ出て、袖が太いのか腕がねじれているのかが判定できない
            yield return AlongLimb(name + "-2Rarm", anim,
                HumanBodyBones.RightUpperArm, HumanBodyBones.RightHand, 0.75f);
            yield return AlongLimb(name + "-3Larm", anim,
                HumanBodyBones.LeftUpperArm, HumanBodyBones.LeftHand, 0.75f);
        }

        /// <summary>骨の軸に**直交する**2方向から撮る（斜めだと腕と袖の前後が分離できない）。</summary>
        private IEnumerator AlongLimb(string name, Animator anim, HumanBodyBones a, HumanBodyBones b,
            float frameH)
        {
            var p0 = Bone(anim, a);
            var p1 = Bone(anim, b);
            var axis = (p1 - p0);
            if (axis.sqrMagnitude < 1e-6f) yield break;
            axis.Normalize();
            var mid = (p0 + p1) * 0.5f;
            var d1 = Vector3.Cross(axis, Vector3.up);
            if (d1.sqrMagnitude < 1e-4f) d1 = Vector3.Cross(axis, Vector3.forward);
            d1.Normalize();
            var d2 = Vector3.Cross(axis, d1).normalized;
            yield return Still(name + "a", mid, frameH * 2.2f, frameH, d1);
            yield return Still(name + "b", mid, frameH * 2.2f, frameH, d2);
        }

        private void OnDisable()
        {
            if (_rig != null) { _rig.Dispose(); _rig = null; }
        }

        private static GameObject Find(string name)
        {
            foreach (var go in Resources.FindObjectsOfTypeAll<GameObject>())
                if (go != null && go.name == name) return go;
            return null;
        }

        private static Vector3 Bone(Animator a, HumanBodyBones b)
        {
            var t = a.GetBoneTransform(b);
            return t != null ? t.position : Vector3.zero;
        }

        private static Vector3 Center(Animator a)
        {
            var h = Bone(a, HumanBodyBones.Hips);
            return new Vector3(h.x, h.y + 0.15f, h.z);
        }

        /// <summary>時間を進めずに構図だけ組んで撮る（フィニッシュのコマを崩さない）。</summary>
        private IEnumerator Still(string name, Vector3 target, float radius, float frameHeightM, Vector3 dir)
        {
            var cam = target + dir * radius + Vector3.up * (frameHeightM * 0.06f);
            _rig.FramePreview(_stageRoot.InverseTransformPoint(cam),
                _stageRoot.InverseTransformPoint(target), frameHeightM);
            yield return Capture(name);
        }

        private IEnumerator Capture(string name)
        {
            yield return new WaitForEndOfFrame();
            var tex = new Texture2D(W, H, TextureFormat.RGBA32, false);
            var prev = RenderTexture.active;
            RenderTexture.active = _rig.Target;
            tex.ReadPixels(new Rect(0, 0, W, H), 0, 0);
            tex.Apply();
            RenderTexture.active = prev;
            File.WriteAllBytes(Path.Combine(Dir, name + ".png"), tex.EncodeToPNG());
            Destroy(tex);
        }
    }
}
#endif
