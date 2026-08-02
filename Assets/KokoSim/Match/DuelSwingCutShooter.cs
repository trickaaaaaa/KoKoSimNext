#if UNITY_EDITOR
using System.Collections;
using System.IO;
using UnityEngine;

namespace KokoSim.Unity.Match
{
    /// <summary>
    /// 【開発用・エディタ限定】振り抜きの止め所（<c>SwingFinishSec</c>）とバットの巻き付きコマ数
    /// （<c>BatWrapFrames</c>）を振って、据え置きのポーズを撮り比べるハーネス（issue #395）。
    ///
    /// 打つ・空振り・ファールは接触時刻が違う（<see cref="DuelStageRig"/> の SwingHitContact/
    /// SwingMissPlane）ので、巻き付き→惰性の受け渡しが両立するかは**結果ごとに組み合わせで
    /// 撮らないと分からない**（design-18 §4.5-⑩）。<c>SwingFinishOverrideForCapture</c> /
    /// <c>BatWrapFramesOverrideForCapture</c> で定数を差し替えて撮る（負値＝既定のまま）。
    ///
    /// 出力: screenshots/{Prefix}-{条件}-{視点}.png
    /// </summary>
    public sealed class DuelSwingCutShooter : MonoBehaviour
    {
        private const string Dir = "screenshots";
        private const int W = 1280;
        private const int H = 960;

        private const string PrefixKey = "KokoSim.DuelSwingCutShooter.Prefix";
        private const string RunsKey = "KokoSim.DuelSwingCutShooter.Runs";
        private const string SpawnFlag = "KokoSim.DuelSwingCutShooter.Spawn";

        /// <summary>据え置きへ入ってからこの秒だけ進めた所を撮る（issue #395 の実測と同じ基準）。</summary>
        private const float IntoHoldSec = 0.30f;

        private DuelStageRig _rig;
        private Transform _stageRoot;

        /// <summary>
        /// <paramref name="runs"/> はカンマ区切りで各条件を <c>打ち切り|巻き付きコマ|結果|打席</c>
        /// （例: <c>"1.433|1.6|miss|R"</c>）。打ち切り・巻き付きコマは負値で既定（定数そのまま）を使う。
        /// 結果は <c>inplay</c> / <c>foul</c> / それ以外は空振り扱い。打席は <c>R</c> / <c>L</c>。
        /// </summary>
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
            new GameObject("~SwingCutAuto").AddComponent<DuelSwingCutShooter>();
        }

