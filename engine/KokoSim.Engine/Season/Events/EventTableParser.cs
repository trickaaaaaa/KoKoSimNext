using System;
using System.Collections.Generic;
using System.Globalization;

namespace KokoSim.Engine.Season.Events;

/// <summary>
/// data/events.yaml の純パーサ（issue #609）。Unity は YamlDotNet を持たないので、
/// <see cref="Nation.SchoolNameVocabParser"/> と同型に<b>エンジン側の1実装</b>へ寄せる
/// （Balance CLI／テスト／Unity が同じコードで読む＝文言と発火表が3箇所でずれない）。
///
/// <para>受け付ける形（これ以外は <see cref="FormatException"/>）:</para>
/// <code>
/// events:
///   - id: injury_generic
///     kind: Random            # Fixed / Random / Choice
///     calendar_week: 18       # Fixed のみ
///     weight: 2.0
///     cooldown_weeks: 6
///     sets_annual_flag: foo
///     requires_not_annual_flag: foo
///     requires_ob: true       # OB（野球部の卒業生）が居る学校でだけ出す（issue #1615）
///     text_no_ob: "..."       # OB が居ない学校のフィード1行（省略＝text と同じ）
///     scene: ground           # ノベル画面の背景（issue #1516）。省略＝ground
///     text: "選手が練習中に故障した。"     # 通知フィード1行の要約
///     variants:                            # 会話まるごとN通り（無ければ text の1行だけ）
///       - requires_ob: true                # この会話は OB が居る学校でだけ選ぶ（issue #1615）
///         lines:
///           - who: player
///             say: "監督、すみません……足が。"
///           - who: manager
///             name: "監督"                 # 省略時は役割の既定名（EventRoles.DefaultName）
///             say: "無理をさせすぎたか。"
///     # ── 選択肢つき（issue #945）。3つとも書き、結果は対応×好転/悪化の6通り ──
///     cast:
///       subject: selfish       # 当事者 {player}: any / selfish / selfless / upper_grade / first_year
///       other: selfless        # 相手 {other}（省略＝相手なし）
///     choices:
///       - stance: scold        # scold / listen / captain
///         label: "呼び出して叱る"
///     outcomes:
///       - stance: scold
///         result: improved     # improved / worsened
///         lines:
///           - who: player
///             say: "すみませんでした。"
/// </code>
/// </summary>
public static class EventTableParser
{
    public static IReadOnlyList<GameEvent> Parse(string yaml)
    {
        var events = new List<GameEvent>();
        if (string.IsNullOrWhiteSpace(yaml)) return events;

        // 収集中のイベント1件ぶんの素。ネストが3段（イベント→variants→lines）あるので、
        // 「いま何を読んでいるか」を状態で持つのではなく、インデントで所属を決める。
        string? id = null, kind = null, text = null, setsFlag = null, requiresNotFlag = null, region = null, scene = null;
        int? calendarWeek = null;
        var requiresGraduatingClass = false;
        var requiresOb = false;
        string? textNoOb = null;
        var elemRequiresOb = false;
        var requiresTransferDraw = false;
        Career.TransferHintTier? elemTransferHint = null;
        double weight = 1.0;
        int cooldown = 4;
        // 効果（issue #613・#1270）。イベント直下の effects: はイベントの効果、outcomes: の要素の中は結果ごとの効果。
        var eff = new EffectBuilder();
        var elemEff = new EffectBuilder();
        double? elemImproveProb = null;
        var variants = new List<EventScript>();
        var choices = new List<EventChoice>();
        var outcomes = new List<EventOutcome>();
        EventCastRule castSubject = EventCastRule.Any;
        EventCastRule? castOther = null;
        // いま読んでいるリスト（variants / outcomes / choices）と、その要素1つぶんの素。
        string listKind = "";
        EventStance? elemStance = null;
        bool? elemImproved = null;
        string? elemLabel = null;
        var elemOpen = false;
        List<EventLine>? currentLines = null;
        string? lineWho = null, lineName = null, lineSay = null;

        void FlushLine()
        {
            if (lineWho is null && lineSay is null) return;
            if (currentLines is null) throw new FormatException("lines: の外に会話行があります。");
            currentLines.Add(new EventLine(lineWho ?? EventRoles.Unknown, lineName ?? "", lineSay ?? ""));
            lineWho = lineName = lineSay = null;
        }

        void FlushVariant()
        {
            FlushLine();
            var kind = listKind;
            var open = elemOpen;
            elemOpen = false;
            if (kind == "choices")
            {
                if (open)
                {
                    if (elemStance is null) throw new FormatException($"{id}: choices の要素に stance がありません。");
                    choices.Add(new EventChoice(elemStance.Value, elemLabel ?? "", elemImproveProb));
                }
            }
            else if (kind == "outcomes")
            {
                if (open)
                {
                    if (elemStance is null || elemImproved is null)
                        throw new FormatException($"{id}: outcomes の要素に stance と result が要ります。");
                    var outcomeEffect = elemEff.Build();
                    outcomes.Add(new EventOutcome(elemStance.Value, elemImproved.Value,
                        new EventScript(currentLines ?? new List<EventLine>()),
                        outcomeEffect.IsEmpty ? null : outcomeEffect));
                }
            }
            else if (currentLines is not null && currentLines.Count > 0)
            {
                variants.Add(new EventScript(currentLines) { RequiresOb = elemRequiresOb, TransferHint = elemTransferHint });
            }
            elemRequiresOb = false;
            elemTransferHint = null;
            currentLines = null;
            elemStance = null; elemImproved = null; elemLabel = null; elemImproveProb = null;
            elemEff = new EffectBuilder();
        }

        void FlushEvent()
        {
            FlushVariant();
            if (id is null) return;
            events.Add(new GameEvent
            {
                Id = id,
                Kind = ParseKind(kind),
                CalendarWeek = calendarWeek,
                Weight = weight,
                CooldownWeeks = cooldown,
                SetsAnnualFlag = setsFlag,
                RequiresNotAnnualFlag = requiresNotFlag,
                RequiresRegion = ParseRegion(region),
                RequiresGraduatingClass = requiresGraduatingClass,
                RequiresOb = requiresOb,
                RequiresTransferDraw = requiresTransferDraw,
                TextWithoutOb = textNoOb,
                Scene = ParseScene(scene),
                Text = text ?? "",
                Variants = variants.ToArray(),
                Effect = eff.Build(),
                Choices = choices.ToArray(),
                Outcomes = outcomes.ToArray(),
                Cast = new EventCast(castSubject, castOther),
            });
            id = kind = text = setsFlag = requiresNotFlag = region = scene = null;
            calendarWeek = null;
            requiresGraduatingClass = false;
            requiresOb = false;
            requiresTransferDraw = false;
            textNoOb = null;
            weight = 1.0;
            cooldown = 4;
            eff = new EffectBuilder();
            variants = new List<EventScript>();
            choices = new List<EventChoice>();
            outcomes = new List<EventOutcome>();
            castSubject = EventCastRule.Any; castOther = null;
            listKind = "";
        }

        // 「- 」で始まる行がどの階層の要素かは<b>インデントで決める</b>。直前に読んだ状態で決めると、
        // 2つ目の会話（`- lines:`）が1つ目の会話の行として吸われる。
        var inEvents = false;
        var variantsKeyIndent = -1;
        var linesKeyIndent = -1;

        foreach (var raw in yaml.Replace("\r\n", "\n").Split('\n'))
        {
            var line = StripComment(raw);
            if (line.Trim().Length == 0) continue;

            var indent = IndentOf(line);
            var body = line.Trim();

            if (indent == 0)
            {
                FlushEvent();
                variantsKeyIndent = linesKeyIndent = -1;
                inEvents = body.StartsWith("events:", StringComparison.Ordinal);
                continue;
            }
            if (!inEvents) continue;

            var isItem = body.StartsWith("- ", StringComparison.Ordinal) || body == "-";
            if (isItem) body = body.Length > 1 ? body.Substring(2).Trim() : "";
            // "- key: value" のキーは 2 文字ぶん内側にあるものとして扱う（所属の判定に使う）。
            var keyIndent = isItem ? indent + 2 : indent;

            if (isItem)
            {
                if (indent <= 2)
                {
                    FlushEvent();                                   // events: 直下＝イベント1件
                    variantsKeyIndent = linesKeyIndent = -1;
                }
                else if (linesKeyIndent >= 0 && indent > linesKeyIndent)
                {
                    FlushLine();                                    // lines: 直下＝会話1行
                }
                else if (variantsKeyIndent >= 0 && indent > variantsKeyIndent)
                {
                    FlushVariant();                                 // variants/outcomes/choices 直下＝要素1つ
                    if (listKind != "choices") currentLines = new List<EventLine>();
                    elemOpen = true;
                    linesKeyIndent = -1;
                }
                if (body.Length == 0) continue;
            }

            var colon = body.IndexOf(':');
            if (colon <= 0) continue;
            var key = body.Substring(0, colon).Trim();
            var value = Unquote(body.Substring(colon + 1).Trim());

            // 効果のキーは outcomes: の要素の中なら結果ごと、それ以外はイベントの効果へ。
            var target = listKind == "outcomes" && elemOpen ? elemEff : eff;
            if (target.TrySet(key, value, id)) continue;

            switch (key)
            {
                case "id": id = value; break;
                case "kind": kind = value; break;
                case "calendar_week": calendarWeek = ParseInt(key, value); break;
                case "weight": weight = ParseDouble(key, value, 1.0); break;
                case "cooldown_weeks": cooldown = ParseInt(key, value) ?? 4; break;
                case "sets_annual_flag": setsFlag = value.Length > 0 ? value : null; break;
                case "requires_not_annual_flag": requiresNotFlag = value.Length > 0 ? value : null; break;
                case "requires_region": region = value.Length > 0 ? value : null; break;
                case "requires_graduating_class": requiresGraduatingClass = value == "true"; break;
                case "requires_ob":
                    if (listKind == "variants" && elemOpen) elemRequiresOb = value == "true";
                    else requiresOb = value == "true";
                    break;
                case "requires_transfer_draw": requiresTransferDraw = value == "true"; break;
                case "transfer_hint": elemTransferHint = ParseTransferHint(value, id); break;
                case "text_no_ob": textNoOb = value.Length > 0 ? value : null; break;
                case "scene": scene = value.Length > 0 ? value : null; break;
                case "text": text = value; break;
                case "improve_prob": elemImproveProb = ParseDouble(key, value, 0.5); break;
                case "variants":
                case "outcomes":
                case "choices":
                    FlushVariant(); listKind = key; variantsKeyIndent = keyIndent; linesKeyIndent = -1; break;
                // 選択肢（issue #945）。cast: の下の subject/other、choices/outcomes の要素の stance/label/result。
                case "subject": castSubject = ParseCast(value); break;
                case "other": castOther = ParseCast(value); break;
                case "stance": elemStance = ParseStance(value); break;
                case "label": elemLabel = value; break;
                case "result": elemImproved = ParseResult(value); break;
                case "lines": currentLines ??= new List<EventLine>(); linesKeyIndent = keyIndent; break;
                case "who": lineWho = value; break;
                case "name": lineName = value; break;
                case "say": lineSay = value; break;
            }
        }
        FlushEvent();
        return events;
    }

