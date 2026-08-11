using System.Collections.Generic;
using System.Linq;
using KokoSim.Engine.Core;
using KokoSim.Engine.Nation;
using KokoSim.Engine.Nation.Tournaments;
using Xunit;

namespace KokoSim.Engine.Tests.Nation;

/// <summary>
/// 大会中セーブ（issue #565 F1）の往復。<b>試合と試合の間</b>で進行体の状態を写し取り、組み直した進行体が
/// 「セーブしなかった場合」と1試合も違わずに続きを消化することを固定する（不変条件#2）。
///
/// <para>ここが緑でないと、大会中のセーブは「読めるが別の大会になる」＝いちばん気づきにくい壊れ方をする。
/// 樹形図・戦績・球数台帳まで含めて突き合わせる。</para>
/// </summary>
public sealed class TournamentRunnerSaveTests
{
    private static readonly NationCoefficients Coeff = new();
    private static readonly TournamentSchedule Schedule = new();

    private static School Sch(int id, double strength) => new()
    {
        Id = id, Name = $"校{id}", PrefectureId = 0, Strength = strength,
    };

    /// <summary>自校(id=1)＋others 校。強さをばらけさせてシード順が自明にならないようにする。</summary>
    private static List<School> Field(int others, double managerStrength = 60)
    {
        var list = new List<School> { Sch(1, managerStrength) };
        for (var i = 0; i < others; i++) list.Add(Sch(100 + i, 30 + (i * 7) % 41));
        return list;
    }

    private static TournamentRunner NewRunner(List<School> field, ulong seed = 42)
        => new(field, field[0], Coeff, new Xoshiro256Random(seed), Schedule, "テスト大会");

    private static TournamentRunner Reload(TournamentRunner r, List<School> field)
        => TournamentRunner.FromSaved(r.CaptureState(), field, field[0], Coeff, Schedule);

    /// <summary>大会の「見えている全部」を1本の文字列へ。ここが一致＝プレイヤーから見て同じ大会。</summary>
    private static string Digest(TournamentRunner r)
    {
        var v = r.BuildBracketView();
        var sb = new System.Text.StringBuilder();
        sb.Append(v.Title).Append('|').Append(v.ChampionName ?? "-").Append('|')
          .Append(v.ManagerIsChampion).Append('|').Append(v.ManagerEliminated).Append('\n');
        foreach (var m in v.Matches)
            sb.Append(m.RoundName).Append(' ').Append(m.WinnerId).Append('>').Append(m.LoserId)
              .Append(' ').Append(m.WinnerScore).Append('-').Append(m.LoserScore)
              .Append(m.ManagerInvolved ? " M" : "").Append(m.MercyEnded ? " C" : "").Append('\n');
        foreach (var round in v.Rounds)
            foreach (var c in round.Cards)
                sb.Append('R').Append(c.Round).Append('#').Append(c.SlotIndex).Append(' ')
                  .Append(c.Top.TeamName ?? "-").Append('/').Append(c.Bottom.TeamName ?? "-")
                  .Append(' ').Append(c.Top.Score?.ToString() ?? "-").Append(':')
                  .Append(c.Bottom.Score?.ToString() ?? "-")
                  .Append(c.IsBye ? " bye" : "").Append(c.IsPlayed ? " played" : "").Append('\n');
        return sb.ToString();
    }

    // ── 往復そのもの ──

    // 1試合だけ消化した状態で保存→復元し、そこから最後まで消化した結果が、保存しなかった場合と一致すること。
    // 「読めた」だけでは足りない＝**続きが同じ**でなければ大会中セーブは意味がない。
    [Fact]
    public void SaveAndReload_MidTournament_ContinuesIdentically()
    {
        var field = Field(15);

        var baseline = NewRunner(field);
        baseline.PlayNextPlayerMatch();
        var reloaded = Reload(baseline, field);

        // 保存直後の見た目が一致（樹形図・試合ログ・自校の状況）。
        Assert.Equal(Digest(baseline), Digest(reloaded));
        Assert.Equal(baseline.NextOpponent?.Id, reloaded.NextOpponent?.Id);
        Assert.Equal(baseline.NextMatchDay, reloaded.NextMatchDay);
        Assert.Equal(baseline.RoundName, reloaded.RoundName);

        // 続きを最後まで消化しても一致（＝乱数の消費位置まで揃っている）。
        while (!baseline.Finished) baseline.PlayNextPlayerMatch();
        baseline.DrainBracketResolution();
        while (!reloaded.Finished) reloaded.PlayNextPlayerMatch();
        reloaded.DrainBracketResolution();

        Assert.Equal(Digest(baseline), Digest(reloaded));
    }

    // 各ラウンドの合間で保存→復元しても、そのつど続きが一致すること（1点だけで通ってもたまたまなので全点で見る）。
    [Fact]
    public void SaveAndReload_AtEveryRoundBoundary_ContinuesIdentically()
    {
        var field = Field(15);

        var expected = NewRunner(field);
        while (!expected.Finished) expected.PlayNextPlayerMatch();
        expected.DrainBracketResolution();
        var want = Digest(expected);

        for (var stop = 1; stop <= 4; stop++)
        {
            var r = NewRunner(field);
            for (var i = 0; i < stop && !r.Finished; i++) r.PlayNextPlayerMatch();
            if (r.Finished) break;   // 敗退済み＝この停止点は存在しない

            var back = Reload(r, field);
            while (!back.Finished) back.PlayNextPlayerMatch();
            back.DrainBracketResolution();
            Assert.Equal(want, Digest(back));
        }
    }

