using System;
using System.Collections.Generic;
using KokoSim.Engine.Core;
using KokoSim.Engine.Match.Field;
using KokoSim.Engine.Nation.Roster;
using KokoSim.Engine.Players;
using KokoSim.Engine.Season;

namespace KokoSim.Engine.Career;

/// <summary>
/// 転任（設計書04 §1.3・issue #641）で部員を学校に残すための変換。部員は監督でなく<b>学校</b>に所属する:
/// 旧校の部員は AI校の永続ロスター（<see cref="AiPlayer"/>）へ、転任先 AI校の部員は自校の育成選手
/// （<see cref="DevelopingPlayer"/>）へ写す。
///
/// <para><b>2つの型は持つ情報の量が違う</b>。AI校の選手が持つのは「いまの能力（試合用スナップショット）・
/// 適性・怪物種別・学年・入学年度」だけで、才能上限・成長タイプ・性格・練習効率・経験値は持たない。</para>
/// <list type="bullet">
/// <item>自校→AI校: 能力は試合投影（<see cref="RosterTeamBuilder.ToPlayer"/>）の値をそのまま写す。
/// 育成側の隠し情報は捨てる（AI校の成長は逆算配分で、才能上限を使わない）。</item>
/// <item>AI校→自校: 能力は逆変換で育成レベルへ戻す。持っていない情報は<b>決定論で推定</b>する
/// （2026-10-10 ユーザー決定）。才能上限＝いまの能力＋新入生と同じ伸びしろ分布×残り学年の割合、
/// 成長タイプ・性格・練習効率・分野別伸び・怪我耐性は新入生と同じ分布から、(校ID, 選手ID) で決まる
/// 専用ストリームで引く（同じセーブなら同じ結果・他の抽選列を乱さない＝不変条件#2）。</item>
/// </list>
/// </summary>
public static class TransferRosterConversion
{
    /// <summary>推定に使う乱数ストリームの塩（他の Fork と衝突しない上位ビット）。</summary>
    private const ulong ConversionSalt = 0x7A4F_0000_0000UL;

    // ===== 自校 → AI校 =====

    /// <summary>
    /// 旧校の在籍部員を AI校の永続ロスターへ写す。選手IDは自校の安定IDをそのまま使う
    /// （監督のキャリア全体で一意＝校内でも一意。教え子の成績を (校ID, 選手ID) で引き続けられる）。
    /// 引退済み（夏で引退した3年）は渡さない前提（<see cref="RosterLifecycle.Active"/> を渡す）。
    /// </summary>
    public static AiSchoolRoster ToAiRoster(int schoolId, IReadOnlyList<DevelopingPlayer> active, int yearIndex)
    {
        var players = new List<AiPlayer>(active.Count);
        var maxId = 0;
        foreach (var dp in active)
        {
            players.Add(ToAiPlayer(dp, yearIndex));
            maxId = Math.Max(maxId, dp.Id);
        }
        return new AiSchoolRoster(schoolId, players, maxId + 1);
    }

    /// <summary>
    /// 育成選手1人を AI校の選手へ。スナップショットは試合投影と同じ値（怪我・スランプの一時補正は外す）。
    /// 守備力だけは<b>適性を掛ける前の地力</b>を置く（AI校は配置のたびに適性を掛ける＝<see cref="AiTeamBuilder"/>）。
    /// 逸材フラグは AI校の怪物種別に対応する概念が無いので写さない（怪物は一芸型/総合型の生成時パッケージ）。
    /// </summary>
    public static AiPlayer ToAiPlayer(DevelopingPlayer dp, int yearIndex)
    {
        var healthy = dp.Clone();
        healthy.Injury = InjurySeverity.None;
        healthy.InjuryType = InjuryType.None;
        healthy.InjuryWeeksRemaining = 0;
        healthy.SlumpWeeks = 0;

        var aptitudes = new int[Enum.GetValues(typeof(FieldPosition)).Length];
        foreach (var pos in AptitudeRoller.AllPositions) aptitudes[(int)pos] = dp.Aptitude(pos);

        var pos0 = PitcherRoleResolver.LeansPitcher(dp) ? FieldPosition.Pitcher : AiRosterFactory.BestFieldPosition(aptitudes);
        var snap = RosterTeamBuilder.ToPlayer(healthy, pos0) with
        {
            Fielding = dp.Level(AbilityKind.Fielding),
            UniformNumber = 0,   // 背番号は AI校側が毎回振る（AiUniformNumbers）
            SourceId = dp.Id,
            Condition = Condition.Normal,
            ConditionValue = 0,
        };
        return new AiPlayer(dp.Id, yearIndex - (dp.Grade - 1), aptitudes, PhenomType.None, dp.Grade, snap);
    }