    /// <summary>
    /// 効果1つぶんの素（issue #613・#1270）。`effects:` の下に書くが、キー名で拾うので入れ子の深さは問わない。
    /// </summary>
    private sealed class EffectBuilder
    {
        private double _funds, _trainingMult = 1.0, _trust, _fame;
        private int _trainingWeeks, _pressure;
        private bool _trainingPlayer;

        public bool TrySet(string key, string value, string? id)
        {
            switch (key)
            {
                case "funds": _funds = ParseDouble(key, value, 0.0); return true;
                case "training_mult": _trainingMult = ParseDouble(key, value, 1.0); return true;
                case "training_weeks": _trainingWeeks = ParseInt(key, value) ?? 0; return true;
                case "training_target": _trainingPlayer = value == "player"; return true;
                case "trust": _trust = ParseDouble(key, value, 0.0); return true;
                case "fame": _fame = ParseDouble(key, value, 0.0); return true;
                case "pressure_next_match": _pressure = ParseInt(key, value) ?? 0; return true;
                default: return false;
            }
        }

        public EventEffect Build() => new(_funds, _trainingMult, _trainingWeeks, _trainingPlayer, _trust, _fame,
            _pressure);
    }

    // kind 省略は Random（後方互換）。**新しい種別を足したらここにも足す**＝足し忘れると例外で気づける。
    // 未知の綴りを Random に落とすと System が週次抽選のプールへ入り、主役の居ない会話が出るうえ
    // 抽選の重み合計が変わって発火の乱数列までずれる（issue #610 の作業中に実際に踏んだ）。
    private static EventKind ParseKind(string? kind) => kind switch
    {
        null or "" or "Random" => EventKind.Random,
        "Fixed" => EventKind.Fixed,
        "Choice" => EventKind.Choice,
        "System" => EventKind.System,
        _ => throw new FormatException($"kind に未知の語: {kind}"),
    };

