using System;
using System.Collections.Generic;
using System.IO;

namespace KokoSim.Engine.Tests.Match.Game;

/// <summary>
/// 凍結ベースライン determinism-baseline.txt の読み込み（"カード シード" → SHA256 ダイジェスト）。
/// EngineDeterminismGateTests（ゲート本体）と DebugTraceTests（トレース on 側の照合。issue #361）で共有する。
/// 再生成は DeterminismBaselineDump の Skip を外して実行し、出力を貼り直す。
/// </summary>
internal static class DeterminismBaseline
{
    public static Dictionary<string, string> Load()
    {
        var path = Find();
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

    private static string Find()
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
