using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEngine;
using Region = KokoSim.Unity.Match.DuelUniformMeshBuilder.Region;

namespace KokoSim.EditorTools
{
    /// <summary>
    /// 購入素体（School Baseball Player 0001）を既存の領域塗り分け（design-18 §3.5）へ載せるベイカ。
    ///
    /// 購入モデルはパーツごとに別メッシュなので、帽子・パンツ・ソックス等は「メッシュ＝1領域」で足りる。
    /// 唯一 Suit（上衣）だけが「白ユニ本体＋紺アンダーシャツ袖・襟」を1メッシュ1テクスチャに同居させて
    /// いるため、ここだけ三角形単位で元テクスチャの色を見て Shirt / Undershirt に振り分ける。
    /// 振り分け結果は UV1.x =(id+0.5)/8 に載せる＝DuelStageProxyLambert の _UseRegions/_RegionColors が
    /// 無改修でそのまま効く（マスクテクスチャを別途持たない＝正典どおり「1アクター1マテリアル」を維持）。
    ///
    /// 呼び出しは execute_code から KokoSim.EditorTools.DuelVendorRegionBaker.BakeAll()
    /// （DuelUniformBaker と同じ流儀・Edit mode 完結・シーン非汚染）。
    ///
    /// 出力は購入テクスチャからの派生物なので、購入アセットと同じ gitignore 配下へ置く（design-18 §3.1）。
    /// </summary>
    public static class DuelVendorRegionBaker
    {
        private const string Fbx =
            "Assets/DuelSpike/Vendor/SchoolBaseballPlayer0001/School_Baseball_Player_0001.fbx";
        private const string SuitTex =
            "Assets/DuelSpike/Vendor/SchoolBaseballPlayer0001/Texture/I_Players_0001_Suit_Co_0001.jpg";
        private const string PantsTex =
            "Assets/DuelSpike/Vendor/SchoolBaseballPlayer0001/Texture/I_Players_0001_Pants_Co_0001.jpg";
        private const string FaceTex =
            "Assets/DuelSpike/Vendor/SchoolBaseballPlayer0001/Texture/I_YM7M_Fb_AR_Face_Co_0001.jpg";
        private const string OutDir = "Assets/DuelSpike/Vendor/Generated";

        /// <summary>
        /// 紺（アンダーシャツ・襟）判定のしきい値。Suit テクスチャは白地(輝度0.8前後・ほぼ無彩色)と
        /// 濃紺(輝度0.1前後・青優位)の二極で、間にほとんど画素がない＝しきい値は鈍感でよい。
        /// </summary>
        private const float NavyLuma = 0.38f;
        private const float NavyBlueOverRed = 1.12f;

        /// <summary>
        /// ベルト（Pants メッシュ内）判定のしきい値。Pants テクスチャは白地の中に
        /// **ベルト革のUV島だけが濃茶**（輝度0.15前後）で描かれているので、暗いだけで足りる。
        /// バックルの金属帯（輝度0.5前後）・ベルト通し（白）は拾わない。
        /// </summary>
        private const float BeltLuma = 0.35f;

        /// <summary>メッシュ名 → 領域（Suit / Pants だけは三角形単位で振り分けるのでここには載せない）。</summary>
        private static readonly Dictionary<string, Region> WholeMeshRegion = new Dictionary<string, Region>
        {
            { "A_Players_0001_Sox_001", Region.Socks },
            { "A_Players_0001_Shoes_0001", Region.Shoes },
            // バックルは帯と同じ領域＝ベルト1本として読ませる（高校球児のベルトは黒が多数派）
            { "A_Players_0001_Beltbuckle_0001", Region.Belt },
            // 頭メッシュ（A_JM7M_AC_0001_Head_0001）は頭部と両手が同居している＝ClassifyHead で振り分ける
            { "LeftEye_Ball_Geo", Region.Skin },
            { "RightEye_Ball_Geo", Region.Skin },
            // 帽子は独立領域（2026-07-29。チームカラーをプレイヤーが領域ごとに決めるため、
            // アンダーと同色に固定していた従来運用をやめた）。
            // クラウンとつばは1メッシュだが、issue #621 で幾何で二分して塗り分けるようにした
            // （ClassifyCap。ここには載せず三角形単位で振り分ける）。
        };

        private const string CapMeshName = "A_Players_0001_Cap_0001";
        internal const string HeadMeshName = "A_JM7M_AC_0001_Head_0001";

        private const string SuitMeshName = "A_Players_0001_Suit_0001";
        private const string PantsMeshName = "A_Players_0001_Pants_0001";

        public static string BakeAll() => BakeAll(SleeveFlare);

        /// <summary>
        /// メニューから1クリックで焼き直す（顔ののっぺらぼう化・髪・袖など、このベイカの変更を取り込む時）。
        /// 焼き直しは資産を作り直す＝GUID が変わるので、<see cref="BakeAll(float)"/> がビルド同梱の索引（F8）も続けて焼き直す。
        /// </summary>
        [MenuItem("KokoSim/Duel/選手の顔と領域を作り直す")]
        public static void BakeAllFromMenu()
        {
            var report = BakeAll();
            if (report.StartsWith("FAILED"))
            {
                Debug.LogError("[DuelVendorRegionBaker] " + report);
                EditorUtility.DisplayDialog("選手の顔と領域を作り直す", report, "OK");
                return;
            }
            Debug.Log("[DuelVendorRegionBaker]\n" + report);
        }

