using System.Globalization;
using System.IO;
using System.Text;
using UnityEngine;
using UnityEngine.Animations;
using UnityEngine.Playables;

namespace KokoSim.EditorTools
{
    /// <summary>
    /// design-18 §7-3 モーション検証スパイク（第1弾: 静止コマ）。
    /// AAパック（Assets/Animo）のHumanoidクリップをCC0素体（Quaternius Animated Human）へ
    /// リターゲットし、確定構図R（§7-1）で投球フォームのコマシートを撮る。
    ///
    /// Edit mode完結・Play不要。サンプリングは PlayableGraph.Evaluate（AnimationModeより
    /// Humanoidリターゲットが確実）。シーンはDuelStageComposerの一時ルート運用に相乗り。
    ///
    /// 使い方（MCP execute_code から）: KokoSim.EditorTools.DuelMotionSpike.RunPoseSheet();
    /// 出力: out/duel-stage/motion/pose_*.png
    /// </summary>
    public static class DuelMotionSpike
    {
        // 素体はUBC Superheroが正（§7-5・2026-07-26監督選定）。旧Animated Humanは比較用に残置
        private const string BodyFbx = "Assets/DuelSpike/UBC/SuperheroMale.fbx";

        /// <summary>§7-5 素体比較用: 非nullならSpawnBodyがこちらのFBXを使う（例: Assets/DuelSpike/AnimatedHuman/AnimatedHuman.fbx）。</summary>
        public static string BodyOverride;
        // クリップ供給元は DuelStageRig.ClipFbxSources に一本化（FindClip 参照）
        private const float TargetHeight = 1.75f;

        // AAセットポジション投球クリップ 6.00s（2026-07-29差し替え）の実測。正規化時刻＝秒÷6.00:
        // 0.000=セット静止 / 0.292=DeliveryStartTime(1.75s・実際に再生を始める切り口) /
        // 0.350=足上げ頂点(2.1s) / 0.450=踏み込み(2.7s) / 0.478=着地(2.87s) /
        // 0.508=リリース(3.05s・右手速度ピーク15.3m/s) / 0.533=直後 / 0.600=フォロー / 0.758=打ち切り(4.55s)。
        // 振りかぶり区間（0.0〜0.292）は本番で再生しないのでコマを置かない
        private static readonly float[] PitchTimes = { 0.000f, 0.292f, 0.350f, 0.450f, 0.478f, 0.508f, 0.533f, 0.600f, 0.758f };

        public static string RunPoseSheet()
        {
            // 注視点y=0.9が正（2026-07-26改定・監督選定。design-18 §7-1）
            return RunPoseSheet(0.9f, "r");
        }

        public static string RunPoseSheet(float aimY, string tag)
        {
            try
            {
                DuelStageComposer.Cleanup();
                DuelStageComposer.BuildStage();
                return RenderPoseSheet(aimY, tag);
            }
            finally
            {
                DuelStageComposer.Cleanup();
            }
        }

        private static string RenderPoseSheet(float aimY, string tag)
        {
            int layer = LayerMask.NameToLayer(DuelStageComposer.LayerName);
            var outDir = Path.Combine(Directory.GetParent(Application.dataPath).FullName, "out/duel-stage/motion");
            Directory.CreateDirectory(outDir);

            SpawnPose.Clear();
            AnkleOffset.Clear();
            var root = GameObject.Find(DuelStageComposer.RootName);
            foreach (var proxy in new[] { "pitcher", "batter", "catcher", "umpire" })
            {
                var t = root.transform.Find(proxy);
                if (t != null) t.gameObject.SetActive(false);
            }

            // 素体4体（色分けはプロキシ踏襲）。yawはクリップごとの素の向き実測（RunFacingProbe）から:
            // 投手（自作Rokoko）も脇3クリップ（打者/捕手/審判/スイング・購入AA）も素で+Z向き
            var pitcher = SpawnBody(root, "body-pitcher",
                new Vector3(0f, DuelStageComposer.MoundHeight, DuelStageComposer.MoundDistance), KokoSim.Unity.Match.DuelStageRig.PitcherYaw,
                new Color(0.92f, 0.92f, 0.90f)); // +Z→−Z（本塁へ投げ下ろす）
            var batter = SpawnBody(root, "body-batter", new Vector3(-0.9f, 0f, 0f), 90f, new Color(0.16f, 0.20f, 0.38f)); // +Z→+X（右打者・本塁向き）
            var catcher = SpawnBody(root, "body-catcher", new Vector3(0f, 0f, -1.0f), 0f, new Color(0.25f, 0.27f, 0.30f)); // 素のまま+Z（投手向き）
            var umpire = SpawnBody(root, "body-umpire", new Vector3(-0.35f, 0f, -1.7f), 0f, new Color(0.12f, 0.12f, 0.13f));

            var pitchClip = FindClip(KokoSim.Unity.Match.DuelStageRig.PitcherClipName);
            var batClip = FindClip("AA_Baseball_Batter_Idle_Loop");
            var catClip = FindClip("AA_Baseball_Catcher_Idle");
            var umpClip = FindClip("AA_Baseball_Umpire_Crouch_Idle");
            if (pitchClip == null || batClip == null || catClip == null || umpClip == null)
                return "clip not found: " + (pitchClip == null) + (batClip == null) + (catClip == null) + (umpClip == null);

            // 脇役は1回だけポーズを打って接地補正
            Sample(batter, batClip, 0.30f); GroundFix(batter, 0f);
            Sample(catcher, catClip, 0.50f); GroundFix(catcher, 0f);
            Sample(umpire, umpClip, 0.50f); GroundFix(umpire, 0f);



            var camGo = new GameObject("~DuelStageCamera") { hideFlags = HideFlags.DontSave };
            var cam = camGo.AddComponent<Camera>();
            cam.cullingMask = 1 << layer;
            cam.clearFlags = CameraClearFlags.SolidColor;
            cam.backgroundColor = new Color(0.55f, 0.78f, 0.90f);
            cam.nearClipPlane = 0.1f;
            cam.farClipPlane = 200f;
            // aimY 変種は旧R構図の選定用だった。採用構図が R-comp60 になった現在は共通カメラを使う
            ApplyAdoptedCamera(cam);

            var rt = new RenderTexture(480, 270, 24, RenderTextureFormat.ARGB32) { filterMode = FilterMode.Point };
            rt.Create();
            var big = new RenderTexture(1920, 1080, 0, RenderTextureFormat.ARGB32);
            big.Create();

            var sb = new StringBuilder();
            try
            {
                cam.targetTexture = rt;
                foreach (var t in PitchTimes)
                {
                    Sample(pitcher, pitchClip, t);
                    GroundFix(pitcher, DuelStageComposer.MoundHeight); // ルートモーションで降下するためコマ毎に接地

                    var pAnim = pitcher.GetComponentInChildren<Animator>();
                    var pHips = pAnim.GetBoneTransform(HumanBodyBones.Hips);
                    var pLf = pAnim.GetBoneTransform(HumanBodyBones.LeftFoot);
                    var pRf = pAnim.GetBoneTransform(HumanBodyBones.RightFoot);
                    sb.Append("  diag t=" + t.ToString("F2", CultureInfo.InvariantCulture)
                        + " rootZ=" + pitcher.transform.position.z.ToString("F2", CultureInfo.InvariantCulture)
                        + " hipsY=" + pHips.position.y.ToString("F2", CultureInfo.InvariantCulture)
                        + " lfY=" + pLf.position.y.ToString("F2", CultureInfo.InvariantCulture)
                        + " rfY=" + pRf.position.y.ToString("F2", CultureInfo.InvariantCulture) + "\n");

                    int pitPx = DuelStageComposer.MeasureScreenHeight(cam, pitcher);
                    DuelStageComposer.Render(cam, rt);

                    var prev = RenderTexture.active;
                    Graphics.Blit(rt, big);
                    RenderTexture.active = big;
                    var tex = new Texture2D(1920, 1080, TextureFormat.RGBA32, false);
                    tex.ReadPixels(new Rect(0, 0, 1920, 1080), 0, 0);
                    tex.Apply();
                    RenderTexture.active = prev;

                    var file = string.Format(CultureInfo.InvariantCulture, "pose_{0}_t{1:000}_pit{2}px.png",
                        tag, Mathf.RoundToInt(t * 100f), pitPx);
                    File.WriteAllBytes(Path.Combine(outDir, file), tex.EncodeToPNG());
                    Object.DestroyImmediate(tex);
                    sb.AppendLine(file);
                }
            }
            finally
            {
                cam.targetTexture = null;
                rt.Release(); Object.DestroyImmediate(rt);
                big.Release(); Object.DestroyImmediate(big);
                Object.DestroyImmediate(camGo);
            }

            Debug.Log("[DuelMotionSpike] pose sheet -> " + outDir + "\n" + sb);
            return sb.ToString();
        }

        /// <summary>
        /// §4.5-② 時間量子化の検証素材: 投球の足上げ→リリース→フォロー（クリップ内2.4秒窓）を
        /// 8/12/15fps でコマ撮りし、RT 480×270 ネイティブ解像度PNGで書き出す（Web側でパラパラ再生）。
        /// カメラはフルレート相当（固定）・キャラのポーズだけが量子化される。
        /// 出力: out/duel-stage/motion/quant/q<fps>_<i>.png
        /// </summary>
        public static string RunQuantStrips()
        {
            try
            {
                DuelStageComposer.Cleanup();
                DuelStageComposer.BuildStage();
                return RenderQuantStrips();
            }
            finally
            {
                DuelStageComposer.Cleanup();
            }
        }

        private static string RenderQuantStrips()
        {
            int layer = LayerMask.NameToLayer(DuelStageComposer.LayerName);
            var outDir = Path.Combine(Directory.GetParent(Application.dataPath).FullName, "out/duel-stage/motion/quant");
            Directory.CreateDirectory(outDir);

            SpawnPose.Clear();
            AnkleOffset.Clear();
            var root = GameObject.Find(DuelStageComposer.RootName);
            foreach (var proxy in new[] { "pitcher", "batter", "catcher", "umpire" })
            {
                var t = root.transform.Find(proxy);
                if (t != null) t.gameObject.SetActive(false);
            }

            var pitcher = SpawnBody(root, "body-pitcher",
                new Vector3(0f, DuelStageComposer.MoundHeight, DuelStageComposer.MoundDistance), KokoSim.Unity.Match.DuelStageRig.PitcherYaw,
                new Color(0.92f, 0.92f, 0.90f));
            var batter = SpawnBody(root, "body-batter", new Vector3(-0.9f, 0f, 0f), 90f, new Color(0.16f, 0.20f, 0.38f));
            var catcher = SpawnBody(root, "body-catcher", new Vector3(0f, 0f, -1.0f), 0f, new Color(0.25f, 0.27f, 0.30f));
            var umpire = SpawnBody(root, "body-umpire", new Vector3(-0.35f, 0f, -1.7f), 0f, new Color(0.12f, 0.12f, 0.13f));

            var pitchClip = FindClip(KokoSim.Unity.Match.DuelStageRig.PitcherClipName);
            Sample(batter, FindClip("AA_Baseball_Batter_Idle_Loop"), 0.30f); GroundFix(batter, 0f);
            Sample(catcher, FindClip("AA_Baseball_Catcher_Idle"), 0.50f); GroundFix(catcher, 0f);
            Sample(umpire, FindClip("AA_Baseball_Umpire_Crouch_Idle"), 0.50f); GroundFix(umpire, 0f);

            var camGo = new GameObject("~DuelStageCamera") { hideFlags = HideFlags.DontSave };
            var cam = camGo.AddComponent<Camera>();
            cam.cullingMask = 1 << layer;
            cam.clearFlags = CameraClearFlags.SolidColor;
            cam.backgroundColor = new Color(0.55f, 0.78f, 0.90f);
            cam.nearClipPlane = 0.1f;
            cam.farClipPlane = 200f;
            ApplyAdoptedCamera(cam);

            const float WindowStartSec = 1.8f; // t=0.30（足上げ前）
            const float WindowLenSec = 2.4f;   // →t=0.70（投げ終わり）
            int[] fpsSet = { 8, 12, 15 };

            var rt = new RenderTexture(480, 270, 24, RenderTextureFormat.ARGB32) { filterMode = FilterMode.Point };
            rt.Create();

            var sb = new StringBuilder();
            try
            {
                cam.targetTexture = rt;
                foreach (var fps in fpsSet)
                {
                    int frames = Mathf.CeilToInt(WindowLenSec * fps) + 1;
                    for (int i = 0; i < frames; i++)
                    {
                        float tSec = WindowStartSec + (float)i / fps;
                        Sample(pitcher, pitchClip, tSec / pitchClip.length);
                        GroundFix(pitcher, DuelStageComposer.MoundHeight);

                        DuelStageComposer.Render(cam, rt);
                        var prev = RenderTexture.active;
                        RenderTexture.active = rt;
                        var tex = new Texture2D(480, 270, TextureFormat.RGBA32, false);
                        tex.ReadPixels(new Rect(0, 0, 480, 270), 0, 0);
                        tex.Apply();
                        RenderTexture.active = prev;

                        var file = string.Format(CultureInfo.InvariantCulture, "q{0}_{1:000}.png", fps, i);
                        File.WriteAllBytes(Path.Combine(outDir, file), tex.EncodeToPNG());
                        Object.DestroyImmediate(tex);
                    }
                    sb.AppendLine("fps=" + fps + " frames=" + frames);
                }
            }
            finally
            {
                cam.targetTexture = null;
                rt.Release(); Object.DestroyImmediate(rt);
                Object.DestroyImmediate(camGo);
            }

            Debug.Log("[DuelMotionSpike] quant strips -> " + outDir + "\n" + sb);
            return sb.ToString();
        }

