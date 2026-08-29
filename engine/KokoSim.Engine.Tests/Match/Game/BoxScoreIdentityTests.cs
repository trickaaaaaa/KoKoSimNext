using System.Linq;
using KokoSim.Engine.Match.AtBat;
using KokoSim.Engine.Match.Field;
using KokoSim.Engine.Match.Game;
using KokoSim.Engine.Players;
using Xunit;

namespace KokoSim.Engine.Tests.Match.Game;

/// <summary>
/// ボックススコアの帰属キー（TeamState の帰属キー読み替え表）。
/// <see cref="Player"/> は record ＝既定の等価が <see cref="Player.Position"/> を含むので、守備位置が
/// 試合中に変わる操作（コンバート・投野交代・DH解除）を挟むと、同じ選手の成績が2つの辞書エントリへ
/// 割れて<b>打撃成績に同じ選手が2行出る</b>（原本スタメン行＝交代前の分／末尾に交代後の分）。
/// 通算成績（StatBook）へは行単位で畳み込むので出場試合数の二重計上にもなる。
/// </summary>
public sealed class BoxScoreIdentityTests
{
    private static Player At(FieldPosition pos, string name)
        => new() { Position = pos, Name = name, UniformNumber = (int)pos + 1 };

    private static Player Rp(string name, int number)
        => new() { Position = FieldPosition.Pitcher, Name = name, UniformNumber = number,
                   Pitching = PitcherAttributes.LeagueAverage };

    private static readonly FieldPosition[] Positions =
    {
        FieldPosition.Catcher, FieldPosition.FirstBase, FieldPosition.SecondBase,
        FieldPosition.ThirdBase, FieldPosition.Shortstop, FieldPosition.LeftField,
        FieldPosition.CenterField, FieldPosition.RightField, FieldPosition.Pitcher,
    };

    private const int LeftSlot = 5;      // 左翼手の打順スロット
    private const int CenterSlot = 6;    // 中堅手の打順スロット
    private const int PitcherSlot = 8;

    private static Team MakeTeam()
    {
        var order = Positions.Select(p => p == FieldPosition.Pitcher ? Rp("先発P", 1) : At(p, $"{p}先発")).ToList();
        return new Team { Name = "テスト校", BattingOrder = order, PitcherSlot = PitcherSlot };
    }

    /// <summary>コンバート（守備位置の入替）を挟んで打った選手が1行にまとまり、成績が合算される。</summary>
    [Fact]
    public void コンバートを挟んでも打撃成績は1行にまとまる()
    {
        var state = new TeamState(MakeTeam());
        var lf = state.CurrentLineup[LeftSlot];

        state.RecordBatting(lf, PlateAppearanceResult.Single, rbi: 1);
        Assert.True(state.SwapDefensivePositions(LeftSlot, CenterSlot));
        // コンバート後は同じ人物でも Position が違う別インスタンスになる。
        var afterConvert = state.CurrentLineup[LeftSlot];
        Assert.Equal(lf.Name, afterConvert.Name);
        Assert.NotEqual(lf, afterConvert);
        state.RecordBatting(afterConvert, PlateAppearanceResult.HomeRun, rbi: 2);

        var lines = state.BuildBattingLines();
        Assert.Equal(9, lines.Count);                                   // 途中出場の追記行が出ない
        var line = Assert.Single(lines, l => l.Name == lf.Name);
        Assert.Equal(2, line.PlateAppearances);
        Assert.Equal(2, line.Hits);
        Assert.Equal(1, line.HomeRuns);
        Assert.Equal(3, line.Rbi);
    }

    /// <summary>試合内累積で効くスキル（尻上がり等）の打席数がコンバートでリセットされない。</summary>
    [Fact]
    public void コンバートで試合内の打席累積が0に戻らない()
    {
        var state = new TeamState(MakeTeam());
        var lf = state.CurrentLineup[LeftSlot];
        state.RecordBatting(lf, PlateAppearanceResult.Single, rbi: 0);

        Assert.True(state.SwapDefensivePositions(LeftSlot, CenterSlot));

        Assert.Equal(1, state.PriorPlateAppearances(state.CurrentLineup[LeftSlot]));
    }

