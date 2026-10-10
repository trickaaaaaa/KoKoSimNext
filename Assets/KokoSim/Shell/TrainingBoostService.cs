using System.Collections.Generic;
using KokoSim.Engine.Saves;

namespace KokoSim.Unity.Shell
{
    /// <summary>
    /// 期間つきの練習効率補正（issue #615）。イベントが「今週から N 週だけ効率が上がる」を作る。
    ///
    /// <para><b>セーブに載せる</b>（v10）。載せないと、強化指定を受けた直後にロードすると効果が消える
    /// ＝プレイヤーから見て「起きたはずのことが無かったことになる」。</para>
    ///
    /// <para>補正は<b>掛け算で重なる</b>（外部コーチ＋合宿など）。上限は置いていないが、
    /// イベント側の倍率が小さいので現実的な範囲に収まる。</para>
    /// </summary>
    public static class TrainingBoostService
    {
        /// <summary>効いている補正1本。</summary>
        public sealed class Boost
        {
            /// <summary>倍率（1.10＝＋10%）。</summary>
            public double Mult;
            /// <summary>残り週数（0で消える）。</summary>
            public int WeeksLeft;
            /// <summary>対象の選手Id（0＝部全体）。</summary>
            public int PlayerId;
            /// <summary>由来のイベントID（画面と保存の手掛かり）。</summary>
            public string Source;
        }

        private static readonly List<Boost> _boosts = new List<Boost>();

        /// <summary>効いている補正（表示・保存用）。</summary>
        public static IReadOnlyList<Boost> Active { get { return _boosts; } }

        /// <summary>補正を1本足す。</summary>
        public static void Add(string source, double mult, int weeks, int playerId)
        {
            if (weeks <= 0 || mult == 1.0) return;
            _boosts.Add(new Boost { Mult = mult, WeeksLeft = weeks, PlayerId = playerId, Source = source });
        }

        /// <summary>
        /// その選手の今週の倍率（部全体ぶん × 本人ぶん）。補正が無ければ 1.0＝従来と1ビット一致。
        /// </summary>
        public static double MultiplierFor(int playerId)
        {
            var m = 1.0;
            foreach (var b in _boosts)
                if (b.WeeksLeft > 0 && (b.PlayerId == 0 || b.PlayerId == playerId)) m *= b.Mult;
            return m;
        }

        /// <summary>
        /// 週送りの<b>最後</b>に1週ぶん減らす。練習を適用した後に呼ぶ＝
        /// 「N週」と言われた週にちゃんとN回効く（先に減らすと1週ぶん短くなる）。
        /// </summary>
        public static void Tick()
        {
            for (var i = _boosts.Count - 1; i >= 0; i--)
            {
                _boosts[i].WeeksLeft--;
                if (_boosts[i].WeeksLeft <= 0) _boosts.RemoveAt(i);
            }
        }

        /// <summary>新規ゲーム・テスト用。</summary>
        public static void Clear() { _boosts.Clear(); }

        /// <summary>セーブ保存（issue #615・v10）。</summary>
        public static void Capture(SaveState s)
        {
            s.TrainingBoosts = new List<TrainingBoostSaveState>();
            foreach (var b in _boosts)
            {
                s.TrainingBoosts.Add(new TrainingBoostSaveState
                {
                    Source = b.Source ?? "",
                    Mult = b.Mult,
                    WeeksLeft = b.WeeksLeft,
                    PlayerId = b.PlayerId,
                });
            }
        }

        /// <summary>セーブ復元。</summary>
        public static void Restore(SaveState s)
        {
            _boosts.Clear();
            if (s.TrainingBoosts == null) return;
            foreach (var b in s.TrainingBoosts)
                _boosts.Add(new Boost { Mult = b.Mult, WeeksLeft = b.WeeksLeft, PlayerId = b.PlayerId, Source = b.Source });
        }
    }
}
