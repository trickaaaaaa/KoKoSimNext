using System.IO;
using System.Linq;
using KokoSim.Unity.Shell;
using NUnit.Framework;

namespace KokoSim.EditorTests
{
    /// <summary>
    /// data/ の YAML はゲームから <see cref="DataFiles"/>（Resources の TextAsset）だけで読む（Web 体験版 #1323）。
    /// StreamingAssets をファイルとして読むとブラウザ版で全部「無い」になる（2026-11-21 の Web お試しビルドで実害）。
    /// Editor ではどちらでも読めて気づけないので、ソースを走査して止める。
    /// </summary>
    public class DataFilesTests
    {
        [Test]
        public void RuntimeCode_DoesNotReadStreamingAssets()
        {
            var offenders = Directory.GetFiles("Assets", "*.cs", SearchOption.AllDirectories)
                .Select(p => p.Replace('\\', '/'))
                .Where(p => !p.Contains("/Editor/") && !p.EndsWith("Shooter.cs") && !p.EndsWith("Probe.cs"))
                .Where(p => File.ReadAllText(p).Contains("Application.streamingAssetsPath"))
                .ToList();
            Assert.IsEmpty(offenders, "実行時コードが StreamingAssets をファイルとして読んでいる（ブラウザ版で読めない）。DataFiles を使う:\n" + string.Join("\n", offenders));
        }

        [TestCase("KokoSim/school-names.yaml")]
        [TestCase("KokoSim/team-colors.yaml")]
        [TestCase("KokoSim/events.yaml")]
        [TestCase("KokoSim/skills.yaml")]
        [TestCase("KokoSim/preview-phrases.yaml")]
        [TestCase("KokoSim/scout-blurbs.yaml")]
        [TestCase("KokoSim/stadiums.yaml")]
        [TestCase("KokoSim/display-names.yaml")]
        [TestCase("KokoSim/pref-formats/regional-tournaments.yaml")]
        public void EveryDataFile_IsReadableFromResources(string relativePath)
        {
            Assert.IsTrue(DataFiles.TryReadText(relativePath, out var text),
                relativePath + " が Resources/KokoSimData に無い（bash tools/sync-engine-dll.sh を回す）");
            Assert.IsNotEmpty(text);
        }

        [Test]
        public void PrefFormats_ReadAllReturnsEveryFileInNameOrder()
        {
            var files = DataFiles.ReadAll("KokoSim/pref-formats");
            var expected = Directory.GetFiles("data/pref-formats", "*.yaml").Select(Path.GetFileNameWithoutExtension)
                .OrderBy(n => n, System.StringComparer.Ordinal).ToList();
            CollectionAssert.AreEqual(expected, files.Select(f => f.Name).ToList());
        }
    }
}
