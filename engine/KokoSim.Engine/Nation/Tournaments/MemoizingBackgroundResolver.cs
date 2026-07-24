using System;
using System.Collections.Concurrent;
using System.Threading;
using KokoSim.Engine.Core;
using KokoSim.Engine.Match.Game;

namespace KokoSim.Engine.Nation.Tournaments;

/// <summary>
/// 裏試合フルシム結果をカード単位でメモ化するラッパ（#試合開始前ロード短縮・自県プレフェッチ用）。
/// 同一カード（先攻ID×後攻ID×ラウンド残数）は一度だけ内部 resolver で解き、以降はキャッシュを返す。
/// これにより「暇な時間に背景スレッドで先に解いて温めておき、試合開始時の <see cref="TournamentRunner"/> は
/// キャッシュ命中で即座に進む」プレフェッチが成立する（背景1球フルシムの約25秒/試合フリーズを暇時間へ移す）。
///
/// <para><b>決定論・整合性</b>: カードの結果は (先攻, 後攻, forked-rng, 台帳内容) だけで決まり、
/// forked-rng は base rng の純導出（<see cref="IRandomSource.Fork"/> は状態を進めない）。プレフェッチと本解決は
/// 同一 fork キー・同一台帳状態（そのラウンドの試合日以前の登板のみ）で呼ばれるため、どちらが先に計算しても
/// 同一結果になる。<see cref="Lazy{T}"/> で「一度だけ計算・一度だけ発行」を保証し、内部 resolver の副作用
/// （全国成績への畳み込み）も1カード1回に保つ。</para>
///
/// <para><b>スレッド安全</b>: <see cref="ConcurrentDictionary{TKey,TValue}"/>＋<see cref="Lazy{T}"/>
/// （ExecutionAndPublication）で複数スレッドからの同時 Resolve を安全にデデュープする。呼び出し側
/// （Shell）はプレフェッチ完了を join してから本解決へ入る運用のため、実際に同時計算が起きるのは
/// 稀（プレイヤーが待たずに試合へ突入した場合のフォールバック）だが、その場合も結果は一意。</para>
/// </summary>
public sealed class MemoizingBackgroundResolver : IBackgroundMatchResolver
{
    private readonly IBackgroundMatchResolver _inner;
    private readonly ConcurrentDictionary<(int Away, int Home, int RoundsRemaining), Lazy<GameResult>> _cache = new();

    public MemoizingBackgroundResolver(IBackgroundMatchResolver inner)
        => _inner = inner ?? throw new ArgumentNullException(nameof(inner));

    public GameResult Resolve(School away, School home, IRandomSource rng, TournamentMatchContext? context = null)
    {
        // カードはトーナメント内で一意（同一2校はシングルエリミで最大1回対戦）。ラウンド残数も鍵に含めて明示。
        var key = (away.Id, home.Id, context?.RoundsRemaining ?? 0);
        var lazy = _cache.GetOrAdd(key, _ => new Lazy<GameResult>(
            () => _inner.Resolve(away, home, rng, context), LazyThreadSafetyMode.ExecutionAndPublication));
        return lazy.Value;
    }
}
