using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using KokoSim.Engine.Core;
using KokoSim.Engine.Match.Game;
using KokoSim.Engine.Season;
using KokoSim.Engine.Season.Events;
using Xunit;

namespace KokoSim.Engine.Tests.Season;

/// <summary>
/// 選手間イベントの選択肢と好転/悪化（issue #945・#1599・設計書23 §3.3）。YAML の形・確率の式の向き・
/// 結束の振れ幅（対応ごとに違う。#1599）・抽選の決定論・当事者の選び方を固定する。
/// </summary>
public sealed class EventChoiceTests
{
    private static readonly CohesionCoefficients C = new();

    // ===== パーサ =====

    private const string Sample = @"
events:
  - id: clash
    kind: Choice
    text: ""もめた。""
    cast:
      subject: selfish
      other: selfless
    variants:
      - lines:
          - who: player
            say: ""打ちたかった。""
      - lines:
          - who: narration
            say: ""空振り。""
    choices:
      - stance: scold
        label: ""{player} を叱る""
      - stance: listen
        label: ""話を聞く""
      - stance: captain
        label: ""主将に任せる""
    outcomes:
      - stance: scold
        result: improved
        lines:
          - who: player
            name: ""{player}""
            say: ""すみません。""
          - who: narration
            say: ""翌日から来た。""
      - stance: scold
        result: worsened
        lines:
          - who: player
            say: ""はいはい。""
  - id: next
    kind: Random
    text: ""雑談。""
    variants:
      - lines:
          - who: player
            say: ""雨ですね。""
";

    [Fact]
    public void Parse_ReadsChoicesOutcomesAndCast()
    {
        var events = EventTableParser.Parse(Sample);
        Assert.Equal(2, events.Count);
        var e = events[0];

        Assert.Equal(2, e.Variants.Count);
        Assert.Single(e.Variants[0].Lines);
        Assert.Equal(new[] { EventStance.Scold, EventStance.Listen, EventStance.Captain }, e.Choices.Select(c => c.Stance));
        Assert.Equal("{player} を叱る", e.Choices[0].Label);
        Assert.Equal(2, e.Outcomes.Count);
        Assert.Equal(EventStance.Scold, e.Outcomes[0].Stance);
        Assert.True(e.Outcomes[0].Improved);
        Assert.Equal(2, e.Outcomes[0].Script.Lines.Count);
        Assert.Equal("{player}", e.Outcomes[0].Script.Lines[0].Name);
        Assert.False(e.Outcomes[1].Improved);
        Assert.Single(e.Outcomes[1].Script.Lines);
        Assert.Equal(new EventCast(EventCastRule.Selfish, EventCastRule.Selfless), e.Cast);
        Assert.True(e.HasChoices);

        // 次のイベントへ選択肢・結果・当事者の条件が漏れない（1件ぶんの素を使い回すパーサなので戻し忘れが起きやすい）。
        var next = events[1];
        Assert.False(next.HasChoices);
        Assert.Empty(next.Outcomes);
        Assert.Equal(EventCast.Default, next.Cast);
        Assert.Single(next.Variants);
    }

    [Theory]
    [InlineData("    choices:\n      - stance: yell\n        label: \"x\"\n")]
    [InlineData("    outcomes:\n      - stance: scold\n        result: maybe\n        lines:\n          - who: player\n            say: \"x\"\n")]
    [InlineData("    cast:\n      subject: rookie\n")]
    public void Parse_UnknownChoiceWords_Throw(string block)
    {
        var yaml = "events:\n  - id: a\n    kind: Choice\n    text: \"a\"\n" + block;
        Assert.Throws<FormatException>(() => EventTableParser.Parse(yaml));
    }

    // ===== 正典（data/events.yaml） =====

    private static readonly string[] BranchedIds = { "skipping_practice", "grade_friction", "selfish_clash" };

    /// <summary>選手間のもめ事への3つの対応（性格で好転の確率が振れ、結束を動かす）。</summary>
    private static readonly EventStance[] CohesionStances = { EventStance.Scold, EventStance.Listen, EventStance.Captain };

    private static IReadOnlyList<GameEvent> RepositoryEvents()
        => EventTableParser.Parse(File.ReadAllText(FindDataFile("events.yaml")));

