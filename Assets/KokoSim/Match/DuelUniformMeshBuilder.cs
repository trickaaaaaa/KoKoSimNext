using System.Collections.Generic;
using UnityEngine;

namespace KokoSim.Unity.Match
{
    /// <summary>
    /// 素体（UBC SuperheroMale）から「ユニフォームを着た形」のメッシュを手続き生成する（design-18 §3.5 U2方式）。
    ///
    /// 入力は素体の SkinnedMeshRenderer、出力は差し替え用の新しい Mesh。
    /// bindposes / boneWeights / ボーン配列は原本からそのままコピーする＝リターゲット鎖
    /// （リリースアンカー実測・yaw・接地）を一切壊さない。
    ///
    /// 処理順:
    ///   ① 頂点ごとに支配ボーン（最大重み）とセグメント軸位置 t を求め、領域
    ///      （ユニ上/ズボン/アンダーシャツ/ソックス/肌/シューズ）を分類。バインドポーズ空間なのでポーズ非依存
    ///   ② 痩せ — ボーン軸に垂直な成分を領域別係数でスケール。頂点の4ボーン重みで係数をブレンドするため
    ///      関節で滑らかに繋がる。ボーン位置は動かさない＝肘・手の軌道が変わらず、モーションの読みが不変
    ///   ③ 膨張 — ボーン＋t から厚みを決め法線方向に加算。厚みは境界近傍で滑らかに補間する
    ///      （段差にすると境界分割で亀裂が開くため。シルエットの段差1〜2pxは115pxでは読めず、
    ///      読まれるのは色境界＝④で鮮鋭化する方）。野球パンツはブラウジング（膝下で膨らみ
    ///      ストッキング境界で絞る）を t のカーブで表現
    ///   ④ 三角形化 — 三角形ごとに頂点を複製（非溶接化）し、領域IDを UV1.x に載せる。
    ///      **境界 t を跨ぐ三角形はイソラインで分割**して色境界を幾何的に鮮鋭にする
    ///      （初版の「3頂点の多数決」は袖口が三角形サイズのギザギザになった＝2026-07-28 スクショ）。
    ///      パレットテクスチャ＋UVブロック（旧§3.5案）より単純で、低解像度のUVブリードも原理的に無い
    ///
    /// 生成は DuelUniformBaker（Editor）が .asset へ焼く。ランタイムは焼いた資産を読むだけ。
    /// 素体メッシュ空間は Z-up（Blender出力）だが、ボーン軸と法線しか使わないため座標系の仮定は無い。
    /// </summary>
    public static class DuelUniformMeshBuilder
    {
        /// <summary>内部領域ID。UV1.x に (id + 0.5)/8 で載せ、シェーダーの _RegionColors[8] を引く。</summary>
        public enum Region
        {
            Shirt = 0,       // ユニ上（胴・半袖）
            Pants = 1,       // ズボン（腰〜膝下ブラウジングまで）
            Undershirt = 2,  // アンダーシャツ（上腕下部〜前腕・首）
            Socks = 3,       // ソックス（膝下〜足首）
            Skin = 4,        // 肌（頭・手・前腕先端）
            Shoes = 5,       // シューズ（固定暗色）
            Cap = 6,         // 帽子（クラウン＋つば。購入素体の Cap メッシュ全体・2026-07-29）
        }

        /// <summary>
        /// 生成パラメータ。境界位置 t は 2026-07-28 の実測プローブ（セグメント軸 t 分布）を基準に決めた。
        /// 定数の流儀はリポジトリ既存（アンカー実測・yaw と同じ「コメント付き既定値」）に合わせ、
        /// ScriptableObject にはしない。
        /// </summary>
        public struct Params
        {
            // ── 境界位置（セグメント軸上の t）──
            public float SleeveEnd;       // upperarm: これ以下がユニ半袖、以遠はアンダーシャツ
            public float UndershirtEnd;   // lowerarm: これ以下がアンダーシャツ、以遠は肌（手首）
            public float StockingStart;   // calf: これ以遠がソックス、手前はズボンの裾（ブラウジング）
            public float CollarEnd;       // neck: これ以下がアンダーシャツ（クルーネック）、以遠は肌

