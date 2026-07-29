using System.IO;
using KokoSim.Unity.Match;
using UnityEditor;
using UnityEngine;

namespace KokoSim.EditorTools
{
    /// <summary>
    /// 打撃フォーム確認窓（開発用・エディタ限定）。**Edit modeで単独で動く**＝Playにも試合にも入らず、
    /// 投手が投げてくるのを待たずに構え／スイングを自由に流して、収録モーキャップの補正
    /// （<see cref="DuelBatterPose"/>）をスライダで追い込むための窓。
    ///
    /// 作りは対決ビュー本体（<see cref="DuelStageRig"/>）をそのまま組む＝**ツールと本番で絵が同じ**。
    /// 素体・装備・シェーダ（ポスタライズ6段）・カメラまで実機と同一で、ここで決めた値は
    /// キャリブレーション窓と同じ <see cref="DuelCalibration"/> に入るので、そのままJSONへ書き出して
    /// <c>DuelCalibration.Default</c> へ焼き戻せる。
    ///
    /// 注意: ステージは HideAndDontSave。窓を閉じる・Playへ入る時に必ず捨てる（残すとシーンに
    /// 孤児が残り、次に組んだステージと二重に映る。design-18 §7-1の実害）。
    /// </summary>
    public sealed class DuelBatterPoseLabWindow : EditorWindow
    {
        private const string JsonPath = "out/duel-calibration.json";
        private const int RtW = 960, RtH = 720;      // 4:3（EnsureSizeが許す最も縦長）
        private const float PoseStep = 1f / 12f;     // ポーズ量子化と同じ刻み（design-18 §4.2）

        private static readonly string[] ViewNames =
            { "投手側から", "真横から", "反対の横から", "手元の寄り", "頭の寄り", "試合の構図" };

        [SerializeField] private DuelCalibration _cal = DuelCalibration.Default;
        [SerializeField] private int _view;
        [SerializeField] private float _speed = 1f;

        private DuelStageRig _rig;
        private Vector2 _scroll;
        private bool _playing;
        private double _lastUpdate;

        [MenuItem("KokoSim/打撃フォーム確認")]
        private static void Open()
        {
            var w = GetWindow<DuelBatterPoseLabWindow>("打撃フォーム確認");
            w.minSize = new Vector2(900f, 560f);
        }

        private void OnEnable()
        {
            _cal = DuelCalibration.Upgrade(DuelStageRig.CalibrationOverride ?? _cal);
            _lastUpdate = EditorApplication.timeSinceStartup;
            EditorApplication.update += Drive;
            EditorApplication.playModeStateChanged += OnPlayModeChanged;
        }

        private void OnDisable()
        {
            EditorApplication.update -= Drive;
            EditorApplication.playModeStateChanged -= OnPlayModeChanged;
            DisposeRig();
        }

        /// <summary>Playへ入る前に必ず捨てる（HideAndDontSaveはシーン再読込を跨いで生き残るため）。</summary>
        private void OnPlayModeChanged(PlayModeStateChange s)
        {
            if (s == PlayModeStateChange.ExitingEditMode) DisposeRig();
        }

        private void DisposeRig()
        {
            if (_rig == null) return;
            _rig.Dispose();
            _rig = null;
            _playing = false;
        }

        private void EnsureRig()
        {
            if (_rig != null) return;
            if (EditorApplication.isPlayingOrWillChangePlaymode) return;
            _rig = new DuelStageRig();
            if (!_rig.Available) { DisposeRig(); return; }
            _rig.EnsureSize(RtW, RtH);
            _rig.ApplyCalibration(_cal);
            _rig.Tick(0.001f);
            AimView();
            _rig.RenderNow();
        }

        /// <summary>再生とコマ描画。Edit modeはカメラが自動で回らないので毎回 RenderNow する。</summary>
        private void Drive()
        {
            double now = EditorApplication.timeSinceStartup;
            float dt = Mathf.Clamp((float)(now - _lastUpdate), 0f, 0.1f);
            _lastUpdate = now;
            if (_rig == null || !_playing) return;
            _rig.Tick(dt * _speed);
            AimView();
            _rig.RenderNow();
            Repaint();
        }

        private void OnGUI()
        {
            EnsureRig();
            if (_rig == null)
            {
                EditorGUILayout.HelpBox(
                    EditorApplication.isPlaying
                        ? "Play中はこの窓は使いません（Playを止めると自動で組み直します）"
                        : "対決ビューの資産（素体・モーション）が読めないため組めません",
                    MessageType.Warning);
                return;
            }

            using (new EditorGUILayout.HorizontalScope())
            {
                DrawStage();
                DrawKnobs();
            }
        }