    // ===== AI校 → 自校 =====

    /// <summary>
    /// 転任先 AI校の在籍者を自校の育成選手へ。<paramref name="nextId"/> から自校の安定IDを振り
    /// （<see cref="PlayerIdCounter"/> と同じ単調増加カウンタ）、続き番号を返す。並びは学年の高い順・
    /// 校内ID順で安定。背番号は能力順で仮割当し、主将を立てる（新規ゲームの生成と同じ後処理）。
    /// </summary>
    public static (List<DevelopingPlayer> Players, int NextId) ToManagerRoster(
        AiSchoolRoster roster, int nextId, ulong masterSeed, RosterCoefficients? c = null,
        PersonalityCoefficients? personalities = null)
    {
        var coeff = c ?? new RosterCoefficients();
        var pc = personalities ?? new PersonalityCoefficients();
        var ordered = new List<AiPlayer>(roster.Players);
        ordered.Sort((a, b) =>
        {
            var g = b.Grade.CompareTo(a.Grade);
            return g != 0 ? g : a.Id.CompareTo(b.Id);
        });

        var list = new List<DevelopingPlayer>(ordered.Count);
        foreach (var ai in ordered)
        {
            var rng = new Xoshiro256Random(masterSeed).Fork(
                ConversionSalt ^ ((ulong)(uint)roster.SchoolId << 16) ^ (ulong)(uint)ai.Id);
            list.Add(ToDevelopingPlayer(ai, rng, coeff, pc));
        }
        var next = PlayerIdCounter.AssignSequential(list, nextId);
        UniformNumberAssigner.AutoAssign(list);
        CaptainSelector.EnsureCaptain(list);
        return (list, next);
    }