        /// <summary>
        /// 袖の張り出し倍率を指定してベイクする（issue #368-② の切り分け用）。
        /// 「袖が目立つ」のが袖メッシュのせいか腕の向きのせいかは、<c>1.0</c>（＝張り出しなし）で
        /// 焼いた絵と並べれば1枚で判る。**確認が済んだら必ず既定へ焼き直すこと**。
        /// </summary>
        public static string BakeAll(float sleeveFlare)
        {
            _flare = sleeveFlare;
            var sb = new System.Text.StringBuilder();
            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(Fbx);
            if (prefab == null) return "FAILED: FBX が見つからない " + Fbx;

            var suitTex = LoadReadable(SuitTex);
            if (suitTex == null) return "FAILED: Suit テクスチャが読めない " + SuitTex;
            var pantsTex = LoadReadable(PantsTex);
            if (pantsTex == null) { Object.DestroyImmediate(suitTex); return "FAILED: Pants テクスチャが読めない " + PantsTex; }
            var faceTex = LoadReadable(FaceTex);
            if (faceTex == null)
            {
                Object.DestroyImmediate(suitTex); Object.DestroyImmediate(pantsTex);
                return "FAILED: 顔テクスチャが読めない " + FaceTex;
            }

            Directory.CreateDirectory(OutDir);

            // 袖の張り出し（FlareSleeves）は上腕ボーンの軸が要る。Animator.GetBoneTransform は
            // 実体化したヒエラルキーでないと引けないので、ここだけ一時インスタンスを立てる。
            // メッシュは共有アセットなので、読む先がプレハブでも実体でも同じものになる
            GameObject inst = null;
            // 全メッシュを作り切ってから書き出す＝途中の例外で既存の資産が一部だけ消えた状態を残さない
            var baked = new List<(string Path, Mesh Mesh)>();
            try
            {
                // 開いているシーンに載せない・保存させない（保存すると購入 FBX への参照がシーンに入る）
                inst = (GameObject)PrefabUtility.InstantiatePrefab(prefab);
                inst.hideFlags = HideFlags.HideAndDontSave;
                var anim = inst.GetComponent<Animator>();

                foreach (var r in inst.GetComponentsInChildren<Renderer>(true))
                {
                    var mesh = MeshOf(r);
                    if (mesh == null) continue;

                    var copy = Object.Instantiate(mesh);
                    copy.name = mesh.name;

                    int[] regionPerVertex;
                    float[] hairWeight = null;
                    string note;
                    if (r.name == HeadMeshName)
                    {
                        int headSm = HeadSubmesh(r);
                        regionPerVertex = ClassifyHead(copy, headSm, faceTex, out hairWeight, out note);
                        // 髪の濃さは元の形で引いた後に顔を均す（均した後だと前後比・高さ比がずれる）
                        note += " / " + FlattenFace(copy, headSm);
                    }
                    else if (r.name == SuitMeshName)
                    {
                        regionPerVertex = ClassifySuit(copy, suitTex, out note);
                        note += " / " + FlareSleeves(copy, regionPerVertex, r as SkinnedMeshRenderer, anim);
                    }
                    else if (r.name == PantsMeshName)
                    {
                        regionPerVertex = ClassifyPants(copy, pantsTex, out note);
                    }
                    else if (r.name == CapMeshName)
                    {
                        regionPerVertex = ClassifyCap(copy, out note);
                    }
                    else
                    {
                        Region reg;
                        if (!WholeMeshRegion.TryGetValue(r.name, out reg))
                        {
                            sb.AppendLine(r.name + ": SKIP（領域の割当が未定義）");
                            Object.DestroyImmediate(copy);
                            continue;
                        }
                        regionPerVertex = new int[copy.vertexCount];
                        for (int i = 0; i < regionPerVertex.Length; i++) regionPerVertex[i] = (int)reg;
                        note = "全体=" + reg;
                    }

                    // UV1.x = 領域ID、UV1.y = **バインドポーズの左右座標**（縦縞の刻み座標）。
                    // 縞をスキニング後の頂点位置で刻むと、生地に印刷された縞ではなく
                    // 「空間に固定された縞の中を体が通り抜ける」絵になる（2026-07-29 実害）。
                    // 静止座標をここで焼いておけば、体が曲がっても縞は生地に付いたまま伸び縮みする。
                    //
                    // 袖は縞を**腕に沿って平行**に通す（2026-11-24 監督指摘。袖は型紙の縦を肩→袖口に取って裁つので、
                    // 腕を下ろすと胴と同じ縦縞になる。旧「腕に直交する輪」は誤りだった）。左右 x の一枚板のままだと、
                    // 斜め下へ伸びたAポーズの腕を平面が斜めに切って楕円の弧になる。継ぎ目（肩）は支配ボーンで切り替える。
                    var rest = copy.vertices;
                    var uv1 = new Vector2[copy.vertexCount];
                    for (int i = 0; i < uv1.Length; i++)
                        uv1[i] = new Vector2(
                            KokoSim.Unity.Match.DuelUniformMeshBuilder.RegionUv(regionPerVertex[i]), rest[i].x);
                    if (r.name == SuitMeshName)
                        note += " / " + SleeveStripeAlongArm(copy, ref regionPerVertex, r as SkinnedMeshRenderer, anim, ref uv1);
                    // ズボンの脚は刻み座標を**その高さの脚の幅で正規化**する（2026-11-24 監督指摘「下半身の縞が線対称の輪になる」）。
                    if (r.name == PantsMeshName)
                        note += " / " + PantsStripeAlongLegs(copy, regionPerVertex, r as SkinnedMeshRenderer, anim, uv1);
                    // 髪の領域では UV1.y を髪の濃さに使う（縞は地色の領域にしか乗らないので刻み座標は要らない）
                    if (hairWeight != null)
                        for (int i = 0; i < uv1.Length; i++)
                            if (regionPerVertex[i] == (int)Region.Hair) uv1[i].y = hairWeight[i];
                    copy.uv2 = uv1;   // Mesh.uv2 == UV1（シェーダの TEXCOORD1）

                    var outPath = OutDir + "/" + r.name + "_Regions.asset";
                    // 実行時に三角形を読めるようにしておく。グラブをはめた手を抜く処理
                    // （DuelStageRig.HideGloveHand・issue #913）が読む。元 FBX の「読めない」設定を
                    // そのまま引き継ぐと、エラーも出ずに手が残るだけになる。
                    // **資産にする前に**立てる: 作った後に SerializedObject で立てても dirty が付かず、
                    // 最後の SaveAssets で書かれない資産が出ていた（2026-11-15 実測: 9枚中7枚が 0 のまま）
                    var so = new SerializedObject(copy);
                    so.FindProperty("m_IsReadable").boolValue = true;
                    so.ApplyModifiedPropertiesWithoutUndo();
                    baked.Add((outPath, copy));
                    sb.AppendLine(r.name + ": " + note);
                }

                foreach (var b in baked)
                {
                    AssetDatabase.DeleteAsset(b.Path);
                    AssetDatabase.CreateAsset(b.Mesh, b.Path);
                    EditorUtility.SetDirty(b.Mesh);
                }
                baked.Clear();
            }
            finally
            {
                foreach (var b in baked) Object.DestroyImmediate(b.Mesh);   // 書き出せなかった複製
                if (inst != null) Object.DestroyImmediate(inst);
                Object.DestroyImmediate(suitTex);
                Object.DestroyImmediate(pantsTex);
                Object.DestroyImmediate(faceTex);
            }
            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();
            // 資産を作り直すと GUID が変わる。ビルド同梱の索引（F8）が既にあれば焼き直す
            // （古い GUID のままだと Player ビルドでだけ領域メッシュが外れる）。
            if (AssetDatabase.LoadAssetAtPath<ScriptableObject>(
                    "Assets/KokoSim/Resources/" + KokoSim.Unity.Match.DuelAssetCatalog.ResourceName + ".asset") != null)
                KokoSim.UnityEditorTools.DuelAssetCatalogBaker.Bake();
            return sb.ToString();
        }

        /// <summary>
        /// 帽子を三角形単位で クラウン / つば に振り分ける（issue #621）。
        ///
        /// テクスチャでは分けられない（帽子は無地1色）ので<b>幾何で二分する</b>。実測した
        /// メッシュ空間（Z-up・顔は -y 側）での形は:
        ///   y  -0.169(つば先) 〜 0.114(後頭部) ／ z  1.666(下端) 〜 1.789(天辺)
        ///   前方の1/4（y &lt; min+0.25）かつ下40%（z &lt; min+0.40）にだけ、薄い板状の帯がある＝つば
        /// しきい値は<b>境界箱に対する比</b>で持つ（絶対値で持つと取り込みスケールが変わった瞬間に
        /// 帽子が丸ごと片方の色になる）。
        /// </summary>
        private static int[] ClassifyCap(Mesh mesh, out string note)
        {
            var v = mesh.vertices;
            var tris = mesh.triangles;
            var b = mesh.bounds;

            float yEdge = b.min.y + b.size.y * VisorFrontShare;
            float zEdge = b.min.z + b.size.z * VisorHeightShare;

            var voteCrown = new int[v.Length];
            var voteVisor = new int[v.Length];
            int triCrown = 0, triVisor = 0;
            for (int t = 0; t < tris.Length; t += 3)
            {
                int a = tris[t], b2 = tris[t + 1], c = tris[t + 2];
                var mid = (v[a] + v[b2] + v[c]) / 3f;   // 三角形の重心で判定（頂点だと縁で割れる）
                if (mid.y < yEdge && mid.z < zEdge)
                {
                    triVisor++; voteVisor[a]++; voteVisor[b2]++; voteVisor[c]++;
                }
                else
                {
                    triCrown++; voteCrown[a]++; voteCrown[b2]++; voteCrown[c]++;
                }
            }

            int conflicts = 0;
            var outRegion = new int[v.Length];
            for (int i = 0; i < v.Length; i++)
            {
                if (voteCrown[i] > 0 && voteVisor[i] > 0) conflicts++;
                outRegion[i] = voteVisor[i] > voteCrown[i] ? (int)Region.CapVisor : (int)Region.Cap;
            }

            note = "三角形 クラウン=" + triCrown + " つば=" + triVisor
                 + " / 頂点の領域またぎ=" + conflicts + "（多い＝境界がギザつく）";
            return outRegion;
        }

