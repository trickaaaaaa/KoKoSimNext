using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using KokoSim.Engine.Core;
using KokoSim.Engine.Season.Events;
using Xunit;

namespace KokoSim.Engine.Tests.Season;

/// <summary>
/// イベント表（data/events.yaml）のパーサと文言バリエーション（issue #609）。
/// Unity は YamlDotNet を持たず<b>この純パーサだけ</b>で正典を読むので、形が壊れると
/// 実ゲームのイベントが丸ごと消える（例外もテスト失敗も出ずに「何も起きない週送り」に戻る）。
/// </summary>
public sealed class EventTableTests
{
    private const string Sample = @"
events:
  - id: fixed_one
    kind: Fixed
    calendar_week: 18
    text: ""夏合宿。""
    variants:
      - lines:
          - who: coach
            say: ""切り上げますか。""
          - who: manager
            say: ""あと五本。""
      - lines:
          - who: narration
            say: ""日が落ちてもノックの音だけが響いていた。""
  - id: random_one
    kind: Random
    weight: 2.5
    cooldown_weeks: 6
    sets_annual_flag: once_a_year
    text: ""故障した。""
    variants:
      - lines:
          - who: player
            name: ""{player}""
            say: ""監督、すみません。#1 じゃなくてもいいです。""
";

    [Fact]
    public void Parse_ReadsEventsAndVariantsAndLines()
    {
        var events = EventTableParser.Parse(Sample);

        Assert.Equal(2, events.Count);

        var fixedOne = events[0];
        Assert.Equal("fixed_one", fixedOne.Id);
        Assert.Equal(EventKind.Fixed, fixedOne.Kind);
        Assert.Equal(18, fixedOne.CalendarWeek);
        Assert.Equal("夏合宿。", fixedOne.Text);
        // ★ 2通り目が1通り目の続きとして吸われないこと（インデントで所属を決める理由）。
        Assert.Equal(2, fixedOne.Variants.Count);
        Assert.Equal(2, fixedOne.Variants[0].Lines.Count);
        Assert.Single(fixedOne.Variants[1].Lines);
        Assert.Equal("coach", fixedOne.Variants[0].Lines[0].Who);
        Assert.Equal("あと五本。", fixedOne.Variants[0].Lines[1].Say);

        var randomOne = events[1];
        Assert.Equal(EventKind.Random, randomOne.Kind);
        Assert.Equal(2.5, randomOne.Weight, 6);
        Assert.Equal(6, randomOne.CooldownWeeks);
        Assert.Equal("once_a_year", randomOne.SetsAnnualFlag);
        Assert.Equal("{player}", randomOne.Variants[0].Lines[0].Name);
        // 引用符の中の # は行末コメントとして落とさない。
        Assert.Equal("監督、すみません。#1 じゃなくてもいいです。", randomOne.Variants[0].Lines[0].Say);
    }

    /// <summary>
    /// <c>kind:</c> の全種別が読めること。パーサは未知の語を Random へ落とすので、種別を足したときに
    /// <c>ParseKind</c> を直し忘れると<b>例外もパース失敗も出ずに Random へ化ける</b>
    /// （＝週次抽選のプールに入り、重み合計が変わって発火の乱数列までずれる。issue #610 で実際に踏んだ）。
    /// </summary>
    [Theory]
    [InlineData("Fixed", EventKind.Fixed)]
    [InlineData("Random", EventKind.Random)]
    [InlineData("Choice", EventKind.Choice)]
    [InlineData("System", EventKind.System)]
    public void Parse_ReadsEveryKind(string word, EventKind expected)
    {
        var events = EventTableParser.Parse($"events:\n  - id: x\n    kind: {word}\n    text: \"t\"\n");
        Assert.Equal(expected, events[0].Kind);
    }

    [Fact]
    public void Parse_OldTableWithoutVariants_StillLoads()
    {
        var events = EventTableParser.Parse("events:\n  - id: legacy\n    kind: Random\n    text: \"雨。\"\n");
        Assert.Single(events);
        Assert.Empty(events[0].Variants);
        // バリエーションが無ければ text の1行を地の文として見せる（後方互換）。
        var script = EventScheduler.PickScript(events[0], 1, new Xoshiro256Random(1));
        Assert.Single(script.Lines);
        Assert.Equal("雨。", script.Lines[0].Say);
        Assert.True(EventRoles.IsNarration(script.Lines[0].Who));
    }

