using System;
using System.Collections.Generic;
using KokoSim.Engine.Core;
using KokoSim.Engine.Match.Tactics;
using KokoSim.Engine.Players;
using KokoSim.Engine.Season;

namespace KokoSim.Engine.Nation.Roster;

/// <summary>
/// AI校の精神力・統率傾向・捕手リードの生成と学年アーク（issue ai-school-mental-fixed-50, 2026-08-18）。
///
/// <para>旧実装は AI校の選手（<see cref="StrengthTeamFactory.CreatePlayer"/>＝使い捨て生成と永続ロスターの両経路）が
/// Mental／Leadership／Lead を一度も振らず <see cref="Player"/> の既定値 50 のままで、主将もいなかった。
/// プレッシャー補正・動揺しきい値・盗塁の読み・伝令判断・主将の緩和・相手校6指標「精神」（常に D）が全校で
/// 同じ「平均・一定」になっていた（自校だけ個体差と時間アークがある非対称）。</para>
///
/// <para>ここが AI 側の分布の<b>単一ソース</b>: 中心は自校の <see cref="RosterCoefficients.MentalMean"/> 等と同じ既定を
/// 学校の強さ（圧縮前）と校風で寄せ、σ は自校と同じ。乱数は呼び出し側が渡す<b>専用 Fork</b>（性格 salt・潜在投手 salt と
/// 別ストリーム）で引くので主RNGの消費列を変えない＝他の能力ロールは変更前後で1ビットも動かない。</para>
/// </summary>
public static class AiMentalModel
{
    /// <summary>精神力用の Fork ソルト（性格 salt / <c>UntrainedPitchingSalt</c> と衝突しない上位ビット）。</summary>
    internal const ulong MentalSalt = 0x3E17_0000_0000_0000UL;

    /// <summary>
    /// 精神力中心。<paramref name="schoolStrength"/> は<b>圧縮前</b>の <see cref="School.Strength"/>
    /// （ティア圧縮は身体・技術の下位校救済であって精神の物差しではない）。校風「守り勝つ」「全員野球」は上乗せ。
    /// </summary>
    public static double MentalCenter(double schoolStrength, SchoolStyle? style, RosterCoefficients rc)
        => rc.MentalMean + (schoolStrength - 50.0) * rc.AiMentalStrengthSlope + StyleBonus(style, rc);

    /// <summary>校風の精神・統率上乗せ（守り勝つ／全員野球だけ。design-11 三層プロファイルの精神の項）。</summary>
    public static double StyleBonus(SchoolStyle? style, RosterCoefficients rc)
        => style is SchoolStyle.DefensiveMinded or SchoolStyle.TotalBaseball ? rc.AiStyleMentalBonus : 0.0;

    /// <summary>
    /// 精神力・統率傾向・捕手リードを引く（新入生＝学年アーク前の値）。式は自校の
    /// <see cref="ProspectGenerator"/>（精神 N(mean,sd)／統率 N(mean,sd)＋性格オフセット／リード＝中心＋野球脳相関＋個体差）
    /// と同じ。<paramref name="fork"/> は専用ストリームで、ここで 3 回だけ引く（消費数固定＝決定論）。
    /// </summary>
    public static (int Mental, int Leadership, int Lead) Roll(
        double mentalCenter, double styleBonus, PersonalityProfile personality, RosterCoefficients rc, IRandomSource fork)
    {
        var mental = (int)MathUtil.Clamp(Math.Round(fork.NextGaussian(mentalCenter, rc.MentalSd)), 1, 99);
        var leadership = (int)MathUtil.Clamp(
            Math.Round(fork.NextGaussian(rc.LeadershipMean + styleBonus, rc.LeadershipSd) + personality.LeadershipMeanOffset), 1, 99);
        var lead = (int)MathUtil.Clamp(
            Math.Round(rc.LeadMean + (mental - 50) * rc.LeadMentalCorr + fork.NextGaussian(0, rc.LeadSd)), 1, 99);
        return (mental, leadership, lead);
    }

    /// <summary>1学年ぶんの精神力アーク（進級時に呼ぶ）。</summary>
    public static Player Promote(Player p, RosterCoefficients rc) => WithGradeArc(p, 2, rc);

    /// <summary>
    /// 新入生の値に (grade−1) 学年ぶんのアークを載せる（使い捨て生成の上級生・ブートストラップの上級生用）。
    /// grade≤1 は恒等。
    /// </summary>
    public static Player WithGradeArc(Player p, int grade, RosterCoefficients rc)
    {
        var years = Math.Max(0, grade - 1);
        if (years == 0) return p;
        var d = (int)Math.Round(rc.AiMentalPerGrade * years);
        if (d == 0) return p;
        return p with { Mental = (int)MathUtil.Clamp(p.Mental + d, 1, 99) };
    }

    /// <summary>
    /// 旧セーブ（精神系を振っていない世代）の後埋め。ロスターの全員が Mental=Leadership=Lead=50 ちょうど（旧固定値）
    /// のときだけ、(校ID, 選手ID) の決定論 Fork で振り直し学年アークを載せる（能力は触らない）。
    /// 一人でも振られていれば何もしない（新生成のロスターで全員がちょうど 50/50/50 になる確率は事実上 0）。
    /// 戻り値＝振り直したか。
    /// </summary>
    public static bool BackfillIfUnrolled(AiSchoolRoster roster, School school, AiRosterDeps deps)
    {
        if (roster.Players.Count == 0) return false;
        foreach (var p in roster.Players)
        {
            var s = p.Snapshot;
            if (s.Mental != 50 || s.Leadership != 50 || s.Lead != 50) return false;
        }
        var rc = deps.Roster;
        var center = MentalCenter(school.Strength, school.Style, rc);
        var bonus = StyleBonus(school.Style, rc);
        var root = new Xoshiro256Random(0x3E17_0000UL ^ (ulong)(long)school.Id);
        foreach (var p in roster.Players)
        {
            var (mental, leadership, lead) = Roll(
                center, bonus, deps.Personality.Profile(p.Snapshot.Personality), rc, root.Fork((ulong)(uint)p.Id));
            p.Snapshot = WithGradeArc(
                p.Snapshot with { Mental = mental, Leadership = leadership, Lead = lead }, p.Grade, rc);
        }
        return true;
    }
}
