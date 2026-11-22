using KokoSim.Engine.Core;

namespace KokoSim.Engine.Career;

/// <summary>
/// マネージャー（NPC・who: team_manager）の入れ替わり（design-04 §1.3a・issue #1430）。
/// 赴任すると1人付き、見た目（10人）は無作為・学年は場面で決まる。3年の夏が終わると1年の新マネージャーに
/// 入れ替わり、引き継ぎイベント（<see cref="EventId"/>）が立つ。
///
/// <para>抽選はすべて呼び出し側が<b>Fork した専用ストリーム</b>で渡した <see cref="IRandomSource"/> を使う
/// （ドラフトと同じ扱い・不変条件#2＝本編の乱数列を乱さない）。<see cref="System.Random"/> は使わない。</para>
/// </summary>
public static class TeamManagerLifecycle
{
    /// <summary>立ち絵の人数（tm01〜tm10）。</summary>
    public const int PortraitCount = 10;

    /// <summary>引き継ぎイベントの data/events.yaml 上の id（<see cref="Season.Events.EventRoles.TeamManager"/> と
    /// <see cref="Season.Events.EventRoles.TeamManagerOutgoing"/> の両方が話す会話）。</summary>
    public const string EventId = "team_manager_succession";

    /// <summary>
    /// 新設校スタート（はじめから）: 1年が付く。前任が居ないので見た目は制約なしで無作為に選ぶ。
    /// </summary>
    public static void AssignForNewSchool(Manager m, IRandomSource rng)
    {
        m.TeamManagerGrade = 1;
        m.TeamManagerPortrait = PickPortrait(rng, exclude: 0);
    }

    /// <summary>
    /// 転任で既存の学校に赴任: 2年か3年のどちらかが無作為に付く。見た目は前任（前の学校のマネージャー）と違う人。
    /// </summary>
    public static void AssignForTransfer(Manager m, IRandomSource rng)
    {
        var previous = m.TeamManagerPortrait;
        m.TeamManagerGrade = rng.NextInt(0, 2) == 0 ? 2 : 3;
        m.TeamManagerPortrait = PickPortrait(rng, exclude: previous);
    }

    /// <summary>3年の夏が終わって引退する学年か（新チーム発足＝<see cref="Season.RosterLifecycle.BeginNewTeam"/> と同じ判定タイミング）。</summary>
    public static bool IsRetiring(Manager m) => m.TeamManagerGrade >= 3;

    /// <summary>
    /// 3年の夏の引退→新しいマネージャー（1年）に入れ替える。引退する側の番号を
    /// <see cref="Manager.OutgoingTeamManagerPortrait"/> へ控える（引き継ぎイベントの立ち絵用）。
    /// 呼ぶ前に <see cref="IsRetiring"/> を確かめること。
    /// </summary>
    public static void Succeed(Manager m, IRandomSource rng)
    {
        var outgoing = m.TeamManagerPortrait;
        m.OutgoingTeamManagerPortrait = outgoing;
        m.TeamManagerGrade = 1;
        m.TeamManagerPortrait = PickPortrait(rng, exclude: outgoing);
    }

    /// <summary>
    /// 年度替わり（4月）の進級（<see cref="Season.RosterLifecycle.AdvanceYear"/> の部員の進級と同じ年次）。
    /// 3年で止める（引退→入れ替えが先に起きるはずの安全弁。乱数不使用）。
    /// </summary>
    public static void AdvanceGrade(Manager m)
    {
        if (m.TeamManagerGrade < 3) m.TeamManagerGrade++;
    }

    /// <summary>1〜<see cref="PortraitCount"/> から exclude（1〜<see cref="PortraitCount"/> 範囲外なら制約なし）を除いて無作為に1つ選ぶ。</summary>
    private static int PickPortrait(IRandomSource rng, int exclude)
    {
        if (exclude < 1 || exclude > PortraitCount) return rng.NextInt(1, PortraitCount + 1);
        var n = rng.NextInt(1, PortraitCount); // exclude を除いた (PortraitCount-1) 人から1〜(PortraitCount-1)
        return n >= exclude ? n + 1 : n;
    }
}
