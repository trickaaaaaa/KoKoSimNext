using System;
using System.Globalization;

namespace KokoSim.Unity.Benchmark
{
    /// <summary>
    /// ベンチマークモード（<c>-benchmark</c>）の起動引数（issue #1314・docs/release/steam-roadmap.md 段階1）。
    /// 引数を読むだけの純粋なクラス（Unity に触れない）。値が読めない引数は既定のままにする＝
    /// 無人で回すものなので、打ち間違いで止めずに既定で走らせる。
    ///
    /// <code>
    /// HakkyuKantokuroku.exe -benchmark [-benchmark-out &lt;dir&gt;] [-benchmark-seed &lt;hex&gt;]
    ///     [-benchmark-weeks &lt;n&gt;] [-benchmark-matches &lt;n&gt;] [-benchmark-match-seconds &lt;s&gt;]
    ///     [-benchmark-idle-seconds &lt;s&gt;] [-benchmark-speed &lt;x&gt;]
    /// </code>
    /// </summary>
    public sealed class BenchmarkOptions
    {
        public const string Flag = "-benchmark";

        /// <summary>母種の既定値。値自体に意味は無い（2台の機で同じ展開にするための固定）。</summary>
        public const ulong DefaultSeed = 0xBE0C_0001UL;

        /// <summary>CSV の出力先。空なら <c>persistentDataPath/benchmark</c>。</summary>
        public string OutDir;

        public ulong Seed = DefaultSeed;

        /// <summary>週送りの上限。大会に入った時点で止まるので、通常は夏の県大会の開幕週（第13週）で終わる。</summary>
        public int MaxWeeks = 30;

        /// <summary>大会で自校の試合を何試合分、実フローと同じ経路で回すか。</summary>
        public int TournamentMatches = 2;

        /// <summary>甲子園の試合（満員・ライブ観戦画面）を映して測る秒数。終局したらそこで止める。</summary>
        public float MatchSeconds = 120f;

        /// <summary>タイトル・ホームの静止画面を測る秒数。</summary>
        public float IdleSeconds = 3f;

        /// <summary>試合の再生速度（×1 が実プレーと同じ）。</summary>
        public float MatchSpeed = 1f;

        public static bool TryParse(string[] args, out BenchmarkOptions options)
        {
            options = null;
            if (args == null) return false;

            var found = false;
            var o = new BenchmarkOptions();
            for (var i = 0; i < args.Length; i++)
            {
                var a = args[i];
                if (a == null) continue;
                if (string.Equals(a, Flag, StringComparison.OrdinalIgnoreCase)) { found = true; continue; }

                var value = i + 1 < args.Length ? args[i + 1] : null;
                switch (a.ToLowerInvariant())
                {
                    case "-benchmark-out":
                        if (!string.IsNullOrWhiteSpace(value)) { o.OutDir = value; i++; }
                        break;
                    case "-benchmark-seed":
                        if (TryParseHex(value, out var seed)) { o.Seed = seed; i++; }
                        break;
                    case "-benchmark-weeks":
                        if (TryParseInt(value, 1, out var weeks)) { o.MaxWeeks = weeks; i++; }
                        break;
                    case "-benchmark-matches":
                        if (TryParseInt(value, 0, out var matches)) { o.TournamentMatches = matches; i++; }
                        break;
                    case "-benchmark-match-seconds":
                        if (TryParseFloat(value, 1f, out var ms)) { o.MatchSeconds = ms; i++; }
                        break;
                    case "-benchmark-idle-seconds":
                        if (TryParseFloat(value, 0f, out var idle)) { o.IdleSeconds = idle; i++; }
                        break;
                    case "-benchmark-speed":
                        if (TryParseFloat(value, 0.1f, out var speed)) { o.MatchSpeed = speed; i++; }
                        break;
                }
            }

            if (!found) return false;
            options = o;
            return true;
        }

        public static bool TryParseHex(string s, out ulong value)
        {
            value = 0;
            if (string.IsNullOrWhiteSpace(s)) return false;
            var t = s.Trim();
            if (t.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) t = t.Substring(2);
            return ulong.TryParse(t.Replace("_", ""), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out value);
        }

        private static bool TryParseInt(string s, int min, out int value)
            => int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out value) && value >= min;

        private static bool TryParseFloat(string s, float min, out float value)
            => float.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out value) && value >= min;
    }
}
