using System.Collections.Generic;
using System.Linq;
using KokoSim.Engine.Core;
using KokoSim.Engine.Match.Field;
using KokoSim.Engine.Nation;
using KokoSim.Engine.Players;
using KokoSim.Engine.Season;

namespace KokoSim.Engine.Nation.Roster;

/// <summary>
/// AI校の永続ロスターの生成（設計書 OPEN-QUESTIONS Q19/Q20 / #80/#82）。新入生コホートを
/// (校ID, 入学年度) シードで決定論生成し、怪物（Phenom）をロールする。セーブ開始時の3学年
/// ブートストラップもここが担う（<see cref="StrengthTeamFactory.ForSchool"/> の使い捨て生成を置換）。
/// </summary>
public static class AiRosterFactory
{
    // 守備8位置（各コホートに1人ずつ＝2学年でベンチ20のポジション網羅を保証）。
    private static readonly FieldPosition[] FieldSlots =
    {
        FieldPosition.Catcher, FieldPosition.FirstBase, FieldPosition.SecondBase,
        FieldPosition.ThirdBase, FieldPosition.Shortstop, FieldPosition.LeftField,
        FieldPosition.CenterField, FieldPosition.RightField,
    };

    /// <summary>(校ID, 入学年度) から新入生コホートの生成シードを導く（決定論・不変条件#2）。ForSchool とは別ソルト。</summary>
    public static IRandomSource CohortSeed(int schoolId, int enrollmentYearIndex)
        => new Xoshiro256Random(0x0C0F_0000UL ^ (ulong)(long)schoolId ^ ((ulong)(long)enrollmentYearIndex * 0x9E37_79B9UL));

    /// <summary>
    /// 新入生コホート（1学年ぶん・全員 Grade=1）を生成する。IDは <paramref name="nextPlayerId"/> から連番で払い出し、
    /// 払い出し後の値を更新して返す。怪物は学校×年ごとにロールし、種別に合う1名へパッケージを適用する。
    /// </summary>
    public static IReadOnlyList<AiPlayer> NewcomerCohort(
        School school, int enrollmentYearIndex, ref int nextPlayerId, AiRosterDeps deps)
    {
        var rng = CohortSeed(school.Id, enrollmentYearIndex);
        var pc = deps.Persistent;
        var center = RecruitCenter(school, pc);

        var usedGiven = new HashSet<string>();
        var players = new List<(AiPlayer Ai, bool IsPitcher)>();
        var profileSalt = 0x51E1_0000UL;
        var salt = 0x50A1_0000UL;
        var archSalt = 0x7B25_0000UL;
        var nameSalt = 0x4E27_0000UL;

        // 野手8人（守備位置1人ずつ）。
        foreach (var pos in FieldSlots)
        {
            var name = PlayerNameGenerator.Generate(deps.NameVocab, rng.Fork(nameSalt++), usedGiven);
            var pr = StrengthTeamFactory.Profile(deps.Roster, rng, ref profileSalt, starter: true);
            var snap = StrengthTeamFactory.PositionPlayer(pos, center, rng, deps.Personality, salt++, name)
                with { Grade = 1, Throws = pr.Throws, Bats = pr.Bats };
            var ai = new AiPlayer(nextPlayerId++, enrollmentYearIndex, pos, PhenomType.None, 1, snap);
            players.Add((ai, false));
        }

        // 投手 N人。
        for (var i = 0; i < pc.PitchersPerCohort; i++)
        {
            var name = PlayerNameGenerator.Generate(deps.NameVocab, rng.Fork(nameSalt++), usedGiven);
            var pr = StrengthTeamFactory.Profile(deps.Roster, rng, ref profileSalt, starter: i == 0);
            var snap = StrengthTeamFactory.Pitcher(name, center, rng, deps.Personality, salt++, deps.Roster.Archetypes, archSalt++)
                with { Grade = 1, Throws = pr.Throws, Bats = pr.Bats };
            var ai = new AiPlayer(nextPlayerId++, enrollmentYearIndex, FieldPosition.Pitcher, PhenomType.None, 1, snap);
            players.Add((ai, true));
        }

        // 怪物ロール（学校×年ごと・全4000校一様, Q20 §2）。種別に合う1名へパッケージ適用。
        var phenomRng = rng.Fork(0x9E01_0000UL);
        var phenomType = PhenomPackages.Roll(pc.Phenom, phenomRng);
        if (phenomType != PhenomType.None)
        {
            var idx = PickPhenomMember(players, phenomType, phenomRng);
            if (idx >= 0)
            {
                var (ai, isP) = players[idx];
                var grown = PhenomPackages.Apply(ai.Snapshot, phenomType, pc.Phenom, phenomRng);
                players[idx] = (new AiPlayer(ai.Id, ai.EnrollmentYearIndex, ai.Position, phenomType, 1, grown), isP);
            }
        }

        // SourceId（成績帰属キー）を確定した Id で焼き込む。
        foreach (var (ai, _) in players)
            ai.Snapshot = ai.Snapshot with { SourceId = ai.Id };

        return players.Select(p => p.Ai).ToList();
    }