    [Fact]
    public void RepositoryTable_BranchedEvents_HaveAllThreeStancesAndSixOutcomes()
    {
        var byId = RepositoryEvents().ToDictionary(e => e.Id);
        foreach (var id in BranchedIds)
        {
            Assert.True(byId.ContainsKey(id), $"{id} が events.yaml に無い");
            var e = byId[id];
            Assert.Equal(EventKind.Choice, e.Kind);
            Assert.True(e.Variants.Count >= 2, $"{id} の出だしの会話が {e.Variants.Count} 通り");
            Assert.Equal(new[] { EventStance.Scold, EventStance.Listen, EventStance.Captain },
                e.Choices.Select(c => c.Stance).ToArray());
            Assert.All(e.Choices, c => Assert.False(string.IsNullOrWhiteSpace(c.Label), $"{id}: 選択肢の言い回しが空"));
            foreach (var s in CohesionStances)
            foreach (var improved in new[] { true, false })
                Assert.True(e.Outcomes.Any(o => o.Stance == s && o.Improved == improved && o.Script.Lines.Count > 0),
                    $"{id}: {s}/{(improved ? "好転" : "悪化")} の会話が無い");
        }

        // 当事者の条件（監督決定 2026-11-14）: 衝突は性格の「試合での姿勢」、もめ事は学年で選ぶ。
        Assert.Equal(new EventCast(EventCastRule.Selfish, EventCastRule.Selfless), byId["selfish_clash"].Cast);
        Assert.Equal(new EventCast(EventCastRule.UpperGrade, EventCastRule.FirstYear), byId["grade_friction"].Cast);
        Assert.Equal(EventCast.Default, byId["skipping_practice"].Cast);
    }

    /// <summary>
    /// 監督は選択肢を選ぶだけ（監督に語らせない・2026-08-19 方針）。出だしの会話にも結果の会話にも監督の行を置かない。
    /// 「……」の溜めは1会話に1回まで（既存の構造検査と同じ基準を結果の会話にも当てる）。
    /// </summary>
    [Fact]
    public void RepositoryTable_BranchedEvents_ManagerNeverSpeaks()
    {
        foreach (var e in RepositoryEvents().Where(x => x.HasChoices))
        {
            var scripts = e.Variants.Concat(e.Outcomes.Select(o => o.Script));
            foreach (var s in scripts)
            {
                Assert.DoesNotContain(s.Lines, l => l.Who == EventRoles.Manager);
                var ellipses = s.Lines.Sum(l => Occurrences(l.Say, "……"));
                Assert.True(ellipses <= 1, $"{e.Id}: 「……」が1会話に {ellipses} 回");
            }
        }
    }

    [Fact]
    public void RepositoryTable_ChoiceTexts_UseOnlyKnownPlaceholdersAndRoles()
    {
        var roles = new HashSet<string>
        {
            EventRoles.Player, EventRoles.TeamManager, EventRoles.Ob, EventRoles.Scout, EventRoles.Local,
            EventRoles.Captain, EventRoles.Narration,
        };
        foreach (var e in RepositoryEvents())
        {
            foreach (var c in e.Choices)
            foreach (var key in EventTextFormat.KeysIn(c.Label))
                Assert.True(EventTextFormat.KnownKeys.Contains(key), $"{e.Id}: 選択肢に未知の差し込み語 {{{key}}}");
            foreach (var o in e.Outcomes)
            foreach (var l in o.Script.Lines)
            {
                Assert.True(roles.Contains(l.Who), $"{e.Id}: 結果の会話に未知の役割 {l.Who}");
                foreach (var key in EventTextFormat.KeysIn(l.Say).Concat(EventTextFormat.KeysIn(l.Name)))
                    Assert.True(EventTextFormat.KnownKeys.Contains(key), $"{e.Id}: 未知の差し込み語 {{{key}}}");
            }
            // {other} を使うのは相手を立てるイベントだけ（相手が居ないと {other} のまま画面に出る）。
            if (e.Cast.Other is null)
            {
                var all = e.Variants.Concat(e.Outcomes.Select(o => o.Script)).SelectMany(s => s.Lines)
                    .SelectMany(l => EventTextFormat.KeysIn(l.Say).Concat(EventTextFormat.KeysIn(l.Name)))
                    .Concat(e.Choices.SelectMany(c => EventTextFormat.KeysIn(c.Label)));
                Assert.DoesNotContain("other", all);
            }
        }
    }