        // ---- 坊主頭の髪（issue #916） ------------------------------------------------------------
        //
        // 購入素体の顔テクスチャ（Face_Co）には**坊主頭（短髪＋額・もみあげ・襟足の生え際）が最初から描かれている**。
        // 領域シェーダはテクスチャを読まず領域ごとの単色で塗るので、頭を丸ごと肌にするとスキンヘッドに見えていた。
        // 髪は別ジオメトリではなく頭蓋の面そのもので、頭・顔・耳・首が1つのUV島に入っている（＝島では分けられない）。
        // 色相は髪も肌も約27°で、**輝度だけで分かれる**（sRGB: 髪0.26〜0.31／生え際のぼかし0.37〜0.45／肌0.50〜0.60）。
        // 幾何で切ると生え際の高さが方位ごとに違う（境界箱の高さ比で 額0.65／耳上0.64／襟足0.34）ので襟足が欠ける。
        //
        // 焼き方: 頭部の頂点は**全部 Hair 領域**にし、UV1.y に髪の濃さ（0=肌〜1=髪）を焼く。シェーダが肌色と髪色を
        // 濃さで混ぜる＝生え際がぼける（監督決定）。髪と肌で領域IDを分けると、頭は溶接メッシュなので境界の頂点が
        // 両側の三角形に共有され、UV1.x が 4.5/8→9.5/8 へ補間されて**途中の領域（靴・帽子・ベルト・つば）の色の帯**が出る。

        /// <summary>これより上（uv.y）は歯・舌の島。髪にしない。</summary>
        internal const float MouthIslandMinV = 0.815f;

        /// <summary>髪の濃さ1とみなす輝度（sRGB）。描かれた短髪は0.26〜0.31。</summary>
        internal const float HairLumaFull = 0.32f;

        /// <summary>髪の濃さ0とみなす輝度（sRGB）。肌は0.50〜0.60、生え際のぼかしは0.37〜0.45。</summary>
        internal const float HairLumaNone = 0.48f;

        /// <summary>赤/緑がこれを超える画素は唇・眼窩の影（髪ではない）。顔の前半分（<see cref="LipFrontShare"/>）でだけ見る。</summary>
        internal const float LipRedOverGreen = 1.3f;

        /// <summary>
        /// 唇・眼窩の除外を効かせる前方の範囲（境界箱の前後比）。髪自体も赤茶で、後頭部の正中には赤/緑1.31〜1.35の
        /// 髪がある＝頭全体で除外すると襟足の真ん中に肌の筋が1本通る（2026-11-14 実測）。唇・眼窩は顔の前にしか無い。
        /// </summary>
        internal const float LipFrontShare = 0.5f;

        /// <summary>眉を除く: 前方（境界箱の前後幅に対する比）。</summary>
        internal const float BrowFrontShare = 0.25f;

        /// <summary>眉を除く: 高さ（境界箱の高さに対する比）がこれ未満の前方は髪にしない（額の生え際は0.65より上）。</summary>
        internal const float BrowHeightShare = 0.68f;

        /// <summary>
        /// 1頂点の髪の濃さ（0〜1）。<paramref name="frontRatio"/> は頭部の境界箱で前端0〜後端1、
        /// <paramref name="heightRatio"/> は下端0〜上端1。色は頂点のUVで顔テクスチャを引いた sRGB。
        /// </summary>
        internal static float HairWeight(Color c, float uvY, float frontRatio, float heightRatio)
        {
            if (uvY >= MouthIslandMinV) return 0f;
            if (frontRatio < LipFrontShare && c.r > c.g * LipRedOverGreen) return 0f;
            if (frontRatio < BrowFrontShare && heightRatio < BrowHeightShare) return 0f;
            float luma = 0.2126f * c.r + 0.7152f * c.g + 0.0722f * c.b;
            float t = Mathf.Clamp01((luma - HairLumaFull) / (HairLumaNone - HairLumaFull));
            return 1f - t * t * (3f - 2f * t);
        }

        /// <summary>頂点の色を引くときの箱の半径（UV）。頭皮の面は粗い（平均辺長2.8cm）ので1画素だと毛の筋を拾う。</summary>
        private const float HairSampleRadiusUv = 0.004f;

        /// <summary>
        /// 頭メッシュを振り分ける。<paramref name="headSubmesh"/> の頂点は <see cref="Region.Hair"/>（髪の濃さを
        /// <paramref name="hairWeight"/> へ）、それ以外（同居している両手）は <see cref="Region.Skin"/>。
        /// 頭部と手の両方の三角形に使われる頂点は手の側（Skin）に倒す＝手に髪の色が乗らない（件数を報告する）。
        /// </summary>
        internal static int[] ClassifyHead(Mesh mesh, int headSubmesh, Texture2D faceTex,
            out float[] hairWeight, out string note)
        {
            int vcount = mesh.vertexCount;
            var region = new int[vcount];
            hairWeight = new float[vcount];
            for (int i = 0; i < vcount; i++) region[i] = (int)Region.Skin;
            if (headSubmesh < 0 || headSubmesh >= mesh.subMeshCount)
            {
                note = "頭部のサブメッシュが見つからない＝全体=Skin（スキンヘッドのまま）";
                return region;
            }

            var inHead = new bool[vcount];
            var inOther = new bool[vcount];
            for (int sm = 0; sm < mesh.subMeshCount; sm++)
                foreach (var i in mesh.GetTriangles(sm))
                    if (sm == headSubmesh) inHead[i] = true; else inOther[i] = true;

            var v = mesh.vertices;
            var uv = mesh.uv;
            var min = Vector3.positiveInfinity;
            var max = Vector3.negativeInfinity;
            for (int i = 0; i < vcount; i++)
                if (inHead[i]) { min = Vector3.Min(min, v[i]); max = Vector3.Max(max, v[i]); }
            var size = Vector3.Max(max - min, Vector3.one * 1e-6f);

            int head = 0, shared = 0, full = 0, partial = 0;
            for (int i = 0; i < vcount; i++)
            {
                if (!inHead[i]) continue;
                if (inOther[i]) { shared++; continue; }
                head++;
                region[i] = (int)Region.Hair;
                // メッシュ空間は Z-up・顔が -y 側（帽子と同じ。ClassifyCap の実測）
                float front = (v[i].y - min.y) / size.y;
                float height = (v[i].z - min.z) / size.z;
                var c = SampleBox(faceTex, uv[i], HairSampleRadiusUv);
                float w = HairWeight(c, uv[i].y, front, height);
                hairWeight[i] = w;
                if (w >= 0.99f) full++;
                else if (w > 0.01f) partial++;
            }
            note = "頭部の頂点=" + head + "（髪=" + full + " 生え際=" + partial + " 肌=" + (head - full - partial)
                 + "）/ 手と共有で肌に倒した頂点=" + shared + "（0なら頭部と手は完全に分離）"
                 + " / 頭部の境界箱 " + min.ToString("F3") + "〜" + max.ToString("F3");
            return region;
        }

        /// <summary>顔テクスチャのマテリアルを持つサブメッシュ（無ければ0）。</summary>
        private static int HeadSubmesh(Renderer r)
        {
            var mats = r.sharedMaterials;
            for (int i = 0; i < mats.Length; i++)
            {
                var m = mats[i];
                if (m == null) continue;
                var tex = m.mainTexture;
                if ((tex != null && tex.name.Contains("Face")) || m.name.Contains("Face")) return i;
            }
            return 0;
        }

        /// <summary>uv を中心に 5×5 点の平均色（sRGB のまま）。</summary>
        private static Color SampleBox(Texture2D tex, Vector2 uv, float radius)
        {
            var sum = Color.clear;
            for (int y = -2; y <= 2; y++)
                for (int x = -2; x <= 2; x++)
                    sum += tex.GetPixelBilinear(uv.x + x * radius * 0.5f, uv.y + y * radius * 0.5f);
            return sum / 25f;
        }

