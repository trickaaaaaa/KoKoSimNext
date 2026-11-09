using KokoSim.Engine.Players;

namespace KokoSim.Unity.Shell
{
    /// <summary>
    /// 調子5段階の表示用テキスト・色（設計書02 §3.3）。以前は NewTeamService / PlayerListState /
    /// PlayerDetailState の3箇所に ConditionJp が重複実装されていた（issue #51）。単一ソースへ統合する。
    /// 色は <see cref="ConditionFace"/> の塗り色と一致させる（表情顔と併記する文字表記が食い違わないように）。
    /// </summary>
    public static class ConditionLabels
    {
        public static string Jp(Condition condition) => condition switch
        {
            Condition.Excellent => "絶好調",
            Condition.Good => "好調",
            Condition.Poor => "不調",
            Condition.Terrible => "絶不調",
            _ => "普通",
        };

        // tokens.uss --cond-* と同値（赤＝絶好調 … 青＝絶不調のパワプロ式・2026-08-15）。
        public static string ColorHex(Condition condition) => condition switch
        {
            Condition.Excellent => "#E0605A",
            Condition.Good => "#E8904A",
            Condition.Poor => "#7FA8D6",
            Condition.Terrible => "#4F6FA8",
            _ => "#E3BD3C",
        };
    }
}