    // ===== 結束の振れ幅（issue #1599・オーナー決定2026-11-25: 叱る＝大きく振れる／話を聞く＝小さく安全／主将に任せる＝主将次第） =====

    [Fact]
    public void CohesionDelta_Scold_IsLargeSwing()
    {
        Assert.Equal(C.ScoldImproveDelta, EventChoiceResolver.CohesionDelta(EventStance.Scold, true, null, C), 12);
        Assert.Equal(C.ScoldWorsenDelta, EventChoiceResolver.CohesionDelta(EventStance.Scold, false, null, C), 12);
        Assert.Equal(5.0, C.ScoldImproveDelta, 12);
        Assert.Equal(-7.0, C.ScoldWorsenDelta, 12);
    }

    [Fact]
    public void CohesionDelta_Listen_IsSmallAndSafe()
    {
        Assert.Equal(C.ListenImproveDelta, EventChoiceResolver.CohesionDelta(EventStance.Listen, true, null, C), 12);
        Assert.Equal(C.ListenWorsenDelta, EventChoiceResolver.CohesionDelta(EventStance.Listen, false, null, C), 12);
        Assert.True(System.Math.Abs(C.ListenImproveDelta) < System.Math.Abs(C.ScoldImproveDelta));
        Assert.True(System.Math.Abs(C.ListenWorsenDelta) < System.Math.Abs(C.ScoldWorsenDelta));
    }

    /// <summary>主将に任せる: 振れ幅そのものが主将の統率力次第（強い主将ほど上振れ大・下振れ小。主将不在がいちばん悪い）。</summary>
    [Fact]
    public void CohesionDelta_Captain_DependsOnCaptainPower()
    {
        var strongCaptain = 49.0 + C.ChoiceCaptainPowerScale;    // dCap = +1
        var weakCaptain = 49.0 - C.ChoiceCaptainPowerScale;      // dCap = -1
        var strongImprove = EventChoiceResolver.CohesionDelta(EventStance.Captain, true, strongCaptain, C);
        var weakImprove = EventChoiceResolver.CohesionDelta(EventStance.Captain, true, weakCaptain, C);
        var noneImprove = EventChoiceResolver.CohesionDelta(EventStance.Captain, true, null, C);
        var strongWorsen = EventChoiceResolver.CohesionDelta(EventStance.Captain, false, strongCaptain, C);
        var weakWorsen = EventChoiceResolver.CohesionDelta(EventStance.Captain, false, weakCaptain, C);

        Assert.True(strongImprove > weakImprove, "強い主将のほうが好転の上振れが大きいはず");
        Assert.Equal(weakImprove, noneImprove, 12);   // 主将不在は d主将=-1 で弱い主将と同じ
        Assert.True(strongWorsen > weakWorsen, "弱い主将のほうが悪化の下振れが大きい（絶対値が大きい）はず");
    }

    // ===== 確率の式の向き =====

    private static PersonalityAxes Axes(int lead = 50, int recept = 50, int dilig = 50, int selfless = 50)
        => new(lead, recept, dilig, selfless);

    private static double P(EventStance s, PersonalityAxes a, double? cap = 46.0)
        => EventChoiceResolver.ImproveProbability(s, a, cap, C);

    [Fact]
    public void Probability_NeutralSubject_IsTheStanceBase()
    {
        Assert.Equal(C.ScoldBase, P(EventStance.Scold, PersonalityAxes.Neutral), 12);
        Assert.Equal(C.ListenBase, P(EventStance.Listen, PersonalityAxes.Neutral), 12);
        Assert.Equal(C.CaptainBase, P(EventStance.Captain, PersonalityAxes.Neutral, cap: C.CaptainPowerNeutral), 12);
    }

    /// <summary>素直な当事者には叱るが効き、頑固な当事者には話を聞く方が効く（監督決定の例そのもの）。</summary>
    [Fact]
    public void Probability_Receptiveness_FlipsScoldVersusListen()
    {
        var obedient = Axes(recept: 85);
        var stubborn = Axes(recept: 15);
        Assert.True(P(EventStance.Scold, obedient) > P(EventStance.Scold, stubborn));
        Assert.True(P(EventStance.Listen, stubborn) > P(EventStance.Listen, obedient));
        Assert.True(P(EventStance.Scold, obedient) > P(EventStance.Listen, obedient), "素直なら叱るが最善のはず");
        Assert.True(P(EventStance.Listen, stubborn) > P(EventStance.Scold, stubborn), "頑固なら聞くが最善のはず");
    }

