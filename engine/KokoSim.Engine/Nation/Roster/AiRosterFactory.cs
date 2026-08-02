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
        var players = new List<AiPlayer>();
        var profileSalt = 0x51E1_0000UL;
        var salt = 0x50A1_0000UL;
        var archSalt = 0x7B25_0000UL;
        var nameSalt = 0x4E27_0000UL;
        var rc = deps.Roster;

        // コホートは「守備8枠＋投手プール」ではなく**同じ生成器を通した N 人**（issue #384）。
        // 一人ひとり守備位置適性をロールし、そこから投手寄せ重み w（打撃の連続補間）と
        // 「投手として育っているか」を導く。誰がどこを守るかは AiTeamBuilder が毎回選抜する
        // ＝選手は配置を持たない（#366）。適性ロールは Fork＝主RNGの消費列を乱さない。
        for (var i = 0; i < pc.CohortSize; i++)
        {
            var aptitudes = AptitudeRoller.Roll(rc, rng.Fork(AptitudeSalt + (ulong)i));
            var pitcherApt = aptitudes[(int)FieldPosition.Pitcher];
            var bestField = AptitudeRoller.BestField(aptitudes);
            // 判定も重みも PitcherRoleResolver に集約された式を使う（自校と同じ係数・同じ分布）。
            // 旧実装は AI校だけ PitcherEmergenceBias=15 という別の下駄を履いていたが、これは
            // 「候補6人プールから選ぶ」構造の歪みを補うためのもので、全員が適性を持つ今は要らない。
            var leansPitcher = PitcherRoleResolver.IsNaturalPitcher(pitcherApt, bestField, rc);
            var weight = PitcherRoleResolver.PitcherWeight(pitcherApt, bestField, rc);

            var name = PlayerNameGenerator.Generate(deps.NameVocab, rng.Fork(nameSalt++), usedGiven);
            var pr = StrengthTeamFactory.Profile(rc, rng, ref profileSalt, starter: i == 0);
            // 打撃は w の連続量（#377）。投手として育っている者だけが投手能力を主RNGから引き、
            // それ以外は肩からの推定で埋まる（#381）＝「全員が投手能力を持つが、投げ慣れた者が上」。
            var snap = leansPitcher
                ? StrengthTeamFactory.Pitcher(name, center, weight, rng, deps.Personality,
                        salt++, rc.Archetypes, archSalt++, rc)
                    with { Grade = 1, Throws = pr.Throws, Bats = pr.Bats }
                : StrengthTeamFactory.PositionPlayer(
                        BestFieldPosition(aptitudes), center, rng, deps.Personality, salt++, name)
                    with { Grade = 1, Throws = pr.Throws, Bats = pr.Bats };
            players.Add(new AiPlayer(nextPlayerId++, enrollmentYearIndex, aptitudes, PhenomType.None, 1, snap));
        }

        // 怪物ロール（学校×年ごと・全4000校一様, Q20 §2）。種別に合う1名へパッケージ適用。
        var phenomRng = rng.Fork(0x9E01_0000UL);
        var phenomType = PhenomPackages.Roll(pc.Phenom, phenomRng);
        if (phenomType != PhenomType.None)
        {
            var idx = PickPhenomMember(players, phenomType, phenomRng, rc);
            if (idx >= 0)
            {
                var ai = players[idx];
                var grown = PhenomPackages.Apply(ai.Snapshot, phenomType, pc.Phenom, phenomRng);
                players[idx] = new AiPlayer(
                    ai.Id, ai.EnrollmentYearIndex, ai.Aptitudes.ToArray(), phenomType, 1, grown);
            }
        }

        // SourceId（成績帰属キー）を確定した Id で焼き込む。
        foreach (var ai in players)
            ai.Snapshot = ai.Snapshot with { SourceId = ai.Id };

        return players;
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

    /// <summary>コホートの適性ロール用 Fork ソルト（他ストリームと別の上位ビット）。</summary>
    private const ulong AptitudeSalt = 0x6F3A_0000UL;

    /// <summary>
    /// 適性が最も高い守備位置（投手を除く8つ）。生成時の <see cref="Player.Position"/> の置き場所と
    /// 怪物の系統判定に使う。<see cref="Player.Position"/> は配置なので <see cref="AiTeamBuilder"/> が
    /// 選抜時に上書きする＝ここで置くのは「まだ選抜を通っていない状態の見た目」でしかない。
    /// </summary>
    internal static FieldPosition BestFieldPosition(IReadOnlyList<int> aptitudes)
    {
        var best = FieldPosition.CenterField;
        var bestValue = int.MinValue;
        foreach (var pos in AptitudeRoller.AllPositions)
        {
            if (pos == FieldPosition.Pitcher) continue;
            if (aptitudes[(int)pos] <= bestValue) continue;
            bestValue = aptitudes[(int)pos];
            best = pos;
        }
        return best;
    }

    /// <summary>新入生の能力中心（未熟スタート＝Strength − FreshmanGap ＋ 名声上振れ）。</summary>
    internal static double RecruitCenter(School school, PersistentRosterCoefficients c)
        => MathUtil.Clamp(school.Strength - c.FreshmanGap + (school.Fame - 50.0) * c.FameRecruitWeight, 10.0, 95.0);

    /// <summary>
    /// 怪物種別に合うコホート成員を選ぶ（投手系→投手向き・鉄砲肩→捕外向き・打撃系→野手向き）。見つからねば −1。
    /// 判定の材料は配置でなく**適性**（issue #384）。旧実装は生成時に固定した Position を見ていたが、
    /// 選手が配置を持たなくなったので「その守備位置が最良適性か」で読み替える。
    /// </summary>
    private static int PickPhenomMember(
        List<AiPlayer> players, PhenomType type, IRandomSource rng, RosterCoefficients rc)
    {
        bool LeansPitcher(AiPlayer p) => PitcherRoleResolver.IsNaturalPitcher(
            p.Aptitude(FieldPosition.Pitcher), AptitudeRoller.BestField(p.Aptitudes), rc);

        bool Match(AiPlayer p) => type switch
        {
            PhenomType.Ace or PhenomType.Finesse => LeansPitcher(p),
            PhenomType.StrongArm => !LeansPitcher(p) && BestFieldPosition(p.Aptitudes)
                is FieldPosition.Catcher or FieldPosition.LeftField
                or FieldPosition.CenterField or FieldPosition.RightField,
            PhenomType.Slugger or PhenomType.Speedster => !LeansPitcher(p),
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
