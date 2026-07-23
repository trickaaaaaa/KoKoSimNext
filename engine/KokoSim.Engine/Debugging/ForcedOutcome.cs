using KokoSim.Engine.Match.AtBat;
using KokoSim.Engine.Match.Game;

namespace KokoSim.Engine.Debugging;

/// <summary>
/// 強制発動（設計書17 §6.1, F4）。<b>唯一「結果を変える」層</b>で、デバッグ経路からしか触れない。
///
/// <para><b>実装方針</b>: 該当の抽選をスキップして結果を固定する。<b>打球の物理層は偽装しない</b>（不変条件#1）。
/// 「本塁打を強制」は「本塁打になる BattedBall を注入する」ではなく「打席結果を HomeRun として確定させる」。
/// つまりこれは<b>下流（表現・記録・UI）の検証</b>に使う道具で、物理モデルの検証には使えない。</para>
///
/// <para>強制した試合は <see cref="GameResult.HasForcedOutcomes"/> が真になり、
/// 決定論ゲート（digest）と統計集計から自動的に外れる。</para>
///
/// <para><b>ここに無いもの</b>（設計書17 §6.1 のスケッチから外した理由）:
/// TriplePlay / Balk / Squeeze / EnterTieBreak / WalkOff は、単独の抽選ゲートを持たず
/// 「守備の共同解決」や「試合状況の帰結」として現れる。無理に固定すると物理層の偽装か第二の解決経路が要り、
/// どちらも不変条件#1に反する。EnterTieBreak は <c>GameContext.TieBreakEnabled</c>＋シナリオの
/// 開始イニング指定で作れるため、強制発動としては持たない。</para>
/// </summary>
public enum ForcedOutcome
{
    None = 0,

    // ── 打席結果を固定する（AtBatSession が投球ループごとスキップして確定させる） ──
    HomeRun,
    Triple,
    Double,
    Single,
    Strikeout,
    Walk,
    HitByPitch,
    /// <summary>ゴロ凡打（打席結果は InPlayOut。進塁は通常どおり下流が解く）。</summary>
    GroundOut,
    /// <summary>フライ凡打（打席結果は InPlayOut）。</summary>
    FlyOut,
    /// <summary>失策出塁（打席結果は ReachedOnError）。</summary>
    ReachedOnError,
    IntentionalWalk,

    // ── 稀プレーの発動を固定する（該当ゲートの確率を1.0にする＝抽選そのものは残す） ──
    WildPitch,
    DroppedThirdStrike,
    FieldersChoice,
    DoublePlay,
    PickoffOut,
    DoubleSteal,

    // ── 走塁（盗塁企図を強制したうえで成否を固定する） ──
    StealSuccess,
    StealCaught,
}

/// <summary>強制発動の分類と、打席結果への写像。</summary>
public static class ForcedOutcomes
{
    /// <summary>打席結果そのものを固定する種別か（投球ループをスキップする側）。</summary>
    public static bool FixesPlateAppearance(this ForcedOutcome f) => ToPlateAppearance(f) is not null;

    /// <summary>打席結果への写像（打席結果を固定しない種別は null）。</summary>
    public static PlateAppearanceResult? ToPlateAppearance(ForcedOutcome f) => f switch
    {
        ForcedOutcome.HomeRun => PlateAppearanceResult.HomeRun,
        ForcedOutcome.Triple => PlateAppearanceResult.Triple,
        ForcedOutcome.Double => PlateAppearanceResult.Double,
        ForcedOutcome.Single => PlateAppearanceResult.Single,
        ForcedOutcome.Strikeout => PlateAppearanceResult.Strikeout,
        ForcedOutcome.Walk => PlateAppearanceResult.Walk,
        ForcedOutcome.IntentionalWalk => PlateAppearanceResult.Walk,
        ForcedOutcome.HitByPitch => PlateAppearanceResult.HitByPitch,
        ForcedOutcome.GroundOut => PlateAppearanceResult.InPlayOut,
        ForcedOutcome.FlyOut => PlateAppearanceResult.InPlayOut,
        ForcedOutcome.ReachedOnError => PlateAppearanceResult.ReachedOnError,
        _ => null,
    };

    /// <summary>盗塁企図そのものを起こす種別か（走者一塁・二塁空きが前提）。</summary>
    public static bool ForcesStealAttempt(this ForcedOutcome f)
        => f is ForcedOutcome.StealSuccess or ForcedOutcome.StealCaught
            or ForcedOutcome.PickoffOut or ForcedOutcome.DoubleSteal;

    /// <summary>
    /// 稀プレーのゲート確率を1.0へ寄せた1打席ぶんの走塁係数を作る。抽選自体は残るので
    /// 乱数の消費順は「既定オンの状態」と同じになる（既定オフのゲートだけ消費が増える）。
    /// </summary>
    public static Match.Game.BaserunningCoefficients Apply(
        Match.Game.BaserunningCoefficients c, ForcedOutcome f) => f switch
    {
        ForcedOutcome.WildPitch => c with { WildPitchProb = 1.0, PassedBallProb = 0.0 },
        ForcedOutcome.DroppedThirdStrike => c with { DropThirdStrikeReachProb = 1.0, DropThirdStrikeCatchingSlope = 0.0 },
        ForcedOutcome.FieldersChoice => c with { FieldersChoiceProb = 1.0, DoublePlayProb = 0.0 },
        ForcedOutcome.DoublePlay => c with { DoublePlayProb = 1.0, SpeedSlope = 0.0 },
        ForcedOutcome.PickoffOut => c with { PickoffBaseProb = 1.0 },
        ForcedOutcome.DoubleSteal => c with { DoubleStealThirdBreakProb = 1.0 },
        _ => c,
    };

    /// <summary>enum 名（大文字小文字を無視）から解釈する。未知なら false（例外を投げない）。</summary>
    public static bool TryParse(string? name, out ForcedOutcome outcome)
        => System.Enum.TryParse(name ?? "", ignoreCase: true, out outcome)
           && System.Enum.IsDefined(typeof(ForcedOutcome), outcome);
}
