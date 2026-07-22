using System.Linq;
using KokoSim.Engine.Core;
using KokoSim.Engine.Match.Game;
using KokoSim.Engine.Nation;
using KokoSim.Engine.Nation.Tournaments;
using KokoSim.Engine.Stats;
using Xunit;

namespace KokoSim.Engine.Tests.Nation;

/// <summary>
/// 自校の先攻/後攻の決定論的な決定（OPEN-QUESTIONS 未決I, issue #70）。
/// 実装本体（Assets/KokoSim/Shell/PlayerMatchResolver）は Unity 側にあり xunit から直接参照できないため、
/// 同ファイルがすること（HomeAwayAssignment.ManagerIsAway で先攻/後攻を決め、真なら自校を away 引数で
/// GameEngine.Play/MatchProgression を呼ぶ）をここで再現する（PlayerMatchResolverTests と同じ手法）。
/// </summary>
public sealed class HomeAwayAssignmentTests
{
    [Fact]
    public void ManagerIsAway_SameCombination_AlwaysSameResult()
    {
        var a = HomeAwayAssignment.ManagerIsAway(-1, 42, yearIndex: 2, week: 10);
        var b = HomeAwayAssignment.ManagerIsAway(-1, 42, yearIndex: 2, week: 10);
        Assert.Equal(a, b);
    }

    [Fact]
    public void ManagerIsAway_DifferentCombinations_ProduceBothOutcomes()
    {
        // 「後攻固定」の再発防止＝十分な組み合わせを振れば両方の結果が出ること。
        var results = Enumerable.Range(0, 200)
            .Select(week => HomeAwayAssignment.ManagerIsAway(-1, 100 + week, yearIndex: 1 + week % 3, week))
            .ToList();
        Assert.Contains(true, results);
        Assert.Contains(false, results);
    }

    [Fact]
    public void ManagerIsAway_DifferentOpponentOrWeek_CanFlipTheResult()
    {
        // 同じ相手・同じ年度でも週が違えば別の組み合わせ＝結果が固定されない（対戦ごとに変わり得る）。
        var byWeek = Enumerable.Range(0, 60)
            .Select(week => HomeAwayAssignment.ManagerIsAway(-1, 7, yearIndex: 1, week))
            .Distinct()
            .ToList();
        Assert.Equal(2, byWeek.Count);
    }

    // ===== issue #70: 先攻試合でもサヨナラ被弾（自校の敗戦）が発生し得ること =====

    private static Team ManagerTeam() => StrengthTeamFactory.Create(58, "自校", new Xoshiro256Random(999));
    private static Team OpponentTeam() => StrengthTeamFactory.Create(60, "相手", new Xoshiro256Random(1234));

    /// <summary>PlayerMatchResolver.Resolve と同一の分岐（away/home 引数の入れ替え）を再現する。</summary>
    private static GameResult ResolveLikePlayerMatchResolver(bool managerIsAway, IRandomSource rng)
    {
        var ctx = new GameContext();
        return managerIsAway
            ? GameEngine.Play(ManagerTeam(), OpponentTeam(), ctx, rng.Fork(2))
            : GameEngine.Play(OpponentTeam(), ManagerTeam(), ctx, rng.Fork(2));
    }

    [Fact]
    public void ManagerAway_WalkOffLossForManager_IsRepresentable()
    {
        // 自校=先攻(away) のとき、相手=後攻(home) が最終回以降にサヨナラで勝つ試合が存在すること。
        // ＝「先攻で守り切れず、最後は攻撃側にできない」展開が起こり得る（issue #70 の動機）。
        var root = new Xoshiro256Random(7);
        var found = false;
        for (var i = 0; i < 200 && !found; i++)
        {
            var r = ResolveLikePlayerMatchResolver(managerIsAway: true, root.Fork((ulong)i));
            if (r.InningsPlayed == 9 && r.HomeWon)
            {
                found = true;
                // DecisionOfRecord 側でも自校（away）の敗戦として正しく処理できる（勝ち投手なし＝自校非該当）。
                var (winPid, _) = DecisionOfRecord.Resolve(r, managerIsAway: true);
                Assert.Null(winPid);
                // 登板があれば負け投手の登板順インデックスが決まる（テスト用の合成選手は SourceId を持たないため
                // Resolve の SourceId 解決ではなく DecisionIndex 自体で検証する）。
                if (r.AwayPitching.Count > 0) Assert.NotNull(DecisionOfRecord.DecisionIndex(r.AwayPitching));
            }
        }
        Assert.True(found, "先攻(away)の自校が9回でサヨナラ負けする試合が1つも見つからなかった。");
    }

    [Fact]
    public void ManagerIsAway_Determinism_SameSeed_SameGameResult()
    {
        var a = ResolveLikePlayerMatchResolver(managerIsAway: true, new Xoshiro256Random(2026));
        var b = ResolveLikePlayerMatchResolver(managerIsAway: true, new Xoshiro256Random(2026));
        Assert.Equal(a.AwayRuns, b.AwayRuns);
        Assert.Equal(a.HomeRuns, b.HomeRuns);
        Assert.Equal(a.InningsPlayed, b.InningsPlayed);
    }
}
