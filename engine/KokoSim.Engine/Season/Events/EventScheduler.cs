using System;
using KokoSim.Engine.Core;

namespace KokoSim.Engine.Season.Events;

/// <summary>
/// 1週の発火イベント。<see cref="Script"/> は発火時に確定した会話（issue #609）＝
/// 提示層は選び直さない（何度開いても同じ文になる）。
/// </summary>
public sealed record FiredEvent(GameEvent Event, int Week, EventScript Script);

/// <summary>
/// イベント発火基盤（設計書03 §5.2 / 04 §3.2）。
/// 各週: 該当する固定イベントを発火 ＋ 重み付き抽選でランダム/選択イベントを発火（上限は係数）。
/// 同一イベントのクールダウン・年次フラグを管理する。効果適用は Phase 5 で接続。
/// </summary>
public sealed class EventScheduler
{
    private readonly IReadOnlyList<GameEvent> _events;
    private readonly EventCoefficients _coeff;
    private readonly Dictionary<string, int> _lastFiredWeek = new();
    private readonly HashSet<string> _annualFlags = new();
    private int _absoluteWeek;

    public EventScheduler(IReadOnlyList<GameEvent> events, EventCoefficients? coeff = null)
    {
        _events = events;
        _coeff = coeff ?? new EventCoefficients();
    }

    /// <summary>
    /// 自校の地方（issue #616）。地方指定つきイベントはここと一致したときだけ発火する。
    /// <b>null（既定）のときは地方指定つきイベントを一切出さない</b>＝地方の概念を持たない
    /// ヘッドレスシムに、その土地でしか起きない話が混ざらない。
    /// </summary>
    public Nation.JapanRegion? Region { get; set; }

    /// <summary>年の切り替えで年次フラグをリセット。</summary>
    public void ResetAnnualFlags() => _annualFlags.Clear();

    /// <summary>
    /// 効き先⑤（issue #1601）: <see cref="EventKind.Choice"/> イベント（選択肢つき選手間イベント）の
    /// 抽選重みに掛ける倍率。既定1.0＝従来と1ビット一致（不変条件#2）。<see cref="EventKind.Random"/> の
    /// 重みには掛けない（悪いイベントが増えるのは「選手間の揉め事」だけ・design-23 §3.4）。
    /// 呼び出し側（Unity の <c>EventService.RunWeek</c>／ヘッドレスシムの <see cref="Season.SeasonEngine"/>）が
    /// 週ごとに <see cref="Match.Game.Cohesion.EventWeightMultiplier"/> で求めた値を据える（自校だけ）。
    /// </summary>
    public double ChoiceWeightMultiplier { get; set; } = 1.0;

    // ── セーブ往復（issue #609）。載せないとロードでクールダウンが全部明け、
    //    直前に見たイベントが即もう一度出る。 ──

    /// <summary>通算経過週（クールダウンの基準）。</summary>
    public int AbsoluteWeek => _absoluteWeek;

    /// <summary>イベントIDごとの最終発火週（通算週）。</summary>
    public IReadOnlyDictionary<string, int> LastFiredWeeks => _lastFiredWeek;

    /// <summary>立っている年次フラグ。</summary>
    public IReadOnlyCollection<string> AnnualFlags => _annualFlags;

    /// <summary>セーブ復元専用。状態をまるごと差し替える。</summary>
    public void Restore(int absoluteWeek, IReadOnlyDictionary<string, int> lastFiredWeeks,
        IReadOnlyCollection<string> annualFlags)
    {
        _absoluteWeek = absoluteWeek;
        _lastFiredWeek.Clear();
        foreach (var kv in lastFiredWeeks) _lastFiredWeek[kv.Key] = kv.Value;
        _annualFlags.Clear();
        foreach (var f in annualFlags) _annualFlags.Add(f);
    }

    /// <summary>
    /// IDでイベント定義を引く（主役つき提示の入口・issue #610）。見つからなければ null＝
    /// 呼び出し側は<b>提示だけ諦めて処理は続ける</b>（会話が無くても出来事そのものは起きている）。
    /// </summary>
    public GameEvent? Find(string id)
    {
        foreach (var e in _events) if (e.Id == id) return e;
        return null;
    }