        /// <summary>配置・向きデバッグ用の真上俯瞰（画面上=+z=投手側）。pose sheetと同じ配置で1枚だけ撮る。</summary>
        public static string RunTopDebug(float pitchT)
        {
            try
            {
                DuelStageComposer.Cleanup();
                DuelStageComposer.BuildStage();

                SpawnPose.Clear();
                AnkleOffset.Clear();
                var root = GameObject.Find(DuelStageComposer.RootName);
                foreach (var proxy in new[] { "pitcher", "batter", "catcher", "umpire" })
                {
                    var t = root.transform.Find(proxy);
                    if (t != null) t.gameObject.SetActive(false);
                }

                var pitcher = SpawnBody(root, "body-pitcher",
                    new Vector3(0f, DuelStageComposer.MoundHeight, DuelStageComposer.MoundDistance), KokoSim.Unity.Match.DuelStageRig.PitcherYaw,
                    new Color(0.92f, 0.92f, 0.90f));
                var batter = SpawnBody(root, "body-batter", new Vector3(-0.9f, 0f, 0f), 90f, new Color(0.16f, 0.20f, 0.38f));
                var catcher = SpawnBody(root, "body-catcher", new Vector3(0f, 0f, -1.0f), 0f, new Color(0.25f, 0.27f, 0.30f));
                var umpire = SpawnBody(root, "body-umpire", new Vector3(-0.35f, 0f, -1.7f), 0f, new Color(0.12f, 0.12f, 0.13f));

                Sample(batter, FindClip("AA_Baseball_Batter_Idle_Loop"), 0.30f); GroundFix(batter, 0f);
                Sample(catcher, FindClip("AA_Baseball_Catcher_Idle"), 0.50f); GroundFix(catcher, 0f);
                Sample(umpire, FindClip("AA_Baseball_Umpire_Crouch_Idle"), 0.50f); GroundFix(umpire, 0f);
                Sample(pitcher, FindClip(KokoSim.Unity.Match.DuelStageRig.PitcherClipName), pitchT);
                GroundFix(pitcher, DuelStageComposer.MoundHeight);

                var camGo = new GameObject("~topCam") { hideFlags = HideFlags.DontSave };
                var cam = camGo.AddComponent<Camera>();
                cam.cullingMask = 1 << LayerMask.NameToLayer(DuelStageComposer.LayerName);
                cam.orthographic = true;
                cam.orthographicSize = 11f;   // 本塁(z=0)〜マウンド(z=18.44)を1枚に収める（向きデバッグ用）
                cam.clearFlags = CameraClearFlags.SolidColor;
                cam.backgroundColor = new Color(0.2f, 0.2f, 0.2f);
                cam.transform.position = new Vector3(0f, 30f, 9f);
                cam.transform.rotation = Quaternion.Euler(90f, 0f, 0f);
                cam.nearClipPlane = 0.1f;
                cam.farClipPlane = 100f;

                var rt = new RenderTexture(800, 800, 24); rt.Create();
                cam.targetTexture = rt;
                Render(cam, rt);
                var prev = RenderTexture.active;
                RenderTexture.active = rt;
                var tex = new Texture2D(800, 800, TextureFormat.RGBA32, false);
                tex.ReadPixels(new Rect(0, 0, 800, 800), 0, 0);
                tex.Apply();
                RenderTexture.active = prev;
                cam.targetTexture = null;
                var outPath = Path.Combine(Directory.GetParent(Application.dataPath).FullName, "out/duel-stage/motion/debug_top.png");
                File.WriteAllBytes(outPath, tex.EncodeToPNG());
                Object.DestroyImmediate(tex);
                rt.Release(); Object.DestroyImmediate(rt);
                Object.DestroyImmediate(camGo);
                return outPath;
            }
            finally
            {
                DuelStageComposer.Cleanup();
            }
        }

        /// <summary>
        /// 各クリップの「素の胸の向き」実測（向き不一致調査 2026-07-26）。yaw=0で1体スポーン→代表時刻を
        /// サンプル→肩線から胸法線を出し、狙いの世界向きへ合わせる必要yawを返す。
        /// 「クリップ前方向=ローカル−X」は投手ルートモーションの実測値であり、脇3クリップ（打者/捕手/審判）に
        /// 同じ規約が当てはまるかをここで検証する。
        /// </summary>
        public static string RunFacingProbe()
        {
            try
            {
                DuelStageComposer.Cleanup();
                DuelStageComposer.BuildStage();
                SpawnPose.Clear();
                AnkleOffset.Clear();
                var root = GameObject.Find(DuelStageComposer.RootName);
                foreach (var proxy in new[] { "pitcher", "batter", "catcher", "umpire" })
                {
                    var t = root.transform.Find(proxy);
                    if (t != null) t.gameObject.SetActive(false);
                }

                var jobs = new[]
                {
                    new { name = "pitcher", clip = KokoSim.Unity.Match.DuelStageRig.PitcherClipName, t = 0.15f, desired = new Vector3(0f, 0f, -1f) },
                    new { name = "batter",  clip = "AA_Baseball_Batter_Idle_Loop",        t = 0.30f, desired = new Vector3(1f, 0f, 0f) },
                    new { name = "swing",   clip = "AA_Baseball_Batter_Swing_Type01_Hitting", t = 0.10f, desired = new Vector3(1f, 0f, 0f) },
                    // 自作Rokoko打撃（構え→スイング→走り出し）。構えの区間で素の向きを読む
                    new { name = "bat-rkk", clip = "Batter_Batting_01", t = 0.10f, desired = new Vector3(1f, 0f, 0f) },
                    // Mixamo「Baseball Strike」。構えの区間（〜0.9s）で素の向きを読む
                    new { name = "bat-mxm", clip = "Batter_Mixamo_Strike_01", t = 0.10f, desired = new Vector3(1f, 0f, 0f) },
                    new { name = "catcher", clip = "AA_Baseball_Catcher_Idle",            t = 0.50f, desired = new Vector3(0f, 0f, 1f) },
                    new { name = "umpire",  clip = "AA_Baseball_Umpire_Crouch_Idle",      t = 0.50f, desired = new Vector3(0f, 0f, 1f) },
                    // バント構え一式（issue #514）。squaring＝投手正対なので、狙いは打撃系(+X)でなく
                    // 捕手・審判と同じ「投手を向く」+Z。移行クリップは始点(idleから繋ぐ側)と終点
                    // (ループへ繋ぐ側)の両方を採り、構え全体を1つの定数オフセットで賄えるか確認する
                    new { name = "bunt-in-start", clip = KokoSim.Unity.Match.DuelStageRig.BatterIdleToBuntClipName, t = 0.05f, desired = new Vector3(0f, 0f, 1f) },
                    new { name = "bunt-in-end",   clip = KokoSim.Unity.Match.DuelStageRig.BatterIdleToBuntClipName, t = 0.95f, desired = new Vector3(0f, 0f, 1f) },
                    new { name = "bunt-loop",     clip = KokoSim.Unity.Match.DuelStageRig.BatterBuntLoopClipName,   t = 0.50f, desired = new Vector3(0f, 0f, 1f) },
                    new { name = "bunt-hit",      clip = KokoSim.Unity.Match.DuelStageRig.BatterBuntHitClipName,   t = 0.10f, desired = new Vector3(0f, 0f, 1f) },
                    new { name = "bunt-miss",     clip = KokoSim.Unity.Match.DuelStageRig.BatterBuntMissClipName,  t = 0.10f, desired = new Vector3(0f, 0f, 1f) },
                };

                var sb = new StringBuilder();
                foreach (var j in jobs)
                {
                    var clip = FindClip(j.clip);
                    if (clip == null)
                    {
                        // 一部クリップ（Mixamo Adobe素材等）はgitignoreで環境によって不在＝
                        // 1本欠けただけで残りの実測ごと落とさない（NullReferenceExceptionでbail outしない）
                        sb.AppendLine(j.name + ": clip not found (" + j.clip + ")");
                        continue;
                    }
                    var body = SpawnBody(root, "probe-" + j.name, Vector3.zero, 0f, Color.white);
                    Sample(body, clip, j.t);
                    var anim = body.GetComponentInChildren<Animator>();
                    var ra = anim.GetBoneTransform(HumanBodyBones.RightUpperArm).position;
                    var la = anim.GetBoneTransform(HumanBodyBones.LeftUpperArm).position;
                    var facing = Vector3.Cross(ra - la, Vector3.up);
                    facing.y = 0f; facing.Normalize();
                    var hips = anim.GetBoneTransform(HumanBodyBones.Hips).rotation * Vector3.forward;
                    hips.y = 0f; hips.Normalize();
                    sb.AppendLine(string.Format(CultureInfo.InvariantCulture,
                        "{0}: chest=({1:F2},{2:F2}) hips=({3:F2},{4:F2}) -> suggestYaw chest={5:F0} hips={6:F0}",
                        j.name, facing.x, facing.z, hips.x, hips.z,
                        Vector3.SignedAngle(facing, j.desired, Vector3.up),
                        Vector3.SignedAngle(hips, j.desired, Vector3.up)));
                    Object.DestroyImmediate(body);
                }
                Debug.Log("[DuelMotionSpike] facing probe\n" + sb);
                return sb.ToString();
            }
            finally
            {
                DuelStageComposer.Cleanup();
            }
        }

