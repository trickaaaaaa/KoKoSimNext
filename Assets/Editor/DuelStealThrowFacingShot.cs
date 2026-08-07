#if UNITY_EDITOR
using System.Collections.Generic;
using System.IO;
using System.Text;
using KokoSim.Engine.Match.Field;
using KokoSim.Engine.Match.Game;
using KokoSim.Engine.Match.Timeline;
using KokoSim.Engine.Match.Timeline.Playback;
using KokoSim.Engine.Players;
using KokoSim.Unity.Match;
using UnityEngine;

namespace KokoSim.UnityEditorTools
{
    /// <summary>
    /// 【開発用・エディタ限定】盗塁・牽制の送球で捕手／投手が的（二塁・一塁等）を向いて
    /// 投げ始めているかの確認撮影（issue #511）。<see cref="DuelThrowFacingShot"/> と同じ物差し
    /// （<c>DuelStageRig.FieldThrowDiagForCapture</c> の実測）だが、盗塁・牽制は
    /// <see cref="Playback.PlaybackSamples"/> に収録が無い（打球が出ないプレー）ので、
    /// <see cref="TimelineBuilder.BuildSteal"/>/<see cref="TimelineBuilder.BuildPickoff"/> で直接組む。
    /// </summary>
    public static class DuelStealThrowFacingShot
    {
        private const string Dir = "out/duel-stage/throwfacing";

        private const int W = 560;
        private const int H = 560;
        private const int Cols = 4;
        private const int Frames = 8;

        // DuelThrowFacingShot と同じ理由（頭出し窓がリリース位置より長くないと切り替わりが写らない）。
        private const float LeadSeconds = 0.9f;
        private const float TailSeconds = 0.25f;
        private const float FrameHeightM = 3.2f;

        [UnityEditor.MenuItem("KokoSim/Duel/#511 盗塁送球の体の向きを撮る")]
        public static void Run()
        {
            Directory.CreateDirectory(Dir);
            var log = new StringBuilder();

            foreach (var (label, play) in BuildScenarios())
            {
                var plan = ThrowsOf(play);
                if (plan.Count == 0)
                {
                    log.AppendLine("# " + label + "\t(送球区間なし＝スキップ)");
                    continue;
                }

                var rig = new DuelStageRig();
                try
                {
                    if (!rig.Available)
                    {
                        Debug.LogError("[#511Shot] DuelStageRig が組めない（素体/クリップ未導入）");
                        return;
                    }
                    rig.EnsureSize(W, H);
                    // ⚠ 実ゲームは対決ビューが送球の頭（＝リリース）を見せ終えてから守備ビューへ切る
                    // （MatchLiveController.FieldCutMomentOf と同じ規約。issue #511）。t0=0 から
                    // 通しで再生すると回頭の時間が十分あって誤差が隠れる＝実際のカット点で撮る。
                    float cut = plan[0].TRelease;
                    rig.BeginFieldPlay(play, cut);

                    var shots = new List<KeyValuePair<float, int>>();
                    for (int k = 0; k < plan.Count; k++)
                    {
                        // カット以前は対決ビュー側の芝居＝守備ビューには存在しないので下限を張る
                        float t0 = Mathf.Max(cut, plan[k].TRelease - LeadSeconds);
                        float t1 = plan[k].TRelease + TailSeconds;
                        for (int f = 0; f < Frames; f++)
                            shots.Add(new KeyValuePair<float, int>(t0 + (t1 - t0) * f / (Frames - 1), k));
                    }

                    var sheets = new Dictionary<int, List<Texture2D>>();
                    int next = 0;
                    float poseStep = 1f / DuelStageRig.PoseFps;
                    for (float t = cut; next < shots.Count && t <= play.Dur + 0.5; t += poseStep)
                    {
                        if (t > cut) rig.Tick(poseStep);
                        rig.SetFieldTime(t);
                        while (next < shots.Count && t + 1e-4f >= shots[next].Key)
                        {
                            int k = shots[next].Value;
                            AimAtThrower(rig, play, plan[k], t);
                            rig.RenderNow();
                            if (!sheets.ContainsKey(k)) sheets[k] = new List<Texture2D>();
                            sheets[k].Add(Grab(rig.Target));
                            next++;
                        }
                    }
                    rig.ClearPreviewFrame();

                    foreach (var kv in sheets)
                    {
                        WriteSheet(kv.Value, label + "-" + kv.Key + "-"
                            + plan[kv.Key].From + "to" + plan[kv.Key].To);
                        foreach (var f in kv.Value) Object.DestroyImmediate(f);
                    }

                    log.AppendLine("# " + label);
                    log.Append(rig.FieldThrowDiagForCapture());
                }
                finally
                {
                    rig.Dispose();
                }
            }

            File.WriteAllText(Path.Combine(Dir, "steal-throws.tsv"), log.ToString());
            Debug.Log("[#511Shot] DONE -> " + Dir + "\n" + log);
        }