        // ---- のっぺらぼう（顔の起伏を均す） -----------------------------------------------------
        //
        // 眼球を消しても顔に目鼻口の「跡」が残っていた（2026-11-15 監督指摘「不気味」）。原因は**形そのもの**:
        // 領域シェーダはテクスチャも法線マップも読まず、頂点法線の Lambert だけで陰影を付ける。顔テクスチャの
        // 塗り潰し（領域色）は効いていても、まぶた・鼻・唇・眼窩は**頭メッシュの起伏**として作り込まれていて
        // （鼻先は頬より約2.5cm前、まぶたは眼窩の縁に沿って盛り上がる）、法線が光を拾って目鼻口が浮かぶ。
        // 実測で髪の濃さ（UV1.y）は顔の前面で全て0＝色の漏れではない。
        //
        // 均し方: 顔の前面を**なめらかな面（左右対称の多項式 y=P(x,z)）で近似**し、窓の中の頂点をその面へ寄せ、
        // 法線も面の法線へ置き換える。面は頬・額・あごの頂点に当てはめ、鼻（面より前）と眼窩（面より奥）は
        // 外れ値として刈り込みながら当て直す＝目鼻口だけが消えて輪郭・あごの丸みは残る。
        // 窓の縁は smoothstep で元の形へ戻す（境目に段を作らない）。
        // 法線は RecalculateNormals を使わない（非溶接メッシュで UV の境目がハードエッジになる。design-18 §3.5.1b）。
        // 口の中（歯・舌の島と口腔）も同じ面へ潰れる＝前面から覗かない。表情のブレンドシェイプは落とす
        // （重みを入れた瞬間に目鼻口が戻るため。対決ビューは表情を使っていない）。

        /// <summary>均す窓の横幅（鼻筋からの |x|[m]）: ここまでは完全に均す。目尻は約0.045（0.045 だと目尻の外の窪みが斜めの光で薄く残った）。</summary>
        internal const float FaceFlatHalfWidth = 0.052f;
        /// <summary>同・ここで元の形へ戻り切る（頬が横を向き始める手前）。</summary>
        internal const float FaceFadeHalfWidth = 0.068f;
        /// <summary>均す窓の上端（鼻先からの高さ[m]）: 眉の上まで。生え際（約+0.10）は髪の濃さが乗るので残す。</summary>
        internal const float FaceFlatTop = 0.080f;
        internal const float FaceFadeTop = 0.100f;
        /// <summary>均す窓の下端（鼻先からの高さ[m]、負）: 下唇の下まで。あご先（約-0.06）の丸みは残す。</summary>
        internal const float FaceFlatBottom = -0.045f;
        internal const float FaceFadeBottom = -0.062f;
        /// <summary>前面とみなす前後比（境界箱の前端0〜後端1）。後頭部の同じ (x,z) の頂点を動かさない。</summary>
        internal const float FaceFrontShare = 0.42f;
        /// <summary>当てはめの外れ値（目鼻口）とみなす残差[m]。</summary>
        private const float FaceFitTrim = 0.0025f;

        /// <summary>多項式の基底（左右対称＝x は偶数次だけ）。座標は鼻先中心・おおむね ±1 に正規化して渡す。</summary>
        internal static void FaceBasis(float x, float z, float[] b)
        {
            float x2 = x * x;
            b[0] = 1f; b[1] = z; b[2] = z * z; b[3] = z * z * z;
            b[4] = x2; b[5] = x2 * z; b[6] = x2 * z * z; b[7] = x2 * x2;
        }

        internal const int FaceBasisCount = 8;

        /// <summary>
        /// 前面の点群 (x,z)→y に左右対称の多項式を最小二乗で当て、残差が <paramref name="trim"/> を超える点
        /// （鼻・眼窩）を外して当て直す。座標は <paramref name="sx"/>/<paramref name="sz"/> で割って正規化済みとして扱う。
        /// 解けなければ null。
        /// </summary>
        internal static float[] FitFaceSurface(List<Vector3> pts, float sx, float sz, float trim, int rounds, out int kept)
        {
            var use = new bool[pts.Count];
            for (int i = 0; i < use.Length; i++) use[i] = true;
            float[] coef = null;
            kept = pts.Count;
            var b = new float[FaceBasisCount];
            for (int round = 0; round <= rounds; round++)
            {
                var ata = new double[FaceBasisCount, FaceBasisCount];
                var aty = new double[FaceBasisCount];
                int n = 0;
                for (int i = 0; i < pts.Count; i++)
                {
                    if (!use[i]) continue;
                    FaceBasis(pts[i].x / sx, pts[i].z / sz, b);
                    for (int r = 0; r < FaceBasisCount; r++)
                    {
                        aty[r] += b[r] * pts[i].y;
                        for (int c = 0; c < FaceBasisCount; c++) ata[r, c] += b[r] * b[c];
                    }
                    n++;
                }
                if (n < FaceBasisCount * 2) return coef;
                var sol = SolveSymmetric(ata, aty);
                if (sol == null) return coef;
                coef = sol;
                kept = n;
                if (round == rounds) break;
                for (int i = 0; i < pts.Count; i++)
                    use[i] = Mathf.Abs(pts[i].y - EvalFace(coef, pts[i].x / sx, pts[i].z / sz)) <= trim;
            }
            return coef;
        }

        internal static float EvalFace(float[] coef, float x, float z)
        {
            var b = new float[FaceBasisCount];
            FaceBasis(x, z, b);
            float y = 0f;
            for (int i = 0; i < FaceBasisCount; i++) y += coef[i] * b[i];
            return y;
        }

        /// <summary>ガウス消去（部分ピボット）。特異なら null。</summary>
        private static float[] SolveSymmetric(double[,] a, double[] y)
        {
            int n = y.Length;
            var m = new double[n, n + 1];
            for (int r = 0; r < n; r++) { for (int c = 0; c < n; c++) m[r, c] = a[r, c]; m[r, n] = y[r]; }
            for (int p = 0; p < n; p++)
            {
                int best = p;
                for (int r = p + 1; r < n; r++) if (System.Math.Abs(m[r, p]) > System.Math.Abs(m[best, p])) best = r;
                if (System.Math.Abs(m[best, p]) < 1e-12) return null;
                if (best != p) for (int c = 0; c <= n; c++) { var t = m[p, c]; m[p, c] = m[best, c]; m[best, c] = t; }
                for (int r = p + 1; r < n; r++)
                {
                    double f = m[r, p] / m[p, p];
                    for (int c = p; c <= n; c++) m[r, c] -= f * m[p, c];
                }
            }
            var x = new float[n];
            for (int r = n - 1; r >= 0; r--)
            {
                double s = m[r, n];
                for (int c = r + 1; c < n; c++) s -= m[r, c] * x[c];
                x[r] = (float)(s / m[r, r]);
            }
            return x;
        }

        /// <summary>窓の重み（1=完全に均す〜0=元の形）。dx=|x|、dz=鼻先からの高さ。</summary>
        internal static float FaceMask(float dx, float dz)
        {
            float wx = 1f - Smooth01((dx - FaceFlatHalfWidth) / (FaceFadeHalfWidth - FaceFlatHalfWidth));
            float wTop = 1f - Smooth01((dz - FaceFlatTop) / (FaceFadeTop - FaceFlatTop));
            float wBot = 1f - Smooth01((FaceFlatBottom - dz) / (FaceFlatBottom - FaceFadeBottom));
            return wx * wTop * wBot;
        }

        private static float Smooth01(float t)
        {
            t = Mathf.Clamp01(t);
            return t * t * (3f - 2f * t);
        }

