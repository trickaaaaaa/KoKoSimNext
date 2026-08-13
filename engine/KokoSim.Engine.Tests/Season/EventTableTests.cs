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
            "injury", "rain_week", "media_coverage", "ob_donation", "recruit_rival_move",
            "ace_hidden_injury", "skipping_practice",
        };
        var ids = RepositoryEvents().Select(e => e.Id).ToHashSet();
        foreach (var id in required) Assert.True(ids.Contains(id), $"イベント {id} が events.yaml から消えている");
    }

    [Fact]
    public void RepositoryTable_WeeklyEventsHaveSeveralVariants()
    {
        // 毎年・何度も出るイベントは同じ文が続くと一気に飽きるので、複数通り持たせる。
        // 週次抽選に乗らないもの（weight 0＝別経路で出す）は対象外。
        foreach (var e in RepositoryEvents())
        {
            if (e.Kind != EventKind.Fixed && e.Weight <= 0) continue;
            Assert.True(e.Variants.Count >= 2, $"{e.Id} の会話が {e.Variants.Count} 通りしかない");
            Assert.All(e.Variants, v => Assert.NotEmpty(v.Lines));
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

    [Fact]
    public void RepositoryTable_UsesOnlyKnownRoles()
    {
        var known = new HashSet<string>
        {
            EventRoles.Manager, EventRoles.Player, EventRoles.Coach, EventRoles.Ob,
            EventRoles.Scout, EventRoles.Local, EventRoles.Captain, EventRoles.Narration,
        };
        foreach (var e in RepositoryEvents())
        foreach (var v in e.Variants)
        foreach (var line in v.Lines)
            Assert.True(known.Contains(line.Who), $"{e.Id}: 未知の役割 {line.Who}（立ち絵が出ない）");
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
