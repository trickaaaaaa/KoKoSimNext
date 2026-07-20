using System.Collections.Generic;
using KokoSim.Engine.Match.Field;

namespace KokoSim.Engine.Match.Timeline;

/// <summary>打球ゾーン（陣形ルールのキー）。方位角と深さから分類する。</summary>
public enum BallZone
{
    Bunt,           // 本塁前の弱い打球
    InfieldLeft,    // 三遊間側の内野
    InfieldRight,   // 一二塁間側の内野
    OutfieldLeft,
    OutfieldCenter,
    OutfieldRight,
}

/// <summary>1野手への割当（ルール表の行）。Target は "ball"/"first"/"second"/"third"/"home"/"none"。</summary>
public sealed record FormationAssignment(FieldPosition Position, FielderTask Task, string Target);

/// <summary>陣形ルール（ゾーン×走者状況→9人の役割）。RunnersOn=null は「走者の有無を問わない」。</summary>
public sealed record FormationRule(BallZone Zone, bool? RunnersOn, IReadOnlyList<FormationAssignment> Assignments);

/// <summary>
/// 守備陣形ルール表（CHANGELOG 33 / 設計書01 §2⑥）。
/// カバーリング・中継・ベースカバーをハードコードせず、状況→担当動作の表引きで
/// タイムラインへ自動付与する（9人全員が毎プレー役割を持つ）。
/// ルールは data/defensive-formations.yaml から注入（未注入時は既定表）。
/// </summary>
public sealed class FormationTable
{
    private readonly IReadOnlyList<FormationRule> _rules;

    public FormationTable(IReadOnlyList<FormationRule> rules)
    {
        _rules = rules;
    }

    /// <summary>ゾーン＋走者状況に最も特化したルールを引く（走者指定あり > 指定なし）。</summary>
    public FormationRule? Lookup(BallZone zone, bool runnersOn)
    {
        FormationRule? generic = null;
        foreach (var r in _rules)
        {
            if (r.Zone != zone) continue;
            if (r.RunnersOn == runnersOn) return r;
            if (r.RunnersOn is null) generic = r;
        }
        return generic;
    }

    /// <summary>方位角[deg]と飛距離[m]からゾーンを分類。</summary>
    public static BallZone Classify(double bearingDeg, double rangeM)
    {
        if (rangeM < 15.0) return BallZone.Bunt;
        if (rangeM <= 46.0) return bearingDeg < 0 ? BallZone.InfieldLeft : BallZone.InfieldRight;
        if (bearingDeg < -15.0) return BallZone.OutfieldLeft;
        if (bearingDeg > 15.0) return BallZone.OutfieldRight;
        return BallZone.OutfieldCenter;
    }

    /// <summary>既定の陣形ルール表（YAML 未注入時のフォールバック。内容は data/defensive-formations.yaml と同一に保つ）。</summary>
    public static FormationTable Default { get; } = new(BuildDefaultRules());