            // ── 布の厚み（法線方向・m）──
            public float ShirtThick;      // 胴のユニ
            public float SleeveThick;     // 半袖（ゆとりが出るぶん胴より厚め）
            public float UndershirtThick; // アンダーシャツ（密着）
            public float PantsThick;      // ズボン基本
            public float ThighSlack;      // 腰〜腿のゆとり増分。無いと「タイツ」に見える（2026-07-28監督指摘）。
                                          // 腰・腿上部で最大→膝へ向けて絞る
            public float BlousonThick;    // ブラウジング最大増分（膝直下で最大→ソックス境界で0へ）
            public float SocksThick;      // ソックス
            public float ShoesThick;      // シューズ

            // ── 痩せ係数（ボーン軸に垂直な成分のスケール。1=そのまま）──
            public float SlimTorso;       // spine_01..03 / pelvis
            public float SlimShoulder;    // clavicle
            public float SlimUpperArm;    // upperarm
            public float SlimForearm;     // lowerarm
            public float SlimThigh;       // thigh
            public float SlimCalf;        // calf
            public float SlimNeck;        // neck_01

            public bool FlatShading;      // true=面法線（比較用）。既定は原本のスムーズ法線を保持

            // ── 布の平滑化（Taubin λ|μ）──
            // 一様な法線オフセットだけだと布が尻・胸・ハムの曲線を全部なぞって「体のラインが出る」
            // （2026-07-28 監督指摘「けつが出てるし不自然」）。実物の布は張力で凸部を均すので、
            // 布領域の頂点を近傍平均で均してから厚みを乗せる。強度は領域別（ズボン最強・上衣中・他ゼロ）
            public int ClothSmoothIterations;   // Taubin往復の回数（0=無効）
        }

        /// <summary>厚みの境界ブレンド幅（t）。色境界は分割で鮮鋭・厚みだけ滑らかに繋ぐ。</summary>
        private const float ThickBand = 0.05f;

        /// <summary>痩せ無し（現状のムキムキのまま着衣だけ）。着衣プロトの基準。</summary>
        public static Params Slim0()
        {
            return new Params
            {
                SleeveEnd = 0.60f,
                UndershirtEnd = 0.85f,
                StockingStart = 0.35f,
                CollarEnd = 0.50f,
                ShirtThick = 0.015f,
                SleeveThick = 0.016f,
                UndershirtThick = 0.004f,
                PantsThick = 0.018f,
                ThighSlack = 0.012f,
                BlousonThick = 0.012f,
                SocksThick = 0.002f,
                ShoesThick = 0.004f,
                SlimTorso = 1f,
                SlimShoulder = 1f,
                SlimUpperArm = 1f,
                SlimForearm = 1f,
                SlimThigh = 1f,
                SlimCalf = 1f,
                SlimNeck = 1f,
                ClothSmoothIterations = 28,   // 拡散距離≈辺長×√(N×0.5)。尻・胸の凸と腰の凹（~15cm）を均す
            };
        }

        /// <summary>
        /// 痩せ中（高校生の中でもがっしりした選手）。2026-07-28 監督選定の既定。
        /// 肩(clavicle)だけ胴より強く絞る＝素体の逆三角形（V字）を緩和する（監督指摘「逆三角形すぎ」）。
        /// </summary>
        public static Params Slim1()
        {
            var p = Slim0();
            p.SlimTorso = 0.90f;
            p.SlimShoulder = 0.86f;
            p.SlimUpperArm = 0.84f;
            p.SlimForearm = 0.85f;
            p.SlimThigh = 0.92f;
            p.SlimCalf = 0.92f;
            p.SlimNeck = 0.94f;
            return p;
        }

