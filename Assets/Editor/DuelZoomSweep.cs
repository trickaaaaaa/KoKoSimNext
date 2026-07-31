#if UNITY_EDITOR
using System.Globalization;
using System.IO;
using System.Text;
using KokoSim.Engine.Match.Timeline.Playback;
using KokoSim.Unity.Match;
using UnityEngine;

namespace KokoSim.UnityEditorTools
{
    /// <summary>
    /// 【開発用・エディタ限定】守備カメラの**寄りの深さ**を振って比べる（F3）。
    ///
    /// 高さ（<see cref="DuelStageRig.FieldStation"/>）を振っても被写体が小さいままだったので、
    /// 今度は枠の詰め方そのものを振る。寄りを止めているのは <c>MaxZoom</c> ではなく
    /// <c>MarginM</c>（箱に 2×Margin を足してからズームを決めるので frameH の下限が 2×Margin）。
    /// **両方を組にして段にする**＝どちらか片方だけ振っても頭打ちで絵が変わらない。
    /// </summary>
    public static class DuelZoomSweep
    {
        private const string Dir = "out/duel-stage/zoom";
        private const int W = 960;
        private const int H = 540;

        private const float StationZ = -14f;
        private const float StationHigh = 24f;   // 高さ比較で監督が見た位置

        /// <summary>据え位置の高さ[m]。寄れるようになって初めてここが絵を決める。</summary>
        private static readonly float[] StationHeights = { 8f, 14f, 24f };

        /// <summary>寄りの段。frameH の下限は 2×Margin なので MaxZoom はその上限に合わせて開ける。</summary>
        private static readonly (string Key, float MaxZoom, float Margin)[] Tiers =
        {
            ("t0", 6f, 6.0f),    // 現行（監督が見た絵）
            ("t1", 13f, 6.0f),   // MaxZoom だけ上限まで開ける＝Margin が効いて頭打ちになる段
            ("t2", 26f, 3.0f),
            ("t3", 40f, 2.0f),
            ("t4", 60f, 1.2f),   // ほぼ人ひとりの寄り
        };

        private static readonly (int Idx, string Label)[] Plays =
        {
            (2, "grd"), (1, "fly"), (5, "xbh"),
        };

        /// <summary>追随の連番を撮る段（寄った枠で球を追い続けられるかは1コマでは判らない）。</summary>
        private const int TrackTierIndex = 3;
        private const int TrackFrames = 4;

        [UnityEditor.MenuItem("KokoSim/Duel/F3 寄りの深さを振って撮る")]
        public static void Run()
        {
            Directory.CreateDirectory(Dir);
            var log = new StringBuilder();
            log.AppendLine("shot\tplay\tmaxZoom\tmarginM\tt\tballX\tballZ\tballH\tframeH_m\taimY\taimPx\tballPx\tballU\tballV");

            var savedStation = DuelStageRig.FieldStation;
            var savedZoom = DuelStageRig.FieldMaxZoom;
            var savedMargin = DuelStageRig.FieldMarginM;
            var savedFollow = DuelStageRig.FieldThrowFollowsBall;
            try
            {
                foreach (var (idx, label) in Plays)
                {
                    var play = PlaybackSamples.All[idx];
                    float res = double.IsFinite(play.ResAt) ? (float)play.ResAt : (float)play.Dur * 0.5f;

                    var rig = new DuelStageRig();
                    try
                    {
                        if (!rig.Available) { Debug.LogError("[Zoom] DuelStageRig が組めない"); return; }
                        rig.EnsureSize(W, H);

                        var tk = Tiers[TrackTierIndex];

                        // ① 決着の瞬間を、寄りの段ぶん撮る（送球フレームは2Dのまま＝余白だけの効きを見る）
                        DuelStageRig.FieldStation = new Vector3(0f, StationHigh, StationZ);
                        foreach (var (key, mz, mg) in Tiers)
                            Shot(rig, log, play, res, mz, mg, false, label + "-" + key);

                        // ② 送球区間で球を追う側に切り替える（ゴロ・長打はここが効く）
                        Shot(rig, log, play, res, tk.MaxZoom, tk.Margin, true, label + "-follow");

                        // ③ 寄れるようになって初めて据え位置の高さが効く（視線が寝るほど立体が出るが、
                        //    低いと外野の絵は書き割り（フェンス外の暗幕）に食われる）
                        foreach (var h in StationHeights)
                        {
                            DuelStageRig.FieldStation = new Vector3(0f, h, StationZ);
                            Shot(rig, log, play, res, tk.MaxZoom, tk.Margin, true,
                                label + "-s" + h.ToString("0", CultureInfo.InvariantCulture));
                        }

                        // ④ 追随の連番（寄った枠で球を追い続けられるか）
                        DuelStageRig.FieldStation = new Vector3(0f, StationHigh, StationZ);
                        for (int i = 0; i < TrackFrames; i++)
                        {
                            float t = Mathf.Lerp(res * 0.2f, res, i / (float)(TrackFrames - 1));
                            Shot(rig, log, play, t, tk.MaxZoom, tk.Margin, true,
                                label + "-trk" + i.ToString(CultureInfo.InvariantCulture));
                        }
                    }
                    finally { rig.Dispose(); }
                }
            }
            finally
            {
                DuelStageRig.FieldStation = savedStation;
                DuelStageRig.FieldMaxZoom = savedZoom;
                DuelStageRig.FieldMarginM = savedMargin;
                DuelStageRig.FieldThrowFollowsBall = savedFollow;
            }

            File.WriteAllText(Path.Combine(Dir, "zoom.tsv"), log.ToString());
            Debug.Log("[Zoom] DONE -> " + Dir);
        }

        private static void Shot(DuelStageRig rig, StringBuilder log, PlaybackPlay play,
            float t, float maxZoom, float margin, bool throwFollowsBall, string name)
        {
            DuelStageRig.FieldMaxZoom = maxZoom;
            DuelStageRig.FieldMarginM = margin;
            DuelStageRig.FieldThrowFollowsBall = throwFollowsBall;
            rig.BeginFieldPlay(play, t);   // プランは Margin/MaxZoom 込みでここで組み直される
            rig.RenderNow();
            Write(rig.Target, name);
            log.AppendLine(name + "\t" + rig.FieldFrameDiagForCapture(t));
        }

        private static void Write(RenderTexture rt, string name)
        {
            if (rt == null) return;
            var prev = RenderTexture.active;
            var tex = new Texture2D(rt.width, rt.height, TextureFormat.RGBA32, false);
            try
            {
                RenderTexture.active = rt;
                tex.ReadPixels(new Rect(0, 0, rt.width, rt.height), 0, 0);
                tex.Apply();
                File.WriteAllBytes(Path.Combine(Dir, name + ".png"), tex.EncodeToPNG());
            }
            finally
            {
                RenderTexture.active = prev;
                Object.DestroyImmediate(tex);
            }
        }
    }
}
#endif
