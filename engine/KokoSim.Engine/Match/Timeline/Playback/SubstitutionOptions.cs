using System.Collections.Generic;
using KokoSim.Engine.Match.Field;
using KokoSim.Engine.Players;

namespace KokoSim.Engine.Match.Timeline.Playback;

/// <summary>代走の対象になれる塁上の走者（BaseIndex: 0=一塁, 1=二塁, 2=三塁）。</summary>
public readonly record struct RunnerChoice(int BaseIndex, Player Runner);

/// <summary>
/// 今この打席境界で監督が選べる選手交代の一覧（設計書09 §6）。<see cref="MatchProgression.SubstitutionOptions"/>
/// が返す観測データで、これ自体は試合結果に影響しない（乱数も消費しない）。
///
/// UIは「攻撃中なら代打／代走」「守備中なら投手交代／守備交代／DH解除」を出し分け、Can* が false の
/// ボタンは無効化して <see cref="BlockedReasonFor"/> の1行を添える。
/// </summary>
public sealed class SubstitutionOptions
{
    /// <summary>この交代を行うチームが先攻(away)か。</summary>
    public required bool TeamIsAway { get; init; }

    /// <summary>そのチームが次に攻撃するか（＝代打・代走が出せる局面か）。false なら守備側の交代のみ。</summary>
    public required bool IsOffense { get; init; }

    /// <summary>試合が終了していて交代できない。</summary>
    public required bool IsFinished { get; init; }

    /// <summary>現在の打順9人（交代反映済み・スロット0-8）。</summary>
    public required IReadOnlyList<Player> Lineup { get; init; }

    /// <summary>次打者の打順スロット（0-8）。代打はこのスロットを置き換える。</summary>
    public required int UpcomingBatterSlot { get; init; }

    /// <summary>現在の投手。</summary>
    public required Player CurrentPitcher { get; init; }

    /// <summary>投手が入っている打順スロット（DH制では -1＝打順外）。</summary>
    public required int PitcherSlot { get; init; }

    /// <summary>DH制が生きているか（DH解除後は false＝不可逆）。</summary>
    public required bool UsesDh { get; init; }

    /// <summary>DHの打順スロット（DH制のときだけ意味を持つ）。</summary>
    public required int DhSlot { get; init; }

    /// <summary>代走に出せる塁上の走者（半イニングが終わっていれば空）。</summary>
    public required IReadOnlyList<RunnerChoice> Runners { get; init; }

    /// <summary>使える野手控え（退場済みは含まない＝リエントリー禁止）。</summary>
    public required IReadOnlyList<Player> Bench { get; init; }

    /// <summary>まだ登板していない控え投手（登板済み・退場済みは含まない）。</summary>
    public required IReadOnlyList<Player> Bullpen { get; init; }

    /// <summary>もう使えない控え野手（出場済み＝リエントリー禁止）。UIはグレーアウトして選ばせない。</summary>
    public required IReadOnlyList<Player> UsedBench { get; init; }

    /// <summary>もう使えない控え投手（登板済み）。UIはグレーアウトして選ばせない。</summary>
    public required IReadOnlyList<Player> UsedBullpen { get; init; }

    public bool CanPinchHit => !IsFinished && IsOffense && Bench.Count > 0;
    public bool CanPinchRun => !IsFinished && IsOffense && Bench.Count > 0 && Runners.Count > 0;
    public bool CanChangePitcher => !IsFinished && !IsOffense && Bullpen.Count > 0;
    public bool CanDefensiveSub => !IsFinished && !IsOffense && Bench.Count > 0;
    public bool CanReleaseDh => !IsFinished && !IsOffense && UsesDh;

    /// <summary>その交代が今できない理由を1行で返す（できるときは null）。UIの無効化ボタンの説明に使う。</summary>
    public string? BlockedReasonFor(SubstitutionKind kind)
    {
        if (IsFinished) return "試合が終了している。";
        switch (kind)
        {
            case SubstitutionKind.PinchHit:
                if (!IsOffense) return "守備中は代打を送れない。";
                return Bench.Count == 0 ? "控えの野手が残っていない。" : null;
            case SubstitutionKind.PinchRun:
                if (!IsOffense) return "守備中は代走を送れない。";
                if (Runners.Count == 0) return "塁上に走者がいない。";
                return Bench.Count == 0 ? "控えの野手が残っていない。" : null;
            case SubstitutionKind.ChangePitcher:
                if (IsOffense) return "攻撃中は投手交代できない。";
                return Bullpen.Count == 0 ? "登板できる控え投手が残っていない。" : null;
            case SubstitutionKind.DefensiveSub:
                if (IsOffense) return "攻撃中は守備交代できない。";
                return Bench.Count == 0 ? "控えの野手が残っていない。" : null;
            case SubstitutionKind.ReleaseDh:
                if (IsOffense) return "攻撃中はDHを解除できない。";
                return UsesDh ? null : "DHを使っていない（または解除済み）。";
            default:
                return null;
        }
    }

    /// <summary>DH解除でDHの選手が就ける守備位置（今その位置を守っている選手が退場する）。</summary>
    public IReadOnlyList<FieldPosition> DhFieldingChoices()
    {
        var list = new List<FieldPosition>();
        if (!UsesDh) return list;
        for (var i = 0; i < Lineup.Count; i++)
        {
            if (i == DhSlot) continue;
            var pos = Lineup[i].Position;
            if (pos == FieldPosition.Pitcher) continue;   // 投手はDH解除で自分が打順へ入る
            list.Add(pos);
        }
        return list;
    }
}

/// <summary>交代の種別（UIのボタン単位）。</summary>
public enum SubstitutionKind
{
    PinchHit,
    PinchRun,
    ChangePitcher,
    DefensiveSub,
    ReleaseDh,
}
