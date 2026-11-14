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
///     text: "選手が練習中に故障した。"     # 通知フィード1行の要約
///     variants:                            # 会話まるごとN通り（無ければ text の1行だけ）
///       - lines:
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
        string? id = null, kind = null, text = null, setsFlag = null, requiresNotFlag = null, region = null;
        int? calendarWeek = null;
        double weight = 1.0;
        int cooldown = 4;
        double effFunds = 0.0, effFacilityFund = 0.0, effTrainingMult = 1.0;
        int effTrainingWeeks = 0;
        bool effTrainingPlayer = false;
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
                    choices.Add(new EventChoice(elemStance.Value, elemLabel ?? ""));
                }
            }
            else if (kind == "outcomes")
            {
                if (open)
                {
                    if (elemStance is null || elemImproved is null)
                        throw new FormatException($"{id}: outcomes の要素に stance と result が要ります。");
                    outcomes.Add(new EventOutcome(elemStance.Value, elemImproved.Value,
                        new EventScript(currentLines ?? new List<EventLine>())));
                }
            }
            else if (currentLines is not null && currentLines.Count > 0)
            {
                variants.Add(new EventScript(currentLines));
            }
            currentLines = null;
            elemStance = null; elemImproved = null; elemLabel = null;
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
                Text = text ?? "",
                Variants = variants.ToArray(),
                Effect = new EventEffect(
                    effFunds, effFacilityFund, effTrainingMult, effTrainingWeeks, effTrainingPlayer),
                Choices = choices.ToArray(),
                Outcomes = outcomes.ToArray(),
                Cast = new EventCast(castSubject, castOther),
            });
            id = kind = text = setsFlag = requiresNotFlag = region = null;
            calendarWeek = null;
            weight = 1.0;
            cooldown = 4;
            effFunds = effFacilityFund = 0.0;
            effTrainingMult = 1.0; effTrainingWeeks = 0; effTrainingPlayer = false;
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

            switch (key)
            {
                case "id": id = value; break;
                case "kind": kind = value; break;
                case "calendar_week": calendarWeek = ParseInt(value); break;
                case "weight": weight = ParseDouble(value, 1.0); break;
                case "cooldown_weeks": cooldown = ParseInt(value) ?? 4; break;
                case "sets_annual_flag": setsFlag = value.Length > 0 ? value : null; break;
                case "requires_not_annual_flag": requiresNotFlag = value.Length > 0 ? value : null; break;
                case "requires_region": region = value.Length > 0 ? value : null; break;
                case "text": text = value; break;
                // 効果（issue #613）。`effects:` の下に書くが、キー名で拾うので入れ子の深さは問わない。
                case "funds": effFunds = ParseDouble(value, 0.0); break;
                case "facility_fund": effFacilityFund = ParseDouble(value, 0.0); break;
                case "training_mult": effTrainingMult = ParseDouble(value, 1.0); break;
                case "training_weeks": effTrainingWeeks = ParseInt(value) ?? 0; break;
                case "training_target": effTrainingPlayer = value == "player"; break;
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

    // 既定を Random にしているのは kind 省略の後方互換。**新しい種別を足したらここにも足す**＝
    // 足し忘れると System が Random に化けて週次抽選のプールへ入り、主役の居ない会話が出るうえ
    // 抽選の重み合計が変わって発火の乱数列までずれる（issue #610 の作業中に実際に踏んだ）。
    private static EventKind ParseKind(string? kind) => kind switch
    {
        "Fixed" => EventKind.Fixed,
        "Choice" => EventKind.Choice,
        "System" => EventKind.System,
        _ => EventKind.Random,
    };

    // 選択肢の語は未知なら例外＝綴りを間違えた選択肢が黙って消えたり別の対応に化けたりしない。
    private static EventStance ParseStance(string v) => v switch
    {
        "scold" => EventStance.Scold,
        "listen" => EventStance.Listen,
        "captain" => EventStance.Captain,
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

    private static int IndentOf(string line)
    {
        var n = 0;
        while (n < line.Length && line[n] == ' ') n++;
        return n;
    }

    /// <summary>行末コメントを落とす。引用符の中の # は残す（文言に「#」が出ても壊れないように）。</summary>
    private static string StripComment(string line)
    {
        var quoted = false;
        for (var i = 0; i < line.Length; i++)
        {
            if (line[i] == '"') quoted = !quoted;
            else if (line[i] == '#' && !quoted) return line.Substring(0, i);
        }
        return line;
    }

    private static string Unquote(string s)
    {
        if (s.Length >= 2 && s[0] == '"' && s[s.Length - 1] == '"') return s.Substring(1, s.Length - 2);
        if (s.Length >= 2 && s[0] == '\'' && s[s.Length - 1] == '\'') return s.Substring(1, s.Length - 2);
        return s;
    }

    private static int? ParseInt(string s)
        => int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v : (int?)null;

    private static double ParseDouble(string s, double fallback)
        => double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : fallback;
}
