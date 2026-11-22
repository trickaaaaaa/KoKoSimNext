using System;
using System.Collections.Generic;
using KokoSim.Engine.Core;
using KokoSim.Engine.Match.Fielding;
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
    FieldersChoice,    // 野選（スクイズで本塁へ投げた／本塁へ投げず走者が生還した形で、打者は一塁で刺せた・打者生存・アウトなし・安打にしない。issue #1258）
}

/// <summary>
/// バント／セーフティバントの解決（設計書02 §4.3）。バントパラメータが主要因（ミートは僅か）。
/// 基礎成功率 = BuntBase + Bunt×Slope − 球速補正 − コース補正。結果は分布で返す。
/// スクイズは <see cref="SqueezeResolver"/> がこれを内部利用する。
/// </summary>
public static class BuntResolver
{
    /// <summary>
    /// バントの処理結果（issue #1092）: 強さ・方向から決まる転がりに対し、どの野手が・どこで・いつ処理したか。
    /// 判定（<see cref="Resolve"/> 内の封殺・内野安打判定）と絵（<see cref="Timeline.TimelineBuilder.BuildBunt"/>）が
    /// この値を共有する単一ソース＝「判定＝絵」原則（design-18 §4.5-③）。
    /// </summary>
    /// <param name="Fielder">処理した野手の守備位置（投手/捕手/一塁手/三塁手のいずれか）。</param>
    /// <param name="Situation">処理点・処理時刻・処理野手の肩/守備/送球精度（<see cref="HomePlayResolver"/> の
    /// レースへそのまま渡せる形）。</param>
    /// <param name="LaunchSpeedMps">転がり始めの初速[m/s]（観測・テスト用）。</param>
    /// <param name="LaunchBearingDeg">方位[deg]（+X=一塁側/−X=三塁側。観測・テスト用）。</param>
    public readonly record struct BuntFielding(
        FieldPosition Fielder, HomePlaySituation Situation, double LaunchSpeedMps, double LaunchBearingDeg);

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

    /// <summary>左打席の一塁への一歩目の有利[s]。打席側（<see cref="HandednessExtensions.EffectiveBats"/> 解決済み）で見る。</summary>
    public static double FirstStepBonusSeconds(Handedness side, BaserunningCoefficients c)
        => side.IsLeftSide() ? c.BuntLeftFirstStepBonusSeconds : 0.0;

    /// <summary>打者走者→一塁の所要[s]（接触基準。バント構えの遅延込み）。判定・絵が共有する。</summary>
    public static double BatterToFirstSeconds(Player batter, bool safety, Handedness side, BaserunningCoefficients c)
    {
        var handednessBonus = FirstStepBonusSeconds(side, c);
        var squareDelay = safety ? c.SafetyBuntSquareDelaySeconds : c.SacrificeBuntSquareDelaySeconds;
        return c.BuntBaseDistanceM / batter.ToBatter(c.AbilityEffect).SpeedToFirstMps()
               + c.BuntRunnerReactionSeconds + squareDelay - handednessBonus;
    }

    /// <summary>
    /// バント内野安打確率（設計書02 §4.1b/§4.3, issue #1092）。犠打・セーフティ共通で
    /// 「打者走者→一塁」対「バント処理→一塁送球」の秒勝負を logistic に通す。処理点・処理時刻・処理野手の肩は
    /// <paramref name="fielding"/>（<see cref="RollFielding"/> の物理レース）から取り、一塁封殺と同じ
    /// <see cref="HomePlayResolver.DefenseTimeSeconds"/> を使う＝一塁送球と封殺を同じ1つのモデルで判定する
    /// （旧実装は「誰が処理したか特定しない」固定3.70秒を使っていた）。
    /// </summary>
    /// <param name="throwNoiseSeconds">一塁送球の逸れによる遅れ（期待値との差, issue #859）を守備側へ足した確率。</param>
    public static double InfieldHitProbability(
        Player batter, bool safety, Handedness side, FieldGeometry field, BuntFielding fielding,
        BaserunningCoefficients c, double throwNoiseSeconds = 0.0)
    {
        var batterTime = BatterToFirstSeconds(batter, safety, side, c);
        var defenseTime = HomePlayResolver.DefenseTimeSeconds(fielding.Situation, field.FirstBase, c.FirstForceTagSeconds, c);
        var margin = defenseTime - batterTime + throwNoiseSeconds;
        return MathUtil.Clamp(MathUtil.Logistic(margin / c.BuntInfieldHitTimeScale), 0.01, 0.95);
    }