        /// <summary>
        /// 構図バリアント一括レンダ（2026-07-26 監督指摘「上の空白が気になる・打者が小さい」の選定素材）。
        /// 上の空白の正体は地面プレーンが尽きた先の背景色（実ゲームではバックネット/スタンドが立つ領域）。
        /// 対処レバーは3つで、組み合わせを実写で並べる:
        ///   ①簡易バックネット書き割り（構図不変・空白だけ埋める）
        ///   ②ズーム詰め（FOV縮小＋注視点下げ＝全員大きく）
        ///   ③望遠圧縮（カメラ後退＋FOV縮小＝投手/打者の距離差を圧縮して打者を相対的に大きく）
        /// カメラは現行Rの俯角・横角を保ったまま後退量から高さ/横位置を逆算する。
        /// 出力: out/duel-stage/frame/frame_[name]_t[時刻].png（480×270→1920×1080 Point拡大）
        /// </summary>
        public static string RunFrameVariants()
        {
            try
            {
                DuelStageComposer.Cleanup();
                DuelStageComposer.BuildStage();

                int layer = LayerMask.NameToLayer(DuelStageComposer.LayerName);
                var outDir = Path.Combine(Directory.GetParent(Application.dataPath).FullName, "out/duel-stage/frame");
                Directory.CreateDirectory(outDir);

                SpawnPose.Clear();
                AnkleOffset.Clear();
                var root = GameObject.Find(DuelStageComposer.RootName);
                foreach (var proxy in new[] { "pitcher", "batter", "catcher", "umpire" })
                {
                    var t = root.transform.Find(proxy);
                    if (t != null) t.gameObject.SetActive(false);
                }

                var pitcher = SpawnBody(root, "body-pitcher",
                    new Vector3(0f, DuelStageComposer.MoundHeight, DuelStageComposer.MoundDistance), KokoSim.Unity.Match.DuelStageRig.PitcherYaw,
                    new Color(0.92f, 0.92f, 0.90f));
                var batter = SpawnBody(root, "body-batter", new Vector3(-0.9f, 0f, 0f), 90f, new Color(0.16f, 0.20f, 0.38f));
                var catcher = SpawnBody(root, "body-catcher", new Vector3(0f, 0f, -1.0f), 0f, new Color(0.25f, 0.27f, 0.30f));
                var umpire = SpawnBody(root, "body-umpire", new Vector3(-0.35f, 0f, -1.7f), 0f, new Color(0.12f, 0.12f, 0.13f));
                var bat = SpawnBat(root);
                AttachMitt(catcher);

                Sample(batter, FindClip("AA_Baseball_Batter_Idle_Loop"), 0.30f); GroundFix(batter, 0f);
                AlignBat(batter, bat);
                Sample(catcher, FindClip("AA_Baseball_Catcher_Idle"), 0.50f); GroundFix(catcher, 0f);
                Sample(umpire, FindClip("AA_Baseball_Umpire_Crouch_Idle"), 0.50f); GroundFix(umpire, 0f);

                // バックネット書き割り（単色の暗インク壁1枚）。asis以外の変種で表示。
                // 2トーン（壁+下段フェンス帯）は望遠圧縮でパッドが画面高3割の巨大縞に化けるため不採用（2026-07-26実写）。
                // BuildStage恒常の壁とは別に自前で出し分けるので、恒常側は消しておく
                var stageWall = root.transform.Find("backstop-wall");
                if (stageWall != null) stageWall.gameObject.SetActive(false);
                var wall = Primitive(root, PrimitiveType.Cube, "variant-backstop",
                    new Vector3(0f, 4f, -16f), new Vector3(34f, 8f, 0.5f), new Color(0.12f, 0.20f, 0.16f), layer);

                // 現行R: 俯角 atan((5.0-0.9)/52.44)=4.47°・横角 atan(3.5/52.44)=3.82° を全変種で維持
                const float slopeV = 0.0782f;
                const float slopeH = 0.0668f;
                var variants = new[]
                {
                    new { name = "asis",    d = 35f,  fov = 5.5f,  aimY = 0.9f, backstop = false },
                    new { name = "bs",      d = 35f,  fov = 5.5f,  aimY = 0.9f, backstop = true },
                    new { name = "tight",   d = 35f,  fov = 4.3f,  aimY = 0.7f, backstop = true },
                    new { name = "comp60",  d = 60f,  fov = 2.9f,  aimY = 0.8f, backstop = true },
                    new { name = "comp100", d = 100f, fov = 1.85f, aimY = 0.8f, backstop = true },
                };
                var pitchTimes = new[] { 0.35f, 0.53f };

                var camGo = new GameObject("~DuelStageCamera") { hideFlags = HideFlags.DontSave };
                var cam = camGo.AddComponent<Camera>();
                cam.cullingMask = 1 << layer;
                cam.clearFlags = CameraClearFlags.SolidColor;
                cam.backgroundColor = new Color(0.55f, 0.78f, 0.90f);
                cam.nearClipPlane = 0.1f;
                cam.farClipPlane = 400f;

                var rt = new RenderTexture(480, 270, 24, RenderTextureFormat.ARGB32) { filterMode = FilterMode.Point };
                rt.Create();
                var big = new RenderTexture(1920, 1080, 0, RenderTextureFormat.ARGB32);
                big.Create();

                var sb = new StringBuilder();
                try
                {
                    cam.targetTexture = rt;
                    foreach (var v in variants)
                    {
                        wall.SetActive(v.backstop);
                        float camZ = DuelStageComposer.MoundDistance + v.d;
                        float dz = camZ - 1f;
                        cam.transform.position = new Vector3(-dz * slopeH, v.aimY + dz * slopeV, camZ);
                        cam.transform.LookAt(new Vector3(0f, v.aimY, 1f));
                        cam.fieldOfView = v.fov;

                        foreach (var t in pitchTimes)
                        {
                            Sample(pitcher, FindClip(KokoSim.Unity.Match.DuelStageRig.PitcherClipName), t);
                            GroundFix(pitcher, DuelStageComposer.MoundHeight);
                            int pitPx = DuelStageComposer.MeasureScreenHeight(cam, pitcher);
                            int batPx = DuelStageComposer.MeasureScreenHeight(cam, batter);
                            DuelStageComposer.Render(cam, rt);

                            var prev = RenderTexture.active;
                            Graphics.Blit(rt, big);
                            RenderTexture.active = big;
                            var tex = new Texture2D(1920, 1080, TextureFormat.RGBA32, false);
                            tex.ReadPixels(new Rect(0, 0, 1920, 1080), 0, 0);
                            tex.Apply();
                            RenderTexture.active = prev;

                            var file = string.Format(CultureInfo.InvariantCulture,
                                "frame_{0}_t{1:000}.png", v.name, Mathf.RoundToInt(t * 100f));
                            File.WriteAllBytes(Path.Combine(outDir, file), tex.EncodeToPNG());
                            Object.DestroyImmediate(tex);
                            sb.AppendLine(string.Format(CultureInfo.InvariantCulture,
                                "{0} pit={1}px bat={2}px ratio={3:F2}", file, pitPx, batPx, batPx / (float)pitPx));
                        }
                    }
                }
                finally
                {
                    cam.targetTexture = null;
                    rt.Release(); Object.DestroyImmediate(rt);
                    big.Release(); Object.DestroyImmediate(big);
                    Object.DestroyImmediate(camGo);
                }

                Debug.Log("[DuelMotionSpike] frame variants -> " + outDir + "\n" + sb);
                return sb.ToString();
            }
            finally
            {
                DuelStageComposer.Cleanup();
            }
        }

        private static GameObject Primitive(GameObject root, PrimitiveType type, string name,
            Vector3 pos, Vector3 scale, Color color, int layer)
        {
            var go = GameObject.CreatePrimitive(type);
            go.name = name;
            Object.DestroyImmediate(go.GetComponent<Collider>());
            go.transform.SetParent(root.transform, false);
            go.transform.localPosition = pos;
            go.transform.localScale = scale;
            var mat = new Material(Shader.Find("KokoSim/DuelStageProxyLambert")) { hideFlags = HideFlags.DontSave };
            mat.SetColor("_BaseColor", color);
            go.GetComponent<MeshRenderer>().sharedMaterial = mat;
            go.layer = layer;
            return go;
        }

        /// <summary>
        /// 投球クリップの向き精密実測（§7-2で判明した「リリースが画面左手前へ飛ぶ」調査用）。
        /// yaw=0でスポーンし、複数時刻の胸向きと「t=0からのルート移動方向」を分けて測る。
        /// 投球はクリップ内回転（振りかぶり→リリースで体が回る）を含むため、単一時刻の胸向きでは
        /// yawを決められない＝リリース時の胸向きと踏み込みの移動方向の両方で決める。
        /// </summary>
        public static string RunPitcherProbe()
        {
            try
            {
                DuelStageComposer.Cleanup();
                DuelStageComposer.BuildStage();
                SpawnPose.Clear();
                AnkleOffset.Clear();
                var root = GameObject.Find(DuelStageComposer.RootName);
                foreach (var proxy in new[] { "pitcher", "batter", "catcher", "umpire" })
                {
                    var t = root.transform.Find(proxy);
                    if (t != null) t.gameObject.SetActive(false);
                }

                var clip = FindClip(KokoSim.Unity.Match.DuelStageRig.PitcherClipName);
                var sb = new StringBuilder();
                var body0 = SpawnBody(root, "probe-p0", Vector3.zero, 0f, Color.white);
                Sample(body0, clip, 0.001f);
                var p0 = body0.transform.position;
                Object.DestroyImmediate(body0);

                Vector3 prevHand = Vector3.zero;
                float prevT = -1f;
                foreach (var t in new[] { 0.15f, 0.35f, 0.45f, 0.48f, 0.50f, 0.52f, 0.54f, 0.56f, 0.58f, 0.60f, 0.62f, 0.65f, 0.70f, 0.90f })
                {
                    var body = SpawnBody(root, "probe-p", Vector3.zero, 0f, Color.white);
                    Sample(body, clip, t);
                    var anim = body.GetComponentInChildren<Animator>();
                    var ra = anim.GetBoneTransform(HumanBodyBones.RightUpperArm).position;
                    var la = anim.GetBoneTransform(HumanBodyBones.LeftUpperArm).position;
                    var hand = anim.GetBoneTransform(HumanBodyBones.RightHand).position;
                    var facing = Vector3.Cross(ra - la, Vector3.up);
                    facing.y = 0f; facing.Normalize();
                    var disp = body.transform.position - p0;
                    disp.y = 0f;
                    // 右手の平均速度（前サンプル区間）＝リリース瞬間の推定（速度ピーク直後が手離れ）
                    float speed = prevT >= 0f ? (hand - prevHand).magnitude / ((t - prevT) * clip.length) : 0f;
                    sb.AppendLine(string.Format(CultureInfo.InvariantCulture,
                        "t={0:F2} chest=({1:F2},{2:F2}) disp=({3:F2},{4:F2}) hand=({5:F2},{6:F2},{7:F2}) v={8:F1}m/s",
                        t, facing.x, facing.z, disp.x, disp.z, hand.x, hand.y, hand.z, speed));
                    prevHand = hand; prevT = t;
                    Object.DestroyImmediate(body);
                }
                Debug.Log("[DuelMotionSpike] pitcher probe\n" + sb);
                return sb.ToString();
            }
            finally
            {
                DuelStageComposer.Cleanup();
            }
        }

        /// <summary>
        /// §7-2第2弾（結果分岐）のアンカー実測: 空振り/見逃し/死球（打者・yaw90）と捕球（捕手・yaw0）の
        /// 各クリップについて、追跡ボーン（打者=右手/捕手=左手ミット）の位置と区間速度を出力する。
        /// スイング面通過・捕球はいずれも追跡ボーン速度のピーク近傍で読む（投手リリースと同じ流儀）。
        /// </summary>
        public static string RunReactionProbe()
        {
            try
            {
                DuelStageComposer.Cleanup();
                DuelStageComposer.BuildStage();
                SpawnPose.Clear();
                AnkleOffset.Clear();
                var root = GameObject.Find(DuelStageComposer.RootName);
                foreach (var proxy in new[] { "pitcher", "batter", "catcher", "umpire" })
                {
                    var t = root.transform.Find(proxy);
                    if (t != null) t.gameObject.SetActive(false);
                }

                var jobs = new[]
                {
                    new { clip = "AA_Baseball_Batter_Swing_Type01_Miss",  yaw = 90f, bone = HumanBodyBones.RightHand },
                    new { clip = "AA_Baseball_Batter_Strike",             yaw = 90f, bone = HumanBodyBones.Head },
                    new { clip = "AA_Baseball_Batter_Hit_ByPitch_Type01", yaw = 90f, bone = HumanBodyBones.Head },
                    new { clip = "AA_Baseball_Catcher_Catching_C",        yaw = 0f,  bone = HumanBodyBones.LeftHand },
                };
                var times = new[] { 0.05f, 0.10f, 0.15f, 0.20f, 0.25f, 0.30f, 0.35f, 0.40f, 0.45f, 0.50f, 0.60f, 0.70f, 0.85f };

                var sb = new StringBuilder();
                foreach (var j in jobs)
                {
                    var clip = FindClip(j.clip);
                    sb.AppendLine(j.clip + " len=" + clip.length.ToString("F2", CultureInfo.InvariantCulture));
                    Vector3 prev = Vector3.zero;
                    float prevT = -1f;
                    foreach (var t in times)
                    {
                        var body = SpawnBody(root, "probe-r", Vector3.zero, j.yaw, Color.white);
                        Sample(body, clip, t);
                        var anim = body.GetComponentInChildren<Animator>();
                        var p = anim.GetBoneTransform(j.bone).position;
                        float speed = prevT >= 0f ? (p - prev).magnitude / ((t - prevT) * clip.length) : 0f;
                        sb.AppendLine(string.Format(CultureInfo.InvariantCulture,
                            "  t={0:F2}({1:F2}s) bone=({2:F2},{3:F2},{4:F2}) v={5:F1}m/s",
                            t, t * clip.length, p.x, p.y, p.z, speed));
                        prev = p; prevT = t;
                        Object.DestroyImmediate(body);
                    }
                }
                Debug.Log("[DuelMotionSpike] reaction probe\n" + sb);
                return sb.ToString();
            }
            finally
            {
                DuelStageComposer.Cleanup();
            }
        }

        /// <summary>
        /// 任意クリップの指定ボーン速度カーブを実測する汎用プローブ（クリップ差し替え時のアンカー再測定用）。
        /// 速度ピーク＝スイングの面通過/当たり、速度が落ち着く点＝構え直り、の目安になる。
        /// 例: RunClipSpeedProbe("AA_Baseball_Batter_Swing_Type02_Miss", "RightHand", 0.1f)
        /// </summary>
        public static string RunClipSpeedProbe(string clipName, string boneName, float stepSec)
        {
            try
            {
                DuelStageComposer.Cleanup();
                DuelStageComposer.BuildStage();
                SpawnPose.Clear();
                AnkleOffset.Clear();
                var root = GameObject.Find(DuelStageComposer.RootName);
                foreach (var proxy in new[] { "pitcher", "batter", "catcher", "umpire" })
                {
                    var t = root.transform.Find(proxy);
                    if (t != null) t.gameObject.SetActive(false);
                }
                var clip = FindClip(clipName);
                if (clip == null) return clipName + " NOT FOUND";
                var bone = (HumanBodyBones)System.Enum.Parse(typeof(HumanBodyBones), boneName);
                var sb = new StringBuilder();
                sb.AppendLine(clipName + " len=" + clip.length.ToString("F2", CultureInfo.InvariantCulture)
                    + " bone=" + boneName);
                Vector3 prev = Vector3.zero;
                bool hasPrev = false;
                int n = Mathf.FloorToInt(clip.length / stepSec);
                for (int i = 0; i <= n; i++)
                {
                    float sec = Mathf.Min(clip.length - 0.001f, i * stepSec);
                    var body = SpawnBody(root, "probe-s", Vector3.zero, 90f, Color.white);
                    Sample(body, clip, sec / clip.length);
                    var anim = body.GetComponentInChildren<Animator>();
                    var p = anim.GetBoneTransform(bone).position;
                    float speed = hasPrev ? (p - prev).magnitude / stepSec : 0f;
                    sb.Append(string.Format(CultureInfo.InvariantCulture, " {0:F1}s={1:F1}", sec, speed));
                    prev = p; hasPrev = true;
                    Object.DestroyImmediate(body);
                }
                sb.Append('\n');
                Debug.Log("[DuelMotionSpike] clip speed probe\n" + sb);
                return sb.ToString();
            }
            finally
            {
                DuelStageComposer.Cleanup();
            }
        }

