#if UNITY_EDITOR
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Text;
using UnityEngine;
using UnityEngine.UIElements;

namespace KokoSim.Unity.Match
{
    /// <summary>
    /// 球審ストライクコールの間合い録画（issue #353）。1打席を12fpsで撮り、球審中心のコンタクトシートと
    /// 実測ログ（球審のクリップ内時刻・右手の高さ）を書き出す。
    /// 「捕球の後に腕が出ているか」「腕を伸ばし切るまで見えるか」「次のリリースまでに構えへ戻るか」は
    /// 静止スクショでは判定できないので、必ずこの連番＋ログで見る（#333 が暫定値のまま閉じた再発防止）。
    /// <see cref="DuelViewRecorder"/> と同型（打者クロップ→球審クロップに差し替えたもの）。
    /// </summary>
    public sealed class UmpireCallRecorder : MonoBehaviour
    {
        private const int Fps = 12;
        private const float TailSeconds = 4.0f;   // 最後の1球のコール〜構え戻りまで録る

        /// <summary>
        /// 出力先（スクラッチパッド配下）。Play入りのドメインリロードで static は初期化子へ戻るので、
        /// 外から代入するのでなくここを書き換えて使う（<see cref="DuelViewRecorder"/> の Dir と同じ運用）。
        /// </summary>
        public static string OutDir =
            "/private/tmp/claude-501/-Users-seiya-oda-Unity-KoKoSimNext/1048af54-b6a3-474d-9aca-a1be741e7898/scratchpad/umpire-rec";

        /// <summary>
        /// リリース間隔[秒]の上書き（0=既定 <see cref="DuelStageRig.DefaultPitchInterval"/>）。間合いの比較録画用。
        /// Play中にこの static へ入れてから component を足すこと（Play入りを跨ぐと初期化子へ戻る）。
        /// </summary>
        public static float IntervalOverride = 0f;

        private IEnumerator Start()
        {
            Application.runInBackground = true;
            MatchLiveController.CaptureMode = true;
            EnsureOnlyMatchLive();
            yield return null; yield return null;
            EnsureOnlyMatchLive();
            for (int i = 0; i < 180; i++) yield return null;
            EnsureOnlyMatchLive();
            yield return null;

            var ctrl = FindController();
            if (ctrl == null) { Debug.LogError("[UmpRec] MatchLive/Controller が見つからない"); yield break; }
            yield return null;
            Directory.CreateDirectory(OutDir);

            // 見逃しストライクを含む打席を探す（ボール・ファウルが混ざるほど比較材料として良い）
            var kinds = new List<string>();
            var guard = 0;
            bool found = false;
            while (guard++ < 60 && ctrl.AdvanceForCapture())
            {
                var pitches = ctrl.CurrentPa?.PitchSeq?.Pitches;
                if (pitches == null || pitches.Count < 2) continue;
                kinds.Clear();
                bool hasCalled = false;
                for (int i = 0; i < pitches.Count; i++)
                {
                    kinds.Add(pitches[i].Kind.ToString());
                    if (pitches[i].Kind == KokoSim.Engine.Match.AtBat.PitchKind.CalledStrike) hasCalled = true;
                }
                if (hasCalled) { found = true; break; }
            }
            if (!found) { Debug.LogError("[UmpRec] 見逃しストライクを含む打席が見つからない"); yield break; }

            if (!ctrl.DuelViewOnForCapture) ctrl.ToggleDuelViewForCapture();
            var rig = ctrl.DuelRigForCapture;
            if (rig == null || !rig.Available) { Debug.LogError("[UmpRec] DuelStageRig が使えない"); yield break; }

            var router = KokoSim.Unity.Shell.ScreenRouter.Instance;
            if (router != null) router.enabled = false;

            if (IntervalOverride > 0f) ctrl.SetDuelPitchIntervalForCapture(IntervalOverride);
            ctrl.RestartCurrentPaForCapture();
            yield return null;
            yield return new WaitForEndOfFrame();

            var umpPx = rig.UmpireScreenPxForCapture();

            float interval = IntervalOverride > 0f ? IntervalOverride : DuelStageRig.DefaultPitchInterval;
            int pitchCount = kinds.Count;
            float dur = DuelStageRig.WindupSeconds + (pitchCount - 1) * interval + 0.45f + TailSeconds;
            var frames = new List<Texture2D>();
            var diag = new StringBuilder("# 球数=" + pitchCount + " 種別=" + string.Join(",", kinds) + "\n");
            diag.Append("# 間隔=").Append(interval.ToString("F2")).Append("s リリース時刻[s]:");
            for (int i = 0; i < pitchCount; i++)
                diag.Append(' ').Append(i).Append('=')
                    .Append((DuelStageRig.WindupSeconds + i * interval).ToString("F2"));
            diag.Append('\n');
            diag.Append("frame\tt\tumpClip\tumpClipT\tumpHandY\tpClipT\tpYaw\tpDx\tpDz\tpRootM\tpFade\n");

            float t = 0f;
            int shot = 0;
            while (t < dur)
            {
                yield return new WaitForEndOfFrame();
                t += Time.deltaTime;
                if (t * Fps < shot) continue;
                var rt = rig.Target;
                if (rt == null || !ctrl.isActiveAndEnabled)
                {
                    Debug.LogError("[UmpRec] RT/コントローラ消失 t=" + t.ToString("F2") + " frames=" + frames.Count);
                    break;
                }
                var tex = new Texture2D(rt.width, rt.height, TextureFormat.RGBA32, false);
                var prev = RenderTexture.active;
                RenderTexture.active = rt;
                tex.ReadPixels(new Rect(0, 0, rt.width, rt.height), 0, 0);
                tex.Apply();
                RenderTexture.active = prev;
                frames.Add(tex);
                diag.Append(frames.Count - 1).Append('\t').Append(t.ToString("F2")).Append('\t')
                    .Append(rig.UmpireDiagForCapture()).Append('\t')
                    .Append(rig.PitcherDiagForCapture()).Append('\n');
                shot = Mathf.FloorToInt(t * Fps) + 1;
            }

            File.WriteAllText(Path.Combine(OutDir, "diag.tsv"), diag.ToString());
            if (frames.Count == 0) { Debug.LogError("[UmpRec] 1コマも撮れていない"); yield break; }

            for (int i = 0; i < frames.Count; i++)
                File.WriteAllBytes(Path.Combine(OutDir, "full-" + i.ToString("D3") + ".png"), frames[i].EncodeToPNG());

            int w = frames[0].width, h = frames[0].height;
            int cropW = Mathf.Min(w, Mathf.RoundToInt(w * 0.55f));
            int cropX = Mathf.Clamp(Mathf.RoundToInt(umpPx.x) - cropW / 2, 0, w - cropW);
            WriteSheets(frames, 2, 6, 5, new RectInt(cropX, 0, cropW, h), "sheet-ump");
            WriteSheets(frames, 3, 4, 4, new RectInt(0, 0, w, h), "sheet-full");

            Debug.Log("[UmpRec] DONE frames=" + frames.Count + " dur=" + dur.ToString("F1")
                + " pitches=" + pitchCount + " dir=" + OutDir);
            foreach (var f in frames) Destroy(f);
        }