        private static IEnumerable<(string, PlaybackPlay)> BuildScenarios()
        {
            var c = new BaserunningCoefficients();
            var field = new FieldGeometry();
            var runner = new Player { Speed = 80, Steal = 75 };
            var catcher = new Player { Position = FieldPosition.Catcher, ArmStrength = 60 };
            var pitcher = new Player { Position = FieldPosition.Pitcher, ArmStrength = 55 };

            yield return ("steal2nd-safe", PlayTimelineAdapter.ToPlaybackPlay(
                TimelineBuilder.BuildSteal(runner, catcher, StealResult.Safe, c, field)));
            yield return ("steal2nd-out", PlayTimelineAdapter.ToPlaybackPlay(
                TimelineBuilder.BuildSteal(runner, catcher, StealResult.CaughtStealing, c, field)));
            yield return ("pickoff1st-out", PlayTimelineAdapter.ToPlaybackPlay(
                TimelineBuilder.BuildPickoff(runner, pitcher, "first", true, c, field)));
        }

        private static void AimAtThrower(DuelStageRig rig, PlaybackPlay play, ThrowPlan p, float t)
        {
            var fp = PlaybackEvaluator.FielderAt(play, p.From, Mathf.Min(t, p.TRelease)).Pos;
            var here = new Vector3((float)fp.X, 0f, (float)fp.Y);
            var dir = new Vector3(p.Target.x - here.x, 0f, p.Target.z - here.z);
            if (dir.sqrMagnitude < 1e-6f) dir = Vector3.forward;
            dir.Normalize();
            rig.FramePreview(here - dir * 4.2f + Vector3.up * 2.4f,
                here + Vector3.up * 1.0f, FrameHeightM);
        }

        private struct ThrowPlan
        {
            public FieldPosition From, To;
            public float TRelease;
            public Vector3 Target;
        }

        private static List<ThrowPlan> ThrowsOf(PlaybackPlay play)
        {
            var list = new List<ThrowPlan>();
            if (play?.Ball == null) return list;
            foreach (var seg in play.Ball)
            {
                var eb = seg as EndpointBallSegment;
                bool isThrow = eb != null ? eb.Kind == BallSegmentKind.Throw : seg is ThrowSegment;
                if (!isThrow) continue;
                var from = seg.At(0.0);
                var to = seg.At(seg.T1 - seg.T0);
                list.Add(new ThrowPlan
                {
                    From = seg.FromRole ?? Nearest(play, (float)seg.T0, from.X, from.Y),
                    To = seg.ToRole ?? Nearest(play, (float)seg.T1, to.X, to.Y),
                    TRelease = (float)seg.T0,
                    Target = new Vector3((float)to.X, (float)to.H, (float)to.Y),
                });
            }
            return list;
        }

        private static FieldPosition Nearest(PlaybackPlay play, float t, double x, double y)
        {
            var all = new[]
            {
                FieldPosition.Pitcher, FieldPosition.Catcher, FieldPosition.FirstBase,
                FieldPosition.SecondBase, FieldPosition.ThirdBase, FieldPosition.Shortstop,
                FieldPosition.LeftField, FieldPosition.CenterField, FieldPosition.RightField,
            };
            var best = FieldPosition.Pitcher;
            float bestD = float.MaxValue;
            foreach (var pos in all)
            {
                var p = PlaybackEvaluator.FielderAt(play, pos, t).Pos;
                float d = new Vector2((float)(p.X - x), (float)(p.Y - y)).magnitude;
                if (d < bestD) { bestD = d; best = pos; }
            }
            return best;
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

        private static void WriteSheet(List<Texture2D> frames, string name)
        {
            if (frames.Count == 0) return;
            int cols = Mathf.Min(Cols, frames.Count);
            int rows = Mathf.CeilToInt(frames.Count / (float)cols);
            int fw = frames[0].width, fh = frames[0].height;
            var sheet = new Texture2D(cols * fw, rows * fh, TextureFormat.RGBA32, false);
            try
            {
                for (int i = 0; i < frames.Count; i++)
                    sheet.SetPixels32((i % cols) * fw, (rows - 1 - i / cols) * fh, fw, fh,
                        frames[i].GetPixels32());
                sheet.Apply();
                File.WriteAllBytes(Path.Combine(Dir, name + ".png"), sheet.EncodeToPNG());
                Debug.Log("[#511Shot] wrote " + name + " (" + frames.Count + " コマ)");
            }
            finally
            {
                Object.DestroyImmediate(sheet);
            }
        }
    }
}
#endif
