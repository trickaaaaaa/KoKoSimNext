using KokoSim.Engine.Match.Field;
using KokoSim.Engine.Match.Fielding;
using KokoSim.Engine.Nation.Roster;
using KokoSim.Engine.Players;

namespace KokoSim.Engine.Match.Tactics;

/// <summary>
/// 敵AIの「選手 × 守備位置」の評価（issue #1709）。試合前のスタメン配置（<see cref="AiTeamBuilder"/>）と
/// 試合中の守備位置の入替（<see cref="AiTacticsBrain"/>）の<b>両方がこの1本を通る</b>＝評価がずれて
/// 自分で組んだ配置を終盤に崩すことが起きない。
///
/// <para>評価 = 位置の重み（<c>defense_weight_*</c>）× 実効守備力（地力×適性補正）＋ 肩 × 位置ごとの肩の重み
/// − 左投げ罰。打撃など「枠に依らない候補ごとの定数」は含めない（入替でも配置でも相殺する）。</para>
/// </summary>
public sealed class DefensePlacement
{
    private readonly EnemyAiCoefficients _ai;
    private readonly FieldingCoefficients _fielding;
    private readonly PersistentRosterCoefficients _roster;

    public DefensePlacement(
        EnemyAiCoefficients? ai = null, FieldingCoefficients? fielding = null, PersistentRosterCoefficients? roster = null)
    {
        _ai = ai ?? new EnemyAiCoefficients();
        _fielding = fielding ?? new FieldingCoefficients();
        _roster = roster ?? new PersistentRosterCoefficients();
    }

    /// <summary>位置の重み。投手・DH は対象外なので null。捕手は配置では評価するが入替・コンバート継投の対象外（呼び出し側で除く）。</summary>
    public double? Weight(FieldPosition pos) => pos switch
    {
        FieldPosition.Catcher => _ai.DefenseWeightCatcher,
        FieldPosition.Shortstop => _ai.DefenseWeightShortstop,
        FieldPosition.CenterField => _ai.DefenseWeightCenterField,
        FieldPosition.SecondBase => _ai.DefenseWeightSecondBase,
        FieldPosition.ThirdBase => _ai.DefenseWeightThirdBase,
        FieldPosition.RightField => _ai.DefenseWeightRightField,
        FieldPosition.LeftField => _ai.DefenseWeightLeftField,
        FieldPosition.FirstBase => _ai.DefenseWeightFirstBase,
        _ => null,
    };

    /// <summary>
    /// <paramref name="p"/>（表示 Lv の能力）を <paramref name="pos"/> に置いたときの評価。
    /// <paramref name="aptitude"/> はその位置の適性（<see cref="Player.PositionAptitudes"/> か、ロスター側の適性）。
    /// </summary>
    public double Score(Player p, int aptitude, FieldPosition pos)
    {
        var w = Weight(pos) ?? 0.0;
        var raw = Season.EffectiveFielding.Raw(AbilityScale.Phys(p.Fielding), aptitude, _fielding);
        var score = w * raw + AbilityScale.Phys(p.ArmStrength) * _roster.FieldSlotArmWeight(pos);
        if (p.Throws == Handedness.Left) score -= LeftThrowPenalty(pos);
        return score;
    }

    /// <summary>試合中の選手が持つ適性（<see cref="Player.PositionAptitudes"/>）で評価する。適性を持たない選手は中庸（補正なし）。</summary>
    public double ScoreAt(Player p, FieldPosition pos)
    {
        var apt = p.PositionAptitudes is { } a && (int)pos < a.Count ? a[(int)pos] : (int)_fielding.AptitudeNeutral;
        return Score(p, apt, pos);
    }

    /// <summary>
    /// 左投げを <see cref="FieldPositions.IsRightThrowOnly"/> の位置へ座らせないための重み罰
    /// （issue ai-lefty-not-at-c-2b-3b-ss）。
    ///
    /// <para>⚠ ハード除外（候補から外す）にしない＝<b>右投げの野手が4人未満のチームで割当そのものが
    /// 成立しなくなる</b>（<c>AssignmentSolver.MaxWeight</c> が解を返せない）。罰の大小で「どうしても
    /// 入れる場合の妥協順」を表す＝<b>三塁 → 二塁 → 捕手 → 遊撃</b>（監督決定 2026-08-13。
    /// 遊撃がいちばん譲れない）。</para>
    ///
    /// <para>値は評価値（高々150程度）より1桁大きく、かつ位置どうしの差もそれより大きい。
    /// ＝右投げが足りているうちは必ず右投げが勝ち、足りないときは妥協順のとおりに崩れる。</para>
    /// </summary>
    public static double LeftThrowPenalty(FieldPosition pos) => pos switch
    {
        FieldPosition.ThirdBase => 1000.0,
        FieldPosition.SecondBase => 2000.0,
        FieldPosition.Catcher => 3000.0,
        FieldPosition.Shortstop => 4000.0,
        _ => 0.0,
    };
}