    /// <summary>小フライの落下点（issue bunt-no-take-on-ball-pitches。<see cref="Timeline.TimelineBuilder.BuildBunt"/>
    /// の絵と共有する単一ソース＝X=0固定・判定＝絵）。小フライは常に捕手が捕る簡略化＝下の物理レースとは無関係。</summary>
    public static Vector3D PopFieldedPoint(BaserunningCoefficients c) => new(0, 0, c.BuntPopFieldedZM);

    /// <summary>
    /// 打球の強さ（初速）と方向（方位）を打者のバント技術から引き、既存のゴロ転がりモデル
    /// （<see cref="BouncePath"/>）へ通し、投手・捕手・一塁手・三塁手の到達レースで処理野手・処理点・処理時刻を
    /// 決める（issue #1092）。新しい物理式は発明しない（不変条件#1）＝転がりは <see cref="FieldingCoefficients"/>
    /// の既存の反発・摩擦係数、到達レースは <see cref="FieldingResolver"/> の内野ゴロ処理と同型（距離÷走力＋反応遅延）。
    ///
    /// <para>品質ロール（0=投手前へ強く・1=ラインぎわへ弱く）は打者のバント技術で分布がシフトし、方位の左右は
    /// 五分＋左打者のドラッグボーナス。投手はフォロースルーの前進（<see cref="BaserunningCoefficients.BuntPitcherChargeFactor"/>）、
    /// 捕手はしゃがみからの反応遅延（<see cref="BaserunningCoefficients.BuntCatcherFieldingReactionPenaltySeconds"/>）を負う。
    /// 乱数消費は必ず2回（品質・方位の左右）＝分岐によらず一定（不変条件#2）。</para>
    ///
    /// <para>投球の効き（issue #1180）: <paramref name="veloPenaltyKmh"/>（反応球速がバント基準を超えた分、
    /// <see cref="VelocityPenalty"/>）と <paramref name="stuffPenalty"/>（その1球の球威の基準超え）が大きいほど
    /// 品質が下がる＝強く投手前へ転がる。品質の式に項を足すだけで乱数は増えない。</para>
    /// </summary>
    public static BuntFielding RollFielding(
        Player batter, Handedness side, IReadOnlyList<Fielder> fielders,
        FieldingCoefficients fieldingCoeff, BaserunningCoefficients c, IRandomSource rng,
        double veloPenaltyKmh = 0.0, double stuffPenalty = 0.0)
    {
        var skillEffect = c.AbilityEffect.Batting(batter.Bunt) - 50;
        var quality = MathUtil.Clamp(
            rng.NextDouble() + skillEffect * c.BuntLaunchQualitySkillSlope
            - veloPenaltyKmh * c.BuntLaunchQualityVelocitySlope - stuffPenalty * c.BuntLaunchQualityStuffSlope,
            0.0, 1.0);
        var firstBaseLineProb = MathUtil.Clamp(
            c.BuntLaunchFirstBaseLineBase + (side.IsLeftSide() ? c.BuntLaunchLeftBatterFirstBaseLineBonus : 0.0),
            0.0, 1.0);
        var towardFirst = MathUtil.Chance(firstBaseLineProb, rng);

        var bearingMagDeg = c.BuntLaunchBearingMinDeg + (c.BuntLaunchBearingMaxDeg - c.BuntLaunchBearingMinDeg) * quality;
        var speedMps = c.BuntLaunchSpeedMaxMps - (c.BuntLaunchSpeedMaxMps - c.BuntLaunchSpeedMinMps) * quality;
        var bearingDeg = towardFirst ? bearingMagDeg : -bearingMagDeg;

        var bearingRad = bearingDeg * Math.PI / 180.0;
        var velocity = new Vector3D(speedMps * Math.Sin(bearingRad), 0, speedMps * Math.Cos(bearingRad));
        // ホーム＝接触点から即転がり始める（バントは弧を描かない。vPerp=0でBouncePathは最初の弾みを
        // スキップし、既存の転がり減速 RollDecelMps2 がそのまま効く）。
        var path = BouncePath.Compute(Vector3D.Zero, velocity, hangTimeSeconds: 0.0, fieldingCoeff, IrregularBounce.None);

        var (fielder, reachAt, fieldedPoint) = RaceFielders(fielders, path, fieldingCoeff, c);
        // 素手で処理する手間（issue #1092）: 到達できた時刻そのものではなく、そこから握ってから送球に
        // 移れる時刻＝HomePlaySituation.BallFieldedAtSeconds として渡す（ResolveThrowPath の握り替え秒とは別）。
        var fieldedAt = reachAt + c.BuntBarehandPenaltySeconds;
        var situation = new HomePlaySituation(
            fieldedPoint, fieldedAt, fielder.Attributes.ThrowSpeedMps,
            fielder.Attributes.Fielding, fielder.Attributes.ThrowAccuracy);
        return new BuntFielding(fielder.Position, situation, speedMps, bearingDeg);
    }

