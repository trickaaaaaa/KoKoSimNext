using KokoSim.Engine.Core;

namespace KokoSim.Engine.Players;

/// <summary>
/// 校風の「型」（issue #1691）による能力配分の学校単位の上乗せ。生成時に各能力の中心へ足す値そのもので、
/// 選手にそのまま持ち回る（<see cref="Players.Player.StyleOffset"/>・<see cref="Season.DevelopingPlayer.StyleOffset"/>）。
/// <b>球質タイプ（<see cref="PitcherArchetype"/>）とは異なり、<see cref="Nation.TeamStrengthProfile"/> は
/// これを差し引かない</b>＝6指標（打撃/投手/守備/機動）の個別指標には配分の偏りがそのまま出る
/// （「打高投低の学校は打撃指標が高く出る」という可視の偏りが issue の狙いそのもの）。チーム総合（6指標の
/// 加重平均）が型の有無で動かないのは、<see cref="SchoolStyleCoefficients"/> 側で各型の配分比を
/// あらかじめ校正してあるため（厳密な代数的打ち消しではなく、能力の天井 [10,99] 付近では強豪校ほど
/// 非線形にわずかにずれる＝<c>SchoolStyleArchetypeTests</c> が許容帯を固定）。保持しているのは主に
/// デバッグ・テストでの検証用（「この選手のどの能力がどれだけ校風由来か」を遡れる）。
/// 既定（全部0）＝型なし・自校の選手は常にこれ。
/// </summary>
public readonly record struct SchoolStyleOffset(
    double Contact = 0, double Power = 0, double Discipline = 0,
    double Fielding = 0, double Catching = 0, double ArmStrength = 0,
    double Speed = 0,
    double Velocity = 0, double Control = 0, double Stamina = 0, double PitchRank = 0)
{
    public static readonly SchoolStyleOffset None = default;

    public bool IsNone => this == None;
}

/// <summary>
/// 校風の型の係数（data/coefficients.yaml <c>roster:</c> 駆動・不変条件#4）。各値は「尖り方1.0」のときに
/// 能力の中心へ足すオフセット。実際の上乗せは学校ごとの尖り方（<see cref="SchoolStyleAllocation.Sharpness"/>）を
/// 掛けた値になる＝同じ校風でも「はっきり尖る学校」と「ほんのり寄る学校」が混在する（issue #1691）。
///
/// <para><b>6指標（<see cref="Nation.TeamStrengthProfile"/>）の打撃/投手/守備/機動の各指標はこのオフセットが
/// そのまま出る</b>（差し引いて中立化しない＝「打高投低の学校は打撃指標が高く出る」という可視の偏りが狙いそのもの）。
/// その代わり<b>総合（6指標の加重平均）が動かないよう、各型の3つの値の比をあらかじめ解いてある</b>:
/// 加重平均の係数（<see cref="TeamStrengthCoefficients"/> 既定＝投手.28/打撃.24/守備.18/選手層.12/機動.10/精神.08）
/// と、各オフセットが選手層（控え野手の<see cref="Season.DevelopingPlayer.AverageLevel(int)"/>・控え投手の
/// <see cref="Nation.TeamStrengthProfile.PitcherComposite"/>）にも均等に乗ることを込みで解くと、
/// 打高投低は <c>0.228×打撃 + 0.328×投手 + 0.216×守備 ≈ 0</c>、豪腕依存は <c>0.14×エース + 0.416×他 ≈ 0</c>、
/// 機動力は <c>0.096×パワー + 0.112×機動 ≈ 0</c> になる比を採っている（<c>SchoolStyleArchetypeTests</c> が
/// 総合の不変を固定）。永続ロスターは <see cref="Nation.Roster.AiRosterCycle.SolveGrowthG"/> の逆算配分が
/// 残差もすべて毎年吸収する。</para>
/// </summary>
public sealed record SchoolStyleCoefficients
{
    /// <summary>尖り方の下限（最も「ほんのり」寄る学校）。学校の強さとは無相関に Fork で決める。</summary>
    public double SharpnessMin { get; init; } = 0.4;
    /// <summary>尖り方の上限（最も「はっきり」尖る学校）。</summary>
    public double SharpnessMax { get; init; } = 1.6;

    // --- 打高投低（PowerHitting）: 打撃（ミート/パワー/選球眼）を厚く、投手・守備を薄く ---
    public double PowerHittingBatting { get; init; } = 14;
    public double PowerHittingPitching { get; init; } = -7;
    public double PowerHittingDefense { get; init; } = -5;

