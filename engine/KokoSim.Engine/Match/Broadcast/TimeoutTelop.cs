using KokoSim.Engine.Match.Game;

namespace KokoSim.Engine.Match.Broadcast;

/// <summary>
/// 伝令のテロップ1行と実況履歴の1行を組む（issue #1739）。自校の伝令（中断から送る）も相手校の伝令（采配AI・
/// <see cref="TimeoutEvent"/>）も、表示する中身はここ一箇所から引く。守備位置は出さない。
/// </summary>
public static class TimeoutTelop
{
    /// <summary>伝令役のテロップの1行（背番号＋名前）。控えが0人で記録員が走ったとき（null）は「記録員」。</summary>
    public static TelopRow Row(SubstitutionPlayer? messenger) =>
        messenger is null
            ? new TelopRow("伝令", "", 0, "記録員", "", null)
            : new TelopRow("伝令", "", messenger.Number, messenger.Name, "", null);

    /// <summary>実況履歴の1行（例「3回表 青雲学院高校 守備伝令 田中 一郎」）。</summary>
    public static string HistoryLine(TimeoutEvent e, string teamName)
        => e.Inning + "回" + (e.IsTopHalf ? "表" : "裏") + " " + teamName + " "
           + (e.IsOffense ? "攻撃伝令 " : "守備伝令 ") + (e.Messenger?.Name ?? "記録員");
}
