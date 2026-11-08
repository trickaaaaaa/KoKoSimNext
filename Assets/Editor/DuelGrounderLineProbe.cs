#if UNITY_EDITOR
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using KokoSim.Engine.Core;
using KokoSim.Engine.Match.Field;
using KokoSim.Engine.Match.Game;
using KokoSim.Engine.Match.Timeline;
using KokoSim.Engine.Match.Timeline.Playback;
using KokoSim.Engine.Nation;
using KokoSim.Unity.Match;
using UnityEngine;

namespace KokoSim.UnityEditorTools
{
    /// <summary>
    /// 【開発用・エディタ限定】内野ゴロの捕球で、球が打球の線から横へ外れる量の実測（issue #782）。
    ///
    /// engine で実試合を回して内野ゴロの打席を集め、守備ビューの予定（捕球点）と、捕球直前のコマの
    /// グラブ誤差を測る。**絵では「球が寄った」のか「カメラの角度でそう見える」のか区別できない**
    /// （design-18 §3.3）ので数で見る。出力: out/duel-stage/grounder-line.tsv と要約1行。
    /// </summary>
    public static class DuelGrounderLineProbe
    {
        private const string Dir = "out/duel-stage";

        [UnityEditor.MenuItem("KokoSim/Duel/内野ゴロの捕球点を実測（#782）")]
        public static void RunMenu() => Debug.Log(Run(40, 200));

        /// <summary><paramref name="games"/> 試合ぶんの内野ゴロ（最大 <paramref name="maxPlays"/> 本）を測り、要約を返す。</summary>
        public static string Run(int games, int maxPlays)
        {
            var plays = CollectGrounders(games, maxPlays);
            var rows = new StringBuilder();
            rows.AppendLine("play\tpos\tkind\tlineOff\tfielderGap\tpulled\tgloveErr\tbodyShift\tgloveReach\tclip");
            var ground = new List<float>();
            var groundGlove = new List<float>();
            var airGlove = new List<float>();
            var rig = new DuelStageRig();
            try
            {
                if (!rig.Available) return "DuelStageRig が組めない（素体/クリップ未導入）";
                rig.EnsureSize(320, 180);
                float poseStep = 1f / DuelStageRig.PoseFps;
                for (int i = 0; i < plays.Count; i++)
                {
                    var play = plays[i];
                    float contact = ContactOf(play);
                    rig.BeginFieldPlay(play, contact);
                    for (float t = contact; t <= (float)play.Dur + poseStep; t += poseStep)
                    {
                        if (t > contact) rig.Tick(poseStep);
                        rig.SetFieldTime(t);
                    }
                    foreach (var line in rig.FieldCatchLineDiagForCapture().Split('\n'))
                    {
                        var l = line.Trim();
                        if (l.Length == 0) continue;
                        rows.AppendLine(i + "\t" + l);
                        var c = l.Split('\t');
                        float off = float.Parse(c[2], CultureInfo.InvariantCulture);
                        float glove = float.Parse(c[5], CultureInfo.InvariantCulture);
                        if (c[1] == "roll") { ground.Add(off); if (glove >= 0f) groundGlove.Add(glove); }
                        else if (glove >= 0f) airGlove.Add(glove);
                    }
                }
            }
            finally
            {
                rig.Dispose();
            }

            Directory.CreateDirectory(Dir);
            File.WriteAllText(Path.Combine(Dir, "grounder-line.tsv"), rows.ToString());
            return string.Format(CultureInfo.InvariantCulture,
                "内野ゴロ {0}本 / ゴロ捕球 {1}件: 線からの横ずれ 最大={2:F3}m p95={3:F3}m 平均={4:F3}m >0.05m={5}件"
                + " | ゴロのグラブ誤差 最大={6:F3}m p95={7:F3}m 平均={8:F3}m（{9}件） | 送球・飛球のグラブ誤差 最大={10:F3}m（{11}件）",
                plays.Count, ground.Count, Max(ground), Pct(ground, 0.95f), Mean(ground), Count(ground, 0.05f),
                Max(groundGlove), Pct(groundGlove, 0.95f), Mean(groundGlove), groundGlove.Count,
                Max(airGlove), airGlove.Count);
        }

