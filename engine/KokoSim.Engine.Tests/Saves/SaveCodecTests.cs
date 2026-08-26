using System.Collections.Generic;
using System.Linq;
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

        // 監督メタ・施設・年契約（財布は2つ＝部費と施設基金の両方が往復すること）
        Assert.Equal(137.5, s.Manager.Funds, 6);
        Assert.Equal(8642.5, s.Manager.FacilityFund, 6);
        Assert.Equal("テスト監督", s.Manager.Name);
        Assert.Equal(2, s.Facilities.LevelOf("lighting"));
        Assert.Equal(3, s.Contracts.LevelOf("scouting"));
        Assert.Equal(1, s.Contracts.LevelOf("coach_pitching"));
        Assert.Equal(0, s.Contracts.LevelOf("expedition"));
        Assert.Equal(1, s.ManagerLastGrownYear);
        // 番狂わせ名声デルタの持ち越し（v12）: 試合ごとの値と並びがそのまま往復すること。
        Assert.Equal(new[] { 6.0, -3.5, 0.0 }, s.ManagerPendingUpsetDeltas);

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

        // 監督オファー待ち（v18）: 種別・候補（地区なし/地区ありの両方）が往復すること。
        Assert.True(s.ManagerTransferPending);
        Assert.Equal((int)KokoSim.Engine.Career.TransferEventKind.FreeAgentOffer, s.ManagerTransferKind);
        Assert.Equal(2, s.ManagerTransferCandidates.Count);
        Assert.Equal(1200, s.ManagerTransferCandidates[0].SchoolId);
        Assert.Null(s.ManagerTransferCandidates[0].DistrictId);
        Assert.Equal(2, s.ManagerTransferCandidates[1].DistrictId);
        Assert.Equal(47.25, s.ManagerTransferCandidates[1].Strength, 6);
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
        // 版番号は上がる（issue #565 で 1→2）。リテラルで書くと版を上げた瞬間に置換が空振りして
        // **テストが黙って無効化される**（実測: v2 化で ok=true になり素通りした）ので現行版から作る。
        var future = json.Replace($"\"version\":{SaveCodec.CurrentVersion}", "\"version\":999");
        Assert.Contains("999", future);

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

    // ===== 大会モードの保存分（issue #565 F1） =====

    // v1（大会中は保存できなかった版）のセーブは tournament を持たない。読めて、通常モード扱いになること。
    [Fact]
    public void V1Save_WithoutTournament_StillLoads_AsNormalMode()
    {
        var json = SaveCodec.Serialize(SaveTestFixtures.BuildRichState());
        // v1 のセーブを作る＝版を1へ落とし、v2 で増えた項目を落とす。
        var v1 = json
            .Replace($"\"version\":{SaveCodec.CurrentVersion}", "\"version\":1")
            .Replace(",\"tournament\":null", "");
        Assert.DoesNotContain("\"tournament\"", v1);

        Assert.True(SaveCodec.TryDeserialize(v1, out var s, out var err), err);
        Assert.Null(s.Tournament);
        Assert.Equal(7, s.Week);        // 他の項目は素通りで読めている
    }

    // ===== 財布2つ・年契約の追加分（設計書04 §1.4a） =====

    // v2（財布が1つ・年契約が無かった版）のセーブは facilityFund / contracts を持たない。
    // 読めて、施設基金0・スカウト網Lv1（＝従来のスカウト費と同額・同効果）で始まること。
    [Fact]
    public void V2Save_WithoutFacilityFundAndContracts_StillLoads_WithSafeDefaults()
    {
        var json = SaveCodec.Serialize(SaveTestFixtures.BuildRichState());
        var v2 = json
            .Replace($"\"version\":{SaveCodec.CurrentVersion}", "\"version\":2")
            .Replace(",\"facilityFund\":8642.5", "");
        // contracts ブロックは丸ごと落とす（値は fixture 由来で固定）。
        var start = v2.IndexOf(",\"contracts\":{", System.StringComparison.Ordinal);
        Assert.True(start >= 0, "contracts ブロックが見つからない");
        var end = v2.IndexOf("}}", start, System.StringComparison.Ordinal) + 2;
        v2 = v2.Remove(start, end - start);
        Assert.DoesNotContain("\"facilityFund\"", v2);
        Assert.DoesNotContain("\"contracts\"", v2);

        Assert.True(SaveCodec.TryDeserialize(v2, out var s, out var err), err);
        Assert.Equal(0, s.Manager.FacilityFund, 9);
        Assert.Equal(1, s.Contracts.LevelOf("scouting"));
        Assert.Equal(0, s.Contracts.LevelOf("coach_pitching"));
        Assert.Equal(137.5, s.Manager.Funds, 6);   // 他の項目は素通りで読めている
        Assert.Equal(2, s.Facilities.LevelOf("lighting"));
    }

    // ===== 自校の同定（新設校スタート・issue #585） =====

    // 校名・本拠県・県内地区が往復すること。
    [Fact]
    public void SchoolIdentity_RoundTrips()
    {
        var json = SaveCodec.Serialize(SaveTestFixtures.BuildRichState());
        Assert.True(SaveCodec.TryDeserialize(json, out var s, out var err), err);
        Assert.Equal("北嶺", s.SchoolName);
        Assert.Equal(27, s.PrefectureId);
        Assert.Equal(3, s.DistrictId);
    }

    // 地区を持たない県（DistrictId=null）も往復すること（null が 0 に化けない）。
    [Fact]
    public void SchoolIdentity_NullDistrict_RoundTrips()
    {
        var state = SaveTestFixtures.BuildRichState();
        state.DistrictId = null;
        var json = SaveCodec.Serialize(state);
        Assert.True(SaveCodec.TryDeserialize(json, out var s, out var err), err);
        Assert.Null(s.DistrictId);
    }

    // v3（自校が神奈川の桜丘固定だった版）のセーブは schoolName/prefectureId/districtId を持たない。
    // 読めて、当時の固定値（桜丘・神奈川13・地区なし）で補われること。
    [Fact]
    public void V3Save_WithoutSchoolIdentity_StillLoads_AsSakuragaokaInKanagawa()
    {
        var json = SaveCodec.Serialize(SaveTestFixtures.BuildRichState());

        // 自校の同定「だけ」を切り取る。文字列置換で消すと、同じキー名を持つ入れ子
        // （勧誘の中学生も prefectureId を持つ・issue #588）まで巻き添えにするので、
        // トップレベルの区間（schoolName の手前 〜 incoming の手前）を位置で切る。
        var start = json.IndexOf(",\"schoolName\":", System.StringComparison.Ordinal);
        var end = json.IndexOf(",\"incoming\":", System.StringComparison.Ordinal);
        Assert.True(start >= 0 && end > start, "自校の同定が想定の位置に直列化されていない");
        var v3 = json.Remove(start, end - start)
            .Replace($"\"version\":{SaveCodec.CurrentVersion}", "\"version\":3");
        Assert.DoesNotContain("\"schoolName\"", v3);
        Assert.DoesNotContain("\"districtId\"", v3);

        Assert.True(SaveCodec.TryDeserialize(v3, out var s, out var err), err);
        Assert.Equal("桜丘", s.SchoolName);
        Assert.Equal(13, s.PrefectureId);
        Assert.Null(s.DistrictId);
        Assert.Equal(7, s.Week);        // 他の項目は素通りで読めている
    }

    // ===== 入部予定者（3月確定→4月仮入部・issue #587） =====

    // 確定済み（未合流）の予定者が、選手の実体と見立ての両方で往復すること。
    [Fact]
    public void IncomingClass_RoundTrips()
    {
        var state = SaveTestFixtures.BuildRichState();
        var json = SaveCodec.Serialize(state);
        Assert.True(SaveCodec.TryDeserialize(json, out var s, out var err), err);

        var a = state.Incoming!;
        var b = s.Incoming;
        Assert.NotNull(b);
        Assert.Equal(a.YearIndex, b!.YearIndex);
        Assert.Equal(a.Disclosure, b.Disclosure, 9);
        Assert.False(b.Joined);
        Assert.Equal(a.Players.Count, b.Players.Count);
        Assert.Equal(a.Reports.Count, b.Reports.Count);
        Assert.Equal(a.Players.Select(p => p.Name), b.Players.Select(p => p.Name));
        Assert.Equal(a.Players.Select(p => p.Level(AbilityKind.Contact)), b.Players.Select(p => p.Level(AbilityKind.Contact)));
        for (var i = 0; i < a.Reports.Count; i++)
        {
            Assert.Equal(a.Reports[i].Name, b.Reports[i].Name);
            Assert.Equal(a.Reports[i].Batting, b.Reports[i].Batting, 9);
            Assert.Equal(a.Reports[i].Pitching, b.Reports[i].Pitching, 9);
            Assert.Equal(a.Reports[i].UnknownCount, b.Reports[i].UnknownCount);
            Assert.Equal(a.Reports[i].Recruited, b.Reports[i].Recruited);
        }
    }

    // 能動勧誘で獲った子は印が付いて往復すること（受動枠と混ざっても区別が残る・issue #588）。
    [Fact]
    public void IncomingClass_RecruitedFlag_RoundTrips()
    {
        var state = SaveTestFixtures.BuildRichState();
        state.Incoming!.Reports[0].Recruited = true;

        Assert.True(SaveCodec.TryDeserialize(SaveCodec.Serialize(state), out var s, out var err), err);

        Assert.True(s.Incoming!.Reports[0].Recruited);
        Assert.False(s.Incoming.Reports[1].Recruited);
    }

    // 合流済みの学年は選手の実体を持たず（ロスター側にしか居ない）、見立てだけが残ること。
    [Fact]
    public void IncomingClass_AfterJoin_KeepsOnlyReports()
    {
        var state = SaveTestFixtures.BuildRichState();
        var incoming = state.Incoming!;
        var joined = incoming.Players.ToList();
        for (var i = 0; i < joined.Count; i++) joined[i].Id = 900 + i;
        incoming.MarkJoined(joined);

        Assert.True(SaveCodec.TryDeserialize(SaveCodec.Serialize(state), out var s, out var err), err);

        Assert.NotNull(s.Incoming);
        Assert.True(s.Incoming!.Joined);
        Assert.Empty(s.Incoming.Players);
        Assert.Equal(joined.Count, s.Incoming.Reports.Count);
        Assert.Equal(900, s.Incoming.Reports[0].PlayerId);
    }

    // 予定者を持たない進行（3月をまだ跨いでいない）も往復すること。
    [Fact]
    public void IncomingClass_Null_RoundTrips()
    {
        var state = SaveTestFixtures.BuildRichState();
        state.Incoming = null;
        Assert.True(SaveCodec.TryDeserialize(SaveCodec.Serialize(state), out var s, out var err), err);
        Assert.Null(s.Incoming);
    }

    // v4（入部予定者が無かった版）のセーブは incoming を持たない。読めて null で補われること
    // ＝4月に進めると RosterLifecycle.AdvanceYear の従来経路（その場生成）へ落ちる。
    [Fact]
    public void V4Save_WithoutIncoming_StillLoads()
    {
        var json = SaveCodec.Serialize(SaveTestFixtures.BuildRichState());
        var start = json.IndexOf(",\"incoming\":", System.StringComparison.Ordinal);
        Assert.True(start >= 0, "incoming が直列化されていない");
        var end = json.IndexOf(",\"tournament\":", System.StringComparison.Ordinal);
        Assert.True(end > start, "tournament が incoming の後に居ない");
        var v4 = json.Remove(start, end - start)
            .Replace($"\"version\":{SaveCodec.CurrentVersion}", "\"version\":4");
        Assert.DoesNotContain("\"incoming\"", v4);

        Assert.True(SaveCodec.TryDeserialize(v4, out var s, out var err), err);
        Assert.Null(s.Incoming);
        Assert.Equal("北嶺", s.SchoolName);   // 他の項目は素通りで読めている
    }

    // ===== 能動勧誘（issue #588） =====

    // 中学生リスト・訪問回数・競合校の格・勧誘対象が往復すること。
    [Fact]
    public void RecruitingState_RoundTrips()
    {
        var state = SaveTestFixtures.BuildRichState();
        Assert.True(SaveCodec.TryDeserialize(SaveCodec.Serialize(state), out var s, out var err), err);

        var a = state.Recruiting!;
        var b = s.Recruiting;
        Assert.NotNull(b);
        Assert.Equal(a.YearIndex, b!.YearIndex);
        Assert.Equal(a.Slots, b.Slots);
        Assert.Equal(a.Resolved, b.Resolved);
        Assert.Equal(a.Prospects.Count, b.Prospects.Count);
        Assert.Equal(a.TargetedCount, b.TargetedCount);
        for (var i = 0; i < a.Prospects.Count; i++)
        {
            Assert.Equal(a.Prospects[i].Player.Name, b.Prospects[i].Player.Name);
            Assert.Equal(a.Prospects[i].PrefectureId, b.Prospects[i].PrefectureId);
            Assert.Equal(a.Prospects[i].Distance, b.Prospects[i].Distance);
            Assert.Equal(a.Prospects[i].RivalTier, b.Prospects[i].RivalTier);
            Assert.Equal(a.Prospects[i].Visits, b.Prospects[i].Visits);
            Assert.Equal(a.Prospects[i].Targeted, b.Prospects[i].Targeted);
            Assert.Equal(a.Prospects[i].Report.Batting, b.Prospects[i].Report.Batting, 9);
        }
    }

    // 勧誘を持たない進行（寮が無い／秋を跨いでいない）も往復すること。
    [Fact]
    public void RecruitingState_Null_RoundTrips()
    {
        var state = SaveTestFixtures.BuildRichState();
        state.Recruiting = null;
        Assert.True(SaveCodec.TryDeserialize(SaveCodec.Serialize(state), out var s, out var err), err);
        Assert.Null(s.Recruiting);
    }

    // v5（勧誘が無かった版）のセーブは recruiting を持たない。読めて null で補われること。
    [Fact]
    public void V5Save_WithoutRecruiting_StillLoads()
    {
        var json = SaveCodec.Serialize(SaveTestFixtures.BuildRichState());
        var start = json.IndexOf(",\"recruiting\":", System.StringComparison.Ordinal);
        Assert.True(start >= 0, "recruiting が直列化されていない");
        var end = json.IndexOf(",\"tournament\":", System.StringComparison.Ordinal);
        Assert.True(end > start, "tournament が recruiting の後に居ない");
        var v5 = json.Remove(start, end - start)
            .Replace($"\"version\":{SaveCodec.CurrentVersion}", "\"version\":5");
        Assert.DoesNotContain("\"recruiting\"", v5);

        Assert.True(SaveCodec.TryDeserialize(v5, out var s, out var err), err);
        Assert.Null(s.Recruiting);
        Assert.NotNull(s.Incoming);   // 他の項目は素通りで読めている
    }

    // v11（番狂わせデルタの持ち越しが無かった版）のセーブは managerPendingUpsetDeltas を持たない。
    // 読めて空で補われること（今年度ぶんは順当扱い＝名声は勝数と甲子園だけで動く）。
    [Fact]
    public void V11Save_WithoutPendingUpsetDeltas_StillLoads_AsEmpty()
    {
        var json = SaveCodec.Serialize(SaveTestFixtures.BuildRichState());
        var start = json.IndexOf(",\"managerPendingUpsetDeltas\":", System.StringComparison.Ordinal);
        Assert.True(start >= 0, "managerPendingUpsetDeltas が直列化されていない");
        var end = json.IndexOf(",\"nationRosters\":", System.StringComparison.Ordinal);
        Assert.True(end > start, "nationRosters が managerPendingUpsetDeltas の後に居ない");
        var v11 = json.Remove(start, end - start)
            .Replace($"\"version\":{SaveCodec.CurrentVersion}", "\"version\":11");
        Assert.DoesNotContain("\"managerPendingUpsetDeltas\"", v11);

        Assert.True(SaveCodec.TryDeserialize(v11, out var s, out var err), err);
        Assert.Empty(s.ManagerPendingUpsetDeltas);
        Assert.Equal(1, s.ManagerLastGrownYear);   // 他の項目は素通りで読めている
    }

    // ===== 前回スタメンの記憶（issue lineup-carry-over-previous-game・v14） =====

    [Fact]
    public void LastLineup_RoundTrips()
    {
        var state = SaveTestFixtures.BuildRichState();
        Assert.True(SaveCodec.TryDeserialize(SaveCodec.Serialize(state), out var s, out var err), err);

        Assert.NotNull(s.LastLineup);
        Assert.Equal(state.LastLineup!.PlayerIds, s.LastLineup!.PlayerIds);
        Assert.Equal(state.LastLineup.Positions, s.LastLineup.Positions);
        Assert.Equal(1, s.LastLineup.StartingPitcherId);
        Assert.True(s.LastLineup.UsesDh);
        Assert.Equal(4, s.LastLineup.DhSlot);
    }

    [Fact]
    public void LastLineup_Null_RoundTripsAsNull()
    {
        var state = SaveTestFixtures.BuildRichState();
        state.LastLineup = null;
        Assert.True(SaveCodec.TryDeserialize(SaveCodec.Serialize(state), out var s, out var err), err);
        Assert.Null(s.LastLineup);
    }

    [Fact]
    public void V13Save_WithoutLastLineup_StillLoads_AsNull()
    {
        var json = SaveCodec.Serialize(SaveTestFixtures.BuildRichState());
        var start = json.IndexOf(",\"lastLineup\":", System.StringComparison.Ordinal);
        Assert.True(start >= 0, "lastLineup が直列化されていない");
        var end = json.LastIndexOf('}');
        var v13 = json.Remove(start, end - start)
            .Replace($"\"version\":{SaveCodec.CurrentVersion}", "\"version\":13");
        Assert.DoesNotContain("\"lastLineup\"", v13);

        Assert.True(SaveCodec.TryDeserialize(v13, out var s, out var err), err);
        Assert.Null(s.LastLineup);
        Assert.Equal(1, s.ManagerLastGrownYear);   // 他の項目は素通りで読めている
    }

    // ===== センバツ選考発表待ち（設計書05 §1.5b・v16） =====

    [Fact]
    public void PendingSenbatsuField_RoundTrips()
    {
        var state = SaveTestFixtures.BuildRichState();
        state.PendingSenbatsuFieldIds = new List<int> { -1, 100, 200, 300 };
        state.PendingSenbatsuJinguRegion = "kanto";
        Assert.True(SaveCodec.TryDeserialize(SaveCodec.Serialize(state), out var s, out var err), err);

        Assert.Equal(new List<int> { -1, 100, 200, 300 }, s.PendingSenbatsuFieldIds);
        Assert.Equal("kanto", s.PendingSenbatsuJinguRegion);
    }

    [Fact]
    public void V15Save_WithoutSenbatsuFields_StillLoads_AsEmpty()
    {
        var json = SaveCodec.Serialize(SaveTestFixtures.BuildRichState());
        var start = json.IndexOf(",\"senbatsu\":", System.StringComparison.Ordinal);
        Assert.True(start >= 0, "senbatsu が直列化されていない");
        var end = json.IndexOf(",\"lastLineup\":", System.StringComparison.Ordinal);
        Assert.True(end > start, "lastLineup が senbatsu 系の後に居ない");
        var v15 = json.Remove(start, end - start)
            .Replace($"\"version\":{SaveCodec.CurrentVersion}", "\"version\":15");
        Assert.DoesNotContain("\"senbatsu\"", v15);
        Assert.DoesNotContain("\"pendingSenbatsuFieldIds\"", v15);

        Assert.True(SaveCodec.TryDeserialize(v15, out var s, out var err), err);
        Assert.Null(s.Senbatsu);
        Assert.Empty(s.PendingSenbatsuFieldIds);
        Assert.Equal("", s.PendingSenbatsuJinguRegion);
        Assert.Equal(1, s.ManagerLastGrownYear);   // 他の項目は素通りで読めている
    }

    // ===== 大会連鎖の中継値（設計書05 §1.1a/§1.5b・v19） =====

    [Fact]
    public void TournamentChainRelayValues_RoundTrip()
    {
        var state = SaveTestFixtures.BuildRichState();
        state.LastPrefChampionId = -1;
        state.LastAutumnPlacementIds = new List<int> { -1, 3848, 3867, 3851 };
        state.LastRegionalChampionId = 3848;
        state.LastRegionalPlacementIds = new List<int> { 3848, -1 };
        state.LastJinguChampionId = 4200;
        Assert.True(SaveCodec.TryDeserialize(SaveCodec.Serialize(state), out var s, out var err), err);

        Assert.Equal(-1, s.LastPrefChampionId);
        Assert.Equal(new List<int> { -1, 3848, 3867, 3851 }, s.LastAutumnPlacementIds);
        Assert.Equal(3848, s.LastRegionalChampionId);
        Assert.Equal(new List<int> { 3848, -1 }, s.LastRegionalPlacementIds);
        Assert.Equal(4200, s.LastJinguChampionId);
    }

    /// <summary>未確定（null）と「0件の順位」を取り違えない＝<c>MyRegionPlacementIds</c> の
    /// フォールバック（地区の順位が無ければ県の順位を使う）は null 判定で書かれている。</summary>
    [Fact]
    public void TournamentChainRelayValues_Unset_RoundTripsAsNullAndEmpty()
    {
        var state = SaveTestFixtures.BuildRichState();
        Assert.True(SaveCodec.TryDeserialize(SaveCodec.Serialize(state), out var s, out var err), err);

        Assert.Null(s.LastPrefChampionId);
        Assert.Empty(s.LastAutumnPlacementIds);
        Assert.Null(s.LastRegionalChampionId);
        Assert.Empty(s.LastRegionalPlacementIds);
        Assert.Null(s.LastJinguChampionId);
    }

    [Fact]
    public void V18Save_WithoutChainRelayValues_StillLoads_AsUnset()
    {
        var state = SaveTestFixtures.BuildRichState();
        state.LastAutumnPlacementIds = new List<int> { -1, 3848 };
        var json = SaveCodec.Serialize(state);
        var start = json.IndexOf(",\"lastPrefChampionId\":", System.StringComparison.Ordinal);
        Assert.True(start >= 0, "lastPrefChampionId が直列化されていない");
        var end = json.IndexOf(",\"lastLineup\":", System.StringComparison.Ordinal);
        Assert.True(end > start, "lastLineup が連鎖中継値の後に居ない");
        var v18 = json.Remove(start, end - start)
            .Replace($"\"version\":{SaveCodec.CurrentVersion}", "\"version\":18");
        Assert.DoesNotContain("\"lastAutumnPlacementIds\"", v18);

        Assert.True(SaveCodec.TryDeserialize(v18, out var s, out var err), err);
        Assert.Null(s.LastPrefChampionId);
        Assert.Empty(s.LastAutumnPlacementIds);
        Assert.Null(s.LastRegionalChampionId);
        Assert.Empty(s.LastRegionalPlacementIds);
        Assert.Null(s.LastJinguChampionId);
        Assert.Equal(1, s.ManagerLastGrownYear);   // 他の項目は素通りで読めている
    }

    // ===== センバツの地区大会成績（設計書05 §1.5b・v17） =====

    [Fact]
    public void RegionalQualifierStats_RoundTrip()
    {
        var state = SaveTestFixtures.BuildRichState();
        // 自校ぶん（PlayerStatStore）と他校ぶん（NationTournamentStats）の両方に実測を入れる
        // （SchoolStatsLookup が校IDで振り分ける対の構造＝PrefQualifier と同じ形）。
        // 自校は「今大会成績を地区大会スコープへ焼き取る」経路で積む（BuildRichState が既に
        // CurrentTournament へ1試合分積んでいる＝SnapshotPrefQualifier と対の作法）。
        state.Stats.SnapshotRegionalQualifier();
        // SyntheticGame() は away 側だけ埋める合成ボックススコア＝schoolIsAway: true で畳む
        // （BuildRichState 内の他の畳み込みと同じ作法。BuildManagerRoster の管理ロスターとは無関係の
        // 別校=400として積む）。
        state.TournamentStats.FoldRegionalQualifier(400, SaveTestFixtures.SyntheticGame(), schoolIsAway: true);

        Assert.True(SaveCodec.TryDeserialize(SaveCodec.Serialize(state), out var s, out var err), err);

        Assert.True(s.Stats.RegionalQualifier.Players.Count > 0);
        var restoredBook = s.TournamentStats.RegionalQualifierForSchool(400);
        Assert.NotNull(restoredBook);
        Assert.True(restoredBook!.Players.Count > 0);
    }

    [Fact]
    public void V16Save_WithoutRegionalQualifierStats_StillLoads_AsEmpty()
    {
        var json = SaveCodec.Serialize(SaveTestFixtures.BuildRichState());
        var start = json.IndexOf(",\"nationRegionalQualifierStats\":", System.StringComparison.Ordinal);
        Assert.True(start >= 0, "nationRegionalQualifierStats が直列化されていない");
        var end = json.IndexOf(",\"nationSchools\":", System.StringComparison.Ordinal);
        Assert.True(end > start, "nationSchools が nationRegionalQualifierStats の後に居ない");
        var v16 = json.Remove(start, end - start)
            .Replace($"\"version\":{SaveCodec.CurrentVersion}", "\"version\":16");
        Assert.DoesNotContain("\"nationRegionalQualifierStats\"", v16);

        Assert.True(SaveCodec.TryDeserialize(v16, out var s, out var err), err);
        Assert.Empty(s.TournamentStats.RegionalQualifierSchools);
        Assert.Empty(s.Stats.RegionalQualifier.Players);
        Assert.Equal(1, s.ManagerLastGrownYear);   // 他の項目は素通りで読めている
    }

    // ===== 教え子名鑑（issue #586） =====

    // 過年度の指名確定（年・当時の背番号・順位・バンド・注目度）が往復すること。
    [Fact]
    public void DraftHistory_RoundTrips()
    {
        var state = SaveTestFixtures.BuildRichState();
        Assert.True(SaveCodec.TryDeserialize(SaveCodec.Serialize(state), out var s, out var err), err);

        var a = state.Draft.Records;
        var b = s.Draft.Records;
        Assert.Equal(a.Count, b.Count);
        Assert.NotEmpty(b);
        for (var i = 0; i < a.Count; i++)
        {
            Assert.Equal(a[i].Year, b[i].Year);
            Assert.Equal(a[i].YearIndex, b[i].YearIndex);
            Assert.Equal(a[i].PlayerId, b[i].PlayerId);
            Assert.Equal(a[i].Name, b[i].Name);
            Assert.Equal(a[i].UniformNumber, b[i].UniformNumber);
            Assert.Equal(a[i].Round, b[i].Round);
            Assert.Equal(a[i].Band, b[i].Band);
            Assert.Equal(a[i].Notability, b[i].Notability, 9);
        }
    }

    // 指名がまだ一度も出ていない進行（新規ゲーム）も往復すること。
    [Fact]
    public void DraftHistory_Empty_RoundTrips()
    {
        var state = SaveTestFixtures.BuildRichState();
        state.Draft = new KokoSim.Engine.Career.Draft.DraftHistory();
        Assert.True(SaveCodec.TryDeserialize(SaveCodec.Serialize(state), out var s, out var err), err);
        Assert.Empty(s.Draft.Records);
    }

    // v6（教え子名鑑が無かった版）のセーブは draft を持たない。読めて空で補われること。
    [Fact]
    public void V6Save_WithoutDraft_StillLoads()
    {
        var json = SaveCodec.Serialize(SaveTestFixtures.BuildRichState());
        var start = json.IndexOf(",\"draft\":", System.StringComparison.Ordinal);
        Assert.True(start >= 0, "draft が直列化されていない");
        var end = json.IndexOf(",\"tournament\":", System.StringComparison.Ordinal);
        Assert.True(end > start, "tournament が draft の後に居ない");
        var v6 = json.Remove(start, end - start)
            .Replace($"\"version\":{SaveCodec.CurrentVersion}", "\"version\":6");
        Assert.DoesNotContain("\"draft\"", v6);

        Assert.True(SaveCodec.TryDeserialize(v6, out var s, out var err), err);
        Assert.Empty(s.Draft.Records);
        Assert.NotNull(s.Recruiting);   // 他の項目は素通りで読めている
    }

    // 歴代大会結果（issue #600・v8）が往復すること。年度別の情報は累計へ畳むと復元できない＝焼く必要がある。
    [Fact]
    public void TournamentHistory_RoundTrips()
    {
        var state = SaveTestFixtures.BuildRichState();
        Assert.True(SaveCodec.TryDeserialize(SaveCodec.Serialize(state), out var s, out var err), err);

        var a = state.TournamentHistory.Entries;
        var b = s.TournamentHistory.Entries;
        Assert.Equal(a.Count, b.Count);
        Assert.NotEmpty(b);
        for (var i = 0; i < a.Count; i++)
        {
            Assert.Equal(a[i].Year, b[i].Year);
            Assert.Equal(a[i].Kind, b[i].Kind);
            Assert.Equal(a[i].ReachedRoundName, b[i].ReachedRoundName);
            Assert.Equal(a[i].ManagerIsChampion, b[i].ManagerIsChampion);
            Assert.Equal(a[i].ChampionId, b[i].ChampionId);
            Assert.Equal(a[i].ChampionName, b[i].ChampionName);
        }
    }

    // 全国の学校別成績（issue #634・v9）が2スコープとも往復すること。通算は大会をまたいで積むので、
    // ここが落ちると相手校のHUDに出す「通算」がロードのたびに消える。
    [Fact]
    public void NationStats_BothScopes_RoundTrip()
    {
        var state = SaveTestFixtures.BuildRichState();
        Assert.True(SaveCodec.TryDeserialize(SaveCodec.Serialize(state), out var s, out var err), err);

        // 素材は先攻(200)側だけ打撃行を持つ合成試合を2試合ぶん（間に StartTournament）。
        // ＝今大会は1試合ぶん・通算は2試合ぶんで、スコープが混ざっていれば必ず食い違う。
        const int awayId = 200;
        Assert.Equal(3, Hits(state.TournamentStats.ForSchool(awayId)));
        Assert.Equal(6, Hits(state.TournamentStats.CareerForSchool(awayId)));

        Assert.Equal(3, Hits(s.TournamentStats.ForSchool(awayId)));
        Assert.Equal(6, Hits(s.TournamentStats.CareerForSchool(awayId)));
        Assert.Equal(
            new List<int>(state.TournamentStats.CareerSchools).Count,
            new List<int>(s.TournamentStats.CareerSchools).Count);
    }

    // v8 のセーブ（通算スコープが無い）が読めること。今大会ぶんは無傷で残り、通算は空から積み直す。
    [Fact]
    public void V8Save_WithoutNationCareerStats_StillLoads()
    {
        var json = SaveCodec.Serialize(SaveTestFixtures.BuildRichState());
        var start = json.IndexOf(",\"nationCareerStats\":", System.StringComparison.Ordinal);
        Assert.True(start >= 0, "nationCareerStats が直列化されていない");
        var end = json.IndexOf(",\"sessionYear\":", System.StringComparison.Ordinal);
        Assert.True(end > start, "sessionYear が nationCareerStats の後に居ない");
        var v8 = json.Remove(start, end - start)
            .Replace($"\"version\":{SaveCodec.CurrentVersion}", "\"version\":8");
        Assert.DoesNotContain("\"nationCareerStats\"", v8);

        Assert.True(SaveCodec.TryDeserialize(v8, out var s, out var err), err);
        Assert.Empty(s.TournamentStats.CareerSchools);
        Assert.NotEmpty(s.TournamentStats.Schools);   // 今大会ぶんは素通りで読めている
    }

    private static int Hits(KokoSim.Engine.Stats.StatBook? book)
    {
        var hits = 0;
        if (book != null)
            foreach (var kv in book.Players) hits += kv.Value.Batting.Hits;
        return hits;
    }

    // まだ1つも大会を終えていない進行（新規ゲーム）も往復すること。
    [Fact]
    public void TournamentHistory_Empty_RoundTrips()
    {
        var state = SaveTestFixtures.BuildRichState();
        state.TournamentHistory = new KokoSim.Engine.Nation.TournamentHistory();
        Assert.True(SaveCodec.TryDeserialize(SaveCodec.Serialize(state), out var s, out var err), err);
        Assert.Empty(s.TournamentHistory.Entries);
    }

    // 大会別アーカイブの年度（issue #600・v8）が往復すること。学年キーは年を持たないので、
    // ここが落ちるとシーズン記録の「（2027）」が出せなくなる。
    [Fact]
    public void ArchivedYear_RoundTrips()
    {
        var state = SaveTestFixtures.BuildRichState();
        Assert.True(SaveCodec.TryDeserialize(SaveCodec.Serialize(state), out var s, out var err), err);

        var key = new KokoSim.Engine.Stats.TournamentArchiveKey(3, KokoSim.Engine.Stats.TournamentSlot.SummerPref);
        var before = state.Stats.Archive.Slot(key);
        var after = s.Stats.Archive.Slot(key);
        Assert.NotNull(before);
        Assert.NotNull(after);
        foreach (var kv in before!)
        {
            var back = after![kv.Key];
            Assert.Equal(kv.Value.Year, back.Year);
            Assert.Equal(kv.Value.UniformNumber, back.UniformNumber);
        }
        Assert.Contains(before, kv => kv.Value.Year == 2);   // 年が焼かれていること自体も測る
    }

    // v7（歴代大会結果が無かった版）のセーブは tournamentHistory を持たない。読めて空で補われること。
    [Fact]
    public void V7Save_WithoutTournamentHistory_StillLoads()
    {
        var json = SaveCodec.Serialize(SaveTestFixtures.BuildRichState());
        var start = json.IndexOf(",\"tournamentHistory\":", System.StringComparison.Ordinal);
        Assert.True(start >= 0, "tournamentHistory が直列化されていない");
        var end = json.IndexOf(",\"tournament\":", System.StringComparison.Ordinal);
        Assert.True(end > start, "tournament が tournamentHistory の後に居ない");
        var v7 = json.Remove(start, end - start)
            .Replace($"\"version\":{SaveCodec.CurrentVersion}", "\"version\":7");
        Assert.DoesNotContain("\"tournamentHistory\"", v7);

        Assert.True(SaveCodec.TryDeserialize(v7, out var s, out var err), err);
        Assert.Empty(s.TournamentHistory.Entries);
        Assert.NotEmpty(s.Draft.Records);   // 他の項目は素通りで読めている
    }

    // 週次イベントの発火状態（issue #609・v9）が往復すること。
    [Fact]
    public void EventSchedulerState_RoundTrips()
    {
        var state = SaveTestFixtures.BuildRichState();
        Assert.True(SaveCodec.TryDeserialize(SaveCodec.Serialize(state), out var s, out var err), err);

        Assert.NotNull(s.Events);
        Assert.Equal(state.Events!.AbsoluteWeek, s.Events!.AbsoluteWeek);
        Assert.Equal(state.Events.LastFiredWeeks.Count, s.Events.LastFiredWeeks.Count);
        foreach (var kv in state.Events.LastFiredWeeks) Assert.Equal(kv.Value, s.Events.LastFiredWeeks[kv.Key]);
        Assert.Equal(state.Events.AnnualFlags.Count, s.Events.AnnualFlags.Count);
        foreach (var f in state.Events.AnnualFlags) Assert.Contains(f, s.Events.AnnualFlags);
    }

    /// <summary>
    /// クールダウンがロードで明けないこと（issue #609 の本題）。状態を載せ忘れると、
    /// 直前に見たイベントが開き直した直後にもう一度出る＝往復の値比較だけでは気づけないので、
    /// <b>復元したスケジューラが実際に発火を止める</b>ところまで測る。
    /// </summary>
    [Fact]
    public void EventSchedulerState_KeepsCooldownsAcrossLoad()
    {
        var table = new[]
        {
            new KokoSim.Engine.Season.Events.GameEvent
            {
                Id = "injury", Kind = KokoSim.Engine.Season.Events.EventKind.Random, Weight = 1.0,
                CooldownWeeks = 12, Text = "故障した。",
            },
        };

        var state = SaveTestFixtures.BuildRichState();
        Assert.True(SaveCodec.TryDeserialize(SaveCodec.Serialize(state), out var s, out var err), err);

        var scheduler = new KokoSim.Engine.Season.Events.EventScheduler(table);
        s.Events!.RestoreInto(scheduler);

        // 通算37週で最終発火31週・クールダウン12週＝まだ明けていない。
        var fired = scheduler.Week(week: 20, rng: new KokoSim.Engine.Core.Xoshiro256Random(1));
        Assert.Empty(fired);
    }

    // v8（イベント状態が無かった版）のセーブは events を持たない。読めて null で補われること。
    [Fact]
    public void V8Save_WithoutEventState_StillLoads()
    {
        var json = SaveCodec.Serialize(SaveTestFixtures.BuildRichState());
        var start = json.IndexOf(",\"events\":", System.StringComparison.Ordinal);
        Assert.True(start >= 0, "events が直列化されていない");
        var end = json.IndexOf(",\"tournament\":", System.StringComparison.Ordinal);
        Assert.True(end > start, "tournament が events の後に居ない");
        var v8 = json.Remove(start, end - start)
            .Replace($"\"version\":{SaveCodec.CurrentVersion}", "\"version\":8");
        Assert.DoesNotContain("\"events\"", v8);

        Assert.True(SaveCodec.TryDeserialize(v8, out var s, out var err), err);
        Assert.Null(s.Events);              // 一度も発火していない＝まっさらで正しい
        Assert.NotEmpty(s.TournamentHistory.Entries);   // 他の項目は素通りで読めている
    }

    // 練習効率の補正（issue #615・v10）が往復すること。
    [Fact]
    public void TrainingBoosts_RoundTrip()
    {
        var state = SaveTestFixtures.BuildRichState();
        Assert.True(SaveCodec.TryDeserialize(SaveCodec.Serialize(state), out var s, out var err), err);

        Assert.Equal(state.TrainingBoosts.Count, s.TrainingBoosts.Count);
        for (var i = 0; i < state.TrainingBoosts.Count; i++)
        {
            Assert.Equal(state.TrainingBoosts[i].Source, s.TrainingBoosts[i].Source);
            Assert.Equal(state.TrainingBoosts[i].Mult, s.TrainingBoosts[i].Mult, 6);
            Assert.Equal(state.TrainingBoosts[i].WeeksLeft, s.TrainingBoosts[i].WeeksLeft);
            Assert.Equal(state.TrainingBoosts[i].PlayerId, s.TrainingBoosts[i].PlayerId);
        }
    }

    // v9（補正が無かった版）のセーブは trainingBoosts を持たない。読めて空で補われること。
    [Fact]
    public void V9Save_WithoutTrainingBoosts_StillLoads()
    {
        var json = SaveCodec.Serialize(SaveTestFixtures.BuildRichState());
        var start = json.IndexOf(",\"trainingBoosts\":", System.StringComparison.Ordinal);
        Assert.True(start >= 0, "trainingBoosts が直列化されていない");
        var end = json.IndexOf(",\"tournament\":", System.StringComparison.Ordinal);
        Assert.True(end > start, "tournament が trainingBoosts の後に居ない");
        var v9 = json.Remove(start, end - start)
            .Replace($"\"version\":{SaveCodec.CurrentVersion}", "\"version\":9");

        Assert.True(SaveCodec.TryDeserialize(v9, out var s, out var err), err);
        Assert.Empty(s.TrainingBoosts);
        Assert.NotNull(s.Events);   // 他の項目は素通りで読めている
    }

    // 大会中のセーブが往復すること（ブラケット・乱数状態・球数台帳・出場校）。
    [Fact]
    public void TournamentState_RoundTrips()
    {
        var s = SaveTestFixtures.BuildRichState();
        s.Tournament = SaveTestFixtures.BuildTournamentState();

        Assert.True(SaveCodec.TryDeserialize(SaveCodec.Serialize(s), out var back, out var err), err);

        var a = s.Tournament!;
        var b = back.Tournament;
        Assert.NotNull(b);
        Assert.Equal(a.Kind, b!.Kind);
        Assert.Equal(a.Title, b.Title);
        Assert.Equal(a.TournamentDay, b.TournamentDay);
        Assert.Equal(a.BannerPending, b.BannerPending);
        Assert.Equal(a.FieldSchoolIds, b.FieldSchoolIds);

        var ra = a.Runner;
        var rb = b.Runner;
        Assert.Equal(ra.Title, rb.Title);
        Assert.Equal(ra.PlayerActive, rb.PlayerActive);
        Assert.Equal(ra.IsChampion, rb.IsChampion);
        Assert.Equal(ra.ChampionName, rb.ChampionName);
        Assert.Equal(ra.RoundIndex, rb.RoundIndex);
        Assert.Equal(ra.RngState, rb.RngState);
        Assert.Equal(ra.Current, rb.Current);
        Assert.Equal(ra.Cards, rb.Cards);
        Assert.Equal(ra.Matches, rb.Matches);
        Assert.Equal(ra.PitchLedger, rb.PitchLedger);
        Assert.Equal(ra.RoundEntrants.Count, rb.RoundEntrants.Count);
        for (var i = 0; i < ra.RoundEntrants.Count; i++)
            Assert.Equal(ra.RoundEntrants[i], rb.RoundEntrants[i]);
    }

    // 不戦勝の空き枠は null で往復すること（0 や -1 の番兵に潰れない＝自校 Id=-1 と衝突しない）。
    [Fact]
    public void TournamentState_KeepsByeSlotsAsNull()
    {
        var s = SaveTestFixtures.BuildRichState();
        s.Tournament = SaveTestFixtures.BuildTournamentState();
        s.Tournament.Runner.Current = new List<int?> { -1, null, 100, null };

        Assert.True(SaveCodec.TryDeserialize(SaveCodec.Serialize(s), out var back, out var err), err);
        Assert.Equal(new List<int?> { -1, null, 100, null }, back.Tournament!.Runner.Current);
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

    /// <summary>
    /// issue #624: スロット一覧が「実行中の自校名」でなく<b>そのセーブの校名</b>を出せるよう、
    /// ヘッダが <c>schoolName</c> を運ぶ。v4（issue #585）から本体に書かれている項目を覗くだけ＝
    /// スキーマ版は上げない。
    /// </summary>
    [Fact]
    public void PeekHeader_ReadsSchoolName()
    {
        var json = SaveCodec.Serialize(SaveTestFixtures.BuildRichState());
        Assert.Equal("北嶺", SaveCodec.PeekHeader(json)!.Value.SchoolName);
    }

    /// <summary>
    /// 校名が無い（v3以前）・型が違う・null のセーブでも<b>例外にならず null を返す</b>。
    /// 既定（桜丘）はヘッダで埋めない＝「読めなかった」と「桜丘という校名だった」を潰さないため。
    /// </summary>
    [Theory]
    [InlineData("{\"version\":8,\"week\":3,\"yearIndex\":1}")]                        // 項目ごと無い（v3以前相当）
    [InlineData("{\"version\":8,\"week\":3,\"yearIndex\":1,\"schoolName\":null}")]    // null
    [InlineData("{\"version\":8,\"week\":3,\"yearIndex\":1,\"schoolName\":42}")]      // 文字列でない
    public void PeekHeader_MissingSchoolName_IsNullNotThrow(string json)
    {
        var header = SaveCodec.PeekHeader(json);

        Assert.NotNull(header);
        Assert.Equal(3, header!.Value.Week);   // 他の項目は読めている（丸ごと失敗にしていない）
        Assert.Null(header.Value.SchoolName);
    }
}