        private IEnumerator Start()
        {
            Application.runInBackground = true;
            Directory.CreateDirectory(Dir);

            _rig = new DuelStageRig();
            if (!_rig.Available) { Debug.LogError("[Cut] rig が組めない"); yield break; }
            _rig.EnsureSize(W, H);
            for (int i = 0; i < 12; i++) { _rig.Tick(1f / 60f); yield return null; }

            var bat = Find("body-" + DuelStageRig.BatterClipName);
            var pit = Find("body-" + DuelStageRig.PitcherClipName);
            var root = Find("~DuelStageLive");
            if (bat == null || pit == null || root == null)
            {
                Debug.LogError("[Cut] 打者/投手/ステージ根が見つからない");
                yield break;
            }
            _stageRoot = root.transform;
            var anim = bat.GetComponentInChildren<Animator>();

            Vector3 toPitcher = pit.transform.position - bat.transform.position;
            toPitcher.y = 0f;
            toPitcher.Normalize();

            string prefix = UnityEditor.EditorPrefs.GetString(PrefixKey, "cut");
            string defaultRuns = "-1|-1|inplay|R,-1|-1|inplay|L,-1|-1|foul|R,-1|-1|foul|L,"
                + "-1|-1|miss|R,-1|-1|miss|L";
            foreach (var spec in UnityEditor.EditorPrefs.GetString(RunsKey, defaultRuns).Split(','))
            {
                var parts = spec.Trim().Split('|');
                if (parts.Length < 4) continue;
                float finishSec = float.Parse(parts[0], System.Globalization.CultureInfo.InvariantCulture);
                float wrapFrames = float.Parse(parts[1], System.Globalization.CultureInfo.InvariantCulture);
                bool left = parts[3].Trim().ToUpperInvariant() == "L";
                yield return ShootRun(prefix, anim, toPitcher, finishSec, wrapFrames, parts[2], left);
            }

            DuelStageRig.CalibrationOverride = null;
            DuelStageRig.SwingFinishOverrideForCapture = -1f;
            DuelStageRig.BatWrapFramesOverrideForCapture = -1f;
            Debug.Log("[Cut] DONE");
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

        private IEnumerator ShootRun(string prefix, Animator anim, Vector3 toPitcher, float finishSec,
            float wrapFrames, string outName, bool left)
        {
            DuelStageRig.SwingFinishOverrideForCapture = finishSec;
            DuelStageRig.BatWrapFramesOverrideForCapture = wrapFrames;
            var cal = DuelCalibration.Upgrade(DuelCalibration.Default);
            DuelStageRig.CalibrationOverride = cal;
            _rig.ApplyCalibration(cal);
            // 前の条件で組んだ寄りの枠を必ず外す。ApplyCalibration → ClearPreviewFrame の順でも
            // 構図が戻ることを毎条件で確かめる（issue #395 の ClearPreviewFrame 修正）
            _rig.ClearPreviewFrame();
            _rig.SetHandedness(left, false);
            _rig.ResetForNewPlateAppearance();
            for (int i = 0; i < 12; i++) { _rig.Tick(1f / 60f); yield return null; }

            string run = string.Format("f{0:F3}-w{1:F2}-{2}-{3}", finishSec, wrapFrames, outName,
                left ? "L" : "R");
            _rig.OnPitchWindup();
            for (int i = 0; i < 4; i++) { _rig.Tick(1f / 60f); yield return null; }
            _rig.OnPitchReleased(0f, OutcomeOf(outName));

            // 据え置きへ入るまで流す（クリップ時刻が動かなくなった所＝打ち切り）。
            // 秒で決め打ちすると速度・結果ごとに入る時刻が違って外すので、実測で待つ
            float t = 0f, holdFrom = -1f;
            bool shot = false;
            while (t < 4f)
            {
                _rig.Tick(1f / 60f); t += 1f / 60f;
                yield return null;
                if (holdFrom < 0f)
                {
                    // 「クリップ時刻が動かない」では判定できない（12fps量子化で毎フレーム止まって見える）
                    if (_rig.BatterHolding) holdFrom = t;
                    continue;
                }
                if (shot)
                {
                    if (t - holdFrom > IntoHoldSec + 0.5f) break;
                    continue;
                }
                if (t - holdFrom >= IntoHoldSec)
                {
                    shot = true;
                    yield return CaptureSet(prefix + "-" + run, anim, toPitcher);
                }
            }
        }

        /// <summary>全景＋据え置きポーズの中景（正面・横）。頭とバットの位置関係が一目で入る画角。</summary>
        private IEnumerator CaptureSet(string name, Animator anim, Vector3 toPitcher)
        {
            // ⓪ 試合の構図そのまま（FramePreview を呼ぶ前に撮る＝ClearPreviewFrame の効きを確認する）
            yield return Capture(name + "-0game");
            yield return Still(name + "-1body", Center(anim), 2.6f, 2.0f, toPitcher);

            // 頭と（頭からより遠い＝振り抜いた側の）手の中間を中心に、直交2方向（正面・横）から寄る。
            // 斜めの絵では頭とバットの前後・左右が分離できない（design-18 §3.3 の教訓）
            var head = HeadArea(anim);
            var side = Vector3.Cross(toPitcher, Vector3.up).normalized;
            yield return Still(name + "-2head-front", head, 1.6f, 1.1f, toPitcher);
            yield return Still(name + "-3head-side", head, 1.6f, 1.1f, side);
        }

        private static Vector3 Center(Animator a)
        {
            var h = Bone(a, HumanBodyBones.Hips);
            return new Vector3(h.x, h.y + 0.15f, h.z);
        }

        /// <summary>頭と振り抜いた側の手の中間＝「頭の横まで抜けたか」を見るための中心点。</summary>
        private static Vector3 HeadArea(Animator a)
        {
            var head = Bone(a, HumanBodyBones.Head);
            var rh = Bone(a, HumanBodyBones.RightHand);
            var lh = Bone(a, HumanBodyBones.LeftHand);
            var hand = Vector3.Distance(head, rh) > Vector3.Distance(head, lh) ? rh : lh;
            return Vector3.Lerp(head, hand, 0.5f);
        }

        private static Vector3 Bone(Animator a, HumanBodyBones b)
        {
            var t = a.GetBoneTransform(b);
            return t != null ? t.position : Vector3.zero;
        }

        /// <summary>時間を進めずに構図だけ組んで撮る（据え置きのコマを崩さない）。</summary>
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
    }
}
#endif