        /// <summary>痩せ強（標準的な高校生の細さ）。</summary>
        public static Params Slim2()
        {
            var p = Slim0();
            p.SlimTorso = 0.82f;
            p.SlimShoulder = 0.82f;
            p.SlimUpperArm = 0.74f;
            p.SlimForearm = 0.77f;
            p.SlimThigh = 0.86f;
            p.SlimCalf = 0.88f;
            p.SlimNeck = 0.90f;
            return p;
        }

        // セグメント軸の「子ボーン」対応（軸 = 自ボーン頭 → 子ボーン頭）。
        // 素体はUnreal系命名（2026-07-28 実測: twist無し・指はhandの子）
        private static readonly Dictionary<string, string> AxisChild = new()
        {
            ["pelvis"] = "spine_01",
            ["spine_01"] = "spine_02",
            ["spine_02"] = "spine_03",
            ["spine_03"] = "neck_01",
            ["neck_01"] = "Head",
            ["clavicle_l"] = "upperarm_l",
            ["clavicle_r"] = "upperarm_r",
            ["upperarm_l"] = "lowerarm_l",
            ["upperarm_r"] = "lowerarm_r",
            ["lowerarm_l"] = "hand_l",
            ["lowerarm_r"] = "hand_r",
            ["thigh_l"] = "calf_l",
            ["thigh_r"] = "calf_r",
            ["calf_l"] = "foot_l",
            ["calf_r"] = "foot_r",
            ["foot_l"] = "ball_l",
            ["foot_r"] = "ball_r",
        };

        /// <summary>コーナー1点ぶんの中間データ（分割で辺上に新設する点もこの形で作る）。</summary>
        private struct Corner
        {
            public Vector3 Pos;      // 痩せ適用済み・膨張前
            public Vector3 Normal;   // 原本のスムーズ法線
            public BoneWeight Bw;
            public float Thick;      // 境界ブレンド済みの厚み
            public float T;          // 支配ボーンのセグメント軸位置
        }

