namespace KokoSim.Engine.Season;

/// <summary>選手2名比較の2タブ（issue #192）。野手能力＝打撃＋走塁＋守備、投手能力＝球速系。</summary>
public enum CompareTab
{
    Fielder = 0,
    Pitcher = 1,
}

/// <summary>
/// メンバー設定／スタメン決定の選手比較で、タブ→能力(<see cref="AbilityKind"/>)の割当を単一ソース化する
/// （issue #192）。Unity側の比較モデル（KokoSim.Unity.Shell.PlayerCompareTabs）はこれにラベル・
/// Lead/Mental（AbilityKind外の2項目）を足して表示行を組む。
/// </summary>
public static class AbilityCompareTabs
{
    /// <summary>野手能力タブの AbilityKind（打撃5＋走塁3＋守備4）。表示順を保持。</summary>
    public static readonly AbilityKind[] FielderAbilities =
    {
        AbilityKind.Contact, AbilityKind.Power, AbilityKind.LaunchTendency, AbilityKind.Discipline,
        AbilityKind.Speed, AbilityKind.Bunt, AbilityKind.Steal, AbilityKind.Baserunning,
        AbilityKind.Fielding, AbilityKind.Catching, AbilityKind.ArmStrength, AbilityKind.ThrowAccuracy,
    };

    /// <summary>投手能力タブの AbilityKind（<see cref="AbilityKinds.Pitching"/> と同一）。</summary>
    public static readonly AbilityKind[] PitcherAbilities = AbilityKinds.Pitching;

    public static AbilityKind[] AbilitiesFor(CompareTab tab) => tab == CompareTab.Fielder ? FielderAbilities : PitcherAbilities;
}
