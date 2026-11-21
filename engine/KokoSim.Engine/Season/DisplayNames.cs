using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;

namespace KokoSim.Engine.Season;

/// <summary>
/// 画面に出す実在の大会名の差し替え口（issue #1319・docs/release/steam-roadmap.md 段階2）。
///
/// <para>甲子園・センバツ・明治神宮大会などの実名を、商標の調べ次第で架空名へ一斉に替えられるよう、
/// 画面に出る大会名はすべてここから引く。<b>正典は <c>data/display-names.yaml</c></b>。
/// 実ゲーム（Unity）は起動時にその表を読んで <see cref="Use"/> する＝表を書き換えるだけで名前が替わる。
/// 既定値（表が読めないとき・engine 単体のテストや Balance CLI）は今の実名で、
/// <c>DisplayNamesTests</c> が表と既定値の一致を固定する（片方だけ替えると赤になる）。</para>
///
/// <para>対象は<b>表示の文字だけ</b>。型名・識別子（<c>TournamentKind.SummerKoshien</c> 等）・コメント・
/// ログは替えない。球場名は <c>data/stadiums.yaml</c> が単一ソース（甲子園・神宮の C# の写しは
/// <c>Stadium</c> 側の一致テストが見張る）。</para>
///
/// <para>イベント文（data/events.yaml）は差し込み語 <c>{koshien}</c> などで書き、<see cref="EventTextFormat"/> が
/// ここの値で展開する。「甲子園の土」のような場所としての使い方も <c>{koshien}</c> で書くので、
/// 架空名にするときは場所としても読める呼び名（例: 聖地）にする。</para>
/// </summary>
public sealed class DisplayNames
{
    /// <summary>夏の全国大会の通称（「甲子園」）。場所としても使う。</summary>
    public string Koshien { get; init; } = "甲子園";

    /// <summary>春の全国大会の通称（「センバツ」）。</summary>
    public string Senbatsu { get; init; } = "センバツ";

    /// <summary>秋の全国大会の呼び名（「明治神宮大会」）。</summary>
    public string Jingu { get; init; } = "明治神宮大会";

    /// <summary>秋の全国大会の短い呼び名（「神宮大会」）。日付の小書きなど狭い所で使う。</summary>
    public string JinguShort { get; init; } = "神宮大会";

    /// <summary>夏の全国大会の正式名（「全国高等学校野球選手権大会」）。年号を前に付けて大会の見出しにする。</summary>
    public string SummerOfficial { get; init; } = "全国高等学校野球選手権大会";

    /// <summary>春の全国大会の正式名（「選抜高等学校野球大会」）。</summary>
    public string SenbatsuOfficial { get; init; } = "選抜高等学校野球大会";

    /// <summary>秋の全国大会の正式名（「明治神宮野球大会（高校の部）」）。</summary>
    public string JinguOfficial { get; init; } = "明治神宮野球大会（高校の部）";

    /// <summary>夏の県大会の冠（「選手権」＝「選手権神奈川大会」）。</summary>
    public string PrefSummerPrefix { get; init; } = "選手権";

    /// <summary>いま使っている表。既定は実名。Unity は起動時に data/display-names.yaml を読んで差し替える。</summary>
    public static DisplayNames Current { get; private set; } = new DisplayNames();

    /// <summary>表を差し替える（起動時に1回。null なら既定＝実名に戻す）。</summary>
    public static void Use(DisplayNames? names) => Current = names ?? new DisplayNames();

    /// <summary>
    /// 表のキー（YAML のキー＝イベント文の差し込み語）と値の対応。キーを足したらここと YAML の両方に足す
    /// （<c>DisplayNamesTests</c> が両方の一致を見張る）。
    /// </summary>
    public static readonly IReadOnlyList<(string Key, Func<DisplayNames, string> Get)> Keys = new (string, Func<DisplayNames, string>)[]
    {
        ("koshien", n => n.Koshien),
        ("senbatsu", n => n.Senbatsu),
        ("jingu", n => n.Jingu),
        ("jingu_short", n => n.JinguShort),
        ("summer_official", n => n.SummerOfficial),
        ("senbatsu_official", n => n.SenbatsuOfficial),
        ("jingu_official", n => n.JinguOfficial),
        ("pref_summer_prefix", n => n.PrefSummerPrefix),
    };

    /// <summary>キーで値を引く（イベント文の差し込み語の展開用）。</summary>
    public bool TryGet(string key, [NotNullWhen(true)] out string? value)
    {
        foreach (var (k, get) in Keys)
        {
            if (k != key) continue;
            value = get(this);
            return true;
        }
        value = null;
        return false;
    }
}

/// <summary>
/// data/display-names.yaml の読み込み。書式は「キー: 値」の平らな並びだけ（# はコメント、値の前後の引用符は外す）。
/// 書いていないキーは既定（実名）のまま。<b>知らないキーは例外</b>＝綴り違いで黙って実名に戻る事故を止める。
/// </summary>
public static class DisplayNamesParser
{
    public static DisplayNames Parse(string? yaml)
    {
        var values = new Dictionary<string, string>();
        foreach (var raw in (yaml ?? "").Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            var hash = line.IndexOf('#');
            if (hash >= 0) line = line.Substring(0, hash);
            line = line.Trim();
            if (line.Length == 0) continue;
            var colon = line.IndexOf(':');
            if (colon <= 0) throw new FormatException($"display-names: 「キー: 値」の形でない行があります: {raw.Trim()}");
            var key = line.Substring(0, colon).Trim();
            var value = line.Substring(colon + 1).Trim();
            if (value.Length >= 2 && (value[0] == '"' || value[0] == '\'') && value[value.Length - 1] == value[0])
                value = value.Substring(1, value.Length - 2);
            if (!IsKnown(key)) throw new FormatException($"display-names: 知らないキー「{key}」があります（DisplayNames.Keys にあるキーだけ書けます）。");
            if (value.Length == 0) throw new FormatException($"display-names: 「{key}」の値が空です。");
            values[key] = value;
        }

        var d = new DisplayNames();
        string Pick(string key, string fallback) => values.TryGetValue(key, out var v) ? v : fallback;
        return new DisplayNames
        {
            Koshien = Pick("koshien", d.Koshien),
            Senbatsu = Pick("senbatsu", d.Senbatsu),
            Jingu = Pick("jingu", d.Jingu),
            JinguShort = Pick("jingu_short", d.JinguShort),
            SummerOfficial = Pick("summer_official", d.SummerOfficial),
            SenbatsuOfficial = Pick("senbatsu_official", d.SenbatsuOfficial),
            JinguOfficial = Pick("jingu_official", d.JinguOfficial),
            PrefSummerPrefix = Pick("pref_summer_prefix", d.PrefSummerPrefix),
        };
    }

    private static bool IsKnown(string key)
    {
        foreach (var (k, _) in DisplayNames.Keys) if (k == key) return true;
        return false;
    }
}