        /// <summary>
        /// 着衣メッシュを生成する。渡された SkinnedMeshRenderer は読み取りのみ（破壊しない）。
        /// Edit mode 前提（isReadable=false でも Edit mode なら頂点が読める＝FBXのインポート設定は触らない）。
        /// </summary>
        public static Mesh Build(SkinnedMeshRenderer source, Params p)
        {
            var mesh = source.sharedMesh;
            var bones = source.bones;
            var verts = mesh.vertices;
            var normals = mesh.normals;
            var weights = mesh.boneWeights;
            var binds = mesh.bindposes;
            var tris = mesh.triangles;

            // ボーン名 → index / ボーン頭位置（メッシュ空間 = bindpose逆行列の平行移動）
            var nameToIndex = new Dictionary<string, int>();
            for (int i = 0; i < bones.Length; i++) nameToIndex[bones[i].name] = i;
            var heads = new Vector3[bones.Length];
            for (int i = 0; i < bones.Length; i++)
            {
                var m = binds[i].inverse;
                heads[i] = new Vector3(m.m03, m.m13, m.m23);
            }
            // 軸（正規化）と長さ。子の無いボーン（指先等）は axis=zero で t を使わない
            var axes = new Vector3[bones.Length];
            var lens = new float[bones.Length];
            var norms = new string[bones.Length];   // 左右接尾辞を落とした正規化名
            for (int i = 0; i < bones.Length; i++)
            {
                norms[i] = NormalizeBone(bones[i].name);
                string child;
                if (AxisChild.TryGetValue(bones[i].name, out child) && nameToIndex.ContainsKey(child))
                {
                    var d = heads[nameToIndex[child]] - heads[i];
                    lens[i] = d.magnitude;
                    axes[i] = lens[i] > 1e-6f ? d / lens[i] : Vector3.zero;
                }
            }

            // ── ①②③ 溶接状態のまま、頂点ごとに支配ボーン・t・痩せ位置・厚みを計算 ──
            int n = verts.Length;
            var dom = new int[n];
            var corner = new Corner[n];
            for (int i = 0; i < n; i++)
            {
                var w = weights[i];
                dom[i] = DominantBone(w);
                float t = AxisT(verts[i], dom[i], heads, axes, lens);

                var v = verts[i];
                var disp = Vector3.zero;
                disp += SlimDisp(v, w.boneIndex0, w.weight0, norms, heads, axes, p);
                disp += SlimDisp(v, w.boneIndex1, w.weight1, norms, heads, axes, p);
                disp += SlimDisp(v, w.boneIndex2, w.weight2, norms, heads, axes, p);
                disp += SlimDisp(v, w.boneIndex3, w.weight3, norms, heads, axes, p);

                corner[i] = new Corner
                {
                    Pos = v + disp,
                    Normal = normals[i],
                    Bw = w,
                    Thick = Thickness(norms[dom[i]], t, p),
                    T = t,
                };
            }

            // ── ②' 布の平滑化: 布領域の頂点を近傍平均で均す（凸部の体のラインを消す）──
            if (p.ClothSmoothIterations > 0)
                SmoothCloth(corner, verts, tris, dom, norms, p);

            // ── ④ 三角形化: 境界を跨ぐ三角形はイソラインで分割、それ以外は多数決 ──
            var outCorners = new List<Corner>(tris.Length + tris.Length / 4);
            var outRegions = new List<Region>(tris.Length / 3 + tris.Length / 12);
            int triCount = tris.Length / 3;
            for (int t3 = 0; t3 < triCount; t3++)
            {
                int i0 = tris[t3 * 3], i1 = tris[t3 * 3 + 1], i2 = tris[t3 * 3 + 2];
                float threshold;
                // 3頂点が同一支配ボーンで、そのボーンに境界があり、t が跨いでいる → イソライン分割
                if (dom[i0] == dom[i1] && dom[i1] == dom[i2]
                    && TryBoundary(norms[dom[i0]], p, out threshold)
                    && Straddles(corner[i0].T, corner[i1].T, corner[i2].T, threshold))
                {
                    SplitTriangle(corner[i0], corner[i1], corner[i2], threshold,
                        norms[dom[i0]], p, outCorners, outRegions);
                }
                else
                {
                    var r = MajorityRegion(norms[dom[i0]], corner[i0].T,
                        norms[dom[i1]], corner[i1].T, norms[dom[i2]], corner[i2].T, p);
                    outCorners.Add(corner[i0]);
                    outCorners.Add(corner[i1]);
                    outCorners.Add(corner[i2]);
                    outRegions.Add(r);
                }
            }

            // ── 展開: コーナー列 → 非溶接メッシュ（膨張＝法線方向の厚み加算はここで確定） ──
            int outN = outCorners.Count;
            var outVerts = new Vector3[outN];
            var outNormals = new Vector3[outN];
            var outWeights = new BoneWeight[outN];
            var outUv1 = new Vector2[outN];
            var outTris = new int[outN];
            for (int t3 = 0; t3 < outN / 3; t3++)
            {
                float u = ((int)outRegions[t3] + 0.5f) / 8f;
                Vector3 faceN = default;
                if (p.FlatShading)
                {
                    var a = outCorners[t3 * 3]; var b = outCorners[t3 * 3 + 1]; var cc = outCorners[t3 * 3 + 2];
                    faceN = Vector3.Cross(
                        (b.Pos + b.Normal * b.Thick) - (a.Pos + a.Normal * a.Thick),
                        (cc.Pos + cc.Normal * cc.Thick) - (a.Pos + a.Normal * a.Thick)).normalized;
                }
                for (int c = 0; c < 3; c++)
                {
                    int dst = t3 * 3 + c;
                    var cn = outCorners[dst];
                    outVerts[dst] = cn.Pos + cn.Normal * cn.Thick;
                    outNormals[dst] = p.FlatShading ? faceN : cn.Normal;
                    outWeights[dst] = cn.Bw;
                    // UV1.y = バインドポーズの左右座標（縦縞の刻み座標）。スキニング後の頂点で刻むと
                    // 縞が空間に固定され、体が縞の中を通り抜ける絵になる（DuelVendorRegionBaker と同じ理由）
                    outUv1[dst] = new Vector2(u, outVerts[dst].x);
                    outTris[dst] = dst;
                }
            }

            var result = new Mesh { name = mesh.name + "_Uniform" };
            result.indexFormat = outN > 65535
                ? UnityEngine.Rendering.IndexFormat.UInt32
                : UnityEngine.Rendering.IndexFormat.UInt16;
            result.vertices = outVerts;
            result.normals = outNormals;
            result.boneWeights = outWeights;
            result.uv2 = outUv1;             // mesh.uv2 = シェーダーの TEXCOORD1
            result.bindposes = binds;        // 原本のまま＝リターゲット鎖不変
            result.triangles = outTris;
            result.RecalculateBounds();
            return result;
        }

