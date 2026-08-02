using System.Collections.Generic;
using KokoSim.Engine.Match.Game;
using KokoSim.Engine.Players;
using KokoSim.Engine.Season;

namespace KokoSim.Engine.Nation;

/// <summary>
/// 実体化済みの相手校（<see cref="StrengthTeamFactory.ForSchool"/> が返す <see cref="Team"/>）から
/// 6指標の <see cref="TeamStrength"/> を求める。自校と同じ <see cref="TeamStrengthProfile"/> を通すため、
/// レーダーの軸の意味とスケールが自校と完全に一致する（練習試合の相手選択で並べて比較できる）。
/// 乱数を使わない純関数（不変条件#2）。
/// </summary>
public static class ScoutedTeamProfile
{
    /// <summary>実体化済みチームの6指標。</summary>
    public static TeamStrength Compute(Team team, TeamStrengthCoefficients c)
        => TeamStrengthProfile.Compute(ToRoster(team), c);

    /// <summary>
    /// 試合層の <see cref="Player"/> を集計用の <see cref="DevelopingPlayer"/> へ写す（表示能力の逆投影）。
    /// 球速・スタミナは物理量で保持されているので <see cref="PitcherAttributes"/> の逆変換で Level に戻す。
    /// </summary>
    public static IReadOnlyList<DevelopingPlayer> ToRoster(Team team)
    {
        var roster = new List<DevelopingPlayer>(team.BattingOrder.Count + team.Reserves.Count);
        foreach (var p in team.BattingOrder) roster.Add(ToDeveloping(p));
        foreach (var p in team.Reserves) roster.Add(ToDeveloping(p));
        if (team.StartingPitcher is { } sp) roster.Add(ToDeveloping(sp));
        return roster;
    }

    private static DevelopingPlayer ToDeveloping(Player p)
    {
        var dp = new DevelopingPlayer
        {
            Name = p.Name,
            Grade = p.Grade,
            Mental = p.Mental,
            Leadership = p.Leadership,
        };
        // 投影済み Player は守備位置適性を失っているので、逆投影では既知の配置（投手として置かれているか）から
        // 導出がその役割を返すよう適性を置き直す（#377: 役割を選手に持たせない）。
        // 材料は #381 で「投手能力の有無」から「配置」へ移した。生成経路では両者は常に同値だが、
        // 全選手が投手能力を持つようになった以上、能力の有無は役割を語らない（#366）。
        PitcherRoleResolver.SeedAptitudeForRole(dp, p.Position == Match.Field.FieldPosition.Pitcher);
        dp.SetLevel(AbilityKind.Contact, p.Contact);
        dp.SetLevel(AbilityKind.Power, p.Power);
        dp.SetLevel(AbilityKind.LaunchTendency, p.LaunchTendency);
        dp.SetLevel(AbilityKind.Discipline, p.Discipline);
        dp.SetLevel(AbilityKind.Speed, p.Speed);
        dp.SetLevel(AbilityKind.ArmStrength, p.ArmStrength);
        dp.SetLevel(AbilityKind.ThrowAccuracy, p.ThrowAccuracy);
        dp.SetLevel(AbilityKind.Fielding, p.Fielding);
        dp.SetLevel(AbilityKind.Catching, p.Catching);
        dp.SetLevel(AbilityKind.Bunt, p.Bunt);
        dp.SetLevel(AbilityKind.Steal, p.Steal);
        dp.SetLevel(AbilityKind.Baserunning, p.Baserunning);

        // 投手レベルは全選手ぶん戻す（#381）。6指標の投手力・選手層は上で置いた適性で投手を選び分けるので、
        // 投手として置かれていない選手の投手レベルが入っても <see cref="TeamStrengthProfile"/> の出力は動かない
        // （野手側の AverageLevel は打撃系の中核能力しか見ない）。
        var a = p.Pitching;
        dp.SetLevel(AbilityKind.Velocity, PitcherAttributes.LevelFromVelocityKmh(a.MaxVelocityKmh));
        dp.SetLevel(AbilityKind.Control, a.Control);
        dp.SetLevel(AbilityKind.Stamina, PitcherAttributes.LevelFromStaminaPitches(a.StaminaPitches));
        dp.SetLevel(AbilityKind.PitchRank, a.PitchRank);
        return dp;
    }
}
