namespace KokoSim.Engine.Players;

/// <summary>
/// 投打の日本語表記（設計書01 §1.1c）。Assets側に散在していた同一ロジックの単一ソース（Issue #140）。
/// </summary>
public static class HandednessLabels
{
    public static string Throws(Handedness throws) => throws == Handedness.Left ? "左投" : "右投";

    public static string Bats(Handedness bats) => bats switch
    {
        Handedness.Left => "左打",
        Handedness.Switch => "両打",
        _ => "右打",
    };

    /// <summary>「右投左打」のような投打まとめ表記。</summary>
    public static string Combined(Handedness throws, Handedness bats) => Throws(throws) + Bats(bats);
}