        /// <summary>
        /// <paramref name="playIndex"/> 本目（<see cref="Run"/> と同じ集合）の <paramref name="pos"/> の捕球を、
        /// 捕球の <paramref name="offsets"/> 秒ずれた時刻で撮る（負＝捕球前）。out/duel-stage/grounder-line-*.png
        /// </summary>
        public static string Shoot(int games, int maxPlays, int playIndex, string pos, float[] offsets)
        {
            var plays = CollectGrounders(games, maxPlays);
            var play = plays[playIndex];
            var rig = new DuelStageRig();
            var sb = new StringBuilder();
            try
            {
                if (!rig.Available) return "DuelStageRig が組めない";
                rig.EnsureSize(960, 540);
                float contact = ContactOf(play);
                rig.BeginFieldPlay(play, contact);
                float tCatch = float.NaN;
                foreach (var line in rig.FieldCatchDiagForCapture().Split('\n'))
                {
                    var c = line.Trim().Split('\t');
                    if (c.Length > 3 && c[0] == pos) { tCatch = float.Parse(c[2], CultureInfo.InvariantCulture); break; }
                }
                if (float.IsNaN(tCatch)) return "捕球予定が無い: " + pos;
                Directory.CreateDirectory(Dir);
                float poseStep = 1f / DuelStageRig.PoseFps;
                int k = 0;
                for (float t = contact; k < offsets.Length && t <= (float)play.Dur + poseStep; t += poseStep)
                {
                    if (t > contact) rig.Tick(poseStep);
                    rig.SetFieldTime(t);
                    if (t + 1e-4f < tCatch + offsets[k]) continue;
                    rig.RenderNow();
                    var rt = rig.Target;
                    var prev = RenderTexture.active;
                    var tex = new Texture2D(rt.width, rt.height, TextureFormat.RGBA32, false);
                    RenderTexture.active = rt;
                    tex.ReadPixels(new Rect(0, 0, rt.width, rt.height), 0, 0);
                    tex.Apply();
                    RenderTexture.active = prev;
                    var file = Path.Combine(Dir, string.Format(CultureInfo.InvariantCulture,
                        "grounder-line-{0}-{1}-{2:+0.00;-0.00}.png", playIndex, pos, offsets[k]));
                    File.WriteAllBytes(file, tex.EncodeToPNG());
                    Object.DestroyImmediate(tex);
                    sb.AppendLine(file + " t=" + t.ToString("F2", CultureInfo.InvariantCulture));
                    k++;
                }
            }
            finally
            {
                rig.Dispose();
            }
            return sb.ToString();
        }

        /// <summary>実試合（固定シード）から内野ゴロの打席の再生データを集める。乱数はシード固定＝毎回同じ集合。</summary>
        private static List<PlaybackPlay> CollectGrounders(int games, int maxPlays)
        {
            var list = new List<PlaybackPlay>();
            for (int g = 0; g < games && list.Count < maxPlays; g++)
            {
                ulong seed = (ulong)(g + 1);
                var away = StrengthTeamFactory.Create(55, "先攻", new Xoshiro256Random(seed * 2));
                var home = StrengthTeamFactory.Create(55, "後攻", new Xoshiro256Random(seed * 2 + 1));
                var result = GameEngine.Play(away, home, new GameContext { CaptureTimelines = true }, new Xoshiro256Random(seed));
                foreach (var e in result.Log)
                {
                    if (e.Timeline == null || e.BattedIsFly) continue;
                    if (!(e.FieldedBy is FieldPosition.Pitcher or FieldPosition.Catcher or FieldPosition.FirstBase
                          or FieldPosition.SecondBase or FieldPosition.ThirdBase or FieldPosition.Shortstop)) continue;
                    list.Add(PlayTimelineAdapter.ToPlaybackPlay(e.Timeline));
                    if (list.Count >= maxPlays) break;
                }
            }
            return list;
        }

        private static float ContactOf(PlaybackPlay play)
        {
            if (play == null || play.Ball.Count == 0) return 0f;
            var head = play.Ball[0];
            bool isPitch = head is PitchSegment
                || (head is EndpointBallSegment eb && eb.Kind == BallSegmentKind.Pitch);
            return isPitch ? (float)head.T1 : 0f;
        }

        private static float Max(List<float> v) { float m = 0f; foreach (var x in v) if (x > m) m = x; return m; }
        private static float Mean(List<float> v) { if (v.Count == 0) return 0f; float s = 0f; foreach (var x in v) s += x; return s / v.Count; }
        private static int Count(List<float> v, float th) { int n = 0; foreach (var x in v) if (x > th) n++; return n; }
        private static float Pct(List<float> v, float p)
        {
            if (v.Count == 0) return 0f;
            var s = new List<float>(v); s.Sort();
            return s[Mathf.Clamp(Mathf.CeilToInt(p * s.Count) - 1, 0, s.Count - 1)];
        }
    }
}
#endif
