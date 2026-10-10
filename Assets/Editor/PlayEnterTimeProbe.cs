#if UNITY_EDITOR
using UnityEditor;
using UnityEngine;

namespace KokoSim.UnityEditorTools
{
    /// <summary>
    /// 【開発用・エディタ限定】▶を押してから操作できるまでの実測（issue #608 の区間B）。
    ///
    /// <para>区間A（▶→Play開始＝ドメインリロード）は Editor.log の <c>Domain Reload Profiling</c> に
    /// 出るが、<b>区間B（Play開始→タイトルが出るまで）はどこにも残らない</b>。ここで測る。</para>
    ///
    /// <para><b>なぜ Unity の中で測るか</b>: 外（MCP/CLI）からポーリングすると、ドメインリロード後の
    /// ブリッジ再接続待ちがまるごと乗って数秒単位で上振れする（実測 6.7〜7.3秒＝ほぼ再接続待ち）。
    /// <c>EditorApplication.update</c> の中で <c>Time.realtimeSinceStartup</c>
    /// （Play開始で0に戻る）を読めば、その混入が無い。</para>
    ///
    /// <para>常設だが<b>既定では何もしない</b>（<c>[InitializeOnLoad]</c> を持たず、
    /// メニューで武装したときだけ購読する）。計測器自体がリロードを重くしては本末転倒なので、
    /// 静的コンストラクタでの自動購読はしない。</para>
    /// </summary>
    public static class PlayEnterTimeProbe
    {
        private const string ArmedKey = "KokoSim.PlayEnterTimeProbe.Armed";

        [MenuItem("KokoSim/計測/#608 ▶からタイトルが出るまでを測る（次の再生1回）")]
        private static void Arm()
        {
            EditorPrefs.SetBool(ArmedKey, true);
            Debug.Log("[#608Probe] 武装した。次に▶を押すと区間Bを実測して Console へ出す。");
        }

        private static bool _armed;

        /// <summary>Play へ入る側のドメインで購読し直す（静的購読はリロードで消えるため）。</summary>
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
        private static void OnEnterPlay()
        {
            if (!EditorPrefs.GetBool(ArmedKey, false)) return;
            EditorPrefs.SetBool(ArmedKey, false);   // 1回だけ
            _armed = true;
            Debug.Log($"[#608Probe] ①シーンロード前={Time.realtimeSinceStartup:F2}s");
            EditorApplication.update += Poll;
        }

        /// <summary>シーン内の全 Awake/OnEnable が終わった時点。①との差＝シーンの初期化そのもの。</summary>
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void AfterScene()
        {
            if (!_armed) return;
            Debug.Log($"[#608Probe] ②シーンロード後={Time.realtimeSinceStartup:F2}s");
        }


        private static void Poll()
        {
            if (!EditorApplication.isPlaying)
            {
                EditorApplication.update -= Poll;
                return;
            }
            var t = System.Type.GetType("KokoSim.Unity.Shell.ScreenRouter, Assembly-CSharp");
            if (t == null) { EditorApplication.update -= Poll; return; }
            var inst = t.GetProperty("Instance").GetValue(null, null);
            if (inst == null) return;
            var cur = t.GetProperty("CurrentScreen").GetValue(inst, null) as string;
            if (string.IsNullOrEmpty(cur)) return;

            EditorApplication.update -= Poll;
            Debug.Log($"[#608Probe] 区間B={Time.realtimeSinceStartup:F2}s（Play開始→{cur} が立つまで）"
                      + $" frame={Time.frameCount}");
        }
    }
}
#endif