    public IReadOnlyList<FiredEvent> Week(int week, IRandomSource rng)
    {
        _absoluteWeek++;
        var fired = new List<FiredEvent>();

        // 固定イベント（カレンダー連動）。抽選を通さず必ず出る。
        foreach (var e in _events)
        {
            if (e.Kind == EventKind.Fixed && e.CalendarWeek == week && CanFire(e))
            {
                Fire(e, week, fired, rng);
            }
        }

        // 重み付き抽選（同一週の上限は係数, 設計書04 §3.2）。
        for (var picked = 0; picked < _coeff.MaxRandomPerWeek; picked++)
        {
            var pool = new List<GameEvent>();
            var totalWeight = 0.0;
            foreach (var e in _events)
            {
                // 抽選に載るのは Random と Choice だけ。Fixed は暦で出し、System は
                // 別経路（怪我・成長など実際に起きた出来事）から主役つきで出す（issue #610）。
                if (e.Kind != EventKind.Random && e.Kind != EventKind.Choice) continue;
                if (!CanFire(e)) continue;
                pool.Add(e);
                totalWeight += WeightOf(e);
            }
            if (pool.Count == 0 || totalWeight <= 0) break;

            // 週あたりの発火確率を抑える（毎週何か起きると煩雑）。
            if (rng.NextDouble() >= _coeff.WeeklyFireProbability) break;

            var roll = rng.NextDouble() * totalWeight;
            foreach (var e in pool)
            {
                roll -= WeightOf(e);
                if (roll <= 0)
                {
                    Fire(e, week, fired, rng);
                    break;
                }
            }
        }

        return fired;
    }

    /// <summary>
    /// 当事者を立てられるか（issue #945）。選択肢つきの選手間イベントは「目立ちたがり」「1年生」のように
    /// 当事者の条件を持つので、条件に合う部員が居ない週は抽選に載せない。<b>null（既定）は判定しない</b>＝
    /// ロスターを渡さないヘッドレスシムでは従来どおり全部が抽選に載る（帯・ゴールデンの抽選プールは不変）。
    /// </summary>
    public Func<GameEvent, bool>? CastFilter { get; set; }

    /// <summary>
    /// この年度末に卒業する3年生が1人以上いるか（issue #1609）。新設校の1〜2年目のように3年生が居ない年は
    /// 卒業式を出さない。<b>null（既定）は判定しない</b>＝名簿を渡さないヘッドレスシムは従来どおり毎年出る。
    /// 固定イベントは抽選に載らず会話の選び方も Fork なので、出さない年があっても他の抽選の乱数列は動かない。
    /// </summary>
    public Func<bool>? GraduatingClassFilter { get; set; }

    /// <summary>
    /// この学校に OB（野球部の卒業生）が居るか（issue #1615）。新設校の最初の2年のように卒業生が1人も居ない間は、
    /// OB 会の寄付のような OB が出てくるイベントを抽選に載せず、OB が出てくる会話も選ばない。
    /// <b>null（既定）は判定しない</b>＝名簿を渡さないヘッドレスシムは従来どおり全部が抽選に載る（帯・ゴールデン不変）。
    /// </summary>
    public Func<bool>? ObFilter { get; set; }

    /// <summary>
    /// 年度末に転任の抽選がある教員監督なら、そのときの信頼度の段（issue #1606）。抽選が無い年・フリー監督は null。
    /// 校長の会話は<b>この関数が null でない年だけ</b>出し、段に合う会話だけを選ぶ。
    /// <b>未設定（null）のときは出さない</b>＝監督を持たないヘッドレスシムには出ない（帯・ゴールデン不変）。
    /// </summary>
    public Func<Career.TransferHintTier?>? TransferHintFilter { get; set; }

    /// <summary>抽選の重み。<see cref="EventKind.Choice"/> だけ <see cref="ChoiceWeightMultiplier"/> を掛ける（効き先⑤）。</summary>
    private double WeightOf(GameEvent e) => e.Kind == EventKind.Choice ? e.Weight * ChoiceWeightMultiplier : e.Weight;