        /// <summary>
        /// 頭部（<paramref name="headSubmesh"/>）の顔の前面を均す。メッシュ空間は Z-up・顔が -y 側（ClassifyCap の実測）。
        /// 鼻先＝頭部で最も前（y 最小）の頂点を窓の基準にする（歯・舌の島は除く）。
        /// </summary>
        internal static string FlattenFace(Mesh mesh, int headSubmesh)
        {
            if (headSubmesh < 0 || headSubmesh >= mesh.subMeshCount) return "顔の均し=SKIP（頭部サブメッシュなし）";
            var v = mesh.vertices;
            var n = mesh.normals;
            var uv = mesh.uv;
            if (n == null || n.Length != v.Length) return "顔の均し=SKIP（法線なし）";
            var inHead = new HashSet<int>(mesh.GetTriangles(headSubmesh));

            var min = Vector3.positiveInfinity;
            var max = Vector3.negativeInfinity;
            int tip = -1;
            foreach (var i in inHead)
            {
                min = Vector3.Min(min, v[i]); max = Vector3.Max(max, v[i]);
                if (uv[i].y >= MouthIslandMinV) continue;
                if (tip < 0 || v[i].y < v[tip].y) tip = i;
            }
            if (tip < 0) return "顔の均し=SKIP（鼻先が見つからない）";
            float frontY = min.y + (max.y - min.y) * FaceFrontShare;
            var nose = v[tip];

            // 当てはめ: 窓より一回り広く取る（縁の外も面が実物に沿っていないと縁で段になる）
            var pts = new List<Vector3>();
            foreach (var i in inHead)
            {
                if (uv[i].y >= MouthIslandMinV || v[i].y > frontY) continue;
                float dx = v[i].x - nose.x, dz = v[i].z - nose.z;
                if (Mathf.Abs(dx) > FaceFadeHalfWidth + 0.01f || dz > FaceFadeTop + 0.01f || dz < FaceFadeBottom - 0.01f) continue;
                pts.Add(new Vector3(dx, v[i].y, dz));
            }
            const float sx = 0.06f, sz = 0.08f;
            int kept;
            var coef = FitFaceSurface(pts, sx, sz, FaceFitTrim, 4, out kept);
            if (coef == null) return "顔の均し=SKIP（当てはめが解けない 点=" + pts.Count + "）";

            int moved = 0;
            float maxShift = 0f;
            foreach (var i in inHead)
            {
                if (v[i].y > frontY) continue;
                float dx = v[i].x - nose.x, dz = v[i].z - nose.z;
                float w = FaceMask(Mathf.Abs(dx), dz);
                if (w <= 0f) continue;
                float y = EvalFace(coef, dx / sx, dz / sz);
                // 面の法線（前＝-y）。F = y - P(x,z) の勾配の逆向き
                const float h = 1e-3f;
                float py = (EvalFace(coef, (dx + h) / sx, dz / sz) - EvalFace(coef, (dx - h) / sx, dz / sz)) / (2f * h);
                float pz = (EvalFace(coef, dx / sx, (dz + h) / sz) - EvalFace(coef, dx / sx, (dz - h) / sz)) / (2f * h);
                var nf = new Vector3(py, -1f, pz).normalized;
                float shift = Mathf.Abs(y - v[i].y) * w;
                maxShift = Mathf.Max(maxShift, shift);
                v[i] = new Vector3(v[i].x, Mathf.Lerp(v[i].y, y, w), v[i].z);
                n[i] = Vector3.Slerp(n[i].normalized, nf, w).normalized;
                moved++;
            }
            mesh.vertices = v;
            mesh.normals = n;
            mesh.ClearBlendShapes();
            mesh.RecalculateBounds();
            return "顔の均し: 当てはめ点=" + kept + "/" + pts.Count + " 動かした頂点=" + moved
                 + " 最大移動=" + (maxShift * 1000f).ToString("F1") + "mm / 鼻先 " + nose.ToString("F3")
                 + " / ブレンドシェイプを落とした";
        }

        /// <summary>つばと判定する前方の割合（境界箱の y 幅に対する比）。</summary>
        private const float VisorFrontShare = 0.25f;

        /// <summary>同・高さの割合（境界箱の z 高さに対する比）。</summary>
        private const float VisorHeightShare = 0.40f;

        /// <summary>
        /// Suit を三角形単位で Shirt / Undershirt に振り分ける。頂点ではなく三角形の UV 重心で
        /// 元テクスチャを引く＝UV島の縁でシームの外側を拾う事故を避ける。
        /// 白ユニと紺アンダーは UV島が分かれている＝縫い目で頂点も割れているはずなので、
        /// 1頂点が両領域の三角形に共有されることは原則ない。起きた分は多数決にして件数を報告する。
        /// </summary>
        private static int[] ClassifySuit(Mesh mesh, Texture2D tex, out string note)
        {
            var uv = mesh.uv;
            var tris = mesh.triangles;
            int vcount = mesh.vertexCount;

            var voteShirt = new int[vcount];
            var voteUnder = new int[vcount];
            int triShirt = 0, triUnder = 0;

            for (int t = 0; t < tris.Length; t += 3)
            {
                int a = tris[t], b = tris[t + 1], c = tris[t + 2];
                var mid = (uv[a] + uv[b] + uv[c]) / 3f;
                var col = tex.GetPixelBilinear(mid.x, mid.y);
                float luma = 0.2126f * col.r + 0.7152f * col.g + 0.0722f * col.b;
                bool navy = luma < NavyLuma && col.b > col.r * NavyBlueOverRed;
                if (navy) { triUnder++; voteUnder[a]++; voteUnder[b]++; voteUnder[c]++; }
                else { triShirt++; voteShirt[a]++; voteShirt[b]++; voteShirt[c]++; }
            }

            int conflicts = 0;
            var outRegion = new int[vcount];
            for (int i = 0; i < vcount; i++)
            {
                if (voteShirt[i] > 0 && voteUnder[i] > 0) conflicts++;
                outRegion[i] = (voteUnder[i] > voteShirt[i]) ? (int)Region.Undershirt : (int)Region.Shirt;
            }

            note = "三角形 Shirt=" + triShirt + " Undershirt=" + triUnder
                 + " / 頂点の領域またぎ=" + conflicts + "（0なら島が完全に分離）";
            return outRegion;
        }

        /// <summary>
        /// Pants を三角形単位で Pants / Belt に振り分ける。ベルトは**別メッシュではなく Pants の中の帯**で、
        /// テクスチャ上は専用のUV島（濃茶の革）として描かれている。ここを塗り分けないと白ズボンの上に
        /// 白いベルトが乗るだけになり、腰に何も無いように見える（2026-07-29 監督指摘）。
        /// 判定・多数決は <see cref="ClassifySuit"/> と同じ流儀。
        /// </summary>
        private static int[] ClassifyPants(Mesh mesh, Texture2D tex, out string note)
        {
            var uv = mesh.uv;
            var tris = mesh.triangles;
            int vcount = mesh.vertexCount;

            var votePants = new int[vcount];
            var voteBelt = new int[vcount];
            int triPants = 0, triBelt = 0;

            for (int t = 0; t < tris.Length; t += 3)
            {
                int a = tris[t], b = tris[t + 1], c = tris[t + 2];
                var mid = (uv[a] + uv[b] + uv[c]) / 3f;
                var col = tex.GetPixelBilinear(mid.x, mid.y);
                float luma = 0.2126f * col.r + 0.7152f * col.g + 0.0722f * col.b;
                if (luma < BeltLuma) { triBelt++; voteBelt[a]++; voteBelt[b]++; voteBelt[c]++; }
                else { triPants++; votePants[a]++; votePants[b]++; votePants[c]++; }
            }

            int conflicts = 0;
            var outRegion = new int[vcount];
            for (int i = 0; i < vcount; i++)
            {
                if (votePants[i] > 0 && voteBelt[i] > 0) conflicts++;
                outRegion[i] = (voteBelt[i] > votePants[i]) ? (int)Region.Belt : (int)Region.Pants;
            }

            note = "三角形 Pants=" + triPants + " Belt=" + triBelt
                 + " / 頂点の領域またぎ=" + conflicts + "（0なら島が完全に分離）";
            return outRegion;
        }

