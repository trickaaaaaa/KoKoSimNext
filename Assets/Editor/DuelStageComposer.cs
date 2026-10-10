using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;

namespace KokoSim.EditorTools
{
    /// <summary>
    /// design-18 §7-1 構図スパイク。6〜7頭身のカプセル人型プロキシで DuelStage（捕手後方構図）を
    /// 組み、カメラバリアントを RT 480×270 → ×4 Point 拡大で一括レンダリングする。
    ///
    /// Edit mode で完結（Play 不要）。シーンには HideFlags.DontSave の一時ルートだけを作り、
    /// 終了時に必ず削除する（SampleScene を汚さない）。
    ///
    /// 使い方（MCP execute_code から）:
    ///   KokoSim.EditorTools.DuelStageComposer.RunAll();    // 構図バリアント一括（§7-1・確定済み）
    ///   KokoSim.EditorTools.DuelStageComposer.RunPixel();  // ピクセル化ポスト12枚マトリクス（§4.4）
    /// 出力: out/duel-stage/&lt;variant&gt;_bat〇〇px_pit〇〇px.png / out/duel-stage/pixel/px_*.png
    /// ファイル名の px は RT 480×270 上での打者/投手の画面内高さ実測（design-18 §4.2 の
    /// 「48〜64px」対「画面高5〜6割」の矛盾を実測で解消するためのデータ）。
    /// </summary>
    public static class DuelStageComposer
    {
        internal const string RootName = "~DuelStageSpike";
        internal const string LayerName = "DuelStage";
        private const float PlayerHeight = 1.75f;   // 身長基準
        private const float HeadDiameter = 0.25f;   // 1.75/7 = 7頭身
        internal const float MoundDistance = 18.44f; // 本塁→投手板
        internal const float MoundHeight = 0.254f;

        private struct Variant
        {
            public string Name;
            public float Dist, Height, Fov, OffsetX, AimY, AimZ;
            public bool HideUmpire;

            public Variant(string name, float d, float h, float fov, float x, float aimY, float aimZ, bool hideUmp)
            {
                Name = name; Dist = d; Height = h; Fov = fov; OffsetX = x; AimY = aimY; AimZ = aimZ; HideUmpire = hideUmp;
            }
        }

        // 採用構図 M_bcast-tele（d35/h5/FOV7°）を基準にした左右オフセット微調整バリアント。
        // 実TV中継のセンターカメラは真後ろでなく左右どちらかにずれており、投手が本塁ラインに
        // 被らない（参考: 広島大会中継＝投手が画面左・打者/審判が中央〜右）。
        // 注視点は本塁付近固定のまま、カメラだけ横に出すと投手が画面端へ逃げる。
        // x負=投手が画面左へ / x正=投手が画面右へ。fov5.5は参考中継のズーム感（打者45%級）。
        private static readonly Variant[] Variants =
        {
            new Variant("Q_off-l",       35f, 5.0f, 7.0f, -3.5f, 1.3f, 1f, false),
            new Variant("R_off-l-tight", 35f, 5.0f, 5.5f, -3.5f, 1.3f, 1f, false),
            new Variant("S_off-l-far",   35f, 5.0f, 7.0f, -6.0f, 1.3f, 1f, false),
            new Variant("T_off-r",       35f, 5.0f, 7.0f,  3.5f, 1.3f, 1f, false),
            new Variant("U_off-r-tight", 35f, 5.0f, 5.5f,  3.5f, 1.3f, 1f, false),
            new Variant("V_off-r-far",   35f, 5.0f, 7.0f,  6.0f, 1.3f, 1f, false),
        };

        public static string RunAll()
        {
            try
            {
                Cleanup(); // 前回の残骸があれば先に消す
                BuildStage();
                var report = RenderVariants();
                return report;
            }
            finally
            {
                Cleanup();
            }
        }

        /// <summary>
        /// §4.4 ピクセル化ポスト検証: RT解像度3段 × 輪郭有無 × パレット有無 = 12枚。
        /// カメラは確定構図 R_off-l-tight 固定。出力 out/duel-stage/pixel/。
        /// </summary>
        public static string RunPixel()
        {
            try
            {
                Cleanup();
                BuildStage();
                return RenderPixelMatrix();
            }
            finally
            {
                Cleanup();
            }
        }

        // ---- ステージ構築 ----------------------------------------------------

