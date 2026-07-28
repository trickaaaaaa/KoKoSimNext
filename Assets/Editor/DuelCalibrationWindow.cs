using System.IO;
using KokoSim.Unity.Match;
using UnityEditor;
using UnityEngine;

namespace KokoSim.EditorTools
{
    /// <summary>
    /// 対決ビューの絵作りキャリブレーションツール（開発用・エディタ限定）。
    /// Play中に MatchLive→対決ビューON で生きている DuelStageRig（DuelStageRig.Live）へ
    /// スライダ操作を即時反映し、スクショ往復なしでカメラ構図・空色・ライトを追い込む。
    ///
    /// 運用の出口: 「JSONへ書き出し」で out/duel-calibration.json を吐き、その値を
    /// DuelCalibration.Default（DuelStageRig.cs）へ焼き戻して確定する（ツール値は焼き戻すまで仮）。
    /// 値は DuelStageRig.CalibrationOverride にも入れるため、rigが作り直されても
    /// （打席送り・RebuildDuelRigForCapture）調整中の見た目は維持される。
    /// </summary>
    public sealed class DuelCalibrationWindow : EditorWindow
    {
        private const string JsonPath = "out/duel-calibration.json";

        [SerializeField] private DuelCalibration _cal = DuelCalibration.Default;

        [MenuItem("KokoSim/対決ビュー キャリブレーション")]
        private static void Open()
        {
            var w = GetWindow<DuelCalibrationWindow>("対決キャリブレーション");
            w.minSize = new Vector2(340f, 430f);
        }

        private void OnEnable()
        {
            // ドメインリロード後: 窓側の値（シリアライズ保持）が正。上書き中だった静的値を張り直す
            if (DuelStageRig.CalibrationOverride.HasValue) _cal = DuelStageRig.CalibrationOverride.Value;
            else Push();
        }

        private void OnInspectorUpdate() => Repaint();   // Play開始・rig生成の状態表示を追従させる

        private void OnGUI()
        {
            var live = DuelStageRig.Live;
            bool rigOn = live != null && live.Available;
            EditorGUILayout.HelpBox(
                rigOn ? "対決ビューに接続中: スライダは即時反映されます"
                      : "rig未接続: Play→試合→対決ビューON で接続されます（現在値は生成時に反映）",
                rigOn ? MessageType.Info : MessageType.Warning);

            EditorGUI.BeginChangeCheck();

            EditorGUILayout.LabelField("カメラ構図（既定=R-comp60）", EditorStyles.boldLabel);
            _cal.Dist = EditorGUILayout.Slider("後退距離 [m]", _cal.Dist, 10f, 120f);
            _cal.FovDeg = EditorGUILayout.Slider("基準FOV [度]", _cal.FovDeg, 1f, 20f);
            _cal.PitchDeg = EditorGUILayout.Slider("俯角 [度]", _cal.PitchDeg, 0f, 15f);
            _cal.YawDeg = EditorGUILayout.Slider("横角 [度]", _cal.YawDeg, -15f, 15f);
            _cal.AimY = EditorGUILayout.Slider("注視高 [m]", _cal.AimY, 0f, 2.5f);
            _cal.AimZ = EditorGUILayout.Slider("注視Z [m]", _cal.AimZ, -2f, 10f);

            EditorGUILayout.Space();
            EditorGUILayout.LabelField("空・ライト", EditorStyles.boldLabel);
            _cal.Sky = EditorGUILayout.ColorField("空色", _cal.Sky);
            _cal.Ambient = EditorGUILayout.Slider("環境光の下限", _cal.Ambient, 0f, 1f);
            _cal.LightDir.x = EditorGUILayout.Slider("ライト向き X", _cal.LightDir.x, -1f, 1f);
            _cal.LightDir.y = EditorGUILayout.Slider("ライト向き Y", _cal.LightDir.y, -1f, 1f);
            _cal.LightDir.z = EditorGUILayout.Slider("ライト向き Z", _cal.LightDir.z, -1f, 1f);

            if (EditorGUI.EndChangeCheck()) Push();

            EditorGUILayout.Space();
            using (new EditorGUILayout.HorizontalScope())
            {
                if (GUILayout.Button("既定値へ戻す")) { _cal = DuelCalibration.Default; Push(); }
                using (new EditorGUI.DisabledScope(!File.Exists(JsonPath)))
                {
                    if (GUILayout.Button("JSON読込"))
                    {
                        _cal = JsonUtility.FromJson<DuelCalibration>(File.ReadAllText(JsonPath));
                        Push();
                    }
                }
                if (GUILayout.Button("JSONへ書き出し")) SaveJson();
            }
            EditorGUILayout.HelpBox("確定したら書き出したJSONの値を DuelCalibration.Default へ焼き戻す（ツール値は仮）", MessageType.None);
        }

        private void Push()
        {
            DuelStageRig.CalibrationOverride = _cal;
            var live = DuelStageRig.Live;
            if (live != null && live.Available) live.ApplyCalibration(_cal);
        }

        private void SaveJson()
        {
            Directory.CreateDirectory(Path.GetDirectoryName(JsonPath));
            var json = JsonUtility.ToJson(_cal, true);
            File.WriteAllText(JsonPath, json);
            Debug.Log("[DuelCalibration] wrote " + JsonPath + "\n" + json);
        }
    }
}