        /// <summary>
        /// 布領域の Taubin 平滑化（λ=0.5 / μ=-0.53 の往復＝体積収縮を抑えつつ凸凹を均す）。
        /// 一様オフセットの布は尻・胸・ハムの曲線を全部なぞる（監督指摘 2026-07-28「けつが出てる」）ため、
        /// 実物の布の張力に相当する処理として、布の掛かる頂点だけを近傍平均へ寄せる。
        /// 強度は領域で変える: ズボン1.0（尻を均す本丸）＞上衣0.75＞袖0.6＞アンダー0.15（密着）＞肌・ソックス0。
        /// UVシームで頂点が複製されていても、位置量子化の溶接グラフで同一視して裂け目を作らない。
        /// 肌との境界1リングは固定（factor=0）し、袖口・裾が体から浮くのを防ぐ。
        /// 領域境界の t は元位置から計算済みのため、平滑化しても色境界の位置はずれない。
        /// </summary>
        private static void SmoothCloth(Corner[] corner, Vector3[] origVerts, int[] tris,
            int[] dom, string[] norms, Params p)
        {
            int n = corner.Length;
            // 位置量子化で溶接（0.1mm格子）
            var canonOf = new Dictionary<Vector3Int, int>();
            var canon = new int[n];
            for (int i = 0; i < n; i++)
            {
                var v = origVerts[i];
                var key = new Vector3Int(Mathf.RoundToInt(v.x * 10000f),
                    Mathf.RoundToInt(v.y * 10000f), Mathf.RoundToInt(v.z * 10000f));
                int c;
                if (!canonOf.TryGetValue(key, out c)) { c = i; canonOf[key] = i; }
                canon[i] = c;
            }
            // 隣接（正準id同士）
            var adj = new Dictionary<int, HashSet<int>>();
            void Link(int a, int b)
            {
                if (a == b) return;
                if (!adj.TryGetValue(a, out var s)) { s = new HashSet<int>(); adj[a] = s; }
                s.Add(b);
            }
            for (int t3 = 0; t3 < tris.Length; t3 += 3)
            {
                int a = canon[tris[t3]], b = canon[tris[t3 + 1]], c = canon[tris[t3 + 2]];
                Link(a, b); Link(b, a); Link(b, c); Link(c, b); Link(c, a); Link(a, c);
            }
            // 平滑化係数（正準頂点単位・複製間で同値のはずだが安全のためmax）
            var factor = new float[n];
            for (int i = 0; i < n; i++)
            {
                float f = ClothFactor(norms[dom[i]], corner[i].T, p);
                if (f > factor[canon[i]]) factor[canon[i]] = f;
            }
            // 肌・ソックス境界の1リングを固定
            var anchored = new float[n];
            System.Array.Copy(factor, anchored, n);
            foreach (var kv in adj)
            {
                if (factor[kv.Key] <= 0f) continue;
                foreach (var nb in kv.Value)
                    if (factor[nb] <= 0f) { anchored[kv.Key] = 0f; break; }
            }
            // 純ラプラシアン反復（収縮許容）。当初Taubin往復（λ0.5/μ-0.53）で実装したが、
            // Taubinは形状保存型で高周波しか均せず、尻のような低周波の凸が丸ごと残った（2026-07-28実測）。
            // 布の張力は「凸を削り凹に橋を架ける」＝拡散そのものなので、μの巻き戻しを入れない
            var pos = new Vector3[n];
            for (int i = 0; i < n; i++) pos[canon[i]] = corner[i].Pos;
            var next = new Vector3[n];
            for (int it = 0; it < p.ClothSmoothIterations; it++)
            {
                foreach (var kv in adj)
                {
                    int i = kv.Key;
                    if (anchored[i] <= 0f) { next[i] = pos[i]; continue; }
                    var avg = Vector3.zero;
                    foreach (var nb in kv.Value) avg += pos[nb];
                    avg /= kv.Value.Count;
                    next[i] = pos[i] + (avg - pos[i]) * (0.5f * anchored[i]);
                }
                foreach (var kv in adj) pos[kv.Key] = next[kv.Key];
            }
            for (int i = 0; i < n; i++) corner[i].Pos = pos[canon[i]];
        }

