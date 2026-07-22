using System;
using System.Collections.Generic;
using System.Linq;
using KokoSim.Engine.Core;
using KokoSim.Engine.Nation;
using KokoSim.Engine.Players;

namespace KokoSim.Engine.Nation.Roster;

/// <summary>
/// AI校の永続ロスターのライフサイクル駆動（設計書 OPEN-QUESTIONS Q19/Q20 / #80/#82）。
/// 代替わり（4月＝進級・新入生加入・3年引退）と節目成長（年間予算を逆算配分）を扱う。
///
/// 逆算配分（Q19 §1）: 在校生の成長量を「翌夏のチーム総合が学校Strengthに一致する」よう決定論で解く。
/// 選手から見れば自然な成長カーブ、学校から見れば従来どおりの生態系（AiSchoolModel.Evolve と整合）。
/// 怪物（Phenom）は逆算配分の対象外＝上乗せ例外（Q20 §1）。総合力の測定から怪物を除外して解くことで、
/// 怪物在籍校の実チーム総合は Strength を上回る（＝無名校の旋風の創発）。
/// </summary>
public static class AiRosterCycle
{
    /// <summary>
    /// 夏大会後の代替わり: 3年生を引退させ、ロスターから除去する（秋の新チームは2学年＝下級生のみ）。
    /// カレンダーの夏引退週に Shell が呼ぶ。冪等（既に除去済みなら何もしない）。
    /// </summary>
    public static void RetireGraduates(AiSchoolRoster roster) => roster.RemoveWhere(p => p.Grade >= 3);

    /// <summary>ブートストラップ直後、上級生を過年度ぶん成長させてチーム総合を Strength 近傍へ寄せる。</summary>
    public static void SettleToTarget(AiSchoolRoster roster, School school, int yearIndex, AiRosterDeps deps)
        // 全員が新入生中心で生成されているので、grade-1 学年ぶん (grade-1)×g を積んで加齢差を作る。
        => Settle(roster, school, yearIndex, deps, gradeFactor: g => g - 1);

    /// <summary>
    /// 1年分の代替わり＋節目成長を適用する（4月の進級・新入生加入・3年引退 → 逆算配分）。
    /// テストの参照実装（原子操作）。Unity側は同じ結果を年3節目に分割して届ける（Phase C）。
    /// </summary>
    public static void AdvanceOneYear(AiSchoolRoster roster, School school, int newYearIndex, AiRosterDeps deps)
    {
        // 3年生は前年夏で引退済み＝ロスターから除去。
        roster.RemoveWhere(p => p.Grade >= 3);

        // 残りは進級（1→2, 2→3）。Snapshot の表示学年も同期。
        foreach (var p in roster.Players)
        {
            p.Grade++;
            p.Snapshot = p.Snapshot with { Grade = p.Grade };
        }

        // 新入生コホート（Grade=1）を加入。
        var nextId = roster.NextPlayerId;
        var cohort = AiRosterFactory.NewcomerCohort(school, newYearIndex, ref nextId, deps);
        foreach (var ai in cohort) roster.Add(ai);
        roster.NextPlayerId = nextId;

        // 怪物（返り＝進級した2・3年）は逆算配分の対象外だが節目成長には乗る（Q20 §1）＝固定の小成長。
        var pc = deps.Persistent;
        foreach (var p in roster.Players.Where(p => p.Phenom.IsPhenom() && p.Grade >= 2))
        {
            var w = p.Grade == 3 ? pc.SeniorGrowthFactor : 1.0;
            p.Snapshot = BumpTo(p.Snapshot, pc.AnnualGrowth * 0.5 * w);
        }

        // 非怪物の在校生を逆算配分で成長（2年は満額・3年は引退間際で控えめ・新入生は当年成長なし）。
        Settle(roster, school, newYearIndex, deps, gradeFactor: g => g switch
        {
            2 => 1.0,
            3 => pc.SeniorGrowthFactor,
            _ => 0.0,
        });
    }