        internal static void BuildStage()
        {
            int layer = LayerMask.NameToLayer(LayerName);
            if (layer < 0) throw new System.InvalidOperationException("layer '" + LayerName + "' not found");

            var root = new GameObject(RootName) { hideFlags = HideFlags.DontSave };
            SetLayer(root, layer);

            // 地面（芝）・内野土・マウンド・本塁ベース・打席枠
            var ground = Primitive(root, PrimitiveType.Plane, "ground",
                new Vector3(0f, 0f, 9f), new Vector3(10f, 1f, 10f), Rgb(0.30f, 0.55f, 0.30f));
            _ = ground;
            Primitive(root, PrimitiveType.Cylinder, "dirt-home",
                new Vector3(0f, 0.005f, 0f), new Vector3(7f, 0.005f, 7f), Rgb(0.63f, 0.47f, 0.33f));
            Primitive(root, PrimitiveType.Cylinder, "mound",
                new Vector3(0f, MoundHeight * 0.5f, MoundDistance), new Vector3(5.4f, MoundHeight * 0.5f, 5.4f), Rgb(0.63f, 0.47f, 0.33f));
            Primitive(root, PrimitiveType.Cube, "home-plate",
                new Vector3(0f, 0.012f, 0f), new Vector3(0.43f, 0.02f, 0.43f), Color.white);
            Primitive(root, PrimitiveType.Cube, "batter-box-L",
                new Vector3(-0.9f, 0.011f, 0f), new Vector3(1.2f, 0.012f, 1.8f), Rgb(0.75f, 0.68f, 0.58f));
            Primitive(root, PrimitiveType.Cube, "batter-box-R",
                new Vector3(0.9f, 0.011f, 0f), new Vector3(1.2f, 0.012f, 1.8f), Rgb(0.75f, 0.68f, 0.58f));
            // バックネット書き割り（単色の暗インク壁・DuelStageRigと同値）。
            // 地面プレーンの尽きた先が背景色で抜けるのを塞ぐ（2026-07-26 構図リフレームD採用時に追加）
            Primitive(root, PrimitiveType.Cube, "backstop-wall",
                new Vector3(0f, 4f, -16f), new Vector3(34f, 8f, 0.5f), Rgb(0.12f, 0.20f, 0.16f));

            // プロキシ4体（カプセル胴＋球頭・7頭身）
            BuildProxy(root, "pitcher", new Vector3(0f, MoundHeight, MoundDistance), 1.0f, Rgb(0.92f, 0.92f, 0.90f));
            BuildProxy(root, "batter", new Vector3(-0.9f, 0f, 0f), 1.0f, Rgb(0.16f, 0.20f, 0.38f));
            BuildProxy(root, "catcher", new Vector3(0f, 0f, -1.0f), 0.62f, Rgb(0.25f, 0.27f, 0.30f)); // しゃがみ
            BuildProxy(root, "umpire", new Vector3(-0.35f, 0f, -1.7f), 0.85f, Rgb(0.12f, 0.12f, 0.13f)); // 中腰・内角寄り
        }

        /// <summary>身長1.75m基準の胴カプセル＋頭球体。crouch はYスケールで表現（捕手0.62/審判0.85）。</summary>
        private static void BuildProxy(GameObject root, string name, Vector3 pos, float crouch, Color bodyColor)
        {
            var proxy = new GameObject(name);
            proxy.transform.SetParent(root.transform, false);
            proxy.transform.localPosition = pos;
            proxy.transform.localScale = new Vector3(1f, crouch, 1f);

            float bodyHeight = PlayerHeight - HeadDiameter;                 // 1.50
            Primitive(proxy, PrimitiveType.Capsule, "body",
                new Vector3(0f, bodyHeight * 0.5f, 0f),
                new Vector3(0.45f, bodyHeight * 0.5f, 0.30f),               // カプセル素体は高さ2
                bodyColor);
            Primitive(proxy, PrimitiveType.Sphere, "head",
                new Vector3(0f, PlayerHeight - HeadDiameter * 0.5f, 0f),
                Vector3.one * HeadDiameter, Rgb(0.85f, 0.65f, 0.50f));

            SetLayer(proxy, LayerMask.NameToLayer(LayerName));
        }

