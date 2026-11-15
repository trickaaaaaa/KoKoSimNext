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
    /// この打席で実際に立つ打席側（issue #1019）。スイッチは投手の逆（右投手→左打席／左投手→右打席）、右・左打者はそのまま。
    /// 打席側の判定式はここにしか置かない＝内角・引っ張り・一塁までの近さ・死球の窓はこの値で見る。
    /// Unity の対決ビュー（絵）も同じ関数を使う。打席の途中で投手が替わったときは新投手の逆へ立ち替える（実物も継投で立ち位置を変えられる）。
    /// </summary>
    public static Handedness EffectiveBats(this Handedness bats, Handedness pitcherThrows)
        => bats == Handedness.Switch
            ? (pitcherThrows == Handedness.Left ? Handedness.Right : Handedness.Left)
            : bats;

    /// <summary>
    /// 左打席に立つか。<see cref="EffectiveBats"/> で解決済みの値を渡す（未解決の <see cref="Handedness.Switch"/> は
    /// 右投手相手＝左打席として扱う）。引っ張り方向・一塁までの近さ・内角の向きはこの1つから出す。
    /// </summary>
    public static bool IsLeftSide(this Handedness bats) => bats != Handedness.Right;

    /// <summary>
    /// 打者の内角側の符号（捕手から見て +x＝一塁側）。右打席は三塁側(−x)に立ち内角も −x、左打席は +x。
    /// <see cref="EffectiveBats"/> で解決した打席側を渡す（スイッチは投手の逆）。
    /// 内角攻め・くさい所の縁狙い・死球の窓は、すべてこの符号から出す。
    /// </summary>
    public static double InsideSign(this Handedness bats) => bats.IsLeftSide() ? 1.0 : -1.0;
}
