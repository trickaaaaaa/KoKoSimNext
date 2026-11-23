using System;
using System.Collections.Generic;
using KokoSim.Engine.Core;
using KokoSim.Engine.Match.Game;

namespace KokoSim.Engine.Season.Events;

/// <summary>
/// 選手間イベントへの監督の対応（issue #945・監督決定 2026-11-14）。全イベント共通の3つで、
/// 言い回しはイベントごとに data/events.yaml の <c>choices:</c> の <c>label:</c> に書く。
/// </summary>
public enum EventStance
{
    /// <summary>厳しく叱る。素直な当事者に効き、頑固な当事者には逆効果。</summary>
    Scold,
    /// <summary>話を聞く。頑固・目立ちたがりの当事者に効く。</summary>
    Listen,
    /// <summary>主将に任せる。主将の統率力で決まり、当事者が「引っ張る」側だと効きにくい。</summary>
    Captain,

    // ── 学業配慮（issue #1270 E1・2026-11-23）。性格で振れない＝好転の確率は選択肢の improve_prob ──
    /// <summary>練習を軽くする（テスト週の配慮）。</summary>
    EasePractice,
    /// <summary>いつも通りやる（同）。</summary>
    AsUsual,
}

/// <summary>対応の性質。</summary>
public static class EventStances
{
    /// <summary>
    /// 結束を動かす対応か（選手間のもめ事への3つの対応だけ。設計書23 §3.3）。学業配慮は
    /// 部員どうしの関係の話ではないので結束は動かさず、信頼度だけを動かす（issue #1270）。
    /// </summary>
    public static bool MovesCohesion(EventStance s) => s is EventStance.Scold or EventStance.Listen or EventStance.Captain;
}

/// <summary>
/// 選択肢1つ（対応と、画面に出す言い回し）。<paramref name="ImproveProb"/> があれば好転の確率は性格によらずこの値
/// （学業配慮など。1.0＝必ず「好転」側の結果）。null なら <see cref="EventChoiceResolver.ImproveProbability"/>。
/// </summary>
public sealed record EventChoice(EventStance Stance, string Label, double? ImproveProb = null);

/// <summary>
/// 結果の会話1つ（対応×好転/悪化）。会話の最終行の結果行はここに書かず、提示層が結束の動きと
/// <paramref name="Effect"/>（信頼度・名声・練習効率など・issue #1270）から足す（既存の結果行と同じ作法）。
/// </summary>
public sealed record EventOutcome(EventStance Stance, bool Improved, EventScript Script, EventEffect? Effect = null)
{
    /// <summary>この結果で起きる効果（無ければ <see cref="EventEffect.None"/>）。</summary>
    public EventEffect EffectOrNone => Effect ?? EventEffect.None;
}

/// <summary>
/// 当事者（<c>{player}</c>）と相手（<c>{other}</c>）の選び方（issue #945）。
/// 「目立ちたがりと献身派の衝突」は性格の④試合での姿勢で、「学年間のもめ事」は学年で選ぶ。
/// </summary>
public enum EventCastRule
{
    /// <summary>指定なし（在籍部員の誰でも）。</summary>
    Any,
    /// <summary>目立ちたがり（④試合での姿勢が50未満）。</summary>
    Selfish,
    /// <summary>献身派（④が50以上）。</summary>
    Selfless,
    /// <summary>上級生（2・3年）。</summary>
    UpperGrade,
    /// <summary>1年生。</summary>
    FirstYear,
}

/// <summary>当事者と相手の選び方。<see cref="Other"/> が null なら相手は出ない。</summary>
public sealed record EventCast(EventCastRule Subject = EventCastRule.Any, EventCastRule? Other = null)
{
    public static readonly EventCast Default = new();

