using KokoSim.Engine.Core;
using KokoSim.Engine.Match.Field;
using KokoSim.Engine.Players;

namespace KokoSim.Engine.Match.Game;

/// <summary>スクイズの結果。</summary>
public readonly record struct SqueezeOutcome(int Runs, bool BatterOut, bool RunnerOut, BuntResult Bunt);

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
    /// 判定用（issue bunt-no-take-on-ball-pitches）。field は三塁走者の本塁レースにも必須。</summary>
    public static SqueezeOutcome Resolve(
        Player batter, Player thirdRunner, PitcherAttributes pitcher, double pitchStuff, double courseDifficulty,
        Player? firstRunner, Player? secondRunner, FieldGeometry field, double wasteProbability,
        BaserunningCoefficients c, IRandomSource rng)
    {
        // ウエストを読まれた場合: 打者はバントできず、飛び出した三塁走者が挟殺される大惨事。
        if (MathUtil.Chance(MathUtil.Clamp(wasteProbability, 0.0, 1.0), rng))
        {
            return new SqueezeOutcome(0, BatterOut: false, RunnerOut: true, BuntResult.MissedBunt);
        }

        // thirdRunner は常に null（本塁封殺は判定しない）: 三塁走者はこのメソッド自身が下で本塁レースを
        // 独立に解くため、BuntResolver側の本塁封殺判定と二重にしない（issue bunt-no-take-on-ball-pitches）。
        var bunt = BuntResolver.Resolve(
            batter, pitcher, pitchStuff, courseDifficulty, safety: false,
            firstRunner, secondRunner, thirdRunner: null, field, c, rng);
        switch (bunt)
        {
            case BuntResult.SacrificeSuccess:
            case BuntResult.InfieldHit:
            case BuntResult.ForceOutAtSecond:
            case BuntResult.ForceOutAtThird:
                // ボールが実際にプレーされた（一塁側の帰趨に関係なく）: 三塁走者は走塁判断のスタート込みで
                // 本塁レースを解く（design-02 §4.4「バント成功率×三塁走者スタート」, issue bunt-no-take-on-ball-pitches）。
                var runnerSafe = MathUtil.Chance(
                    HomePlayResolver.SuccessProbability(
                        thirdRunner, fromBase: 3, BuntResolver.FieldedSituation(c), field, c,
                        HomePlayResolver.HomeParams(c), c.SqueezeThirdRunnerStartDelaySeconds),
                    rng);
                // 打者は犠打成功なら1アウト、内野安打/一塁封殺/二塁封殺（打者以外が封殺されただけ）なら生きる。
                var batterOut = bunt == BuntResult.SacrificeSuccess;
                return runnerSafe
                    ? new SqueezeOutcome(1, BatterOut: batterOut, RunnerOut: false, bunt)
                    : new SqueezeOutcome(0, BatterOut: batterOut, RunnerOut: true, bunt);
            case BuntResult.PopOut:
            case BuntResult.PopOutDoubledOff:
                // 小フライ→三塁走者は動かず釘付けのまま（RunnerOutは三塁走者専用のフィールドなのでfalse固定）。
                // 一塁走者が併殺で刺された場合（PopOutDoubledOff）は GameEngine 側が bunt の値そのものを見て
                // 一塁走者の退場・追加アウトを別途処理する（本塁封殺の危険は依然スコープ外）。
                return new SqueezeOutcome(0, BatterOut: true, RunnerOut: false, bunt);
            default: // Foul / MissedBunt: 三塁走者が飛び出していれば挟殺。
                return new SqueezeOutcome(0, BatterOut: false, RunnerOut: true, bunt);
        }
    }
}
