#if KOKOSIM_DEBUG || UNITY_EDITOR || DEVELOPMENT_BUILD
using KokoSim.Engine.Debugging;
using KokoSim.Engine.Match.Game;

namespace KokoSim.Unity.Debugging
{
    /// <summary>
    /// 「いま観測している試合」の観測バッファを1箇所に集める場（設計書17 §4.2 / §5, F3）。
    ///
    /// <para>デバッグHUDはここからだけ読む。データを供給する側は2つある:</para>
    /// <list type="bullet">
    ///   <item>MCP/Editorメニュー経由のヘッドレス試合（<see cref="DebugBridge"/>）</item>
    ///   <item>画面上のライブ観戦（MatchLiveController が自前生成する試合）</item>
    /// </list>
    ///
    /// <para><b>HUDは表示専用</b>で、ここに溜まった観測を読むだけ＝engine を1回も追加で呼ばない。
    /// よって HUD の表示/非表示で試合結果は1ビットも変わらない（設計書17 §9 F3 DoD）。</para>
    ///
    /// <para><see cref="Enabled"/> が偽のあいだは <see cref="AttachTo"/> が何もしない＝
    /// 観測そのものが走らない（既定オフ。HUDを開くかMCPから触ったときだけオンになる）。</para>
    /// </summary>
    public static class DebugTraceHub
    {
        /// <summary>観測を有効にするか。既定オフ＝通常プレイは従来どおりゼロコスト。</summary>
        public static bool Enabled { get; set; }

        /// <summary>現在の観測バッファ（未接続なら null）。HUDのデータ源。</summary>
        public static RingBufferTraceSink Ring { get; private set; }

        /// <summary>HUD を表示中か（F1トグル）。</summary>
        public static bool HudVisible { get; set; }

        /// <summary>新しい観測バッファを用意して返す（前の試合のぶんは捨てる）。</summary>
        public static RingBufferTraceSink NewRing()
        {
            Ring = new RingBufferTraceSink(pitchCapacity: 64, paCapacity: 16);
            return Ring;
        }

        /// <summary>
        /// 観測が有効なら、この <see cref="GameContext"/> に観測を差し込んだコピーを返す。
        /// 無効なら<b>引数をそのまま返す</b>（呼び出し側は常にこれを通せばよい＝分岐を持たない）。
        /// </summary>
        public static GameContext AttachTo(GameContext ctx)
            => Enabled ? ctx with { CaptureTrace = true, TraceSink = NewRing() } : ctx;
    }
}
#endif