        /// <summary>every コマおきに拾い、cols×rows のタイル（2pxの暗色目地）でシートPNGを書き出す。</summary>
        private static void WriteSheets(List<Texture2D> frames, int every, int cols, int rows, RectInt crop, string prefix)
        {
            const int gap = 2;
            var picked = new List<int>();
            for (int i = 0; i < frames.Count; i += every) picked.Add(i);
            int perSheet = cols * rows;
            int sheetW = cols * crop.width + (cols - 1) * gap;
            int sheetH = rows * crop.height + (rows - 1) * gap;
            var bg = new Color32(24, 24, 24, 255);
            for (int s = 0; s * perSheet < picked.Count; s++)
            {
                var sheet = new Texture2D(sheetW, sheetH, TextureFormat.RGBA32, false);
                var px = new Color32[sheetW * sheetH];
                for (int i = 0; i < px.Length; i++) px[i] = bg;
                sheet.SetPixels32(px);
                for (int k = 0; k < perSheet && s * perSheet + k < picked.Count; k++)
                {
                    var src = frames[picked[s * perSheet + k]];
                    int col = k % cols;
                    int row = k / cols;
                    int dx = col * (crop.width + gap);
                    int dy = sheetH - (row + 1) * crop.height - row * gap;
                    sheet.SetPixels(dx, dy, crop.width, crop.height,
                        src.GetPixels(crop.x, crop.y, crop.width, crop.height));
                }
                sheet.Apply();
                File.WriteAllBytes(Path.Combine(OutDir, prefix + "-" + s + ".png"), sheet.EncodeToPNG());
                Destroy(sheet);
            }
        }

        private static void EnsureOnlyMatchLive()
        {
            foreach (var d in Resources.FindObjectsOfTypeAll<UIDocument>())
            {
                if (!d.gameObject.scene.IsValid()) continue;
                d.gameObject.SetActive(d.gameObject.name == "MatchLive");
            }
        }

        private static MatchLiveController FindController()
        {
            foreach (var d in Resources.FindObjectsOfTypeAll<UIDocument>())
            {
                if (!d.gameObject.scene.IsValid()) continue;
                if (d.gameObject.name == "MatchLive") return d.GetComponent<MatchLiveController>();
            }
            return null;
        }
    }
}
#endif