    // 選択肢の語は未知なら例外＝綴りを間違えた選択肢が黙って消えたり別の対応に化けたりしない。
    private static EventStance ParseStance(string v) => v switch
    {
        "scold" => EventStance.Scold,
        "listen" => EventStance.Listen,
        "captain" => EventStance.Captain,
        "ease" => EventStance.EasePractice,
        "as_usual" => EventStance.AsUsual,
        _ => throw new FormatException($"stance に未知の語: {v}"),
    };

    private static bool ParseResult(string v) => v switch
    {
        "improved" => true,
        "worsened" => false,
        _ => throw new FormatException($"result に未知の語: {v}"),
    };

    private static EventCastRule ParseCast(string v) => v switch
    {
        "any" => EventCastRule.Any,
        "selfish" => EventCastRule.Selfish,
        "selfless" => EventCastRule.Selfless,
        "upper_grade" => EventCastRule.UpperGrade,
        "first_year" => EventCastRule.FirstYear,
        _ => throw new FormatException($"cast に未知の語: {v}"),
    };

    // 未知の地方名は「指定なし」に落とさず**例外**にする。落とすと、綴りを間違えた雪国イベントが
    // 全国どこでも出る（絵で気づけない形で壊れる）。
    private static Nation.JapanRegion? ParseRegion(string? name)
    {
        if (string.IsNullOrEmpty(name)) return null;
        if (Enum.TryParse<Nation.JapanRegion>(name, out var r)) return r;
        throw new FormatException($"requires_region に未知の地方名: {name}");
    }

