namespace KokoSim.Unity.Shell
{
    /// <summary>
    /// 新規ゲームの母種（マスターシード）。1セッションに1回だけ非決定的に引く＝ここが唯一のエントロピー注入点。
    /// 以降の大会シード等はこの母種から導出するため、母種を固定すればゲーム全体が完全再現される（決定論は維持）。
    /// これにより「毎回まったく同じ試合」を解消しつつ、同じ母種のロードでは同じ展開に再現できる。
    ///
    /// 注: 不変条件#2（乱数はシード付き IRandomSource・System.Random/Guid 禁止）は「エンジンの内部ロジック」に対する規定。
    /// ここは Shell（UnityEngine 層）での「新規ゲームの初期種の採取」であり、エンジンの決定論を壊さない唯一の外部入力。
    /// 将来はセーブデータへ母種を保存し、ロード時に Reset で復元する。
    ///
    /// <para><b>設計書17 §3.1 / §8（Q18-1 確定・2026-07-21）</b>: 方針は「観測は残す、操作は消す」。
    /// 母種の<b>表示</b>（<see cref="Master"/> / <see cref="MasterHex"/> / 起動ログ1行）は読み取り専用の数字＝
    /// 操作経路にならないのでリリースビルドにも残す（バグ報告からその人のプレイを丸ごと再現できる）。
    /// 母種の<b>固定</b>（<see cref="Reset"/>）は任意のシードを引き直せてしまうので <c>KOKOSIM_DEBUG</c> の内側に閉じる。</para>
    /// </summary>
    public static class GameSeed
    {
        private static ulong _master = InitMaster();

        /// <summary>この新規ゲームの母種。全ての派生シードのもと。</summary>
        public static ulong Master => _master;

        /// <summary>母種の16進表記（バグ報告・再現トークンへの貼り付け用）。</summary>
        public static string MasterHex => Hex(_master);

        private static ulong InitMaster()
        {
            var master = unchecked((ulong)System.DateTime.UtcNow.Ticks * 0x9E3779B97F4A7C15UL);
            // 設計書17 §8（Q18-1）: 母種ログ1行は KOKOSIM_DEBUG の「外」＝define非依存で常に出す。
            // 出力先は Debug.Log なので Player.log に残り、バグ報告からそのプレイを丸ごと再現できる。
            UnityEngine.Debug.Log("[KokoSim] master seed = " + Hex(master));
            return master;
        }

        private static string Hex(ulong v)
            => "0x" + v.ToString("x16", System.Globalization.CultureInfo.InvariantCulture);

// デバッグ面のゲート（設計書17 §8）。Editor と Development Build では常に有効、
// リリースビルドでは KOKOSIM_DEBUG を明示しない限り型ごと消える。この3項の並びは
// Assets/KokoSim/Debug/ 配下と同一（grep で1箇所も漏らさないための単一の書き方）。
#if KOKOSIM_DEBUG || UNITY_EDITOR || DEVELOPMENT_BUILD
        /// <summary>
        /// 母種を明示設定（デバッグ・将来のセーブ復元用）。同じ値を入れれば同じ展開に再現。
        /// 設計書17 §8（Q18-1）: 「固定」は操作面なのでリリースビルドからは型ごと消える。
        /// セーブ復元でこれが必要になったら、復元専用の別APIとして define の外へ切り出すこと
        /// （「表示は残す・固定は消す」の線は動かさない）。
        /// </summary>
        public static void Reset(ulong master)
        {
            _master = master;
            UnityEngine.Debug.Log("[KokoSim] master seed <- " + Hex(master));
        }

        /// <summary>16進文字列（"0x..." 可）から母種を設定する。解釈できなければ false。</summary>
        public static bool TryResetHex(string hex)
        {
            if (string.IsNullOrWhiteSpace(hex)) return false;
            var s = hex.Trim();
            if (s.StartsWith("0x", System.StringComparison.OrdinalIgnoreCase)) s = s.Substring(2);
            if (!ulong.TryParse(s, System.Globalization.NumberStyles.HexNumber,
                    System.Globalization.CultureInfo.InvariantCulture, out var v)) return false;
            Reset(v);
            return true;
        }
#endif
    }
}
