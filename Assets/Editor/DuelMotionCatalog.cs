using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using UnityEngine;
using UnityEngine.Animations;
using UnityEngine.Playables;

namespace KokoSim.EditorTools
{
    /// <summary>
    /// design-18 §7-3 購入モーションの棚卸し。AAパック全218クリップ（__preview__除く）を
    /// 素体（UBC Superhero Male）へリターゲットし、12fpsのコマ列を横1枚のスプライトシートへ焼く。
    /// ブラウザ側でパラパラ再生して「何が買えているか」を一望するための素材。
    ///
    /// 方針:
    ///  - その場再生（ルートモーションの平行移動は毎コマ破棄・回転は残す＝turn系はその場で回る）
    ///  - 接地はクリップ全体の最小足高さで一括オフセット（ジャンプ/ダイブの上下動は保つ）
    ///  - 向きはクリップごとに胸法線を実測して3/4正面へ揃える（素の向きはクリップごとに違う。§7-3の罠②）
    ///  - 出力はグレースケール8bit・32階調のPNG（自前エンコーダ）。RGBA32の1/4以下で、
    ///    218本＝約1万コマをブラウザ1ページに載せるための必須条件（実測19MB→4MB以下）
    ///
    /// 使い方（MCP execute_code から）: KokoSim.EditorTools.DuelMotionCatalog.RunRole("Catcher");
    /// 出力: out/duel-stage/catalog/<clip>.png ＋ catalog_<role>.tsv
    /// </summary>
    public static class DuelMotionCatalog
    {
        // クリップ供給元は DuelStageRig.ClipFbxSources に一本化（LoadClips 参照）
        private const string RootName = "~DuelMotionCatalog";
        private const string ClipPrefix = "AA_Baseball_";

        private const int FrameW = 128;
        private const int FrameH = 128;
        private const float ViewH = 2.56f;      // 縦の写る範囲[m]。128px/2.56m = 50px/m
        private const float CenterY = 1.10f;    // 注視高（足元-0.18m〜頭上2.38mが入る）
        private const float CamDist = 8f;
        private const float CamElevDeg = 8f;
        private const float FacingDeg = 35f;    // 3/4正面（0=真正面）
        private const int Fps = 12;             // §4.2 確定の時間量子化
        private const int MaxFrames = 60;       // これを超える長尺は全長を等間隔に間引く

        private const int GroundRow = 9;        // 下端から数えた y=0 の画素行（画面半幅1.28m・注視1.10m から）
        private const byte GroundTone = 46;     // 接地の目安線（背景0との差だけ付ける）

        public static string RunRole(string role)
        {
            return RunRole(role, 0, 9999);
        }

        /// <summary>MCPのタイムアウト回避用に from/count で分割実行できる。</summary>
        public static string RunRole(string role, int from, int count)
        {
            try
            {
                Cleanup();
                return Render(role, from, count);
            }
            finally
            {
                Cleanup();
            }
        }

