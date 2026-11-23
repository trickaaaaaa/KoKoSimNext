#if UNITY_EDITOR
using System.Collections.Generic;
using KokoSim.Engine.Players;
using KokoSim.Unity.Components;
using UnityEngine;
using UnityEngine.UIElements;

namespace KokoSim.Unity.Debugging
{
    /// <summary>
    /// 球種チャート（<see cref="PitchChartView"/>）の見本帳。撮影レシピから
    /// <c>call: PitchChartGallery.Show("PlayerList")</c> で、表示中の画面の上に大きさ・持ち球・投げ手の違う
    /// チャートを並べる（選手画面の 480×310／比較カード 154×104／試合中の偵察・スタメン比較 230×150）。
    /// 実際の部員は持ち球も投げ手も選べないので、崩れやすい組み合わせはここで固定して見る。
    /// </summary>
    public static class PitchChartGallery
    {
        public static void Show(string screen)
        {
            var go = GameObject.Find(screen);
            var doc = go != null ? go.GetComponent<UIDocument>() : null;
            if (doc == null) { Debug.LogError("[PitchChartGallery] screen not found: " + screen); return; }
            var root = doc.rootVisualElement;
            root.Q<VisualElement>("pchart-gallery")?.RemoveFromHierarchy();

            var g = new VisualElement { name = "pchart-gallery" };
            g.AddToClassList("ui-root");
            g.style.position = Position.Absolute;
            g.style.left = 0; g.style.top = 0; g.style.right = 0; g.style.bottom = 0;
            g.style.backgroundColor = (Color)new Color32(0xe7, 0xe2, 0xd7, 0xff);
            g.style.paddingLeft = 24; g.style.paddingTop = 24;
            root.Add(g);

            var sets = new List<(string, List<PitchChartDatum>)>
            {
                ("3球種・右", Three(Handedness.Right)),
                ("6球種・右（モック）", Six(Handedness.Right)),
                ("9球種・左・カットS", All(Handedness.Left)),
                ("1球種", new List<PitchChartDatum> { D(PitchType.Fastball, "G", Handedness.Right, "121") }),
            };

            var full = Row(g);
            foreach (var (label, data) in sets) Card(full, label, 480, 310, data, false);
            var mid = Row(g);
            foreach (var (label, data) in sets) Card(mid, label, 230, 150, data, true);
            var small = Row(g);
            foreach (var (label, data) in sets) Card(small, label, 154, 104, data, true);
            g.BringToFront();
        }

        private static VisualElement Row(VisualElement g)
        {
            var r = new VisualElement();
            r.style.flexDirection = FlexDirection.Row;
            r.style.flexWrap = Wrap.Wrap;
            r.style.marginBottom = 12;
            g.Add(r);
            return r;
        }

        private static void Card(VisualElement row, string label, float w, float h, List<PitchChartDatum> data, bool compact)
        {
            var card = new VisualElement();
            card.style.backgroundColor = (Color)new Color32(0xfb, 0xfa, 0xf6, 0xff);
            card.style.marginRight = 12; card.style.marginBottom = 4;
            card.style.paddingLeft = 8; card.style.paddingRight = 8; card.style.paddingTop = 4; card.style.paddingBottom = 8;
            var t = new Label(label);
            t.style.fontSize = 11;
            t.style.color = (Color)new Color32(0x6f, 0x6a, 0x60, 0xff);
            card.Add(t);
            var host = new VisualElement();
            host.style.width = w; host.style.height = h;
            card.Add(host);
            row.Add(card);
            new PitchChartView(host, compact).SetData(data);
        }

        private static List<PitchChartDatum> Three(Handedness th) => new List<PitchChartDatum>
        {
            D(PitchType.Fastball, "B", th, "144"), D(PitchType.Slider, "B", th), D(PitchType.Shuuto, "B", th),
        };

        private static List<PitchChartDatum> Six(Handedness th) => new List<PitchChartDatum>
        {
            D(PitchType.Fastball, "A", th, "148"), D(PitchType.Cutter, "B", th), D(PitchType.Slider, "S", th),
            D(PitchType.Curve, "C", th), D(PitchType.Fork, "B", th), D(PitchType.Sinker, "D", th),
        };

        private static List<PitchChartDatum> All(Handedness th) => new List<PitchChartDatum>
        {
            D(PitchType.Fastball, "S", th, "156"), D(PitchType.TwoSeam, "E", th), D(PitchType.Shuuto, "S", th),
            D(PitchType.Sinker, "C", th), D(PitchType.Changeup, "A", th), D(PitchType.Fork, "B", th),
            D(PitchType.Curve, "F", th), D(PitchType.Slider, "D", th), D(PitchType.Cutter, "S", th),
        };

        private static PitchChartDatum D(PitchType t, string rank, Handedness th, string velo = null) => new PitchChartDatum
        {
            Type = t,
            Name = KokoSim.Unity.Shell.PitchTypeLabels.Jp(t),
            ShortName = KokoSim.Unity.Shell.PitchTypeLabels.Abbr(t),
            Kire = rank,
            AngleDeg = PitchTypeStyle.AngleFor(t, th),
            IsFastball = t == PitchType.Fastball,
            VeloText = velo,
        };
    }
}
#endif