    /// <summary>投手・捕手・一塁手・三塁手の到達レース（issue #1092）。<see cref="FieldingResolver"/> の
    /// 内野ゴロ処理（距離÷走力＋反応遅延で最速到達を選ぶ）と同型。乱数は消費しない（決定論, 不変条件#2）。</summary>
    private static (Fielder Fielder, double FieldedAtSeconds, Vector3D FieldedPoint) RaceFielders(
        IReadOnlyList<Fielder> fielders, BouncePath path, FieldingCoefficients fieldingCoeff, BaserunningCoefficients c)
    {
        var candidates = new List<Fielder>(4);
        foreach (var f in fielders)
        {
            if (f.Position is FieldPosition.Pitcher or FieldPosition.Catcher
                or FieldPosition.FirstBase or FieldPosition.ThirdBase)
                candidates.Add(f);
        }
        if (candidates.Count == 0)
        {
            // 想定外（バント局面で投手すら渡されていない）: 標準位置のリーグ平均投手で代替する。
            var (px, pz) = FieldGeometry.StandardPosition(FieldPosition.Pitcher);
            candidates.Add(new Fielder(FieldPosition.Pitcher, new Vector3D(px, 0, pz), Players.FielderAttributes.LeagueAverage));
        }

        // 投手はフォロースルーの前進ぶん、ホーム方向へ詰めた位置から数える（issue #1092。
        // AlignmentTactics.Adjust のコーナー陣形と同じ「チャージ係数」の作法）。
        var starts = new Vector3D[candidates.Count];
        for (var k = 0; k < candidates.Count; k++)
        {
            starts[k] = candidates[k].Position == FieldPosition.Pitcher
                ? candidates[k].Location * c.BuntPitcherChargeFactor
                : candidates[k].Location;
        }

        var step = Math.Max(0.005, fieldingCoeff.InfieldInterceptStepSeconds);
        var end = path.SettleAtSeconds;
        var reachTime = new double[candidates.Count];
        var reachPoint = new Vector3D[candidates.Count];
        for (var k = 0; k < candidates.Count; k++) { reachTime[k] = double.PositiveInfinity; reachPoint[k] = path.Landing; }

        for (var t = 0.0; ; t += step)
        {
            var tt = Math.Min(t, end);
            var p = path.PositionAt(tt);
            for (var k = 0; k < candidates.Count; k++)
            {
                if (!double.IsPositiveInfinity(reachTime[k])) continue;
                var f = candidates[k];
                var dist = (p - starts[k]).Length;
                var reach = dist / f.Attributes.SprintSpeedMps + f.Attributes.ReactionDelaySeconds + RolePenaltySeconds(f.Position, fieldingCoeff, c);
                if (reach <= tt) { reachTime[k] = tt; reachPoint[k] = p; }
            }
            if (tt >= end) break;
        }

        var best = -1;
        for (var k = 0; k < candidates.Count; k++)
            if (reachTime[k] < (best < 0 ? double.PositiveInfinity : reachTime[best])) best = k;

        if (best >= 0) return (candidates[best], reachTime[best], reachPoint[best]);

        // 誰も間に合わないまま止まる（極端に弱いバント）: 止まった球に最も近い対象が拾いに行く。
        var stop = path.StopPosition;
        var nk = 0; var nd = double.MaxValue;
        for (var k = 0; k < candidates.Count; k++)
        {
            var d = (stop - starts[k]).Length;
            if (d < nd) { nd = d; nk = k; }
        }
        var chosen = candidates[nk];
        var at = end + nd / chosen.Attributes.SprintSpeedMps + RolePenaltySeconds(chosen.Position, fieldingCoeff, c);
        return (chosen, at, stop);
    }

    /// <summary>投手のフォロースルー復帰／捕手のしゃがみからの反応遅延（issue #1092）。それ以外の役割は0。</summary>
    private static double RolePenaltySeconds(FieldPosition role, FieldingCoefficients fieldingCoeff, BaserunningCoefficients c)
        => role switch
        {
            FieldPosition.Pitcher => fieldingCoeff.PitcherFieldingReactionPenaltySeconds,
            FieldPosition.Catcher => c.BuntCatcherFieldingReactionPenaltySeconds,
            _ => 0.0,
        };