    /// <summary>
    /// AI校の選手1人を育成選手へ（ID は呼び出し側が振る）。能力は試合スナップショットの逆変換
    /// （球速は km/h→Lv、スタミナは目安球数→Lv、変化球は球種ランクからのオフセット）。
    /// 推定する隠し情報は <paramref name="rng"/> だけから引く。
    /// </summary>
    public static DevelopingPlayer ToDevelopingPlayer(
        AiPlayer ai, IRandomSource rng, RosterCoefficients c, PersonalityCoefficients pc)
    {
        var s = ai.Snapshot;
        var growth = ProspectGenerator.SampleGrowthType(rng);
        var personality = pc.Sample(rng);
        var profile = pc.Profile(personality);
        double GrowthRoll() => MathUtil.Clamp(
            rng.NextGaussian(c.GrowthMean, c.GrowthSd) + (growth == GrowthType.Late ? c.LateGrowthBonus : 0),
            c.GrowthMin, c.GrowthMax);

        var dp = new DevelopingPlayer
        {
            Name = s.Name,
            Grade = ai.Grade,
            GrowthType = growth,
            IsProdigy = ai.Phenom != PhenomType.None,
            Throws = s.Throws,
            Bats = s.Bats,
            PitchingGrowth = GrowthRoll(),
            BattingGrowth = GrowthRoll(),
            DefenseGrowth = GrowthRoll(),
            PersonalityFactor = MathUtil.Clamp(
                profile.SelfGrowthFactor + rng.NextGaussian(0, pc.FactorJitterSd), pc.FactorMin, pc.FactorMax),
            Personality = personality,
            Mental = s.Mental,
            Leadership = s.Leadership,
            Lead = s.Lead,
            InjuryResistance = MathUtil.Clamp(rng.NextGaussian(50, 15), 10, 90),
            Skills = s.Skills,
        };
        foreach (var pos in AptitudeRoller.AllPositions) dp.SetAptitude(pos, ai.Aptitude(pos));

        // 残り学年の割合で伸びしろを縮める（新入生の伸びしろは3年ぶん。3年生は1/3しか残っていない）。
        var remaining = MathUtil.Clamp((4 - ai.Grade) / 3.0, 1.0 / 3.0, 1.0);
        int CapFor(AbilityKind k, int level)
        {
            var gapFactor = c.CapGapFactor(k) * remaining;
            var gap = rng.NextGaussian(c.CapGapMean * gapFactor, c.CapGapSd * gapFactor)
                      + (growth == GrowthType.Late ? c.LateCapBonus * remaining : 0);
            var rounded = Math.Round(level + gap);
            return k == AbilityKind.Stamina
                ? (int)Math.Max(level + 2, rounded)
                : (int)MathUtil.Clamp(rounded, level + 2, 99);
        }
        void Set(AbilityKind k, int level)
        {
            var lv = k == AbilityKind.Stamina ? Math.Max(1, level) : (int)MathUtil.Clamp(level, 1, 99);
            dp.SetLevel(k, lv);
            dp.SetCap(k, CapFor(k, lv));
        }

        Set(AbilityKind.Contact, s.Contact);
        Set(AbilityKind.Power, s.Power);
        Set(AbilityKind.Discipline, s.Discipline);
        Set(AbilityKind.Speed, s.Speed);
        Set(AbilityKind.ArmStrength, s.ArmStrength);
        Set(AbilityKind.Fielding, s.Fielding);
        Set(AbilityKind.Catching, s.Catching);
        Set(AbilityKind.Bunt, s.Bunt);
        Set(AbilityKind.Steal, s.Steal);
        Set(AbilityKind.Baserunning, s.Baserunning);
        Set(AbilityKind.ThrowAccuracy, s.ThrowAccuracy);
        // 弾道は型属性（育成で伸ばさない）＝上限は現在値。
        dp.SetLevel(AbilityKind.LaunchTendency, s.LaunchTendency);
        dp.SetCap(AbilityKind.LaunchTendency, s.LaunchTendency);

        var pit = s.Pitching ?? PitcherAttributes.LeagueAverage;
        Set(AbilityKind.Velocity, PitcherAttributes.LevelFromVelocityKmh(pit.MaxVelocityKmh));
        Set(AbilityKind.Control, pit.Control);
        Set(AbilityKind.Stamina, PitcherAttributes.LevelFromStaminaPitches(pit.StaminaPitches));
        Set(AbilityKind.PitchRank, pit.PitchRank);
        var repertoire = pit.Repertoire ?? Array.Empty<PitchSlot>();
        var fastball = FindFastball(repertoire);
        Set(AbilityKind.Stuff, fastball?.Power ?? pit.PitchRank);
        foreach (var slot in repertoire)
        {
            if (slot.Type == PitchType.Fastball) continue;
            dp.LearnedPitches.Add(new LearnedPitch(slot.Type, slot.Power - pit.PitchRank, slot.Sharpness - pit.PitchRank));
        }

        // 精神系の上限も同じ流儀（新入生は現在値＋gap、下限は現在値＋2）。
        dp.MentalCap = (int)MathUtil.Clamp(Math.Round(s.Mental + rng.NextGaussian(c.CapGapMean * remaining, c.CapGapSd * remaining)), s.Mental + 2, 99);
        dp.LeadCap = (int)MathUtil.Clamp(Math.Round(s.Lead + rng.NextGaussian(c.CapGapMean * remaining, c.CapGapSd * remaining)), s.Lead + 2, 99);
        dp.ConditionValue = s.ConditionValue;
        return dp;
    }

    private static PitchSlot? FindFastball(IReadOnlyList<PitchSlot> repertoire)
    {
        foreach (var slot in repertoire) if (slot.Type == PitchType.Fastball) return slot;
        return null;
    }
}
