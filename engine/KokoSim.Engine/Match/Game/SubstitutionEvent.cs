using System.Collections.Generic;
using KokoSim.Engine.Match.Field;
using KokoSim.Engine.Players;

namespace KokoSim.Engine.Match.Game;

/// <summary>交代1件の種類（issue #920 交代テロップ）。</summary>
public enum SubstitutionEntryKind
{
    /// <summary>代打（控えが打順へ入って打席に立つ）。</summary>
    PinchHit,
    /// <summary>代走（控えが塁上の走者と入れ替わる）。</summary>
    PinchRun,
    /// <summary>守備交代（控えが守備位置へ入る。守備固め・ベンチからの補充を含む）。</summary>
    DefensiveSub,
    /// <summary>投手交代（控えから・在フィールドの野手から、どちらも）。</summary>
    Relief,
    /// <summary>出場中の選手の守備位置が変わった（コンバート・投野交代で投手が守備へ回った等）。</summary>
    PositionChange,
    /// <summary>DH解除（投手が打順へ入る）。</summary>
    DhRelease,
}

/// <summary>交代に出てくる選手1人（表示に要る分だけ）。</summary>
public sealed record SubstitutionPlayer(int? SourceId, int Number, string Name);

/// <summary>
/// 交代1件。<see cref="Order"/> は打順（1-9）で、DH制の投手（打順外）は 0。
/// <see cref="Position"/> は<b>いま就いた位置</b>（試合で実際に就いた位置＝表示してよい位置。DHの枠は
/// <see cref="FieldPosition.DesignatedHitter"/>）。代打・代走は守備に就いていないが、引き継いだ位置が入る
/// （表示側が種類で出し分ける）。<see cref="Out"/> は退いた選手（誰も退かない守備位置の変更は null）。
/// </summary>
public sealed record SubstitutionEntry(
    SubstitutionEntryKind Kind, int Order, SubstitutionPlayer In, SubstitutionPlayer? Out, FieldPosition Position);

/// <summary>
/// 同じ瞬間に1チームで起きた交代のまとまり（issue #920）。<see cref="IsTopHalf"/> は起きた（＝これから進む）
/// 半イニング、<see cref="TeamIsAway"/> は交代したチームが先攻か。観測専用＝乱数を使わず、試合結果・
/// 決定論digest・統計帯に影響しない（<see cref="GameProgress.Substitutions"/>）。
/// </summary>
public sealed record SubstitutionEvent(int Inning, bool IsTopHalf, bool TeamIsAway, IReadOnlyList<SubstitutionEntry> Entries);

/// <summary>
/// 1チームの出場選手の写し（打順9枠＋投手＋DHの有無）。前回の写しと今を比べて交代を割り出す
/// （<see cref="GameProgress.ObserveSubstitutions(int, bool)"/>）。交代の経路（プレイヤー采配・AI・取り消しの再生）を
/// 問わず同じ規則で拾えるよう、経路ごとに記録を足すのでなく差分で取る。
/// </summary>
internal sealed class LineupPicture
{
    private readonly Player[] _key = new Player[9];      // 成績の帰属キー（守備位置の書き換えで変わらない本人）
    private readonly Player[] _player = new Player[9];   // その時点の実体（表示用）
    private readonly FieldPosition[] _pos = new FieldPosition[9];
    private Player _pitcherKey = null!;
    private Player _pitcher = null!;
    private bool _usesDh;

    public static LineupPicture Of(TeamState t)
    {
        var pic = new LineupPicture();
        pic.Fill(t);
        return pic;
    }

    private void Fill(TeamState t)
    {
        var live = t.CurrentLineup;
        for (var i = 0; i < 9; i++)
        {
            _player[i] = live[i];
            _key[i] = t.AccountKey(live[i]);
            _pos[i] = t.DisplayPositionAt(i);
        }
        _pitcher = t.CurrentPitcher;
        _pitcherKey = t.AccountKey(t.CurrentPitcher);
        _usesDh = t.UsesDh;
    }

    private bool InGame(Player key)
    {
        if (key.Equals(_pitcherKey)) return true;
        foreach (var k in _key) if (k.Equals(key)) return true;
        return false;
    }

    private bool InOrder(Player key)
    {
        foreach (var k in _key) if (k.Equals(key)) return true;
        return false;
    }

