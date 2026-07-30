#if UNITY_EDITOR
using System.Collections;
using System.IO;
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
    /// 出力: screenshots/{Prefix}-{通し番号}-t{リリースからの秒}.png
    /// </summary>
    public sealed class DuelSwingStripShooter : MonoBehaviour
    {
        private const string Dir = "screenshots";
        private const int W = 1440;
        private const int H = 810;   // 16:9（基準480×270の3倍）＝確定構図の縦横比

        /// <summary>出力名の頭。クリップを入れ替えて2本撮るとき起動側が EditorPrefs 経由で変える。</summary>
        private const string PrefixKey = "KokoSim.DuelSwingStripShooter.Prefix";

        /// <summary>動作モード。""=そのまま撮る / "bake"=手追従の結果を焼く / "path"=焼いた軌道で撮る。</summary>
        private const string ModeKey = "KokoSim.DuelSwingStripShooter.Mode";

        /// <summary>焼いた軌道の置き場（out/ は成果物置き場でgitignore＝較正JSONと同じ扱い）。</summary>
        public const string BatPathJson = "out/duel-bat-path.json";

        private const float StepSec = 0.05f;
        private const float FirstSec = 0.05f;
        private const int FrameCount = 16;   // 0.05〜0.80s（面通過=0.45s を中に含む）

        private DuelStageRig _rig;

        private const string SpawnFlag = "KokoSim.DuelSwingStripShooter.Spawn";

        public static void Arm(string prefix) => Arm(prefix, "");

        public static void Arm(string prefix, string mode)
        {
            UnityEditor.EditorPrefs.SetString(PrefixKey, prefix);
            UnityEditor.EditorPrefs.SetString(ModeKey, mode ?? "");
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

            string mode = UnityEditor.EditorPrefs.GetString(ModeKey, "");
            // 軌道テーブルは**リグを組む前**に載せる（Tick が始まった後だと最初の数コマだけ手追従になる）。
            // static はPlay突入のドメインリロードで消えるので、Edit modeで代入しても届かない＝
            // ここで毎回ファイルから読む（較正上書きが消える罠と同じ性質。2026-07-30 実害）
            DuelBatPath.Recording = mode == "bake";
            DuelBatPath.Recorded = mode == "bake" ? new DuelBatPath() : null;
            DuelBatPath.Current = null;
            if (mode == "path" && File.Exists(BatPathJson))
            {
                DuelBatPath.Current = DuelBatPath.FromJson(File.ReadAllText(BatPathJson));
                Debug.Log("[SwingStrip] 軌道を読み込み: keys="
                    + (DuelBatPath.Current == null ? 0 : DuelBatPath.Current.Keys.Count));
            }

            _rig = new DuelStageRig();
            if (!_rig.Available) { Debug.LogError("[SwingStrip] rig が組めない"); yield break; }
            _rig.EnsureSize(W, H);
            if (mode == "path")
            {
                // 較正の切り替えはリグへ直に流す（CalibrationOverride は Play 突入で消えるため）
                var cal = DuelCalibration.Default;
                cal.BatFromPath = 1f;
                _rig.ApplyCalibration(cal);
            }
            for (int i = 0; i < 12; i++) { _rig.Tick(1f / 60f); yield return null; }

            var bat = Find("body-" + DuelStageRig.BatterClipName);
            if (bat == null)
            {
                Debug.LogError("[SwingStrip] 打者が見つからない（clip=" + DuelStageRig.BatterClipName + "）");
                yield break;
            }
            // カメラには触らない＝ここまでで組まれている確定構図（R-comp60）のまま撮る

            _rig.OnPitchWindup();
            for (int i = 0; i < 4; i++) { _rig.Tick(1f / 60f); yield return null; }
            _rig.OnPitchReleased(0f, DuelPitchOutcome.SwingMiss);

            float t = 0f;
            for (int f = 0; f < FrameCount; f++)
            {
                float mark = FirstSec + f * StepSec;
                while (t < mark) { _rig.Tick(1f / 60f); t += 1f / 60f; yield return null; }
                var clock = _rig.BatterClock;
                Debug.Log(string.Format("[SwingStrip] f={0:00} t={1:F2}s clip={2:F3}s speed={3:F2}",
                    f, mark, clock.x, clock.y));
                yield return Capture(string.Format("{0}-{1:00}-t{2:F2}",
                    UnityEditor.EditorPrefs.GetString(PrefixKey, "strip"), f, mark));
            }

            if (mode == "bake" && DuelBatPath.Recorded != null && DuelBatPath.Recorded.Keys.Count > 0)
            {
                DuelBatPath.Recording = false;
                Directory.CreateDirectory(Path.GetDirectoryName(BatPathJson));
                File.WriteAllText(BatPathJson, DuelBatPath.Recorded.ToJson());
                Debug.Log("[SwingStrip] 軌道を焼いた: keys=" + DuelBatPath.Recorded.Keys.Count
                    + " → " + BatPathJson);
            }

            Debug.Log("[SwingStrip] DONE");
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
