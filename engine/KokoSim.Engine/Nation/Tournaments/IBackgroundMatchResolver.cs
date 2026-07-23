using KokoSim.Engine.Core;
using KokoSim.Engine.Match.Game;

namespace KokoSim.Engine.Nation.Tournaments;

/// <summary>
/// 裏試合（自校が関与しない全試合）を1球単位フルエンジンで解決する継ぎ目（設計書05 §1.4 / #43）。
/// 集計モデル（<see cref="Nation.AggregateMatch"/>）を廃し、全4000校の試合を同一エンジンで解いて
/// ボックススコアを全国通算成績へ積む。TournamentRunner に注入する（null なら従来の集計モデル）。
/// 渡される rng は本流から Fork 済みの隔離ストリーム＝本流の消費に影響しない（背景試合の決定論を保つ）。
/// </summary>
public interface IBackgroundMatchResolver
{
    /// <summary>
    /// away（先攻）と home（後攻）の1試合をフルシムで解決し、結果を返す（成績畳み込みは実装側）。
    /// <paramref name="context"/> は大会進行コンテキスト（エース温存判断, issue #42。既定 null＝常時エース先発）。
    /// </summary>
    GameResult Resolve(School away, School home, IRandomSource rng, TournamentMatchContext? context = null);
}
