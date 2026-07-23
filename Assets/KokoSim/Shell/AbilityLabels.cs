using KokoSim.Engine.Season;

namespace KokoSim.Unity.Shell
{
    /// <summary>
    /// 能力種別（AbilityKind）の日本語表示名の単一ソース（issue #94）。
    /// 画面ごとの直書き（Home/Training/Member/Lineup/PlayerDetail/交代パネル）をここへ集約し、
    /// 表記ゆれ（特に PitchRank＝「キレ」）の再発を防ぐ。用語集（CLAUDE.md）に一致させる：
    /// PitchRank＝キレ（球種ランク）。「球種」は持ち球リスト（LearnedPitches）の意味に取っておく。
    /// </summary>
    public static class AbilityLabels
    {
        public static string Jp(AbilityKind k) => k switch
        {
            AbilityKind.Contact => "ミート",
            AbilityKind.Power => "パワー",
            AbilityKind.LaunchTendency => "弾道",
            AbilityKind.Discipline => "選球眼",
            AbilityKind.Speed => "走力",
            AbilityKind.ArmStrength => "肩",
            AbilityKind.Fielding => "守備",
            AbilityKind.Catching => "捕球",
            AbilityKind.Velocity => "球速",
            AbilityKind.Control => "制球",
            AbilityKind.Stamina => "スタミナ",
            AbilityKind.PitchRank => "キレ",   // 用語集どおり。旧「球種」表記は #94 で廃止。
            AbilityKind.Bunt => "バント",
            AbilityKind.Steal => "盗塁",
            AbilityKind.Baserunning => "走塁",
            AbilityKind.ThrowAccuracy => "送球",
            _ => k.ToString(),
        };
    }
}