    /// <summary>
    /// バント1回を解決。safety=true はセーフティ（内野安打を積極的に狙う）。pitchStuff は投球の球威
    /// （<c>PitchPlan.Stuff</c>、0中心）。courseDifficulty はコース難度（<c>StrikeZone.EdgeProximity</c>、0〜1）。
    /// firstRunner と field を渡すと一塁走者の二塁封殺（<see cref="BuntResult.ForceOutAtSecond"/>）も判定する
    /// （firstRunner が null なら一塁走者なしの場面として一塁送球のみで判定）。
    /// secondRunner も渡す（かつ firstRunner が非null）と、二塁走者の三塁封殺（<see cref="BuntResult.ForceOutAtThird"/>、
    /// 1・2塁時の真のフォースのみ成立）も候補に入る。thirdRunner も渡す（かつ firstRunner・secondRunner が
    /// 非null＝満塁の真のフォース）と、三塁走者の本塁封殺（<see cref="BuntResult.ForceOutAtHome"/>、無得点）も
    /// 候補に入る。<paramref name="fielders"/>・<paramref name="fieldingCoeff"/> は打球の強さ・方向から処理野手・
    /// 処理点・処理時刻を決める物理レース（<see cref="RollFielding"/>, issue #1092）に使う。
    /// スクイズ（<see cref="SqueezeResolver"/>）は三塁走者が別途独立に本塁レースを解くため、ここでは
    /// thirdRunner に常に null を渡す（二重判定を避ける）。
    ///
    /// <para>前の走者を狙う判断（オーナー決定2026-11-17）: 封殺のsafe確率が
    /// <see cref="BaserunningCoefficients.BuntForceOutLeadRunnerSafeThreshold"/> 未満の候補だけを狙い、
    /// その中で一番アウトにしやすい（safe確率が低い）ものを選ぶ。候補が無ければ一塁で確実に1つ取る
    /// （一塁送球と内野安打も同じ物理レースの処理点・処理時刻で判定＝「同じ1つのモデル」）。</para>
    ///
    /// 分岐: 空振り(技術/球速/球威で変動) / ファウル(技術で減少) / 小フライ(技術/球速/球威で変動) /
    /// （成功判定、コース補正込み）本塁封殺 or 三塁封殺 or 一塁封殺 or 内野安打(いずれも時間勝負) or 犠打成功 /
    /// 失敗=小フライ。
    /// </summary>
    public static BuntResult Resolve(
        Player batter, PitcherAttributes pitcher, double pitchStuff, double courseDifficulty, bool safety,
        Player? firstRunner, Player? secondRunner, Player? thirdRunner,
        FieldGeometry field, IReadOnlyList<Fielder> fielders, FieldingCoefficients fieldingCoeff,
        BaserunningCoefficients c, IRandomSource rng)
        => Resolve(batter, pitcher, pitchStuff, courseDifficulty, safety, firstRunner, secondRunner, thirdRunner,
            field, fielders, fieldingCoeff, c, rng, out _, out _, batterSide: batter.Bats);

