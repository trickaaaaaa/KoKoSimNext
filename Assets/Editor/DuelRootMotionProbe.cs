#if UNITY_EDITOR
using System.Text;
using UnityEngine;
using UnityEngine.Animations;
using UnityEngine.Playables;

namespace KokoSim.EditorTools
{
    /// <summary>
    /// 投手のルートモーション積分の突き合わせプローブ（design-18 §7-2）。
    /// DuelStageRig と同じグラフ構成（2入力ミキサー＋ルートモーションON）を Edit mode で組み、
    ///   R: 60fpsで頭から積分した基準（SeekActor相当）
    ///   A: 12fpsコマ打ちのみ（フェードなし）
    ///   B: 12fpsコマ打ち＋切り口クロスフェード（in0=退場ポーズ・重みランプ）
    ///   C: B＋フェード中だけ applyRootMotion を切る（＝現行の実装）
    /// の最終transformを比べる。実測 2026-07-30:
    ///   R dPos=( 0.03,-0.75) dYaw=-107.4 / A dPos=(-0.31,-0.58) dYaw=-107.4
    ///   B dPos=( 0.06,-0.02) dYaw=+124.0 / C dPos=(-0.32,-0.57) dYaw=-108.8
    /// ＝①刻み幅（12/60fps）で食い違うのは**位置だけ**（向きは一致）、
    ///   ②`Pause` した退場側でもミキサーは混合ルート姿勢の差分を流すので**フェード中は
    ///     ルートモーションを切らないと壊れる**（231°ずれ）、が読める。
    /// 繋ぎ方（フェード長・切り口）を触ったらここで測り直す。絵では切り分けられない。
    /// 使い方: KokoSim.EditorTools.DuelRootMotionProbe.Run() の戻り値を読む。
    /// </summary>
    public static class DuelRootMotionProbe
    {
        private const string VendorBodyFbx =
            "Assets/DuelSpike/Vendor/SchoolBaseballPlayer0001/School_Baseball_Player_0001.fbx";
        private const string BodyFbx = "Assets/DuelSpike/UBC/SuperheroMale.fbx";
        private const float TargetHeight = 1.75f;
        private const float Yaw = 270f;
        private const float Start = 1.75f;
        private const float Fade = 0.25f;
        private const float Step = 1f / 12f;
        private const int Steps = 15;      // 1.75 → 3.00

        private static GameObject _go;
        private static Animator _anim;
        private static PlayableGraph _graph;
        private static AnimationMixerPlayable _mixer;
        private static AnimationClipPlayable _pl;
        private static AnimationClipPlayable _fadeFrom;
        private static AnimationClip _clip;
        private static Vector3 _basePos;
        private static Quaternion _baseRot;

        public static string Run()
        {
            var sb = new StringBuilder();
            try
            {
                Build();
                float end = Start + Steps * Step;

                Seek(end);
                sb.Append("R(60fps seek to ").Append(end.ToString("F3")).Append(") ")
                  .Append(Fmt()).Append('\n');

                Seek(Start);
                for (int i = 0; i < Steps; i++)
                {
                    _graph.Evaluate(Step);
                }
                sb.Append("A(12fps no-fade)                 ").Append(Fmt()).Append('\n');

                Seek(Start);
                _fadeFrom = AnimationClipPlayable.Create(_graph, _clip);
                _fadeFrom.SetTime(4.5f);
                _fadeFrom.Pause();
                _mixer.ConnectInput(0, _fadeFrom, 0);
                _mixer.SetInputWeight(0, 1f);
                _mixer.SetInputWeight(1, 0f);
                float rem = Fade;
                for (int i = 0; i < Steps; i++)
                {
                    if (rem > 0f)
                    {
                        rem -= Step;
                        float u = Mathf.Clamp01(1f - rem / Fade);
                        float w = u * u * (3f - 2f * u);
                        _mixer.SetInputWeight(0, 1f - w);
                        _mixer.SetInputWeight(1, w);
                        // 実機（DuelStageRig.StepActor）はフェード終了で CancelFade する＝入力0を外す
                        if (rem <= 0f && CancelAtFadeEnd) Cancel();
                    }
                    _graph.Evaluate(Step);
                }
                sb.Append("B(12fps + crossfade)             ").Append(Fmt()).Append('\n');

                // C: フェード中だけ applyRootMotion を切る（＝退場側のルート姿勢に引っぱられない）。
                // 畳むときは motion off のまま Evaluate(0) して基準を素のポーズへ張り直してから戻す
                Seek(Start);
                _fadeFrom = AnimationClipPlayable.Create(_graph, _clip);
                _fadeFrom.SetTime(4.5f);
                _fadeFrom.Pause();
                _mixer.ConnectInput(0, _fadeFrom, 0);
                _mixer.SetInputWeight(0, 1f);
                _mixer.SetInputWeight(1, 0f);
                _anim.applyRootMotion = false;
                rem = Fade;
                for (int i = 0; i < Steps; i++)
                {
                    if (rem > 0f)
                    {
                        rem -= Step;
                        float u = Mathf.Clamp01(1f - rem / Fade);
                        float w = u * u * (3f - 2f * u);
                        _mixer.SetInputWeight(0, 1f - w);
                        _mixer.SetInputWeight(1, w);
                        if (rem <= 0f)
                        {
                            Cancel();
                            _graph.Evaluate(0f);            // motion off のまま基準を素のポーズへ
                            _anim.applyRootMotion = true;
                        }
                    }
                    _graph.Evaluate(Step);
                }
                sb.Append("C(crossfade + rootmotion凍結)     ").Append(Fmt()).Append('\n');
            }
            finally
            {
                if (_graph.IsValid()) _graph.Destroy();
                if (_go != null) Object.DestroyImmediate(_go);
            }
            return sb.ToString();
        }

