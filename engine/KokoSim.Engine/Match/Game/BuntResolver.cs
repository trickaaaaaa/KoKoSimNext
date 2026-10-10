using KokoSim.Engine.Core;
using KokoSim.Engine.Match.Field;
using KokoSim.Engine.Players;

namespace KokoSim.Engine.Match.Game;

/// <summary>バントの結果分布（設計書02 §4.3）。</summary>
public enum BuntResult
{
    SacrificeSuccess,  // 犠打成功（走者進塁・打者アウト）
    ForceOutAtSecond,  // 一塁走者を二塁で封殺（打者は一塁生存＝野選相当。issue bunt-no-take-on-ball-pitches）
    ForceOutAtThird,   // 二塁走者（1・2塁時）を三塁で封殺（打者・一塁走者は生存＝野選相当）
    ForceOutAtHome,    // 三塁走者（満塁時）を本塁で封殺（打者・一塁・二塁走者は生存＝野選相当・無得点）
    InfieldHit,        // 内野安打（セーフティ成功）
    PopOut,            // 小フライ（危険なアウト）
    PopOutDoubledOff,  // 小フライ＋一塁走者が戻れず併殺（design-09「小フライ併殺」）
    Foul,              // ファウル（カウントのみ進む）
    MissedBunt,        // 空振り
}

/// <summary>
/// バント／セーフティバントの解決（設計書02 §4.3）。バントパラメータが主要因（ミートは僅か）。
/// 基礎成功率 = BuntBase + Bunt×Slope − 球速補正 − コース補正。結果は分布で返す。
/// スクイズは <see cref="SqueezeResolver"/> がこれを内部利用する。
/// </summary>
public static class BuntResolver
{
    /// <summary>投手の反応球速がバント基準を超えた分[km/h]（速いほど大きい。0が下限）。
    /// 成功率・空振り率・小フライ率が共有する。</summary>
    private static double VelocityPenalty(PitcherAttributes pitcher, BaserunningCoefficients c)
        => System.Math.Max(0.0, pitcher.MaxVelocityKmh + pitcher.ReactionVelocityOffsetKmh - c.BuntVelocityRefKmh);

    /// <summary>バント成功率（犠打が転がる確率。分岐後の成功判定に使う）。courseDifficulty はコース補正
    /// （<c>StrikeZone.EdgeProximity</c>, 0=中心〜1=縁/ゾーン外, 設計書02 §4.3の欠落項）。</summary>
    public static double SuccessRate(
        Player batter, PitcherAttributes pitcher, double courseDifficulty, BaserunningCoefficients c)
    {
        // 性格④（設計書01 §1.1）: 自己犠牲型は+、目立ちたがりは−。BuntSuccessBonus は投影時に性格表から解決済み（既定0）。
        // VelocityPenalty は「基準を超えた球速[km/h]」の生値（Miss/Pop側は独自スロープで使う）なので、
        // ここでは従来どおり BuntVelocityPenaltyPerKmh を掛けて確率スケールへ変換する。
        return MathUtil.Clamp(
            c.BuntBase + c.AbilityEffect.Batting(batter.Bunt) * c.BuntSkillSlope
            - VelocityPenalty(pitcher, c) * c.BuntVelocityPenaltyPerKmh
            - courseDifficulty * c.BuntCourseSlope + batter.BuntSuccessBonus,
            0.05, 0.98);
    }

    /// <summary>
    /// バント内野安打確率（設計書02 §4.1b, Step4-②）。犠打・セーフティ共通で
    /// 「打者走者→一塁」対「バント処理→一塁送球」の秒勝負を logistic に通す。
    /// 犠打は構え遅延が大きく、走力が低いとほぼ0%（＝従来挙動）だが、快足×好バントは送りでも数%生きる。
    /// </summary>
    public static double InfieldHitProbability(Player batter, bool safety, BaserunningCoefficients c)
    {
        var handednessBonus = batter.Bats is Handedness.Left or Handedness.Switch
            ? c.BuntLeftFirstStepBonusSeconds : 0.0;
        var squareDelay = safety ? c.SafetyBuntSquareDelaySeconds : c.SacrificeBuntSquareDelaySeconds;
        var batterTime = c.BuntBaseDistanceM / batter.ToBatter(c.AbilityEffect).SpeedToFirstMps()
                         + c.BuntRunnerReactionSeconds + squareDelay - handednessBonus;
        var defenseTime = c.BuntFieldThrowBaseSeconds + (c.AbilityEffect.Batting(batter.Bunt) - 50) * c.BuntPlacementSlope;
        var margin = defenseTime - batterTime;
        return MathUtil.Clamp(MathUtil.Logistic(margin / c.BuntInfieldHitTimeScale), 0.01, 0.95);
    }

