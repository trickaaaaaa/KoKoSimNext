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
    /// </summary>
    public static class GameSeed
    {
        private static ulong _master = InitMaster();

        /// <summary>この新規ゲームの母種。全ての派生シードのもと。</summary>
        public static ulong Master => _master;

        private static ulong InitMaster() => unchecked((ulong)System.DateTime.UtcNow.Ticks * 0x9E3779B97F4A7C15UL);

        /// <summary>母種を明示設定（テスト・将来のセーブ復元用）。同じ値を入れれば同じ展開に再現。</summary>
        public static void Reset(ulong master) => _master = master;
    }
}