        /// <summary>
        /// 打者クリップの両手ボーン間隔カーブを0.1s刻みで実測する（AlignBatの片手判定閾値の根拠）。
        /// 素体UBCは両手で握っていてもボーン間隔が~0.2m台あるため、閾値は「本当に手が離れた」区間と
        /// 通常保持区間をこの表で分離できる値に置く。
        /// </summary>
        public static string RunHandGapProbe()
        {
            try
            {
                DuelStageComposer.Cleanup();
                DuelStageComposer.BuildStage();
                SpawnPose.Clear();
                AnkleOffset.Clear();
                var root = GameObject.Find(DuelStageComposer.RootName);
                foreach (var proxy in new[] { "pitcher", "batter", "catcher", "umpire" })
                {
                    var t = root.transform.Find(proxy);
                    if (t != null) t.gameObject.SetActive(false);
                }
                var clips = new[]
                {
                    "AA_Baseball_Batter_Idle_Loop",
                    "AA_Baseball_Batter_Swing_Type01_Miss",
                    "AA_Baseball_Batter_Swing_Type01_Hitting",
                    "AA_Baseball_Batter_Swing_Type02_Miss",
                    "AA_Baseball_Batter_Swing_Type02_Hitting",
                    "AA_Baseball_Batter_Strike",
                    "AA_Baseball_Batter_Hit_ByPitch_Type01",
                };
                var sb = new StringBuilder();
                foreach (var name in clips)
                {
                    var clip = FindClip(name);
                    if (clip == null) { sb.AppendLine(name + " NOT FOUND"); continue; }
                    sb.Append(name + " len=" + clip.length.ToString("F2", CultureInfo.InvariantCulture) + "\n ");
                    int n = Mathf.FloorToInt(clip.length * 10f);
                    for (int i = 0; i <= n; i++)
                    {
                        float sec = Mathf.Min(clip.length - 0.001f, i * 0.1f);
                        var body = SpawnBody(root, "probe-h", Vector3.zero, 90f, Color.white);
                        Sample(body, clip, sec / clip.length);
                        var anim = body.GetComponentInChildren<Animator>();
                        var lh = anim.GetBoneTransform(HumanBodyBones.LeftHand);
                        var rh = anim.GetBoneTransform(HumanBodyBones.RightHand);
                        float gap = (lh != null && rh != null) ? Vector3.Distance(lh.position, rh.position) : -1f;
                        sb.Append(string.Format(CultureInfo.InvariantCulture, " {0:F1}s={1:F2}", sec, gap));
                        Object.DestroyImmediate(body);
                    }
                    sb.Append('\n');
                }
                Debug.Log("[DuelMotionSpike] hand gap probe\n" + sb);
                return sb.ToString();
            }
            finally
            {
                DuelStageComposer.Cleanup();
            }
        }

        /// <summary>
        /// 投球クリップのyaw候補4種（-90/0/90/180）をリリース(0.53)と直後(0.60)で撮り比べる目視決着用。
        /// 出力: out/duel-stage/frame/pitchyaw_[yaw]_t[時刻].png（採用構図R-comp60）
        /// </summary>
        public static string RunPitcherYawSheet()
        {
            try
            {
                DuelStageComposer.Cleanup();
                DuelStageComposer.BuildStage();
                int layer = LayerMask.NameToLayer(DuelStageComposer.LayerName);
                var outDir = Path.Combine(Directory.GetParent(Application.dataPath).FullName, "out/duel-stage/frame");
                Directory.CreateDirectory(outDir);
                SpawnPose.Clear();
                AnkleOffset.Clear();
                var root = GameObject.Find(DuelStageComposer.RootName);
                foreach (var proxy in new[] { "pitcher", "batter", "catcher", "umpire" })
                {
                    var t = root.transform.Find(proxy);
                    if (t != null) t.gameObject.SetActive(false);
                }
                var batter = SpawnBody(root, "body-batter", new Vector3(-0.9f, 0f, 0f), 90f, new Color(0.16f, 0.20f, 0.38f));
                Sample(batter, FindClip("AA_Baseball_Batter_Idle_Loop"), 0.30f); GroundFix(batter, 0f);

                var clip = FindClip(KokoSim.Unity.Match.DuelStageRig.PitcherClipName);
                var camGo = new GameObject("~DuelStageCamera") { hideFlags = HideFlags.DontSave };
                var cam = camGo.AddComponent<Camera>();
                cam.cullingMask = 1 << layer;
                cam.clearFlags = CameraClearFlags.SolidColor;
                cam.backgroundColor = new Color(0.55f, 0.78f, 0.90f);
                cam.nearClipPlane = 0.1f;
                cam.farClipPlane = 400f;
                ApplyAdoptedCamera(cam);

                var rt = new RenderTexture(480, 270, 24, RenderTextureFormat.ARGB32) { filterMode = FilterMode.Point };
                rt.Create();
                var big = new RenderTexture(1920, 1080, 0, RenderTextureFormat.ARGB32);
                big.Create();
                var sb = new StringBuilder();
                try
                {
                    cam.targetTexture = rt;
                    foreach (var yaw in new[] { -90f, 0f, 90f, 180f })
                    {
                        var pitcher = SpawnBody(root, "body-pitcher",
                            new Vector3(0f, DuelStageComposer.MoundHeight, DuelStageComposer.MoundDistance), yaw,
                            new Color(0.92f, 0.92f, 0.90f));
                        foreach (var t in new[] { 0.53f, 0.60f })
                        {
                            Sample(pitcher, clip, t);
                            GroundFix(pitcher, DuelStageComposer.MoundHeight);
                            DuelStageComposer.Render(cam, rt);
                            var prev = RenderTexture.active;
                            Graphics.Blit(rt, big);
                            RenderTexture.active = big;
                            var tex = new Texture2D(1920, 1080, TextureFormat.RGBA32, false);
                            tex.ReadPixels(new Rect(0, 0, 1920, 1080), 0, 0);
                            tex.Apply();
                            RenderTexture.active = prev;
                            var file = string.Format(CultureInfo.InvariantCulture,
                                "pitchyaw_{0}_t{1:000}.png", Mathf.RoundToInt(yaw), Mathf.RoundToInt(t * 100f));
                            File.WriteAllBytes(Path.Combine(outDir, file), tex.EncodeToPNG());
                            Object.DestroyImmediate(tex);
                            sb.AppendLine(file);
                        }
                        Object.DestroyImmediate(pitcher);
                    }
                }
                finally
                {
                    cam.targetTexture = null;
                    rt.Release(); Object.DestroyImmediate(rt);
                    big.Release(); Object.DestroyImmediate(big);
                    Object.DestroyImmediate(camGo);
                }
                Debug.Log("[DuelMotionSpike] pitcher yaw sheet -> " + outDir + "\n" + sb);
                return sb.ToString();
            }
            finally
            {
                DuelStageComposer.Cleanup();
            }
        }

        /// <summary>
        /// 採用構図 R-comp60 のカメラ設定（2026-07-26 構図リフレームD採用・DuelStageRigと同値）。
        /// 俯角4.5°・横角3.8°は旧Rのまま、後退60m+FOV2.9°の望遠圧縮で投手/打者の遠近差を潰す。
        /// </summary>
        internal static void ApplyAdoptedCamera(Camera cam)
        {
            const float dist = 60f;
            const float aimY = 0.8f;
            float camZ = DuelStageComposer.MoundDistance + dist;
            float dz = camZ - 1f;
            cam.transform.position = new Vector3(-dz * 0.0668f, aimY + dz * 0.0782f, camZ);
            cam.transform.LookAt(new Vector3(0f, aimY, 1f));
            cam.fieldOfView = 2.9f;
        }

        private static void Render(Camera cam, RenderTexture rt)
        {
            DuelStageComposer.Render(cam, rt);
        }

        // ---- 小道具（§3.3: 自作プリミティブ＋手ボーンアタッチ） --------------

        /// <summary>
        /// プリミティブ小道具を手ボーン等の子として装着する。localPos/localScaleはワールド寸法で指定し、
        /// ボーンのlossyScale（素体の身長正規化スケール）を打ち消してローカル値に変換する。
        /// </summary>
        private static GameObject AttachProp(GameObject body, HumanBodyBones bone, PrimitiveType type,
            Vector3 worldPos, Vector3 localEuler, Vector3 worldScale, Color color)
        {
            var animator = body.GetComponentInChildren<Animator>();
            if (animator == null) return null;
            var boneT = animator.GetBoneTransform(bone);
            if (boneT == null) return null;

            var prop = GameObject.CreatePrimitive(type);
            prop.name = "prop-" + bone;
            Object.DestroyImmediate(prop.GetComponent<Collider>());
            prop.transform.SetParent(boneT, false);
            var inv = new Vector3(1f / boneT.lossyScale.x, 1f / boneT.lossyScale.y, 1f / boneT.lossyScale.z);
            prop.transform.localPosition = Vector3.Scale(worldPos, inv);
            prop.transform.localEulerAngles = localEuler;
            prop.transform.localScale = Vector3.Scale(worldScale, inv);

            var mat = new Material(Shader.Find("KokoSim/DuelStageProxyLambert")) { hideFlags = HideFlags.DontSave };
            mat.SetColor("_BaseColor", color);
            prop.GetComponent<MeshRenderer>().sharedMaterial = mat;
            prop.layer = LayerMask.NameToLayer(DuelStageComposer.LayerName);
            return prop;
        }

        /// <summary>
        /// バット: 先細りは省略した円柱（§3.3の本番品質基準）。手ボーンの子には**しない**——
        /// 手ボーン軸は素体依存（本素体はY=前腕方向で胴に埋まる）のため、両手ボーンを結ぶ線で
        /// 毎コマ AlignBat() で整列させる。ステージルート直下（スケール1）に置く。
        /// </summary>
        private static GameObject SpawnBat(GameObject stageRoot)
        {
            var bat = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
            bat.name = "prop-bat";
            Object.DestroyImmediate(bat.GetComponent<Collider>());
            bat.transform.SetParent(stageRoot.transform, false);
            bat.transform.localScale = new Vector3(0.055f, 0.425f, 0.055f); // 円柱高2×0.425=全長0.85m
            var mat = new Material(Shader.Find("KokoSim/DuelStageProxyLambert")) { hideFlags = HideFlags.DontSave };
            mat.SetColor("_BaseColor", new Color(0.78f, 0.62f, 0.42f));
            bat.GetComponent<MeshRenderer>().sharedMaterial = mat;
            bat.layer = LayerMask.NameToLayer(DuelStageComposer.LayerName);
            return bat;
        }

        /// <summary>両手ボーンの位置からバットを整列（下手＝ノブ側→上手方向へ延長）。Sample後に毎回呼ぶ。</summary>
        private static void AlignBat(GameObject batter, GameObject bat)
        {
            var animator = batter.GetComponentInChildren<Animator>();
            if (animator == null) return;
            var rh = animator.GetBoneTransform(HumanBodyBones.RightHand).position;
            var lh = animator.GetBoneTransform(HumanBodyBones.LeftHand).position;
            var axis = (rh - lh).normalized; // 右打者: 左手が下（ノブ側）・右手が上
            bat.transform.rotation = Quaternion.FromToRotation(Vector3.up, axis);
            // グリップ（ノブ+10cm）が下手の手に来るよう、円柱中心を軸沿いに配置
            bat.transform.position = lh + axis * (0.425f - 0.10f);
        }

        /// <summary>捕手ミット: 潰した楕円体。</summary>
        private static GameObject AttachMitt(GameObject catcher)
        {
            return AttachProp(catcher, HumanBodyBones.LeftHand, PrimitiveType.Sphere,
                Vector3.zero, Vector3.zero, new Vector3(0.26f, 0.24f, 0.15f), new Color(0.45f, 0.27f, 0.15f));
        }

        // スイング走査の実測（2026-07-26）: ロード~0.25/振り出し0.27-0.29/当たり≈0.30/
        // フォロー0.33-0.45/走り出し0.55-（Hittingクリップは一塁への走り出しまで含む）
        private static readonly float[] SwingTimes = { 0.10f, 0.22f, 0.27f, 0.30f, 0.33f, 0.38f, 0.45f, 0.55f };

        /// <summary>
        /// スイング側のコマシート: 打者がSwing_Type01_Hittingを振り（両手整列バット付き）、
        /// 投手はリリース直後・捕手はミット付き構えで固定。カメラは確定R構図。
        /// 出力: out/duel-stage/motion/pose_swing_*.png
        /// </summary>
        public static string RunSwingSheet()
        {
            try
            {
                DuelStageComposer.Cleanup();
                DuelStageComposer.BuildStage();
                return RenderSwingSheet();
            }
            finally
            {
                DuelStageComposer.Cleanup();
            }
        }

