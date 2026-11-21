using UnityEngine;
using UnityEngine.UIElements;

namespace KokoSim.Unity.Match.Telops
{
    /// <summary>
    /// ホームラン（issue #1179 §3）。どちらのチームが打っても同じ演出・約3.2秒。
    /// 判定11種（Calls）とは別の専用アニメ＝<see cref="CallTelopView"/>は使わない。
    ///
    /// <para>ホームランは1試合に数回なので、再生速度では縮めない（issue本文「判定のように縮めない」）。
    /// 自分でクロックを持ち、<see cref="Tick"/>に渡された実時間(deltaTime)だけ進めて3.2秒で自動的に閉じる＝
    /// 呼び出し側（Match2DPlaybackElement）はPlay()を1回呼ぶだけでよい。</para>
    /// </summary>
    public sealed class HomeRunTelopView
    {
        private const double TotalSeconds = 3.2;
        private const double InSeconds = 0.36;
        private const double HoldUntil = 2.80;
        private const double FrameStart = 0.43;
        private const double FrameEnd = 1.15;
        private const int FrameCount = 16;

        private readonly VisualElement _root;
        private readonly VisualElement _dim;
        private readonly VisualElement _group;
        private readonly VisualElement _gold;
        private readonly Label _distance;

        private double _elapsed = -1.0; // -1 = 非再生中
        private int _lastFrame = -1;

        public HomeRunTelopView(VisualElement root)
        {
            _root = root;
            _dim = root.Q<VisualElement>("hr-dim");
            _group = root.Q<VisualElement>("hr-group");
            _gold = root.Q<VisualElement>("hr-gold");
            _distance = root.Q<Label>("hr-distance");
        }

        public bool IsPlaying => _elapsed >= 0.0;

        /// <summary>再生開始（issue本文「飛距離は画像の下に、今の文字のまま残す」）。</summary>
        public void Play(double distanceM)
        {
            _elapsed = 0.0;
            ClearGoldFrame();
            _distance.text = "推定飛距離 " + Mathf.RoundToInt((float)distanceM) + "m";
            _root.style.display = DisplayStyle.Flex;
            SetGoldFrame(0);
            Apply(0.0);
        }

        /// <summary>毎フレーム呼ぶ（実時間のdeltaTime。再生速度・高速試合モードでは呼ばない＝出さない）。</summary>
        public void Tick(double deltaTimeSeconds)
        {
            if (!IsPlaying) return;
            _elapsed += deltaTimeSeconds;
            if (_elapsed >= TotalSeconds) { Hide(); return; }
            Apply(_elapsed);
        }

        public void Hide()
        {
            _elapsed = -1.0;
            ClearGoldFrame();
            _root.style.display = DisplayStyle.None;
        }

        // 同じ詳細度で最後に定義された 15 が常に勝つので、コマのクラスは使い終わりに必ず外す（2本目以降が最終コマで止まる）
        private void ClearGoldFrame()
        {
            if (_lastFrame >= 0) _gold.RemoveFromClassList("tl-hr-gold-" + _lastFrame.ToString("00"));
            _lastFrame = -1;
        }

        private void Apply(double t)
        {
            double scale, opacity;
            if (t < InSeconds)
            {
                var eased = TelopEasing.OutBack(t / InSeconds);
                scale = 0.55 + (1.0 - 0.55) * eased;
                opacity = System.Math.Clamp(eased, 0.0, 1.0);
            }
            else if (t < HoldUntil)
            {
                scale = 1.0;
                opacity = 1.0;
            }
            else
            {
                var eased = TelopEasing.InQuad((t - HoldUntil) / (TotalSeconds - HoldUntil));
                scale = 1.0 + (1.04 - 1.0) * eased;
                opacity = System.Math.Clamp(1.0 - eased, 0.0, 1.0);
            }
            _group.style.scale = new Scale(new Vector2((float)scale, (float)scale));
            _group.style.opacity = (float)opacity;
            _dim.style.opacity = (float)opacity;

            if (t >= FrameStart)
            {
                var frameDur = (FrameEnd - FrameStart) / FrameCount;
                var frame = frameDur > 0.0
                    ? (int)System.Math.Clamp((t - FrameStart) / frameDur, 0, FrameCount - 1)
                    : FrameCount - 1;
                SetGoldFrame(frame);
            }
        }

        private void SetGoldFrame(int frame)
        {
            if (frame == _lastFrame) return;
            if (_lastFrame >= 0) _gold.RemoveFromClassList("tl-hr-gold-" + _lastFrame.ToString("00"));
            _gold.AddToClassList("tl-hr-gold-" + frame.ToString("00"));
            _lastFrame = frame;
        }
    }
}
