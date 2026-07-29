using System.IO;
using KokoSim.Unity.Match;
using UnityEditor;
using UnityEngine;

namespace KokoSim.EditorTools
{
    /// <summary>
    /// 対決ビューの絵作りキャリブレーションツール（開発用・エディタ限定）。
    /// Play中に MatchLive→対決ビューON で生きている DuelStageRig（DuelStageRig.Live）へ
    /// スライダ操作を即時反映し、スクショ往復なしでカメラ構図・空色・ライト・立ち位置・
    /// 被り物（帽子/ヘルメット）の装着位置を追い込む。
    ///
    /// 運用の出口: 「JSONへ書き出し」で out/duel-calibration.json を吐き、その値を
    /// DuelCalibration.Default（DuelStageRig.cs）へ焼き戻して確定する（ツール値は焼き戻すまで仮）。
    /// 値は DuelStageRig.CalibrationOverride にも入れるため、rigが作り直されても
    /// （打席送り・RebuildDuelRigForCapture）調整中の見た目は維持される。
    ///
    /// 注意: 立ち位置のライブ移動中は装備の視線ベイク（つばの向き）がスポーン時のまま＝
    /// 大きく動かした場合は焼き戻し後の rig 再生成（対決ビュー入り直し）で最終確認する。
    /// </summary>
    public sealed class DuelCalibrationWindow : EditorWindow
    {
        private const string JsonPath = "out/duel-calibration.json";

        [SerializeField] private DuelCalibration _cal = DuelCalibration.Default;
        private Vector2 _scroll;

        [MenuItem("KokoSim/対決ビュー キャリブレーション")]
        private static void Open()
        {
            var w = GetWindow<DuelCalibrationWindow>("対決キャリブレーション");
            w.minSize = new Vector2(360f, 480f);
        }