        /// <summary>1本だけ本番と同じ経路で回し、コマ毎の骨位置を返す（ポーズが当たっているかの切り分け用）。</summary>
        public static string RunDiag(string clipName)
        {
            try
            {
                Cleanup();
                int layer = LayerMask.NameToLayer("DuelStage");
                var root = new GameObject(RootName) { hideFlags = HideFlags.DontSave };
                root.layer = layer;
                var body = DuelMotionSpike.SpawnBody(root, "body", Vector3.zero, 0f, Color.white);
                float ankle = DuelMotionSpike.AnkleOffsetOf(body);
                var animator = body.GetComponentInChildren<Animator>();
                var clip = LoadClips(null, 0, 9999).Find(c => c.name == clipName || c.name == ClipPrefix + clipName);
                if (clip == null) return "clip not found";

                var times = FrameTimes(clip.length);
                float yaw, yFix;
                Probe(body, animator, clip, times, ankle, Quaternion.Euler(0f, FacingDeg, 0f) * Vector3.forward, out yaw, out yFix);
                var sb = new StringBuilder();
                sb.AppendLine("frames=" + times.Length + " yaw=" + yaw.ToString("F0") + " yFix=" + yFix.ToString("F2"));

                var anchor = new Vector3(0f, yFix, 0f);
                var foot = animator.GetBoneTransform(HumanBodyBones.LeftFoot);
                var rig = new BakeRig();
                rig.Setup(body, root, layer);

                var cam = MakeCamera(layer);
                var rt = new RenderTexture(FrameW, FrameH, 24, RenderTextureFormat.ARGB32)
                { filterMode = FilterMode.Point, antiAliasing = 1 };
                rt.Create();
                cam.targetTexture = rt;
                var tex = new Texture2D(FrameW, FrameH, TextureFormat.RGBA32, false);
                try
                {
                    for (int i = 0; i < times.Length; i += 6)
                    {
                        SampleAt(body, animator, clip, times[i], anchor, yaw);
                        rig.Update();
                        int boundsPx = DuelStageComposer.MeasureScreenHeight(cam, body);

                        DuelStageComposer.Render(cam, rt);
                        var prev = RenderTexture.active;
                        RenderTexture.active = rt;
                        tex.ReadPixels(new Rect(0, 0, FrameW, FrameH), 0, 0);
                        tex.Apply();
                        RenderTexture.active = prev;

                        var px = tex.GetPixels32();
                        int lit = 0, top = -1, bottom = -1;
                        for (int y = 0; y < FrameH; y++)
                            for (int x = 0; x < FrameW; x++)
                                if (px[y * FrameW + x].r > 24) { lit++; if (top < 0 || y > top) top = y; if (bottom < 0 || y < bottom) bottom = y; }

                        sb.AppendLine(string.Format(CultureInfo.InvariantCulture,
                            "i={0} t={1:F2} footY={2:F2} boundsPx={3} litPx={4} pixelH={5}",
                            i, times[i], foot.position.y, boundsPx, lit, top - bottom));
                    }
                }
                finally
                {
                    cam.targetTexture = null;
                    Object.DestroyImmediate(tex);
                    rt.Release(); Object.DestroyImmediate(rt);
                    rig.Dispose();
                }
                return sb.ToString();
            }
            finally
            {
                Cleanup();
            }
        }

        /// <summary>クリップ名の役割セグメント（Pitcher/Batter/Catcher/Defender/Offense/Umpire）と本数を返す。</summary>
        public static string ListRoles()
        {
            var counts = new Dictionary<string, int>();
            foreach (var c in LoadClips(null, 0, 9999))
            {
                var r = RoleOf(c.name);
                counts[r] = counts.ContainsKey(r) ? counts[r] + 1 : 1;
            }
            var sb = new StringBuilder();
            foreach (var kv in counts) sb.AppendLine(kv.Key + "\t" + kv.Value);
            return sb.ToString();
        }

        private static string RoleOf(string clipName)
        {
            var s = clipName.StartsWith(ClipPrefix) ? clipName.Substring(ClipPrefix.Length) : clipName;
            int i = s.IndexOf('_');
            return i < 0 ? s : s.Substring(0, i);
        }

        private static List<AnimationClip> LoadClips(string role, int from, int count)
        {
            var list = new List<AnimationClip>();
            foreach (var fbx in KokoSim.Unity.Match.DuelStageRig.ClipFbxSources)
            {
                foreach (var a in UnityEditor.AssetDatabase.LoadAllAssetsAtPath(fbx))
                {
                    var c = a as AnimationClip;
                    if (c == null) continue;
                    if (c.name.StartsWith("__preview__")) continue;   // FBXにはプレビュー複製が同居する
                    if (!string.IsNullOrEmpty(role) && RoleOf(c.name) != role) continue;
                    list.Add(c);
                }
            }
            list.Sort((x, y) => string.CompareOrdinal(x.name, y.name));
            if (from > 0) list.RemoveRange(0, Mathf.Min(from, list.Count));
            if (list.Count > count) list.RemoveRange(count, list.Count - count);
            return list;
        }

