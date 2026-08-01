#if UNITY_EDITOR
using System.Collections;
using System.IO;
using System.Text;
using UnityEngine;

namespace KokoSim.Unity.Match
{
    /// <summary>
    /// 【開発用・エディタ限定】打撃クリップのスイングを**試合の構図のまま等間隔で**撮るコマ送りハーネス。
    ///
    /// <see cref="DuelBatterFormShooter"/> は破綻箇所の寄りを撮るのが目的で、コマごとに骨へ狙いを
    /// 付け直す＝並べても動きにならない。差し替えたクリップの「振りとして繋がっているか」は
    /// カメラを動かさずに並べないと判定できない。
    ///
    /// カメラは**FramePreview を呼ばない**＝確定構図（R-comp60）のまま撮る。寄りや真横で判定すると
    /// 実際にプレイヤーが見る絵と違う結論になるため（2026-07-30 監督指摘）。RTは基準の16:9で、
    /// 縦横比を変えると構図が崩れる（EnsureSize のクランプと ApplyFov は 16:9 基準）。
    ///
    /// issue #368 で**条件の総当たり**へ拡張した。1回の Play で「再生速度 × 結果 × 左右打者」を
    /// 順に撮る＝Play突入のたびにドメインリロードが挟まる連打を避ける（unity-batch-screenshot の教訓）。
    /// あわせて毎コマの骨の回り方（度/コマ）をログへ出す＝フィニッシュの腕の破綻を**実測で**切り分ける
    /// ためのデータ（design-18 §4.5-⑩「惰性の手は1コマで175°回る」の再測定）。
    ///
    /// 出力: screenshots/{Prefix}-{run}-{通し番号}-t{リリースからの秒}.jpg
    /// </summary>
    public sealed class DuelSwingStripShooter : MonoBehaviour
    {
        private const string Dir = "screenshots";
        private const int W = 960;
        private const int H = 540;   // 16:9（基準480×270の2倍）＝確定構図の縦横比。
                                     // フォームを寄って判定したい回だけ 1920×1080 へ上げる（構図は画角固定なので変わらない）

        /// <summary>出力名の頭。クリップを入れ替えて2本撮るとき起動側が EditorPrefs 経由で変える。</summary>
        private const string PrefixKey = "KokoSim.DuelSwingStripShooter.Prefix";

        /// <summary>撮る条件の並び。"速度|結果|左右" をカンマ区切り（例 "1.4|miss|R,2.0|inplay|L"）。</summary>
        private const string RunsKey = "KokoSim.DuelSwingStripShooter.Runs";

        private const float StepSec = 0.05f;
        private const float FirstSec = 0.05f;
        /// <summary>0.05〜1.20s。面通過(0.45s)と振り切り(速度1.4で0.84s)・据え置きの頭までを含む。</summary>
        private const int FrameCount = 24;

        private DuelStageRig _rig;

        private const string SpawnFlag = "KokoSim.DuelSwingStripShooter.Spawn";

        public static void Arm(string prefix) => Arm(prefix, "1.4|miss|R");

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
            new GameObject("~SwingStripAuto").AddComponent<DuelSwingStripShooter>();
        }