        private static string Fmt()
        {
            var p = _go.transform.localPosition - _basePos;
            float yaw = Mathf.DeltaAngle(Yaw, _go.transform.localRotation.eulerAngles.y);
            return "dPos=(" + p.x.ToString("F3") + "," + p.z.ToString("F3") + ") |d|="
                + new Vector2(p.x, p.z).magnitude.ToString("F3") + "  dYaw=" + yaw.ToString("F1");
        }

        private static void Build()
        {
            var src = UnityEditor.AssetDatabase.LoadAssetAtPath<GameObject>(VendorBodyFbx)
                   ?? UnityEditor.AssetDatabase.LoadAssetAtPath<GameObject>(BodyFbx);
            _clip = KokoSim.Unity.Match.DuelStageRig.LoadClip(
                KokoSim.Unity.Match.DuelStageRig.PitcherClipName);
            _go = Object.Instantiate(src);
            _go.hideFlags = HideFlags.HideAndDontSave;
            var rs = _go.GetComponentsInChildren<Renderer>();
            var b = rs[0].bounds;
            foreach (var r in rs) b.Encapsulate(r.bounds);
            _go.transform.localScale = Vector3.one * (TargetHeight / Mathf.Max(0.01f, b.size.y));
            _go.transform.localPosition = Vector3.zero;
            _go.transform.localRotation = Quaternion.Euler(0f, Yaw, 0f);
            _basePos = _go.transform.localPosition;
            _baseRot = _go.transform.localRotation;

            _anim = _go.GetComponentInChildren<Animator>();
            _anim.applyRootMotion = true;
            _anim.cullingMode = AnimatorCullingMode.AlwaysAnimate;
            _graph = PlayableGraph.Create("rootprobe");
            var output = AnimationPlayableOutput.Create(_graph, "out", _anim);
            _mixer = AnimationMixerPlayable.Create(_graph, 2);
            _pl = AnimationClipPlayable.Create(_graph, _clip);
            _mixer.ConnectInput(1, _pl, 0);
            _mixer.SetInputWeight(1, 1f);
            output.SetSourcePlayable(_mixer);
        }

        /// <summary>フェード終了時に入力0を外すか（実機と同じ挙動＝true）。</summary>
        public static bool CancelAtFadeEnd = true;

        private static void Cancel()
        {
            if (_fadeFrom.IsValid()) { _mixer.DisconnectInput(0); _fadeFrom.Destroy(); }
            _mixer.SetInputWeight(1, 1f);
        }

        private static void Seek(float t)
        {
            Cancel();
            _go.transform.localPosition = _basePos;
            _go.transform.localRotation = _baseRot;
            _anim.applyRootMotion = false;
            _pl.SetTime(0);
            _graph.Evaluate(0f);
            _anim.applyRootMotion = true;
            int n = Mathf.Max(1, Mathf.CeilToInt(t * 60f));
            float dt = t / n;
            for (int i = 0; i < n; i++) _graph.Evaluate(dt);
        }
    }
}
#endif
