namespace KokoSim.Engine.Match.Tactics;

/// <summary>
/// 校風＝采配の個性（設計書11 §3）。同じ局面でも手が変わる。学校属性（設計書05）に確率的に付与。
/// 自チーム（プレイヤー）には校風の偏りは適用しない（設計書11 §7）。
/// </summary>
public enum SchoolStyle
{
    SmallBall,        // 機動力野球: 盗塁・バント・エンドラン多用。1点を積極的に取りに行く
    PowerHitting,     // 強打・待球: 待球して長打狙い。バント少なめ
    DefensiveMinded,  // 守り勝つ野球: 継投・守備固め厚く僅差を守る。手堅い送りバント
    TotalBaseball,    // 全員野球: 代打・代走・継投を小刻みに動かす（層を活かす）
    AceDependent,     // 豪腕依存: エースに託す（継投・伝令が遅い＝諸刃）
    Standard,         // 型なし: 突出した傾向なし。ティア相応の標準采配
}
