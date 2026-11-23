using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using NUnit.Framework;

namespace KokoSim.EditorTests
{
    /// <summary>
    /// ファイルを書く・消す実行時コードは、同じファイルで <c>WebStorage.Flush()</c> を呼ぶ（Web 体験版 #1323）。
    /// ブラウザ版は呼ばないと IndexedDB へ書き出されず、ページを開き直すとセーブが消える
    /// （2026-11-23 の Web お試しビルドで「つづきから」が押せなかった）。Editor では消えないので気づけない。
    /// </summary>
    public class WebStorageTests
    {
        private static readonly Regex Writes = new Regex(@"\bFile\.(WriteAll\w*|Delete|Replace|Move|Copy|AppendAll\w*)\s*\(|FileMode\.Create");

        // 撮影・診断の書き出し（セーブではない＝ブラウザ版で消えても困らない）。
        private static readonly string[] DiagnosticWriters =
        {
            "DuelUmpireLookSheet.cs", "DuelViewRecorder.cs", "UmpireCallRecorder.cs", "ScreenshotBatch.cs",
            "TournamentPreviewController.cs",
        };

        [Test]
        public void RuntimeFileWrites_FlushToWebStorage()
        {
            var offenders = Directory.GetFiles("Assets", "*.cs", SearchOption.AllDirectories)
                .Select(p => p.Replace('\\', '/'))
                .Where(p => !p.Contains("/Editor/") && !p.EndsWith("Shooter.cs") && !p.EndsWith("Probe.cs")
                            && !p.EndsWith("Verifier.cs") && !p.Contains("/Benchmark/")
                            && !DiagnosticWriters.Any(p.EndsWith))
                .Where(p =>
                {
                    var code = string.Join("\n", File.ReadAllLines(p).Where(l => !l.TrimStart().StartsWith("//")));
                    return Writes.IsMatch(code) && !code.Contains("WebStorage.Flush()");
                })
                .ToList();
            Assert.IsEmpty(offenders,
                "ファイルを書く・消すのに WebStorage.Flush() を呼んでいない（ブラウザ版で開き直すと消える）:\n"
                + string.Join("\n", offenders));
        }
    }
}
