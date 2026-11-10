using System.Collections.Generic;
using KokoSim.Engine.Players;

namespace KokoSim.Engine.Match.Batting;

/// <summary>
/// 球種の慣れ（設計書02 §2.3b 案A・issue #836）: 打者×投手の試合内蓄積（球種ごとの「見慣れ度」）。
/// 同一投手との対戦回数の慣れ（<see cref="ContactModel.FamiliarityEffect"/>）とは別軸で、配球で同じ球種が
/// 連投されるほど、その球種だけ見慣れ度が指数移動平均で溜まる（緩急・球種の散らしに意味が生まれる）。
///
/// <para>継投で投手が替われば新しいインスタンスを使う（<see cref="Game.TeamState.PitchFamiliarityTrackerFor"/>
/// が打者×投手キーで新規作成する）＝自然に0から。乱数は一切消費しない（純粋な状態機械）。</para>
/// </summary>
public sealed class PitchFamiliarityTracker
{
    private readonly Dictionary<PitchType, double> _levels = new();

    /// <summary>この球種の現在の見慣れ度（直前までの投球履歴から。この球自体はまだ含まない）。未記録は0。</summary>
    public double Level(PitchType type) => _levels.TryGetValue(type, out var v) ? v : 0.0;

    /// <summary>
    /// 1球を履歴へ畳み込む（毎球呼ぶ）: 全球種を <paramref name="decay"/> 倍に減衰させ、来た球種だけ +1 する。
    /// 式（設計書02 §2.3b 案A）: 見慣れ度[t] ← λ×見慣れ度[t] + (来た球種==t ? 1 : 0)。
    /// </summary>
    public void Advance(PitchType thrown, double decay)
    {
        if (_levels.Count > 0)
        {
            // Dictionary は反復中に値だけ書き換えてよい（キーの追加・削除は不可）。
            var keys = new PitchType[_levels.Count];
            _levels.Keys.CopyTo(keys, 0);
            foreach (var k in keys) _levels[k] *= decay;
        }
        _levels[thrown] = Level(thrown) + 1.0;
    }
}
