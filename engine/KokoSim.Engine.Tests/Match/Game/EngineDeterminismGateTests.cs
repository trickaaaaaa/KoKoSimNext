using System;
using System.Collections.Generic;
using System.IO;
using Xunit;

namespace KokoSim.Engine.Tests.Match.Game;

/// <summary>
/// 決定論ゲート（設計者Claude指示）。打席単位ステップ化リファクタの前後で GameResult が1ビットも
/// 変わらないことを保証する。代表カード（avg/tactics/modern）×50シードの GameResult ダイジェスト
/// （スコア・全ログ・全統計・全カウンタ）を凍結ベースライン determinism-baseline.txt と照合する。
/// 1シードでも不一致なら no-go（どのカード/シードが割れたかを列挙して失敗）。
/// ゲート通過後も回帰テストとして恒久保持する。
/// </summary>
public sealed class EngineDeterminismGateTests
{
    [Fact]
    public void GameResults_MatchFrozenBaseline()
    {
        var baseline = LoadBaseline();
        var mismatches = new List<string>();
        var missing = new List<string>();
        var chec0 = 0;

        foreach (var card in DeterminismCards.CardNames)
            foreach (var seed in DeterminismCards.Seeds())
            {
                var key = $"{card} {seed}";
                if (!baseline.TryGetValue(key, out var expected)) { missing.Add(key); continue; }

                var actual = GameResultDigest.Sha256Of(DeterminismCards.Run(card, seed));
                chec0++;
                if (actual != expected) mismatches.Add($"{key}: expected {expected} got {actual}");
            }

        Assert.True(missing.Count == 0, "ベースライン欠落: " + string.Join(", ", missing));
        Assert.True(mismatches.Count == 0,
            $"決定論ゲート不一致（{mismatches.Count}件・no-go）:\n" + string.Join("\n", mismatches));
        Assert.True(chec0 >= 150, $"検証件数が少ない（{chec0}）");
    }

    private static Dictionary<string, string> LoadBaseline()
    {
        var path = FindBaseline();
        var map = new Dictionary<string, string>();
        foreach (var line in File.ReadAllLines(path))
        {
            var t = line.Trim();
            if (t.Length == 0) continue;
            var parts = t.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length != 3) throw new FormatException($"baseline 行が不正: '{line}'");
            map[$"{parts[0]} {parts[1]}"] = parts[2];
        }
        return map;
    }

    private static string FindBaseline()
    {
        var direct = Path.Combine(AppContext.BaseDirectory, "Match", "Game", "determinism-baseline.txt");
        if (File.Exists(direct)) return direct;
        // フォールバック: 出力ディレクトリ直下やソースツリーを辿る。
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var c1 = Path.Combine(dir.FullName, "determinism-baseline.txt");
            if (File.Exists(c1)) return c1;
            var c2 = Path.Combine(dir.FullName, "Match", "Game", "determinism-baseline.txt");
            if (File.Exists(c2)) return c2;
            dir = dir.Parent;
        }
        throw new FileNotFoundException("determinism-baseline.txt が見つかりません。");
    }
}