        private static GameObject Primitive(GameObject parent, PrimitiveType type, string name,
            Vector3 localPos, Vector3 localScale, Color color)
        {
            var go = GameObject.CreatePrimitive(type);
            go.name = name;
            go.transform.SetParent(parent.transform, false);
            go.transform.localPosition = localPos;
            go.transform.localScale = localScale;
            Object.DestroyImmediate(go.GetComponent<Collider>());

            // シーンライト非依存の自前Lambert（陰影が無いとポスタライズが効かない。§4スパイク）
            var mat = new Material(Shader.Find("KokoSim/DuelStageProxyLambert")) { hideFlags = HideFlags.DontSave };
            mat.SetColor("_BaseColor", color);
            go.GetComponent<MeshRenderer>().sharedMaterial = mat;
            SetLayer(go, LayerMask.NameToLayer(LayerName));
            return go;
        }

        // ---- レンダリング ----------------------------------------------------

        private static string RenderVariants()
        {
            int layer = LayerMask.NameToLayer(LayerName);
            var outDir = Path.Combine(Directory.GetParent(Application.dataPath).FullName, "out/duel-stage");
            Directory.CreateDirectory(outDir);

            var camGo = new GameObject("~DuelStageCamera") { hideFlags = HideFlags.DontSave };
            var cam = camGo.AddComponent<Camera>();
            cam.cullingMask = 1 << layer;
            cam.clearFlags = CameraClearFlags.SolidColor;
            cam.backgroundColor = Rgb(0.55f, 0.78f, 0.90f); // 夏空
            cam.nearClipPlane = 0.1f;
            cam.farClipPlane = 200f;

            var rt = new RenderTexture(480, 270, 24, RenderTextureFormat.ARGB32)
            { name = "DuelStageRT", filterMode = FilterMode.Point };
            rt.Create();
            var big = new RenderTexture(1920, 1080, 0, RenderTextureFormat.ARGB32) { name = "DuelStageBigRT" };
            big.Create();

            var umpire = GameObject.Find(RootName + "/umpire");
            var batter = GameObject.Find(RootName + "/batter");
            var pitcher = GameObject.Find(RootName + "/pitcher");

            var sb = new StringBuilder();
            try
            {
                foreach (var v in Variants)
                {
                    if (umpire != null) umpire.SetActive(!v.HideUmpire);

                    cam.transform.position = new Vector3(v.OffsetX, v.Height, MoundDistance + v.Dist);
                    cam.transform.LookAt(new Vector3(0f, v.AimY, v.AimZ));
                    cam.fieldOfView = v.Fov;
                    cam.targetTexture = rt; // WorldToScreenPoint を RT 基準(480×270)にするため

                    int batPx = MeasureScreenHeight(cam, batter);
                    int pitPx = MeasureScreenHeight(cam, pitcher);

                    Render(cam, rt);

                    // ×4 整数 Point 拡大 → PNG
                    var prev = RenderTexture.active;
                    Graphics.Blit(rt, big);
                    RenderTexture.active = big;
                    var tex = new Texture2D(1920, 1080, TextureFormat.RGBA32, false);
                    tex.ReadPixels(new Rect(0, 0, 1920, 1080), 0, 0);
                    tex.Apply();
                    RenderTexture.active = prev;

                    var file = string.Format(CultureInfo.InvariantCulture,
                        "{0}_d{1}_h{2}_fov{3}_bat{4}px_pit{5}px.png", v.Name, v.Dist, v.Height, v.Fov, batPx, pitPx);
                    File.WriteAllBytes(Path.Combine(outDir, file), tex.EncodeToPNG());
                    Object.DestroyImmediate(tex);

                    sb.AppendLine(file + "  batter=" + batPx + "px(" + Mathf.RoundToInt(batPx / 270f * 100f)
                        + "%) pitcher=" + pitPx + "px(" + Mathf.RoundToInt(pitPx / 270f * 100f) + "%)");
                }
            }
            finally
            {
                cam.targetTexture = null;
                rt.Release(); Object.DestroyImmediate(rt);
                big.Release(); Object.DestroyImmediate(big);
                Object.DestroyImmediate(camGo);
            }

            Debug.Log("[DuelStageComposer] " + Variants.Length + " variants -> " + outDir + "\n" + sb);
            return sb.ToString();
        }

        // ---- ピクセル化ポスト（§4.1 ベースライン自前二段） ------------------

