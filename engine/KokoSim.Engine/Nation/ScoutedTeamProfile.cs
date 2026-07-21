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
        var roster = new List<DevelopingPlayer>(team.BattingOrder.Count + team.Bullpen.Count + team.Bench.Count);
        foreach (var p in team.BattingOrder) roster.Add(ToDeveloping(p));
        foreach (var p in team.Bullpen) roster.Add(ToDeveloping(p));
        foreach (var p in team.Bench) roster.Add(ToDeveloping(p));
        if (team.StartingPitcher is { } sp) roster.Add(ToDeveloping(sp));
        return roster;
    }

    private static DevelopingPlayer ToDeveloping(Player p)
    {
        var dp = new DevelopingPlayer
        {
            Name = p.Name,
            Grade = p.Grade,
            IsPitcher = p.Pitching is not null,
            Mental = p.Mental,
            Leadership = p.Leadership,
        };
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

        if (p.Pitching is { } a)
        {
            dp.SetLevel(AbilityKind.Velocity, PitcherAttributes.LevelFromVelocityKmh(a.MaxVelocityKmh));
            dp.SetLevel(AbilityKind.Control, a.Control);
            dp.SetLevel(AbilityKind.Stamina, PitcherAttributes.LevelFromStaminaPitches(a.StaminaPitches));
            dp.SetLevel(AbilityKind.PitchRank, a.PitchRank);
        }
        return dp;
    }
}
