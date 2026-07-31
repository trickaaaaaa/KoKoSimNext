using KokoSim.Engine.Match.AtBat;
using KokoSim.Engine.Match.Field;
using KokoSim.Engine.Match.Game;
using KokoSim.Engine.Players;

namespace KokoSim.Engine.Match.Timeline.Playback;

/// <summary>
/// 投げ終えた1球ぶんの観測（設計書15 Phase D-1 のUI採用）。
/// <see cref="LivePlateAppearance"/> が「確定した打席まるごと」なのに対し、こちらは
/// <b>打席が確定する前の1球</b>——判定・球種・コース・弾道と、その時点の局面・対戦を運ぶ。
///
/// <para>試合画面が打席単位（<see cref="MatchProgression.Advance"/>）から1球単位
/// （<see cref="MatchProgression.AdvancePitch"/>）へ移るための供給口。打席単位のままだと、
/// 采配の指示は「いま見ている打席」ではなく次の打席にしか効かず、しかもバント等が選べるかどうかを
/// 打席の結果（＝これから再生する内容）から引くことになり結果を先に漏らしてしまう。</para>
///
/// <para><b>観測専用</b>: すべて既に解決済みの値の写しで、新たな抽選をしない。
/// 生成は <see cref="GameContext.CapturePitches"/> が真のときだけ＝統計シム・裏試合はゼロコスト、
/// 乱数消費・digest・統計帯は不変（不変条件#2/#5）。</para>
/// </summary>
public sealed record LivePitch
{
    // ── 局面（この球を投げ終えた直後）──
    public required int Inning { get; init; }
    public required bool IsTop { get; init; }
    public required int AwayScore { get; init; }
    public required int HomeScore { get; init; }
    /// <summary>この球の時点のアウト数（0〜2）。</summary>
    public required int Outs { get; init; }
    public bool BaseFirst { get; init; }
    public bool BaseSecond { get; init; }
    public bool BaseThird { get; init; }

    // ── 対戦（打席内で不変だが、代打・継投をまたぐので毎球載せる）──
    public int BatterOrder { get; init; }
    public required string BatterName { get; init; }
    public int? BatterSourceId { get; init; }
    public Handedness BatterBats { get; init; }
    public FieldPosition BatterPosition { get; init; }
    public int BatterNumber { get; init; }
    public double BatterConditionValue { get; init; }
    public required string PitcherName { get; init; }
    public int? PitcherSourceId { get; init; }
    public Handedness PitcherThrows { get; init; }
    public int PitcherNumber { get; init; }
    public double PitcherConditionValue { get; init; }

    /// <summary>いま投げた球（<see cref="PitchSequence"/> と同じ表現＝再生側は同じコードで描ける）。</summary>
    public required PitchToken Pitch { get; init; }

    /// <summary>この球で打席が決着したか（次の <see cref="MatchProgression.AdvancePitch"/> が打席を返す見込み）。</summary>
    public bool EndsPlateAppearance { get; init; }

    /// <summary>この球までの投球数（打席内・1始まり）。</summary>
    public int PitchNumberInPa { get; init; }
}

/// <summary>
/// <see cref="PitchRecord"/>（解決層の記録）→ <see cref="PitchToken"/>（再生層の表現）の変換。
/// 打席確定時の一括変換（<see cref="MatchProgression"/>）と1球ごとの変換（<see cref="GameEngine"/>）で
/// <b>同じ1本を使う</b>——別々に持つと、同じ球なのに描き方が食い違う。
/// </summary>
public static class PitchTokenFactory
{
    /// <param name="runnerOnFirst">ファウルの打球再生に載せる走者。判定には使わない（絵だけ）。</param>
    public static PitchToken From(PitchRecord p, GameContext ctx,
        bool runnerOnFirst, bool runnerOnSecond, bool runnerOnThird)
    {
        // ファウルの打球タイムライン（Issue #313）: 物理層（BattedBall）を持つファウルだけ、打った瞬間から
        // 着地までの短い再生を添える。守備解決は経由しない＝BuildFoulFlight は純関数で乱数を消費しない。
        PlaybackPlay? foulFlight = p.Kind == PitchKind.Foul && p.BattedBall is { } fb
            ? PlayTimelineAdapter.ToPlaybackPlay(TimelineBuilder.BuildFoulFlight(
                fb, ctx.Aerodynamics, ctx.Fielding, ctx.Field, runnerOnFirst, runnerOnSecond, runnerOnThird))
            : null;
        // コース・弾道（Issue #332）: 判定に使われた実着弾位置と観測専用弾道をそのまま渡す（新規抽選なし）。
        return new PitchToken(p.Kind, p.BallsAfter, p.StrikesAfter, p.PitchType, p.VelocityKmh,
            foulFlight, p.LocationX, p.LocationY, p.Trajectory);
    }
}