    /// <summary>投野交代（野手が登板し、退く投手が空いた守備へ回る）でも打者が2人に増えない。</summary>
    [Fact]
    public void 投野交代でも打撃成績は1行にまとまる()
    {
        var state = new TeamState(MakeTeam());
        var starter = state.CurrentPitcher;
        var lf = state.CurrentLineup[LeftSlot];
        state.RecordBatting(starter, PlateAppearanceResult.Single, rbi: 0);
        state.RecordBatting(lf, PlateAppearanceResult.Walk, rbi: 0);

        // 控えが居ないので在フィールドの野手が登板し、退いた投手はその守備位置へ回る（両者の Position が変わる）。
        Assert.True(state.RelieveWith(lf));
        Assert.Equal(lf.Name, state.CurrentPitcher.Name);

        state.RecordBatting(state.CurrentPitcher, PlateAppearanceResult.Double, rbi: 1);
        state.RecordBatting(state.CurrentLineup[PitcherSlot], PlateAppearanceResult.Strikeout, rbi: 0);

        var lines = state.BuildBattingLines();
        Assert.Equal(9, lines.Count);
        Assert.Equal(9, lines.Select(l => l.Name).Distinct().Count());   // 同じ選手が2行に出ない
        Assert.Equal(2, Assert.Single(lines, l => l.Name == lf.Name).PlateAppearances);
        Assert.Equal(2, Assert.Single(lines, l => l.Name == starter.Name).PlateAppearances);
    }

    /// <summary>
    /// 野手登板（issue #209 Phase C）後に <see cref="TeamState.LivePitcherLine"/> を呼ぶと、
    /// 昇格した投手（守備位置を書き換えた新インスタンス）の成績が積まれている。
    /// <see cref="TeamState.CurrentPitcher"/> は Rekey 後の実体、<see cref="TeamState.RecordPitching"/> は
    /// <c>KeyOf</c> で元の実体へ積むため、<c>LivePitcherLine</c> 側が生の <c>CurrentPitcher</c> で
    /// 素引きすると常に空（球数・奪三振・失点が0のまま）になっていた不具合の回帰テスト。
    /// </summary>
    [Fact]
    public void 野手登板後のLivePitcherLineに球数と奪三振が積まれる()
    {
        var state = new TeamState(MakeTeam());
        var lf = state.CurrentLineup[LeftSlot];

        // 控えが居ないので在フィールドの野手が登板する（RelieveWithがPromoteFielderToMoundへ倒れる）。
        Assert.True(state.RelieveWith(lf));
        Assert.Equal(lf.Name, state.CurrentPitcher.Name);

        state.RecordPitching(state.CurrentPitcher, PlateAppearanceResult.Strikeout, runs: 0, outs: 1, pitches: 5);
        state.RecordPitching(state.CurrentPitcher, PlateAppearanceResult.Single, runs: 1, outs: 0, pitches: 3, unearnedRuns: 0);

        var live = state.LivePitcherLine();
        Assert.Equal(lf.Name, live.Name);
        Assert.Equal(8, live.Pitches);
        Assert.Equal(1, live.Outs);
        Assert.Equal(1, live.StrikeOuts);
        Assert.Equal(1, live.Runs);
        Assert.Equal(1, live.EarnedRuns);
    }

    /// <summary>失策も同一人物へ寄る（守備位置をまたいでも1行・合計は チーム失策と一致）。</summary>
    [Fact]
    public void 守備位置をまたいだ失策は1行にまとまる()
    {
        var state = new TeamState(MakeTeam());
        var lf = state.CurrentLineup[LeftSlot];

        state.RecordFieldingError(lf, FieldPosition.LeftField);
        Assert.True(state.SwapDefensivePositions(LeftSlot, CenterSlot));
        state.RecordFieldingError(state.CurrentLineup[LeftSlot], FieldPosition.CenterField);

        var lines = state.BuildFieldingLines();
        Assert.Equal(2, Assert.Single(lines).Errors);
        Assert.Equal(state.Errors, lines.Sum(l => l.Errors));
    }
}