    /// <summary>
    /// セーブ開始時の3学年ブートストラップ（設計書 Q19 §5 / Q20 §4 遡及適用）。入学年度が異なる3コホート
    /// （現在=1年・−1=2年・−2=3年）を生成し、上級生には過年度ぶんの成長を適用してロスターを組む。
    /// これで初年度から2・3年生（怪物含む）が存在し得る。純関数（(校ID, 現在年度) だけで決まる）。
    /// </summary>
    public static AiSchoolRoster Bootstrap(School school, int currentYearIndex, AiRosterDeps deps)
    {
        var nextId = 1;
        var roster = new AiSchoolRoster(school.Id, System.Array.Empty<AiPlayer>(), nextId);

        // 古いコホートから順に加入させ、加入後に「その学年へ上がるまでの成長」を適用する。
        for (var grade = 3; grade >= 1; grade--)
        {
            var enrollment = currentYearIndex - (grade - 1);
            var cohort = NewcomerCohort(school, enrollment, ref nextId, deps);
            foreach (var ai in cohort)
            {
                ai.Grade = grade;
                ai.Snapshot = ai.Snapshot with { Grade = grade };
                roster.Add(ai);
            }
        }
        roster.NextPlayerId = nextId;

        // 上級生の過年度成長を逆算配分でならす（ブートストラップ直後にチーム総合が Strength 近傍へ来るように）。
        AiRosterCycle.SettleToTarget(roster, school, currentYearIndex, deps);
        return roster;
    }

    /// <summary>新入生の能力中心（未熟スタート＝Strength − FreshmanGap ＋ 名声上振れ）。</summary>
    internal static double RecruitCenter(School school, PersistentRosterCoefficients c)
        => MathUtil.Clamp(school.Strength - c.FreshmanGap + (school.Fame - 50.0) * c.FameRecruitWeight, 10.0, 95.0);

    /// <summary>怪物種別に合うコホート成員を選ぶ（投手系→投手・鉄砲肩→捕手・打撃系→野手）。見つからねば −1。</summary>
    private static int PickPhenomMember(
        List<(AiPlayer Ai, bool IsPitcher)> players, PhenomType type, IRandomSource rng)
    {
        bool Match((AiPlayer Ai, bool IsPitcher) p) => type switch
        {
            PhenomType.Ace or PhenomType.Finesse => p.IsPitcher,
            PhenomType.StrongArm => p.Ai.Position is FieldPosition.Catcher or FieldPosition.LeftField
                or FieldPosition.CenterField or FieldPosition.RightField,
            PhenomType.Slugger or PhenomType.Speedster => !p.IsPitcher,
            PhenomType.AllRound => true,
            _ => false,
        };
        var candidates = new List<int>();
        for (var i = 0; i < players.Count; i++)
            if (Match(players[i])) candidates.Add(i);
        if (candidates.Count == 0) return -1;
        return candidates[rng.NextInt(0, candidates.Count)];
    }
}