    // --- 投守型（DefensiveMinded）: 投手・守備/捕球/肩を厚く、打撃を薄く ---
    public double DefensiveMindedPitching { get; init; } = 10;
    public double DefensiveMindedDefense { get; init; } = 6;
    public double DefensiveMindedBatting { get; init; } = -20;

    // --- 機動力（SmallBall）: 走力・盗塁・バントを厚く、パワーを薄く（ミート/選球眼は触らない） ---
    public double SmallBallMobility { get; init; } = 10;
    public double SmallBallPower { get; init; } = -12;

    // --- 豪腕依存（AceDependent）: エースへの集中を大きく（エースの上乗せ↑・2番手以降投手と野手は薄く） ---
    public double AceDependentAceBoost { get; init; } = 18;
    public double AceDependentOthersPenalty { get; init; } = -6;
}

/// <summary>校風の型（<see cref="Match.Tactics.SchoolStyle"/>）→能力配分オフセットの写像（純関数）。</summary>
public static class SchoolStyleAllocation
{
    /// <summary>
    /// 0〜1の抽選値（<see cref="Nation.School.StyleIntensityRoll"/>）を尖り方の実数値へ写す
    /// （学校の強さとは無相関＝強豪校だけが尖るわけではない）。型なし（Standard/TotalBaseball）は呼ばなくてよい
    /// （<see cref="ForFielder"/>/<see cref="ForPitcher"/> が型なしで常に <see cref="SchoolStyleOffset.None"/> を返すため無害）。
    /// </summary>
    public static double Sharpness(double roll, SchoolStyleCoefficients c)
        => c.SharpnessMin + MathUtil.Clamp(roll, 0.0, 1.0) * (c.SharpnessMax - c.SharpnessMin);

    /// <summary>野手（スタメン・控え共通。学校内で均一）に乗る配分オフセット。</summary>
    public static SchoolStyleOffset ForFielder(Match.Tactics.SchoolStyle? style, double sharpness, SchoolStyleCoefficients c)
    {
        if (style is not { } s || sharpness <= 0) return SchoolStyleOffset.None;
        return s switch
        {
            Match.Tactics.SchoolStyle.PowerHitting => new SchoolStyleOffset(
                Contact: c.PowerHittingBatting * sharpness,
                Power: c.PowerHittingBatting * sharpness,
                Discipline: c.PowerHittingBatting * sharpness,
                Fielding: c.PowerHittingDefense * sharpness,
                Catching: c.PowerHittingDefense * sharpness,
                ArmStrength: c.PowerHittingDefense * sharpness),
            Match.Tactics.SchoolStyle.DefensiveMinded => new SchoolStyleOffset(
                Contact: c.DefensiveMindedBatting * sharpness,
                Power: c.DefensiveMindedBatting * sharpness,
                Discipline: c.DefensiveMindedBatting * sharpness,
                Fielding: c.DefensiveMindedDefense * sharpness,
                Catching: c.DefensiveMindedDefense * sharpness,
                ArmStrength: c.DefensiveMindedDefense * sharpness),
            Match.Tactics.SchoolStyle.SmallBall => new SchoolStyleOffset(
                Power: c.SmallBallPower * sharpness,
                Speed: c.SmallBallMobility * sharpness),
            Match.Tactics.SchoolStyle.AceDependent => new SchoolStyleOffset(
                Contact: c.AceDependentOthersPenalty * sharpness,
                Power: c.AceDependentOthersPenalty * sharpness,
                Discipline: c.AceDependentOthersPenalty * sharpness),
            _ => SchoolStyleOffset.None,   // Standard / TotalBaseball: 丸いまま
        };
    }

    /// <summary>
    /// マウンドに置く投手に乗る配分オフセット。<paramref name="isAce"/>＝エース格（豪腕依存はエースだけ別枠で強く上乗せし、
    /// 2番手以降は野手と同じ罰を受ける）。他の型はエース・控えの区別なく同じ値。
    /// </summary>
    public static SchoolStyleOffset ForPitcher(Match.Tactics.SchoolStyle? style, double sharpness, bool isAce, SchoolStyleCoefficients c)
    {
        if (style is not { } s || sharpness <= 0) return SchoolStyleOffset.None;
        var p = s switch
        {
            Match.Tactics.SchoolStyle.PowerHitting => c.PowerHittingPitching * sharpness,
            Match.Tactics.SchoolStyle.DefensiveMinded => c.DefensiveMindedPitching * sharpness,
            Match.Tactics.SchoolStyle.AceDependent => (isAce ? c.AceDependentAceBoost : c.AceDependentOthersPenalty) * sharpness,
            _ => 0.0,
        };
        return p == 0.0 ? SchoolStyleOffset.None : new SchoolStyleOffset(Velocity: p, Control: p, Stamina: p, PitchRank: p);
    }
}