    // ===== 正典（data/events.yaml）そのものの検査 =====

    private static IReadOnlyList<GameEvent> RepositoryEvents()
        => EventTableParser.Parse(File.ReadAllText(FindDataFile("events.yaml")));

    [Fact]
    public void RepositoryTable_ParsesAndEveryEventHasText()
    {
        var events = RepositoryEvents();
        Assert.NotEmpty(events);
        Assert.All(events, e =>
        {
            Assert.False(string.IsNullOrWhiteSpace(e.Id));
            Assert.False(string.IsNullOrWhiteSpace(e.Text), $"{e.Id} に通知フィード用の text がない");
        });
        // ID の重複はクールダウンが片方にしか効かない形で壊れるので弾く。
        Assert.Equal(events.Count, events.Select(e => e.Id).Distinct().Count());
    }

    /// <summary>
    /// 既存イベントが消えていないこと。文言を書き足すつもりでファイルを書き直したときに
    /// 行ごと落ちるのが一番起きやすく（実際に issue #609 の作業中、選択肢イベント2件を落とした）、
    /// <b>抽選プールが変わるので発火の乱数列まで動く</b>＝黙って「いつ何が起きるか」がずれる。
    /// 追加は自由（部分集合で判定）。
    /// </summary>
    [Fact]
    public void RepositoryTable_KeepsEveryRegisteredEvent()
    {
        var required = new[]
        {
            "entrance_ceremony", "summer_camp", "culture_festival", "winter_camp", "draft_meeting", "graduation",
            // "injury"（週次の故障の会話）は issue #695 で削除＝効果が無く、誰も故障していないのに語っていた。
            "rain_week", "media_coverage", "ob_donation", "recruit_rival_move",
            "ace_hidden_injury", "skipping_practice",
            // システムイベント（issue #610〜）。IDは呼び出し側にベタ書きされているので、
            // 消えると「その出来事だけ黙って会話が出ない」形で壊れる。
            "injury_occurred", "injury_returned",
            "growth_awakening", "growth_breakthrough", "growth_slump",
            "growth_plateau", "growth_yips", "growth_yips_overcome",
            "mgr_koshien_experience", "mgr_rival_lesson", "mgr_big_loss",
            "mgr_seminar", "mgr_pro_ob", "mgr_mentor",
            "insight_growth_type",
            "draft_candidate", "draft_scout_visit", "draft_nominated", "draft_missed",
            "koshien_berth_donation", "koshien_draw",
            "senbatsu_berth_donation", "senbatsu_draw", "senbatsu_not_selected",
            "booster_club_fund", "gear_broken", "local_company_support",
            "outside_coach_visit", "self_practice_awakened", "exam_week_slump",
            "region_snow_indoor", "region_local_festival", "region_urban_ground_share",
        };
        var ids = RepositoryEvents().Select(e => e.Id).ToHashSet();
        foreach (var id in required) Assert.True(ids.Contains(id), $"イベント {id} が events.yaml から消えている");
    }

    [Fact]
    public void RepositoryTable_WeeklyEventsHaveSeveralVariants()
    {
        // 毎年・何度も出るイベントは同じ文が続くと一気に飽きるので、複数通り持たせる。
        // 週次抽選に乗らないもの（weight 0＝別経路で出す）は対象外。System は出番が多いので対象。
        foreach (var e in RepositoryEvents())
        {
            if (e.Kind != EventKind.Fixed && e.Kind != EventKind.System && e.Weight <= 0) continue;
            Assert.True(e.Variants.Count >= 2, $"{e.Id} の会話が {e.Variants.Count} 通りしかない");
            Assert.All(e.Variants, v => Assert.NotEmpty(v.Lines));
        }
    }

    /// <summary>
    /// システムイベント（issue #610〜）は週次抽選に載らないこと。載ると「誰も故障していないのに
    /// 故障の会話だけ出る」＝主役の居ない会話になる（{player} が無関係の部員に化ける）。
    /// </summary>
    [Fact]
    public void SystemEvents_AreNotDrawnByTheWeeklyLottery()
    {
        var system = RepositoryEvents().Where(e => e.Kind == EventKind.System).ToList();
        Assert.NotEmpty(system);

        var scheduler = new EventScheduler(system, new EventCoefficients { WeeklyFireProbability = 1.0 });
        var rng = new Xoshiro256Random(9);
        for (var w = 0; w < 50; w++) Assert.Empty(scheduler.Week(w, rng));
    }