        /// <summary>布平滑化の領域係数。ズボンが本丸（尻・ハム）、密着するアンダー・肌・ソックスは触らない。</summary>
        private static float ClothFactor(string bone, float t, Params p)
        {
            switch (bone)
            {
                case "pelvis":
                case "thigh": return 1f;
                case "calf": return t <= p.StockingStart ? 0.9f : 0f;   // ズボンの裾まで
                // 上衣も1.0: 0.75では胸・肩の筋肉線と腰のくびれが残った（2026-07-28監督指摘）。
                // ジャージは胸から腰まで直線的に垂れるのが実物
                case "spine_01":
                case "spine_02":
                case "spine_03":
                case "clavicle": return 1f;
                case "upperarm": return t <= p.SleeveEnd ? 0.6f : 0.15f;
                case "lowerarm": return t <= p.UndershirtEnd ? 0.15f : 0f;
                default: return 0f;
            }
        }

        /// <summary>
        /// 境界イソライン t=threshold で三角形をマーチング分割する（孤立1頂点＋対岸2頂点 → 1+2枚）。
        /// 交点のコーナーデータは辺上の線形補間（BoneWeightだけは近い方の端点を採る＝
        /// 境界は関節から離れたセグメント中腹にあり両端の支配が同じため補間誤差が出ない）。
        /// 厚みは Thickness() が境界近傍でブレンド済み＝両側のサブ三角形が同じ交点座標を共有し亀裂が開かない。
        /// </summary>
        private static void SplitTriangle(Corner c0, Corner c1, Corner c2, float threshold,
            string bone, Params p, List<Corner> outCorners, List<Region> outRegions)
        {
            var cs = new[] { c0, c1, c2 };
            var side = new bool[3];
            for (int i = 0; i < 3; i++) side[i] = cs[i].T > threshold;

            // 孤立頂点（他2つと逆側）を探す
            int lone = side[0] == side[1] ? 2 : (side[0] == side[2] ? 1 : 0);
            int a = (lone + 1) % 3, b = (lone + 2) % 3;

            var iA = LerpCorner(cs[lone], cs[a], threshold);
            var iB = LerpCorner(cs[lone], cs[b], threshold);

            // 孤立側 1枚: (lone, iA, iB)（巻き順は元の cyclic 順を保存）
            outCorners.Add(cs[lone]); outCorners.Add(iA); outCorners.Add(iB);
            outRegions.Add(Classify(bone, cs[lone].T, p));
            // 対岸クアッド 2枚: (iA, a, b) + (iA, b, iB)
            var far = Classify(bone, cs[a].T, p);
            outCorners.Add(iA); outCorners.Add(cs[a]); outCorners.Add(cs[b]);
            outRegions.Add(far);
            outCorners.Add(iA); outCorners.Add(cs[b]); outCorners.Add(iB);
            outRegions.Add(far);
        }