    private bool CanFire(GameEvent e)
    {
        if (e.RequiresTransferDraw && TransferHintFilter?.Invoke() == null) return false;
        if (e.RequiresOb && ObFilter is { } ob && !ob()) return false;
        if (e.RequiresGraduatingClass && GraduatingClassFilter is { } graduating && !graduating()) return false;
        if (CastFilter is { } cast && e.Cast != EventCast.Default && !cast(e)) return false;
        if (e.RequiresRegion is { } need && Region != need) return false;
        if (e.RequiresNotAnnualFlag is { } f && _annualFlags.Contains(f)) return false;
        if (_lastFiredWeek.TryGetValue(e.Id, out var last) && _absoluteWeek - last < e.CooldownWeeks) return false;
        return true;
    }

    private void Fire(GameEvent e, int week, List<FiredEvent> fired, IRandomSource rng)
    {
        _lastFiredWeek[e.Id] = _absoluteWeek;
        if (e.SetsAnnualFlag is { } flag) _annualFlags.Add(flag);
        fired.Add(new FiredEvent(e, week, PickScript(e, _absoluteWeek, 0, rng, ObFilter?.Invoke() ?? true, TransferHintFilter?.Invoke())));
    }

    /// <summary>
    /// 会話バリエーションを1つ選ぶ（issue #609）。<b>親ストリームを進めない Fork</b> で引くので、
    /// バリエーションを増やしても既存の発火抽選の乱数列は 1 語もずれない（＝この機能を足しても
    /// 「いつ何が起きるか」は変わらない）。同じ (イベントID, 通算週) なら常に同じ会話。
    /// </summary>
    public static EventScript PickScript(GameEvent e, int absoluteWeek, IRandomSource rng)
        => PickScript(e, absoluteWeek, 0, rng);

    /// <summary>
    /// 主役つきイベント用（issue #610）。<paramref name="salt"/> に主役の選手IDを渡すと、
    /// <b>同じ週に2人が同時に故障しても別の会話</b>になる（同じ人・同じ週なら常に同じ会話）。
    /// </summary>
    /// <param name="hasOb">
    /// OB が居る学校か（issue #1615）。false なら <see cref="EventScript.RequiresOb"/> の会話を候補から外して引く。
    /// true なら全会話が候補＝従来と同じ引き方（同じ Fork・同じ個数）。
    /// </param>
    /// <param name="transferHint">
    /// 転任の匂わせの段（issue #1606）。指定があれば、その段の会話（と段の指定が無い会話）だけから引く。
    /// null（既定）は絞らない＝従来と同じ引き方。
    /// </param>
    public static EventScript PickScript(
        GameEvent e, int absoluteWeek, int salt, IRandomSource rng, bool hasOb = true,
        Career.TransferHintTier? transferHint = null)
    {
        if (e.Variants.Count == 0) return EventScript.Single(EventRoles.Narration, e.Text);
        var candidates = e.Variants;
        if (transferHint is { } tier)
        {
            var open = new List<EventScript>(candidates.Count);
            foreach (var v in candidates) if (v.TransferHint is null || v.TransferHint == tier) open.Add(v);
            if (open.Count > 0) candidates = open;
        }
        if (!hasOb)
        {
            var open = new List<EventScript>(candidates.Count);
            foreach (var v in candidates) if (!v.RequiresOb) open.Add(v);
            if (open.Count > 0) candidates = open;   // 全部が OB 前提のイベントは出せる会話が無い＝全会話から引く（データの欠陥はテストが見張る）
        }
        if (candidates.Count == 1) return candidates[0];
        return candidates[rng.Fork(StreamOf(e.Id, absoluteWeek, salt)).NextInt(0, candidates.Count)];
    }

    /// <summary>
    /// (イベントID, 通算週, 塩) から Fork のストリーム識別子を作る（会話の抽選と、選択肢の結果の抽選
    /// <see cref="EventChoiceResolver"/> が共有する。後者はさらに固有の塩を混ぜて別の列にする）。
    /// </summary>
    public static ulong StreamOf(string eventId, int absoluteWeek, int salt)
        => Fnv1a(eventId)
           ^ ((ulong)absoluteWeek * 0x9E3779B97F4A7C15UL)
           ^ ((ulong)(uint)salt * 0xD1B54A32D192ED03UL);

    private static ulong Fnv1a(string s)
    {
        var hash = 0xcbf29ce484222325UL;
        foreach (var ch in s)
        {
            hash ^= ch;
            hash *= 0x100000001b3UL;
        }
        return hash;
    }
}
