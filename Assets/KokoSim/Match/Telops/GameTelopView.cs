using UnityEngine;
using UnityEngine.UIElements;

namespace KokoSim.Unity.Match.Telops
{
    /// <summary>
    /// プレイボール／ゲームセット（issue #1179 §1・§4）。作り・重ね順・位置・タイムラインは共通で、
    /// 素材と色だけ差し替える（<see cref="Play"/> の variant切替）。合計約4.0秒、右から流れてきて
    /// 少し中央で止まり、左へ抜けていく。寿命の正典は呼び出し側（MatchLiveControllerの
    /// gameTelopHoldSeconds）のまま＝ここは渡された経過秒(elapsed)を動きへ変換するだけ。
    /// </summary>
    public sealed class GameTelopView
    {
        private static readonly Color GameSetTint = new Color32(0x24, 0x32, 0x4a, 0xff);

        private readonly VisualElement _dim;
        private readonly VisualElement _linesHost;
        private readonly VisualElement[] _lines;
        private readonly VisualElement _ghost;
        private readonly VisualElement _group;
        private readonly VisualElement _shadow;
        private readonly VisualElement _extrude;
        private readonly VisualElement _face;
        private readonly VisualElement _sub;
        private readonly VisualElement _barLeft;
        private readonly VisualElement _barRight;

        public GameTelopView(VisualElement root)
        {
            _dim = root.Q<VisualElement>("gt-dim");
            _linesHost = root.Q<VisualElement>("gt-lines");
            _lines = new[]
            {
                root.Q<VisualElement>("gt-line-1"), root.Q<VisualElement>("gt-line-2"),
                root.Q<VisualElement>("gt-line-3"), root.Q<VisualElement>("gt-line-4"),
                root.Q<VisualElement>("gt-line-5"),
            };
            _ghost = root.Q<VisualElement>("gt-ghost");
            _group = root.Q<VisualElement>("gt-group");
            _shadow = root.Q<VisualElement>("gt-shadow");
            _extrude = root.Q<VisualElement>("gt-extrude");
            _face = root.Q<VisualElement>("gt-face");
            _sub = root.Q<VisualElement>("gt-sub");
            _barLeft = root.Q<VisualElement>("gt-bar-left");
            _barRight = root.Q<VisualElement>("gt-bar-right");
        }

        /// <summary>この回の素材・色を決める（プレイボール＝スクールカラー／ゲームセット＝濃紺固定）。
        /// 呼び出し側（MatchLiveController）が器の表示/非表示とクロックをまだ持つ＝ここでは変えない。</summary>
        public void Play(bool isGameSet, Color accent)
        {
            SetVariant(_shadow, isGameSet, "tl-pb-shadow", "tl-gs-shadow");
            SetVariant(_extrude, isGameSet, "tl-pb-extrude", "tl-gs-extrude");
            SetVariant(_face, isGameSet, "tl-pb-face", "tl-gs-face");
            SetVariant(_sub, isGameSet, "tl-pb-sub", "tl-gs-sub");
            SetVariant(_ghost, isGameSet, "tl-pb-ghost", "tl-gs-ghost");
            SetVariant(_barLeft, isGameSet, "tl-pb-bar tl-pb-bar-left", "tl-gs-bar tl-gs-bar-left");
            SetVariant(_barRight, isGameSet, "tl-pb-bar tl-pb-bar-right", "tl-gs-bar tl-gs-bar-right");

            // 厚みの型（extrude）の着色: プレイボール＝スクールカラー／ゲームセット＝濃紺固定
            // （issue #1179 §4「勝っても負けても同じトーン」）。
            var tint = isGameSet ? GameSetTint : accent;
            _extrude.style.unityBackgroundImageTintColor = new StyleColor(tint);

            // スピード線3本目とプレイボールの横棒だけが差し色（issue §1・§4）。ゲームセットの横棒は
            // 白固定（USSの tl-gs-bar が既に塗る）＝ここでは触らない。
            _lines[2].style.backgroundColor = new StyleColor(tint);
            if (!isGameSet)
            {
                _barLeft.style.backgroundColor = new StyleColor(accent);
                _barRight.style.backgroundColor = new StyleColor(accent);
            }

            Tick(0.0);
        }

