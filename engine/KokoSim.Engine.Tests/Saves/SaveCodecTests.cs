using KokoSim.Engine.Players;
using KokoSim.Engine.Saves;
using KokoSim.Engine.Season;
using Xunit;

namespace KokoSim.Engine.Tests.Saves;

/// <summary>
/// セーブ DTO の往復と版チェック（issue #507 完了条件）。ひととおり埋めた <see cref="SaveState"/> を
/// 「直列化→復元→再直列化」して1文字も変わらない（＝往復が全単射）ことと、個々の可変値が正しく戻ることを固定する。
/// 版が違う/壊れたセーブは例外で落ちず false で弾くことも確かめる。
/// </summary>
public sealed class SaveCodecTests
{
    [Fact]
    public void Dto_Roundtrip_ReserializesIdentically()
    {
        var state = SaveTestFixtures.BuildRichState();
        var json1 = SaveCodec.Serialize(state);

        Assert.True(SaveCodec.TryDeserialize(json1, out var restored, out var err), err);
        var json2 = SaveCodec.Serialize(restored);

        Assert.Equal(json1, json2);
    }

    [Fact]
    public void Dto_Roundtrip_RestoresMutableValues()
    {
        var state = SaveTestFixtures.BuildRichState();
        Assert.True(SaveCodec.TryDeserialize(SaveCodec.Serialize(state), out var s, out var err), err);

        // 母種シード（2^53超）・暦
        Assert.Equal(0xDEADBEEFCAFEF00DUL, s.MasterSeed);
        Assert.Equal(7, s.Week);
        Assert.Equal(2, s.YearIndex);

        // 自校ロスター（分数の経験値・習得球種・スキル・引退・背番号）
        Assert.Equal(state.Roster.Count, s.Roster.Count);
        Assert.Equal(12.5, s.Roster[0].Exp(AbilityKind.Contact), 6);
        Assert.Equal(3.5, s.Roster[0].AptitudeExp(KokoSim.Engine.Match.Field.FieldPosition.SecondBase), 6);
        Assert.Contains(s.Roster[3].LearnedPitches, lp => lp.Type == PitchType.Slider && lp.SharpnessOffset == 5);
        Assert.True(s.Roster[4].Skills.Has(Skill.Monster));
        Assert.True(s.Roster[4].Skills.IsHidden(Skill.Monster));
        Assert.True(s.Roster[6].IsRetired);
        Assert.Equal(18, s.Roster[7].UniformNumber);
        Assert.Equal(InjurySite.Elbow, s.Roster[1].InjurySite);

        // 監督メタ・施設
        Assert.Equal(137.5, s.Manager.Funds, 6);
        Assert.Equal("テスト監督", s.Manager.Name);
        Assert.Equal(2, s.Facilities.LevelOf("lighting"));
        Assert.Equal(1, s.ManagerLastGrownYear);

        // 成績（private set の集計器＋球種別内訳が復元されている）
        var p1 = s.Stats.Career.Get(1);
        Assert.NotNull(p1);
        Assert.Equal(2, p1!.Batting.Hits);
        Assert.Equal(8, p1.Pitching.StrikeOuts);
        Assert.Equal(3, p1.Pitching.BattingAgainstByPitch[PitchType.Slider].AtBats);
        Assert.Equal(1, s.Stats.Career.Get(2)!.Fielding.Errors);

        // 戦績
        Assert.Equal(2, s.Records.For(100).OfficialWins);
        Assert.Equal(1, s.Records.For(100).SummerAppearances);

        // 全国 materialize 済みロスター・新チーム・チームカラー
        Assert.Equal(state.NationRosters.Count, s.NationRosters.Count);
        Assert.True(s.NewTeamPending);
        Assert.Equal(2, s.NewTeamRetiredNames.Count);
        Assert.NotNull(s.TeamColorOverride);
        Assert.True(s.TeamColorOverride!.Stripe.On);
        Assert.Equal(state.TeamColorOverride!.Shirt, s.TeamColorOverride.Shirt);
    }

    [Fact]
    public void NationRoster_FullSnapshot_Roundtrips()
    {
        // AI選手の能力スナップショット（Player＋PitcherAttributes＋レパートリー）まで往復することを確かめる。
        var state = SaveTestFixtures.BuildRichState();
        Assert.True(SaveCodec.TryDeserialize(SaveCodec.Serialize(state), out var s, out _));

        var a0 = state.NationRosters[0].Players[0];
        var b0 = s.NationRosters[0].Players[0];
        Assert.Equal(a0.Id, b0.Id);
        Assert.Equal(a0.Grade, b0.Grade);
        Assert.Equal(a0.Snapshot.Contact, b0.Snapshot.Contact);
        Assert.Equal(a0.Snapshot.Pitching.MaxVelocityKmh, b0.Snapshot.Pitching.MaxVelocityKmh, 6);
        Assert.Equal(a0.Snapshot.Name, b0.Snapshot.Name);
    }

    // ===== 版チェック（例外で落ちない） =====

    [Fact]
    public void NewerVersion_IsRejected_WithoutThrowing()
    {
        var json = SaveCodec.Serialize(SaveTestFixtures.BuildRichState());
        var future = json.Replace("\"version\":1", "\"version\":999");

        var ok = SaveCodec.TryDeserialize(future, out _, out var err);
        Assert.False(ok);
        Assert.Contains("未対応の版", err);
    }

    [Theory]
    [InlineData("これはJSONではない")]
    [InlineData("{\"foo\":1}")]          // version が無い
    [InlineData("{\"version\":\"x\"}")]  // version が数値でない
    [InlineData("")]                       // 空
    [InlineData("{\"version\":1}")]       // version はあるが中身が欠ける
    public void BrokenOrIncompleteSave_IsRejected_WithoutThrowing(string bad)
    {
        var ok = SaveCodec.TryDeserialize(bad, out var s, out var err);
        Assert.False(ok);
        Assert.NotEqual("", err);
        Assert.NotNull(s);   // 失敗時も空の DTO を返す（null を返さない）
    }

    [Fact]
    public void PeekVersion_ReadsHeader_OrNullWhenUnreadable()
    {
        var json = SaveCodec.Serialize(SaveTestFixtures.BuildRichState());
        Assert.Equal(SaveCodec.CurrentVersion, SaveCodec.PeekVersion(json));
        Assert.Null(SaveCodec.PeekVersion("壊れた"));
    }

    [Fact]
    public void PeekHeader_ReadsWeekAndYear_OrNullWhenUnreadable()
    {
        var json = SaveCodec.Serialize(SaveTestFixtures.BuildRichState());
        var header = SaveCodec.PeekHeader(json);

        Assert.NotNull(header);
        Assert.Equal(SaveCodec.CurrentVersion, header!.Value.Version);
        Assert.Equal(7, header.Value.Week);
        Assert.Equal(2, header.Value.YearIndex);

        Assert.Null(SaveCodec.PeekHeader("壊れた"));
        Assert.Null(SaveCodec.PeekHeader(""));
    }
}