        private IEnumerator Start()
        {
            Application.runInBackground = true;
            Directory.CreateDirectory(Dir);

            _rig = new DuelStageRig();
            if (!_rig.Available) { Debug.LogError("[SwingStrip] rig が組めない"); yield break; }
            _rig.EnsureSize(W, H);
            for (int i = 0; i < 12; i++) { _rig.Tick(1f / 60f); yield return null; }

            var bat = Find("body-" + DuelStageRig.BatterClipName);
            if (bat == null)
            {
                Debug.LogError("[SwingStrip] 打者が見つからない（clip=" + DuelStageRig.BatterClipName + "）");
                yield break;
            }
            var anim = bat.GetComponentInChildren<Animator>();
            // カメラには触らない＝ここまでで組まれている確定構図（R-comp60）のまま撮る

            string prefix = UnityEditor.EditorPrefs.GetString(PrefixKey, "strip");
            string runs = UnityEditor.EditorPrefs.GetString(RunsKey, "1.4|miss|R");
            foreach (var spec in runs.Split(','))
            {
                // 速さ|結果|左右[|胴の戻し[度]|立ち向きのオフセット[度]]。後ろ2つは省略可（既定＝較正のまま）
                var parts = spec.Trim().Split('|');
                if (parts.Length < 3) continue;
                float k = Num(parts[0], 1.4f);
                string outName = parts[1];
                bool left = parts[2].Trim().ToUpperInvariant() == "L";
                float torso = parts.Length > 3 ? Num(parts[3], float.NaN) : float.NaN;
                float yaw = parts.Length > 4 ? Num(parts[4], 0f) : 0f;
                yield return ShootRun(prefix, anim, k, outName, left, torso, yaw);
            }

            DuelStageRig.CalibrationOverride = null;
            Debug.Log("[SwingStrip] DONE");
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

        private static float Num(string s, float fallback)
        {
            float v;
            return float.TryParse(s.Trim(), System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out v) ? v : fallback;
        }

        private IEnumerator ShootRun(string prefix, Animator anim, float k, string outName, bool left,
            float torsoCloseDeg, float yawOffsetDeg)
        {
            var cal = DuelCalibration.Upgrade(DuelCalibration.Default);
            cal.SwingSpeed = k;
            // 開き（2026-08-01）の比較用。NaN=較正のまま／立ち向きは**加算**なので0で無効
            if (!float.IsNaN(torsoCloseDeg)) cal.TorsoCloseDeg = torsoCloseDeg;
            cal.BatterYawDeg += yawOffsetDeg;
            DuelStageRig.CalibrationOverride = cal;
            _rig.ApplyCalibration(cal);
            _rig.SetHandedness(left, false);
            _rig.ResetForNewPlateAppearance();
            for (int i = 0; i < 12; i++) { _rig.Tick(1f / 60f); yield return null; }

            string run = string.Format("k{0:F1}-{1}-{2}-t{3}-y{4}", k, outName, left ? "L" : "R",
                float.IsNaN(torsoCloseDeg) ? "def" : torsoCloseDeg.ToString("F0"), yawOffsetDeg.ToString("F0"));
            _rig.OnPitchWindup();
            for (int i = 0; i < 4; i++) { _rig.Tick(1f / 60f); yield return null; }
            _rig.OnPitchReleased(0f, OutcomeOf(outName));

            var sb = new StringBuilder();
            sb.Append("[SwingStrip] run=").Append(run).Append('\n');
            sb.Append("f\tt[s]\tclip[s]\tspd\t")
              .Append("胸\t左上腕\t左前腕\t左手\t右上腕\t右前腕\t右手（度/コマ）\t右前腕の向き\n");
            var prev = new Quaternion[Bones.Length];
            bool havePrev = false;
            float t = 0f;
            for (int f = 0; f < FrameCount; f++)
            {
                float mark = FirstSec + f * StepSec;
                while (t < mark) { _rig.Tick(1f / 60f); t += 1f / 60f; yield return null; }
                var clock = _rig.BatterClock;
                sb.Append(f.ToString("00")).Append('\t').Append(mark.ToString("F2")).Append('\t')
                  .Append(clock.x.ToString("F3")).Append('\t').Append(clock.y.ToString("F2"));
                for (int b = 0; b < Bones.Length; b++)
                {
                    var tr = anim.GetBoneTransform(Bones[b]);
                    var rot = tr != null ? tr.rotation : Quaternion.identity;
                    sb.Append('\t').Append(havePrev && tr != null
                        ? Quaternion.Angle(prev[b], rot).ToString("F0") : "-");
                    prev[b] = rot;
                }
                // 絶対値も1本出す＝「据え置き中に回っている」のが**累積**（毎コマ同じ向きへ足される）か
                // **点滅**（2つの姿勢を往復）かは、差分だけでは切り分けられない
                var rl = anim.GetBoneTransform(HumanBodyBones.RightLowerArm);
                sb.Append('\t').Append(rl != null ? rl.forward.ToString("F2") : "-");
                sb.Append('\n');
                havePrev = true;
                yield return Capture(string.Format("{0}-{1}-{2:00}-t{3:F2}", prefix, run, f, mark));
            }
            Debug.Log(sb.ToString());
        }

        /// <summary>回り方を測る骨（胸＝体の回転／上腕・前腕・手＝腕の回内）。</summary>
        private static readonly HumanBodyBones[] Bones =
        {
            HumanBodyBones.Chest,
            HumanBodyBones.LeftUpperArm, HumanBodyBones.LeftLowerArm, HumanBodyBones.LeftHand,
            HumanBodyBones.RightUpperArm, HumanBodyBones.RightLowerArm, HumanBodyBones.RightHand,
        };

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

        private IEnumerator Capture(string name)
        {
            yield return new WaitForEndOfFrame();
            var tex = new Texture2D(W, H, TextureFormat.RGBA32, false);
            var prev = RenderTexture.active;
            RenderTexture.active = _rig.Target;
            tex.ReadPixels(new Rect(0, 0, W, H), 0, 0);
            tex.Apply();
            RenderTexture.active = prev;
            // 総当たりで枚数が増える（速度3×結果3×左右2×24コマ）ので比較シートは JPEG で組む。
            // 破綻の切り分けに使う寄りの絵は別ハーネスが PNG で撮る
            File.WriteAllBytes(Path.Combine(Dir, name + ".jpg"), tex.EncodeToJPG(88));
            Destroy(tex);
        }
    }
}
#endif