        // §4.2 の比較枠3解像度。いずれも 1920×1080 の整数倍（×6/×4/×3）
        private static readonly Vector2Int[] PixelResolutions =
        {
            new Vector2Int(320, 180),
            new Vector2Int(480, 270),
            new Vector2Int(640, 360),
        };

        private static string RenderPixelMatrix()
        {
            int layer = LayerMask.NameToLayer(LayerName);
            var outDir = Path.Combine(Directory.GetParent(Application.dataPath).FullName, "out/duel-stage/pixel");
            Directory.CreateDirectory(outDir);

            var camGo = new GameObject("~DuelStageCamera") { hideFlags = HideFlags.DontSave };
            var cam = camGo.AddComponent<Camera>();
            cam.cullingMask = 1 << layer;
            cam.clearFlags = CameraClearFlags.SolidColor;
            cam.nearClipPlane = 0.1f;
            cam.farClipPlane = 200f;

            // 確定構図 R-comp60（design-18 §7-1。2026-07-26 構図リフレームDで望遠圧縮に改定）
            DuelMotionSpike.ApplyAdoptedCamera(cam);

            var skyColor = Rgb(0.55f, 0.78f, 0.90f);
            // データパスのクリア値: R=1（最遠深度）・GBA=0.5（法線ゼロ）。空との深度差でシルエット輪郭が立つ
            var dataClear = new Color(1f, 0.5f, 0.5f, 0.5f);

            var root = GameObject.Find(RootName);
            var renderers = root.GetComponentsInChildren<MeshRenderer>(true);
            var depthMat = new Material(Shader.Find("KokoSim/DuelStageDepthNormal")) { hideFlags = HideFlags.DontSave };
            var postMat = new Material(Shader.Find("KokoSim/DuelStagePixelPost")) { hideFlags = HideFlags.DontSave };

            var big = new RenderTexture(1920, 1080, 0, RenderTextureFormat.ARGB32) { name = "DuelStageBigRT" };
            big.Create();

            var sb = new StringBuilder();
            try
            {
                foreach (var res in PixelResolutions)
                {
                    var colorRt = MakeRt(res, RenderTextureFormat.ARGB32, RenderTextureReadWrite.Default, 24);
                    var dataRt = MakeRt(res, RenderTextureFormat.ARGBHalf, RenderTextureReadWrite.Linear, 24);
                    var tmpA = MakeRt(res, RenderTextureFormat.ARGB32, RenderTextureReadWrite.Default, 0);
                    var tmpB = MakeRt(res, RenderTextureFormat.ARGB32, RenderTextureReadWrite.Default, 0);
                    try
                    {
                        // 1) 通常カラー
                        cam.backgroundColor = skyColor;
                        cam.targetTexture = colorRt;
                        Render(cam, colorRt);

                        // 2) 深度+法線（全レンダラを差し替えマテリアルで再描画→復元）
                        var saved = new Material[renderers.Length];
                        for (int i = 0; i < renderers.Length; i++) { saved[i] = renderers[i].sharedMaterial; renderers[i].sharedMaterial = depthMat; }
                        cam.backgroundColor = dataClear;
                        cam.targetTexture = dataRt;
                        Render(cam, dataRt);
                        for (int i = 0; i < renderers.Length; i++) renderers[i].sharedMaterial = saved[i];

                        postMat.SetTexture("_DataTex", dataRt);

                        // 3) 4条件（none / pal / edge / pal+edge）を合成して整数拡大→PNG
                        for (int pal = 0; pal <= 1; pal++)
                        for (int edge = 0; edge <= 1; edge++)
                        {
                            RenderTexture final = colorRt;
                            if (pal == 1) { Graphics.Blit(colorRt, tmpA, postMat, 0); final = tmpA; }
                            if (edge == 1) { var dst = (final == tmpA) ? tmpB : tmpA; Graphics.Blit(final, dst, postMat, 1); final = dst; }

                            var prev = RenderTexture.active;
                            Graphics.Blit(final, big);
                            RenderTexture.active = big;
                            var tex = new Texture2D(1920, 1080, TextureFormat.RGBA32, false);
                            tex.ReadPixels(new Rect(0, 0, 1920, 1080), 0, 0);
                            tex.Apply();
                            RenderTexture.active = prev;

                            var file = string.Format(CultureInfo.InvariantCulture,
                                "px_{0}x{1}_pal{2}_edge{3}.png", res.x, res.y, pal, edge);
                            File.WriteAllBytes(Path.Combine(outDir, file), tex.EncodeToPNG());
                            Object.DestroyImmediate(tex);
                            sb.AppendLine(file);
                        }
                    }
                    finally
                    {
                        cam.targetTexture = null;
                        ReleaseRt(colorRt); ReleaseRt(dataRt); ReleaseRt(tmpA); ReleaseRt(tmpB);
                    }
                }
            }
            finally
            {
                big.Release(); Object.DestroyImmediate(big);
                Object.DestroyImmediate(depthMat);
                Object.DestroyImmediate(postMat);
                Object.DestroyImmediate(camGo);
            }

            Debug.Log("[DuelStageComposer] pixel matrix 12 -> " + outDir + "\n" + sb);
            return sb.ToString();
        }