    /// <summary>統率の強い主将なら任せるが効く。主将不在がいちばん効かない。当事者が「引っ張る」側だと効きにくい。</summary>
    [Fact]
    public void Probability_Captain_FollowsCaptainPowerAndSubjectLeadership()
    {
        var strong = P(EventStance.Captain, PersonalityAxes.Neutral, cap: 80 * 85 / 100.0);
        var weak = P(EventStance.Captain, PersonalityAxes.Neutral, cap: 30 * 40 / 100.0);
        var none = P(EventStance.Captain, PersonalityAxes.Neutral, cap: null);
        Assert.True(strong > weak && weak >= none);
        Assert.True(strong > P(EventStance.Scold, PersonalityAxes.Neutral), "強い主将なら任せるが叱るより効くはず");
        Assert.True(P(EventStance.Captain, Axes(lead: 20)) > P(EventStance.Captain, Axes(lead: 85)));
    }

    [Fact]
    public void Probability_Diligence_HelpsEveryStance_AndSelfishnessFavorsListening()
    {
        foreach (var s in CohesionStances)
            Assert.True(P(s, Axes(dilig: 85)) > P(s, Axes(dilig: 15)), $"{s}: 勤勉さで好転しやすくならない");
        Assert.True(P(EventStance.Listen, Axes(selfless: 15)) > P(EventStance.Listen, Axes(selfless: 85)));
    }

    [Fact]
    public void Probability_StaysWithinBounds()
    {
        foreach (var s in CohesionStances)
        foreach (var v in new[] { 1, 99 })
        foreach (var cap in new double?[] { null, 0, 99 })
        {
            var p = P(s, Axes(v, v, v, v), cap);
            Assert.InRange(p, C.ChoiceProbMin, C.ChoiceProbMax);
            p = P(s, Axes(v, 100 - v, v, 100 - v), cap);
            Assert.InRange(p, C.ChoiceProbMin, C.ChoiceProbMax);
        }
    }

    // ===== 完了条件（issue #1599・オーナー決定2026-11-25）: 合う対応で好転7〜8割・合わない対応で2〜3割 =====

    /// <summary>叱る: 素直な当事者（合う）で7〜8割・頑固な当事者（合わない）で2〜3割。</summary>
    [Fact]
    public void Completion_Scold_MatchingReceptiveness_SevenToEightyPercent_MismatchUnderThirty()
    {
        var match = P(EventStance.Scold, Axes(recept: 85));
        var mismatch = P(EventStance.Scold, Axes(recept: 15));
        Assert.InRange(match, 0.70, 0.80);
        Assert.InRange(mismatch, 0.20, 0.30);
    }

    /// <summary>話を聞く: 頑固＋目立ちたがり（合う）で7〜8割・素直＋献身派（合わない）で2〜3割。</summary>
    [Fact]
    public void Completion_Listen_MatchingStubbornAndSelfish_SevenToEightyPercent_MismatchUnderThirty()
    {
        var match = P(EventStance.Listen, Axes(recept: 15, selfless: 15));
        var mismatch = P(EventStance.Listen, Axes(recept: 85, selfless: 85));
        Assert.InRange(match, 0.70, 0.80);
        Assert.InRange(mismatch, 0.20, 0.30);
    }

    /// <summary>
    /// 主将に任せる: 「引っ張らない当事者」×「統率力のある主将」（合う）で7〜8割、
    /// 「引っ張る当事者」×「統率力の弱い主将」（合わない）で2〜3割。
    /// </summary>
    [Fact]
    public void Completion_Captain_MatchingWeakLeaderWithStrongCaptain_SevenToEightyPercent_MismatchUnderThirty()
    {
        var goodCaptainPower = 80.0 * 80.0 / 100.0;   // 統率傾向80×精神力80/100
        var weakCaptainPower = 40.0 * 85.0 / 100.0;   // 統率傾向40×精神力85/100
        var match = P(EventStance.Captain, Axes(lead: 15), cap: goodCaptainPower);
        var mismatch = P(EventStance.Captain, Axes(lead: 85), cap: weakCaptainPower);
        Assert.InRange(match, 0.70, 0.80);
        Assert.InRange(mismatch, 0.20, 0.30);
    }

