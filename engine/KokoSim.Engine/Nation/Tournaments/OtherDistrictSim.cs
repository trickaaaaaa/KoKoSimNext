using System;
using System.Collections.Generic;
using System.Linq;
using KokoSim.Engine.Core;
using KokoSim.Engine.Match.Game;

namespace KokoSim.Engine.Nation.Tournaments;

/// <summary>他地区の予選を解いた結果。進出校と、解いた全試合（成績を後からまとめて積むため）。</summary>
public sealed record OtherDistrictResult(
    IReadOnlyList<School> Qualifiers,
    IReadOnlyList<OtherDistrictGame> Games);

/// <summary>他地区の1試合ぶん（先攻校ID・後攻校ID・結果）。</summary>
public readonly record struct OtherDistrictGame(int AwayId, int HomeId, GameResult Game);

/// <summary>
/// 自校が居ない地区の秋季地区予選を<b>1球フルシム</b>で解き、進出校と全試合の結果を返す（issue #1106）。
///
/// <para>これまでは集計モデル（スコアだけ）で解いており、ボックススコアが無いので勝ち上がってきた相手校の
/// 選手成績が全国の成績帳に1件も積まれなかった（HUD が「—」になる）。</para>
///
/// <para><b>成績は積まず、試合結果を集めて返す</b>。呼び出し側がメインスレッドで1回だけ積む＝背景スレッドで
/// 回しても、ロード復帰で回し直しても二重に積まれない（結果は同じシードで再現できるので、セーブには載せない）。
/// 乱数は渡された <c>rng</c> から導く（地区ごとに独立のシードを渡せば、解く順序が結果に影響しない）。</para>
/// </summary>
public static class OtherDistrictSim
{
    /// <summary>勝ち抜き戦の地区。順位上位 <paramref name="advance"/> 校が進出する。</summary>
    public static OtherDistrictResult RunKnockout(
        IReadOnlyList<School> entrants, int advance, NationCoefficients coeff, IRandomSource rng,
        TournamentSchedule schedule, string title, IBackgroundMatchResolver resolver)
    {
        if (entrants.Count <= advance) return new OtherDistrictResult(entrants, Array.Empty<OtherDistrictGame>());

        var games = new List<OtherDistrictGame>();
        var runner = TournamentRunner.Spectated(entrants, coeff, rng, schedule, title, resolver,
            onBackgroundMatch: (a, h, g) => games.Add(new OtherDistrictGame(a, h, g)));
        runner.DrainBracketResolution();

        var byId = entrants.ToDictionary(s => s.Id);
        var qualifiers = KnockoutPlacement.FromMatches(runner.BuildBracketView().Matches, id => byId[id])
            .Take(advance).ToList();
        return new OtherDistrictResult(qualifiers, games);
    }

    /// <summary>ブロックリーグ戦の地区（神奈川・issue #772）。各ブロック上位 <paramref name="advancePerBlock"/> 校が進出する。</summary>
    public static OtherDistrictResult RunBlockLeague(
        IReadOnlyList<IReadOnlyList<School>> blocks, int advancePerBlock, NationCoefficients coeff, IRandomSource rng,
        TournamentSchedule schedule, string title, IBackgroundMatchResolver resolver)
    {
        var games = new List<OtherDistrictGame>();
        var league = BlockLeagueRunner.Spectated(blocks, advancePerBlock, coeff, rng, schedule, title, resolver,
            onBackgroundMatch: (a, h, g) => games.Add(new OtherDistrictGame(a, h, g)));
        league.DrainBracketResolution();
        return new OtherDistrictResult(league.Qualifiers(), games);
    }
}