        /// <summary>袖口での半径倍率。実物のユニフォームの袖はアンダーシャツの腕より明らかに太い。</summary>
        private const float SleeveFlare = 1.45f;

        /// <summary>この回のベイクで使う倍率（既定は <see cref="SleeveFlare"/>）。切り分け用に差し替える。</summary>
        private static float _flare = SleeveFlare;

        /// <summary>
        /// ユニフォームの袖を張り出させる。購入素体の袖は**アンダーシャツの腕とほぼ同じ太さの筒**で、
        /// 色を塗り分けただけでは「腕に色の境目が描いてある」ようにしか見えない
        /// （2026-07-29 監督指摘「袖口は大きく、それが再現されていない」）。
        ///
        /// 袖はアンダーシャツの腕とは**別のシェル**（袖口は塞がっていて、腕の筒がその中を通っている）
        /// なので、外へ広げても穴は開かない。肩から袖口へ向けて半径を <see cref="SleeveFlare"/> 倍まで
        /// なめらかに太らせる。
        ///
        /// 腕の軸は**上腕ボーンのバインドポーズ**から取る。素体はAポーズで腕がX軸に平行でないため、
        /// メッシュ座標から軸を推測すると胴の頂点まで巻き込む（2026-07-29 実測で確認）。
        /// 法線は触らない: 非溶接メッシュを RecalculateNormals すると UV島の境目が全部ハードエッジになり
        /// 体が面取りされて見える。半径方向の緩い拡大では法線の狂いはポスタライズ後に判別できない。
        /// </summary>
        private static string FlareSleeves(Mesh mesh, int[] region, SkinnedMeshRenderer smr, Animator anim)
        {
            if (smr == null || anim == null || !anim.isHuman) return "袖の張り出し=SKIP（Humanoidでない）";
            var bw = mesh.boneWeights;
            var bind = mesh.bindposes;
            if (bw == null || bw.Length != mesh.vertexCount || bind == null || bind.Length == 0)
                return "袖の張り出し=SKIP（スキン情報なし）";

            var verts = mesh.vertices;
            int moved = 0;
            var arms = new[]
            {
                new[] { HumanBodyBones.LeftUpperArm, HumanBodyBones.LeftLowerArm },
                new[] { HumanBodyBones.RightUpperArm, HumanBodyBones.RightLowerArm },
            };
            foreach (var arm in arms)
            {
                int bi = BoneIndex(smr, anim, arm[0]);
                int bj = BoneIndex(smr, anim, arm[1]);
                if (bi < 0 || bj < 0) continue;
                // bindpose は「メッシュ→ボーン」なので、逆行列でボーン原点をメッシュ空間へ戻す
                var p0 = bind[bi].inverse.MultiplyPoint3x4(Vector3.zero);
                var p1 = bind[bj].inverse.MultiplyPoint3x4(Vector3.zero);
                float len = (p1 - p0).magnitude;
                if (len < 1e-4f) continue;
                var u = (p1 - p0) / len;

                // 袖＝ユニ上の領域かつ支配ボーンが上腕。袖口＝その中で一番肘寄りの位置
                float tMax = 0f;
                for (int i = 0; i < verts.Length; i++)
                    if (region[i] == (int)Region.Shirt && Dominant(bw[i]) == bi)
                        tMax = Mathf.Max(tMax, Vector3.Dot(verts[i] - p0, u) / len);
                if (tMax <= 0.05f) continue;

                for (int i = 0; i < verts.Length; i++)
                {
                    if (region[i] != (int)Region.Shirt || Dominant(bw[i]) != bi) continue;
                    float t = Mathf.Clamp01(Vector3.Dot(verts[i] - p0, u) / len / tMax);
                    float w = t * t * (3f - 2f * t);   // 肩=0・袖口=1 のなめらかな立ち上がり
                    var d = verts[i] - p0;
                    var along = u * Vector3.Dot(d, u);
                    verts[i] = p0 + along + (d - along) * Mathf.Lerp(1f, _flare, w);
                    moved++;
                }
            }

            mesh.vertices = verts;
            mesh.RecalculateBounds();
            return "袖を張り出した頂点=" + moved + "（袖口 " + _flare.ToString("F2") + "倍）";
        }

        /// <summary>袖の断面を調べる刻み[m]。</summary>
        private const float SleeveSliceStepM = 0.004f;

        /// <summary>断面の半幅が袖の真ん中のこの割合を下回ったら、そこから先は筒でない（袖山・袖口の縁）とみなす。</summary>
        private const float SleeveTubeShare = 0.8f;

        /// <summary>袖付けの位置の胴の x を平均するとき、袖付けの前後この範囲[m]の頂点を見る。</summary>
        private const float SleeveSeamBandM = 0.01f;

