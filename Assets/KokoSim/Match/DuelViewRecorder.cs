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
    /// 【開発用・エディタ限定・使い捨て】対決ビューのバット追従検証用「録画」ハーネス。
    /// 空振りを含む打席を頭から再生し、ステージRTを12fps（ポーズ量子化と同刻み）で読み出して
    /// 連番PNG＋コンタクトシート（フル画/打者クロップ）＋バット診断TSVを吐く。検証が済んだら削除してよい。
    /// </summary>
    public sealed class DuelViewRecorder : MonoBehaviour
    {
        private const int Fps = 12;
        private const float TailSeconds = 6.0f;   // スイング後（走り出し→idle復帰→次球の見送りまで）を録る
        private const string Dir =
            "/private/tmp/claude-501/-Users-seiya-oda-Unity-KoKoSimNext/765cbda6-20bc-46f3-a6ab-86132e97a1d5/scratchpad/duel-rec";

        private IEnumerator Start()
        {
            Application.runInBackground = true;
            MatchLiveController.CaptureMode = true;
            EnsureOnlyMatchLive();
            yield return null; yield return null;
            EnsureOnlyMatchLive();

            // シェル側の遅延遷移（NewTeamService.Clear後のHome再活性化）がMatchLiveを非活性化して
            // OnDisable→rig.Dispose を踏む罠があるため、遷移が落ち着くまで待ってから再アサートする
            for (int i = 0; i < 180; i++) yield return null;
            EnsureOnlyMatchLive();
            yield return null;

            var ctrl = FindController();
            if (ctrl == null) { Debug.LogError("[DuelRec] MatchLive/Controller が見つからない"); yield break; }
            yield return null;
            Directory.CreateDirectory(Dir);

            // 空振りを含む打席を探す（DuelViewShooterと同じ走査）
            int targetIdx = -1;
            var guard = 0;
            while (guard++ < 60 && ctrl.AdvanceForCapture())
            {
                var pitches = ctrl.CurrentPa?.PitchSeq?.Pitches;
                if (pitches == null) continue;
                for (int i = 0; i < pitches.Count; i++)
                {
                    if (pitches[i].Kind == KokoSim.Engine.Match.AtBat.PitchKind.SwingingStrike) { targetIdx = i; break; }
                }
                if (targetIdx >= 0) break;
            }
            if (targetIdx < 0) { Debug.LogError("[DuelRec] 空振りを含む打席が見つからない"); yield break; }

            if (!ctrl.DuelViewOnForCapture) ctrl.ToggleDuelViewForCapture();
            var rig = ctrl.DuelRigForCapture;
            if (rig == null || !rig.Available) { Debug.LogError("[DuelRec] DuelStageRig が使えない"); yield break; }

            // 録画中の画面遷移を差し止める: 試合終了ハンドオフや週送りは全て ShowDeferred（ルータUpdate適用）
            // 経由なので、ルータを無効化すれば MatchLive の OnDisable→rig破棄を防げる
            var router = KokoSim.Unity.Shell.ScreenRouter.Instance;
            if (router != null) router.enabled = false;

            ctrl.RestartCurrentPaForCapture();
            yield return null;
            yield return new WaitForEndOfFrame();

            // クロップ中心＝録画開始時（構え）の左手ボーン位置。以後固定（スイングの弧が入るよう幅広め）
            var handPx = rig.BatterScreenPxForCapture();

            float dur = 3.0f + targetIdx * 4.5f + 0.45f + TailSeconds;
            var frames = new List<Texture2D>();
            var diag = new StringBuilder("frame\tt\tclip\tclipT\thandGap\toneHand\tbatActive\n");
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
                    // rigのDispose（=MatchLive非活性化）を踏んだら黙ってstarveせず、撮れた分で打ち切る。
                    // 誰に画面を奪われたか分かるようアクティブ文書を添える
                    var docs = new StringBuilder();
                    foreach (var d in Resources.FindObjectsOfTypeAll<UIDocument>())
                        if (d.gameObject.scene.IsValid() && d.gameObject.activeInHierarchy)
                            docs.Append(d.gameObject.name).Append(' ');
                    Debug.LogError("[DuelRec] RT/コントローラ消失 t=" + t.ToString("F2")
                        + " frames=" + frames.Count + " active=[" + docs + "]");
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
                    .Append(rig.BatDiagForCapture()).Append('\n');
                shot = Mathf.FloorToInt(t * Fps) + 1;
            }

            File.WriteAllText(Path.Combine(Dir, "diag.tsv"), diag.ToString());
            if (frames.Count == 0) { Debug.LogError("[DuelRec] 1コマも撮れていない"); yield break; }

            // 連番フル画（個別確認・ズーム用）
            for (int i = 0; i < frames.Count; i++)
                File.WriteAllBytes(Path.Combine(Dir, "full-" + i.ToString("D3") + ".png"), frames[i].EncodeToPNG());

            // コンタクトシート: フル画は3コマ間引き4列、打者クロップは2コマ間引き5列
            int w = frames[0].width, h = frames[0].height;
            var fullRect = new RectInt(0, 0, w, h);
            int cropW = Mathf.Min(w, Mathf.RoundToInt(w * 0.62f));
            int cropX = Mathf.Clamp(Mathf.RoundToInt(handPx.x) - cropW / 2, 0, w - cropW);
            var cropRect = new RectInt(cropX, 0, cropW, h);
            WriteSheets(frames, 3, 4, 4, fullRect, "sheet-full");
            WriteSheets(frames, 2, 5, 5, cropRect, "sheet-crop");

            Debug.Log("[DuelRec] DONE frames=" + frames.Count + " dur=" + dur.ToString("F1")
                + " targetIdx=" + targetIdx + " crop=" + cropRect);
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
                    int row = k / cols;   // 上段→下段の時系列にするためY反転で書く
                    int dx = col * (crop.width + gap);
                    int dy = sheetH - (row + 1) * crop.height - row * gap;
                    sheet.SetPixels(dx, dy, crop.width, crop.height,
                        src.GetPixels(crop.x, crop.y, crop.width, crop.height));
                }
                sheet.Apply();
                File.WriteAllBytes(Path.Combine(Dir, prefix + "-" + s + ".png"), sheet.EncodeToPNG());
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
