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

public static class HandednessExtensions
{
    /// <summary>
    /// 打者の内角側の符号（捕手から見て +x＝一塁側）。右打者は三塁側(−x)に立ち内角も −x、左打者（スイッチは左打席想定）は +x。
    /// 内角攻め・くさい所の縁狙い・死球の窓は、すべてこの符号から出す。
    /// </summary>
    public static double InsideSign(this Handedness bats) => bats == Handedness.Right ? -1.0 : 1.0;
}