        /// <summary>
        /// 袖の縞の刻み座標を「腕軸に垂直な断面の中の位置（-1〜+1）× 袖の真ん中の半幅」で上書きする（2026-11-24 監督指摘）。
        /// 縞は腕に沿って平行に通る＝腕を下ろすと胴と同じ縦縞（袖は型紙の縦を肩→袖口に取って裁つ）。
        /// 対象＝ユニ上の領域かつ支配ボーンが上腕/前腕。軸は <see cref="FlareSleeves"/> と同じく上腕ボーンの**バインドポーズ**、
        /// 断面の中の「左右」は腕軸に垂直で正面から見える向き（x を腕軸に垂直へ倒した向き）。断面の幅で正規化するのは
        /// ズボンの脚と同じ理由（張り出した袖は円錐なので、正規化しないと側面で縞が輪になる。<see cref="PantsStripeAlongLegs"/>）。
        /// 筒になる手前（肩の天辺）は身頃として胴の縞のまま残し、そこを袖付けとする（縫い目で縞が食い違うのは実物どおり）。
        /// 単位は胴と同じメッシュ空間[m]。
        /// </summary>
        private static string SleeveStripeAlongArm(Mesh mesh, ref int[] region, SkinnedMeshRenderer smr, Animator anim, ref Vector2[] uv1)
        {
            if (smr == null || anim == null || !anim.isHuman) return "袖の縞=SKIP（Humanoidでない）";
            var bw = mesh.boneWeights;
            var bind = mesh.bindposes;
            if (bw == null || bw.Length != mesh.vertexCount || bind == null || bind.Length == 0)
                return "袖の縞=SKIP（スキン情報なし）";
            var verts = mesh.vertices;
            var tris = mesh.triangles;
            int failed = 0;
            var arms = new[]
            {
                new[] { HumanBodyBones.LeftUpperArm, HumanBodyBones.LeftLowerArm },
                new[] { HumanBodyBones.RightUpperArm, HumanBodyBones.RightLowerArm },
            };
            var sleeves = new List<SleeveFrame>();
            var vertArm = new int[verts.Length];   // 袖（筒の区間）の頂点ならその腕の添字、それ以外（胴・肩の天辺）は -1
            for (int i = 0; i < vertArm.Length; i++) vertArm[i] = -1;
            foreach (var arm in arms)
            {
                int bi = BoneIndex(smr, anim, arm[0]);
                int bj = BoneIndex(smr, anim, arm[1]);
                if (bi < 0 || bj < 0) continue;
                var p0 = bind[bi].inverse.MultiplyPoint3x4(Vector3.zero);
                var p1 = bind[bj].inverse.MultiplyPoint3x4(Vector3.zero);
                if ((p1 - p0).sqrMagnitude < 1e-8f) continue;
                var f = new SleeveFrame { Along = (p1 - p0).normalized };
                f.Across = (Vector3.right - f.Along * Vector3.Dot(Vector3.right, f.Along)).normalized;

                var member = new bool[verts.Length];
                float tMin = float.MaxValue, tMax = float.MinValue;
                for (int i = 0; i < verts.Length; i++)
                {
                    if (region[i] != (int)Region.Shirt) continue;
                    int dom = Dominant(bw[i]);
                    if (dom != bi && dom != bj) continue;
                    member[i] = true;
                    float t = Vector3.Dot(verts[i], f.Along);
                    tMin = Mathf.Min(tMin, t); tMax = Mathf.Max(tMax, t);
                }
                f.Edges = TubeEdges(verts, tris, i => member[i]);
                float mid = (tMin + tMax) * 0.5f;
                if (!SliceAcross(f.Edges, f.Along, f.Across, mid, out float loRef, out float hiRef)) continue;
                f.HRef = (hiRef - loRef) * 0.5f;
                // 筒として成り立っている区間 [TLo, THi]。袖山（肩の天辺）・袖口の縁は断面が細く潰れている。
                // 肩の天辺は筒の「ふた」で、ここに腕に沿った縞を載せると全部の縞がふたへ密集して細かい線の束になる
                // （2026-11-24 実測）＝TLo より肩側は身頃として胴の縦縞のまま残し、TLo を袖付けとする。袖口側は端の断面を使い回す
                f.TLo = mid; f.THi = mid;
                for (float t = mid; t >= tMin; t -= SleeveSliceStepM)
                {
                    if (!SliceAcross(f.Edges, f.Along, f.Across, t, out float a0, out float a1) || (a1 - a0) * 0.5f < f.HRef * SleeveTubeShare) break;
                    f.TLo = t;
                }
                for (float t = mid; t <= tMax; t += SleeveSliceStepM)
                {
                    if (!SliceAcross(f.Edges, f.Along, f.Across, t, out float a0, out float a1) || (a1 - a0) * 0.5f < f.HRef * SleeveTubeShare) break;
                    f.THi = t;
                }
                // 袖の座標の原点を袖付けの位置の胴の x に揃える（縫い目で縞が食い違う幅を小さくする）
                float sumX = 0f; int nX = 0;
                for (int i = 0; i < verts.Length; i++)
                    if (member[i] && Mathf.Abs(Vector3.Dot(verts[i], f.Along) - f.TLo) < SleeveSeamBandM) { sumX += verts[i].x; nX++; }
                f.COff = nX > 0 ? sumX / nX : 0f;

                int a = sleeves.Count;
                sleeves.Add(f);
                for (int i = 0; i < verts.Length; i++)
                {
                    if (!member[i] || Vector3.Dot(verts[i], f.Along) < f.TLo) continue;
                    if (!f.Coord(verts[i], out float s)) { failed++; continue; }
                    uv1[i].y = s;
                    vertArm[i] = a;
                }
            }
            int moved = 0;
            for (int i = 0; i < vertArm.Length; i++) if (vertArm[i] >= 0) moved++;
            string seam = SplitSleeveSeam(mesh, vertArm, sleeves, ref region, ref uv1);
            return "袖の縞を腕に沿わせた頂点=" + moved + "（断面が採れず x のまま=" + failed + "）/ " + seam;
        }

        /// <summary>1本の袖の座標系（<see cref="SleeveStripeAlongArm"/>）。</summary>
        private sealed class SleeveFrame
        {
            public Vector3 Along, Across;
            public List<(Vector3 A, Vector3 B)> Edges;
            public float TLo, THi, HRef, COff;

            /// <summary>
            /// 袖の縞の刻み座標。断面の中の位置 u（-1〜+1）をそのまま使うと、筒の側面（u=±1）で値が止まり、そこに当たった縞が
            /// 太い棒に広がる（張り出した袖は側面が広く正面から見える＝2026-11-24 実測）。asin で周りに沿った長さへ直すと
            /// 縞の間隔が一周どこでも揃う。表と裏は側面で折り返す（同じ値を逆順に辿る）ので切れ目は出ない
            /// </summary>
            public bool Coord(Vector3 p, out float s)
            {
                s = 0f;
                float t = Mathf.Clamp(Vector3.Dot(p, Along), TLo, THi);
                if (!SliceAcross(Edges, Along, Across, t, out float lo, out float hi)) return false;
                float u = Mathf.Clamp((Vector3.Dot(p, Across) - (lo + hi) * 0.5f) / ((hi - lo) * 0.5f), -1f, 1f);
                s = COff + Mathf.Asin(u) * HRef;
                return true;
            }
        }

        /// <summary>
        /// 袖付けで頂点を割って、縞の刻み座標が三角形の中で混ざらないようにする（縫い目で縞がはっきり切れる＝実物どおり）。
        /// 割らないと、袖付けをまたぐ三角形の中で胴の x と袖の座標が補間され、縞が U 字の入れ子になる（2026-11-24 実測）。
        /// またぐ三角形は多数決で袖か胴に振り、少数側の頂点を複製して三角形の側の座標を持たせる。
        /// 位置・法線・ボーンの重みは同じなので陰影にも変形にも継ぎ目は出ない。
        /// </summary>
        private static string SplitSleeveSeam(Mesh mesh, int[] vertArm, List<SleeveFrame> sleeves, ref int[] region, ref Vector2[] uv1)
        {
            if (mesh.blendShapeCount > 0) return "袖付けの割り=SKIP（ブレンドシェイプあり）";
            var pos = new List<Vector3>(); mesh.GetVertices(pos);
            var nrm = new List<Vector3>(); mesh.GetNormals(nrm);
            var tan = new List<Vector4>(); mesh.GetTangents(tan);
            var uv0 = new List<Vector2>(); mesh.GetUVs(0, uv0);
            var col = new List<Color>(); mesh.GetColors(col);
            var bws = new List<BoneWeight>(mesh.boneWeights);
            var uv1L = new List<Vector2>(uv1);
            var regL = new List<int>(region);
            var dup = new Dictionary<(int, int), int>();   // (元の頂点, 割り当て先の腕 or -1=胴) → 複製
            int split = 0, dupCount = 0;

            int Dup(int i, int arm)
            {
                if (dup.TryGetValue((i, arm), out int j)) return j;
                j = pos.Count;
                pos.Add(pos[i]);
                if (nrm.Count > 0) nrm.Add(nrm[i]);
                if (tan.Count > 0) tan.Add(tan[i]);
                if (uv0.Count > 0) uv0.Add(uv0[i]);
                if (col.Count > 0) col.Add(col[i]);
                bws.Add(bws[i]);
                regL.Add(regL[i]);
                float s = pos[i].x;
                if (arm >= 0 && !sleeves[arm].Coord(pos[i], out s)) s = pos[i].x;
                uv1L.Add(new Vector2(uv1L[i].x, s));
                dup[(i, arm)] = j;
                dupCount++;
                return j;
            }

            var subTris = new List<int[]>();
            for (int sm = 0; sm < mesh.subMeshCount; sm++)
            {
                var tris = mesh.GetTriangles(sm);
                for (int t = 0; t < tris.Length; t += 3)
                {
                    int arm = -1, nSleeve = 0;
                    for (int k = 0; k < 3; k++)
                        if (vertArm[tris[t + k]] >= 0) { nSleeve++; arm = vertArm[tris[t + k]]; }
                    if (nSleeve == 0 || nSleeve == 3) continue;
                    int side = nSleeve >= 2 ? arm : -1;   // 多数決
                    for (int k = 0; k < 3; k++)
                        if (vertArm[tris[t + k]] != side) tris[t + k] = Dup(tris[t + k], side);
                    split++;
                }
                subTris.Add(tris);
            }

            mesh.SetVertices(pos);
            if (nrm.Count > 0) mesh.SetNormals(nrm);
            if (tan.Count > 0) mesh.SetTangents(tan);
            if (uv0.Count > 0) mesh.SetUVs(0, uv0);
            if (col.Count > 0) mesh.SetColors(col);
            mesh.boneWeights = bws.ToArray();
            for (int sm = 0; sm < subTris.Count; sm++) mesh.SetTriangles(subTris[sm], sm);
            uv1 = uv1L.ToArray();
            region = regL.ToArray();
            return "袖付けで割った三角形=" + split + "（複製した頂点=" + dupCount + "）";
        }