        private static string RenderSwingSheet()
        {
            int layer = LayerMask.NameToLayer(DuelStageComposer.LayerName);
            var outDir = Path.Combine(Directory.GetParent(Application.dataPath).FullName, "out/duel-stage/motion");
            Directory.CreateDirectory(outDir);

            SpawnPose.Clear();
            AnkleOffset.Clear();
            var root = GameObject.Find(DuelStageComposer.RootName);
            foreach (var proxy in new[] { "pitcher", "batter", "catcher", "umpire" })
            {
                var t = root.transform.Find(proxy);
                if (t != null) t.gameObject.SetActive(false);
            }

            var pitcher = SpawnBody(root, "body-pitcher",
                new Vector3(0f, DuelStageComposer.MoundHeight, DuelStageComposer.MoundDistance), KokoSim.Unity.Match.DuelStageRig.PitcherYaw,
                new Color(0.92f, 0.92f, 0.90f));
            var batter = SpawnBody(root, "body-batter", new Vector3(-0.9f, 0f, 0f), 90f, new Color(0.16f, 0.20f, 0.38f));
            var catcher = SpawnBody(root, "body-catcher", new Vector3(0f, 0f, -1.0f), 0f, new Color(0.25f, 0.27f, 0.30f));
            var umpire = SpawnBody(root, "body-umpire", new Vector3(-0.35f, 0f, -1.7f), 0f, new Color(0.12f, 0.12f, 0.13f));
            var bat = SpawnBat(root);
            AttachMitt(catcher);

            var swingClip = FindClip("AA_Baseball_Batter_Swing_Type01_Hitting");
            Sample(pitcher, FindClip(KokoSim.Unity.Match.DuelStageRig.PitcherClipName), 0.58f); GroundFix(pitcher, DuelStageComposer.MoundHeight);
            Sample(catcher, FindClip("AA_Baseball_Catcher_Idle"), 0.50f); GroundFix(catcher, 0f);
            Sample(umpire, FindClip("AA_Baseball_Umpire_Crouch_Idle"), 0.50f); GroundFix(umpire, 0f);

            var camGo = new GameObject("~DuelStageCamera") { hideFlags = HideFlags.DontSave };
            var cam = camGo.AddComponent<Camera>();
            cam.cullingMask = 1 << layer;
            cam.clearFlags = CameraClearFlags.SolidColor;
            cam.backgroundColor = new Color(0.55f, 0.78f, 0.90f);
            cam.nearClipPlane = 0.1f;
            cam.farClipPlane = 200f;
            ApplyAdoptedCamera(cam);

            var rt = new RenderTexture(480, 270, 24, RenderTextureFormat.ARGB32) { filterMode = FilterMode.Point };
            rt.Create();
            var big = new RenderTexture(1920, 1080, 0, RenderTextureFormat.ARGB32);
            big.Create();

            var sb = new StringBuilder();
            try
            {
                cam.targetTexture = rt;
                foreach (var t in SwingTimes)
                {
                    Sample(batter, swingClip, t);
                    GroundFix(batter, 0f);
                    AlignBat(batter, bat);

                    int batPx = DuelStageComposer.MeasureScreenHeight(cam, batter);
                    DuelStageComposer.Render(cam, rt);

                    var prev = RenderTexture.active;
                    Graphics.Blit(rt, big);
                    RenderTexture.active = big;
                    var tex = new Texture2D(1920, 1080, TextureFormat.RGBA32, false);
                    tex.ReadPixels(new Rect(0, 0, 1920, 1080), 0, 0);
                    tex.Apply();
                    RenderTexture.active = prev;

                    var file = string.Format(CultureInfo.InvariantCulture, "pose_swing_t{0:000}_bat{1}px.png",
                        Mathf.RoundToInt(t * 100f), batPx);
                    File.WriteAllBytes(Path.Combine(outDir, file), tex.EncodeToPNG());
                    Object.DestroyImmediate(tex);
                    sb.AppendLine(file);
                }
            }
            finally
            {
                cam.targetTexture = null;
                rt.Release(); Object.DestroyImmediate(rt);
                big.Release(); Object.DestroyImmediate(big);
                Object.DestroyImmediate(camGo);
            }

            Debug.Log("[DuelMotionSpike] swing sheet -> " + outDir + "\n" + sb);
            return sb.ToString();
        }

        /// <summary>スイングの実タイミング走査: t0..t1をn等分し、近接サイドから連番で撮る。</summary>
        public static string RunSwingScan(float t0, float t1, int n)
        {
            try
            {
                DuelStageComposer.Cleanup();
                DuelStageComposer.BuildStage();

                SpawnPose.Clear();
                AnkleOffset.Clear();
                var root = GameObject.Find(DuelStageComposer.RootName);
                foreach (var proxy in new[] { "pitcher", "batter", "catcher", "umpire" })
                {
                    var t = root.transform.Find(proxy);
                    if (t != null) t.gameObject.SetActive(false);
                }

                var batter = SpawnBody(root, "body-batter", new Vector3(-0.9f, 0f, 0f), 90f, new Color(0.16f, 0.20f, 0.38f));
                var bat = SpawnBat(root);
                var clip = FindClip("AA_Baseball_Batter_Swing_Type01_Hitting");

                var camGo = new GameObject("~scanCam") { hideFlags = HideFlags.DontSave };
                var cam = camGo.AddComponent<Camera>();
                cam.cullingMask = 1 << LayerMask.NameToLayer(DuelStageComposer.LayerName);
                cam.clearFlags = CameraClearFlags.SolidColor;
                cam.backgroundColor = new Color(0.75f, 0.75f, 0.75f);
                cam.fieldOfView = 40f;
                cam.nearClipPlane = 0.05f;
                cam.farClipPlane = 50f;
                cam.transform.position = new Vector3(-0.9f, 1.2f, 3.2f);
                cam.transform.LookAt(new Vector3(-0.9f, 1.0f, 0f));

                var rt = new RenderTexture(480, 270, 24); rt.Create();
                var outDir = Path.Combine(Directory.GetParent(Application.dataPath).FullName, "out/duel-stage/motion/scan");
                Directory.CreateDirectory(outDir);
                var sb = new StringBuilder();
                try
                {
                    cam.targetTexture = rt;
                    for (int i = 0; i < n; i++)
                    {
                        float t = Mathf.Lerp(t0, t1, n <= 1 ? 0f : (float)i / (n - 1));
                        Sample(batter, clip, t);
                        GroundFix(batter, 0f);
                        AlignBat(batter, bat);
                        DuelStageComposer.Render(cam, rt);
                        var prev = RenderTexture.active;
                        RenderTexture.active = rt;
                        var tex = new Texture2D(480, 270, TextureFormat.RGBA32, false);
                        tex.ReadPixels(new Rect(0, 0, 480, 270), 0, 0);
                        tex.Apply();
                        RenderTexture.active = prev;
                        var file = string.Format(CultureInfo.InvariantCulture, "scan_t{0:000}.png", Mathf.RoundToInt(t * 1000f));
                        File.WriteAllBytes(Path.Combine(outDir, file), tex.EncodeToPNG());
                        Object.DestroyImmediate(tex);
                        sb.AppendLine(file);
                    }
                    return sb.ToString();
                }
                finally
                {
                    cam.targetTexture = null;
                    rt.Release(); Object.DestroyImmediate(rt);
                    Object.DestroyImmediate(camGo);
                }
            }
            finally
            {
                DuelStageComposer.Cleanup();
            }
        }

        /// <summary>
        /// バット整列の検証用: 打者単体＋両手整列バットで構え(t=0.10)と当たり(t=0.35)を近接2アングルで撮る。
        /// </summary>
        public static string RunPropTune()
        {
            try
            {
                DuelStageComposer.Cleanup();
                DuelStageComposer.BuildStage();

                SpawnPose.Clear();
                AnkleOffset.Clear();
                var root = GameObject.Find(DuelStageComposer.RootName);
                foreach (var proxy in new[] { "pitcher", "batter", "catcher", "umpire" })
                {
                    var t = root.transform.Find(proxy);
                    if (t != null) t.gameObject.SetActive(false);
                }

                var batter = SpawnBody(root, "body-batter", new Vector3(-0.9f, 0f, 0f), 90f, new Color(0.16f, 0.20f, 0.38f));
                var bat = SpawnBat(root);

                var clip = FindClip("AA_Baseball_Batter_Swing_Type01_Hitting");
                var camGo = new GameObject("~tuneCam") { hideFlags = HideFlags.DontSave };
                var cam = camGo.AddComponent<Camera>();
                cam.cullingMask = 1 << LayerMask.NameToLayer(DuelStageComposer.LayerName);
                cam.clearFlags = CameraClearFlags.SolidColor;
                cam.backgroundColor = new Color(0.75f, 0.75f, 0.75f);
                cam.fieldOfView = 35f;
                cam.nearClipPlane = 0.05f;
                cam.farClipPlane = 50f;

                var rt = new RenderTexture(640, 360, 24); rt.Create();
                var outDir = Path.Combine(Directory.GetParent(Application.dataPath).FullName, "out/duel-stage/motion");
                var shots = new[]
                {
                    new { name = "tune_stance_side", t = 0.10f, camPos = new Vector3(-0.9f, 1.3f, 3.0f), look = new Vector3(-0.9f, 1.1f, 0f) },
                    new { name = "tune_stance_front", t = 0.10f, camPos = new Vector3(2.2f, 1.3f, 0f), look = new Vector3(-0.9f, 1.1f, 0f) },
                    new { name = "tune_contact_side", t = 0.35f, camPos = new Vector3(-0.9f, 1.3f, 3.0f), look = new Vector3(-0.9f, 1.1f, 0f) },
                    new { name = "tune_contact_front", t = 0.35f, camPos = new Vector3(2.2f, 1.3f, 0f), look = new Vector3(-0.9f, 1.1f, 0f) },
                };
                try
                {
                    cam.targetTexture = rt;
                    var sb = new StringBuilder();
                    foreach (var shot in shots)
                    {
                        Sample(batter, clip, shot.t);
                        GroundFix(batter, 0f);
                        AlignBat(batter, bat);
                        cam.transform.position = shot.camPos;
                        cam.transform.LookAt(shot.look);
                        DuelStageComposer.Render(cam, rt);
                        var prev = RenderTexture.active;
                        RenderTexture.active = rt;
                        var tex = new Texture2D(640, 360, TextureFormat.RGBA32, false);
                        tex.ReadPixels(new Rect(0, 0, 640, 360), 0, 0);
                        tex.Apply();
                        RenderTexture.active = prev;
                        File.WriteAllBytes(Path.Combine(outDir, shot.name + ".png"), tex.EncodeToPNG());
                        Object.DestroyImmediate(tex);
                        sb.AppendLine(shot.name + ".png");
                    }
                    return sb.ToString();
                }
                finally
                {
                    cam.targetTexture = null;
                    rt.Release(); Object.DestroyImmediate(rt);
                    Object.DestroyImmediate(camGo);
                }
            }
            finally
            {
                DuelStageComposer.Cleanup();
            }
        }

        // ---- 素体・サンプリング ----------------------------------------------

        /// <summary>バインドポーズで測った「足ボーン高さ−接地面」。GroundFix相当を外部で組む時に使う。</summary>
        internal static float AnkleOffsetOf(GameObject body)
        {
            float v;
            return AnkleOffset.TryGetValue(body, out v) ? v : 0f;
        }

        internal static GameObject SpawnBody(GameObject root, string name, Vector3 pos, float yawDeg, Color color)
        {
            var prefab = UnityEditor.AssetDatabase.LoadAssetAtPath<GameObject>(
                string.IsNullOrEmpty(BodyOverride) ? BodyFbx : BodyOverride);
            var go = (GameObject)Object.Instantiate(prefab);
            go.name = name;
            go.transform.SetParent(root.transform, false);

            // 身長正規化（バインドポーズのバウンズ実測から）
            var rs = go.GetComponentsInChildren<Renderer>();
            var b = rs[0].bounds;
            foreach (var r in rs) b.Encapsulate(r.bounds);
            float scale = TargetHeight / Mathf.Max(0.01f, b.size.y);
            go.transform.localScale = Vector3.one * scale;
            go.transform.localPosition = pos;
            go.transform.localRotation = Quaternion.Euler(0f, yawDeg, 0f);

            var mat = new Material(Shader.Find("KokoSim/DuelStageProxyLambert")) { hideFlags = HideFlags.DontSave };
            mat.SetColor("_BaseColor", color);
            foreach (var r in go.GetComponentsInChildren<Renderer>()) r.sharedMaterial = mat;

            var animator = go.GetComponentInChildren<Animator>();
            if (animator != null)
            {
                animator.applyRootMotion = true; // クリップは踏み込み・降下をルートモーションで持つ（捨てると浮く）
                animator.cullingMode = AnimatorCullingMode.AlwaysAnimate;
            }
            foreach (var smr in go.GetComponentsInChildren<SkinnedMeshRenderer>())
                smr.updateWhenOffscreen = true; // バウンズをバインドポーズ箱でなく実ポーズから再計算させる

            // バインドポーズで「足ボーン高さ−接地面」を測っておく（足ボーンは足首にあり底面より高い）
            if (animator != null)
            {
                var lf0 = animator.GetBoneTransform(HumanBodyBones.LeftFoot);
                var rf0 = animator.GetBoneTransform(HumanBodyBones.RightFoot);
                if (lf0 != null && rf0 != null)
                    AnkleOffset[go] = Mathf.Min(lf0.position.y, rf0.position.y) - go.transform.position.y;
            }

            SetLayerRecursive(go, LayerMask.NameToLayer(DuelStageComposer.LayerName));
            SpawnPose[go] = new Pose(go.transform.localPosition, go.transform.localRotation);
            return go;
        }

        private static readonly System.Collections.Generic.Dictionary<GameObject, Pose> SpawnPose =
            new System.Collections.Generic.Dictionary<GameObject, Pose>();