    /// <summary>
    /// 逆算配分の一般形。非怪物の在校生を <paramref name="gradeFactor"/>×g（g は二分探索で決める強度）だけ
    /// 現スナップショットから成長させ、怪物を除いたチーム総合が school.Strength に一致する g を求める。
    /// </summary>
    private static void Settle(AiSchoolRoster roster, School school, int yearIndex, AiRosterDeps deps,
        Func<int, double> gradeFactor)
    {
        var target = school.Strength;
        var growable = roster.Players
            .Where(p => !p.Phenom.IsPhenom() && p.Grade is >= 1 and <= 3 && gradeFactor(p.Grade) > 0)
            .ToList();
        if (growable.Count == 0) return;

        // 成長前スナップショット（各二分探索イテレーションのベース）。
        var bases = growable.Select(p => (Player: p, Base: p.Snapshot)).ToList();

        void ApplyG(double g)
        {
            foreach (var (p, b) in bases) p.Snapshot = BumpTo(b, gradeFactor(p.Grade) * g);
        }

        // Overall は g に対し単調非減少。g の最大値でも届かなければ上限で打ち切り（強豪はキャップで頭打ち＝許容）。
        double lo = 0.0, hi = 40.0;
        ApplyG(hi);
        if (ProjectedOverall(roster, school, yearIndex, deps) < target) { return; }   // hi 適用済みで確定

        for (var i = 0; i < 26; i++)
        {
            var mid = (lo + hi) / 2;
            ApplyG(mid);
            if (ProjectedOverall(roster, school, yearIndex, deps) < target) lo = mid;
            else hi = mid;
        }
        ApplyG(lo);   // target をわずかに下回る側（オーバーシュートしない）。
    }

    /// <summary>怪物を除いた在校生でベンチ20 Team を組み、6指標の Overall を返す（逆算配分のターゲット尺度）。</summary>
    private static double ProjectedOverall(AiSchoolRoster roster, School school, int yearIndex, AiRosterDeps deps)
    {
        var active = roster.Players.Where(p => p.Grade is >= 1 and <= 3 && !p.Phenom.IsPhenom()).ToList();
        if (active.Count == 0) return school.Strength;
        var team = AiTeamBuilder.BuildFrom(active, school, yearIndex, deps);
        return ScoutedTeamProfile.Compute(team, deps.TeamStrength).Overall;
    }

    /// <summary>スナップショットの中核能力を <paramref name="amount"/> だけ底上げする（弾道傾向・精神は伸ばさない）。</summary>
    internal static Player BumpTo(Player b, double amount)
    {
        var d = (int)Math.Round(amount);
        if (d <= 0) return b;
        int Up(int v) => (int)MathUtil.Clamp(v + d, 10, 99);
        var r = b with
        {
            Contact = Up(b.Contact),
            Power = Up(b.Power),
            Discipline = Up(b.Discipline),
            Speed = Up(b.Speed),
            ArmStrength = Up(b.ArmStrength),
            ThrowAccuracy = Up(b.ThrowAccuracy),
            Fielding = Up(b.Fielding),
            Catching = Up(b.Catching),
            Bunt = Up(b.Bunt),
            Steal = Up(b.Steal),
            Baserunning = Up(b.Baserunning),
            Lead = Up(b.Lead),
        };
        if (b.Pitching is { } a)
        {
            var vl = Up(PitcherAttributes.LevelFromVelocityKmh(a.MaxVelocityKmh));
            var sl = Up(PitcherAttributes.LevelFromStaminaPitches(a.StaminaPitches));
            r = r with
            {
                Pitching = a with
                {
                    MaxVelocityKmh = PitcherAttributes.VelocityKmhFromLevel(vl),
                    Control = Up(a.Control),
                    PitchRank = Up(a.PitchRank),
                    StaminaPitches = PitcherAttributes.StaminaPitchesFromLevel(sl),
                },
            };
        }
        return r;
    }
}
