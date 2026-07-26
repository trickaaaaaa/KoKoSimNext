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
        private const string BodyFbx = "Assets/DuelSpike/AnimatedHuman/AnimatedHuman.fbx";
        private const string ClipFbx = "Assets/Animo/AA_Basic_Baseball_Movement_Animations_Package/Animations/AA_Basic_Baseball_Movement_Animations_Package.fbx";
        private const float TargetHeight = 1.75f;

        // 手の軌跡プローブ実測（2026-07-26）: 振りかぶり0.05-0.30/足上げ0.35-0.45/リリース≈0.52/
        // フォロー0.55-0.65/戻り0.8-。リリース周辺に厚くコマを置く
        private static readonly float[] PitchTimes = { 0.15f, 0.35f, 0.45f, 0.50f, 0.53f, 0.56f, 0.60f, 0.70f };

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

            // 素体4体（色分けはプロキシ踏襲）。向きは初回レンダで検証して調整する
            // AAクリップの前方向はローカル−X（ルートモーション実測 2026-07-26）。yawは「−Xを向けたい世界方向」から逆算
            var pitcher = SpawnBody(root, "body-pitcher",
                new Vector3(0f, DuelStageComposer.MoundHeight, DuelStageComposer.MoundDistance), -90f,
                new Color(0.92f, 0.92f, 0.90f)); // −X→−Z（本塁へ投げ下ろす）
            var batter = SpawnBody(root, "body-batter", new Vector3(-0.9f, 0f, 0f), 180f, new Color(0.16f, 0.20f, 0.38f)); // −X→+X（右打者・本塁向き）
            var catcher = SpawnBody(root, "body-catcher", new Vector3(0f, 0f, -1.0f), 90f, new Color(0.25f, 0.27f, 0.30f)); // −X→+Z（投手向き）
            var umpire = SpawnBody(root, "body-umpire", new Vector3(-0.35f, 0f, -1.7f), 90f, new Color(0.12f, 0.12f, 0.13f));

            var pitchClip = FindClip("AA_Baseball_Pitcher_Pitching_type01");
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
            cam.transform.position = new Vector3(-3.5f, 5.0f, DuelStageComposer.MoundDistance + 35f);
            cam.transform.LookAt(new Vector3(0f, aimY, 1f));
            cam.fieldOfView = 5.5f;

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
                new Vector3(0f, DuelStageComposer.MoundHeight, DuelStageComposer.MoundDistance), -90f,
                new Color(0.92f, 0.92f, 0.90f));
            var batter = SpawnBody(root, "body-batter", new Vector3(-0.9f, 0f, 0f), 180f, new Color(0.16f, 0.20f, 0.38f));
            var catcher = SpawnBody(root, "body-catcher", new Vector3(0f, 0f, -1.0f), 90f, new Color(0.25f, 0.27f, 0.30f));
            var umpire = SpawnBody(root, "body-umpire", new Vector3(-0.35f, 0f, -1.7f), 90f, new Color(0.12f, 0.12f, 0.13f));

            var pitchClip = FindClip("AA_Baseball_Pitcher_Pitching_type01");
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
            cam.transform.position = new Vector3(-3.5f, 5.0f, DuelStageComposer.MoundDistance + 35f);
            cam.transform.LookAt(new Vector3(0f, 0.9f, 1f));
            cam.fieldOfView = 5.5f;

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
                    new Vector3(0f, DuelStageComposer.MoundHeight, DuelStageComposer.MoundDistance), -90f,
                    new Color(0.92f, 0.92f, 0.90f));
                var batter = SpawnBody(root, "body-batter", new Vector3(-0.9f, 0f, 0f), 180f, new Color(0.16f, 0.20f, 0.38f));
                var catcher = SpawnBody(root, "body-catcher", new Vector3(0f, 0f, -1.0f), 90f, new Color(0.25f, 0.27f, 0.30f));
                var umpire = SpawnBody(root, "body-umpire", new Vector3(-0.35f, 0f, -1.7f), 90f, new Color(0.12f, 0.12f, 0.13f));

                Sample(batter, FindClip("AA_Baseball_Batter_Idle_Loop"), 0.30f); GroundFix(batter, 0f);
                Sample(catcher, FindClip("AA_Baseball_Catcher_Idle"), 0.50f); GroundFix(catcher, 0f);
                Sample(umpire, FindClip("AA_Baseball_Umpire_Crouch_Idle"), 0.50f); GroundFix(umpire, 0f);
                Sample(pitcher, FindClip("AA_Baseball_Pitcher_Pitching_type01"), pitchT);
                GroundFix(pitcher, DuelStageComposer.MoundHeight);

                var camGo = new GameObject("~topCam") { hideFlags = HideFlags.DontSave };
                var cam = camGo.AddComponent<Camera>();
                cam.cullingMask = 1 << LayerMask.NameToLayer(DuelStageComposer.LayerName);
                cam.orthographic = true;
                cam.orthographicSize = 4.5f;
                cam.clearFlags = CameraClearFlags.SolidColor;
                cam.backgroundColor = new Color(0.2f, 0.2f, 0.2f);
                cam.transform.position = new Vector3(0f, 30f, 0.5f);
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
                new Vector3(0f, DuelStageComposer.MoundHeight, DuelStageComposer.MoundDistance), -90f,
                new Color(0.92f, 0.92f, 0.90f));
            var batter = SpawnBody(root, "body-batter", new Vector3(-0.9f, 0f, 0f), 180f, new Color(0.16f, 0.20f, 0.38f));
            var catcher = SpawnBody(root, "body-catcher", new Vector3(0f, 0f, -1.0f), 90f, new Color(0.25f, 0.27f, 0.30f));
            var umpire = SpawnBody(root, "body-umpire", new Vector3(-0.35f, 0f, -1.7f), 90f, new Color(0.12f, 0.12f, 0.13f));
            var bat = SpawnBat(root);
            AttachMitt(catcher);

            var swingClip = FindClip("AA_Baseball_Batter_Swing_Type01_Hitting");
            Sample(pitcher, FindClip("AA_Baseball_Pitcher_Pitching_type01"), 0.58f); GroundFix(pitcher, DuelStageComposer.MoundHeight);
            Sample(catcher, FindClip("AA_Baseball_Catcher_Idle"), 0.50f); GroundFix(catcher, 0f);
            Sample(umpire, FindClip("AA_Baseball_Umpire_Crouch_Idle"), 0.50f); GroundFix(umpire, 0f);

            var camGo = new GameObject("~DuelStageCamera") { hideFlags = HideFlags.DontSave };
            var cam = camGo.AddComponent<Camera>();
            cam.cullingMask = 1 << layer;
            cam.clearFlags = CameraClearFlags.SolidColor;
            cam.backgroundColor = new Color(0.55f, 0.78f, 0.90f);
            cam.nearClipPlane = 0.1f;
            cam.farClipPlane = 200f;
            cam.transform.position = new Vector3(-3.5f, 5.0f, DuelStageComposer.MoundDistance + 35f);
            cam.transform.LookAt(new Vector3(0f, 0.9f, 1f));
            cam.fieldOfView = 5.5f;

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

                var batter = SpawnBody(root, "body-batter", new Vector3(-0.9f, 0f, 0f), 180f, new Color(0.16f, 0.20f, 0.38f));
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

                var batter = SpawnBody(root, "body-batter", new Vector3(-0.9f, 0f, 0f), 180f, new Color(0.16f, 0.20f, 0.38f));
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

        private static GameObject SpawnBody(GameObject root, string name, Vector3 pos, float yawDeg, Color color)
        {
            var prefab = UnityEditor.AssetDatabase.LoadAssetAtPath<GameObject>(BodyFbx);
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

        private static AnimationClip FindClip(string clipName)
        {
            foreach (var a in UnityEditor.AssetDatabase.LoadAllAssetsAtPath(ClipFbx))
            {
                var c = a as AnimationClip;
                if (c != null && c.name == clipName) return c;
            }
            return null;
        }

        private static void SetLayerRecursive(GameObject go, int layer)
        {
            go.layer = layer;
            foreach (Transform child in go.transform) SetLayerRecursive(child.gameObject, layer);
        }
    }
}