    /// <summary>
    /// <see cref="Resolve(Player,PitcherAttributes,double,double,bool,Player?,Player?,Player?,FieldGeometry,System.Collections.Generic.IReadOnlyList{Fielder},FieldingCoefficients,BaserunningCoefficients,IRandomSource)"/>
    /// に、処理野手の送球の逸れ（issue #859。判定に使った値そのもの）と、打球の物理レース結果（issue #1092。
    /// <see cref="Timeline.TimelineBuilder.BuildBunt"/> が「判定＝絵」で使う）を添えて返す。
    /// <paramref name="batterSide"/> は打席側（<see cref="HandednessExtensions.EffectiveBats"/> 解決済み、
    /// 既定は<paramref name="batter"/>.Bats）。送球まで行かなかった（空振り・ファウル・小フライ）なら両方 null。
    /// </summary>
    public static BuntResult Resolve(
        Player batter, PitcherAttributes pitcher, double pitchStuff, double courseDifficulty, bool safety,
        Player? firstRunner, Player? secondRunner, Player? thirdRunner,
        FieldGeometry field, IReadOnlyList<Fielder> fielders, FieldingCoefficients fieldingCoeff,
        BaserunningCoefficients c, IRandomSource rng,
        out ThrowScatterModel.Scatter? throwScatter, out BuntFielding? fielding,
        Handedness? batterSide = null)
    {
        throwScatter = null;
        fielding = null;
        var side = batterSide ?? batter.Bats;
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
        {
            // 転がしそこね: 一部はファウル（打席継続）、残りは小フライの凡打（issue #1092・BuntMisrollFoulShare）。
            if (MathUtil.Chance(c.BuntMisrollFoulShare, rng)) return BuntResult.Foul;
            return PopOutOrDoubledOff(firstRunner, field, c, rng);
        }

        // 打球の強さ・方向→処理野手・処理点・処理時刻の物理レース（issue #1092）。乱数消費は常に2回
        // （品質・方位の左右）。
        var rolled = RollFielding(batter, side, fielders, fieldingCoeff, c, rng, veloPenalty, stuffPenalty);
        fielding = rolled;

        var infieldHitP = InfieldHitProbability(batter, safety, side, field, rolled, c);
        // 処理野手の送球1本の逸れ（issue #859）。どの塁へ投げるかは期待値（下の比較）で選び、
        // 選んだ塁との勝負にだけその本の遅れ（期待値との差＝ゼロ平均）を足す。実際に処理した野手の
        // 送球精度（rolled.Situation.ThrowerThrowAccuracy, issue #1092）で引く＝「誰が処理したか特定しない」簡略化の撤去。
        var (scatter, noise) = HomePlayResolver.SampleThrowScatter(rolled.Situation.ThrowerThrowAccuracy, c, rng);
        throwScatter = scatter;

        // 封殺の候補（オーナー決定2026-11-17）: 封殺のsafe確率がしきい値未満の候補だけを狙い、
        // その中で一番アウトにしやすい（safe確率が低い）ものを守備は選ぶ。乱数消費は常に1回（どの分岐でも）。
        var threshold = c.BuntForceOutLeadRunnerSafeThreshold;
        double? bestSafeP = null;
        var bestResult = BuntResult.ForceOutAtSecond;
        Func<double>? realizedSafeP = null;   // 選んだ封殺の、逸れの遅れ込みの確率
        if (firstRunner is not null)
        {
            var r1SafeP = HomePlayResolver.SuccessProbability(
                firstRunner, fromBase: 1, rolled.Situation, field, c,
                HomePlayResolver.SecondForceParams(field, c), c.BuntForceR1StartDelaySeconds);
            if (r1SafeP < threshold)
            {
                bestSafeP = r1SafeP; bestResult = BuntResult.ForceOutAtSecond;
                realizedSafeP = () => ForceSafeP(firstRunner, 1, HomePlayResolver.SecondForceParams(field, c), c.BuntForceR1StartDelaySeconds);
            }

            if (secondRunner is not null)
            {
                var r2SafeP = HomePlayResolver.SuccessProbability(
                    secondRunner, fromBase: 2, rolled.Situation, field, c,
                    HomePlayResolver.ThirdForceParams(field, c), c.BuntForceR2StartDelaySeconds);
                if (r2SafeP < threshold && (bestSafeP is null || r2SafeP < bestSafeP))
                {
                    bestSafeP = r2SafeP;
                    bestResult = BuntResult.ForceOutAtThird;
                    realizedSafeP = () => ForceSafeP(secondRunner, 2, HomePlayResolver.ThirdForceParams(field, c), c.BuntForceR2StartDelaySeconds);
                }

                if (thirdRunner is not null)
                {
                    var r3SafeP = HomePlayResolver.SuccessProbability(
                        thirdRunner, fromBase: 3, rolled.Situation, field, c,
                        HomePlayResolver.HomeForceParams(c), c.BuntForceR3StartDelaySeconds);
                    if (r3SafeP < threshold && (bestSafeP is null || r3SafeP < bestSafeP))
                    {
                        bestSafeP = r3SafeP;
                        bestResult = BuntResult.ForceOutAtHome;
                        realizedSafeP = () => ForceSafeP(thirdRunner, 3, HomePlayResolver.HomeForceParams(c), c.BuntForceR3StartDelaySeconds);
                    }
                }
            }
        }
        if (bestSafeP is not null && realizedSafeP is not null)
            return MathUtil.Chance(realizedSafeP(), rng) ? BuntResult.InfieldHit : bestResult;

        // 犠打・セーフティ共通の内野安打判定（時間軸）。セーフティは構え遅延が小さく生きやすい。
        if (MathUtil.Chance(InfieldHitProbability(batter, safety, side, field, rolled, c, noise), rng)) return BuntResult.InfieldHit;
        return BuntResult.SacrificeSuccess;

        double ForceSafeP(Player runner, int fromBase, HomePlayResolver.BasePlayParams bp, double delay)
            => MathUtil.Logistic(
                (HomePlayResolver.Margin(runner, fromBase, rolled.Situation, field, c, bp, delay) + noise) / bp.MarginScale);
    }

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
}