        // ---------------------------------------------------------------- 左: 絵と操作

        private void DrawStage()
        {
            using (new EditorGUILayout.VerticalScope(GUILayout.Width(position.width * 0.56f)))
            {
                // RTが外から破棄されていると絵だけが消える（rigは生きているので窓は組み直さない）。
                // 一度描かせれば RenderNow が作り直す
                var rt = _rig.Target;
                if (rt == null) { _rig.RenderNow(); rt = _rig.Target; }
                float w = Mathf.Max(240f, position.width * 0.56f - 12f);
                float h = rt != null ? w * rt.height / rt.width : w * 0.75f;
                var r = GUILayoutUtility.GetRect(w, h, GUILayout.ExpandWidth(false));
                if (rt != null) GUI.DrawTexture(r, rt, ScaleMode.ScaleToFit, false);

                EditorGUI.BeginChangeCheck();
                _view = GUILayout.Toolbar(_view, ViewNames);
                if (EditorGUI.EndChangeCheck()) { AimView(); _rig.RenderNow(); }

                DuelFormGui.DrawSwingButtons(_rig);
                if (Event.current.type == EventType.Used) Redraw();

                using (new EditorGUILayout.HorizontalScope())
                {
                    if (GUILayout.Button(_playing ? "停止" : "再生", GUILayout.Width(70f)))
                    {
                        _playing = !_playing;
                        _lastUpdate = EditorApplication.timeSinceStartup;
                    }
                    if (GUILayout.Button("◀ コマ", GUILayout.Width(70f))) Step(-PoseStep);
                    if (GUILayout.Button("コマ ▶", GUILayout.Width(70f))) Step(+PoseStep);
                    _speed = EditorGUILayout.Slider("速さ", _speed, 0.1f, 1.5f);
                }

                float len = Mathf.Max(0.01f, _rig.BatterClipLength);
                float t = Mathf.Clamp(_rig.BatterClipTime, 0f, len);
                EditorGUI.BeginChangeCheck();
                float nt = EditorGUILayout.Slider("クリップ内 [秒]", t, 0f, len);
                if (EditorGUI.EndChangeCheck()) { _playing = false; _rig.SeekBatter(nt); Redraw(); }

                // 区間の目安（実測アンカー。design-18 §7-2）
                EditorGUILayout.LabelField(
                    "構え 0.00–1.70 ／ 予備動作 –2.80 ／ スイング –3.15（面通過3.05）／ 走り出し 3.25–",
                    EditorStyles.miniLabel);
                EditorGUILayout.SelectableLabel(_rig.BatterPoseDiag(),
                    EditorStyles.miniLabel, GUILayout.Height(30f));
            }
        }

        private void Step(float dt)
        {
            _playing = false;
            float len = Mathf.Max(0.01f, _rig.BatterClipLength);
            _rig.SeekBatter(Mathf.Clamp(_rig.BatterClipTime + dt, 0f, len - 0.001f));
            Redraw();
        }

        private void Redraw()
        {
            AimView();
            _rig.RenderNow();
            Repaint();
        }

        // ---------------------------------------------------------------- 右: つまみ