    /// <summary>バント処理の起点（一塁走者の二塁封殺・スクイズの三塁走者本塁判定が共有, issue
    /// bunt-no-take-on-ball-pitches）。「誰が処理したか」を特定しない簡略化＝
    /// <see cref="BaserunningCoefficients.BuntFieldThrowBaseSeconds"/> と同じ抽象レベル。
    /// 新しい走者物理式は書かず、既存の <see cref="HomePlayResolver"/> のレースへそのまま渡す（不変条件#1）。</summary>
    public static HomePlaySituation FieldedSituation(BaserunningCoefficients c)
        => new(
            new Vector3D(c.BuntFieldedXM, 0, c.BuntFieldedZM),
            c.BuntFieldedAtSeconds,
            c.BuntFielderThrowSpeedMps,
            (int)c.BuntFielderFieldingLevel);

    /// <summary>小フライの落下点（issue bunt-no-take-on-ball-pitches。<see cref="Timeline.TimelineBuilder.BuildBunt"/>
    /// の絵と共有する単一ソース＝X=0固定・判定＝絵）。</summary>
    public static Vector3D PopFieldedPoint(BaserunningCoefficients c) => new(0, 0, c.BuntPopFieldedZM);

    /// <summary>小フライからのライナー併殺（design-09「小フライ併殺」）。firstRunner/field が非null のときだけ
    /// 判定し、一塁走者が戻れなければ<see cref="BuntResult.PopOutDoubledOff"/>を返す（既存の
    /// <see cref="DoubledOffResolver"/>＝通常打球のContact始動と同型の時間勝負をそのまま使う。新しい走者物理式は
    /// 書かない）。乱数消費は firstRunner/field が揃っているときだけ1回追加（揃っていなければ0回＝従来と同じ）。</summary>
    private static BuntResult PopOutOrDoubledOff(
        Player? firstRunner, FieldGeometry? field, BaserunningCoefficients c, IRandomSource rng)
    {
        if (firstRunner is null || field is null) return BuntResult.PopOut;
        var catchToFirstM = (field.FirstBase - PopFieldedPoint(c)).Length;
        return DoubledOffResolver.Resolve(
            firstRunner, c.BuntPopHangSeconds, catchToFirstM, c.BuntFielderThrowSpeedMps, c, rng,
            (int)c.BuntFielderFieldingLevel) == DoubledOffResult.DoubledOff
            ? BuntResult.PopOutDoubledOff
            : BuntResult.PopOut;
    }

    /// <summary>ボール球を見送る確率（issue bunt-no-take-on-ball-pitches）。呼び出し側は inZone=false の
    /// ときだけ呼ぶ（ゾーン内では見送り判定自体を行わない）。</summary>
    public static double TakeProbability(double distanceOutsideM, Player batter, BaserunningCoefficients c)
        => MathUtil.Clamp(
            c.BuntTakeBase + distanceOutsideM * c.BuntTakeDistanceSlope
            + (c.AbilityEffect.Batting(batter.Discipline) - 50) * c.BuntTakeDisciplineSlope,
            0.0, c.BuntTakeCap);

