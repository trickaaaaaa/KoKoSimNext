namespace KokoSim.Unity.Shell
{
    /// <summary>
    /// 裏で走る全シムの完了を1箇所で待つ（issue #1226）。static 状態（<see cref="NationService"/> の
    /// ロスター・年間台帳・大会成績）に触る節目は必ずここを通す＝経路ごとに待つシムの組み合わせを
    /// 書き分けない（抜け漏れの温床だった）。
    ///
    /// <list type="bullet">
    /// <item><see cref="NationBackgroundSim"/> — 全国47県の裏試合フルシム（#208）</item>
    /// <item><see cref="BracketFinishSim"/> — 自校敗退後の残りブラケット消化（#529）</item>
    /// <item><see cref="KoshienQualifierStatsSim"/> / <see cref="SenbatsuQualifierStatsSim"/> — 甲子園/センバツ
    /// 代表校の県予選・地区大会成績の打ち直し（設計書05 §1.4b/§1.5b）</item>
    /// <item><see cref="GameSession.JoinPrefetch"/> — 自県裏カードの先読み（本解決と同じ <c>NationRosters</c> を読む）</item>
    /// </list>
    ///
    /// どれも未開始なら即返るので、呼び先が都度「要るか」を判定する必要はない。
    /// </summary>
    public static class BackgroundSims
    {
        /// <summary>上の全シムの完了を待つ。ロード・冬の節目・年度替わり・監督の転任など、
        /// static 状態を書き換える直前に必ず呼ぶ。</summary>
        public static void EnsureAllCompleted()
        {
            NationBackgroundSim.EnsureCompleted();
            BracketFinishSim.EnsureCompleted();
            KoshienQualifierStatsSim.EnsureCompleted();
            SenbatsuQualifierStatsSim.EnsureCompleted();
            GameSession.Current.JoinPrefetch();
        }
    }
}
