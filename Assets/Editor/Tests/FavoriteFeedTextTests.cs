using KokoSim.Unity.Home;
using KokoSim.Unity.Tournament;
using NUnit.Framework;

namespace KokoSim.EditorTests
{
    /// <summary>展望で自校に印が付いたときの1行（issue #1476・2026-11-23 オーナー選択の文言）。</summary>
    public class FavoriteFeedTextTests
    {
        private static TournamentPreviewState.ContenderRow Row(string sym, string cls, string label)
            => new TournamentPreviewState.ContenderRow { MarkSym = sym, MarkClass = cls, MarkLabel = label, Name = "桜丘高校", IsManager = true };

        [Test]
        public void Favorite()
            => Assert.AreEqual("新聞の展望で、桜丘高校が「◎優勝候補」に挙がった。部員の顔が少し引きつっている。",
                HomeState.MarkedFeedText(Row("◎", "fav", "優勝候補")));

        [Test]
        public void Contender()
            => Assert.AreEqual("新聞の展望で、桜丘高校が「○対抗」に挙がった。マネージャーが切り抜きを作っている。",
                HomeState.MarkedFeedText(Row("○", "cont", "対抗")));

        [Test]
        public void DarkHorse()
            => Assert.AreEqual("新聞の展望で、桜丘高校が「▲ダークホース」扱いだ。「ダーク」の響きに部員が妙に喜んでいる。",
                HomeState.MarkedFeedText(Row("▲", "dark", "ダークホース")));
    }
}