    /// <summary>平均的な部員（性格4軸・主将の統率力とも中立）でも、どれか1つは期待値0以上の対応がある（B.の完了条件）。</summary>
    [Fact]
    public void Completion_AverageMember_HasAtLeastOneNonNegativeExpectedValueChoice()
    {
        var neutralCaptainPower = C.CaptainPowerNeutral;
        double Ev(EventStance s)
        {
            var p = P(s, PersonalityAxes.Neutral, cap: neutralCaptainPower);
            var up = EventChoiceResolver.CohesionDelta(s, true, neutralCaptainPower, C);
            var down = EventChoiceResolver.CohesionDelta(s, false, neutralCaptainPower, C);
            return p * up + (1 - p) * down;
        }

        var evScold = Ev(EventStance.Scold);
        var evListen = Ev(EventStance.Listen);
        var evCaptain = Ev(EventStance.Captain);
        Assert.True(evScold >= 0 || evListen >= 0 || evCaptain >= 0,
            $"どの対応も期待値マイナス: scold={evScold} listen={evListen} captain={evCaptain}");
        // 「話を聞く」が安全策として常にプラス（叱る・任せるは平均的な部員には割に合わない博打のまま）。
        Assert.True(evListen >= 0, $"話を聞くの期待値がマイナス: {evListen}");
        Assert.True(evScold < 0, $"叱るの期待値がプラスだと『大きく振れる博打』にならない: {evScold}");
    }

    /// <summary>性格の匂わせ（issue #1599）: 当事者の性格だけで、いちばん好転しやすい対応を正しく選ぶ。</summary>
    [Fact]
    public void BestMatchingStance_PicksTheHighestProbabilityStance()
    {
        Assert.Equal(EventStance.Scold, EventChoiceResolver.BestMatchingStance(Axes(recept: 85), C));
        Assert.Equal(EventStance.Listen, EventChoiceResolver.BestMatchingStance(Axes(recept: 15, selfless: 15), C));
        Assert.Equal(EventStance.Captain, EventChoiceResolver.BestMatchingStance(Axes(lead: 1), C));
        // 性格が完全に中立の当事者には「話を聞く」が最善（listen_base がいちばん高い）。
        Assert.Equal(EventStance.Listen, EventChoiceResolver.BestMatchingStance(PersonalityAxes.Neutral, C));
    }

    // ===== 抽選（決定論・主RNGを進めない） =====

    [Fact]
    public void Roll_IsStable_AndDoesNotAdvanceTheParentStream()
    {
        var e = RepositoryEvents().First(x => x.Id == "skipping_practice");
        var a = new Xoshiro256Random(77);
        var b = new Xoshiro256Random(77);
        var r1 = EventChoiceResolver.Roll(e, 12, 5, a);
        Assert.Equal(r1, EventChoiceResolver.Roll(e, 12, 5, a), 15);
        for (var i = 0; i < 8; i++) Assert.Equal(b.NextDouble(), a.NextDouble(), 15);

        // 会話の抽選と同じ (イベント, 週, 人) でも別の列（同じ値に縛られない）。
        var rolls = Enumerable.Range(1, 40).Select(w => EventChoiceResolver.Roll(e, w, 5, new Xoshiro256Random(3))).ToList();
        Assert.True(rolls.Distinct().Count() == rolls.Count);
        Assert.All(rolls, r => Assert.InRange(r, 0.0, 1.0));
    }

    /// <summary>引いた乱数が確率どおりに好転を出すこと（多くの週・人で実測が式の確率に寄る）。</summary>
    [Fact]
    public void Outcome_EmpiricalImproveRate_MatchesTheProbability()
    {
        var e = RepositoryEvents().First(x => x.Id == "selfish_clash");
        var rng = new Xoshiro256Random(945);
        foreach (var (stance, axes) in new[]
                 {
                     (EventStance.Scold, Axes(recept: 85)), (EventStance.Scold, Axes(recept: 15)),
                     (EventStance.Listen, Axes(recept: 15, selfless: 20)), (EventStance.Captain, Axes()),
                 })
        {
            var p = P(stance, axes);
            var n = 0; var improved = 0;
            for (var w = 0; w < 100; w++)
            for (var salt = 1; salt <= 40; salt++)
            {
                n++;
                if (EventChoiceResolver.Improved(EventChoiceResolver.Roll(e, w, salt, rng), p)) improved++;
            }
            Assert.InRange(improved / (double)n, p - 0.03, p + 0.03);
        }
    }

