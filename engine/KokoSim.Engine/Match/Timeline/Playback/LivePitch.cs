using KokoSim.Engine.Match.AtBat;
using KokoSim.Engine.Match.Field;
using KokoSim.Engine.Match.Game;
using KokoSim.Engine.Players;

namespace KokoSim.Engine.Match.Timeline.Playback;

/// <summary>
/// これから1球を投げる局面（設計書15 Phase D-1 のUI採用）。<b>采配窓ごと</b>——各投球の手前——に更新される。
///
/// <para><see cref="MatchProgression.AdvancePitch"/> が <see cref="AdvancePitchResult.Pitch"/> を返した
/// 時点の「いまの対戦」。<b>打席の頭（まだ1球も投げていない窓）でも埋まる</b>のが肝で、これが無いと
/// UI は1球目を投げるまで打者が誰かを知れず、打席の頭でサイン（バント・盗塁・エンドラン）を
/// 受け付けられない。投げ終えた球は <see cref="LivePitch"/> が別に運ぶ。</para>
///
/// <para><b>観測専用</b>: すべて解決済みの値の写しで、新たな抽選をしない。生成は
/// <see cref="GameContext.CapturePitches"/> が真のときだけ＝統計シム・裏試合はゼロコスト、
/// 乱数消費・digest・統計帯は不変（不変条件#2/#5）。</para>
/// </summary>
public sealed record LiveAtBat
{
    // ── 局面 ──
    public required int Inning { get; init; }
    public required bool IsTop { get; init; }
    public required int AwayScore { get; init; }
    public required int HomeScore { get; init; }
    /// <summary>アウト数（0〜2）。</summary>
    public required int Outs { get; init; }
    public bool BaseFirst { get; init; }
    public bool BaseSecond { get; init; }
    public bool BaseThird { get; init; }
    /// <summary>これから投げる球の前のカウント。</summary>
    public int Balls { get; init; }
    public int Strikes { get; init; }
    /// <summary>これから投げる球が打席内で何球目か（1始まり）。1なら打席の頭。</summary>
    public int PitchNumberInPa { get; init; }

    // ── 対戦（代打・継投をまたぐので窓ごとに載せる）──
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
}

/// <summary>
/// 投げ終えた1球（設計書15 Phase D-1 のUI採用）。<see cref="LiveAtBat"/> が「これから投げる局面」なのに対し、
/// こちらは「投げ終えた結果」——判定・球種・コース・弾道と、その球で動いたアウト・塁。
/// 誰が誰に投げたかは同じ窓の <see cref="LiveAtBat"/> が持つので、ここでは重複して持たない。
/// </summary>
public sealed record LivePitch
{
    /// <summary>いま投げた球（<see cref="PitchSequence"/> と同じ表現＝再生側は同じコードで描ける）。</summary>
    public required PitchToken Pitch { get; init; }

    /// <summary>この球で打席が決着したか（次の <see cref="MatchProgression.AdvancePitch"/> が打席を返す見込み）。</summary>
    public bool EndsPlateAppearance { get; init; }

    // ── この球を投げ終えた直後の状態（盗塁・牽制死で打席途中に動く）──
    public int Outs { get; init; }
    public bool BaseFirst { get; init; }
    public bool BaseSecond { get; init; }
    public bool BaseThird { get; init; }

    /// <summary>打席内で何球目だったか（1始まり）。</summary>
    public int PitchNumberInPa { get; init; }

    /// <summary>
    /// この球の前に起きた盗塁（二盗/三盗/本盗/重盗）の再生データ（issue #485）。盗塁企図が解決した球にだけ載る。
    /// 打球の <see cref="LiveAtBat"/>→<see cref="MatchProgression"/>.Current.Play（打席確定）と違い、盗塁は
    /// 打席途中で起きるためこの1球観測（LivePitch）が唯一の運び口。<see cref="TimelineBuilder.BuildSteal"/> の
    /// 出力を <see cref="PlayTimelineAdapter.ToPlaybackPlay"/> で変換した純関数の写し＝乱数消費ゼロ・帯不変。
    /// <see cref="GameContext.CaptureTimelines"/> が真のときだけ非null（統計シム・裏試合はゼロコスト）。
    /// </summary>
    public PlaybackPlay? StealPlay { get; init; }

    /// <summary>
    /// この球の前に起きた牽制（アウト/セーフいずれも, issue #486）の再生データ。盗塁企図の裏で
    /// <see cref="Game.PickoffResolver.Resolve"/> が発火した球にだけ載る（判定結果に関わらず載る＝
    /// アウト/セーフで再生の有無を分岐させない、2026-08-05 issue決定）。<see cref="StealPlay"/> と同じ球に
    /// 同時に載ることがある（牽制がセーフだった直後、同じ球で盗塁企図の勝負に入るため）——再生側は
    /// <see cref="PickoffPlay"/>→<see cref="StealPlay"/> の順で連結して再生する。
    /// <see cref="TimelineBuilder.BuildPickoff"/> の出力を <see cref="PlayTimelineAdapter.ToPlaybackPlay"/> で
    /// 変換した純関数の写し＝乱数消費ゼロ・帯不変。<see cref="GameContext.CaptureTimelines"/> が真のときだけ非null。
    /// </summary>
    public PlaybackPlay? PickoffPlay { get; init; }
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
        // 狙い位置（Issue #396）: PitchPlan.AimX/AimY の写し。捕手は投球前からこの狙いを知っているため
        // 対決ビューの投球前の構えに使ってよい（実着弾位置は引き続きリリース後にしか使わせない）。
        return new PitchToken(p.Kind, p.BallsAfter, p.StrikesAfter, p.PitchType, p.VelocityKmh,
            foulFlight, p.LocationX, p.LocationY, p.AimX, p.AimY, p.Trajectory);
    }
}