        /// <summary>3頂点とも <paramref name="member"/> の三角形の辺（筒状の部位の断面を採る用）。</summary>
        private static List<(Vector3 A, Vector3 B)> TubeEdges(Vector3[] verts, int[] tris, System.Func<int, bool> member)
        {
            var edges = new List<(Vector3 A, Vector3 B)>();
            for (int t = 0; t < tris.Length; t += 3)
            {
                if (!member(tris[t]) || !member(tris[t + 1]) || !member(tris[t + 2])) continue;
                for (int k = 0; k < 3; k++) edges.Add((verts[tris[t + k]], verts[tris[t + (k + 1) % 3]]));
            }
            return edges;
        }

        /// <summary>
        /// 平面 dot(p, along)=t で辺を切った交点の、<paramref name="across"/> 方向の最小・最大＝その位置の断面の左右の端。
        /// 輪切りの頂点は平面に乗っていない（1〜3cm ばらつく）ので、頂点を窓で拾わず辺の交点で厳密に採る。
        /// </summary>
        private static bool SliceAcross(List<(Vector3 A, Vector3 B)> edges, Vector3 along, Vector3 across, float t,
            out float lo, out float hi)
        {
            lo = float.MaxValue; hi = float.MinValue;
            foreach (var e in edges)
            {
                float t0 = Vector3.Dot(e.A, along), t1 = Vector3.Dot(e.B, along);
                if (t < Mathf.Min(t0, t1) || t > Mathf.Max(t0, t1)) continue;
                float f = Mathf.Abs(t1 - t0) < 1e-7f ? 0f : (t - t0) / (t1 - t0);
                float w = Mathf.Lerp(Vector3.Dot(e.A, across), Vector3.Dot(e.B, across), f);
                lo = Mathf.Min(lo, w); hi = Mathf.Max(hi, w);
            }
            return hi - lo > 1e-3f;
        }

        /// <summary>脚の正規化を始める高さ＝膝から股関節までのこの割合の所。ここより上（股下〜腰）は従来の x のまま。</summary>
        private const float LegStripeTopShare = 0.4f;

        /// <summary>
        /// ズボンの脚の縞の刻み座標を「その高さの脚の断面の中の左右位置（-1〜+1）× 基準の高さの半幅」で上書きする。
        ///
        /// バインドポーズの左右座標 x のまま刻むと、縞は x=一定の平面とズボンの面の交線＝**等高線**になる。
        /// 脚の側面は面が ±x を向いている（x がほとんど変わらない）ので、太さが数mm変わるだけで
        /// 平面が筒から抜けて交線が閉じ、**脚の側面に左右対称の輪**ができる（膝・裾の膨らみで顕著）。
        /// 高さごとに脚の左右の端を同じ座標へ揃えれば、どの高さでも縞の本数が同じになり、
        /// 縞は脚に沿った真っ直ぐな線になる（脚の傾き・先細りもまとめて消える）。
        ///
        /// 断面の端は**その高さの平面で辺を切った交点**から厳密に採る（脚の輪切りは平面でなく
        /// 1〜3cm ばらついているので、高さの窓で頂点を拾う方式だと隣の輪が混ざって端が揺れる）。
        /// 基準の高さ（太ももの上）では正規化後の座標が元の x と一致するので、その上の従来の x と継ぎ目なくつながる。
        /// 高さは膝・股関節ボーンの**バインドポーズ**から取る（メッシュは Z-up・design-18 §3.5.1b）。
        /// </summary>
        private static string PantsStripeAlongLegs(Mesh mesh, int[] region, SkinnedMeshRenderer smr, Animator anim, Vector2[] uv1)
        {
            if (smr == null || anim == null || !anim.isHuman) return "脚の縞=SKIP（Humanoidでない）";
            var bind = mesh.bindposes;
            int hipL = BoneIndex(smr, anim, HumanBodyBones.LeftUpperLeg);
            int kneeL = BoneIndex(smr, anim, HumanBodyBones.LeftLowerLeg);
            if (hipL < 0 || kneeL < 0 || bind == null || bind.Length == 0) return "脚の縞=SKIP（脚のボーンなし）";
            float zHip = bind[hipL].inverse.MultiplyPoint3x4(Vector3.zero).z;
            float zKnee = bind[kneeL].inverse.MultiplyPoint3x4(Vector3.zero).z;
            float zTop = zKnee + (zHip - zKnee) * LegStripeTopShare;

            var verts = mesh.vertices;
            var tris = mesh.triangles;
            int moved = 0, failed = 0;
            var up = Vector3.forward;   // メッシュは Z-up
            foreach (float side in new[] { -1f, 1f })
            {
                // この脚＝同じ側の頂点。断面は基準の高さより少し上の輪まで要るので、高さでは絞らない
                // （股より上は左右がつながるが、両端とも同じ側の三角形しか採らないので混ざらない）
                var edges = TubeEdges(verts, tris, i => verts[i].x * side > 0f);
                if (!SliceAcross(edges, up, Vector3.right, zTop, out float loRef, out float hiRef)) continue;
                float cRef = (loRef + hiRef) * 0.5f, hRef = (hiRef - loRef) * 0.5f;
                for (int i = 0; i < verts.Length; i++)
                {
                    var p = verts[i];
                    if (region[i] != (int)Region.Pants || p.x * side <= 0f || p.z >= zTop) continue;
                    if (!SliceAcross(edges, up, Vector3.right, p.z, out float lo, out float hi)) { failed++; continue; }
                    float u = (p.x - (lo + hi) * 0.5f) / ((hi - lo) * 0.5f);
                    uv1[i].y = cRef + u * hRef;
                    moved++;
                }
            }
            return "脚の縞を脚の幅で正規化した頂点=" + moved + "（断面が採れず x のまま=" + failed + "・上端 z=" + zTop.ToString("F3") + "）";
        }

        private static int BoneIndex(SkinnedMeshRenderer smr, Animator anim, HumanBodyBones human)
        {
            var t = anim.GetBoneTransform(human);
            if (t == null) return -1;
            var bones = smr.bones;
            for (int i = 0; i < bones.Length; i++) if (bones[i] == t) return i;
            return -1;
        }

        private static int Dominant(BoneWeight w)
        {
            int b = w.boneIndex0; float m = w.weight0;
            if (w.weight1 > m) { b = w.boneIndex1; m = w.weight1; }
            if (w.weight2 > m) { b = w.boneIndex2; m = w.weight2; }
            if (w.weight3 > m) { b = w.boneIndex3; }
            return b;
        }

        private static Mesh MeshOf(Renderer r)
        {
            var smr = r as SkinnedMeshRenderer;
            if (smr != null) return smr.sharedMesh;
            var mf = r.GetComponent<MeshFilter>();
            return mf == null ? null : mf.sharedMesh;
        }

        /// <summary>
        /// テクスチャを importer 設定に触らず読めるようにする（isReadable を書き換えると
        /// 購入アセット側の .meta を汚すため、ファイルを直接デコードする）。
        /// </summary>
        private static Texture2D LoadReadable(string assetPath)
        {
            var full = Path.Combine(Application.dataPath, "..", assetPath);
            if (!File.Exists(full)) return null;
            var tex = new Texture2D(2, 2, TextureFormat.RGBA32, false);
            if (!ImageConversion.LoadImage(tex, File.ReadAllBytes(full), false))
            {
                Object.DestroyImmediate(tex);
                return null;
            }
            return tex;
        }
    }
}
