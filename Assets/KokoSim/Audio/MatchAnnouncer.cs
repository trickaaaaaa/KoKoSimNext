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
    /// 部品は1本のクリップにつなぎ、場内放送らしい残響とエコーをかける（尾が消えるまで応援を絞る）。
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
            return host.Play(list);
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

    /// <summary>
    /// 放送の部品を1本のクリップにつないで鳴らす口。部品の間は無音、末尾に残響の尾ぶんの無音を足す。
    /// 1本の AudioSource に残響（AudioReverbFilter）とエコー（AudioEchoFilter）をかけるので、部品の
    /// つなぎ目で残響が途切れず、尾も末尾の無音の中で最後まで鳴る。止めたら尾も一緒に消える。
    /// </summary>
    internal sealed class AnnouncerHost : MonoBehaviour
    {
        private const double LeadSeconds = 0.05;   // 予約の頭の余裕（今すぐ鳴らすと頭が欠けることがある）
        private const float GapSeconds = 0.18f;    // 部品と部品の間（「4番、」「キャッチャー、」の読点の間）
        private const float TailSeconds = 2.5f;    // 残響の尾（末尾に足す無音。ダッキングもここまで続く）
        private const float Volume = 1f;

        private AudioSource _source;
        private AudioClip _merged;
        private double _voiceEndDsp = -1;
        private double _tailEndDsp = -1;

        /// <summary>声が終わるまで（残響の尾は含まない。采配窓が1球目を待つ長さ）。</summary>
        public bool IsPlaying => _voiceEndDsp >= 0 && AudioSettings.dspTime < _voiceEndDsp;

        private void Awake()
        {
            _source = gameObject.AddComponent<AudioSource>();
            _source.playOnAwake = false;
            _source.spatialBlend = 0f;
            _source.volume = Volume;

            var echo = gameObject.AddComponent<AudioEchoFilter>();
            echo.delay = 230f;
            echo.decayRatio = 0.3f;
            echo.dryMix = 1f;
            echo.wetMix = 0.35f;

            var reverb = gameObject.AddComponent<AudioReverbFilter>();
            reverb.reverbPreset = AudioReverbPreset.User;
            reverb.dryLevel = 0f;
            reverb.room = -1000f;
            reverb.roomHF = -1800f;
            reverb.decayTime = 2.2f;
            reverb.decayHFRatio = 0.5f;
            reverb.reflectionsLevel = -1200f;
            reverb.reflectionsDelay = 0.05f;
            reverb.reverbLevel = 0f;
            reverb.reverbDelay = 0.04f;
            reverb.diffusion = 100f;
            reverb.density = 100f;
            reverb.hfReference = 5000f;
        }

        public bool Play(List<AudioClip> clips)
        {
            StopNow();
            _merged = Merge(clips, out var voiceSeconds);
            if (_merged == null) return false;
            _source.clip = _merged;
            var start = AudioSettings.dspTime + LeadSeconds;
            _source.PlayScheduled(start);
            _voiceEndDsp = start + voiceSeconds;
            _tailEndDsp = start + _merged.length;
            MatchBrassBand.SetDucked(true);
            return true;
        }

        public void StopNow()
        {
            _source.Stop();
            _source.clip = null;
            if (_merged != null) { Destroy(_merged); _merged = null; }
            if (_tailEndDsp >= 0) MatchBrassBand.SetDucked(false);
            _voiceEndDsp = -1;
            _tailEndDsp = -1;
        }

        private void Update()
        {
            if (_tailEndDsp >= 0 && AudioSettings.dspTime >= _tailEndDsp)
            {
                _voiceEndDsp = -1;
                _tailEndDsp = -1;
                MatchBrassBand.SetDucked(false);
            }
        }

        /// <summary>
        /// <c>GetData</c> の前に波形を読み込む。素材は Preload Audio Data オフ（.meta の preloadAudioData: 0）なので、
        /// <c>Resources.Load</c> 直後は Unloaded のまま＝従来は <c>PlayScheduled</c> が再生時に読んでいたが、
        /// つなぐには再生前に中身が要る。素材は Load In Background オフ（Decompress On Load）なので
        /// <c>LoadAudioData</c> はその場で読み終わる（同期）。素材は Git 管理外で .meta を直せないためコードで読む。
        /// 誰かが Load In Background をオンにして読み込み中のまま返ってきた場合は、今回は鳴らさずに警告する
        /// （読み込みは裏で進むので次の打席からは鳴る）。
        /// </summary>
        private static bool EnsureLoaded(AudioClip clip)
        {
            if (clip.loadState == AudioDataLoadState.Loaded) return true;
            if (clip.loadState != AudioDataLoadState.Loading) clip.LoadAudioData();
            if (clip.loadState == AudioDataLoadState.Loaded) return true;
            Debug.LogWarning($"[MatchAnnouncer] 部品の波形がまだ読み込まれていないので放送を鳴らさない: {clip.name} (loadState={clip.loadState})");
            return false;
        }

        private static AudioClip Merge(List<AudioClip> clips, out float voiceSeconds)
        {
            voiceSeconds = 0f;
            var first = clips[0];
            int ch = first.channels, hz = first.frequency;
            int gap = Mathf.RoundToInt(GapSeconds * hz);
            int tail = Mathf.RoundToInt(TailSeconds * hz);
            int voiceFrames = 0;
            for (var i = 0; i < clips.Count; i++)
            {
                if (clips[i].channels != ch || clips[i].frequency != hz)
                {
                    Debug.LogWarning($"[MatchAnnouncer] 部品の形式が揃っていないので放送を鳴らさない: {clips[i].name} ({clips[i].channels}ch/{clips[i].frequency}Hz) vs {first.name} ({ch}ch/{hz}Hz)");
                    return null;
                }
                if (!EnsureLoaded(clips[i])) return null;
                voiceFrames += clips[i].samples + (i + 1 < clips.Count ? gap : 0);
            }
            var data = new float[(voiceFrames + tail) * ch];
            int at = 0;
            for (var i = 0; i < clips.Count; i++)
            {
                var part = new float[clips[i].samples * ch];
                if (!clips[i].GetData(part, 0))
                {
                    Debug.LogWarning($"[MatchAnnouncer] 部品の波形を読めないので放送を鳴らさない: {clips[i].name} (loadState={clips[i].loadState}, loadType={clips[i].loadType})");
                    return null;
                }
                System.Array.Copy(part, 0, data, at, part.Length);
                at += part.Length + (i + 1 < clips.Count ? gap * ch : 0);
            }
            var merged = AudioClip.Create("AnnounceMerged", voiceFrames + tail, ch, hz, false);
            merged.SetData(data, 0);
            voiceSeconds = (float)voiceFrames / hz;
            return merged;
        }
    }
}
