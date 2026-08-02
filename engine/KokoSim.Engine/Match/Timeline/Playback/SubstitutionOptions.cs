using System.Collections.Generic;
using System.Linq;
using KokoSim.Engine.Match.Field;
using KokoSim.Engine.Players;

namespace KokoSim.Engine.Match.Timeline.Playback;

/// <summary>代走の対象になれる塁上の走者（BaseIndex: 0=一塁, 1=二塁, 2=三塁）。</summary>
public readonly record struct RunnerChoice(int BaseIndex, Player Runner);

/// <summary>
/// 今この打席境界で監督が選べる選手交代の一覧（設計書09 §6）。<see cref="MatchProgression.SubstitutionOptions"/>
/// が返す観測データで、これ自体は試合結果に影響しない（乱数も消費しない）。
///
/// UIは「攻撃中なら代打／代走」「守備中なら守備交代（割当）」を出し分ける（issue #256 で守備側の4タブ＝投手交代／
/// 守備交代／守備位置／DH解除は「守備の割当」1操作へ統合。継投・投野交代・守備固め・コンバート・DH解除は割当から導出）。
/// Can*／<see cref="BlockedReasonFor"/> は各操作が今できるかの観測に引き続き使う（統合後もUIの活性判定・理由表示に用いる）。
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

    /// <summary>
    /// 使える控え（退場済み・登板済みは含まない＝リエントリー禁止）。名簿順＝背番号順。
    /// 投手控えと野手控えに分けない（issue #381）＝代打・代走・守備交代・継投すべてこの1本から選ぶ。
    /// </summary>
    public required IReadOnlyList<Player> Reserves { get; init; }

    /// <summary>もう使えない控え（出場済み・登板済み）。UIはグレーアウトして選ばせない。</summary>
    public required IReadOnlyList<Player> UsedReserves { get; init; }

    /// <summary>
    /// 継投で指名できる全候補（控え＋在フィールドの守備野手。issue #256）。AIの継投（GameEngine）と
    /// 同一集合＝プレイヤーもAIと同じ人選ができる。<see cref="MatchProgression.Relieve"/> の Player 引数はこの中から選ぶ
    /// （在フィールド野手を指名すると投野交代で守備穴を backfill する）。
    /// </summary>
    public required IReadOnlyList<Player> ReliefCandidates { get; init; }

    /// <summary>
    /// 投手交代で指名できる全候補＝控え全員（issue #137: 誰でも投手として登板できる）。
    /// <see cref="MatchProgression.ChangePitcher"/> の Player 引数はこの一覧の中から選ぶ。
    /// </summary>
    public IReadOnlyList<Player> PitcherCandidates => Reserves;

    /// <summary>もう指名できない投手交代候補＝使い切った控え。</summary>
    public IReadOnlyList<Player> UsedPitcherCandidates => UsedReserves;

    public bool CanPinchHit => !IsFinished && IsOffense && Reserves.Count > 0;
    public bool CanPinchRun => !IsFinished && IsOffense && Reserves.Count > 0 && Runners.Count > 0;
    public bool CanChangePitcher => !IsFinished && !IsOffense && PitcherCandidates.Count > 0;
    public bool CanDefensiveSub => !IsFinished && !IsOffense && Reserves.Count > 0;
    public bool CanReleaseDh => !IsFinished && !IsOffense && UsesDh;

    /// <summary>
    /// 守備位置の入替（コンバート・issue #235）。控えが尽きていてもできる＝交代回数を消費しないので、
    /// 入れ替えられる枠が2つ以上あれば常に可能。
    /// </summary>
    public bool CanSwapDefense => !IsFinished && !IsOffense && SwappableSlots.Count >= 2;

    /// <summary>
    /// 守備位置を入れ替えられる打順スロット（投手・DHの枠は除く）。UIはこの集合だけをクリック可能にする。
    /// 判定は <see cref="Game.TeamState.IsSwappableDefenseSlot"/> と同じ（守備に就いている枠だけ）。
    /// </summary>
    public IReadOnlyList<int> SwappableSlots
    {
        get
        {
            var list = new List<int>(9);
            for (var i = 0; i < Lineup.Count; i++)
            {
                if (UsesDh ? i == DhSlot : i == PitcherSlot) continue;
                list.Add(i);
            }
            return list;
        }
    }

    /// <summary>
    /// コンバート継投（issue #235）＝現投手を守備へ回して控え投手を登板させる。
    /// 押し出される野手が要るので、守備に就いている枠が1つ以上＋登板候補が必要。
    /// </summary>
    public bool CanConvertRelief => !IsFinished && !IsOffense && PitcherCandidates.Count > 0
                                    && SwappableSlots.Count > 0;

    /// <summary>コンバート継投で元投手が就ける守備位置（今そこを守っている野手が退場する）。</summary>
    public IReadOnlyList<FieldPosition> ConvertTargets()
    {
        var list = new List<FieldPosition>();
        foreach (var i in SwappableSlots) list.Add(Lineup[i].Position);
        return list;
    }

    /// <summary>その交代が今できない理由を1行で返す（できるときは null）。UIの無効化ボタンの説明に使う。</summary>
    public string? BlockedReasonFor(SubstitutionKind kind)
    {
        if (IsFinished) return "試合が終了している。";
        switch (kind)
        {
            case SubstitutionKind.PinchHit:
                if (!IsOffense) return "守備中は代打を送れない。";
                return Reserves.Count == 0 ? "控えの野手が残っていない。" : null;
            case SubstitutionKind.PinchRun:
                if (!IsOffense) return "守備中は代走を送れない。";
                if (Runners.Count == 0) return "塁上に走者がいない。";
                return Reserves.Count == 0 ? "控えの野手が残っていない。" : null;
            case SubstitutionKind.DefensiveSub:
                if (IsOffense) return "攻撃中は守備交代できない。";
                return Reserves.Count == 0 ? "控えの野手が残っていない。" : null;
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

/// <summary>
/// 交代の種別。攻撃側の攻撃タブ（代打／代走）と守備側の単一操作（守備の割当）を表す。
/// issue #256 で守備側の旧4種（投手交代／守備交代／守備位置／DH解除）は「守備の割当」1操作へ統合し、
/// 種別としては <see cref="DefensiveSub"/> に集約した（継投・投野交代・守備固め・コンバート・DH解除は割当から導出）。
/// </summary>
public enum SubstitutionKind
{
    PinchHit,
    PinchRun,
    /// <summary>守備側の割当操作（issue #256）。継投・投野交代・守備固め・コンバート・DH解除の総称。</summary>
    DefensiveSub,
}