    /// <summary>
    /// バント1回を解決。safety=true はセーフティ（内野安打を積極的に狙う）。pitchStuff は投球の球威
    /// （<c>PitchPlan.Stuff</c>、0中心）。courseDifficulty はコース難度（<c>StrikeZone.EdgeProximity</c>、0〜1）。
    /// firstRunner と field を渡すと一塁走者の二塁封殺（<see cref="BuntResult.ForceOutAtSecond"/>）も判定する
    /// （どちらか null なら従来どおり一塁送球のみで判定＝一塁走者なしの場面や field 未接続のテストで安全）。
    /// secondRunner も渡す（かつ firstRunner が非null）と、二塁走者の三塁封殺（<see cref="BuntResult.ForceOutAtThird"/>、
    /// 1・2塁時の真のフォースのみ成立）も候補に入る。thirdRunner も渡す（かつ firstRunner・secondRunner が
    /// 非null＝満塁の真のフォース）と、三塁走者の本塁封殺（<see cref="BuntResult.ForceOutAtHome"/>、無得点）も
    /// 候補に入る＝守備は一塁送球/二塁封殺/三塁封殺/本塁封殺のうちアウトになりやすい一つを選ぶ
    /// （<see cref="BaserunningModel.ResolveDoublePlay"/>と同型の比較原則。一球で1つのアウトのみ＝
    /// バント併殺はスコープ外）。スクイズ（<see cref="SqueezeResolver"/>）は三塁走者が別途独立に本塁レースを
    /// 解くため、ここでは thirdRunner に常に null を渡す（二重判定を避ける）。
    /// 分岐: 空振り(技術/球速/球威で変動) / ファウル(技術で減少) / 小フライ(技術/球速/球威で変動) /
    /// （成功判定、コース補正込み）本塁封殺 or 三塁封殺 or 一塁封殺 or 内野安打(いずれも時間勝負) or 犠打成功 /
    /// 失敗=小フライ。
    /// </summary>
    public static BuntResult Resolve(
        Player batter, PitcherAttributes pitcher, double pitchStuff, double courseDifficulty, bool safety,
        Player? firstRunner, Player? secondRunner, Player? thirdRunner, FieldGeometry? field,
        BaserunningCoefficients c, IRandomSource rng)
    {
        var veloPenalty = VelocityPenalty(pitcher, c);
        var skillEffect = c.AbilityEffect.Batting(batter.Bunt) - 50;
        var stuffPenalty = System.Math.Max(0.0, pitchStuff - c.BuntStuffRefValue);

        // 空振り: 技術が高いほど減り、球速・球威が高いほど増える（設計書02 §4.3の連動を復元）。
        var missShare = MathUtil.Clamp(
            c.BuntMissShare - skillEffect * c.BuntMissSkillSlope
            + veloPenalty * c.BuntMissVelocitySlope + stuffPenalty * c.BuntMissStuffSlope,
            c.BuntMissFloor, 0.90);
        if (MathUtil.Chance(missShare, rng)) return BuntResult.MissedBunt;

        // ファウル: 上手い打者ほど減る（設計書02 §4.3, Step4-③）。
        var foulShare = System.Math.Max(c.BuntFoulFloor, c.BuntFoulShare - skillEffect * c.BuntFoulSkillSlope);
        if (MathUtil.Chance(foulShare, rng)) return BuntResult.Foul;

        // 小フライ: 空振りと同じ3軸で連動する。
        var popShare = MathUtil.Clamp(
            c.BuntPopShare - skillEffect * c.BuntPopSkillSlope
            + veloPenalty * c.BuntPopVelocitySlope + stuffPenalty * c.BuntPopStuffSlope,
            c.BuntPopFloor, 0.90);
        if (MathUtil.Chance(popShare, rng)) return PopOutOrDoubledOff(firstRunner, field, c, rng);

        if (!MathUtil.Chance(SuccessRate(batter, pitcher, courseDifficulty, c), rng))
            return PopOutOrDoubledOff(firstRunner, field, c, rng); // 転がしそこねの凡打

        var infieldHitP = InfieldHitProbability(batter, safety, c);

        // 封殺の候補（Q31=BaserunningModel.ResolveDoublePlay と同じ比較原則）: 一塁送球・二塁封殺・三塁封殺
        // （1・2塁の真のフォース）・本塁封殺（満塁の真のフォース）のうち、一番アウトになりやすい
        // （safe確率が低い）ものを守備は選ぶ。乱数消費は常に1回（どの分岐でも）。
        double? bestSafeP = null;
        var bestResult = BuntResult.ForceOutAtSecond;
        if (firstRunner is not null && field is not null)
        {
            var r1SafeP = HomePlayResolver.SuccessProbability(
                firstRunner, fromBase: 1, FieldedSituation(c), field, c,
                HomePlayResolver.SecondForceParams(field, c), c.BuntForceR1StartDelaySeconds);
            if (r1SafeP < infieldHitP) { bestSafeP = r1SafeP; bestResult = BuntResult.ForceOutAtSecond; }

            if (secondRunner is not null)
            {
                var r2SafeP = HomePlayResolver.SuccessProbability(
                    secondRunner, fromBase: 2, FieldedSituation(c), field, c,
                    HomePlayResolver.ThirdForceParams(field, c), c.BuntForceR2StartDelaySeconds);
                if (r2SafeP < infieldHitP && (bestSafeP is null || r2SafeP < bestSafeP))
                {
                    bestSafeP = r2SafeP;
                    bestResult = BuntResult.ForceOutAtThird;
                }

                if (thirdRunner is not null)
                {
                    var r3SafeP = HomePlayResolver.SuccessProbability(
                        thirdRunner, fromBase: 3, FieldedSituation(c), field, c,
                        HomePlayResolver.HomeForceParams(c), c.BuntForceR3StartDelaySeconds);
                    if (r3SafeP < infieldHitP && (bestSafeP is null || r3SafeP < bestSafeP))
                    {
                        bestSafeP = r3SafeP;
                        bestResult = BuntResult.ForceOutAtHome;
                    }
                }
            }
        }
        if (bestSafeP is double safeP)
            return MathUtil.Chance(safeP, rng) ? BuntResult.InfieldHit : bestResult;

        // 犠打・セーフティ共通の内野安打判定（時間軸）。セーフティは構え遅延が小さく生きやすい。
        if (MathUtil.Chance(infieldHitP, rng)) return BuntResult.InfieldHit;
        return BuntResult.SacrificeSuccess;
    }
}