        private void DrawKnobs()
        {
            using (new EditorGUILayout.VerticalScope())
            {
                _scroll = EditorGUILayout.BeginScrollView(_scroll);
                EditorGUI.BeginChangeCheck();
                DuelFormGui.DrawBattingForm(ref _cal);
                if (EditorGUI.EndChangeCheck()) Push();
                EditorGUILayout.EndScrollView();

                using (new EditorGUILayout.HorizontalScope())
                {
                    if (GUILayout.Button("既定値へ戻す"))
                    {
                        var d = DuelCalibration.Default;
                        // 触るのは打撃フォームだけ（カメラ・空・装着位置の調整を巻き添えにしない）
                        _cal.BatterYawDeg = d.BatterYawDeg;
                        _cal.GripGapM = d.GripGapM;
                        _cal.GripPullBias = d.GripPullBias;
                        _cal.GripElbowPoleDeg = d.GripElbowPoleDeg;
                        _cal.GripElbowPoleDegTop = d.GripElbowPoleDegTop;
                        _cal.GripWristFlexDeg = d.GripWristFlexDeg;
                        _cal.GripWristFlexStrength = d.GripWristFlexStrength;
                        _cal.GripWristFlexDegTop = d.GripWristFlexDegTop;
                        _cal.GripWristFlexStrengthTop = d.GripWristFlexStrengthTop;
                        _cal.GripWristTwistDeg = d.GripWristTwistDeg;
                        _cal.GripWristTwistDegTop = d.GripWristTwistDegTop;
                        _cal.GripWristSideDeg = d.GripWristSideDeg;
                        _cal.GripWristSideDegTop = d.GripWristSideDegTop;
                        _cal.GripReachGuard = d.GripReachGuard;
                        _cal.GripReachGuardSwing = d.GripReachGuardSwing;
                        _cal.GripReachGuardTop = d.GripReachGuardTop;
                        _cal.GripReachGuardSwingTop = d.GripReachGuardSwingTop;
                        _cal.GripFingerCurl = d.GripFingerCurl;
                        _cal.GripFingerCurlTop = d.GripFingerCurlTop;
                        _cal.GripForearmRollDeg = d.GripForearmRollDeg;
                        _cal.GripForearmRollDegTop = d.GripForearmRollDegTop;
                        _cal.HeadStrength = d.HeadStrength;
                        _cal.HeadYawOffsetDeg = d.HeadYawOffsetDeg;
                        _cal.HeadElevDeg = d.HeadElevDeg;
                        _cal.HeadNeckShare = d.HeadNeckShare;
                        _cal.HeadUntilSec = d.HeadUntilSec;
                        _cal.KneeDeg = d.KneeDeg;
                        _cal.KneeFromSec = d.KneeFromSec;
                        _cal.KneeFullSec = d.KneeFullSec;
                        _cal.KneeToSec = d.KneeToSec;
                        Push();
                    }
                    using (new EditorGUI.DisabledScope(!File.Exists(JsonPath)))
                    {
                        if (GUILayout.Button("JSON読込"))
                        {
                            _cal = DuelCalibration.Upgrade(
                                JsonUtility.FromJson<DuelCalibration>(File.ReadAllText(JsonPath)));
                            Push();
                        }
                    }
                    if (GUILayout.Button("JSONへ書き出し"))
                    {
                        Directory.CreateDirectory(Path.GetDirectoryName(JsonPath));
                        var json = JsonUtility.ToJson(_cal, true);
                        File.WriteAllText(JsonPath, json);
                        Debug.Log("[DuelPoseLab] wrote " + JsonPath + "\n" + json);
                    }
                }
                EditorGUILayout.HelpBox(
                    "確定したら書き出したJSONの値を DuelCalibration.Default へ焼き戻す（ツール値は仮）",
                    MessageType.None);
            }
        }

        private void Push()
        {
            DuelStageRig.CalibrationOverride = _cal;
            if (_rig == null) return;
            _rig.ApplyCalibration(_cal);   // カメラを試合構図へ戻すので、この後に狙い直す
            _rig.SeekBatter(_rig.BatterClipTime);   // 補正値を今のコマへ即座に反映
            Redraw();
        }

        // ---------------------------------------------------------------- カメラ

        private void AimView()
        {
            var anim = _rig.BatterAnimator;
            var root = _rig.StageRoot;
            if (anim == null || root == null) return;
            if (_view == 5)
            {
                // プレビュー枠（縦に収めたい実寸）を解除しないと画角が残り、望遠のはずが全景になる
                _rig.ClearPreviewFrame();
                _rig.ApplyCalibration(_cal);
                return;
            }

            Vector3 hips = Bone(anim, HumanBodyBones.Hips);
            Vector3 center = hips + Vector3.up * 0.15f;
            Vector3 toP = _rig.PitcherHeadPoint - hips;
            toP.y = 0f;
            toP = toP.sqrMagnitude > 1e-6f ? toP.normalized : Vector3.forward;
            Vector3 side = Vector3.Cross(Vector3.up, toP).normalized;

            Vector3 target = center, dir = toP;
            float radius = 3.2f, frameH = 2.1f;
            switch (_view)
            {
                case 1: dir = side; break;
                case 2: dir = -side; break;
                case 3:
                    target = Vector3.Lerp(Bone(anim, HumanBodyBones.LeftHand),
                        Bone(anim, HumanBodyBones.RightHand), 0.5f);
                    radius = 0.62f; frameH = 0.45f; break;
                case 4:
                    target = Bone(anim, HumanBodyBones.Head);
                    radius = 0.60f; frameH = 0.42f; break;
            }
            Vector3 cam = target + dir * radius + Vector3.up * (frameH * 0.06f);
            _rig.FramePreview(root.InverseTransformPoint(cam), root.InverseTransformPoint(target), frameH);
        }

        private static Vector3 Bone(Animator a, HumanBodyBones b)
        {
            var t = a.GetBoneTransform(b);
            return t != null ? t.position : Vector3.zero;
        }
    }
}
