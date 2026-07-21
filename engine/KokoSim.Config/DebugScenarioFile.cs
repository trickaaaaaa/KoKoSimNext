using KokoSim.Engine.Debugging;

namespace KokoSim.Config;

/// <summary>
/// <c>data/debug/scenarios.yaml</c>（デバッグの場面ジャンプ）の探索とロード（設計書17 §3.4, F2）。
/// IO はこの層に隔離する（不変条件#3）。<b>解釈は engine の <see cref="ScenarioYamlParser"/> に委譲</b>し、
/// Unity（YamlDotNet を持たない）と同一コードで読む＝解釈のズレを構造的に潰す。
///
/// <para><b>欠損は正常系</b>（OPEN-QUESTIONS Q18-2 の (4)）: リリースビルドは <c>data/debug/</c> を
/// ディレクトリ単位で同梱除外するため、ファイルが無ければ<b>0件のカタログを返し例外を投げない</b>。
/// 「壊れたYAML」だけは投げる（黙って0件にすると編集ミスに気づけないため）。</para>
/// </summary>
public static class DebugScenarioLoader
{
    /// <summary>data/debug/scenarios.yaml の既定の相対パス。</summary>
    public const string DefaultRelativePath = "data/debug/scenarios.yaml";

    /// <summary>ファイルから読む。存在しなければ <see cref="ScenarioCatalog.Empty"/>。</summary>
    public static ScenarioCatalog LoadFromFileOrEmpty(string? path)
        => path is not null && File.Exists(path) ? Parse(File.ReadAllText(path)) : ScenarioCatalog.Empty;

    /// <summary>
    /// 実行ディレクトリから上へ辿って <c>data/debug/scenarios.yaml</c> を探す（見つからなければ0件）。
    /// リポジトリ内から CLI/テストを回すときの探索経路。
    /// </summary>
    public static ScenarioCatalog LoadFromRepoOrEmpty(string? startDirectory = null)
        => LoadFromFileOrEmpty(FindDefaultPath(startDirectory));

    /// <summary>既定パスを探す（見つからなければ null）。</summary>
    public static string? FindDefaultPath(string? startDirectory = null)
    {
        var dir = new DirectoryInfo(startDirectory ?? AppContext.BaseDirectory);
        while (dir is not null)
        {
            var candidate = Path.Combine(dir.FullName, DefaultRelativePath);
            if (File.Exists(candidate)) return candidate;
            dir = dir.Parent;
        }
        return null;
    }

    /// <summary>解釈は engine の純パーサ。書式違反は <see cref="InvalidDataException"/> に包んで投げる。</summary>
    public static ScenarioCatalog Parse(string yaml)
    {
        try
        {
            return ScenarioYamlParser.Parse(yaml);
        }
        catch (Exception e) when (e is FormatException or ArgumentException)
        {
            throw new InvalidDataException($"data/debug/scenarios.yaml が読めません: {e.Message}", e);
        }
    }
}
