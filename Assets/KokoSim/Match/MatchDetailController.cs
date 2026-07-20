using System.Collections.Generic;
using KokoSim.Engine.Match.Timeline.Playback;
using UnityEngine;
using UnityEngine.UIElements;

namespace KokoSim.Unity.Match
{
    /// <summary>
    /// 試合詳細（2D俯瞰再生）のコントローラ（設計書06 §3.4 守備俯瞰・独立画面）。
    /// docs/design/mock-match-2d-view.html の再生ループ・実況キャプション・リザルトバッジを移植。
    /// 再生データは engine 単一ソース PlaybackSamples.All（モック PLAYS の忠実移植）。
    /// このスライスはエンジン実出力ではなく7プレーのサンプルを再生する「1プレー再生ハーネス」。
    /// </summary>
    [RequireComponent(typeof(UIDocument))]
    public sealed class MatchDetailController : MonoBehaviour
    {
        private VisualElement _root;
        private Match2DPlaybackElement _view;
        private Label _caption;
        private Label _result;
        private readonly List<Button> _playButtons = new();
        private readonly List<Button> _speedButtons = new();

        private IReadOnlyList<PlaybackPlay> _plays;
        private PlaybackPlay _play;
        private double _t;
        private float _speed = 1f;
        private bool _paused;   // スクショ用シーク中は自動再生を止める

        private void OnEnable()
        {
            _root = GetComponent<UIDocument>().rootVisualElement;
            _plays = PlaybackSamples.All;

            // 球場再生要素をステージへ差し込む（mock: canvas）。
            var host = _root.Q<VisualElement>("field-host");
            _view = new Match2DPlaybackElement();
            host?.Add(_view);

            _caption = _root.Q<Label>("caption");
            _result = _root.Q<Label>("result-chip");

            BuildPlayList();
            WireSpeedControls();

            var replay = _root.Q<Button>("replay");
            if (replay != null) replay.clicked += () => Start(_play);

            Start(_plays[0]);
        }

        // プレー選択ボタン（mock: PLAYS ボタン群）。選択でハイライト＋再生開始。
        private void BuildPlayList()
        {
            var list = _root.Q<VisualElement>("play-list");
            if (list == null) return;
            list.Clear();
            _playButtons.Clear();

            for (var i = 0; i < _plays.Count; i++)
            {
                var play = _plays[i];
                var b = new Button { text = play.Name };
                b.AddToClassList("chip-btn");
                b.clicked += () => Start(play);
                list.Add(b);
                _playButtons.Add(b);
            }
        }

        private void WireSpeedControls()
        {
            _speedButtons.Clear();
            WireSpeed("spd-05", 0.5f);
            WireSpeed("spd-1", 1f);
            WireSpeed("spd-2", 2f);
        }

        private void WireSpeed(string name, float speed)
        {
            var b = _root.Q<Button>(name);
            if (b == null) return;
            _speedButtons.Add(b);
            b.clicked += () =>
            {
                _speed = speed;
                foreach (var sb in _speedButtons) sb.EnableInClassList("chip-btn--on", sb == b);
            };
        }

        // mock: start(p)。t=0 から再生し直す。
        private void Start(PlaybackPlay p)
        {
            _play = p;
            _t = 0;
            _view.SetPlay(p);
            foreach (var b in _playButtons) b.EnableInClassList("chip-btn--on", b.text == p.Name);
            RenderFrame();
        }

        // mock: frame(ts)。t<=dur+0.6 の間だけ speed 倍で進める（以降は最終フレームを保持）。
        private void Update()
        {
            if (_play == null || _paused) return;
            if (_t <= _play.Dur + 0.6)
            {
                _t += Time.deltaTime * _speed;
                RenderFrame();
            }
        }

        /// <summary>
        /// スクショ用の決定論シーク。自動再生を止め、t=0 から dt 刻みで軌跡を積み直して
        /// 指定時刻 t の静止フレームを作る（mock はフレームごとに軌跡を push するため、
        /// 単に SetTime(t) すると彗星の尾が出ない。ここで再生到達と同じ軌跡を再現する）。
        /// UnityMCP execute_code から: controller.CaptureSeek(playIndex, t)。
        /// </summary>
        public void CaptureSeek(int playIndex, double t)
        {
            _paused = true;
            _play = _plays[playIndex];
            _view.SetPlay(_play);   // 軌跡クリア＋t=0
            for (var s = 0.0; s < t; s += 0.03) _view.SetTime(s);
            _t = t;
            RenderFrame();
            foreach (var b in _playButtons) b.EnableInClassList("chip-btn--on", b.text == _play.Name);
        }

        // mock: draw()。ボール/野手/走者は要素側、実況とリザルトはここ。
        private void RenderFrame()
        {
            _view.SetTime(_t);

            if (_caption != null) _caption.text = PlaybackEvaluator.CaptionAt(_play, _t);
            if (_result != null)
            {
                _result.text = _play.Result;
                _result.style.display = _t >= _play.ResAt ? DisplayStyle.Flex : DisplayStyle.None;
            }
        }
    }
}