        /// <summary>
        /// HumanoidクリップをPlayableGraphで評価する（Edit mode可・リターゲット込み）。
        /// ルートモーションはデルタ蓄積なので、スポーン姿勢へ戻してから0→tを60fps刻みで前進させる。
        /// </summary>
        private static void Sample(GameObject body, AnimationClip clip, float normTime)
        {
            var animator = body.GetComponentInChildren<Animator>();
            if (animator == null) return;
            Pose basePose;
            if (SpawnPose.TryGetValue(body, out basePose))
            {
                body.transform.localPosition = basePose.position;
                body.transform.localRotation = basePose.rotation;
            }
            var graph = PlayableGraph.Create("~duel-sample");
            try
            {
                var output = AnimationPlayableOutput.Create(graph, "out", animator);
                var playable = AnimationClipPlayable.Create(graph, clip);
                output.SetSourcePlayable(playable);
                float target = clip.length * Mathf.Clamp01(normTime);
                int steps = Mathf.Max(1, Mathf.CeilToInt(target * 60f));
                float dt = target / steps;
                for (int i = 0; i < steps; i++) graph.Evaluate(dt);
            }
            finally
            {
                graph.Destroy();
            }
        }

        /// <summary>
        /// クリップのルート軌跡を本番と同じ配置（マウンド上・PitcherYaw）で秒刻みに実測する。
        /// モーキャップは収録中の立ち位置ドリフトがそのままルートモーションに乗るため、
        /// 「本塁方向（−Z）へどれだけ踏み込むか」と「横（X）へどれだけ流れるか」を分けて見る必要がある。
        /// 出力は 1行1サンプル: 秒 / ステージローカルXZ / 開始位置からの変位 / 胸法線。
        /// </summary>
        public static string RunRootPathProbe(string clipName, float stepSec)
        {
            try
            {
                DuelStageComposer.Cleanup();
                DuelStageComposer.BuildStage();
                SpawnPose.Clear();
                AnkleOffset.Clear();
                var root = GameObject.Find(DuelStageComposer.RootName);
                foreach (var proxy in new[] { "pitcher", "batter", "catcher", "umpire" })
                {
                    var t = root.transform.Find(proxy);
                    if (t != null) t.gameObject.SetActive(false);
                }
                var clip = FindClip(clipName);
                if (clip == null) return clipName + " NOT FOUND";

                var sb = new StringBuilder();
                sb.AppendLine(clipName + " len=" + clip.length.ToString("F2", CultureInfo.InvariantCulture)
                    + "  (Z−=本塁方向 / X=横流れ)");
                Vector3 start = Vector3.zero;
                int n = Mathf.FloorToInt(clip.length / stepSec);
                for (int i = 0; i <= n; i++)
                {
                    float sec = Mathf.Min(clip.length - 0.001f, i * stepSec);
                    var body = SpawnBody(root, "probe-root",
                        new Vector3(0f, DuelStageComposer.MoundHeight, DuelStageComposer.MoundDistance),
                        KokoSim.Unity.Match.DuelStageRig.PitcherYaw, Color.white);
                    Sample(body, clip, sec / clip.length);
                    var p = body.transform.position;
                    if (i == 0) start = p;
                    var anim = body.GetComponentInChildren<Animator>();
                    var ra = anim.GetBoneTransform(HumanBodyBones.RightUpperArm).position;
                    var la = anim.GetBoneTransform(HumanBodyBones.LeftUpperArm).position;
                    var facing = Vector3.Cross(ra - la, Vector3.up);
                    facing.y = 0f; facing.Normalize();
                    sb.AppendLine(string.Format(CultureInfo.InvariantCulture,
                        "t={0:F2} pos=({1:F2},{2:F2}) d=({3:F2},{4:F2}) chest=({5:F2},{6:F2})",
                        sec, p.x, p.z, p.x - start.x, p.z - start.z, facing.x, facing.z));
                    Object.DestroyImmediate(body);
                }
                Debug.Log("[DuelMotionSpike] root path probe\n" + sb);
                return sb.ToString();
            }
            finally
            {
                DuelStageComposer.Cleanup();
            }
        }

        private static readonly System.Collections.Generic.Dictionary<GameObject, float> AnkleOffset =
            new System.Collections.Generic.Dictionary<GameObject, float>();

        /// <summary>
        /// 低い方の足ボーンが「接地面＋足首オフセット」に来るよう縦方向だけ平行移動する。
        /// レンダラAABBはスキニングの保守的バウンズで下に膨らみ、接地が浮く（2026-07-26の実測）ため使わない。
        /// </summary>
        private static void GroundFix(GameObject body, float groundY)
        {
            var animator = body.GetComponentInChildren<Animator>();
            if (animator == null) return;
            var lf = animator.GetBoneTransform(HumanBodyBones.LeftFoot);
            var rf = animator.GetBoneTransform(HumanBodyBones.RightFoot);
            if (lf == null || rf == null) return;
            float ankle;
            if (!AnkleOffset.TryGetValue(body, out ankle)) ankle = 0f;
            float footY = Mathf.Min(lf.position.y, rf.position.y);
            body.transform.position += new Vector3(0f, (groundY + ankle) - footY, 0f);
        }

        /// <summary>
        /// 守備・走塁クリップの**素の胸の向き**実測（打球処理の3D化 F2〜）。
        ///
        /// <see cref="RunFacingProbe"/> は対決4体むけに「狙いの向き」を job ごとに固定して
        /// 必要yawを直接返すが、野手は**立ち位置ごとに向く先が違う**（全員が本塁を向く＝
        /// 一塁手と左翼手では世界向きが90°違う）。そこで狙いを取らず**素の向きだけ**を返し、
        /// 使う側が `yaw = SignedAngle(素の向き, その野手の向くべき向き)` で組む。
        ///
        /// ⚠ 素の向きはクリップごとに違う（投手だけローカル−X、他は+Z等）。新クリップを足したら
        /// 必ずここへ1行足して実測してから yaw を決める（design-18 §7-3 の掟）。
        /// ⚠ 途中で向きが変わる芝居（捕逸・送球）は**入りの構えで測る**＝normTime は小さく取る。
        /// </summary>
        public static string RunFielderFacingProbe()
        {
            try
            {
                DuelStageComposer.Cleanup();
                DuelStageComposer.BuildStage();
                SpawnPose.Clear();
                AnkleOffset.Clear();
                var root = GameObject.Find(DuelStageComposer.RootName);
                foreach (var proxy in new[] { "pitcher", "batter", "catcher", "umpire" })
                {
                    var t = root.transform.Find(proxy);
                    if (t != null) t.gameObject.SetActive(false);
                }

                var clips = new[]
                {
                    "AA_Baseball_Defender_Standing_Idle_Loop_Type01",
                    "AA_Baseball_Defender_Crouch_Idle_Loop_Type01",
                    "AA_Baseball_Offense_Crouch_Idle_Loop_Type01",
                };

                var sb = new StringBuilder();
                sb.AppendLine("clip\tchestX\tchestZ\thipsX\thipsZ\tyawTo-Z\tlen");
                foreach (var name in clips)
                {
                    var clip = FindClip(name);
                    if (clip == null) { sb.AppendLine(name + "\t(クリップが無い)"); continue; }
                    var body = SpawnBody(root, "probe-" + name, Vector3.zero, 0f, Color.white);
                    Sample(body, clip, 0.10f);   // 入りの構えで測る
                    var anim = body.GetComponentInChildren<Animator>();
                    var ra = anim.GetBoneTransform(HumanBodyBones.RightUpperArm).position;
                    var la = anim.GetBoneTransform(HumanBodyBones.LeftUpperArm).position;
                    var facing = Vector3.Cross(ra - la, Vector3.up);
                    facing.y = 0f; facing.Normalize();
                    var hips = anim.GetBoneTransform(HumanBodyBones.Hips).rotation * Vector3.forward;
                    hips.y = 0f; hips.Normalize();
                    sb.AppendLine(string.Format(CultureInfo.InvariantCulture,
                        "{0}\t{1:F3}\t{2:F3}\t{3:F3}\t{4:F3}\t{5:F1}\t{6:F2}",
                        name, facing.x, facing.z, hips.x, hips.z,
                        Vector3.SignedAngle(facing, new Vector3(0f, 0f, -1f), Vector3.up), clip.length));
                    Object.DestroyImmediate(body);
                }
                Debug.Log("[DuelMotionSpike] fielder facing probe\n" + sb);
                return sb.ToString();
            }
            finally
            {
                DuelStageComposer.Cleanup();
            }
        }

        /// <summary>
        /// 走行・移動クリップの**前進量**実測（打球処理の3D化 F4）。速度合わせの単一ソース。
        ///
        /// engine の <c>FielderAt</c> は位置しか返さないので、Unity 側は
        /// <c>Speed = v_engine / v_clip</c> で足の回転を合わせる（合わせないと空中を滑るか
        /// 足が空回りする）。その <c>v_clip</c> は**クリップごとに実測するしかない**
        /// （尺は catalog に出ているが、1ループで何m進むかは出ていない）。
        ///
        /// あわせて **進む向きと体の向きの角度差**を出す。ルートモーションの進行方向は
        /// クリップの素の向きと一致しない（後退・横走りは体が別の方向を向いたまま進む）ので、
        /// 「engine の速度ベクトルへ体を向ける」だけだと後退クリップが逆向きに走る。
        ///
        /// ⚠ ルートモーションが**演技の本質**であるクリップ（ダイブ・スライディング・走り込み捕球）は
        /// 速度合わせの対象ではない。ここで測る変位は「開始位置を到達点から逆算する」ために使う。
        /// </summary>
        public static string RunLocomotionProbe()
        {
            // 走行ループ（速度合わせに使う）／踏み出し／ルートモーションが演技のもの／走塁
            var names = new[]
            {
                "Defender_Movement_Run_Loop", "Defender_Movement_Run_Back_Loop",
                "Defender_Movement_Run_L90_Loop", "Defender_Movement_Run_R90_Loop",
                "Defender_Movement_Sprint_Loop", "Defender_Movement_Sprint_L90_Loop",
                "Defender_Movement_Sprint_R90_Loop",
                "Defender_Movement_Walk_F_Loop", "Defender_Movement_Walk_B_Loop",
                "Defender_Movement_Walk_L_Loop", "Defender_Movement_Walk_R_Loop",
                "Defender_Movement_Shuffle_L", "Defender_Movement_Shuffle_R",
                "Defender_Movement_Crouch_To_Run_Type01", "Defender_Movement_Crouch_To_Run_Type02",
                "Defender_Movement_Crouch_To_Run_Back", "Defender_Movement_Crouch_To_Run_L90",
                "Defender_Movement_Crouch_To_Run_R90",
                "Defender_Movement_Sprint_Diving_Catch", "Defender_Movement_Sprint_Sliding_Catch",
                "Defender_Movement_Run_To_Catching_Type01", "Defender_Movement_Run_To_Catching_Type02",
                "Defender_Movement_Run_Back_To_Catching", "Defender_Movement_Run_Back_To_Jumping_Catch",
                "Defender_Movement_Run_L90_To_Catching_Type01", "Defender_Movement_Run_R90_To_Catching_Type01",
                "Defender_Movement_Shuffle_L_Catching", "Defender_Movement_Shuffle_R_Catching",
                "Offense_Crouch_To_Running", "Offense_Base_Running", "Offense_Running_To_Base",
                "Offense_Base_Walking", "Offense_Back_To_Base",
                "Offense_Base_Sliding_Type01_Succ", "Offense_Base_Sliding_Type01_Fail",
            };

            try
            {
                DuelStageComposer.Cleanup();
                DuelStageComposer.BuildStage();
                SpawnPose.Clear();
                AnkleOffset.Clear();
                var root = GameObject.Find(DuelStageComposer.RootName);
                foreach (var proxy in new[] { "pitcher", "batter", "catcher", "umpire" })
                {
                    var t = root.transform.Find(proxy);
                    if (t != null) t.gameObject.SetActive(false);
                }

                var sb = new StringBuilder();
                sb.AppendLine("clip\tlen\tdx\tdz\tdist\tv_clip\tchestX\tchestZ\tmoveVsChest");
                foreach (var shortName in names)
                {
                    var name = "AA_Baseball_" + shortName;
                    var clip = FindClip(name);
                    if (clip == null) { sb.AppendLine(shortName + "\t(クリップが無い)"); continue; }

                    // 素の向き（入りの構え）。途中で振り向く芝居があるので頭で測る
                    var b0 = SpawnBody(root, "loco0", Vector3.zero, 0f, Color.white);
                    Sample(b0, clip, 0.05f);
                    var anim0 = b0.GetComponentInChildren<Animator>();
                    var ra = anim0.GetBoneTransform(HumanBodyBones.RightUpperArm).position;
                    var la = anim0.GetBoneTransform(HumanBodyBones.LeftUpperArm).position;
                    var chest = Vector3.Cross(ra - la, Vector3.up);
                    chest.y = 0f; chest.Normalize();
                    var p0 = b0.transform.position;
                    Object.DestroyImmediate(b0);

                    // 終端まで積分した先の位置（同じ体を進めるとサンプルが累積するので必ず取り直す）
                    var b1 = SpawnBody(root, "loco1", Vector3.zero, 0f, Color.white);
                    Sample(b1, clip, 1f);
                    var p1 = b1.transform.position;
                    Object.DestroyImmediate(b1);

                    var d = new Vector3(p1.x - p0.x, 0f, p1.z - p0.z);
                    float dist = d.magnitude;
                    // 「体の向きに対して」どちらへ進むか。0°=前進 / 180°=後退 / ±90°=横
                    float moveVsChest = dist > 0.01f
                        ? Vector3.SignedAngle(chest, d.normalized, Vector3.up) : 0f;

                    sb.AppendLine(string.Format(CultureInfo.InvariantCulture,
                        "{0}\t{1:F2}\t{2:F3}\t{3:F3}\t{4:F3}\t{5:F2}\t{6:F2}\t{7:F2}\t{8:F0}",
                        shortName, clip.length, d.x, d.z, dist,
                        clip.length > 0.01f ? dist / clip.length : 0f,
                        chest.x, chest.z, moveVsChest));
                }

                System.IO.Directory.CreateDirectory("out/duel-stage");
                System.IO.File.WriteAllText("out/duel-stage/locomotion.tsv", sb.ToString());
                Debug.Log("[DuelMotionSpike] locomotion probe -> out/duel-stage/locomotion.tsv\n" + sb);
                return sb.ToString();
            }
            finally
            {
                DuelStageComposer.Cleanup();
            }
        }

