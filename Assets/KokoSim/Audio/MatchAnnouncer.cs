using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UIElements;

namespace KokoSim.Unity.Audio
{
    /// <summary>
    /// 場内放送（ウグイス嬢・issue #919）。打席に入る打者を「4番、キャッチャー、田中くん。」と紹介する。
    /// 表示専用＝判定・帯には一切関与しない。何を言うか（部品の並び）は engine の
    /// <see cref="KokoSim.Engine.Match.Broadcast.PaAnnouncement"/> が決め、ここは部品をつないで鳴らすだけ。
    ///
    /// 声は VOICEVOX:No.7（アナウンス）で生成したもの（tools/voicevox-announcements.py で作り直せる）。
    /// Git 管理外（.gitignore の Assets/KokoSim/Audio/Vendor/）に置き、Resources の <c>Voice/No7/Announce/</c> から読む。
    /// <b>部品が1つでも無ければ黙って鳴らさない</b>（購入素体・効果音と同じ流儀＝ゲームは動く）。
    ///
    /// 部品は <see cref="AudioSource.PlayScheduled"/> で dspTime に合わせて隙間なくつなぐ（フレームの粒に左右されない）。
    /// 鳴っている間は応援（<see cref="MatchBrassBand"/>）を絞る。
    /// オン/オフはセーブでなくアプリ設定（PlayerPrefs）＝次の試合・別のセーブでも同じ。オフにしたらその場で止める。
    /// </summary>
    public static class MatchAnnouncer
    {
        private const string Dir = "Voice/No7/Announce/";
        private const string PrefKey = "KokoSim.Settings.Announcer";

        private static AnnouncerHost _host;
        private static readonly Dictionary<string, AudioClip> _clips = new();

        /// <summary>オン/オフが変わった（切り替えボタンの点灯を合わせる）。</summary>
        public static event System.Action EnabledChanged;

        /// <summary>場内放送を流すか（アプリ設定。既定はオン）。偽にしたら鳴っている放送もその場で止める。</summary>
        public static bool Enabled
        {
            get => PlayerPrefs.GetInt(PrefKey, 1) != 0;
            set
            {
                PlayerPrefs.SetInt(PrefKey, value ? 1 : 0);
                PlayerPrefs.Save();
                if (!value) Stop();
                EnabledChanged?.Invoke();
            }
        }

        /// <summary>いま放送している最中か（采配窓はこれが終わるまで1球目を待つ）。</summary>
        public static bool IsPlaying => _host != null && _host.IsPlaying;

        /// <summary>
        /// 部品の並びを頭から鳴らす（鳴っていたものは止める）。部品が1つでも無ければ鳴らさず false。
        /// </summary>
        public static bool Play(IReadOnlyList<string> clipNames)
        {
            if (clipNames == null || clipNames.Count == 0) return false;
            var list = new List<AudioClip>(clipNames.Count);
            foreach (var name in clipNames)
            {
                var c = Clip(name);
                if (c == null) return false;
                list.Add(c);
            }
            var host = Host();
            if (host == null) return false;
            host.Play(list);
            return true;
        }

        /// <summary>鳴っている放送をその場で止める（オフにした・高速試合モード・×4・代打で言い直す等）。</summary>
        public static void Stop()
        {
            if (_host != null) _host.StopNow();
        }

        /// <summary>
        /// 切り替えボタン（chip-btn）を場内放送のオン/オフに結ぶ。点灯は <c>chip-btn--on</c>。
        /// 戻り値は外すための処理（画面の OnDisable で呼ぶ）。
        /// </summary>
        public static System.Action BindToggle(Button button)
        {
            if (button == null) return () => { };
            void Apply() => button.EnableInClassList("chip-btn--on", Enabled);
            void Click() => Enabled = !Enabled;
            button.clicked += Click;
            EnabledChanged += Apply;
            Apply();
            return () =>
            {
                button.clicked -= Click;
                EnabledChanged -= Apply;
            };
        }

        private static AudioClip Clip(string name)
        {
            if (_clips.TryGetValue(name, out var cached)) return cached;
            var c = Resources.Load<AudioClip>(Dir + name);
            _clips[name] = c;   // 無いものも覚える（毎回探しに行かない）
            return c;
        }

        private static AnnouncerHost Host()
        {
            if (_host != null) return _host;
            var go = new GameObject("MatchAnnouncer");
            Object.DontDestroyOnLoad(go);
            _host = go.AddComponent<AnnouncerHost>();
            if (Object.FindAnyObjectByType<AudioListener>() == null) go.AddComponent<AudioListener>();
            return _host;
        }
    }

    /// <summary>放送の部品を dspTime で並べて鳴らす口。部品ごとに AudioSource を1本使う（同時に予約するため）。</summary>
    internal sealed class AnnouncerHost : MonoBehaviour
    {
        private const double LeadSeconds = 0.05;   // 予約の頭の余裕（今すぐ鳴らすと頭が欠けることがある）
        private const double GapSeconds = 0.18;    // 部品と部品の間（「4番、」「キャッチャー、」の読点の間）
        private const float Volume = 1f;

        private readonly List<AudioSource> _sources = new();
        private double _endDsp = -1;

        public bool IsPlaying => _endDsp >= 0 && AudioSettings.dspTime < _endDsp;

        public void Play(List<AudioClip> clips)
        {
            StopNow();
            while (_sources.Count < clips.Count)
            {
                var s = gameObject.AddComponent<AudioSource>();
                s.playOnAwake = false;
                s.spatialBlend = 0f;
                s.volume = Volume;
                _sources.Add(s);
            }
            var t = AudioSettings.dspTime + LeadSeconds;
            for (var i = 0; i < clips.Count; i++)
            {
                var s = _sources[i];
                s.clip = clips[i];
                s.PlayScheduled(t);
                t += clips[i].length + (i + 1 < clips.Count ? GapSeconds : 0);
            }
            _endDsp = t;
            MatchBrassBand.SetDucked(true);
        }

        public void StopNow()
        {
            foreach (var s in _sources) { s.Stop(); s.clip = null; }
            if (_endDsp >= 0) MatchBrassBand.SetDucked(false);
            _endDsp = -1;
        }

        private void Update()
        {
            if (_endDsp >= 0 && AudioSettings.dspTime >= _endDsp)
            {
                _endDsp = -1;
                MatchBrassBand.SetDucked(false);
            }
        }
    }
}