        private static string Render(string role, int from, int count)
        {
            int layer = LayerMask.NameToLayer("DuelStage");
            if (layer < 0) return "layer 'DuelStage' not found";

            var outDir = Path.Combine(Directory.GetParent(Application.dataPath).FullName, "out/duel-stage/catalog");
            Directory.CreateDirectory(outDir);

            var clips = LoadClips(role, from, count);
            if (clips.Count == 0) return "no clips for role=" + role;

            var root = new GameObject(RootName) { hideFlags = HideFlags.DontSave };
            root.layer = layer;
            var body = DuelMotionSpike.SpawnBody(root, "body", Vector3.zero, 0f, new Color(0.86f, 0.87f, 0.84f));
            float ankle = DuelMotionSpike.AnkleOffsetOf(body);
            var animator = body.GetComponentInChildren<Animator>();

            var rig = new BakeRig();
            rig.Setup(body, root, layer);

            var cam = MakeCamera(layer);
            var desiredFacing = Quaternion.Euler(0f, FacingDeg, 0f) * Vector3.forward;

            var rt = new RenderTexture(FrameW, FrameH, 24, RenderTextureFormat.ARGB32)
            { filterMode = FilterMode.Point, antiAliasing = 1 };
            rt.Create();
            var frameTex = new Texture2D(FrameW, FrameH, TextureFormat.RGBA32, false);

            var sb = new StringBuilder();
            sb.AppendLine("clip\tframes\tclip_sec\tplay_sec\tyaw\tpx");
            try
            {
                cam.targetTexture = rt;
                foreach (var clip in clips)
                {
                    var times = FrameTimes(clip.length);
                    float yaw;
                    float yFix;
                    Probe(body, animator, clip, times, ankle, desiredFacing, out yaw, out yFix);

                    var anchor = new Vector3(0f, yFix, 0f);
                    int stripW = FrameW * times.Length;
                    var strip = new byte[stripW * FrameH];   // 上から下・グレースケール8bit

                    // スキンメッシュは「グラフを作って評価し、破棄した」直後の姿勢しか描画に乗らない。
                    // 常設グラフを前進させながら撮ると棒立ち（バインド姿勢）が撮れる。骨Transformを
                    // 手で書き戻しても同じ（2026-07-26 実測）。よってコマ毎に0→tを打ち直す。
                    for (int i = 0; i < times.Length; i++)
                    {
                        SampleAt(body, animator, clip, times[i], anchor, yaw);
                        rig.Update();

                        DuelStageComposer.Render(cam, rt);
                        var prev = RenderTexture.active;
                        RenderTexture.active = rt;
                        frameTex.ReadPixels(new Rect(0, 0, FrameW, FrameH), 0, 0);
                        frameTex.Apply();
                        RenderTexture.active = prev;

                        Blit(frameTex.GetPixels32(), strip, stripW, i * FrameW);
                    }

                    var shortName = clip.name.StartsWith(ClipPrefix) ? clip.name.Substring(ClipPrefix.Length) : clip.name;
                    var bytes = GrayPng.Encode(strip, stripW, FrameH);
                    File.WriteAllBytes(Path.Combine(outDir, shortName + ".png"), bytes);

                    sb.AppendLine(string.Format(CultureInfo.InvariantCulture, "{0}\t{1}\t{2:F2}\t{3:F2}\t{4:F0}\t{5}",
                        shortName, times.Length, clip.length, times[times.Length - 1], yaw, bytes.Length));
                }
            }
            finally
            {
                cam.targetTexture = null;
                Object.DestroyImmediate(frameTex);
                rt.Release(); Object.DestroyImmediate(rt);
                Object.DestroyImmediate(cam.gameObject);
                rig.Dispose();
            }

            File.WriteAllText(Path.Combine(outDir, "catalog_" + role + ".tsv"), sb.ToString());
            Debug.Log("[DuelMotionCatalog] " + role + " -> " + outDir + "\n" + sb);
            return sb.ToString();
        }

        /// <summary>
        /// Edit modeの単発描画（RenderRequest）ではスキニングが更新されず、骨をいくら動かしても
        /// 前のポーズのまま焼き付く（2026-07-26 実測: レンダラのバウンズ高さは変わるのに画素の
        /// シルエット高さが不変）。そこで SkinnedMeshRenderer.BakeMesh でCPUスキニングした結果を
        /// 素のMeshRendererに差し替えて描く。撮るのは常にこの複製側。
        /// </summary>
        private sealed class BakeRig
        {
            private SkinnedMeshRenderer[] _smrs;
            private MeshFilter[] _filters;
            private Mesh[] _meshes;