    /// <summary>
    /// 在籍部員から当事者と相手を決める（純関数・乱数不使用）。<paramref name="index"/> は暦から決まる番号で、
    /// 条件に合う部員の中で <c>index % 人数</c> 番目を選ぶ（同じ週なら同じ人）。主将（<paramref name="captainId"/>）は
    /// 当事者にも相手にもしない＝「主将に任せる」の相手が本人になる会話を作らない。
    /// 条件に合う部員が足りなければ false（このイベントはその週には起こせない）。
    /// </summary>
    public bool TryPick(IReadOnlyList<DevelopingPlayer> roster, int index, int captainId,
        out DevelopingPlayer? subject, out DevelopingPlayer? other)
    {
        subject = other = null;
        var subjects = Candidates(roster, Subject, captainId, excludeId: int.MinValue);
        if (subjects.Count == 0) return false;
        subject = subjects[Mod(index, subjects.Count)];
        if (Other is not { } rule) return true;
        var others = Candidates(roster, rule, captainId, excludeId: subject.Id);
        if (others.Count == 0) { subject = null; return false; }
        other = others[Mod(index, others.Count)];
        return true;
    }

    private static List<DevelopingPlayer> Candidates(IReadOnlyList<DevelopingPlayer> roster, EventCastRule rule,
        int captainId, int excludeId)
    {
        var list = new List<DevelopingPlayer>();
        foreach (var p in roster)
        {
            if (p.Id == captainId || p.Id == excludeId || p.IsCaptain) continue;
            if (Matches(p, rule)) list.Add(p);
        }
        return list;
    }

    public static bool Matches(DevelopingPlayer p, EventCastRule rule) => rule switch
    {
        EventCastRule.Selfish => !Players.Personalities.High(p.Selflessness),
        EventCastRule.Selfless => Players.Personalities.High(p.Selflessness),
        EventCastRule.UpperGrade => p.Grade >= 2,
        EventCastRule.FirstYear => p.Grade == 1,
        _ => true,
    };

    private static int Mod(int a, int n) => ((a % n) + n) % n;
}

/// <summary>当事者の性格4軸（1〜99・50が中立）。手組みの選手は全部50＝どの対応も基準どおり。</summary>
public readonly record struct PersonalityAxes(int Leadership, int Receptiveness, int Diligence, int Selflessness)
{
    public static readonly PersonalityAxes Neutral = new(50, 50, 50, 50);

    public static PersonalityAxes Of(DevelopingPlayer p)
        => new(p.Leadership, p.Receptiveness, p.Diligence, p.Selflessness);
}

/// <summary>
/// 選択肢つきイベントの好転/悪化の決め方（設計書23 §3.3・issue #945）。純関数＋注入乱数。
/// <code>
/// d軸 = (軸の値 − 50) / 50                         （当事者の性格。−0.98〜+0.98）
/// d主将 = clamp((主将の統率力 − 基準) / 幅, −1, 1)  （統率力＝統率傾向×精神力/100。主将不在は −1）
/// 共通      = 勤勉さの重み × d勤勉
/// 叱る      = 叱るの基準 + 叱る×素直さ × d素直 + 共通
/// 話を聞く  = 聞くの基準 + 聞く×素直さ × d素直 + 聞く×姿勢 × d姿勢 + 共通
/// 主将に任せる = 任せるの基準 + 任せる×主将 × d主将 + 任せる×当事者の統率 × d統率 + 共通
/// 好転の確率 = clamp(上の値, 下限, 上限)
/// </code>
/// 係数は data/coefficients.yaml の <c>cohesion:</c>（<see cref="CohesionCoefficients"/>）。
/// </summary>
public static class EventChoiceResolver
{
    private static double D(int axis) => (axis - 50) / 50.0;

    /// <summary>主将の統率力（design-09 §8: 統率傾向×精神力/100）。主将不在は null。</summary>
    public static double? CaptainPower(DevelopingPlayer? captain)
        => captain is null ? null : CaptainSelector.LeadershipPower(captain);

