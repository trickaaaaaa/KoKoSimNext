using KokoSim.Engine.Core;
using KokoSim.Engine.Match.Game;
using KokoSim.Engine.Match.Timeline.Playback;

namespace KokoSim.Engine.Nation.Tournaments;

/// <summary>詳細シムの結果（ボックススコア＋自校が先攻/後攻どちらか）。</summary>
public sealed record PlayerMatchDetail(GameResult Result, bool ManagerIsAway);

/// <summary>
/// ライブ観戦用: 監督戦を打席単位で進める進行体＋自校が先攻/後攻か。<see cref="Resolve"/> と同じ
/// teams+rng から作るので、全打席を進めた最終結果は Resolve のボックススコアと一致する（決定論）。
/// </summary>
public sealed record PlayerMatchLive(MatchProgression Progression, bool ManagerIsAway);

/// <summary>
/// 自校の一戦だけを詳細試合エンジン（GameEngine）で解決する継ぎ目。純エンジン（TournamentRunner）を
/// ロスター・スタメン・Unity から疎結合に保つための注入点。実装は Shell 側（自校ロスター＋スタメンを持つ層）。
/// 渡される rng は本流から Fork 済みの隔離ストリーム＝本流の消費に影響しない（背景試合の決定論を保つ）。
/// </summary>
public interface IPlayerMatchResolver
{
    /// <summary>
    /// 自校 manager と相手 opponent の一戦を詳細シムで解決する（自動消化・一括）。
    /// <paramref name="mercyRuleEnabled"/> は大会側（<see cref="TournamentRunner"/>）が大会種別・
    /// ラウンドから決定する試合ルール（設計書05 §1.3, OPEN-QUESTIONS Q18）。BeginLive と必ず同じ値を渡すこと。
    /// </summary>
    PlayerMatchDetail Resolve(School manager, School opponent, IRandomSource rng, bool mercyRuleEnabled);

    /// <summary>
    /// 自校 manager と相手 opponent の一戦を「打席単位でライブ進行」する進行体を作る（観戦用）。
    /// Resolve と同じ teams+rng+mercyRuleEnabled を使うため、采配なしで全打席進めた結果は Resolve と一致する。
    /// </summary>
    PlayerMatchLive BeginLive(School manager, School opponent, IRandomSource rng, bool mercyRuleEnabled);
}
