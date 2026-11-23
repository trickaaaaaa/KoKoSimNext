#if UNITY_WEBGL && !UNITY_EDITOR
using System.Runtime.InteropServices;
#endif

namespace KokoSim.Unity.Shell
{
    /// <summary>
    /// セーブの置き場所（<c>Application.persistentDataPath</c>）への書き込み・削除のあとに呼ぶ（Web 体験版 #1323）。
    ///
    /// <para>ブラウザ版はファイルをメモリに書くだけで、ここで IndexedDB へ書き出さないとページを開き直したときに
    /// セーブが消える（2026-11-23 の Web お試しビルドで「つづきから」が押せなかった）。Windows/Mac/Editor では何もしない。</para>
    /// </summary>
    public static class WebStorage
    {
#if UNITY_WEBGL && !UNITY_EDITOR
        [DllImport("__Internal")]
        private static extern void KokoSim_FlushStorage();

        public static void Flush() => KokoSim_FlushStorage();
#else
        public static void Flush() { }
#endif
    }
}