    // 自校が敗退して残りブラケットを消化している途中でも往復できること（BracketFinishSim の合間）。
    [Fact]
    public void SaveAndReload_WhileResolvingRemainingBracket_ContinuesIdentically()
    {
        var field = Field(15, managerStrength: 20);   // 弱い自校＝早期敗退

        var expected = NewRunner(field);
        while (!expected.Finished) expected.PlayNextPlayerMatch();
        Assert.True(expected.BracketResolutionPending, "この素材では残りブラケットが残らない＝検証にならない");
        expected.DrainBracketResolution();
        var want = Digest(expected);

        var r = NewRunner(field);
        while (!r.Finished) r.PlayNextPlayerMatch();
        r.StepBracketResolution();               // 1ラウンドだけ消化した中途半端な地点で保存する
        var back = Reload(r, field);
        back.DrainBracketResolution();

        Assert.Equal(want, Digest(back));
        Assert.False(back.PlayerActive);
        Assert.Equal(expected.ChampionName, back.ChampionName);
    }

    // 優勝直後（大会終了済み）の状態も往復できること。
    [Fact]
    public void SaveAndReload_AfterChampionship_KeepsResult()
    {
        var field = Field(7, managerStrength: 99);
        var r = NewRunner(field);
        while (!r.Finished) r.PlayNextPlayerMatch();
        Assert.True(r.IsChampion);

        var back = Reload(r, field);
        Assert.True(back.IsChampion);
        Assert.True(back.Finished);
        Assert.Equal(r.ChampionName, back.ChampionName);
        Assert.Equal(Digest(r), Digest(back));
    }

    // 上の一致テストに歯が有ることの確認（issue #565）。乱数の内部状態を保存せず種から作り直すと
    // 消費位置が合わず、続きが別の大会になる——それをここで**失敗する形**で示しておく。
    // これが「一致」になったら、上の3本は何も検証していない。
    [Fact]
    public void ReloadingWithAFreshRng_DivergesFromTheOriginal()
    {
        var field = Field(15);
        var r = NewRunner(field);
        r.PlayNextPlayerMatch();

        var kept = TournamentRunner.FromSaved(r.CaptureState(), field, field[0], Coeff, Schedule);
        var lost = r.CaptureState();
        lost.RngState = new Xoshiro256Random(42).CaptureState();   // 種から作り直した＝消費位置が頭に戻る
        var fresh = TournamentRunner.FromSaved(lost, field, field[0], Coeff, Schedule);

        while (!kept.Finished) kept.PlayNextPlayerMatch();
        kept.DrainBracketResolution();
        while (!fresh.Finished) fresh.PlayNextPlayerMatch();
        fresh.DrainBracketResolution();

        Assert.NotEqual(Digest(kept), Digest(fresh));
    }

    // ── 保存できない局面 ──

    // 観戦中（Begin して Complete していない）は保存させない＝F2 の対象（issue #565）。
    [Fact]
    public void CaptureState_Throws_WhileLiveMatchIsPending()
    {
        var field = Field(15);
        var runner = new TournamentRunner(field, field[0], Coeff, new Xoshiro256Random(42), Schedule,
            "テスト大会", new StubPlayerResolver());

        Assert.True(runner.CanCaptureState);
        runner.BeginNextPlayerMatch();
        Assert.False(runner.CanCaptureState);
        Assert.Throws<System.InvalidOperationException>(() => runner.CaptureState());
    }

    // ── 復元の入力ミスは黙って通さない ──

    // 出場校に居ない Id が保存されていたら例外（黙って不戦勝や別校にすり替えない）。
    [Fact]
    public void FromSaved_Throws_WhenSchoolIsMissingFromField()
    {
        var field = Field(15);
        var r = NewRunner(field);
        r.PlayNextPlayerMatch();
        var state = r.CaptureState();

        var broken = field.Where(s => s.Id == 1 || s.Id > 103).ToList();   // 何校か抜く
        Assert.Throws<System.ArgumentException>(
            () => TournamentRunner.FromSaved(state, broken, field[0], Coeff, Schedule));
    }

    // 自校の Strength は大会中の実戦成長で動く。シードを組み直す実装だと樹形図がずれるので、
    // **保存された並びを使う**ことを固定する（復元時に別 Strength の自校を渡しても同じ大会になる）。
    [Fact]
    public void FromSaved_KeepsSeeding_EvenIfManagerStrengthChanged()
    {
        var field = Field(15, managerStrength: 60);
        var r = NewRunner(field);
        r.PlayNextPlayerMatch();
        var want = Digest(r);

        var grown = new List<School>(field) { [0] = Sch(1, 95) };   // 実戦成長で強くなった自校
        var back = TournamentRunner.FromSaved(r.CaptureState(), grown, grown[0], Coeff, Schedule);

        Assert.Equal(want, Digest(back));
        Assert.Equal(r.NextOpponent?.Id, back.NextOpponent?.Id);
    }

    /// <summary>ライブ観戦を開始できるだけのダミー resolver（試合の中身は使わない＝Begin できれば足りる）。</summary>
    private sealed class StubPlayerResolver : IPlayerMatchResolver
    {
        public PlayerMatchDetail Resolve(School manager, School opponent, IRandomSource rng,
            bool mercyRuleEnabled, TournamentMatchContext? context = null)
            => throw new System.NotSupportedException();

        public PlayerMatchLive BeginLive(School manager, School opponent, IRandomSource rng,
            bool mercyRuleEnabled, TournamentMatchContext? context = null)
            => new(null!, ManagerIsAway: false);
    }
}