    /// <summary>
    /// 主役つき提示は<b>同じ週でも人が違えば別の会話</b>になること（issue #610）。
    /// 同じ週に2人が同時に故障したとき、一字一句同じ会話が2回続くと「バグに見える」。
    /// </summary>
    [Fact]
    public void PickScript_WithSalt_DiffersPerSubject_AndIsStablePerSubject()
    {
        var e = RepositoryEvents().First(x => x.Id == "injury_occurred");
        var rng = new Xoshiro256Random(31);

        // 同じ人・同じ週なら常に同じ会話。
        Assert.Same(EventScheduler.PickScript(e, 10, 7, rng), EventScheduler.PickScript(e, 10, 7, rng));

        // 人が違えば少なくとも1回は別の会話になる。
        var seen = new HashSet<EventScript>();
        for (var id = 1; id <= 30; id++) seen.Add(EventScheduler.PickScript(e, 10, id, rng));
        Assert.True(seen.Count >= 2, "同じ週だと誰が故障しても同じ会話になる");
    }

    /// <summary>
    /// salt 無しの呼び出しが従来と1ビットも変わらないこと（既存の週次イベントの会話が動かない）。
    /// </summary>
    [Fact]
    public void PickScript_WithoutSalt_IsUnchanged()
    {
        var e = RepositoryEvents().First(x => x.Variants.Count >= 3);
        var rng = new Xoshiro256Random(44);
        for (var w = 1; w <= 20; w++)
            Assert.Same(EventScheduler.PickScript(e, w, rng), EventScheduler.PickScript(e, w, 0, rng));
    }

    /// <summary>
    /// 故障はマネージャーの一言で言い切る（issue #695・2026-10-28 監督決定）。小芝居を挟まず、
    /// その1行に誰が・何で・何週離脱かが全部入る＝結果行に頼らない。
    /// </summary>
    [Fact]
    public void InjuryOccurred_IsOneTeamManagerLine_NamingWhoWhatAndWeeks()
    {
        var e = RepositoryEvents().First(x => x.Id == "injury_occurred");
        Assert.True(e.Variants.Count >= 2, "主役で会話を分けるので2通り以上要る");
        foreach (var v in e.Variants)
        {
            var line = Assert.Single(v.Lines);
            Assert.Equal(EventRoles.TeamManager, line.Who);
            var keys = EventTextFormat.KeysIn(line.Say);
            Assert.Contains("player", keys);
            Assert.Contains("injury", keys);
            Assert.Contains("weeks", keys);
        }

        var said = EventTextFormat.Apply(e.Variants[0].Lines[0].Say, new Dictionary<string, string>
        {
            ["player"] = "山田", ["injury"] = "足首の捻挫", ["weeks"] = "3",
        });
        Assert.DoesNotContain("{", said);
        Assert.Contains("足首の捻挫", said);
        Assert.Contains("全治3週間", said);
    }

    /// <summary>
    /// 週次の抽選表に「故障した」と語るだけの会話を置かない（issue #695）。効果の無いランダム抽選なので、
    /// 選ばれた部員は実際には故障しておらず、語られた選手が普通に練習している状態になっていた。
    /// 故障は InjuryModel が起こし、主役つきの injury_occurred が語る。
    /// </summary>
    [Fact]
    public void RepositoryTable_HasNoWeeklyDrawnInjuryTalk()
    {
        foreach (var e in RepositoryEvents().Where(x => x.Kind == EventKind.Random))
        {
            Assert.NotEqual("injury", e.Id);
            Assert.DoesNotContain("故障", e.Text);
        }
    }

    [Fact]
    public void RepositoryTable_UsesOnlyKnownPlaceholders()
    {
        // 差し込み語の書き間違いは「{plyaer}」のまま画面に出る。データ側で先に止める。
        foreach (var e in RepositoryEvents())
        foreach (var v in e.Variants)
        foreach (var line in v.Lines)
        foreach (var key in EventTextFormat.KeysIn(line.Say).Concat(EventTextFormat.KeysIn(line.Name)))
            Assert.True(EventTextFormat.KnownKeys.Contains(key), $"{e.Id}: 未知の差し込み語 {{{key}}}");
    }

