using System.Collections.Generic;
using UnityEngine;

namespace KokoSim.Unity.Audio
{
    /// <summary>
    /// 甲子園の試合でアルプス席のブラスバンド応援を流す。表示専用＝判定・帯には一切関与しない。
    ///
    /// 曲は監督が Suno（有料プラン）で作ったもの。Git 管理外（Assets/KokoSim/Audio/Vendor/）に置き、
    /// Resources の <c>Music/Brass/{normal|chance|score}_NN</c> から読む。<b>曲が無い環境では黙って鳴らさない</b>。
    ///
    /// 流れ方:
    /// - 攻撃している側の学校の曲を流す（守っている側は鳴らさない）
    /// - 得点圏（二塁か三塁）に走者がいる間は「チャンス」の曲
    /// - 点が入ったら「得点」の曲の頭だけを流し、終わったら攻撃の曲へ戻る
    /// - 攻守交代の間・高速試合モード中・試合終了後は止める
    ///
    /// どの学校がどの曲かは学校IDで決める（乱数・string.GetHashCode は使わない＝毎回同じ曲）。
    /// 場面ごとに曲の数が違ってよい（セットで揃えない）。
    /// </summary>
    public static class MatchBrassBand
    {
        private const string Dir = "Music/Brass/";
        private const string Normal = "normal";
        private const string Chance = "chance";
        private const string Score = "score";

        private static readonly Dictionary<string, AudioClip[]> _banks = new();
        private static BrassBandHost _host;
        private static bool _enabled;
        private static int _awaySchoolId, _homeSchoolId;

        /// <summary>
        /// 試合の頭で呼ぶ。<paramref name="enabled"/> が偽（甲子園以外）なら、この試合のあいだ何も鳴らさない。
        /// </summary>
        public static void Begin(bool enabled, int awaySchoolId, int homeSchoolId)
        {
            _enabled = enabled;
            _awaySchoolId = awaySchoolId;
            _homeSchoolId = homeSchoolId;
            if (_host != null) _host.StopNow();
        }

        /// <summary>いまの攻撃側と得点圏の走者の有無を伝える（塁の表示を更新するたびに呼んでよい）。</summary>
        public static void SetOffense(bool isTop, bool runnerInScoringPosition)
        {
            if (!_enabled) return;
            var id = isTop ? _awaySchoolId : _homeSchoolId;
            var clip = Pick(runnerInScoringPosition ? Chance : Normal, id);
            Host()?.SetTheme(clip);
        }

        /// <summary>点が入った瞬間に呼ぶ。<paramref name="isTop"/> は点を取った側（表＝先攻）。</summary>
        public static void Scored(bool isTop)
        {
            if (!_enabled) return;
            var clip = Pick(Score, isTop ? _awaySchoolId : _homeSchoolId);
            if (clip != null) Host()?.PlayFanfare(clip);
        }

        /// <summary>攻守交代の間・高速試合モードなど、いったん演奏を止める（次の <see cref="SetOffense"/> で再開）。</summary>
        public static void Rest()
        {
            if (_host != null) _host.SetTheme(null);
        }

        /// <summary>
        /// 場内放送の間は応援を絞る（issue #919）。真で絞り、偽で戻す。曲は止めない（音量だけ）。
        /// </summary>
        public static void SetDucked(bool ducked)
        {
            if (_host != null) _host.SetDucked(ducked);
        }

        /// <summary>試合が終わった。<paramref name="immediate"/> なら即座に、そうでなければ絞って止める。</summary>
        public static void End(bool immediate)
        {
            _enabled = false;
            if (_host == null) return;
            if (immediate) _host.StopNow();
            else _host.FadeAll();
        }

        /// <summary>
        /// 学校IDから場面の曲を1つ選ぶ。場面ごとに足す数を変えて、同じ学校でも場面ごとに別のセット由来の曲になりうる。
        /// </summary>
        private static AudioClip Pick(string scene, int schoolId)
        {
            var clips = Bank(scene);
            if (clips.Length == 0) return null;
            long salt = scene == Normal ? 0 : scene == Chance ? 1 : 2;
            long k = (long)schoolId * 7919L + salt * 31L;
            int i = (int)(((k % clips.Length) + clips.Length) % clips.Length);
            return clips[i];
        }

        private static AudioClip[] Bank(string scene)
        {
            if (_banks.TryGetValue(scene, out var cached)) return cached;
            var list = new List<AudioClip>();
            for (int n = 1; n <= 20; n++)
            {
                var c = Resources.Load<AudioClip>(Dir + scene + "_" + n.ToString("00"));
                if (c != null) list.Add(c);
            }
            var arr = list.ToArray();
            _banks[scene] = arr;
            return arr;
        }

        private static BrassBandHost Host()
        {
            if (_host != null) return _host;
            var go = new GameObject("MatchBrassBand");
            Object.DontDestroyOnLoad(go);
            _host = go.AddComponent<BrassBandHost>();
            if (Object.FindAnyObjectByType<AudioListener>() == null) go.AddComponent<AudioListener>();
            return _host;
        }
    }