        private void OnEnable()
        {
            // ドメインリロード後: 窓側の値（シリアライズ保持）が正。旧スキーマ値は既定で補完してから張り直す
            if (DuelStageRig.CalibrationOverride.HasValue) _cal = DuelStageRig.CalibrationOverride.Value;
            _cal = DuelCalibration.Upgrade(_cal);
            Push();
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

            _scroll = EditorGUILayout.BeginScrollView(_scroll);
            EditorGUI.BeginChangeCheck();

            EditorGUILayout.LabelField("カメラ構図（既定=R-comp60）", EditorStyles.boldLabel);
            _cal.Dist = EditorGUILayout.Slider("後退距離 [m]", _cal.Dist, 10f, 120f);
            _cal.FovDeg = EditorGUILayout.Slider("基準FOV [度]", _cal.FovDeg, 1f, 20f);
            _cal.PitchDeg = EditorGUILayout.Slider("俯角 [度]", _cal.PitchDeg, 0f, 15f);
            _cal.YawDeg = EditorGUILayout.Slider("横角 [度]", _cal.YawDeg, -15f, 15f);
            _cal.AimY = EditorGUILayout.Slider("注視高 [m]", _cal.AimY, 0f, 2.5f);
            _cal.AimZ = EditorGUILayout.Slider("注視Z [m]", _cal.AimZ, -2f, 10f);

            EditorGUILayout.Space();
            EditorGUILayout.LabelField("立ち位置 [m]（本塁=原点・+z=マウンド側。投手は対象外）", EditorStyles.boldLabel);
            _cal.BatterX = EditorGUILayout.Slider("打者 x", _cal.BatterX, -2f, 2f);
            _cal.BatterZ = EditorGUILayout.Slider("打者 z", _cal.BatterZ, -1.5f, 1.5f);
            _cal.CatcherX = EditorGUILayout.Slider("捕手 x", _cal.CatcherX, -1f, 1f);
            _cal.CatcherZ = EditorGUILayout.Slider("捕手 z", _cal.CatcherZ, -3f, 0.5f);
            _cal.UmpireX = EditorGUILayout.Slider("球審 x", _cal.UmpireX, -1.5f, 1.5f);
            _cal.UmpireZ = EditorGUILayout.Slider("球審 z", _cal.UmpireZ, -4f, 0f);

            EditorGUILayout.Space();
            EditorGUILayout.LabelField("被り物の装着位置 [m]（頭蓋基準: x=左右/y=上下/z=顔前）", EditorStyles.boldLabel);
            _cal.CapOffset.x = EditorGUILayout.Slider("帽子(投手) 左右X", _cal.CapOffset.x, -0.15f, 0.15f);
            _cal.CapOffset.y = EditorGUILayout.Slider("帽子(投手) 上下Y", _cal.CapOffset.y, -0.15f, 0.15f);
            _cal.CapOffset.z = EditorGUILayout.Slider("帽子(投手) 前後Z", _cal.CapOffset.z, -0.15f, 0.15f);
            _cal.HelmetOffset.x = EditorGUILayout.Slider("ヘルメット(打者) 左右X", _cal.HelmetOffset.x, -0.15f, 0.15f);
            _cal.HelmetOffset.y = EditorGUILayout.Slider("ヘルメット(打者) 上下Y", _cal.HelmetOffset.y, -0.15f, 0.15f);
            _cal.HelmetOffset.z = EditorGUILayout.Slider("ヘルメット(打者) 前後Z", _cal.HelmetOffset.z, -0.15f, 0.15f);
            // 被り角。殻の中心まわりに回るので、位置を決めたあとに向きだけ直せる。
            // スライダは粗く掴む用で、細かい値は右の数値欄に直接打てる
            _cal.HelmetEuler.x = EditorGUILayout.Slider("ヘルメット(打者) 前後の傾き", _cal.HelmetEuler.x, -180f, 180f);
            _cal.HelmetEuler.y = EditorGUILayout.Slider("ヘルメット(打者) ヨー", _cal.HelmetEuler.y, -180f, 180f);
            _cal.HelmetEuler.z = EditorGUILayout.Slider("ヘルメット(打者) 左右の傾き", _cal.HelmetEuler.z, -180f, 180f);
            _cal.CatcherHelmetOffset.x = EditorGUILayout.Slider("ヘルメット(捕手) 左右X", _cal.CatcherHelmetOffset.x, -0.15f, 0.15f);
            _cal.CatcherHelmetOffset.y = EditorGUILayout.Slider("ヘルメット(捕手) 上下Y", _cal.CatcherHelmetOffset.y, -0.15f, 0.15f);
            _cal.CatcherHelmetOffset.z = EditorGUILayout.Slider("ヘルメット(捕手) 前後Z", _cal.CatcherHelmetOffset.z, -0.15f, 0.15f);
            _cal.CatcherMaskOffset.x = EditorGUILayout.Slider("マスク(捕手) 左右X", _cal.CatcherMaskOffset.x, -0.15f, 0.15f);
            _cal.CatcherMaskOffset.y = EditorGUILayout.Slider("マスク(捕手) 上下Y", _cal.CatcherMaskOffset.y, -0.15f, 0.15f);
            _cal.CatcherMaskOffset.z = EditorGUILayout.Slider("マスク(捕手) 前後Z", _cal.CatcherMaskOffset.z, -0.15f, 0.15f);
            _cal.CatcherChestOffset.x = EditorGUILayout.Slider("胸当て(捕手) 左右X", _cal.CatcherChestOffset.x, -0.15f, 0.15f);
            _cal.CatcherChestOffset.y = EditorGUILayout.Slider("胸当て(捕手) 上下Y", _cal.CatcherChestOffset.y, -0.15f, 0.15f);
            _cal.CatcherChestOffset.z = EditorGUILayout.Slider("胸当て(捕手) 前後Z", _cal.CatcherChestOffset.z, -0.15f, 0.15f);

            EditorGUILayout.Space();
            EditorGUILayout.LabelField("打撃フォーム（収録モーキャップの破綻を骨へ当て直す）", EditorStyles.boldLabel);
            EditorGUILayout.LabelField("　じっくり追い込むなら KokoSim → 打撃フォーム確認（試合に入らず単独で動く）", EditorStyles.miniLabel);
            DuelFormGui.DrawBattingForm(ref _cal);
            DuelFormGui.DrawSwingButtons(rigOn ? live : null);

            EditorGUILayout.Space();
            EditorGUILayout.LabelField("空・ライト", EditorStyles.boldLabel);
            _cal.Sky = EditorGUILayout.ColorField("空色", _cal.Sky);
            _cal.Ambient = EditorGUILayout.Slider("環境光の下限", _cal.Ambient, 0f, 1f);
            _cal.LightDir.x = EditorGUILayout.Slider("ライト向き X", _cal.LightDir.x, -1f, 1f);
            _cal.LightDir.y = EditorGUILayout.Slider("ライト向き Y", _cal.LightDir.y, -1f, 1f);
            _cal.LightDir.z = EditorGUILayout.Slider("ライト向き Z", _cal.LightDir.z, -1f, 1f);

            if (EditorGUI.EndChangeCheck()) Push();
            EditorGUILayout.EndScrollView();

            EditorGUILayout.Space();
            using (new EditorGUILayout.HorizontalScope())
            {
                if (GUILayout.Button("既定値へ戻す")) { _cal = DuelCalibration.Default; Push(); }
                using (new EditorGUI.DisabledScope(!File.Exists(JsonPath)))
                {
                    if (GUILayout.Button("JSON読込"))
                    {
                        _cal = DuelCalibration.Upgrade(
                            JsonUtility.FromJson<DuelCalibration>(File.ReadAllText(JsonPath)));
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
