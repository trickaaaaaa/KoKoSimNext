#if UNITY_EDITOR
using System.Collections;
using System.IO;
using UnityEngine;

namespace KokoSim.Unity.Match
{
    /// <summary>
    /// 【開発用・エディタ限定・使い捨て】打撃モーションの直し（design-18 §7-2）を実物で確かめる撮影ハーネス。
    ///
    /// 見たいのは3つ（監督指摘 2026-07-29）:
    ///   ① 両手が離れて握れていない ② 頭が振れる・投手を見ていない ③ 踏み込み脚の膝が開きすぎる
    /// ①②は構えで、③はスイング中でしか判定できないので、構え（投手側・寄り2枚・横）と
    /// スイング（投手側・横を各3コマ）を撮る。
    ///
    /// 寄りの絵は**狙点を骨の実測から取る**（カメラの狙い外しと未修正は絵では区別できない＝
    /// グラブ検証の教訓。design-18 §3.3）。
    /// </summary>
    public sealed class DuelBatterFormShooter : MonoBehaviour
    {
        private const string Dir = "screenshots";
        private const int W = 1440;
        private const int H = 1080;

        /// <summary>出力ファイル名の頭（before/after で撮り分けるため起動側が変える）。</summary>
        public static string Prefix = "i-batform";

        private DuelStageRig _rig;
        private Transform _stageRoot;   // ~DuelStageLive（ステージはワールド y=-500 に置かれている）

        /// <summary>
        /// 起動フラグ。Edit modeでシーンへ置いたオブジェクトはPlay突入時のシーン再読込で消えることがあり、
        /// 「Playにはなったが1枚も撮れていない」が起きる（2026-07-29実害）。EditorPrefs に立てた印を
        /// 見て自分で湧く方式にすると、シーンを汚さずに確実に走る。
        /// </summary>
        private const string SpawnFlag = "KokoSim.DuelBatterFormShooter.Spawn";

        /// <summary>撮影を予約する（execute_code から呼んで Play へ入る）。</summary>
        public static void Arm() => UnityEditor.EditorPrefs.SetBool(SpawnFlag, true);

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void AutoSpawn()
        {
            if (!UnityEditor.EditorPrefs.GetBool(SpawnFlag, false)) return;
            UnityEditor.EditorPrefs.SetBool(SpawnFlag, false);
            new GameObject("~BatFormShootAuto").AddComponent<DuelBatterFormShooter>();
        }