    /// <summary>
    /// 結果行（<see cref="EventRoles.Result"/>）は地の文と同じく話者を持たない。yaml には書かず提示層が
    /// 足す行なので、既定名が空で「声の無い行」に分類されることだけ固定する（立ち絵スロットが空で出ない）。
    /// </summary>
    [Fact]
    public void ResultRole_IsVoicelessAndUnnamed()
    {
        Assert.Equal("", EventRoles.DefaultName(EventRoles.Result));
        Assert.True(EventRoles.IsResult(EventRoles.Result));
        Assert.False(EventRoles.IsNarration(EventRoles.Result));
        Assert.True(EventRoles.IsVoiceless(EventRoles.Result));
        Assert.True(EventRoles.IsVoiceless(EventRoles.Narration));
        Assert.False(EventRoles.IsVoiceless(EventRoles.Manager));
    }

    [Fact]
    public void RepositoryTable_UsesOnlyKnownRoles()
    {
        var known = new HashSet<string>
        {
            EventRoles.Manager, EventRoles.Player, EventRoles.TeamManager, EventRoles.Ob,
            EventRoles.Scout, EventRoles.Local, EventRoles.Captain, EventRoles.Narration,
        };
        foreach (var e in RepositoryEvents())
        foreach (var v in e.Variants)
        foreach (var line in v.Lines)
            Assert.True(known.Contains(line.Who), $"{e.Id}: 未知の役割 {line.Who}（立ち絵が出ない）");
    }

    /// <summary>
    /// 監督に語らせすぎない（監督指示 2026-08-19・issue event-dialogue-manager-too-cool）。
    /// 旧版は 109 通り中 104 通りが監督の決め台詞で締まり、（……）の独白 26 本で自分の哲学を語る
    /// 「ナルシスト監督」だった。プレイヤー＝監督なので、ゲームが勝手に自分の口から臭い台詞を出す形になる。
    /// 文体の良し悪しは検査できないが、<b>構造</b>（締める・独白・連発・溜め）は検査できるので固定する。
    /// AI で会話を量産すると同じ癖が再発するための歯止め。
    /// </summary>
    [Fact]
    public void RepositoryTable_ManagerDoesNotPreach()
    {
        var variants = 0;
        var managerCloses = 0;
        foreach (var e in RepositoryEvents())
        foreach (var v in e.Variants)
        {
            variants++;
            var managerLines = v.Lines.Count(l => l.Who == EventRoles.Manager);
            Assert.True(managerLines <= 1, $"{e.Id}: 1つの会話に監督の発話が {managerLines} 本（1本まで）");

            var ellipses = v.Lines.Sum(l => Occurrences(l.Say, "……"));
            Assert.True(ellipses <= 1, $"{e.Id}: 「……」が1会話に {ellipses} 回（溜めは1回まで）");

            foreach (var l in v.Lines.Where(l => l.Who == EventRoles.Manager))
            {
                Assert.False(l.Say.StartsWith("（"), $"{e.Id}: 監督の独白「{l.Say}」（プレイヤーの内面を勝手に決めない）");
                Assert.True(l.Say.Length <= 24, $"{e.Id}: 監督の台詞が長い（{l.Say.Length}字）「{l.Say}」");
            }
            if (v.Lines[^1].Who == EventRoles.Manager) managerCloses++;
        }
        Assert.True(variants > 0);
        Assert.True(managerCloses * 3 <= variants,
            $"監督の行で締まる会話が {managerCloses}/{variants}（1/3 まで。締めは地の文・部員・マネージャーへ）");
    }

    private static int Occurrences(string s, string needle)
    {
        var n = 0;
        for (var i = s.IndexOf(needle, StringComparison.Ordinal); i >= 0; i = s.IndexOf(needle, i + needle.Length, StringComparison.Ordinal)) n++;
        return n;
    }

    // ===== 効果（issue #613） =====

