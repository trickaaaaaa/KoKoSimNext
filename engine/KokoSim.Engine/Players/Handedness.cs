namespace KokoSim.Engine.Players;

/// <summary>
/// 投打の利き（設計書01 §1.1c）。投げ手・打ち手は条件付きで生成する（生成分布は 2B で実装）。
/// 左投げ右打ちは絶滅危惧種、右投げ左打ちは意図的に量産される。スイッチは少数。
/// </summary>
public enum Handedness
{
    Right,
    Left,
    Switch, // 両打ち（打ち手のみ。投げ手には使わない）
}