        private IEnumerator Start()
        {
            Application.runInBackground = true;
            Directory.CreateDirectory(Dir);

            _rig = new DuelStageRig();
            if (!_rig.Available) { Debug.LogError("[BatForm] rig が組めない"); yield break; }
            _rig.EnsureSize(W, H);
            for (int i = 0; i < 12; i++) { _rig.Tick(1f / 60f); yield return null; }

            // 素体GOの名前はクリップ名から作られる（SpawnActor）。クリップ差し替えで名前が変わるので
            // 定数から引く＝「打者/投手が見つからない」で空振りしない
            var bat = Find("body-" + DuelStageRig.BatterClipName);
            var pit = Find("body-" + DuelStageRig.PitcherClipName);
            var root = Find("~DuelStageLive");
            if (bat == null || pit == null || root == null)
            {
                Debug.LogError("[BatForm] 打者/投手/ステージ根が見つからない");
                yield break;
            }
            _stageRoot = root.transform;
            var anim = bat.GetComponentInChildren<Animator>();

            // 投手方向（＝打者が見るべき向き）と、その真横
            Vector3 toPitcher = pit.transform.position - bat.transform.position;
            toPitcher.y = 0f;
            toPitcher.Normalize();
            Vector3 side = Vector3.Cross(Vector3.up, toPitcher).normalized;

            LogPose(anim, bat, toPitcher, "構え");

            // ⓪ 試合の構図そのまま（FramePreview を呼ぶ前）。立ち向きを変えると承認済みの絵が動くので、
            //    新旧を同じカメラで撮って見比べられるようにする。旧向きは打者を -BatterYawFix 戻して再現
            //    （Tickを挟まない＝ルートモーションで戻されない）
            yield return Capture("6-game-new");
            var keep = bat.transform.rotation;
            bat.transform.rotation = keep * Quaternion.Euler(0f, -33.8f, 0f);
            yield return Capture("7-game-old");
            bat.transform.rotation = keep;

            // ① 構え: 投手側から全身 → 手元の寄り → 頭の寄り → 真横から全身
            yield return Shot("0-stance-front", Center(anim), 3.2f, 2.10f, toPitcher);
            yield return Shot("1-stance-grip", Grip(anim), 0.62f, 0.45f, toPitcher);
            // 握りは真横からも撮る。正面だけだと手首のねじれ（前腕軸まわり）が前後に潰れて見えない
            yield return Shot("1b-stance-grip-side", Grip(anim), 0.62f, 0.45f, side);
            yield return Shot("2-stance-head", Bone(anim, HumanBodyBones.Head), 0.60f, 0.42f, toPitcher);
            yield return Shot("3-stance-side", Center(anim), 3.2f, 2.10f, side);

            // ② スイング: 空振りを流し、面通過の前後を投手側と真横から
            _rig.OnPitchWindup();
            for (int i = 0; i < 4; i++) { _rig.Tick(1f / 60f); yield return null; }
            _rig.OnPitchReleased(0f, DuelPitchOutcome.SwingMiss);

            float[] marks = { 0.12f, 0.30f, 0.45f, 0.62f };   // リリースからの秒（面通過=0.45s）
            float t = 0f;
            for (int m = 0; m < marks.Length; m++)
            {
                while (t < marks[m]) { _rig.Tick(1f / 60f); t += 1f / 60f; yield return null; }
                var c = Center(anim);
                yield return ShotStill("4-swing-front-" + m, c, 3.2f, 2.10f, toPitcher);
                yield return ShotStill("5-swing-side-" + m, c, 3.2f, 2.10f, side);
                if (m == 2) LogPose(anim, bat, toPitcher, "スイング面通過");
            }

            Debug.Log("[BatForm] DONE");
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

        /// <summary>胸のあたり＝全身を収める時の狙点。</summary>
        private static Vector3 Center(Animator a)
        {
            var h = Bone(a, HumanBodyBones.Hips);
            return new Vector3(h.x, h.y + 0.15f, h.z);
        }

        /// <summary>握りの中心（両手首の中点）。</summary>
        private static Vector3 Grip(Animator a)
            => Vector3.Lerp(Bone(a, HumanBodyBones.LeftHand), Bone(a, HumanBodyBones.RightHand), 0.5f);

        /// <summary>絵と一緒に実測値も残す（目測で判定しないため）。</summary>
        private static void LogPose(Animator a, GameObject bat, Vector3 toPitcher, string label)
        {
            Transform gear = null;
            foreach (var t in bat.GetComponentsInChildren<Transform>(true))
                if (t.name == "gear-head") { gear = t; break; }
            var lh = Bone(a, HumanBodyBones.LeftHand);
            var rh = Bone(a, HumanBodyBones.RightHand);
            var thigh = Bone(a, HumanBodyBones.LeftLowerLeg) - Bone(a, HumanBodyBones.LeftUpperLeg);
            thigh.y = 0f;
            string face = "-";
            if (gear != null)
            {
                var f = gear.forward;
                var fh = new Vector3(f.x, 0f, f.z).normalized;
                face = Vector3.SignedAngle(fh, toPitcher, Vector3.up).ToString("F1") + "° 仰角"
                     + (Mathf.Asin(Mathf.Clamp(f.y, -1f, 1f)) * Mathf.Rad2Deg).ToString("F1") + "°";
            }
            Debug.Log("[BatForm] " + label + ": 手首間隔=" + (lh - rh).magnitude.ToString("F3") + "m"
                + " 顔→投手=" + face
                + " 大腿ヨー(投手基準)=" + Vector3.SignedAngle(toPitcher, thigh.normalized, Vector3.up).ToString("F1") + "°");
        }

        /// <summary>FramePreview はステージ根ローカルで受ける。骨の実測はワールドなので必ず通す。</summary>
        private void Aim(Vector3 target, float radius, float frameHeightM, Vector3 dir)
        {
            var cam = target + dir * radius + Vector3.up * (frameHeightM * 0.06f);
            _rig.FramePreview(_stageRoot.InverseTransformPoint(cam),
                _stageRoot.InverseTransformPoint(target), frameHeightM);
        }

        private IEnumerator Shot(string name, Vector3 target, float radius, float frameHeightM, Vector3 dir)
        {
            Aim(target, radius, frameHeightM, dir);
            for (int i = 0; i < 3; i++) { _rig.Tick(1f / 60f); yield return null; }
            yield return Capture(name);
        }

        /// <summary>時間を進めずに撮る（スイングのコマを崩さない）。</summary>
        private IEnumerator ShotStill(string name, Vector3 target, float radius, float frameHeightM, Vector3 dir)
        {
            Aim(target, radius, frameHeightM, dir);
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
            File.WriteAllBytes(Path.Combine(Dir, Prefix + "-" + name + ".png"), tex.EncodeToPNG());
            Destroy(tex);
        }
    }
}
#endif
