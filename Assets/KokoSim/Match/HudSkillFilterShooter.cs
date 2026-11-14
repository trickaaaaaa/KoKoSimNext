#if UNITY_EDITOR
using System.Collections;
using System.Collections.Generic;
using System.IO;
using KokoSim.Engine.Players;
using KokoSim.Unity.Shell;
using UnityEngine;
using UnityEngine.UIElements;

namespace KokoSim.Unity.Match
{
    /// <summary>
    /// 【開発用・エディタ限定】試合 HUD の特殊能力チップを場面で絞る（issue #781）の確認撮影。
    /// 自校の部員全員に打撃（目慣れ・粘り・チャンス◎）＋投手（尻上がり・クセ球・クイック◎）＋チームの能力を見えている状態で
    /// 持たせ、守備中（投手帯＝投手だけ）と攻撃中（打者帯＝打撃だけ）を撮る。チップの文字はログにも出す。
    /// </summary>
    public sealed class HudSkillFilterShooter : MonoBehaviour
    {
        private const int W = 1600;
        private const int H = 900;
        private const string Dir = "screenshots";

        private static readonly Skill[] Given =
        {
            Skill.SlowStarterBat, Skill.Grinder, Skill.SlowStarterPitch, Skill.DeceptiveBall,
            Skill.ClutchHitter, Skill.QuickDelivery, Skill.Moodmaker,
        };

        private IEnumerator Start()
        {
            Application.runInBackground = true;
            MatchLiveController.CaptureMode = true;

            var saved = new Dictionary<int, SkillSet>();
            foreach (var p in RosterService.Roster)
            {
                saved[p.Id] = p.Skills;
                p.Skills = new SkillSet(visible: Given);
            }

            EnsureOnlyMatchLive();
            yield return null; yield return null;
            EnsureOnlyMatchLive();
            var ctrl = FindController();
            if (ctrl == null) { Debug.LogError("[HudSkillShot] MatchLive/Controller が見つからない"); yield break; }
            for (int i = 0; i < 6; i++) yield return null;
            Directory.CreateDirectory(Dir);
            // デモ試合は校IDを持たない（issue #634）＝自校扱いにならずチップが出ない。撮影の間だけ
            // 自校（後攻）の校IDを成績参照の自校IDへ揃える（本番の試合は大会側がこの値を渡している）
            var flags = System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance;
            typeof(MatchLiveController).GetField("_homeSchoolId", flags).SetValue(ctrl, NationService.ManagerSchoolId);

            // 1回表＝相手の攻撃（自校が守備＝投手帯に自校投手）
            ctrl.AdvanceForCapture();
            for (int i = 0; i < 12; i++) yield return null;
            ctrl.FreezeLiveForCapture(true);
            for (int i = 0; i < 4; i++) yield return null;
            yield return Capture(ctrl, "i781-hud-defense");
            ctrl.FreezeLiveForCapture(false);

            // 1回裏（自校の攻撃＝打者帯に自校打者）まで進める
            for (int n = 0; n < 40 && (ctrl.CurrentAtBatForCapture?.IsTop ?? true); n++)
                if (!ctrl.AdvanceForCapture()) break;
            ctrl.AdvanceForCapture();
            for (int i = 0; i < 12; i++) yield return null;
            ctrl.FreezeLiveForCapture(true);
            for (int i = 0; i < 4; i++) yield return null;
            yield return Capture(ctrl, "i781-hud-offense");
            ctrl.FreezeLiveForCapture(false);

            foreach (var p in RosterService.Roster)
                if (saved.TryGetValue(p.Id, out var s)) p.Skills = s;
            Debug.Log("[HudSkillShot] DONE");
        }

        private IEnumerator Capture(MatchLiveController ctrl, string file)
        {
            EnsureOnlyMatchLive();
            yield return null;
            yield return new WaitForEndOfFrame();
            var root = ctrl.GetComponent<UIDocument>().rootVisualElement;
            var rows = new List<string>();
            root.Query<VisualElement>(className: "mhud-skills").ForEach(row =>
            {
                var names = new List<string>();
                row.Query<Label>(className: "skill-chip").ForEach(l => names.Add(l.text + (string.IsNullOrEmpty(l.tooltip) ? "" : "（" + l.tooltip + "）")));
                rows.Add(string.Join(" / ", names));
            });
            Debug.Log("[HudSkillShot] " + file + " 表=" + (ctrl.CurrentAtBatForCapture?.IsTop.ToString() ?? "?")
                      + " チップ段=" + rows.Count + " :: " + string.Join(" || ", rows));

            ctrl.DuelRigForCapture?.RenderNow();
            yield return null;
            var ps = ctrl.GetComponent<UIDocument>().panelSettings;
            var prevRt = ps.targetTexture; var prevScale = ps.scaleMode; var prevRef = ps.referenceResolution;
            var rt = new RenderTexture(W, H, 24, RenderTextureFormat.ARGB32); rt.Create();
            ps.scaleMode = PanelScaleMode.ScaleWithScreenSize;
            ps.referenceResolution = new Vector2Int(W, H);
            ps.targetTexture = rt;
            yield return null; yield return new WaitForEndOfFrame();
            yield return null; yield return new WaitForEndOfFrame();
            Texture2D tex = null;
            try
            {
                tex = new Texture2D(W, H, TextureFormat.RGBA32, false);
                var prevActive = RenderTexture.active;
                RenderTexture.active = rt;
                tex.ReadPixels(new Rect(0, 0, W, H), 0, 0);
                tex.Apply();
                RenderTexture.active = prevActive;
                File.WriteAllBytes(Path.Combine(Dir, file + ".png"), tex.EncodeToPNG());
            }
            finally
            {
                ps.targetTexture = prevRt; ps.scaleMode = prevScale; ps.referenceResolution = prevRef;
                rt.Release(); Destroy(rt);
                if (tex != null) Destroy(tex);
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