    [Fact]
    public void Parse_ReadsEffects()
    {
        var events = EventTableParser.Parse(
            "events:\n  - id: donation\n    kind: Random\n    text: \"寄付。\"\n"
            + "    effects:\n      funds: 30\n      facility_fund: -12.5\n");

        Assert.Equal(30.0, events[0].Effect.Funds, 6);
        Assert.Equal(-12.5, events[0].Effect.FacilityFund, 6);
        Assert.False(events[0].Effect.IsEmpty);
    }

    [Fact]
    public void Parse_WithoutEffects_HasNoEffect()
    {
        var events = EventTableParser.Parse("events:\n  - id: talk\n    kind: Random\n    text: \"雑談。\"\n");
        Assert.True(events[0].Effect.IsEmpty);
    }

    /// <summary>
    /// 効果が<b>次のイベントへ漏れない</b>こと。1件ぶんの素を使い回すパーサなので、
    /// 取り出しのたびに戻し忘れると、寄付イベントの次のイベントまで部費が増える。
    /// </summary>
    [Fact]
    public void Parse_EffectsDoNotLeakIntoTheNextEvent()
    {
        var events = EventTableParser.Parse(
            "events:\n  - id: a\n    text: \"寄付。\"\n    effects:\n      funds: 30\n"
            + "  - id: b\n    text: \"雑談。\"\n");

        Assert.Equal(30.0, events[0].Effect.Funds, 6);
        Assert.True(events[1].Effect.IsEmpty, "前のイベントの効果が次へ漏れている");
    }

    /// <summary>
    /// 財布の区別（設計書04 §1.4）。後援会・OB寄付は<b>施設基金</b>（建物にしか使えない）へ、
    /// 道具の破損は<b>部費</b>から。ここが入れ替わると、寄付で道具が買えてしまう。
    /// </summary>
    [Fact]
    public void RepositoryTable_FundEvents_UseTheRightWallet()
    {
        var byId = RepositoryEvents().ToDictionary(e => e.Id);

        Assert.True(byId["booster_club_fund"].Effect.FacilityFund > 0, "後援会の寄付が施設基金へ入っていない");
        Assert.Equal(0.0, byId["booster_club_fund"].Effect.Funds, 6);

        Assert.True(byId["gear_broken"].Effect.Funds < 0, "道具の破損が部費から出ていない");
        Assert.Equal(0.0, byId["gear_broken"].Effect.FacilityFund, 6);

        Assert.True(byId["ob_donation"].Effect.Funds > 0);
        Assert.True(byId["local_company_support"].Effect.Funds > 0);
    }

    /// <summary>
    /// 期間つきの練習効率補正（issue #615）が読めること。<b>倍率と週数の両方</b>が要る
    /// （片方だけだと「効くけど終わらない」「終わるけど効かない」になる）。
    /// </summary>
    [Fact]
    public void Parse_ReadsTrainingBoost()
    {
        var events = EventTableParser.Parse(
            "events:\n  - id: camp\n    text: \"合宿。\"\n"
            + "    effects:\n      training_mult: 1.15\n      training_weeks: 3\n      training_target: player\n");

        Assert.Equal(1.15, events[0].Effect.TrainingMult, 6);
        Assert.Equal(3, events[0].Effect.TrainingWeeks);
        Assert.True(events[0].Effect.TrainingTargetIsPlayer);
        Assert.True(events[0].Effect.HasTrainingBoost);
    }

    [Fact]
    public void TrainingBoost_NeedsBothMultiplierAndWeeks()
    {
        var onlyMult = EventTableParser.Parse(
            "events:\n  - id: a\n    text: \"t\"\n    effects:\n      training_mult: 1.2\n")[0];
        var onlyWeeks = EventTableParser.Parse(
            "events:\n  - id: b\n    text: \"t\"\n    effects:\n      training_weeks: 4\n")[0];

        Assert.False(onlyMult.Effect.HasTrainingBoost);
        Assert.False(onlyWeeks.Effect.HasTrainingBoost);
        Assert.True(onlyMult.Effect.IsEmpty);
        Assert.True(onlyWeeks.Effect.IsEmpty);
    }