    /// <summary>
    /// 応援曲の音源2本（攻撃の曲／得点の曲）と、切り替えの絞り・持ち上げを持つ。
    /// 時計は実時間（倍速・中断に関係なくスタンドは演奏を続ける）。
    /// </summary>
    internal sealed class BrassBandHost : MonoBehaviour
    {
        private const float ThemeVolume = 0.45f;     // 打球音・場内放送が埋もれない大きさ
        private const float FanfareVolume = 0.55f;
        private const float SwitchFadeSeconds = 0.6f;
        private const float FanfareSeconds = 10f;    // 得点の曲は頭のこの秒数だけ流す
        private const float FanfareFadeSeconds = 1.5f;
        private const float DuckLevel = 0.3f;        // 場内放送の間の音量（元の音量に対する割合。issue #919）
        private const float DuckSeconds = 0.25f;     // 絞る・戻すのにかける時間

        private AudioSource _theme, _fanfare;
        private AudioClip _wanted;        // いま流したい攻撃の曲（null＝止める）
        private float _fanfareClock = -1f; // 得点の曲の経過秒（<0＝鳴らしていない）
        private bool _fadingAll;
        private bool _ducked;
        private float _duck = 1f;          // いまの絞り（1＝元の音量）

        private void Awake()
        {
            _theme = Make(loop: true);
            _fanfare = Make(loop: false);
        }

        private AudioSource Make(bool loop)
        {
            var s = gameObject.AddComponent<AudioSource>();
            s.playOnAwake = false;
            s.loop = loop;
            s.spatialBlend = 0f;
            s.volume = 0f;
            return s;
        }

        public void SetTheme(AudioClip clip)
        {
            _fadingAll = false;
            _wanted = clip;
        }

        public void PlayFanfare(AudioClip clip)
        {
            _fadingAll = false;
            _fanfare.Stop();
            _fanfare.clip = clip;
            _fanfare.volume = FanfareVolume * _duck;
            _fanfare.Play();
            _fanfareClock = 0f;
            // 攻撃の曲は止めて、得点の曲が終わったら頭から流し直す
            _theme.Stop();
            _theme.clip = null;
        }

        public void SetDucked(bool ducked) => _ducked = ducked;

        public void FadeAll()
        {
            _wanted = null;
            _fadingAll = true;
        }

        public void StopNow()
        {
            _wanted = null;
            _fadingAll = false;
            _fanfareClock = -1f;
            _theme.Stop(); _theme.clip = null; _theme.volume = 0f;
            _fanfare.Stop(); _fanfare.clip = null;
        }

        private void Update()
        {
            float dt = Time.unscaledDeltaTime;
            float step = dt / SwitchFadeSeconds;
            float prevDuck = _duck;
            _duck = Mathf.MoveTowards(_duck, _ducked ? DuckLevel : 1f, dt / DuckSeconds * (1f - DuckLevel));
            if (_fanfareClock >= 0f && !_fadingAll && prevDuck > 0f)
                _fanfare.volume *= _duck / prevDuck;   // 得点の曲も同じ割合で絞る・戻す

            if (_fanfareClock >= 0f)
            {
                _fanfareClock += dt;
                float fadeFrom = FanfareSeconds - FanfareFadeSeconds;
                if (_fadingAll) _fanfare.volume = Mathf.MoveTowards(_fanfare.volume, 0f, step * FanfareVolume);
                else if (_fanfareClock > fadeFrom)
                    _fanfare.volume = FanfareVolume * _duck * Mathf.Clamp01((FanfareSeconds - _fanfareClock) / FanfareFadeSeconds);
                if (_fanfareClock >= FanfareSeconds || !_fanfare.isPlaying || _fanfare.volume <= 0f)
                {
                    _fanfare.Stop();
                    _fanfare.clip = null;
                    _fanfareClock = -1f;
                }
                else return;   // 得点の曲の間は攻撃の曲を鳴らさない
            }

            if (_theme.clip != _wanted)
            {
                // 今の曲を絞り切ってから次へ（曲の途中で急に切れないように）
                if (_theme.clip != null && _theme.isPlaying && _theme.volume > 0f)
                {
                    _theme.volume = Mathf.MoveTowards(_theme.volume, 0f, step * ThemeVolume);
                    return;
                }
                _theme.Stop();
                _theme.clip = _wanted;
                _theme.volume = 0f;
                if (_wanted != null) _theme.Play();
            }
            else if (_theme.clip != null)
            {
                // 場内放送の間は絞った音量へ（issue #919）。絞り・戻しは _duck が時間をかけて動く
                float target = ThemeVolume * _duck;
                if (_theme.volume < target)
                    _theme.volume = Mathf.MoveTowards(_theme.volume, target, step * ThemeVolume);
                else _theme.volume = target;
            }
        }
    }
}
