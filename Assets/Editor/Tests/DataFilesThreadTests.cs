using System.Threading.Tasks;
using KokoSim.Unity.Shell;
using NUnit.Framework;

namespace KokoSim.EditorTests
{
    /// <summary>
    /// data/ の YAML は裏のスレッドからも読める（2026-11-22）。Resources.Load はメインスレッドからしか呼べず、
    /// 大会の後始末（結果画面の OK → BackgroundGameOp）で初めて地区大会の枠を読むと空になり、地区大会→神宮→
    /// センバツの連鎖が毎回切れていた。DataFiles が起動時に全部控え、どのスレッドにも同じ中身を返すことを固定する。
    /// </summary>
    public class DataFilesThreadTests
    {
        [Test]
        public void TryReadText_FromThreadPool_ReturnsSameAsMainThread()
        {
            DataFiles.Preload();
            Assert.IsTrue(DataFiles.TryReadText("KokoSim/pref-formats/regional-tournaments.yaml", out var main),
                "メインスレッドで地区大会の枠が読めない（sync-engine-dll.sh を回したか）");

            string background = null;
            var ok = Task.Run(() => DataFiles.TryReadText("KokoSim/pref-formats/regional-tournaments.yaml", out background)).Result;

            Assert.IsTrue(ok, "裏のスレッドで地区大会の枠が読めない");
            Assert.AreEqual(main, background);
        }

        [Test]
        public void ReadAll_FromThreadPool_ListsTheSameFiles()
        {
            DataFiles.Preload();
            var main = DataFiles.ReadAll("KokoSim/pref-formats");
            var background = Task.Run(() => DataFiles.ReadAll("KokoSim/pref-formats")).Result;

            Assert.Greater(main.Count, 0, "pref-formats が空");
            CollectionAssert.AreEqual(main, background);
        }
    }
}
