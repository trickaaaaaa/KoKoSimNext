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
    /// 【開発用・エディタ限定】守備カメラの据え位置（station）の高さを振って比べる（F3）。
    ///
    /// 「縦に収めたい実寸」を詰めても、カメラが低いと視線が地面とほぼ平行になり、
    /// 画面の大半が遠景の芝で埋まって被写体が埋もれる。高さは絵でしか決まらないので並べる。
    /// 撮るのは**決着の瞬間**（<c>ResAt</c>）＝そのプレーでいちばん見たいコマ。
    /// </summary>
    public static class DuelStationSweep
    {
        private const string Dir = "out/duel-stage/station";
        private const int W = 960;
        private const int H = 540;

        /// <summary>本塁からの後退距離[m]。バックネット書き割り（z=−16）より手前に置く。</summary>
        private const float StationZ = -14f;

        private static readonly float[] Heights = { 24f, 45f, 70f, 100f };

        /// <summary>ゴロ・フライ・長打（PlaybackSamples の添字）とファイル名ラベル。</summary>
        private static readonly (int Idx, string Label)[] Plays =
        {
            (2, "grd"), (1, "fly"), (5, "xbh"),
        };

        [UnityEditor.MenuItem("KokoSim/Duel/F3 守備カメラの高さを振って撮る")]
        public static void Run()
        {
            Directory.CreateDirectory(Dir);
            var log = new StringBuilder();
            log.AppendLine("play\theightM\tt\tframeH_m\taimPx\tballPx");

            var savedStation = DuelStageRig.FieldStation;
            var savedZoom = DuelStageRig.FieldMaxZoom;
            DuelStageRig.FieldMaxZoom = 8f;
            try
            {
                foreach (var (idx, label) in Plays)
                {
                    var play = PlaybackSamples.All[idx];
                    // 決着の瞬間。ResAt が非有限なプレーは中盤で代用する
                    float t = double.IsFinite(play.ResAt) ? (float)play.ResAt : (float)play.Dur * 0.5f;

                    foreach (var h in Heights)
                    {
                        DuelStageRig.FieldStation = new Vector3(0f, h, StationZ);
                        var rig = new DuelStageRig();
                        try
                        {
                            if (!rig.Available)
                            {
                                Debug.LogError("[Station] DuelStageRig が組めない");
                                return;
                            }
                            rig.EnsureSize(W, H);
                            rig.BeginFieldPlay(play, t);
                            rig.RenderNow();
                            Write(rig.Target,
                                label + "-h" + h.ToString("0", CultureInfo.InvariantCulture));
                            log.AppendLine(h.ToString("0", CultureInfo.InvariantCulture) + "\t"
                                + rig.FieldFrameDiagForCapture(t));
                        }
                        finally
                        {
                            rig.Dispose();
                        }
                    }
                }
            }
            finally
            {
                DuelStageRig.FieldStation = savedStation;
                DuelStageRig.FieldMaxZoom = savedZoom;
            }

            File.WriteAllText(Path.Combine(Dir, "station.tsv"), log.ToString());
            Debug.Log("[Station] DONE -> " + Dir);
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
                Debug.Log("[Station] wrote " + name);
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