    private static IReadOnlyList<FormationRule> BuildDefaultRules()
    {
        static FormationAssignment A(FieldPosition p, FielderTask t, string target = "none") => new(p, t, target);

        return new[]
        {
            new FormationRule(BallZone.Bunt, null, new[]
            {
                A(FieldPosition.Pitcher, FielderTask.FieldBall, "ball"),
                A(FieldPosition.Catcher, FielderTask.CoverBase, "home"),
                A(FieldPosition.FirstBase, FielderTask.FieldBall, "ball"),
                A(FieldPosition.SecondBase, FielderTask.CoverBase, "first"),
                A(FieldPosition.ThirdBase, FielderTask.CoverBase, "third"),
                A(FieldPosition.Shortstop, FielderTask.CoverBase, "second"),
                A(FieldPosition.LeftField, FielderTask.Backup, "third"),
                A(FieldPosition.CenterField, FielderTask.Backup, "second"),
                A(FieldPosition.RightField, FielderTask.Backup, "first"),
            }),
            new FormationRule(BallZone.InfieldLeft, null, new[]
            {
                A(FieldPosition.Shortstop, FielderTask.FieldBall, "ball"),
                A(FieldPosition.ThirdBase, FielderTask.FieldBall, "ball"),
                A(FieldPosition.SecondBase, FielderTask.CoverBase, "second"),
                A(FieldPosition.FirstBase, FielderTask.CoverBase, "first"),
                A(FieldPosition.Pitcher, FielderTask.Backup, "home"),
                A(FieldPosition.Catcher, FielderTask.CoverBase, "home"),
                A(FieldPosition.LeftField, FielderTask.Backup, "ball"),
                A(FieldPosition.CenterField, FielderTask.Backup, "second"),
                A(FieldPosition.RightField, FielderTask.Backup, "first"),
            }),
            new FormationRule(BallZone.InfieldRight, null, new[]
            {
                A(FieldPosition.SecondBase, FielderTask.FieldBall, "ball"),
                A(FieldPosition.FirstBase, FielderTask.FieldBall, "ball"),
                A(FieldPosition.Shortstop, FielderTask.CoverBase, "second"),
                A(FieldPosition.Pitcher, FielderTask.CoverBase, "first"),
                A(FieldPosition.Catcher, FielderTask.CoverBase, "home"),
                A(FieldPosition.ThirdBase, FielderTask.CoverBase, "third"),
                A(FieldPosition.RightField, FielderTask.Backup, "ball"),
                A(FieldPosition.CenterField, FielderTask.Backup, "second"),
                A(FieldPosition.LeftField, FielderTask.Hold, "none"),
            }),
            new FormationRule(BallZone.OutfieldLeft, null, new[]
            {
                A(FieldPosition.LeftField, FielderTask.FieldBall, "ball"),
                A(FieldPosition.CenterField, FielderTask.Backup, "ball"),
                A(FieldPosition.Shortstop, FielderTask.Cutoff, "second"),
                A(FieldPosition.SecondBase, FielderTask.CoverBase, "second"),
                A(FieldPosition.ThirdBase, FielderTask.CoverBase, "third"),
                A(FieldPosition.FirstBase, FielderTask.CoverBase, "first"),
                A(FieldPosition.Pitcher, FielderTask.Backup, "home"),
                A(FieldPosition.Catcher, FielderTask.CoverBase, "home"),
                A(FieldPosition.RightField, FielderTask.Hold, "none"),
            }),
            new FormationRule(BallZone.OutfieldCenter, null, new[]
            {
                A(FieldPosition.CenterField, FielderTask.FieldBall, "ball"),
                A(FieldPosition.LeftField, FielderTask.Backup, "ball"),
                A(FieldPosition.RightField, FielderTask.Backup, "ball"),
                A(FieldPosition.SecondBase, FielderTask.Cutoff, "second"),
                A(FieldPosition.Shortstop, FielderTask.CoverBase, "second"),
                A(FieldPosition.FirstBase, FielderTask.CoverBase, "first"),
                A(FieldPosition.ThirdBase, FielderTask.CoverBase, "third"),
                A(FieldPosition.Pitcher, FielderTask.Backup, "home"),
                A(FieldPosition.Catcher, FielderTask.CoverBase, "home"),
            }),
            new FormationRule(BallZone.OutfieldRight, null, new[]
            {
                A(FieldPosition.RightField, FielderTask.FieldBall, "ball"),
                A(FieldPosition.CenterField, FielderTask.Backup, "ball"),
                A(FieldPosition.SecondBase, FielderTask.Cutoff, "second"),
                A(FieldPosition.Shortstop, FielderTask.CoverBase, "second"),
                A(FieldPosition.FirstBase, FielderTask.CoverBase, "first"),
                A(FieldPosition.ThirdBase, FielderTask.CoverBase, "third"),
                A(FieldPosition.Pitcher, FielderTask.Backup, "home"),
                A(FieldPosition.Catcher, FielderTask.CoverBase, "home"),
                A(FieldPosition.LeftField, FielderTask.Hold, "none"),
            }),
        };
    }
}
