using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using NUnit.Framework;

namespace KokoSim.EditorTests
{
    /// <summary>
    /// 裏の仕事は <c>KokoSim.Unity.Shell.BackgroundWork</c> だけから回す（Web 体験版 #1323）。
    /// ブラウザ版はスレッドが無く、直接 <c>Task.Run</c> した仕事は始まらない・<c>Thread.Sleep</c> の待ちは永久に止まる
    /// （2026-11-21 の Web お試しビルドで試合前の「他会場の結果を集計中」が 0 のまま止まった）。
    /// Editor では動いてしまい気づけないので、ソースを走査して止める。
    /// </summary>
    public class BackgroundWorkTests
    {
        private static readonly Regex Direct = new Regex(@"(?<!BackgroundWork\.)\b(Task\.Run|Thread\.Sleep|Parallel\.For|Parallel\.ForEach|new Thread)\s*\(");

        [Test]
        public void RuntimeCode_StartsBackgroundWorkOnlyThroughBackgroundWork()
        {
            var offenders = Directory.GetFiles("Assets", "*.cs", SearchOption.AllDirectories)
                .Select(p => p.Replace('\\', '/'))
                .Where(p => !p.Contains("/Editor/") && !p.EndsWith("Shooter.cs") && !p.EndsWith("Probe.cs")
                            && !p.EndsWith("Verifier.cs") && !p.EndsWith("/BackgroundWork.cs"))
                .SelectMany(p => File.ReadAllLines(p)
                    .Select((line, i) => (p, i, line))
                    .Where(x => !x.line.TrimStart().StartsWith("//") && Direct.IsMatch(x.line)))
                .Select(x => $"{x.p}:{x.i + 1}: {x.line.Trim()}")
                .ToList();
            Assert.IsEmpty(offenders,
                "実行時コードが裏の仕事を直接回している（ブラウザ版で始まらない／止まる）。BackgroundWork.Run / Sleep を使う:\n"
                + string.Join("\n", offenders));
        }
    }
}
