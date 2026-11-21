using System;
using System.Collections.Generic;
using System.IO;
using KokoSim.Engine.Season;
using KokoSim.Engine.Season.Events;
using Xunit;

namespace KokoSim.Engine.Tests.Season;

/// <summary>
/// 大会名の差し替え口（issue #1319）。表（data/display-names.yaml）と engine の既定値の一致、
/// 読み込みの止め方、イベント文と大会種別の呼び名が表から引かれることを固定する。
/// <see cref="DisplayNames.Current"/> は静的なので、差し替えるテストは finally で必ず既定に戻す。
/// </summary>
[Collection(DisplayNamesCollection.Name)]
public class DisplayNamesTests
{
    [Fact]
    public void RepositoryTable_EqualsTheEngineDefaults()
    {
        // 架空名に替えたら engine の既定値（Balance CLI・engine 単体のテストが使う）もそろえる。
        // 片方だけ替えた状態を赤で知らせる（不変条件#4 と同じ考え方）。
        var table = DisplayNamesParser.Parse(File.ReadAllText(FindDataFile("display-names.yaml")));
        var defaults = new DisplayNames();
        foreach (var (key, get) in DisplayNames.Keys)
            Assert.True(get(defaults) == get(table), $"{key}: 表「{get(table)}」と既定値「{get(defaults)}」が違う");
    }

    [Fact]
    public void RepositoryTable_WritesEveryKey()
    {
        // 書き忘れたキーは黙って既定値（実名）になる＝架空名に替えたつもりで実名が残る。全部書いてあることを固定する。
        var text = File.ReadAllText(FindDataFile("display-names.yaml"));
        foreach (var (key, _) in DisplayNames.Keys)
            Assert.Contains("\n" + key + ":", "\n" + text.Replace("\r", ""));
    }

    [Fact]
    public void Parse_UnknownKey_Throws()
        => Assert.Throws<FormatException>(() => DisplayNamesParser.Parse("koshein: 聖地\n"));

    [Fact]
    public void Parse_EmptyValue_Throws()
        => Assert.Throws<FormatException>(() => DisplayNamesParser.Parse("koshien:\n"));

    [Fact]
    public void Parse_StripsCommentsAndQuotes_AndKeepsDefaultsForMissingKeys()
    {
        var n = DisplayNamesParser.Parse("# 見出し\nkoshien: \"聖地\"   # 通称\n");
        Assert.Equal("聖地", n.Koshien);
        Assert.Equal(new DisplayNames().Senbatsu, n.Senbatsu);
    }

    [Fact]
    public void SwappingTheTable_ChangesEventTextAndTournamentNames()
    {
        try
        {
            DisplayNames.Use(new DisplayNames { Koshien = "聖地", Senbatsu = "春の大会", Jingu = "秋の全国大会" });

            Assert.Equal("聖地の土を持ち帰る。", EventTextFormat.Apply("{koshien}の土を持ち帰る。", new Dictionary<string, string>()));
            Assert.Equal("春の大会出場、", EventTextFormat.Apply("{senbatsu}出場、", null));
            Assert.Equal("聖地", TournamentKindNames.Of(TournamentKind.SummerKoshien));
            Assert.Equal("春の大会", TournamentKindNames.Of(TournamentKind.SenbatsuKoshien));
            Assert.Equal("秋の全国大会", TournamentKindNames.Of(TournamentKind.Jingu));
        }
        finally
        {
            DisplayNames.Use(null);
        }
    }

    [Fact]
    public void EventValues_TakePrecedenceOverTheTable()
    {
        // 呼び出し元が渡した値（{school} 等）が先。表のキーと同名の値が来ても呼び出し元を優先する。
        Assert.Equal("X", EventTextFormat.Apply("{koshien}", new Dictionary<string, string> { ["koshien"] = "X" }));
    }

    [Fact]
    public void RepositoryEvents_DoNotSpellRealTournamentNamesDirectly()
    {
        // 実名を文に直接書くと、表を替えてもその文だけ実名が残る。差し込み語で書く（コメント行は対象外）。
        var banned = new[] { "甲子園", "センバツ", "選抜", "神宮", "選手権" };
        var lines = File.ReadAllLines(FindDataFile("events.yaml"));
        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i].TrimStart();
            if (line.StartsWith("#")) continue;
            foreach (var w in banned)
                Assert.False(line.Contains(w), $"events.yaml:{i + 1} に実名「{w}」が直接書かれている: {line}");
        }
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

/// <summary>
/// <see cref="DisplayNames.Current"/> を差し替えるテストと、大会名を読むテストを並行させないための直列化。
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class DisplayNamesCollection
{
    public const string Name = "DisplayNames (static table)";
}
