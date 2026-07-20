using System.Collections.Generic;
using KokoSim.Engine.Match.Game;

namespace KokoSim.Engine.Match.Timeline.Playback;

/// <summary>
/// 実試合を2D俯瞰で「1試合まるごと」観戦するための供給列（実試合供給の入口）。
/// <see cref="GameResult"/> のプレー記録から、タイムラインを持つプレー（＝インプレー打球・盗塁）だけを
/// 頭から順に <see cref="PlaybackPlay"/> へ変換し、イニング・表裏・得点・打者名を添えて返す。
/// UI（Live コントローラ）はこの列を順に再生し、プレー間に間合いを置きスコアボードを更新するだけ。
///
/// 変換は <see cref="PlayTimelineAdapter"/> に委譲し、再生ハーネス（Match2DPlaybackElement/
/// MatchDetailController）には一切依存・干渉しない。Unity 非依存で dotnet テスト可能。
/// </summary>
public sealed record MatchPlaybackItem
{
    public required PlaybackPlay Play { get; init; }
    public required int Inning { get; init; }
    public required bool IsTop { get; init; }
    /// <summary>このプレー確定後の先攻/後攻の得点（スコアボード表示用）。</summary>
    public required int AwayScore { get; init; }
    public required int HomeScore { get; init; }
    public required string BatterName { get; init; }
    /// <summary>このプレーで入った点（得点シーンの強調用）。</summary>
    public required int RunsScored { get; init; }
}

public static class MatchPlaybackFeed
{
    /// <summary>試合結果を、観戦できるプレー列へ変換する（タイムライン付きプレーのみ・頭から順）。</summary>
    public static IReadOnlyList<MatchPlaybackItem> Build(GameResult result)
    {
        var items = new List<MatchPlaybackItem>();
        var away = 0;
        var home = 0;

        foreach (var e in result.Log)
        {
            // 得点は打席の発生順に積む（このプレーぶんを反映後の値を表示する）。
            if (e.IsTop) away += e.RunsScored;
            else home += e.RunsScored;

            if (e.Timeline is null) continue; // 三振・四球などタイムラインを持たない打席は観戦対象外

            items.Add(new MatchPlaybackItem
            {
                Play = PlayTimelineAdapter.ToPlaybackPlay(e.Timeline),
                Inning = e.Inning,
                IsTop = e.IsTop,
                AwayScore = away,
                HomeScore = home,
                BatterName = e.BatterName,
                RunsScored = e.RunsScored,
            });
        }

        return items;
    }
}
