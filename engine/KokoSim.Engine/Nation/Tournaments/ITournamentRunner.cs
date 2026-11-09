using KokoSim.Engine.Match.Game;

namespace KokoSim.Engine.Nation.Tournaments;

/// <summary>
/// 対話で進める大会の進行体の共通の口（issue #772）。勝ち抜き戦（<see cref="TournamentRunner"/>）と
/// 地区予選のブロックリーグ戦（<see cref="BlockLeagueRunner"/>）を、Shell（GameSession・ホーム・大会タブ）が
/// 同じ手順で進められるようにする。
///
/// <para>契約（両実装で同じ）:</para>
/// <list type="bullet">
/// <item>自校の試合で止まる（<see cref="NextOpponent"/>・<see cref="NextMatchDay"/>）。自校が出ない試合は背景で解く</item>
/// <item><see cref="PlayNextPlayerMatch"/>（一括）と <see cref="BeginNextPlayerMatch"/>→<see cref="CompleteNextPlayerMatch"/>
///   （ライブ観戦）は同じ大会展開になる（決定論）</item>
/// <item><see cref="Finished"/> になったら自校の大会は終わり。残りの裏試合は <see cref="BracketResolutionPending"/> の間
///   <see cref="StepBracketResolution"/> で刻む</item>
/// <item>試合と試合の間だけ <see cref="CaptureState"/> で保存できる</item>
/// </list>
/// </summary>
public interface ITournamentRunner
{
    string Title { get; }
    /// <summary>自校がまだ大会に残っているか（勝ち抜き戦＝未敗退。リーグ戦＝全節を消化するまで真）。</summary>
    bool PlayerActive { get; }
    /// <summary>自校が優勝したか（リーグ戦には優勝が無い＝常に偽）。</summary>
    bool IsChampion { get; }
    /// <summary>自校にとって大会が終了したか。</summary>
    bool Finished { get; }
    string? ChampionName { get; }
    School? NextOpponent { get; }
    /// <summary>次戦の試合日（大会開幕からの相対日）。</summary>
    int NextMatchDay { get; }
    /// <summary>出場校数（日程表の日割りの母数）。</summary>
    int FieldSize { get; }
    int TotalRounds { get; }
    /// <summary>次戦の呼び名（「準決勝」「リーグ第2戦」等）。</summary>
    string RoundName { get; }

    PlayerMatchOutcome PlayNextPlayerMatch();
    LivePlayerMatch BeginNextPlayerMatch();
    PlayerMatchOutcome CompleteNextPlayerMatch(GameResult result);
    bool HasPendingLiveMatch { get; }
    LivePlayerMatch ResumePendingLiveMatch();

    /// <summary>試合ログ（通算戦績の集計キー）＋表の形。リーグ戦は節ごとの対戦を「ラウンド」として返す。</summary>
    TournamentBracketView BuildBracketView();
    /// <summary>
    /// 現ラウンドの自校非関与カードを先に解いてメモ化 resolver を温める。<paramref name="maxParallelism"/> は
    /// 同時に解くカード数（既定1＝逐次）。カードごとに独立 Fork・ランナー状態は読み取り専用なので、
    /// 並列でも結果は逐次と一致する（BackgroundPrefetchTests）。
    /// </summary>
    void PrefetchCurrentRoundBackgroundCards(int maxParallelism = 1);

    bool BracketResolutionPending { get; }
    int RemainingBracketRounds { get; }
    void StepBracketResolution();
    void DrainBracketResolution();

    bool CanCaptureState { get; }
    TournamentRunnerState CaptureState();
}