        /// <summary>毎フレーム呼ぶ。elapsedは再生速度で縮めた後の秒数（呼び出し側のgameTelopClock）。</summary>
        public void Tick(double elapsed)
        {
            // 1) 暗幕: 0.00→0.60 フェードイン、3.40まで保つ、3.40→4.00 フェードアウト。
            double dimOpacity;
            if (elapsed < 0.60) dimOpacity = TelopEasing.Segment(elapsed, 0.0, 0.60, 0.0, 1.0, TelopEasing.EaseInOut);
            else if (elapsed < 3.40) dimOpacity = 1.0;
            else dimOpacity = TelopEasing.Segment(elapsed, 3.40, 4.00, 1.0, 0.0, TelopEasing.EaseInOut);
            _dim.style.opacity = (float)dimOpacity;

            // 2) スピード線: 1回目0.00-0.55（最大不透明度1）、2回目3.15-3.85（最大0.9）。
            //    どちらも「まとめてxを+1700から-1700へ」・不透明度は最初0.1秒で立ち上がり終わりで戻す。
            if (elapsed >= 0.0 && elapsed <= 0.55) ApplyLinesBurst(elapsed, 0.0, 0.55, 1.0);
            else if (elapsed >= 3.15 && elapsed <= 3.85) ApplyLinesBurst(elapsed, 3.15, 3.85, 0.9);
            else _linesHost.style.opacity = 0f;

            // 3) 巨大な英字(ghost): 0.00→0.75 OutExpo(x:+900→+70, op:0→1)。0.75→3.25 等速(x:+70→-70)。
            //    3.25→4.00 InExpo(x:-70→-900, op:1→0)。
            double ghostX, ghostOp;
            if (elapsed < 0.75)
            {
                ghostX = TelopEasing.Segment(elapsed, 0.0, 0.75, 900.0, 70.0, TelopEasing.OutExpo);
                ghostOp = TelopEasing.Segment(elapsed, 0.0, 0.75, 0.0, 1.0, TelopEasing.OutExpo);
            }
            else if (elapsed < 3.25)
            {
                ghostX = TelopEasing.Segment(elapsed, 0.75, 3.25, 70.0, -70.0, TelopEasing.Linear);
                ghostOp = 1.0;
            }
            else
            {
                ghostX = TelopEasing.Segment(elapsed, 3.25, 4.00, -70.0, -900.0, TelopEasing.InExpo);
                ghostOp = TelopEasing.Segment(elapsed, 3.25, 4.00, 1.0, 0.0, TelopEasing.InExpo);
            }
            _ghost.style.translate = new Translate(new Length((float)ghostX), 0, 0);
            _ghost.style.opacity = (float)ghostOp;

            // 4) 文字のまとまり（shadow/extrude/face/sub/バー、issue表の4〜7）: 1つのwrapperに translateX。
            //    0.00→0.65 OutExpo(+1800→0)。0.65→3.20 等速(0→-32)。3.20→3.90 InExpo(-32→-1900)。
            double groupX;
            if (elapsed < 0.65) groupX = TelopEasing.Segment(elapsed, 0.0, 0.65, 1800.0, 0.0, TelopEasing.OutExpo);
            else if (elapsed < 3.20) groupX = TelopEasing.Segment(elapsed, 0.65, 3.20, 0.0, -32.0, TelopEasing.Linear);
            else groupX = TelopEasing.Segment(elapsed, 3.20, 3.90, -32.0, -1900.0, TelopEasing.InExpo);
            _group.style.translate = new Translate(new Length((float)groupX), 0, 0);
        }

        private void ApplyLinesBurst(double elapsed, double burstStart, double burstEnd, double maxOpacity)
        {
            var dur = burstEnd - burstStart;
            var local = elapsed - burstStart;
            var x = TelopEasing.Segment(local, 0.0, dur, 1700.0, -1700.0, TelopEasing.Linear);
            double op;
            if (local < 0.1) op = TelopEasing.Segment(local, 0.0, 0.1, 0.0, maxOpacity, TelopEasing.Linear);
            else if (local > dur - 0.1) op = TelopEasing.Segment(local, dur - 0.1, dur, maxOpacity, 0.0, TelopEasing.Linear);
            else op = maxOpacity;
            _linesHost.style.translate = new Translate(new Length((float)x), 0, 0);
            _linesHost.style.opacity = (float)op;
        }

        private static void SetVariant(VisualElement e, bool isGameSet, string pbClasses, string gsClasses)
        {
            foreach (var c in pbClasses.Split(' ')) e.RemoveFromClassList(c);
            foreach (var c in gsClasses.Split(' ')) e.RemoveFromClassList(c);
            foreach (var c in (isGameSet ? gsClasses : pbClasses).Split(' ')) e.AddToClassList(c);
        }
    }
}