            public void Setup(GameObject body, GameObject root, int layer)
            {
                _smrs = body.GetComponentsInChildren<SkinnedMeshRenderer>();
                _filters = new MeshFilter[_smrs.Length];
                _meshes = new Mesh[_smrs.Length];
                for (int i = 0; i < _smrs.Length; i++)
                {
                    _smrs[i].enabled = false;   // 古いポーズを二重に描かせない
                    var go = new GameObject("~baked" + i) { hideFlags = HideFlags.DontSave };
                    go.transform.SetParent(root.transform, false);
                    go.layer = layer;
                    _filters[i] = go.AddComponent<MeshFilter>();
                    go.AddComponent<MeshRenderer>().sharedMaterial = _smrs[i].sharedMaterial;
                    _meshes[i] = new Mesh { hideFlags = HideFlags.DontSave };
                }
            }

            public void Update()
            {
                for (int i = 0; i < _smrs.Length; i++)
                {
                    _smrs[i].BakeMesh(_meshes[i], true);   // useScale=true で素体の身長正規化を取り込む
                    _filters[i].sharedMesh = _meshes[i];
                    _filters[i].transform.SetPositionAndRotation(
                        _smrs[i].transform.position, _smrs[i].transform.rotation);
                }
            }

            public void Dispose()
            {
                if (_meshes == null) return;
                for (int i = 0; i < _meshes.Length; i++) Object.DestroyImmediate(_meshes[i]);
                _meshes = null;
            }
        }

        /// <summary>全クリップ共通の固定カメラ（3/4正面・仰角なりの俯瞰）。比較できることが最優先。</summary>
        private static Camera MakeCamera(int layer)
        {
            var aim = new Vector3(0f, CenterY, 0f);
            var camGo = new GameObject("~DuelCatalogCamera") { hideFlags = HideFlags.DontSave };
            var cam = camGo.AddComponent<Camera>();
            cam.cullingMask = 1 << layer;
            cam.clearFlags = CameraClearFlags.SolidColor;
            cam.backgroundColor = Color.black;
            cam.nearClipPlane = 0.1f;
            cam.farClipPlane = 100f;
            cam.orthographic = false;
            cam.fieldOfView = 2f * Mathf.Atan((ViewH * 0.5f) / CamDist) * Mathf.Rad2Deg;
            cam.aspect = (float)FrameW / FrameH;
            float el = CamElevDeg * Mathf.Deg2Rad;
            camGo.transform.position = aim + new Vector3(0f, Mathf.Sin(el), Mathf.Cos(el)) * CamDist;
            camGo.transform.LookAt(aim);
            return cam;
        }

        /// <summary>12fps刻み。MaxFramesを超える長尺だけ全長を等間隔に間引く（内容を切らない）。</summary>
        private static float[] FrameTimes(float length)
        {
            int n = Mathf.Clamp(Mathf.CeilToInt(length * Fps), 2, MaxFrames);
            var times = new float[n];
            for (int i = 0; i < n; i++) times[i] = length * i / (n - 1);
            return times;
        }

        /// <summary>
        /// 1コマ分のポーズを打つ。毎回グラフを作り直して0→tを刻む（ルートモーションはデルタ蓄積のため
        /// 途中から始められない。加えて描画に乗るのはグラフ破棄直後の姿勢だけ）。
        /// </summary>
        private static void SampleAt(GameObject body, Animator animator, AnimationClip clip, float time,
            Vector3 anchor, float yaw)
        {
            body.transform.localPosition = anchor;
            body.transform.localRotation = Quaternion.Euler(0f, yaw, 0f);

            var graph = PlayableGraph.Create("~catalog-sample");
            try
            {
                var output = AnimationPlayableOutput.Create(graph, "out", animator);
                output.SetSourcePlayable(AnimationClipPlayable.Create(graph, clip));
                int steps = Mathf.Max(1, Mathf.CeilToInt(time * 60f));
                float dt = time / steps;
                for (int i = 0; i < steps; i++) graph.Evaluate(dt);
            }
            finally
            {
                graph.Destroy();
            }
            body.transform.localPosition = anchor;   // 平行移動は捨てる（その場再生）
        }