        [UnityEditor.MenuItem("KokoSim/Duel/F4 走行クリップを実測する")]
        private static void RunLocomotionProbeMenu() => RunLocomotionProbe();

        /// <summary>
        /// 野手の捕球クリップの**捕球の瞬間**と**グラブの位置**を実測する（打球処理の3D化 F5）。
        ///
        /// 捕手で確立した三点セット（アンカー表＋開始予約＋短時間IK）を野手へ広げるための表を焼く。
        /// ⚠ **捕球の瞬間の定義はクリップごとに違う**（design-18 §4.5-③）。捕手ですら2種あった:
        ///   ・構えて待つクリップ ＝ 球が当たった衝撃で引かれる＝**引き始め**
        ///   ・ミットを運ぶクリップ ＝ 的へ運び終わって速度が落ちる点＝**運び終わり**
        /// 野手はこれに**伸び切り**（ダイブ・ジャンプ）が加わる。一律の式で出すと
        /// 「捕ってから球が着く」が起きるので、判定材料を全部出して1本ずつ決める。
        ///
        /// 判定材料は**構えからグラブがどれだけ動いたか**（disp）で採る。
        /// ⚠ 「腰→グラブの距離」で測ってはいけない: 腕を下ろした構えも前へ伸ばした捕球姿勢も
        /// 同じ 0.56m になり、区別がつかない（2026-08-01 実測で <c>Catching_TypeF</c> が
        /// 「最初から最大＝構えて待つ系」と誤判定された）。方向が変わるだけで距離は変わらない。
        ///
        /// 出す量:
        ///   dispMax/tDisp … 構えからの移動量の最大とその時刻（＝運び終わり／伸び切り＝捕球の候補）
        ///   tPeak         … グラブの速さのピーク（引き始め系なら捕球はこの直後）
        ///   gy            … その瞬間のグラブの**足元からの高さ**[m]。選択の主軸（球の高さと突き合わせる）
        ///   gx/gz         … 同じ瞬間の横/前（腰基準・体の向きを軸に取る）
        /// </summary>
        public static string RunFielderCatchProbe()
        {
            var names = new[]
            {
                // 構えて待つ系（その場で捕る）
                "Defender_Catching_TypeF", "Defender_Catching_TypeL",
                "Defender_Catching_TypeR", "Defender_Catching_TypeU",
                "Defender_1st_baseman_Catching", "Defender_3rd_baseman_Catching",
                "Defender_Catched_Type01", "Defender_Catched_Type02",
                "Defender_Catched_Type03", "Defender_Catched_Type04",
                // 運ぶ系（走り込み・横っ跳び）
                "Defender_Movement_Run_To_Catching_Type01", "Defender_Movement_Run_To_Catching_Type02",
                "Defender_Movement_Run_Back_To_Catching",
                "Defender_Movement_Run_L90_To_Catching_Type01", "Defender_Movement_Run_L90_To_Catching_Type02",
                "Defender_Movement_Run_R90_To_Catching_Type01", "Defender_Movement_Run_R90_To_Catching_Type02",
                "Defender_Movement_Shuffle_L_Catching", "Defender_Movement_Shuffle_R_Catching",
                // 伸び切り系
                "Defender_Catching_Jumping_Catch", "Defender_Movement_Run_Back_To_Jumping_Catch",
                "Defender_Movement_Sprint_Diving_Catch", "Defender_Movement_Sprint_Sliding_Catch",
            };
            const float dt = 0.02f;

            try
            {
                DuelStageComposer.Cleanup();
                DuelStageComposer.BuildStage();
                SpawnPose.Clear();
                AnkleOffset.Clear();
                var root = GameObject.Find(DuelStageComposer.RootName);
                foreach (var proxy in new[] { "pitcher", "batter", "catcher", "umpire" })
                {
                    var t = root.transform.Find(proxy);
                    if (t != null) t.gameObject.SetActive(false);
                }

                var sb = new StringBuilder();
                sb.AppendLine("clip\tlen\tdispMax\ttDisp\ttPeak\tvPeak\tgy\tgx\tgz");
                foreach (var shortName in names)
                {
                    var clip = FindClip("AA_Baseball_" + shortName);
                    if (clip == null) { sb.AppendLine(shortName + "\t(クリップが無い)"); continue; }

                    int n = Mathf.Max(1, Mathf.FloorToInt(clip.length / dt));
                    var loc = new Vector3[n + 1];   // 腰基準（右/上/前）
                    var footY = new float[n + 1];   // 足元の高さ（グラブの対地高を出すため）
                    for (int i = 0; i <= n; i++)
                    {
                        float sec = Mathf.Min(clip.length - 0.001f, i * dt);
                        var body = SpawnBody(root, "catch", Vector3.zero, 0f, Color.white);
                        Sample(body, clip, sec / clip.length);
                        var anim = body.GetComponentInChildren<Animator>();
                        var glove = anim.GetBoneTransform(HumanBodyBones.LeftHand).position;
                        var lf = anim.GetBoneTransform(HumanBodyBones.LeftFoot).position;
                        var rf = anim.GetBoneTransform(HumanBodyBones.RightFoot).position;
                        // ⚠ 軸は**アクターのローカル**（＝立ち位置と yaw を与える座標系）で採る。
                        // 肩の線から作った解剖学的な前後左右で採ると、収録が捕球までに体を回している分だけ
                        // ずれ、その表で yaw を決めると「グラブが球の方を向いていない」形になる
                        // （2026-08-01 実測: 残差 0.026m → 0.165m。**絵ではカメラの狙い外しと区別できない**）
                        loc[i] = body.transform.InverseTransformPoint(glove);
                        footY[i] = glove.y - Mathf.Min(lf.y, rf.y);
                        Object.DestroyImmediate(body);
                    }

                    int iDisp = 0, iPeak = 0;
                    float dispMax = 0f, vPeak = 0f;
                    for (int i = 1; i <= n; i++)
                    {
                        float disp = (loc[i] - loc[0]).magnitude;
                        if (disp > dispMax) { dispMax = disp; iDisp = i; }
                        float v = (loc[i] - loc[i - 1]).magnitude / dt;
                        if (v > vPeak) { vPeak = v; iPeak = i; }
                    }

                    sb.AppendLine(string.Format(CultureInfo.InvariantCulture,
                        "{0}\t{1:F2}\t{2:F3}\t{3:F2}\t{4:F2}\t{5:F1}\t{6:F3}\t{7:F3}\t{8:F3}",
                        shortName, clip.length, dispMax, iDisp * dt, iPeak * dt, vPeak,
                        footY[iDisp], loc[iDisp].x, loc[iDisp].z));
                }

                System.IO.Directory.CreateDirectory("out/duel-stage");
                System.IO.File.WriteAllText("out/duel-stage/fieldcatch.tsv", sb.ToString());
                Debug.Log("[DuelMotionSpike] fielder catch probe -> out/duel-stage/fieldcatch.tsv\n" + sb);
                return sb.ToString();
            }
            finally
            {
                DuelStageComposer.Cleanup();
            }
        }

        [UnityEditor.MenuItem("KokoSim/Duel/F5 野手の捕球クリップを実測する")]
        private static void RunFielderCatchProbeMenu() => RunFielderCatchProbe();

        /// <summary>
        /// 送球クリップの**リリースの瞬間**と**手の位置**を実測する（打球処理の3D化 F6）。
        ///
        /// リリースの定義は投手と同じ **利き手（右手）の速さのピーク**に揃える
        /// （<c>DuelCalibration.BallReleaseClipTime</c>＝2.97 は「手の速度ピーク実測 2.95〜3.00」で採った値）。
        /// 定義を役ごとに変えると、同じ「投げる」なのに球の出所が役ごとにずれる。
        ///
        /// ⚠ 捕球と違って **腕は振り抜いた後も速い**（フォロースルー）。ピークが1つとは限らないので、
        /// 手が最も高い／最も前に出た時刻も併記して1本ずつ目で決める材料にする。
        ///
        /// 出す量:
        ///   tPeak/vPeak … 右手の速さのピークとその時刻（＝リリースの第一候補）
        ///   hy          … その瞬間の手の**足元からの高さ**[m]（球の出所の高さ＝engine の 1.7 と比べる）
        ///   hx/hz       … 同じ瞬間の横/前（腰基準・体の向きを軸に取る）
        ///   tFwd/fwdMax … 手が最も前に出た時刻と量[m]（ピークが振り抜き側に出たときの切り分け用）
        ///   tHigh/hiMax … 手が最も高くなった時刻と高さ[m]（同上）
        /// </summary>
        public static string RunThrowProbe()
        {
            var names = new[]
            {
                // 野手の送球（内野→塁・外野→中継）
                "Defender_Pass_the_ball_type01", "Defender_Pass_the_ball_type02",
                "Defender_Pass_the_ball_type03",
                // 捕球から続けて投げる収録があるか（尺が長い4本）を同じ物差しで確かめる
                "Defender_Catched_Type01", "Defender_Catched_Type02",
                "Defender_Catched_Type03", "Defender_Catched_Type04",
                // 捕手（盗塁の二塁送球・投手への返球）
                "Catcher_Throwing", "Catcher_To_Pitcher",
            };
            const float dt = 0.02f;

            try
            {
                DuelStageComposer.Cleanup();
                DuelStageComposer.BuildStage();
                SpawnPose.Clear();
                AnkleOffset.Clear();
                var root = GameObject.Find(DuelStageComposer.RootName);
                foreach (var proxy in new[] { "pitcher", "batter", "catcher", "umpire" })
                {
                    var t = root.transform.Find(proxy);
                    if (t != null) t.gameObject.SetActive(false);
                }

                var sb = new StringBuilder();
                sb.AppendLine("clip\tlen\ttPeak\tvPeak\thy\thx\thz\tdirDeg\televDeg\ttFwd\tfwdMax\ttHigh\thiMax");
                foreach (var shortName in names)
                {
                    var clip = FindClip("AA_Baseball_" + shortName);
                    if (clip == null) { sb.AppendLine(shortName + "\t(クリップが無い)"); continue; }

                    int n = Mathf.Max(1, Mathf.FloorToInt(clip.length / dt));
                    var loc = new Vector3[n + 1];   // 腰基準（右/上/前）
                    var footY = new float[n + 1];   // 手の対地高
                    for (int i = 0; i <= n; i++)
                    {
                        float sec = Mathf.Min(clip.length - 0.001f, i * dt);
                        var body = SpawnBody(root, "throw", Vector3.zero, 0f, Color.white);
                        Sample(body, clip, sec / clip.length);
                        var anim = body.GetComponentInChildren<Animator>();
                        var hand = anim.GetBoneTransform(HumanBodyBones.RightHand).position;
                        var lf = anim.GetBoneTransform(HumanBodyBones.LeftFoot).position;
                        var rf = anim.GetBoneTransform(HumanBodyBones.RightFoot).position;
                        // 軸は**アクターのローカル**（捕球プローブと同じ理由。yaw を決める座標系に揃える）
                        loc[i] = body.transform.InverseTransformPoint(hand);
                        footY[i] = hand.y - Mathf.Min(lf.y, rf.y);
                        Object.DestroyImmediate(body);
                    }

                    int iPeak = 0, iFwd = 0, iHigh = 0;
                    float vPeak = 0f, fwdMax = float.MinValue, hiMax = float.MinValue;
                    for (int i = 0; i <= n; i++)
                    {
                        if (i > 0)
                        {
                            float v = (loc[i] - loc[i - 1]).magnitude / dt;
                            if (v > vPeak) { vPeak = v; iPeak = i; }
                        }
                        if (loc[i].z > fwdMax) { fwdMax = loc[i].z; iFwd = i; }
                        if (footY[i] > hiMax) { hiMax = footY[i]; iHigh = i; }
                    }

                    // 球が出ていく向き＝**リリースの瞬間の手の速度**（体の向きではない）。
                    // 収録は横手投げも含むので「体の正面へ飛ぶ」と決め打つと、投げる相手と
                    // 球の行き先が食い違う。走行クリップの MoveDirDeg と同じ考え方で実測する
                    var rel = iPeak > 0 ? (loc[iPeak] - loc[iPeak - 1]) / dt : Vector3.forward;
                    var relH = new Vector2(rel.x, rel.z);
                    float dirDeg = relH.sqrMagnitude > 1e-6f
                        ? Mathf.Atan2(relH.x, relH.y) * Mathf.Rad2Deg : 0f;
                    float elevDeg = Mathf.Atan2(rel.y, Mathf.Max(1e-4f, relH.magnitude)) * Mathf.Rad2Deg;

                    sb.AppendLine(string.Format(CultureInfo.InvariantCulture,
                        "{0}\t{1:F2}\t{2:F2}\t{3:F1}\t{4:F3}\t{5:F3}\t{6:F3}\t{7:F1}\t{8:F1}\t{9:F2}\t{10:F3}\t{11:F2}\t{12:F3}",
                        shortName, clip.length, iPeak * dt, vPeak,
                        footY[iPeak], loc[iPeak].x, loc[iPeak].z, dirDeg, elevDeg,
                        iFwd * dt, fwdMax, iHigh * dt, hiMax));
                }

                System.IO.Directory.CreateDirectory("out/duel-stage");
                System.IO.File.WriteAllText("out/duel-stage/throw.tsv", sb.ToString());
                Debug.Log("[DuelMotionSpike] throw probe -> out/duel-stage/throw.tsv\n" + sb);
                return sb.ToString();
            }
            finally
            {
                DuelStageComposer.Cleanup();
            }
        }

