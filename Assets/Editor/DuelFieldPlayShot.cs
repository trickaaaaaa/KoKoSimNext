#if UNITY_EDITOR
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using KokoSim.Engine.Match.Timeline.Playback;
using KokoSim.Unity.Match;
using UnityEngine;

namespace KokoSim.UnityEditorTools
{
    /// <summary>
    /// 【開発用・エディタ限定】打球処理3D化 F3 の確認撮影。
    ///
    /// engine の <c>PlaybackSamples</c>（2D俯瞰と同じ実データ）を守備カメラで再生し、
    /// 1プレー＝1枚のコンタクトシート（12fps のコマを並べたもの）＋実測TSVに落とす。
    /// 見たいのは2つ:
    ///   ① 打球の行方が読めるか（既存の追随カメラのパン・ズームが3Dで効いているか）
    ///   ② ズーム上限をいくつにするか（3.4=2D俯瞰の現行値 / 6 / 8 を振って並べる）
    ///
    /// **3結果とも撮る**（ゴロ・フライ・長打）。1結果でしか試さないと見逃す（design-18 §4.5-⑭）。
    /// Edit mode の単発描画はスキンが前ポーズのまま焼かれるが、ここで見たいのは
    /// カメラの枠取りと球の位置なので判断に影響しない。
    /// </summary>
    public static class DuelFieldPlayShot
    {
        private const string Dir = "out/duel-stage/fieldplay";

        /// <summary>コマの解像度。シートに並べるので実ゲーム基準（480×270）の倍で撮る。</summary>
        private const int W = 640;
        private const int H = 360;

        private const int Cols = 4;

        /// <summary>
        /// シートに並べるコマ数。**間隔はプレー長から割り出す**（固定間隔にすると
        /// 長いプレーで頭の数秒しか写らず、3本とも同じ絵になって比較にならない。2026-08-01 実害）。
        /// </summary>
        private const int Frames = 12;

        /// <summary>ゴロ・フライ・長打の3本（PlaybackSamples の添字）。</summary>
        private static readonly int[] PlayIndices = { 2, 1, 5 };

        [UnityEditor.MenuItem("KokoSim/Duel/F3 守備カメラで打球を撮る")]
        public static void Run()
        {
            Directory.CreateDirectory(Dir);
            var log = new StringBuilder();
            log.AppendLine("play\tmaxZoom\tmarginM\tt\tballX\tballZ\tballH\tframeH_m\taimY\taimPx\tballPx\tballU\tballV\tstationY");
            var plans = new StringBuilder();
            plans.AppendLine("play\tstationPlan");

            foreach (var idx in PlayIndices)
            {
                var play = PlaybackSamples.All[idx];
                var rig = new DuelStageRig();
                try
                {
                    if (!rig.Available)
                    {
                        Debug.LogError("[F3Shot] DuelStageRig が組めない（素体/クリップ未導入）");
                        return;
                    }
                    rig.EnsureSize(W, H);
                    // 実ゲームと同じく**打球の瞬間から**始める。0 から始めると据え位置の切り替え計画が
                    // 「まだ対決ビューが映している区間」まで数えてしまい、実際には出ないカットが混ざる。
                    float contact = ContactOf(play);
                    rig.BeginFieldPlay(play, contact);
                    // カットは1コマの絵に写らない＝いつ何回切れるかは実測でしか採れない
                    plans.AppendLine(Label(idx) + "\t" + rig.FieldStationPlanForCapture());

                    var frames = new List<Texture2D>();
                    float step = ((float)play.Dur - contact) / (Frames - 1);
                    for (int n = 0; n < Frames; n++)
                    {
                        float t = contact + n * step;
                        rig.SetFieldTime(t);
                        rig.RenderNow();
                        frames.Add(Grab(rig.Target));
                        log.AppendLine(rig.FieldFrameDiagForCapture(t));
                    }

                    // 名前は日本語なのでファイル名に使えない。添字＋ラベルで一意にする
                    WriteSheet(frames, Label(idx));
                    foreach (var f in frames) Object.DestroyImmediate(f);
                }
                finally
                {
                    rig.Dispose();
                }
            }

            File.WriteAllText(Path.Combine(Dir, "fieldplay.tsv"), log.ToString());
            File.WriteAllText(Path.Combine(Dir, "stations.tsv"), plans.ToString());
            Debug.Log("[F3Shot] DONE -> " + Dir);
        }

        /// <summary>
        /// 打球の瞬間＝先頭の投球セグメントの終わり。
        /// <c>MatchLiveController.ContactMomentOf</c>（private）の第1分岐と同じ定義
        /// （実況キャプション送りと「対決で見せたぶん」の調整は撮影では要らない）。
        /// </summary>
        private static float ContactOf(PlaybackPlay play)
        {
            if (play == null || play.Ball.Count == 0) return 0f;
            var head = play.Ball[0];
            bool isPitch = head is PitchSegment
                || (head is EndpointBallSegment eb
                    && eb.Kind == KokoSim.Engine.Match.Timeline.BallSegmentKind.Pitch);
            return isPitch ? (float)head.T1 : 0f;
        }

        private static Texture2D Grab(RenderTexture rt)
        {
            var prev = RenderTexture.active;
            var tex = new Texture2D(rt.width, rt.height, TextureFormat.RGBA32, false);
            RenderTexture.active = rt;
            tex.ReadPixels(new Rect(0, 0, rt.width, rt.height), 0, 0);
            tex.Apply();
            RenderTexture.active = prev;
            return tex;
        }

        /// <summary>コマを格子に並べて1枚にする（見比べは1枚で済ませたい）。</summary>
        private static void WriteSheet(List<Texture2D> frames, string name)
        {
            if (frames.Count == 0) return;
            int cols = Mathf.Min(Cols, frames.Count);
            int rows = Mathf.CeilToInt(frames.Count / (float)cols);
            int fw = frames[0].width, fh = frames[0].height;
            var sheet = new Texture2D(cols * fw, rows * fh, TextureFormat.RGBA32, false);
            try
            {
                // 余りマスは黒で埋める（ReadPixels の残りかすが出ないように）
                var blank = new Color32[fw * fh];
                for (int i = 0; i < blank.Length; i++) blank[i] = new Color32(0, 0, 0, 255);
                for (int r = 0; r < rows; r++)
                    for (int c = 0; c < cols; c++)
                        sheet.SetPixels32(c * fw, (rows - 1 - r) * fh, fw, fh, blank);

                for (int i = 0; i < frames.Count; i++)
                {
                    int r = i / cols, c = i % cols;
                    // Texture2D は左下原点。コマは左上から並べたいので行を反転する
                    sheet.SetPixels32(c * fw, (rows - 1 - r) * fh, fw, fh, frames[i].GetPixels32());
                }
                sheet.Apply();
                File.WriteAllBytes(Path.Combine(Dir, name + ".png"), sheet.EncodeToPNG());
                Debug.Log("[F3Shot] wrote " + name + " (" + frames.Count + " コマ)");
            }
            finally
            {
                Object.DestroyImmediate(sheet);
            }
        }

        /// <summary>PlaybackSamples の添字 → ファイル名に使える短いラベル。</summary>
        private static string Label(int idx)
        {
            switch (idx)
            {
                case 1: return "fly";     // ② センターフライ
                case 2: return "grd";     // ③ ショートゴロ 6-3
                case 5: return "xbh";     // ⑥ 右中間破る長打→中継バックホーム
                default: return "p" + idx;
            }
        }
    }
}
#endif