        /// <summary>本番と同条件で1周し、胸法線（向き）とクリップ全体の最小足高さ（接地）を測る。</summary>
        private static void Probe(GameObject body, Animator animator, AnimationClip clip, float[] times,
            float ankle, Vector3 desiredFacing, out float yaw, out float yFix)
        {
            yaw = 0f;
            float minSole = float.MaxValue;
            body.transform.localPosition = Vector3.zero;
            body.transform.localRotation = Quaternion.identity;

            var graph = PlayableGraph.Create("~catalog-probe");
            try
            {
                var output = AnimationPlayableOutput.Create(graph, "out", animator);
                output.SetSourcePlayable(AnimationClipPlayable.Create(graph, clip));
                float t = 0f;
                // 向きは「入りの構え」で決める。中盤で測ると捕逸の追いかけ・送球の踏み替えのように
                // 途中で向きが変わるクリップが背中向きスタートになる（2026-07-26 実測）
                int probeAt = Mathf.Min(times.Length - 1, Mathf.Max(1, times.Length / 10));
                for (int i = 0; i < times.Length; i++)
                {
                    Advance(graph, ref t, times[i]);
                    body.transform.localPosition = Vector3.zero;

                    var lf = animator.GetBoneTransform(HumanBodyBones.LeftFoot);
                    var rf = animator.GetBoneTransform(HumanBodyBones.RightFoot);
                    if (lf != null && rf != null)
                        minSole = Mathf.Min(minSole, Mathf.Min(lf.position.y, rf.position.y) - ankle);

                    if (i == probeAt)
                    {
                        // 肩線の法線＝胸の向き。ヒップ回転は素体依存で当てにならない（§7-3の罠②）
                        var ra = animator.GetBoneTransform(HumanBodyBones.RightUpperArm);
                        var la = animator.GetBoneTransform(HumanBodyBones.LeftUpperArm);
                        if (ra != null && la != null)
                        {
                            var facing = Vector3.Cross(ra.position - la.position, Vector3.up);
                            facing.y = 0f;
                            if (facing.sqrMagnitude > 1e-6f)
                                yaw = Vector3.SignedAngle(facing.normalized, desiredFacing, Vector3.up);
                        }
                    }
                }
            }
            finally
            {
                graph.Destroy();
            }
            yFix = (minSole == float.MaxValue) ? 0f : -minSole;
        }

        /// <summary>ルートモーションはデルタ蓄積なので前進のみ。1/60秒以下に刻んで target まで進める。</summary>
        private static void Advance(PlayableGraph graph, ref float t, float target)
        {
            float remain = target - t;
            if (remain <= 0f) { graph.Evaluate(0f); return; }
            int steps = Mathf.Max(1, Mathf.CeilToInt(remain * 60f));
            float dt = remain / steps;
            for (int i = 0; i < steps; i++) graph.Evaluate(dt);
            t = target;
        }

        /// <summary>
        /// 1コマ（ReadPixelsは下から上）をグレースケール32階調にしてストリップ（上から下）へ流し込む。
        /// 階調を落とすのは見た目の都合ではなく圧縮率のため（フラットな明暗ほどdeflateが効く）。
        /// </summary>
        private static void Blit(Color32[] src, byte[] dst, int stripW, int xOffset)
        {
            for (int y = 0; y < FrameH; y++)
            {
                int dstRow = (FrameH - 1 - y) * stripW + xOffset;
                int srcRow = y * FrameW;
                for (int x = 0; x < FrameW; x++)
                {
                    var c = src[srcRow + x];
                    int g = (c.r * 77 + c.g * 150 + c.b * 29) >> 8;
                    g = (g >> 3) << 3;
                    if (g == 0 && y == GroundRow) g = GroundTone;   // 接地の目安線（キャラの背後には出さない）
                    dst[dstRow + x] = (byte)g;
                }
            }
        }