    private static SubstitutionPlayer Ref(Player p) => new(p.SourceId, p.UniformNumber, p.Name);

    /// <summary>
    /// 前の写し <paramref name="prev"/> から今の写し <paramref name="cur"/> への変化を交代の列にする（変化なしは空）。
    /// 並びは打順順で、DH制の投手（打順外）の交代は末尾。
    /// </summary>
    public static List<SubstitutionEntry> Diff(LineupPicture prev, LineupPicture cur, TeamState t)
    {
        var list = new List<(SubstitutionEntry Entry, Player? OutKey)>();
        var pitcherChanged = !cur._pitcherKey.Equals(prev._pitcherKey);
        for (var i = 0; i < 9; i++)
        {
            var inKey = cur._key[i];
            var outKey = prev._key[i];
            if (!inKey.Equals(outKey))
            {
                var outRef = cur.InGame(outKey) ? null : Ref(prev._player[i]);
                SubstitutionEntryKind kind;
                if (!prev.InGame(inKey))
                {
                    // 控えから入った。入り方は打撃成績の起用区分が正（代打・代走・守備交代）。
                    // 区分を持たない入り方（継投・ベンチからの補充）は、投手になったかどうかで分ける。
                    kind = t.EntryKindOf(cur._player[i]) switch
                    {
                        BoxEntry.PinchHit => SubstitutionEntryKind.PinchHit,
                        BoxEntry.PinchRun => SubstitutionEntryKind.PinchRun,
                        BoxEntry.DefensiveSub => SubstitutionEntryKind.DefensiveSub,
                        _ => inKey.Equals(cur._pitcherKey) ? SubstitutionEntryKind.Relief : SubstitutionEntryKind.DefensiveSub,
                    };
                }
                else
                {
                    // 出場中の選手がこの枠へ来た。DH解除で投手が打順へ入ったときだけ DH解除、他は位置の変更。
                    kind = inKey.Equals(prev._pitcherKey) && !prev.InOrder(inKey) && prev._usesDh && !cur._usesDh
                        ? SubstitutionEntryKind.DhRelease
                        : SubstitutionEntryKind.PositionChange;
                }
                list.Add((new SubstitutionEntry(kind, i + 1, Ref(cur._player[i]), outRef, cur._pos[i]), outRef is null ? null : outKey));
            }
            else if (cur._pos[i] != prev._pos[i])
            {
                // 同じ選手のまま位置だけ動いた。新しく投手になったなら継投（在フィールドの野手の登板）。
                var relief = pitcherChanged && inKey.Equals(cur._pitcherKey);
                var outRef = relief && !cur.InGame(prev._pitcherKey) ? Ref(prev._pitcher) : null;
                list.Add((new SubstitutionEntry(
                    relief ? SubstitutionEntryKind.Relief : SubstitutionEntryKind.PositionChange,
                    i + 1, Ref(cur._player[i]), outRef, cur._pos[i]), outRef is null ? null : prev._pitcherKey));
            }
        }
        // DH制の投手（打順外）の交代。
        if (pitcherChanged && !cur.InOrder(cur._pitcherKey))
        {
            var outRef = cur.InGame(prev._pitcherKey) ? null : Ref(prev._pitcher);
            list.Add((new SubstitutionEntry(SubstitutionEntryKind.Relief, 0, Ref(cur._pitcher), outRef, FieldPosition.Pitcher),
                outRef is null ? null : prev._pitcherKey));
        }
        // 同じ人が2件の「退く選手」に出ないようにする（ベンチ補充つきの野手登板＝退いた投手の枠に控えが入り、
        // 別の枠で野手が登板する）。退いた投手は継投の行にだけ残す。
        var result = new List<SubstitutionEntry>(list.Count);
        for (var a = 0; a < list.Count; a++)
        {
            var (e, ok) = list[a];
            if (ok is not null && e.Kind != SubstitutionEntryKind.Relief)
            {
                for (var b = 0; b < list.Count; b++)
                {
                    if (b == a || list[b].OutKey is not { } other || !other.Equals(ok)) continue;
                    if (list[b].Entry.Kind == SubstitutionEntryKind.Relief || b < a) { e = e with { Out = null }; break; }
                }
            }
            result.Add(e);
        }
        return result;
    }
}
