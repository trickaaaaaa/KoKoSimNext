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
            { "A_JM7M_AC_0001_Head_0001", Region.Skin },
            { "LeftEye_Ball_Geo", Region.Skin },
            { "RightEye_Ball_Geo", Region.Skin },
            // 帽子は独立領域（2026-07-29。チームカラーをプレイヤーが領域ごとに決めるため、
            // アンダーと同色に固定していた従来運用をやめた）。
            // クラウンとつばは1メッシュだが、issue #621 で幾何で二分して塗り分けるようにした
            // （ClassifyCap。ここには載せず三角形単位で振り分ける）。
        };

        private const string CapMeshName = "A_Players_0001_Cap_0001";

        private const string SuitMeshName = "A_Players_0001_Suit_0001";
        private const string PantsMeshName = "A_Players_0001_Pants_0001";

        public static string BakeAll() => BakeAll(SleeveFlare);

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

            Directory.CreateDirectory(OutDir);

            // 袖の張り出し（FlareSleeves）は上腕ボーンの軸が要る。Animator.GetBoneTransform は
            // 実体化したヒエラルキーでないと引けないので、ここだけ一時インスタンスを立てる。
            // メッシュは共有アセットなので、読む先がプレハブでも実体でも同じものになる
            var inst = (GameObject)PrefabUtility.InstantiatePrefab(prefab);
            var anim = inst.GetComponent<Animator>();

            foreach (var r in inst.GetComponentsInChildren<Renderer>(true))
            {
                var mesh = MeshOf(r);
                if (mesh == null) continue;

                var copy = Object.Instantiate(mesh);
                copy.name = mesh.name;

                int[] regionPerVertex;
                string note;
                if (r.name == SuitMeshName)
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
                var rest = copy.vertices;
                var uv1 = new Vector2[copy.vertexCount];
                for (int i = 0; i < uv1.Length; i++)
                    uv1[i] = new Vector2(
                        KokoSim.Unity.Match.DuelUniformMeshBuilder.RegionUv(regionPerVertex[i]), rest[i].x);
                copy.uv2 = uv1;   // Mesh.uv2 == UV1（シェーダの TEXCOORD1）

                var outPath = OutDir + "/" + r.name + "_Regions.asset";
                AssetDatabase.DeleteAsset(outPath);
                AssetDatabase.CreateAsset(copy, outPath);
                sb.AppendLine(r.name + ": " + note);
            }

            Object.DestroyImmediate(inst);
            Object.DestroyImmediate(suitTex);
            Object.DestroyImmediate(pantsTex);
            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();
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
