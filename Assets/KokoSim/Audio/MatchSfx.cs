using System.Collections.Generic;
using UnityEngine;

namespace KokoSim.Unity.Audio
{
    /// <summary>
    /// 試合の効果音（打球音・捕手の捕球音）。表示専用＝判定・帯には一切関与しない。
    ///
    /// 素材は On-Jin ～音人～（https://on-jin.com/）の野球SE。規約で「素材のまま第三者が取得できる状態」
    /// が禁止なので Git 管理外（.gitignore の Assets/KokoSim/Audio/Vendor/）に置き、Resources から読む。
    /// **素材が無い環境では黙って鳴らさない**（購入素体と同じ流儀＝ゲームは動く）。
    ///
    /// 強さは「段（どのクリップ群か）」と「音量・ピッチ」の2つで出す。素材は段ごとに数本しか無いので、
    /// 段の中の連続的な差は音量とピッチで付ける。同じ段の中は順番に回す（乱数は使わない）。
    /// </summary>
    public static class MatchSfx
    {
        private const string ResourceDir = "Sfx/OnJin/";

        // 捕球音の段の境目[km/h]。高校生の球速帯（110〜150）を3つに割る
        private const float CatchMidKmh = 120f;
        private const float CatchStrongKmh = 140f;

        // 打球の強さ 0..1 に写す打ち出し直後の速さ[m/s]（≒90〜160km/h）
        private const float BatWeakMps = 25f;
        private const float BatStrongMps = 44f;
        // これ以上は「芯で捉えた」大きい音のクリップへ
        private const float BatStrongThreshold = 0.7f;

        private static GameObject _host;
        private static readonly List<AudioSource> _voices = new();
        private static int _nextVoice;
        private static readonly Dictionary<string, AudioClip[]> _banks = new();
        private static readonly Dictionary<string, int> _rotation = new();

        /// <summary>
        /// 捕手の捕球音。<paramref name="velocityKmh"/> が速いほど強い段・大きい音。
        /// 球速が不明（0以下）なら中くらいで鳴らす。
        /// </summary>
        public static void PlayCatch(float velocityKmh)
        {
            float kmh = velocityKmh > 0f ? velocityKmh : 128f;
            string bank = kmh >= CatchStrongKmh ? "catch_strong"
                : kmh >= CatchMidKmh ? "catch_mid"
                : "catch_light";
            float s = Mathf.InverseLerp(100f, 155f, kmh);
            Play(bank, Mathf.Lerp(0.65f, 1.0f, s), Mathf.Lerp(0.97f, 1.04f, s));
        }

        /// <summary>
        /// 打球音（金属バット）。<paramref name="strength01"/> は 0=当たり損ね〜1=芯で痛烈。
        /// バントは専用の「コツッ」を使う。
        /// </summary>
        public static void PlayBat(float strength01, bool isBunt)
        {
            float s = Mathf.Clamp01(strength01);
            if (isBunt)
            {
                Play("bat_bunt", 0.8f, 1f);
                return;
            }
            string bank = s >= BatStrongThreshold ? "bat_hit_strong" : "bat_hit";
            // 素材は控えめな音量で収録されているので、強い当たりは上限まで出す
            Play(bank, Mathf.Lerp(0.45f, 1.0f, s), Mathf.Lerp(0.92f, 1.03f, s));
        }

        /// <summary>
        /// 打ち出し直後の速さ[m/s]から打球の強さ 0..1 を出す。
        /// </summary>
        public static float BatStrengthFromSpeed(float launchSpeedMps)
            => Mathf.InverseLerp(BatWeakMps, BatStrongMps, launchSpeedMps);

        private static void Play(string bank, float volume, float pitch)
        {
            var clips = Bank(bank);
            if (clips.Length == 0) return;
            _rotation.TryGetValue(bank, out int i);
            _rotation[bank] = i + 1;
            var clip = clips[i % clips.Length];

            var src = Voice();
            if (src == null) return;
            src.pitch = pitch;
            src.PlayOneShot(clip, volume);
        }

        private static AudioClip[] Bank(string bank)
        {
            if (_banks.TryGetValue(bank, out var cached)) return cached;
            // bat_hit_01, bat_hit_02 … のように連番で置く（bat_hit_strong_* は別の段なので混ぜない）
            var list = new List<AudioClip>();
            for (int n = 1; n <= 9; n++)
            {
                var c = Resources.Load<AudioClip>(ResourceDir + bank + "_" + n.ToString("00"));
                if (c != null) list.Add(c);
            }
            var arr = list.ToArray();
            _banks[bank] = arr;
            return arr;
        }

        /// <summary>
        /// 鳴らす口を返す。前の音を切らずに重ねられるよう数本を回す
        /// （ピッチは口ごとの設定なので、PlayOneShot を1本で重ねると前の音のピッチまで変わる）。
        /// </summary>
        private static AudioSource Voice()
        {
            if (_host == null)
            {
                _voices.Clear();
                _host = new GameObject("MatchSfx");
                Object.DontDestroyOnLoad(_host);
                for (int i = 0; i < 4; i++)
                {
                    var s = _host.AddComponent<AudioSource>();
                    s.playOnAwake = false;
                    s.spatialBlend = 0f;   // 2D＝カメラ位置に関係なく同じ聞こえ方
                    _voices.Add(s);
                }
                // 聞き手が居ないシーンでは鳴らないので、無ければ自前で持つ
                if (Object.FindAnyObjectByType<AudioListener>() == null)
                    _host.AddComponent<AudioListener>();
            }
            var v = _voices[_nextVoice % _voices.Count];
            _nextVoice++;
            return v;
        }
    }
}
