using System.Collections.Generic;
using KokoSim.Engine.Core;
using KokoSim.Engine.Match.Field;

namespace KokoSim.Engine.Match.Tactics;

/// <summary>
/// 守備指示→初期守備位置の変換（設計書09 §2.1）。
/// 陣形は守備解決（幾何計算）の初期位置そのものなので、前進守備の本塁封殺率↑や
/// 「間を抜かれると長打」はすべて物理から自然に出る（不変条件#1）。
/// </summary>
public static class AlignmentTactics
{
    /// <summary>指示を初期守備位置へ反映。全指示が既定（普通×普通×シフトなし）なら入力をそのまま返す。</summary>
    public static IReadOnlyList<Fielder> Adjust(
        IReadOnlyList<Fielder> fielders, DefensiveTactics tactics, TacticsCoefficients c)
    {
        if (tactics.Infield == DefenseDepth.Normal
            && tactics.Outfield == DefenseDepth.Normal
            && !tactics.BuntShift)
        {
            return fielders;
        }

        var inF = tactics.Infield == DefenseDepth.In ? c.InfieldInFactor
            : tactics.Infield == DefenseDepth.Deep ? c.InfieldDeepFactor : 1.0;
        var outF = tactics.Outfield == DefenseDepth.In ? c.OutfieldInFactor
            : tactics.Outfield == DefenseDepth.Deep ? c.OutfieldDeepFactor : 1.0;

        var list = new List<Fielder>(fielders.Count);
        foreach (var f in fielders)
        {
            var factor = f.Position switch
            {
                FieldPosition.Pitcher or FieldPosition.Catcher => 1.0, // バッテリーは動かない
                FieldPosition.FirstBase or FieldPosition.ThirdBase =>
                    tactics.BuntShift ? c.BuntShiftCornerChargeFactor : inF, // シフトは前進より優先
                FieldPosition.SecondBase or FieldPosition.Shortstop => inF,
                _ => outF, // 外野3人
            };
            list.Add(factor == 1.0
                ? f
                : f with { Location = new Vector3D(f.Location.X * factor, f.Location.Y, f.Location.Z * factor) });
        }
        return list;
    }
}