    /// <summary>既定は部全体（<c>training_target</c> 省略）。個人向けを既定にすると全体強化が効かない。</summary>
    [Fact]
    public void RepositoryTable_TrainingEvents_HaveSaneTargets()
    {
        var byId = RepositoryEvents().ToDictionary(e => e.Id);
        Assert.False(byId["outside_coach_visit"].Effect.TrainingTargetIsPlayer, "外部コーチは部全体のはず");
        Assert.True(byId["self_practice_awakened"].Effect.TrainingTargetIsPlayer, "自主練は本人だけのはず");
        Assert.True(byId["exam_week_slump"].Effect.TrainingMult < 1.0, "テスト週間は効率が下がるはず");
    }

    // ===== 地域イベント（issue #616） =====

    /// <summary>
    /// 地方指定つきイベントは<b>その地方でだけ</b>出ること。地方を設定していないスケジューラ
    /// （ヘッドレスシム）では一切出ない＝その土地でしか起きない話が全国に混ざらない。
    /// </summary>
    [Fact]
    public void RegionalEvents_FireOnlyInTheirRegion()
    {
        var snow = RepositoryEvents().Where(e => e.Id == "region_snow_indoor").ToList();
        Assert.Single(snow);
        Assert.Equal(KokoSim.Engine.Nation.JapanRegion.HokkaidoTohoku, snow[0].RequiresRegion);

        var coeff = new EventCoefficients { WeeklyFireProbability = 1.0 };

        // 地方を設定していない＝出ない。
        var noRegion = new EventScheduler(snow, coeff);
        var rng = new Xoshiro256Random(5);
        for (var w = 0; w < 60; w++) Assert.Empty(noRegion.Week(w, rng));

        // 別の地方＝出ない。
        var kanto = new EventScheduler(snow, coeff) { Region = KokoSim.Engine.Nation.JapanRegion.Kanto };
        for (var w = 0; w < 60; w++) Assert.Empty(kanto.Week(w, rng));

        // その地方＝出る。
        var tohoku = new EventScheduler(snow, coeff) { Region = KokoSim.Engine.Nation.JapanRegion.HokkaidoTohoku };
        var fired = 0;
        for (var w = 0; w < 60; w++) fired += tohoku.Week(w, rng).Count;
        Assert.True(fired > 0, "その地方でも発火しない");
    }

    /// <summary>
    /// 未知の地方名は<b>例外</b>にすること。既定値へ落とすと、綴りを間違えた雪国イベントが
    /// 全国どこでも出る（絵では気づけない）。
    /// </summary>
    [Fact]
    public void Parse_UnknownRegion_Throws()
    {
        Assert.Throws<FormatException>(() => EventTableParser.Parse(
            "events:\n  - id: x\n    text: \"t\"\n    requires_region: Hokkaido\n"));
    }

    // ===== バリエーション抽選 =====

    [Fact]
    public void PickScript_IsStableForTheSameWeek_AndVariesAcrossWeeks()
    {
        var e = RepositoryEvents().First(x => x.Variants.Count >= 3);
        var rng = new Xoshiro256Random(20260813);

        // 同じ週なら何度引いても同じ会話（開き直しても文が変わらない）。
        var a = EventScheduler.PickScript(e, 12, rng);
        var b = EventScheduler.PickScript(e, 12, rng);
        Assert.Same(a, b);

        // 週をまたげば少なくとも1回は別の会話になる（固定化していない）。
        var seen = new HashSet<EventScript>();
        for (var w = 1; w <= 40; w++) seen.Add(EventScheduler.PickScript(e, w, rng));
        Assert.True(seen.Count >= 2, "週が変わっても同じ会話しか出ない");
    }

    [Fact]
    public void PickScript_DoesNotAdvanceTheParentStream()
    {
        // ★ 恒等の担保: バリエーションは Fork で引くので、通数を増やしても
        //    「いつ何が起きるか」（発火抽選の乱数列）は1語もずれない。
        var e = RepositoryEvents().First(x => x.Variants.Count >= 2);
        var a = new Xoshiro256Random(77);
        var b = new Xoshiro256Random(77);

        EventScheduler.PickScript(e, 5, a);
        for (var i = 0; i < 8; i++) Assert.Equal(b.NextDouble(), a.NextDouble(), 12);
    }

    private static string FindDataFile(string fileName)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var candidate = Path.Combine(dir.FullName, "data", fileName);
            if (File.Exists(candidate)) return candidate;
            dir = dir.Parent;
        }
        throw new FileNotFoundException($"data/{fileName} がリポジトリ内に見つかりません。");
    }
}
