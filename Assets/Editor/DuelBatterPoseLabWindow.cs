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

        private const string BatPathJson = "out/duel-bat-path.json";

        // 掴む平面。バットは3次元なので、2次元の絵の上で動かすには「どの面で動かすか」が要る
        private static readonly string[] PlaneNames = { "正面（左右×上下）", "真上（左右×前後）", "真横（前後×上下）" };

        [SerializeField] private DuelCalibration _cal = DuelCalibration.Default;
        [SerializeField] private int _view;
        [SerializeField] private float _speed = 1f;
        [SerializeField] private int _plane;

        private DuelStageRig _rig;
        private Vector2 _scroll;
        private bool _playing;
        private double _lastUpdate;
        private DuelBatPath _path = new DuelBatPath();
        private int _drag;   // 0=掴んでいない / 1=中心（位置）/ 2=ノブ端（向き）

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
            DuelBatPath.Current = _path;   // static はドメインリロードで消える＝組むたびに入れ直す
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
                DrawBatOverlay(r);

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

                DrawBatPathBar();

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

        /// <summary>バットを手で置くための操作列（有効化・掴む平面・キーの打ち消し・焼き直し・保存）。</summary>
        private void DrawBatPathBar()
        {
            EditorGUI.BeginChangeCheck();
            bool on = EditorGUILayout.ToggleLeft(
                "バットを手で置く（切ると両手追従へ戻る）", _cal.BatFromPath >= 0.5f);
            if (EditorGUI.EndChangeCheck())
            {
                _cal.BatFromPath = on ? 1f : 0f;
                // 空のまま有効にすると「掴めるのに何も起きない」死に状態になる（キーが2本たまるまで
                // 手追従へ落ちるため）。入れた瞬間に今の絵を焼いて、そこから編集を始められるようにする
                if (on && (_path == null || !_path.HasKeys)) BakePath();
                else Push();
            }

            using (new EditorGUI.DisabledScope(!on))
            {
                _plane = GUILayout.Toolbar(_plane, PlaneNames);

                float t = _rig.BatterClipTime;
                bool hasKey = false;
                if (_path?.Keys != null)
                    foreach (var k in _path.Keys)
                        if (Mathf.Abs(k.T - t) <= 0.02f) { hasKey = true; break; }

                using (new EditorGUILayout.HorizontalScope())
                {
                    EditorGUILayout.LabelField(
                        hasKey ? "◆ このコマにキーあり" : "このコマはキー無し（補間）",
                        EditorStyles.miniLabel, GUILayout.Width(150f));
                    if (GUILayout.Button("ここにキー", GUILayout.Width(80f)))
                    {
                        var tr = _rig.BatterTransform;
                        if (tr != null && _rig.TryGetBatWorld(out var bp, out var br))
                        {
                            _path.Set(t, tr.InverseTransformPoint(bp),
                                (Quaternion.Inverse(tr.rotation) * br).eulerAngles);
                            Push();
                        }
                    }
                    using (new EditorGUI.DisabledScope(!hasKey))
                    {
                        if (GUILayout.Button("消す", GUILayout.Width(50f))) { _path.Remove(t); Push(); }
                    }
                    if (GUILayout.Button("焼き直す", GUILayout.Width(70f))) BakePath();
                    EditorGUILayout.LabelField(
                        "キー " + (_path?.Keys?.Count ?? 0), EditorStyles.miniLabel, GUILayout.Width(50f));
                }

                using (new EditorGUILayout.HorizontalScope())
                {
                    if (GUILayout.Button("軌道をJSONへ", GUILayout.Width(110f)))
                    {
                        Directory.CreateDirectory(Path.GetDirectoryName(BatPathJson));
                        File.WriteAllText(BatPathJson, _path.ToJson());
                        Debug.Log("[DuelPoseLab] wrote " + BatPathJson);
                    }
                    using (new EditorGUI.DisabledScope(!File.Exists(BatPathJson)))
                    {
                        if (GUILayout.Button("軌道を読込", GUILayout.Width(100f)))
                        {
                            var loaded = DuelBatPath.FromJson(File.ReadAllText(BatPathJson));
                            if (loaded != null) { _path = loaded; Push(); }
                        }
                    }
                    EditorGUILayout.LabelField(
                        "中心＝位置 ／ 白い端＝向き。掴む面は上のトグルで選ぶ",
                        EditorStyles.miniLabel);
                }
            }
        }

        // ---------------------------------------------------------------- 絵の上でバットを掴む

        /// <summary>ビューポート（0..1・左下原点）→ 窓のGUI座標（左上原点）。</summary>
        private static Vector2 ToGui(Rect r, Vector2 vp)
            => new Vector2(r.x + vp.x * r.width, r.y + (1f - vp.y) * r.height);

        /// <summary>掴む平面の法線（ワールド）。打者ローカル軸から作る＝立ち向きを変えても意味が変わらない。</summary>
        private Vector3 PlaneNormal()
        {
            var tr = _rig.BatterTransform;
            if (tr == null) return Vector3.up;
            switch (_plane)
            {
                case 1: return Vector3.up;        // 真上から見た面＝左右×前後
                case 2: return tr.right;          // 真横から見た面＝前後×上下
                default: return tr.forward;       // 正面から見た面＝左右×上下
            }
        }

        /// <summary>
        /// プレビューにバットの操作ハンドルを重ね、ドラッグでその**コマのキー**を書き換える。
        ///
        /// 中心＝位置／ノブ端＝向き。3次元を2次元で掴むので、動かす平面は上のトグルで選ぶ。
        /// 視線と平面が平行に近いときはリグ側が拒否する（掴んでも動かない＝任意の大きさへ飛ばない）。
        /// </summary>
        private void DrawBatOverlay(Rect r)
        {
            if (_cal.BatFromPath < 0.5f) return;
            var tr = _rig.BatterTransform;
            if (tr == null || !_rig.TryGetBatWorld(out var center, out var rot)) return;

            float halfLen = Mathf.Max(0.1f, _cal.BatLengthM) * 0.5f;
            Vector3 axis = rot * Vector3.up;             // ノブ→バレル
            Vector3 knob = center - axis * halfLen;
            if (!_rig.TryWorldToViewport(center, out var vc)) return;
            if (!_rig.TryWorldToViewport(knob, out var vk)) return;
            Vector2 pc = ToGui(r, vc), pk = ToGui(r, vk);

            Handles.BeginGUI();
            Handles.color = new Color(0.96f, 0.78f, 0.29f, 0.9f);
            Handles.DrawAAPolyLine(3f, pc, pk);
            Handles.EndGUI();
            EditorGUI.DrawRect(new Rect(pc.x - 5f, pc.y - 5f, 10f, 10f), new Color(0.96f, 0.78f, 0.29f));
            EditorGUI.DrawRect(new Rect(pk.x - 4f, pk.y - 4f, 8f, 8f), Color.white);

            var e = Event.current;
            switch (e.type)
            {
                case EventType.MouseDown when r.Contains(e.mousePosition):
                    if (Vector2.Distance(e.mousePosition, pk) <= 10f) _drag = 2;
                    else if (Vector2.Distance(e.mousePosition, pc) <= 12f) _drag = 1;
                    if (_drag != 0) { _playing = false; e.Use(); }
                    break;

                case EventType.MouseDrag when _drag != 0:
                {
                    var vp = new Vector2((e.mousePosition.x - r.x) / r.width,
                                         1f - (e.mousePosition.y - r.y) / r.height);
                    // 平面は**バット中心を通す**。中心を動かすときも向きを変えるときも同じ面で扱う
                    if (_rig.ViewportToPlane(vp, center, PlaneNormal(), out var hit))
                    {
                        float t = _rig.BatterClipTime;
                        if (_drag == 1)
                        {
                            _path.Set(t, tr.InverseTransformPoint(hit),
                                (Quaternion.Inverse(tr.rotation) * rot).eulerAngles);
                        }
                        else
                        {
                            Vector3 na = center - hit;
                            if (na.sqrMagnitude > 1e-6f)
                            {
                                var world = Quaternion.FromToRotation(Vector3.up, na.normalized);
                                _path.Set(t, tr.InverseTransformPoint(center),
                                    (Quaternion.Inverse(tr.rotation) * world).eulerAngles);
                            }
                        }
                        Push();
                    }
                    e.Use();
                    break;
                }

                case EventType.MouseUp when _drag != 0:
                    _drag = 0;
                    e.Use();
                    break;
            }
        }

        /// <summary>
        /// いまの手追従の結果を12fps刻みで焼いて出発点のテーブルにする。
        /// 焼いた直後は今と同じ絵になる＝テーブル経路が正しいことを絵で確かめてから編集に入れる。
        /// 焼くのは打ち切り点までで、その先（ほぼ静止の間延び）はキーを持たない。
        /// </summary>
        private void BakePath()
        {
            var tr = _rig.BatterTransform;
            if (tr == null) return;
            float keep = _cal.BatFromPath;
            float keepT = _rig.BatterClipTime;
            _cal.BatFromPath = 0f;               // 焼く間だけ手追従へ戻す（自分の出力を焼き直さない）
            _rig.ApplyCalibration(_cal);

            var baked = new DuelBatPath();
            float end = Mathf.Min(_rig.BatterClipLength - 0.001f, 2.70f);
            for (float t = 0f; t <= end; t += PoseStep)
            {
                _rig.SeekBatter(t);
                if (!_rig.TryGetBatWorld(out var bp, out var br)) continue;
                baked.Set(t, tr.InverseTransformPoint(bp),
                    (Quaternion.Inverse(tr.rotation) * br).eulerAngles);
            }

            _path = baked;
            _cal.BatFromPath = keep;
            Push();
            _rig.SeekBatter(keepT);
            Redraw();
            Debug.Log("[DuelPoseLab] 軌道を焼いた: keys=" + baked.Keys.Count);
        }

        private void Push()
        {
            DuelStageRig.CalibrationOverride = _cal;
            DuelBatPath.Current = _path;
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
