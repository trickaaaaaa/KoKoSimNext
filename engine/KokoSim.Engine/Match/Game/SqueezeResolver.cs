using System.Collections.Generic;
using KokoSim.Engine.Core;
using KokoSim.Engine.Match.Field;
using KokoSim.Engine.Match.Fielding;
using KokoSim.Engine.Players;

namespace KokoSim.Engine.Match.Game;

/// <summary>スクイズの結果。</summary>
public readonly record struct SqueezeOutcome(int Runs, bool BatterOut, bool RunnerOut, BuntResult Bunt)
{
    /// <summary>バントを処理した野手の送球の逸れ（issue #859。判定に使った値そのもの）。送球まで行かなければ null。</summary>
    public KokoSim.Engine.Match.Fielding.ThrowScatterModel.Scatter? BuntThrowScatter { get; init; }

    /// <summary>バントの物理レース結果（issue #1092。<see cref="Timeline.TimelineBuilder.BuildBunt"/> が
    /// 「判定＝絵」で使う単一ソース）。送球まで行かなければ null。</summary>
    public BuntResolver.BuntFielding? BuntFielding { get; init; }

    /// <summary>ウエスト（外された球）で打者がバントできなかったか。<see cref="BuntResult.MissedBunt"/> のうち
    /// 打者がバットを出さなかった側＝ボールのカウント。打者が空振りした側は false（ストライクのカウント）。</summary>
    public bool Wasted { get; init; }
}

/// <summary>
/// スクイズの解決（設計書02 §4.4）。バント成功率 × 三塁走者スタート(走塁判断) × ウエスト判定。
/// ウエスト（外し）: 相手バッテリーが察知するとボールを外し、三本間挟殺の大惨事になり得る。
/// 現フェーズは配球AI未接続のため wasteProbability を外から与える（既定0＝外さない）。
/// </summary>
public static class SqueezeResolver
{
    /// <summary>pitchStuff/courseDifficulty は <see cref="BuntResolver.Resolve"/> へそのまま転送する
    /// （投球1球ごとの球威・コース難度）。firstRunner/secondRunner/field は一塁走者の二塁封殺
    /// （<see cref="BuntResult.ForceOutAtSecond"/>）・二塁走者の三塁封殺（<see cref="BuntResult.ForceOutAtThird"/>）
    /// 判定用（issue bunt-no-take-on-ball-pitches）。field は三塁走者の本塁レースにも必須。
    /// <paramref name="fielders"/>・<paramref name="fieldingCoeff"/> は打球の物理レース（issue #1092）に使う。</summary>
    public static SqueezeOutcome Resolve(
        Player batter, Player thirdRunner, PitcherAttributes pitcher, double pitchStuff, double courseDifficulty,
        Player? firstRunner, Player? secondRunner, FieldGeometry field,
        IReadOnlyList<Fielder> fielders, FieldingCoefficients fieldingCoeff,
        double wasteProbability, BaserunningCoefficients c, IRandomSource rng)
    {
        // ウエストを読まれた場合: 打者はバントできず、飛び出した三塁走者が挟殺される大惨事。
        if (MathUtil.Chance(MathUtil.Clamp(wasteProbability, 0.0, 1.0), rng))
        {
            return new SqueezeOutcome(0, BatterOut: false, RunnerOut: true, BuntResult.MissedBunt) { Wasted = true };
        }

        // thirdRunner は常に null（本塁封殺は判定しない）: 三塁走者はこのメソッド自身が下で本塁レースを
        // 独立に解くため、BuntResolver側の本塁封殺判定と二重にしない（issue bunt-no-take-on-ball-pitches）。
        var bunt = BuntResolver.Resolve(
            batter, pitcher, pitchStuff, courseDifficulty, safety: false,
            firstRunner, secondRunner, thirdRunner: null, field, fielders, fieldingCoeff, c, rng,
            out var buntScatter, out var buntFielding);
        switch (bunt)
        {
            case BuntResult.SacrificeSuccess:
            case BuntResult.InfieldHit:
            case BuntResult.ForceOutAtSecond:
            case BuntResult.ForceOutAtThird:
            case BuntResult.FieldersChoice:
                // ボールが実際にプレーされた: 守備は送球先を1つだけ選ぶ（issue #1258）。本塁へ投げるか、一塁側
                // （一塁／封殺）の処理だけを行うか。二重に投げてアウトを2つ取らない。
                // 処理点・処理時刻・処理野手の肩は BuntResolver が物理レースで引いた同じ situation を使う
                // （issue #1092。二重に処理野手を決め直さない＝判定＝絵の単一ソース）。
                var homeSafeP = HomePlayResolver.SuccessProbability(
                    thirdRunner, fromBase: 3, buntFielding!.Value.Situation, field, c,
                    HomePlayResolver.HomeParams(c), c.SqueezeThirdRunnerStartDelaySeconds);
                // 乱数消費は送球先によらず常に1回（本塁へ投げないときは使わない）。
                var homeSafe = MathUtil.Chance(homeSafeP, rng);
                if (homeSafeP >= c.SqueezeHomeThrowSafeThreshold)
                {
                    // 走者は間に合う＝本塁へは投げない。一塁側の結果はそのまま、三塁走者は生還する。
                    return new SqueezeOutcome(1, BatterOut: bunt == BuntResult.SacrificeSuccess, RunnerOut: false, bunt)
                        with { BuntThrowScatter = buntScatter, BuntFielding = buntFielding };
                }
                // 本塁へ投げた（一塁側は何も処理しない＝打者は一塁で生きる）。刺せば野選（ForceOutAtHome＝三塁走者アウト・
                // 打者生存）、刺せなければ走者生還。打者が一塁で間に合っていた形（内野安打）なら安打、それ以外は野選。
                var afterHomeThrow = bunt == BuntResult.InfieldHit ? BuntResult.InfieldHit : BuntResult.FieldersChoice;
                return (homeSafe
                    ? new SqueezeOutcome(1, BatterOut: false, RunnerOut: false, afterHomeThrow)
                    : new SqueezeOutcome(0, BatterOut: false, RunnerOut: true, BuntResult.ForceOutAtHome))
                    with { BuntThrowScatter = buntScatter, BuntFielding = buntFielding };
            case BuntResult.PopOut:
            case BuntResult.PopOutDoubledOff:
                // 小フライ→三塁走者は動かず釘付けのまま（RunnerOutは三塁走者専用のフィールドなのでfalse固定）。
                // 一塁走者が併殺で刺された場合（PopOutDoubledOff）は GameEngine 側が bunt の値そのものを見て
                // 一塁走者の退場・追加アウトを別途処理する（本塁封殺の危険は依然スコープ外）。
                return new SqueezeOutcome(0, BatterOut: true, RunnerOut: false, bunt)
                    with { BuntThrowScatter = buntScatter, BuntFielding = buntFielding };
            case BuntResult.Foul:
                // ファウルはボールデッド: 三塁走者は三塁へ戻る（アウトにならない）。カウントはストライク（AtBatSession）。
                return new SqueezeOutcome(0, BatterOut: false, RunnerOut: false, bunt)
                    with { BuntThrowScatter = buntScatter, BuntFielding = buntFielding };
            default: // MissedBunt（打者の空振り）: 飛び出した三塁走者は挟殺される。
                return new SqueezeOutcome(0, BatterOut: false, RunnerOut: true, bunt)
                    with { BuntThrowScatter = buntScatter, BuntFielding = buntFielding };
        }
    }
}