        private static Corner LerpCorner(Corner from, Corner to, float threshold)
        {
            float denom = to.T - from.T;
            float u = Mathf.Abs(denom) < 1e-6f ? 0.5f : Mathf.Clamp((threshold - from.T) / denom, 0.001f, 0.999f);
            return new Corner
            {
                Pos = Vector3.Lerp(from.Pos, to.Pos, u),
                Normal = Vector3.Slerp(from.Normal, to.Normal, u),
                Bw = u < 0.5f ? from.Bw : to.Bw,
                Thick = Mathf.Lerp(from.Thick, to.Thick, u),
                T = threshold,
            };
        }

        /// <summary>このボーン種に色境界（t閾値）があるか。</summary>
        private static bool TryBoundary(string bone, Params p, out float threshold)
        {
            switch (bone)
            {
                case "upperarm": threshold = p.SleeveEnd; return true;
                case "lowerarm": threshold = p.UndershirtEnd; return true;
                case "calf": threshold = p.StockingStart; return true;
                case "neck_01": threshold = p.CollarEnd; return true;
                default: threshold = 0f; return false;
            }
        }

        private static bool Straddles(float t0, float t1, float t2, float threshold)
        {
            bool s0 = t0 > threshold, s1 = t1 > threshold, s2 = t2 > threshold;
            return s0 != s1 || s1 != s2;
        }

        private static Region MajorityRegion(string b0, float t0, string b1, float t1, string b2, float t2, Params p)
        {
            var r0 = Classify(b0, t0, p);
            var r1 = Classify(b1, t1, p);
            var r2 = Classify(b2, t2, p);
            if (r1 == r2) return r1;   // r1=r2 なら r0 に関わらず多数（または全一致）
            return r0;                 // 全部違う場合と r0=r1 / r0=r2 の場合は r0
        }

        private static string NormalizeBone(string bone)
        {
            return bone.EndsWith("_l") || bone.EndsWith("_r") ? bone.Substring(0, bone.Length - 2) : bone;
        }

        private static int DominantBone(BoneWeight w)
        {
            int bi = w.boneIndex0; float bw = w.weight0;
            if (w.weight1 > bw) { bi = w.boneIndex1; bw = w.weight1; }
            if (w.weight2 > bw) { bi = w.boneIndex2; bw = w.weight2; }
            if (w.weight3 > bw) { bi = w.boneIndex3; bw = w.weight3; }
            return bi;
        }

        private static float AxisT(Vector3 v, int bone, Vector3[] heads, Vector3[] axes, float[] lens)
        {
            if (axes[bone] == Vector3.zero || lens[bone] < 1e-6f) return 0f;
            return Vector3.Dot(v - heads[bone], axes[bone]) / lens[bone];
        }

        /// <summary>①領域分類。正規化済みボーン名（左右接尾辞なし）とセグメント軸位置 t から6領域へ。</summary>
        private static Region Classify(string bone, float t, Params p)
        {
            switch (bone)
            {
                case "spine_01":
                case "spine_02":
                case "spine_03":
                case "clavicle":
                    return Region.Shirt;
                case "upperarm":
                    return t <= p.SleeveEnd ? Region.Shirt : Region.Undershirt;
                case "lowerarm":
                    return t <= p.UndershirtEnd ? Region.Undershirt : Region.Skin;
                case "neck_01":
                    return t <= p.CollarEnd ? Region.Undershirt : Region.Skin;
                case "pelvis":
                case "thigh":
                    return Region.Pants;
                case "calf":
                    return t <= p.StockingStart ? Region.Pants : Region.Socks;
                case "foot":
                case "ball":
                    return Region.Shoes;
                default:
                    return Region.Skin;   // Head・hand・指
            }
        }