    // 転任の匂わせの段（issue #1606）。未知の語は例外（綴り違いが黙って全段に出るのを防ぐ）。
    private static Career.TransferHintTier ParseTransferHint(string name, string? eventId) => name switch
    {
        "high" => Career.TransferHintTier.High,
        "mid" => Career.TransferHintTier.Mid,
        "low" => Career.TransferHintTier.Low,
        _ => throw new FormatException($"{eventId}: transfer_hint に未知の段: {name}"),
    };

    // 場面（issue #1516）。未知の語は既定へ落とさず**例外**にする（地方名と同じ理由＝綴り違いが黙って
    // グラウンドの背景になり、絵でしか気づけない）。
    private static EventScene ParseScene(string? name) => name switch
    {
        null or "" => EventScene.Ground,
        "ground" => EventScene.Ground,
        "bench" => EventScene.Bench,
        "school" => EventScene.School,
        "clubroom" => EventScene.Clubroom,
        "office" => EventScene.Office,
        "camp" => EventScene.Camp,
        "hall" => EventScene.Hall,
        "night" => EventScene.Night,
        _ => throw new FormatException($"scene に未知の場所: {name}"),
    };

    private static int IndentOf(string line)
    {
        var n = 0;
        while (n < line.Length && line[n] == ' ') n++;
        return n;
    }

    /// <summary>
    /// 行末コメントを落とす。引用符（二重・単一）の中の # と、直前が空白でない # は値の一部として残す
    /// （YAML と同じ＝文言や値に「#」が出ても壊れないように）。
    /// </summary>
    private static string StripComment(string line)
    {
        var quote = '\0';
        for (var i = 0; i < line.Length; i++)
        {
            var c = line[i];
            if (quote != '\0') { if (c == quote) quote = '\0'; }
            else if (c == '"' || c == '\'') quote = c;
            else if (c == '#' && (i == 0 || char.IsWhiteSpace(line[i - 1]))) return line.Substring(0, i);
        }
        return line;
    }

    private static string Unquote(string s)
    {
        if (s.Length >= 2 && s[0] == '"' && s[s.Length - 1] == '"') return s.Substring(1, s.Length - 2);
        if (s.Length >= 2 && s[0] == '\'' && s[s.Length - 1] == '\'') return s.Substring(1, s.Length - 2);
        return s;
    }

    // 値が空（または null/~）なら「書かなかった」＝既定。書いてあるのに読めないものは既定へ落とさず例外にする
    // （`weight: 2,0` が黙って 1.0 になる、のような化け方を読み込み時に殺す）。
    private static bool IsAbsent(string s) => s.Length == 0 || s == "null" || s == "~";

    private static int? ParseInt(string key, string s)
    {
        if (IsAbsent(s)) return null;
        if (int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var v)) return v;
        throw new FormatException($"{key} は整数で書いてください: {s}");
    }

    private static double ParseDouble(string key, string s, double fallback)
    {
        if (IsAbsent(s)) return fallback;
        if (double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var v)
            && !double.IsNaN(v) && !double.IsInfinity(v)) return v;
        throw new FormatException($"{key} は数値で書いてください: {s}");
    }
}
