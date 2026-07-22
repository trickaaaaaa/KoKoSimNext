using KokoSim.Engine.Core;
using KokoSim.Engine.Match.Field;
using KokoSim.Engine.Players;

namespace KokoSim.Engine.Nation.Roster;

/// <summary>
/// 怪物（Phenom, Q20）のパッケージ適用。尖り型は「主軸能力S帯＋同系統の支持能力を70〜80帯へ底上げ」を
/// パッケージで載せる（単発1能力S化では怪物感が出ないため, Q20 §3）。総合型は主要能力を高帯へ＝S級。
/// 主軸／支持の「どの能力か」は enum 構造なのでここ（コード）に置き、帯・確率はYAML係数（不変条件#4）。
/// 決定論: 引き上げ値は渡された（Fork済み）rng の純関数。既に高い値は下げない（max を取る）。
/// </summary>
public static class PhenomPackages
{
    /// <summary>
    /// 新入生コホート1つに怪物が現れるか・その種別をロールする（学校×年ごと・全4000校一様, Q20 §2）。
    /// 総合型を先に判定（稀）→ 尖り型 → None。渡す rng は (校ID,入学年度) から Fork した専用ストリーム。
    /// </summary>
    public static PhenomType Roll(PhenomCoefficients c, IRandomSource rng)
    {
        if (rng.NextDouble() < c.AllRoundRatePerSchoolYear) return PhenomType.AllRound;
        if (rng.NextDouble() >= c.SpikeRatePerSchoolYear) return PhenomType.None;

        // 尖り型5種を重みで抽選。
        var w = new[] { c.AceWeight, c.FinesseWeight, c.SluggerWeight, c.SpeedsterWeight, c.StrongArmWeight };
        var total = w[0] + w[1] + w[2] + w[3] + w[4];
        var pick = rng.NextDouble() * (total <= 0 ? 1.0 : total);
        var acc = 0.0;
        for (var i = 0; i < w.Length; i++)
        {
            acc += w[i];
            if (pick < acc) return i switch
            {
                0 => PhenomType.Ace,
                1 => PhenomType.Finesse,
                2 => PhenomType.Slugger,
                3 => PhenomType.Speedster,
                _ => PhenomType.StrongArm,
            };
        }
        return PhenomType.StrongArm;
    }

    /// <summary>この種別が投手に付くか（Ace/Finesse＝投手・総合型は投手にも野手にも付き得る）。</summary>
    public static bool IsPitcherType(PhenomType t) => t is PhenomType.Ace or PhenomType.Finesse;

    /// <summary>この種別が捕手/外野に付くか（鉄砲肩）。</summary>
    public static bool IsThrowerType(PhenomType t) => t is PhenomType.StrongArm;

    /// <summary>
    /// パッケージを適用して怪物選手を返す（<see cref="Player.Phenom"/> を立て、主軸S帯＋支持底上げ）。
    /// 投手系は <see cref="PitcherAttributes"/>（球速km/h・スタミナ球数）も帯へ引き上げる。
    /// </summary>
    public static Player Apply(Player p, PhenomType type, PhenomCoefficients c, IRandomSource rng)
    {
        if (type == PhenomType.None) return p;
        var result = p with { Phenom = type };
        return type switch
        {
            PhenomType.Ace => WithPitcher(result, c, rng, mainVelocity: true, mainControl: false,
                supportPitchRank: true, supportStamina: true),
            PhenomType.Finesse => WithPitcher(result, c, rng, mainVelocity: false, mainControl: true,
                supportPitchRank: true, supportStamina: false),
            PhenomType.Slugger => result with
            {
                Power = Main(result.Power, c, rng),
                Contact = Support(result.Contact, c, rng),
                LaunchTendency = Support(result.LaunchTendency, c, rng),   // 弾道高め
            },
            PhenomType.Speedster => result with
            {
                Speed = Main(result.Speed, c, rng),
                Contact = Support(result.Contact, c, rng),
                Steal = Support(result.Steal, c, rng),
                Baserunning = Support(result.Baserunning, c, rng),
            },
            PhenomType.StrongArm => result with
            {
                ArmStrength = Main(result.ArmStrength, c, rng),
                ThrowAccuracy = Support(result.ThrowAccuracy, c, rng),
                Fielding = Support(result.Fielding, c, rng),
                Catching = Support(result.Catching, c, rng),
            },
            PhenomType.AllRound => ApplyAllRound(result, c, rng),
            _ => result,
        };
    }

    /// <summary>総合型（S級）: 投手なら投手能力群を、野手なら打撃＋守備の主要能力を高帯へ。</summary>
    private static Player ApplyAllRound(Player p, PhenomCoefficients c, IRandomSource rng)
    {
        int Hi(int cur) => System.Math.Max(cur, rng.NextInt(c.AllRoundMin, c.AllRoundMax + 1));
        if (p.Pitching is { } a)
        {
            var velLevel = System.Math.Max(PitcherAttributes.LevelFromVelocityKmh(a.MaxVelocityKmh),
                rng.NextInt(c.AllRoundMin, c.AllRoundMax + 1));
            var staLevel = System.Math.Max(PitcherAttributes.LevelFromStaminaPitches(a.StaminaPitches),
                rng.NextInt(c.AllRoundMin, c.AllRoundMax + 1));
            return p with
            {
                Pitching = a with
                {
                    MaxVelocityKmh = PitcherAttributes.VelocityKmhFromLevel(velLevel),
                    Control = Hi(a.Control),
                    PitchRank = Hi(a.PitchRank),
                    StaminaPitches = PitcherAttributes.StaminaPitchesFromLevel(staLevel),
                },
            };
        }
        return p with
        {
            Contact = Hi(p.Contact),
            Power = Hi(p.Power),
            Speed = Hi(p.Speed),
            ArmStrength = Hi(p.ArmStrength),
            Fielding = Hi(p.Fielding),
            Discipline = Hi(p.Discipline),
        };
    }

    private static Player WithPitcher(Player p, PhenomCoefficients c, IRandomSource rng,
        bool mainVelocity, bool mainControl, bool supportPitchRank, bool supportStamina)
    {
        if (p.Pitching is not { } a) return p;   // 投手系怪物は投手にのみ適用（保険）
        var velLevel = PitcherAttributes.LevelFromVelocityKmh(a.MaxVelocityKmh);
        var staLevel = PitcherAttributes.LevelFromStaminaPitches(a.StaminaPitches);
        if (mainVelocity) velLevel = System.Math.Max(velLevel, rng.NextInt(c.MainMin, c.MainMax + 1));
        if (supportStamina) staLevel = System.Math.Max(staLevel, rng.NextInt(c.SupportMin, c.SupportMax + 1));
        return p with
        {
            Pitching = a with
            {
                MaxVelocityKmh = PitcherAttributes.VelocityKmhFromLevel(velLevel),
                Control = mainControl ? Main(a.Control, c, rng) : a.Control,   // 制球は学校準拠（剛腕はノーコン味）
                PitchRank = supportPitchRank ? Support(a.PitchRank, c, rng) : a.PitchRank,
                StaminaPitches = PitcherAttributes.StaminaPitchesFromLevel(staLevel),
            },
        };
    }

    private static int Main(int cur, PhenomCoefficients c, IRandomSource rng)
        => System.Math.Max(cur, rng.NextInt(c.MainMin, c.MainMax + 1));

    private static int Support(int cur, PhenomCoefficients c, IRandomSource rng)
        => System.Math.Max(cur, rng.NextInt(c.SupportMin, c.SupportMax + 1));
}