    [Fact]
    public void PickOutcome_ReturnsTheMatchingScript()
    {
        var e = RepositoryEvents().First(x => x.Id == "grade_friction");
        var rng = new Xoshiro256Random(1);
        foreach (var s in CohesionStances)
        foreach (var improved in new[] { true, false })
        {
            var script = EventChoiceResolver.PickOutcome(e, s, improved, 3, 7, rng);
            Assert.NotNull(script);
            Assert.Contains(e.Outcomes, o => o.Stance == s && o.Improved == improved && ReferenceEquals(o.Script, script));
        }
        var plain = RepositoryEvents().First(x => x.Id == "rain_week");
        Assert.Null(EventChoiceResolver.PickOutcome(plain, EventStance.Scold, true, 3, 7, rng));
    }

    // ===== 学業配慮・信頼度（issue #1270 E1） =====

    [Fact]
    public void Parse_OutcomeEffects_AndFixedImproveProb_StayOnTheirElement()
    {
        const string yaml = @"
events:
  - id: exam
    kind: Random
    text: ""テスト。""
    effects:
      fame: 1
    choices:
      - stance: ease
        label: ""軽くする""
        improve_prob: 1.0
      - stance: as_usual
        label: ""いつも通り""
        improve_prob: 0.6
    outcomes:
      - stance: ease
        result: improved
        effects:
          trust: 2
          training_mult: 0.85
          training_weeks: 2
        lines:
          - who: player
            say: ""勉強します。""
      - stance: as_usual
        result: worsened
        effects:
          trust: -1
        lines:
          - who: player
            say: ""眠い。""
";
        var e = EventTableParser.Parse(yaml).Single();
        Assert.Equal(1.0, e.Effect.Fame);
        Assert.Equal(0.0, e.Effect.Trust);   // 結果の効果はイベントの効果へ漏れない
        Assert.False(e.Effect.HasTrainingBoost);
        Assert.Equal(1.0, e.Choices[0].ImproveProb);
        Assert.Equal(0.6, e.Choices[1].ImproveProb);
        var ease = e.Outcomes[0].EffectOrNone;
        Assert.Equal(2, ease.Trust);
        Assert.True(ease.HasTrainingBoost);
        Assert.Equal(0.85, ease.TrainingMult);
        Assert.Equal(-1, e.Outcomes[1].EffectOrNone.Trust);
        Assert.False(e.Outcomes[1].EffectOrNone.HasTrainingBoost);
    }

    [Fact]
    public void FixedImproveProb_IgnoresPersonality()
    {
        var fixedChoice = new EventChoice(EventStance.AsUsual, "いつも通り", 0.6);
        Assert.Equal(0.6, EventChoiceResolver.ImproveProbability(fixedChoice, Axes(dilig: 1), null, C), 9);
        Assert.Equal(0.6, EventChoiceResolver.ImproveProbability(fixedChoice, Axes(dilig: 99), 99, C), 9);
        var scold = new EventChoice(EventStance.Scold, "叱る");
        Assert.Equal(EventChoiceResolver.ImproveProbability(EventStance.Scold, Axes(dilig: 70), null, C),
            EventChoiceResolver.ImproveProbability(scold, Axes(dilig: 70), null, C), 9);
    }

    [Fact]
    public void OnlyInterpersonalStances_MoveCohesion()
    {
        Assert.All(CohesionStances, s => Assert.True(EventStances.MovesCohesion(s)));
        Assert.False(EventStances.MovesCohesion(EventStance.EasePractice));
        Assert.False(EventStances.MovesCohesion(EventStance.AsUsual));
    }