        [UnityEditor.MenuItem("KokoSim/Duel/F6 送球クリップを実測する")]
        private static void RunThrowProbeMenu() => RunThrowProbe();

        /// <summary>
        /// 走者の**塁に着く瞬間**を実測する（打球処理の3D化 F7）。
        ///
        /// 走行ループと違って到達・スライディングは「前へ進んで、止まる」演技なので、
        /// 判定材料は**前進が止まる時刻**。engine の走者レッグの終端（T1）へこの瞬間を合わせる。
        ///
        /// ⚠ 尺で決め打たない。<c>Running_To_Base</c> は 6.43s あるが前進はごく短く、
        /// 残りは立ち止まってからの芝居（design-18 §4.5-⑬「収録の要らない芝居は尺でなく補正窓で消す」）。
        ///
        /// 出す量:
        ///   tArr/fwdMax … 前進が最大に達した時刻[s]と前進量[m]（＝塁に着く瞬間の候補）
        ///   tLow/hipLow … 腰が最も低くなった時刻[s]と高さ[m]（スライディングかどうかが割れる）
        ///   vHead       … 前進が始まってから止まるまでの平均速度[m/s]（走り込みの速さ）
        /// </summary>
        public static string RunRunnerArriveProbe()
        {
            var names = new[]
            {
                "Offense_Running_To_Base", "Offense_Base_Running_To_Walking",
                "Offense_Base_Sliding_Type01_Succ", "Offense_Base_Sliding_Type01_Fail",
                "Offense_Base_Sliding_Type02_Succ", "Offense_Base_Sliding_Type02_Fail",
                "Offense_Back_To_Base", "Offense_Crouch_To_Running",
            };
            const float dt = 0.02f;

            try
            {
                DuelStageComposer.Cleanup();
                DuelStageComposer.BuildStage();
                SpawnPose.Clear();
                AnkleOffset.Clear();
                var root = GameObject.Find(DuelStageComposer.RootName);
                foreach (var proxy in new[] { "pitcher", "batter", "catcher", "umpire" })
                {
                    var t = root.transform.Find(proxy);
                    if (t != null) t.gameObject.SetActive(false);
                }

                var sb = new StringBuilder();
                sb.AppendLine("clip\tlen\ttTouch\treachX\treachZ\treachY\tlimb\ttLow\thipLow");
                // このコマは塁に触れる高さか（足でも手でも、塁は地面にある）
                const float TouchGroundM = 0.35f;
                var limbs = new[]
                {
                    HumanBodyBones.LeftFoot, HumanBodyBones.RightFoot,
                    HumanBodyBones.LeftHand, HumanBodyBones.RightHand,
                };
                foreach (var shortName in names)
                {
                    var clip = FindClip("AA_Baseball_" + shortName);
                    if (clip == null) { sb.AppendLine(shortName + "\t(クリップが無い)"); continue; }

                    int n = Mathf.Max(1, Mathf.FloorToInt(clip.length / dt));
                    // ⚠ 前進量は**腰では測れない**。Sample はルートモーションを載せないので
                    //    腰と根の差は体の中の傾きしか出ず、実測で 0.08〜0.18m しか動かなかった。
                    //    塁に届くのは足か手なので、その**最も前へ出た点**を採る（F5 のグラブと同じ考え方）
                    var reach = new Vector3[n + 1];
                    var limb = new HumanBodyBones[n + 1];
                    var hip = new float[n + 1];
                    for (int i = 0; i <= n; i++)
                    {
                        float sec = Mathf.Min(clip.length - 0.001f, i * dt);
                        var body = SpawnBody(root, "run", Vector3.zero, 0f, Color.white);
                        Sample(body, clip, sec / clip.length);
                        var anim = body.GetComponentInChildren<Animator>();
                        var hips = anim.GetBoneTransform(HumanBodyBones.Hips).position;
                        var lf = anim.GetBoneTransform(HumanBodyBones.LeftFoot).position;
                        var rf = anim.GetBoneTransform(HumanBodyBones.RightFoot).position;
                        float floor = Mathf.Min(lf.y, rf.y);
                        var best = new Vector3(0f, 0f, float.MinValue);
                        var bestLimb = HumanBodyBones.LeftFoot;
                        foreach (var b in limbs)
                        {
                            var tr = anim.GetBoneTransform(b);
                            if (tr == null) continue;
                            // ⚠ 高さで絞らないと**走っている途中の振り上げた手**を拾う（実測で
                            //    Sliding_Type01_Succ が 0.22s・手の高さ 0.958m ＝ まだ走っている所を
                            //    「塁に触った」と誤判定した）。塁に触るのは地面の高さの出来事
                            if (tr.position.y - floor > TouchGroundM) continue;
                            var p = body.transform.InverseTransformPoint(tr.position);
                            if (p.z > best.z) { best = new Vector3(p.x, tr.position.y - floor, p.z); bestLimb = b; }
                        }
                        if (best.z <= float.MinValue * 0.5f) best = new Vector3(0f, 0f, -99f);
                        reach[i] = best;
                        limb[i] = bestLimb;
                        hip[i] = hips.y - floor;
                        Object.DestroyImmediate(body);
                    }

                    int iTouch = 0, iLow = 0;
                    float reachMax = float.MinValue, hipLow = float.MaxValue;
                    for (int i = 0; i <= n; i++)
                    {
                        if (reach[i].z > reachMax) { reachMax = reach[i].z; iTouch = i; }
                        if (hip[i] < hipLow) { hipLow = hip[i]; iLow = i; }
                    }

                    sb.AppendLine(string.Format(CultureInfo.InvariantCulture,
                        "{0}\t{1:F2}\t{2:F2}\t{3:F3}\t{4:F3}\t{5:F3}\t{6}\t{7:F2}\t{8:F3}",
                        shortName, clip.length, iTouch * dt,
                        reach[iTouch].x, reach[iTouch].z, reach[iTouch].y, limb[iTouch],
                        iLow * dt, hipLow));
                }

                System.IO.Directory.CreateDirectory("out/duel-stage");
                System.IO.File.WriteAllText("out/duel-stage/runner.tsv", sb.ToString());
                Debug.Log("[DuelMotionSpike] runner arrive probe -> out/duel-stage/runner.tsv\n" + sb);
                return sb.ToString();
            }
            finally
            {
                DuelStageComposer.Cleanup();
            }
        }

        [UnityEditor.MenuItem("KokoSim/Duel/F7 走者の到達クリップを実測する")]
        private static void RunRunnerArriveProbeMenu() => RunRunnerArriveProbe();

        /// <summary>
        /// タッチプレーの受け手クリップ（<c>AA_Baseball_Defender_Tag_Out</c> /
        /// <c>AA_Baseball_Catcher_Tag_Out</c>）の**タッチの瞬間**を実測する（issue #489）。
        ///
        /// <see cref="RunFielderCatchProbe"/> と同じ考え方（構えからグラブが最も動いた瞬間＝
        /// 運び終わり／伸び切り）を流用する。タッチ動作は「受けて→走者へ振り下ろす／差し出す」の
        /// 一続きなので、構えからの変位が最大になった瞬間がそのままタッチの瞬間になるはず
        /// （捕球で誤判定が起きた「腰→グラブの距離では測れない」罠と同じ理由で、変位量で採る）。
        ///
        /// 出す量は F5 の捕球プローブと同型（clip/len/dispMax/tDisp/tPeak/vPeak/gy/gx/gz）。
        /// <c>gy/gx/gz</c> はタッチの瞬間のグラブの足元からの高さ・アクターローカル水平位置。
        /// </summary>
        public static string RunTagOutProbe()
        {
            var names = new[] { "Defender_Tag_Out", "Catcher_Tag_Out" };
            const float dt = 0.02f;

            try
            {
                DuelStageComposer.Cleanup();
                DuelStageComposer.BuildStage();
                SpawnPose.Clear();
                AnkleOffset.Clear();
                var root = GameObject.Find(DuelStageComposer.RootName);
                foreach (var proxy in new[] { "pitcher", "batter", "catcher", "umpire" })
                {
                    var t = root.transform.Find(proxy);
                    if (t != null) t.gameObject.SetActive(false);
                }

                var sb = new StringBuilder();
                sb.AppendLine("clip\tlen\tdispMax\ttDisp\ttPeak\tvPeak\tgy\tgx\tgz");
                foreach (var shortName in names)
                {
                    var clip = FindClip("AA_Baseball_" + shortName);
                    if (clip == null) { sb.AppendLine(shortName + "\t(クリップが無い)"); continue; }

                    int n = Mathf.Max(1, Mathf.FloorToInt(clip.length / dt));
                    var loc = new Vector3[n + 1];   // 腰基準（右/上/前）
                    var footY = new float[n + 1];   // 足元の高さ（グラブの対地高を出すため）
                    for (int i = 0; i <= n; i++)
                    {
                        float sec = Mathf.Min(clip.length - 0.001f, i * dt);
                        var body = SpawnBody(root, "tagout", Vector3.zero, 0f, Color.white);
                        Sample(body, clip, sec / clip.length);
                        var anim = body.GetComponentInChildren<Animator>();
                        var glove = anim.GetBoneTransform(HumanBodyBones.LeftHand).position;
                        var lf = anim.GetBoneTransform(HumanBodyBones.LeftFoot).position;
                        var rf = anim.GetBoneTransform(HumanBodyBones.RightFoot).position;
                        // 軸は**アクターのローカル**（F5 の捕球プローブと同じ理由。yaw を決める座標系に揃える）
                        loc[i] = body.transform.InverseTransformPoint(glove);
                        footY[i] = glove.y - Mathf.Min(lf.y, rf.y);
                        Object.DestroyImmediate(body);
                    }

                    int iDisp = 0, iPeak = 0;
                    float dispMax = 0f, vPeak = 0f;
                    for (int i = 1; i <= n; i++)
                    {
                        float disp = (loc[i] - loc[0]).magnitude;
                        if (disp > dispMax) { dispMax = disp; iDisp = i; }
                        float v = (loc[i] - loc[i - 1]).magnitude / dt;
                        if (v > vPeak) { vPeak = v; iPeak = i; }
                    }

                    sb.AppendLine(string.Format(CultureInfo.InvariantCulture,
                        "{0}\t{1:F2}\t{2:F3}\t{3:F2}\t{4:F2}\t{5:F1}\t{6:F3}\t{7:F3}\t{8:F3}",
                        shortName, clip.length, dispMax, iDisp * dt, iPeak * dt, vPeak,
                        footY[iDisp], loc[iDisp].x, loc[iDisp].z));
                }

                System.IO.Directory.CreateDirectory("out/duel-stage");
                System.IO.File.WriteAllText("out/duel-stage/tagout.tsv", sb.ToString());
                Debug.Log("[DuelMotionSpike] tag-out probe -> out/duel-stage/tagout.tsv\n" + sb);
                return sb.ToString();
            }
            finally
            {
                DuelStageComposer.Cleanup();
            }
        }

        [UnityEditor.MenuItem("KokoSim/Duel/F9 タッチアウトの受け手クリップを実測する")]
        private static void RunTagOutProbeMenu() => RunTagOutProbe();

        private static AnimationClip FindClip(string clipName)
        {
            // 供給元は本番リグと同じ入口（自作Assets/Motions → 購入Assets/Animoの順）で引く。
            // 実測（アンカー・向き）と本番でクリップ実体がずれないための一本化。
            return KokoSim.Unity.Match.DuelStageRig.LoadClip(clipName);
        }

        private static void SetLayerRecursive(GameObject go, int layer)
        {
            go.layer = layer;
            foreach (Transform child in go.transform) SetLayerRecursive(child.gameObject, layer);
        }
    }
}
