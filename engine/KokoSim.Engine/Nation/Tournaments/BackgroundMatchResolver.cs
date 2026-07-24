using KokoSim.Engine.Core;
using KokoSim.Engine.Match.Game;
using KokoSim.Engine.Nation.Roster;
using KokoSim.Engine.Players;
using KokoSim.Engine.Stats;

namespace KokoSim.Engine.Nation.Tournaments;

/// <summary>
/// 裏試合フルシム化の標準実装（設計書05 §1.4 / #43）。両校の永続ロスター（<see cref="NationRosters"/>）から
/// Team を組み、<see cref="GameEngine.Play"/> で1球単位に解決し、ボックススコアを全国通算成績へ畳み込む。
/// 敵AI采配（<see cref="IEnemyBrainFactory"/>）を注入すれば全校の継投・交代も駆動する（既定 null＝無指示）。
/// </summary>
public sealed class BackgroundMatchResolver : IBackgroundMatchResolver
{
    private readonly NationRosters _rosters;
    private readonly GameContext _ctx;
    private readonly NationTournamentStats? _stats;
    private readonly int _yearIndex;
    private readonly ModernRules? _modernRules;
    private readonly int? _calendarYear;
    private readonly IEnemyBrainFactory? _brains;
    private readonly System.Action<int, int, GameResult>? _onMatch;
    private readonly System.Action? _afterMatch;

    /// <param name="onMatch">
    /// 解決した各裏試合の (先攻校ID, 後攻校ID, 結果) を受け取る任意コールバック（記録の解像度2層, Q15未決2）。
    /// Shell が自校関与＋注目試合のボックススコア/継投履歴を選択保存するのに使う（null＝記録なし＝成績のみ）。
    /// </param>
    /// <param name="afterMatch">
    /// 各裏試合を1つ解決し終えるたびに呼ぶ任意フック（#208）。Shell の背景ワーカーが「試合と試合の合間で
    /// スロットル状態を見てスピン待機／即継続する」ために使う＝ライブ観戦中はここで即座に手を止められ、
    /// 県丸ごとを走り切ってしまわない（stop-the-world GC の連発を止める）。結果・乱数には触れない＝決定論不変。
    /// </param>
    public BackgroundMatchResolver(
        NationRosters rosters, GameContext ctx, int yearIndex,
        NationTournamentStats? stats = null, ModernRules? modernRules = null, int? calendarYear = null,
        IEnemyBrainFactory? brains = null, System.Action<int, int, GameResult>? onMatch = null,
        System.Action? afterMatch = null)
    {
        _rosters = rosters;
        _ctx = ctx;
        _stats = stats;
        _yearIndex = yearIndex;
        _modernRules = modernRules;
        _calendarYear = calendarYear;
        _brains = brains;
        _onMatch = onMatch;
        _afterMatch = afterMatch;
    }

    public GameResult Resolve(School away, School home, IRandomSource rng, TournamentMatchContext? context = null)
    {
        var awayAceRest = AceRestContext.From(context, home.Tier);
        var homeAceRest = AceRestContext.From(context, away.Tier);
        var awayTeam = WithBrain(_rosters.TeamFor(away, _yearIndex, _modernRules, _calendarYear, awayAceRest), away);
        var homeTeam = WithBrain(_rosters.TeamFor(home, _yearIndex, _modernRules, _calendarYear, homeAceRest), home);
        var result = GameEngine.Play(awayTeam, homeTeam, _ctx, rng.Fork(2));
        _stats?.FoldMatch(away.Id, home.Id, result);
        _onMatch?.Invoke(away.Id, home.Id, result);
        _afterMatch?.Invoke();   // #208: 試合の合間でワーカーがスロットル（Paused=停止）を反映できる継ぎ目。
        return result;
    }

    private Team WithBrain(Team team, School school)
        => _brains is null ? team : team with { Tactics = _brains.BrainFor(school) };
}

/// <summary>
/// 学校ごとの敵AI采配ブレインを供給する継ぎ目（設計書11 / #40）。エンジンは敵AIの実装（EnemyAiFactory は
/// Shell 側にある）に依存しないため、注入で疎結合に保つ。null なら無指示（従来の裏試合挙動）。
/// </summary>
public interface IEnemyBrainFactory
{
    Match.Tactics.ITacticsBrain? BrainFor(School school);
}