        private static RenderTexture MakeRt(Vector2Int res, RenderTextureFormat fmt, RenderTextureReadWrite rw, int depth)
        {
            var rt = new RenderTexture(res.x, res.y, depth, fmt, rw) { filterMode = FilterMode.Point };
            rt.Create();
            return rt;
        }

        private static void ReleaseRt(RenderTexture rt)
        {
            if (rt == null) return;
            rt.Release();
            Object.DestroyImmediate(rt);
        }

        /// <summary>URP では Camera.Render() が使えないため RenderRequest で1枚だけ描く。</summary>
        internal static void Render(Camera cam, RenderTexture rt)
        {
            var request = new UniversalRenderPipeline.SingleCameraRequest { destination = rt };
            if (RenderPipeline.SupportsRenderRequest(cam, request))
            {
                RenderPipeline.SubmitRenderRequest(cam, request);
            }
            else
            {
                cam.Render(); // フォールバック（ビルトインRP時のみ到達）
            }
        }

        /// <summary>プロキシのレンダラ包含AABBの8隅をスクリーン投影し、RT上の縦pxを実測する。</summary>
        internal static int MeasureScreenHeight(Camera cam, GameObject proxy)
        {
            if (proxy == null) return -1;
            var renderers = proxy.GetComponentsInChildren<Renderer>();
            if (renderers.Length == 0) return -1;
            var b = renderers[0].bounds;
            foreach (var r in renderers) b.Encapsulate(r.bounds);

            float minY = float.MaxValue, maxY = float.MinValue;
            for (int i = 0; i < 8; i++)
            {
                var corner = new Vector3(
                    (i & 1) == 0 ? b.min.x : b.max.x,
                    (i & 2) == 0 ? b.min.y : b.max.y,
                    (i & 4) == 0 ? b.min.z : b.max.z);
                var sp = cam.WorldToScreenPoint(corner);
                if (sp.z <= 0f) continue; // カメラ背後
                minY = Mathf.Min(minY, sp.y);
                maxY = Mathf.Max(maxY, sp.y);
            }
            if (minY > maxY) return 0;
            return Mathf.RoundToInt(Mathf.Clamp(maxY, 0f, 270f) - Mathf.Clamp(minY, 0f, 270f));
        }

        // ---- 後始末 ----------------------------------------------------------

        internal static void Cleanup()
        {
            for (var go = GameObject.Find(RootName); go != null; go = GameObject.Find(RootName))
            {
                DestroyMaterials(go);
                Object.DestroyImmediate(go);
            }
            var cam = GameObject.Find("~DuelStageCamera");
            if (cam != null) Object.DestroyImmediate(cam);
        }

        /// <summary>
        /// Primitive/SpawnBat等が都度 new Material するぶんは GameObject 破棄だけでは残る
        /// （マテリアルは子オブジェクトではない）。2026-08-22 実測で22,111個のリークを確認・
        /// ここ1箇所で塞ぐ（呼び出し側は全員 RootName の下にぶら下げる規約なので網羅できる）。
        /// </summary>
        private static void DestroyMaterials(GameObject root)
        {
            foreach (var r in root.GetComponentsInChildren<Renderer>(true))
                foreach (var m in r.sharedMaterials)
                    if (m != null) Object.DestroyImmediate(m);
        }

        private static void SetLayer(GameObject go, int layer)
        {
            go.layer = layer;
            foreach (Transform child in go.transform) SetLayer(child.gameObject, layer);
        }

        private static Color Rgb(float r, float g, float b) { return new Color(r, g, b, 1f); }
    }
}
