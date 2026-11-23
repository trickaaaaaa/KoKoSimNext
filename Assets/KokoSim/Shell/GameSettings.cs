using System.Collections.Generic;
using UnityEngine;

namespace KokoSim.Unity.Shell
{
    /// <summary>
    /// 設定画面（issue #1315・docs/release/steam-roadmap.md 段階2）の値の持ち主。画面（解像度・フルスクリーン／ウインドウ）と
    /// 音量（全体・効果音・場内放送）を PlayerPrefs に持ち、起動時と変更時に反映する。
    ///
    /// <para>fps の上限は <see cref="KokoSim.Bootstrap.FrameRateGuard"/> が唯一の持ち主（ここでは持たない）。
    /// 場内放送のオンオフは <see cref="KokoSim.Unity.Audio.MatchAnnouncer"/> の既存キーのまま。設定はセーブでなく
    /// この機械の好み＝PlayerPrefs（セーブの版に関わらない）。</para>
    /// </summary>
    public static class GameSettings
    {
        private const string KeyWidth = "KokoSim.Settings.ResolutionWidth";
        private const string KeyHeight = "KokoSim.Settings.ResolutionHeight";
        private const string KeyFullScreen = "KokoSim.Settings.FullScreen";
        private const string KeyMaster = "KokoSim.Settings.VolumeMaster";
        private const string KeySe = "KokoSim.Settings.VolumeSe";
        private const string KeyAnnouncer = "KokoSim.Settings.VolumeAnnouncer";

        /// <summary>音量は 0〜100 の10刻み（設定画面の ◀▶ で動かす）。</summary>
        public const int VolumeStep = 10;
        private const int DefaultVolume = 100;

        /// <summary>選べる解像度の下限の幅（1280×720 未満は文字が潰れる）。</summary>
        private const int MinWidth = 1280;

        // ---------- 画面 ----------

        /// <summary>
        /// 解像度と全画面をゲームから変えられるか。ブラウザ版は false＝大きさはページの枠と置き場所の全画面ボタンが決める
        /// （ゲームから全画面にすると、最初のクリックでブラウザごと全画面になってしまう。2026-11-23 の Web お試しビルド）。
        /// </summary>
        public static bool DisplayAdjustable => !BuildPlatform.IsBrowser;

        public static bool FullScreen
        {
            get => PlayerPrefs.GetInt(KeyFullScreen, 1) != 0;
            set { PlayerPrefs.SetInt(KeyFullScreen, value ? 1 : 0); ApplyDisplay(); }
        }

        /// <summary>いまの解像度（未設定ならモニタの解像度）。</summary>
        public static Vector2Int Resolution
        {
            get
            {
                var w = PlayerPrefs.GetInt(KeyWidth, 0);
                var h = PlayerPrefs.GetInt(KeyHeight, 0);
                if (w <= 0 || h <= 0) { var n = Screen.currentResolution; return new Vector2Int(n.width, n.height); }
                return new Vector2Int(w, h);
            }
            set
            {
                PlayerPrefs.SetInt(KeyWidth, value.x);
                PlayerPrefs.SetInt(KeyHeight, value.y);
                ApplyDisplay();
            }
        }

        /// <summary>選べる解像度（モニタが出せるもの・幅 1280 以上・重複なし・小さい順）。いまの値は必ず含める。</summary>
        public static List<Vector2Int> ResolutionChoices()
        {
            var list = new List<Vector2Int>();
            foreach (var r in Screen.resolutions)
            {
                var v = new Vector2Int(r.width, r.height);
                if (v.x >= MinWidth && !list.Contains(v)) list.Add(v);
            }
            var cur = Resolution;
            if (!list.Contains(cur)) list.Add(cur);
            list.Sort((a, b) => a.x != b.x ? a.x.CompareTo(b.x) : a.y.CompareTo(b.y));
            return list;
        }

        // ---------- 音量（0〜100） ----------

        public static int VolumeMaster { get => Get(KeyMaster); set { Set(KeyMaster, value); ApplyAudio(); } }
        public static int VolumeSe { get => Get(KeySe); set => Set(KeySe, value); }
        public static int VolumeAnnouncer { get => Get(KeyAnnouncer); set => Set(KeyAnnouncer, value); }

        /// <summary>効果音（打球音・歓声・応援）に掛ける倍率。鳴らすたびに読む＝変えたらすぐ効く。</summary>
        public static float SeScale => VolumeSe / 100f;

        /// <summary>場内放送に掛ける倍率。</summary>
        public static float AnnouncerScale => VolumeAnnouncer / 100f;

        private static int Get(string key) => Mathf.Clamp(PlayerPrefs.GetInt(key, DefaultVolume), 0, 100);

        private static void Set(string key, int v) => PlayerPrefs.SetInt(key, Mathf.Clamp(v, 0, 100));

        // ---------- 反映 ----------

        private static void ApplyDisplay()
        {
#if UNITY_EDITOR || UNITY_WEBGL
            // エディタの Game View の大きさは触らない（撮影の解像度が変わる）。ブラウザ版も触らない（DisplayAdjustable）
            return;
#else
            var r = Resolution;
            Screen.SetResolution(r.x, r.y, FullScreen ? FullScreenMode.FullScreenWindow : FullScreenMode.Windowed);
#endif
        }

        private static void ApplyAudio() => AudioListener.volume = VolumeMaster / 100f;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void OnRuntimeLoad()
        {
            ApplyDisplay();
            ApplyAudio();
        }
    }
}
