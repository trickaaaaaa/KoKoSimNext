namespace KokoSim.Unity.Shell
{
    /// <summary>
    /// どの版で動いているかを答える唯一の口（issue #1534）。ブラウザ版（Web 体験版 #1323）かどうかの判定は
    /// ここにだけ置き、画面ごとに <c>#if UNITY_WEBGL</c> を直書きしない。エディタは Windows 版と同じ扱い。
    /// </summary>
    public static class BuildPlatform
    {
        /// <summary>ブラウザ版（WebGL のビルド）で動いているか。</summary>
        public static bool IsBrowser
        {
#if UNITY_WEBGL && !UNITY_EDITOR
            get { return true; }
#elif UNITY_EDITOR
            get { return ForceBrowserInEditor; }
#else
            get { return false; }
#endif
        }

#if UNITY_EDITOR
        /// <summary>【エディタ限定】ブラウザ版の画面をエディタで撮る・試すために、ブラウザ版として振る舞わせる。</summary>
        public static bool ForceBrowserInEditor;
#endif
    }
}