    /// <summary>
    /// 選択肢の好転の確率。<see cref="EventChoice.ImproveProb"/> があればそれ（0〜1で止める・性格に依らない）、
    /// 無ければ性格と主将の統率力から（<see cref="ImproveProbability(EventStance, PersonalityAxes, double?, CohesionCoefficients)"/>）。
    /// </summary>
    public static double ImproveProbability(EventChoice choice, PersonalityAxes subject, double? captainPower,
        CohesionCoefficients c)
        => choice.ImproveProb is { } fixedP
            ? MathUtil.Clamp(fixedP, 0.0, 1.0)
            : ImproveProbability(choice.Stance, subject, captainPower, c);

    /// <summary>好転の確率（0〜1）。</summary>
    public static double ImproveProbability(EventStance stance, PersonalityAxes subject, double? captainPower,
        CohesionCoefficients c)
    {
        var p = c.ChoiceDiligenceWeight * D(subject.Diligence);
        switch (stance)
        {
            case EventStance.Scold:
                p += c.ScoldBase + c.ScoldReceptivenessWeight * D(subject.Receptiveness);
                break;
            case EventStance.Listen:
                p += c.ListenBase + c.ListenReceptivenessWeight * D(subject.Receptiveness)
                     + c.ListenSelflessnessWeight * D(subject.Selflessness);
                break;
            case EventStance.Captain:
                var dCap = captainPower is { } cp
                    ? MathUtil.Clamp((cp - c.CaptainPowerNeutral) / c.ChoiceCaptainPowerScale, -1.0, 1.0)
                    : -1.0;
                p += c.CaptainBase + c.CaptainPowerSlope * dCap
                     + c.CaptainSubjectLeadershipWeight * D(subject.Leadership);
                break;
        }
        return MathUtil.Clamp(p, c.ChoiceProbMin, c.ChoiceProbMax);
    }

    /// <summary>
    /// 好転/悪化を引くための一様乱数（0〜1）。<b>親ストリームを進めない Fork</b> で引く＝イベント抽選の乱数列は
    /// 1語もずれない。対応を選ぶ前に（発火時に）確定させ、どの対応を選んでも同じ値と比べる＝選び直しで
    /// 結果を探れない・同じ週の同じ人なら常に同じ。
    /// </summary>
    public static double Roll(GameEvent e, int absoluteWeek, int salt, IRandomSource rng)
    {
        var stream = EventScheduler.StreamOf(e.Id, absoluteWeek, salt) ^ ChoiceStreamSalt;
        return rng.Fork(stream).NextDouble();
    }

    /// <summary>選択肢の抽選を会話バリエーションの抽選と分けるための塩。</summary>
    private const ulong ChoiceStreamSalt = 0xC0E5_1017_0945_0000UL;

    /// <summary>好転したか（<paramref name="roll"/> が確率未満なら好転）。</summary>
    public static bool Improved(double roll, double probability) => roll < probability;

    /// <summary>
    /// 結果の会話を選ぶ（対応×好転/悪化に合うもの。複数あれば Fork で1つ）。無ければ null。
    /// </summary>
    public static EventScript? PickOutcome(GameEvent e, EventStance stance, bool improved, int absoluteWeek, int salt,
        IRandomSource rng)
        => PickOutcomeEntry(e, stance, improved, absoluteWeek, salt, rng)?.Script;

    /// <summary><see cref="PickOutcome"/> の結果を効果つきで返す（issue #1270）。同じ抽選＝同じ会話を選ぶ。</summary>
    public static EventOutcome? PickOutcomeEntry(GameEvent e, EventStance stance, bool improved, int absoluteWeek,
        int salt, IRandomSource rng)
    {
        var matches = new List<EventOutcome>();
        foreach (var o in e.Outcomes)
            if (o.Stance == stance && o.Improved == improved) matches.Add(o);
        if (matches.Count == 0) return null;
        if (matches.Count == 1) return matches[0];
        var stream = EventScheduler.StreamOf(e.Id, absoluteWeek, salt) ^ OutcomeStreamSalt;
        return matches[rng.Fork(stream).NextInt(0, matches.Count)];
    }

    private const ulong OutcomeStreamSalt = 0x0C0E_5945_0000_0001UL;
}