    /// <summary>資料の表（out/economy-proposal-1299.md §4.2・2026-11-23 オーナー決定 E1）どおりの信頼度。</summary>
    [Fact]
    public void RepositoryTable_TrustEffects_MatchTheDecision()
    {
        var byId = RepositoryEvents().ToDictionary(e => e.Id);
        foreach (var id in BranchedIds)
            foreach (var o in byId[id].Outcomes)
                Assert.Equal(o.Improved ? 2 : -2, o.EffectOrNone.Trust);

        var exam = byId["exam_week_slump"];
        Assert.Equal(EventKind.Random, exam.Kind);   // kind を変えない＝週次抽選の乱数列は変わらない
        Assert.True(exam.Effect.IsEmpty);            // 効果は選んだ結果の側へ移した
        Assert.Equal(2, exam.Outcomes.Single(o => o.Stance == EventStance.EasePractice).EffectOrNone.Trust);
        Assert.Equal(-1, exam.Outcomes.Single(o => o.Stance == EventStance.AsUsual).EffectOrNone.Trust);
    }

    /// <summary>マスコミ取材（issue #1467 P2）: 名声 +1・次の公式戦の自校側プレッシャー +1。kind は Random のまま。</summary>
    [Fact]
    public void RepositoryTable_MediaCoverage_RaisesFameAndNextMatchPressure()
    {
        var media = RepositoryEvents().Single(e => e.Id == "media_coverage");
        Assert.Equal(EventKind.Random, media.Kind);
        Assert.False(media.HasChoices);
        Assert.Equal(1.0, media.Effect.Fame);
        Assert.Equal(1, media.Effect.PressureNextMatch);
        Assert.Equal(0.0, media.Effect.Trust);
        Assert.False(media.Effect.IsEmpty);
    }

    // ===== 当事者の選び方 =====

    private static DevelopingPlayer Dp(int id, int grade = 2, int selfless = 50, bool captain = false)
        => new() { Id = id, Name = "p" + id, Grade = grade, Selflessness = selfless, Receptiveness = 40, IsCaptain = captain };

    [Fact]
    public void Cast_PicksSelfishSubjectAndSelflessPartner_NeverTheCaptain()
    {
        var roster = new List<DevelopingPlayer>
        {
            Dp(1, selfless: 30, captain: true), Dp(2, selfless: 30), Dp(3, selfless: 70), Dp(4, selfless: 20), Dp(5, selfless: 80),
        };
        var cast = new EventCast(EventCastRule.Selfish, EventCastRule.Selfless);
        for (var i = 0; i < 10; i++)
        {
            Assert.True(cast.TryPick(roster, i, captainId: 1, out var s, out var o));
            Assert.True(s!.Selflessness < 50 && s.Id != 1);
            Assert.True(o!.Selflessness >= 50 && o.Id != s.Id);
        }

        var grade = new EventCast(EventCastRule.UpperGrade, EventCastRule.FirstYear);
        Assert.False(grade.TryPick(roster, 0, 1, out _, out _), "1年生が居ないのに立てられた");
        roster.Add(Dp(6, grade: 1));
        Assert.True(grade.TryPick(roster, 0, 1, out var up, out var rookie));
        Assert.True(up!.Grade >= 2);
        Assert.Equal(6, rookie!.Id);
    }

    [Fact]
    public void Scheduler_CastFilter_KeepsUncastableEventsOutOfThePool()
    {
        var events = EventTableParser.Parse(Sample);
        var withFilter = new EventScheduler(events, new EventCoefficients { WeeklyFireProbability = 1.0 })
        {
            CastFilter = _ => false,
        };
        var rng = new Xoshiro256Random(5);
        for (var w = 0; w < 30; w++)
            Assert.All(withFilter.Week(w, rng), f => Assert.NotEqual("clash", f.Event.Id));

        // フィルタ無し（ヘッドレスシム）は従来どおり載る。
        var plain = new EventScheduler(events, new EventCoefficients { WeeklyFireProbability = 1.0 });
        var fired = Enumerable.Range(0, 30).SelectMany(w => plain.Week(w, rng)).Select(f => f.Event.Id);
        Assert.Contains("clash", fired);
    }

    private static int Occurrences(string s, string needle)
    {
        var n = 0;
        for (var i = s.IndexOf(needle, StringComparison.Ordinal); i >= 0; i = s.IndexOf(needle, i + needle.Length, StringComparison.Ordinal)) n++;
        return n;
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
