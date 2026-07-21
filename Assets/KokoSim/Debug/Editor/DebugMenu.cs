#if UNITY_EDITOR
using UnityEditor;
using UnityEngine;

namespace KokoSim.Unity.Debugging.EditorTools
{
    /// <summary>
    /// デバッグ機能のEditorメニュー（設計書17 §7）。
    ///
    /// <para><b>人間も MCP も同じ関数（<see cref="DebugBridge"/>）を叩く</b>。ここは薄い呼び出し口に徹し、
    /// 独自のロジックを持たない＝経路を二重化しない。結果は Console にそのまま JSON で出す
    /// （MCP から見える文字列と1文字も違わないので、片方でだけ壊れることがない）。</para>
    /// </summary>
    public static class DebugMenu
    {
        private const string Root = "KokoSim/Debug/";

        [MenuItem(Root + "シナリオ一覧", priority = 0)]
        private static void ListScenarios()
        {
            DebugScenarios.Reload();
            Log(DebugBridge.ListScenarios());
        }

        [MenuItem(Root + "HUD を出す（F1でもトグル）", priority = 1)]
        private static void ShowHud()
        {
            if (!Application.isPlaying)
            {
                UnityEngine.Debug.LogWarning("[KokoSim/Debug] HUD は Play 中のみ出せます。");
                return;
            }
            DebugHudOverlay.Ensure();
            Log(DebugBridge.ToggleHud(true));
        }

        [MenuItem(Root + "HUD を隠す", priority = 2)]
        private static void HideHud() => Log(DebugBridge.ToggleHud(false));

        [MenuItem(Root + "試合を起こす/9回裏2死満塁", priority = 20)]
        private static void StartBasesLoaded() => Start("bases-loaded-9th");

        [MenuItem(Root + "試合を起こす/タイブレーク10回表", priority = 21)]
        private static void StartTieBreak() => Start("tiebreak-10th");

        [MenuItem(Root + "試合を起こす/暴投検証", priority = 22)]
        private static void StartWildPitch() => Start("wild-pitch-lab");

        [MenuItem(Root + "試合を起こす/振り逃げ検証", priority = 23)]
        private static void StartDroppedThird() => Start("dropped-third-strike-lab");

        [MenuItem(Root + "試合を起こす/サヨナラ検証", priority = 24)]
        private static void StartWalkoff() => Start("walkoff-lab");

        [MenuItem(Root + "進める/1球", priority = 40)]
        private static void AdvanceOnePitch() => Log(DebugBridge.AdvancePitch(1));

        [MenuItem(Root + "進める/1打席", priority = 41)]
        private static void AdvanceOnePa() => Log(DebugBridge.AdvancePa(1));

        [MenuItem(Root + "進める/イニング終了まで", priority = 42)]
        private static void AdvanceInning() => Log(DebugBridge.AdvanceUntil("inning-end"));

        [MenuItem(Root + "進める/試合終了まで", priority = 43)]
        private static void AdvanceGame() => Log(DebugBridge.AdvanceUntil("game-end"));

        [MenuItem(Root + "強制発動/本塁打", priority = 60)]
        private static void ForceHomeRun() => Log(DebugBridge.Force("HomeRun"));

        [MenuItem(Root + "強制発動/暴投", priority = 61)]
        private static void ForceWildPitch() => Log(DebugBridge.Force("WildPitch"));

        [MenuItem(Root + "強制発動/振り逃げ", priority = 62)]
        private static void ForceDroppedThird() => Log(DebugBridge.Force("DroppedThirdStrike"));

        [MenuItem(Root + "強制発動/併殺", priority = 63)]
        private static void ForceDoublePlay() => Log(DebugBridge.Force("DoublePlay"));

        [MenuItem(Root + "観測/状態をダンプ", priority = 80)]
        private static void DumpState() => Log(DebugBridge.DumpState());

        [MenuItem(Root + "観測/直近16球をダンプ", priority = 81)]
        private static void DumpTrace() => Log(DebugBridge.DumpTrace(16));

        [MenuItem(Root + "観測/母種をコピー", priority = 82)]
        private static void CopySeed()
        {
            EditorGUIUtility.systemCopyBuffer = Shell.GameSeed.MasterHex;
            UnityEngine.Debug.Log("[KokoSim/Debug] 母種をクリップボードへ: " + Shell.GameSeed.MasterHex);
        }

        /// <summary>直近の状態ダンプから再現トークンを拾ってクリップボードへ（バグ報告に貼る用）。</summary>
        [MenuItem(Root + "観測/再現トークンをコピー", priority = 83)]
        private static void CopyReproToken()
        {
            var json = DebugBridge.DumpState();
            const string key = "\"repro\":\"";
            var i = json.IndexOf(key, System.StringComparison.Ordinal);
            if (i < 0)
            {
                UnityEngine.Debug.LogWarning("[KokoSim/Debug] 再現トークンがありません（先に試合を起こす）。");
                return;
            }
            var start = i + key.Length;
            var end = json.IndexOf('"', start);
            EditorGUIUtility.systemCopyBuffer = json.Substring(start, end - start);
            UnityEngine.Debug.Log("[KokoSim/Debug] 再現トークンをクリップボードへ: " + EditorGUIUtility.systemCopyBuffer);
        }

        private static void Start(string scenarioId)
        {
            DebugTraceHub.Enabled = true;   // 起こす試合は必ず観測付き（HUD・DumpTrace の前提）
            Log(DebugBridge.StartMatch(scenarioId, ""));
        }

        private static void Log(string json) => UnityEngine.Debug.Log("[KokoSim/Debug] " + json);
    }
}
#endif