        /// <summary>
        /// ③膨張の厚み。ボーン種と t だけで決まる（領域には依存しない）。
        /// 境界±ThickBand で両側の厚みへ滑らかにブレンド＝分割しても亀裂が開かない。
        /// ズボンの膝下はブラウジング（膨らみ→ソックス境界で絞り）。
        /// </summary>
        private static float Thickness(string bone, float t, Params p)
        {
            switch (bone)
            {
                case "spine_01":
                case "spine_02":
                case "spine_03":
                case "clavicle":
                    return p.ShirtThick;
                case "upperarm":
                    return BlendAt(t, p.SleeveEnd, p.SleeveThick, p.UndershirtThick);
                case "lowerarm":
                    return BlendAt(t, p.UndershirtEnd, p.UndershirtThick, 0f);
                case "neck_01":
                    return BlendAt(t, p.CollarEnd, p.UndershirtThick, 0f);
                case "pelvis":
                    return p.PantsThick + p.ThighSlack * 0.8f;   // 尻回りもだぶつかせる
                case "thigh":
                {
                    // 腰〜腿上部で最大のゆとり→膝へ向けて6割絞る（実物のズボンのドレープ。
                    // 一定厚だと脚に密着して「タイツ」に見える）
                    float taper = 1f - Mathf.SmoothStep(0f, 1f, Mathf.Clamp01((t - 0.55f) / 0.45f)) * 0.6f;
                    return p.PantsThick + p.ThighSlack * taper;
                }
                case "calf":
                {
                    // 膝直下（t=0）で最大、ソックス境界で絞ってソックス厚へ
                    float k = Mathf.Clamp01(1f - t / Mathf.Max(0.01f, p.StockingStart));
                    float pants = p.PantsThick + p.BlousonThick * k;
                    return BlendAt(t, p.StockingStart, pants, p.SocksThick);
                }
                case "foot":
                case "ball":
                    return p.ShoesThick;
                default:
                    return 0f;   // Head・hand・指
            }
        }

        /// <summary>閾値±ThickBand で before→after へスムーズにブレンド。</summary>
        private static float BlendAt(float t, float threshold, float before, float after)
        {
            float k = Mathf.SmoothStep(0f, 1f, Mathf.Clamp01((t - threshold + ThickBand) / (2f * ThickBand)));
            return Mathf.Lerp(before, after, k);
        }

        /// <summary>②痩せの1ボーンぶんの変位（重み付き）。軸の無いボーン・係数1は0を返す。</summary>
        private static Vector3 SlimDisp(Vector3 v, int bone, float weight, string[] norms,
            Vector3[] heads, Vector3[] axes, Params p)
        {
            if (weight <= 0f) return Vector3.zero;
            float s = SlimScale(norms[bone], p);
            if (Mathf.Approximately(s, 1f)) return Vector3.zero;
            var axis = axes[bone];
            if (axis == Vector3.zero) return Vector3.zero;
            var d = v - heads[bone];
            var perp = d - axis * Vector3.Dot(d, axis);
            return perp * (s - 1f) * weight;
        }

        private static float SlimScale(string bone, Params p)
        {
            switch (bone)
            {
                case "pelvis":
                case "spine_01":
                case "spine_02":
                case "spine_03": return p.SlimTorso;
                case "clavicle": return p.SlimShoulder;
                case "upperarm": return p.SlimUpperArm;
                case "lowerarm": return p.SlimForearm;
                case "thigh": return p.SlimThigh;
                case "calf": return p.SlimCalf;
                case "neck_01": return p.SlimNeck;
                default: return 1f;
            }
        }
    }
}