        private static void Cleanup()
        {
            for (var go = GameObject.Find(RootName); go != null; go = GameObject.Find(RootName))
                Object.DestroyImmediate(go);
            var cam = GameObject.Find("~DuelCatalogCamera");
            if (cam != null) Object.DestroyImmediate(cam);
        }
    }

    /// <summary>
    /// グレースケール8bit（PNG color type 0）の最小エンコーダ。Texture2D.EncodeToPNG は
    /// 常にRGBA32で書くため、1万コマ規模だと4倍の容量になる。行フィルタはUp固定
    /// （背景が縦に一様なコマ列では差分がほぼ全ゼロになりdeflateが最大に効く）。
    /// </summary>
    internal static class GrayPng
    {
        public static byte[] Encode(byte[] gray, int width, int height)
        {
            var raw = new byte[(width + 1) * height];
            for (int y = 0; y < height; y++)
            {
                int o = y * (width + 1);
                raw[o] = (byte)(y == 0 ? 0 : 2);          // 0=None / 2=Up
                int src = y * width, prev = (y - 1) * width;
                for (int x = 0; x < width; x++)
                    raw[o + 1 + x] = y == 0 ? gray[src + x] : (byte)(gray[src + x] - gray[prev + x]);
            }

            byte[] deflated;
            using (var ms = new MemoryStream())
            {
                using (var ds = new System.IO.Compression.DeflateStream(ms, System.IO.Compression.CompressionLevel.Optimal, true))
                    ds.Write(raw, 0, raw.Length);
                deflated = ms.ToArray();
            }

            using (var ms = new MemoryStream())
            {
                ms.Write(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }, 0, 8);

                var ihdr = new byte[13];
                Be32(ihdr, 0, width); Be32(ihdr, 4, height);
                ihdr[8] = 8; ihdr[9] = 0; ihdr[10] = 0; ihdr[11] = 0; ihdr[12] = 0;
                Chunk(ms, "IHDR", ihdr);

                var zlib = new byte[deflated.Length + 6];
                zlib[0] = 0x78; zlib[1] = 0x9C;
                System.Array.Copy(deflated, 0, zlib, 2, deflated.Length);
                Be32(zlib, zlib.Length - 4, unchecked((int)Adler32(raw)));
                Chunk(ms, "IDAT", zlib);

                Chunk(ms, "IEND", new byte[0]);
                return ms.ToArray();
            }
        }

        private static void Chunk(MemoryStream ms, string type, byte[] data)
        {
            var len = new byte[4]; Be32(len, 0, data.Length);
            ms.Write(len, 0, 4);
            var body = new byte[4 + data.Length];
            for (int i = 0; i < 4; i++) body[i] = (byte)type[i];
            System.Array.Copy(data, 0, body, 4, data.Length);
            ms.Write(body, 0, body.Length);
            var crc = new byte[4]; Be32(crc, 0, unchecked((int)Crc32(body)));
            ms.Write(crc, 0, 4);
        }

        private static void Be32(byte[] b, int o, int v)
        {
            b[o] = (byte)(v >> 24); b[o + 1] = (byte)(v >> 16); b[o + 2] = (byte)(v >> 8); b[o + 3] = (byte)v;
        }

        private static uint[] _crcTable;

        private static uint Crc32(byte[] data)
        {
            if (_crcTable == null)
            {
                _crcTable = new uint[256];
                for (uint n = 0; n < 256; n++)
                {
                    uint c = n;
                    for (int k = 0; k < 8; k++) c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;
                    _crcTable[n] = c;
                }
            }
            uint crc = 0xFFFFFFFFu;
            for (int i = 0; i < data.Length; i++) crc = _crcTable[(crc ^ data[i]) & 0xFF] ^ (crc >> 8);
            return crc ^ 0xFFFFFFFFu;
        }

        private static uint Adler32(byte[] data)
        {
            uint a = 1, b = 0;
            for (int i = 0; i < data.Length; i++)
            {
                a = (a + data[i]) % 65521;
                b = (b + a) % 65521;
            }
            return (b << 16) | a;
        }
    }
}
