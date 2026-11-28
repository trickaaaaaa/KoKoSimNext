using System.Collections.Generic;
using KokoSim.Engine.Players;

namespace KokoSim.Engine.Match.Game;

/// <summary>
/// 試合中の「約束の見張り」（設計書04 §2.4「入部の条件（約束）」）。スカウトで交わした約束を、采配AI（委任・裏解決）が
/// 破らないようにするための選手ID（<see cref="Player.SourceId"/>）の集合。エンジンは采配AIの判断を適用する前に
/// これで除外する＝判断そのもの（乱数の消費）は変えず、結果だけを約束に沿わせる。
///
/// <para>自校だけが持つ（Shell が公式戦のときだけ組む）。AI校・裏試合・統計シムは null＝従来と1ビットも変わらない。
/// プレイヤーが手で行う交代・サインはここを通らない（画面の警告と確認で守る）。</para>
/// </summary>
public sealed class MatchPromiseGuard
{
    private readonly HashSet<int> _noPinchHitFor;
    private readonly HashSet<int> _noBunt;
    private readonly HashSet<int> _noEntry;
    private readonly HashSet<int> _noPitch;

    /// <param name="noPinchHitFor">代打を送らない打者（「代打を出さないで」）。</param>
    /// <param name="noBunt">バント・スクイズのサインを出さない打者（「バントさせないで」）。</param>
    /// <param name="noEntry">途中から出さない控え（「1年夏は出さないで」）。</param>
    /// <param name="noPitch">登板させない選手（「連投させないで」で前日に投げた子）。</param>
    public MatchPromiseGuard(
        IEnumerable<int> noPinchHitFor, IEnumerable<int> noBunt, IEnumerable<int> noEntry, IEnumerable<int> noPitch)
    {
        _noPinchHitFor = new HashSet<int>(noPinchHitFor);
        _noBunt = new HashSet<int>(noBunt);
        _noEntry = new HashSet<int>(noEntry);
        _noPitch = new HashSet<int>(noPitch);
    }

    /// <summary>見張る約束が1つも無いか（無ければ Team に載せない）。</summary>
    public bool IsEmpty => _noPinchHitFor.Count == 0 && _noBunt.Count == 0 && _noEntry.Count == 0 && _noPitch.Count == 0;

    /// <summary>見張る選手IDの4集合（保存した試合の再生用に中身をそのまま取り出す）。</summary>
    public IReadOnlyCollection<int> NoPinchHitForIds => _noPinchHitFor;
    public IReadOnlyCollection<int> NoBuntIds => _noBunt;
    public IReadOnlyCollection<int> NoEntryIds => _noEntry;
    public IReadOnlyCollection<int> NoPitchIds => _noPitch;

    /// <summary>この打者に代打を送ると約束を破るか。</summary>
    public bool BlocksPinchHitFor(Player? batter) => Has(_noPinchHitFor, batter);

    /// <summary>この打者にバント・スクイズをさせると約束を破るか。</summary>
    public bool BlocksBunt(Player? batter) => Has(_noBunt, batter);

    /// <summary>この控えを途中から出すと約束を破るか。</summary>
    public bool BlocksEntry(Player? sub) => Has(_noEntry, sub);

    /// <summary>この選手を登板させると約束を破るか（途中出場そのものが禁じられている子も含む）。</summary>
    public bool BlocksPitching(Player? p) => Has(_noPitch, p) || Has(_noEntry, p);

    private static bool Has(HashSet<int> set, Player? p) => p?.SourceId is { } id && set.Contains(id);
}
