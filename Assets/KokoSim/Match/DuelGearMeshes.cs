using System.Collections.Generic;
using UnityEngine;

namespace KokoSim.Unity.Match
{
    /// <summary>
    /// 装備メッシュ（Assets/DuelSpike/Generated/AIGear）のロードと「装着正位」への較正。
    /// 出自はすべてMeshy生成＋Blenderデシメート（tools/aigear-decimate.py）。
    /// bpy手続きモデリング（tools/aigear-plates.py）の板もの2点は、Meshy生成物へ差し替えて
    /// 2026-07-29 に廃止した。素体・クリップと同じくロードはエディタ限定（AssetDatabase）＝
    /// ビルド同梱方法は本実装時に決める。ロード不可時は null を返し、呼び元はプリミティブ装備へ
    /// フォールバックする（DuelStageRig.AttachGear）。
    ///
    /// 装着正位 = 「+Z が正面（視線方向）・+Y が上・1unit=1m・原点が装着基準点」。
    /// 生成メッシュは向き・スケールが資産ごとに不定なので、頂点を実測して座標系ごと焼き直す
    /// （バット・被り物と同じ「ボーン軸もTポーズも信用せず実測で焼く」流儀）。
    /// </summary>
    public static class DuelGearMeshes
    {
        private const string Dir = "Assets/DuelSpike/Generated/AIGear/";
        private static readonly Dictionary<string, Mesh> Cache = new Dictionary<string, Mesh>();

        // ---- 公開getter（較正済み・キャッシュ） --------------------------------

        /// <summary>帽子。原点=クラウン中心、つば+Z前方・俯角25°、クラウン幅0.24m。</summary>
        public static Mesh Cap() => Get("AIGear_Cap1_lo", CanonicalizeCap);

        /// <summary>捕手マスク（ケージ）。原点=中心、幅0.25m、正面+Z・上+Y。</summary>
        public static Mesh CatcherMask() => Get("AIGear_CatcherMask1_lo", m => CanonicalizeMeshy(m, 0.25f));

        /// <summary>
        /// 捕手ヘルメット（殻）。原点=中心、正面+Z・上+Y。
        /// **非一様**フィット（幅0.205 / 高0.195 / 奥行0.265）＝生成メッシュの素の比率が
        /// 0.174×0.260×0.159 で「頭より横に狭く前後に長い」ため、一様スケールではどう置いても
        /// 頭に合わない（幅で合わせると奥行0.353の頭巾、奥行で合わせると幅0.164で頭がはみ出す）。
        /// 15%程度の非一様は素の生成物の比率ズレの補正であり、ポスタライズ＋ピクセル化後は判別できない。
        /// </summary>
        public static Mesh CatcherHelmet() => Get("AIGear_CatcherHelmet0_lo",
            m => CanonicalizeMeshy(m, new Vector3(0.205f, 0.195f, 0.265f)));

        /// <summary>
        /// 胸プロテクター。原点=中心、正面+Z・上+Y。非一様フィット（幅0.40 / 高0.44 / 奥行0.15）。
        /// 生成物は肩当て・Yストラップ・パッドの段まで持つので、装着側の別置き肩当ては不要になる。
        /// 素の奥行0.242は胸当てとしては厚すぎる（体から浮く）ため、奥行だけ強めに詰めて胸に沿わせる。
        /// </summary>
        public static Mesh Chest() => Get("AIGear_ChestProtector1_lo",
            m => CanonicalizeMeshy(m, new Vector3(0.400f, 0.440f, 0.150f)));

        /// <summary>
        /// 野手・投手グラブ（左手用）。原点=中心、指が上（+Y）・ポケット開口が+Z（＝装着側で相手を向く）・親指が+X。
        /// **購入品があればそれを使い、無ければ Tripo 生成物へ落ちる**（issue #913）。購入品は再配布NG＝gitignore
        /// （design-18 §3.1。購入素体と同じ扱い）なので、他端末・CI では Tripo 版になる。どちらを読んだかは
        /// <see cref="GloveIsVendor"/> で分かる（装着の前後位置が形で違うため）。
        /// </summary>
        public static Mesh Glove()
        {
            var vendor = VendorGlove();
            GloveIsVendor = vendor != null;
            return vendor ?? TripoGlove();
        }

        /// <summary>直近の <see cref="Glove"/> が購入品を返したか。</summary>
        public static bool GloveIsVendor { get; private set; }

        /// <summary>
        /// 購入グラブ（CGTrader「baseball glove」Legendexpert 作・Royalty Free・2026-11-14 購入）。元の FBX は
        /// art-src/vendor-glove-a/glove-b.fbx（gitignore）。姿勢は最初から「正面 -Y・上 +Z・親指 -X」で回転不要。
        /// 22部品・60万tri を remesh 0.002m → 5,000tri へ。紐の垂れ（細長い島5本）はデシメート時に除いてある
        /// （tools/aigear-decimate.py の drop_thin＝残すとトゲに見える）。寸法は素のまま（幅0.232 / 高0.300 / 奥行0.191）。
        /// ウェブ（部品 afsas001）は内野手用の H 型で穴あきだったので、デシメート時に裏へ板を当てて塞いである
        /// （plate 引数・issue #927。投手用は握りを見せないために閉じたウェブを使う）。引数は aigear-decimate.py の頭に書いた。
        /// </summary>
        private static Mesh VendorGlove() => GetAt(VendorDir + "CGT_Glove_lo.fbx", m =>
        {
            CanonicalizeMeshy(m, new Vector3(0.232f, 0.300f, 0.191f));
            Unpainted(m);
        });

        /// <summary>
        /// Tripo（H2.5・画像→3D）の生成物（購入品が無い環境用）。Tripo はこのグラブを「指先が上・ポケットが横（-X）」で
        /// 立てて出したので、デシメート時に Z+90° で Meshy と同じ姿勢（正面 -Y・上 +Z）へ回してある
        /// （tools/aigear-decimate.py の rot 引数。ミットは仰向けで出てきて X+90°＝**向きは生成物ごとに違う**）。
        /// 背面の手首口が貫通していて手と奥の景色が見えたので、内側に芯（core）を入れて塞いである。
        /// 生成物は閉じかけの形で奥行 0.263m・親指が開いて幅 0.300m あるので、奥行を 0.16m・幅を 0.25m へ詰める。
        /// </summary>
        private static Mesh TripoGlove() => Get("AIGear_GloveT_lo", m =>
        {
            CanonicalizeMeshy(m, new Vector3(0.250f, 0.291f, 0.160f));
            // 塗り分けはしない（PaintPocket の「+Z を向く中心＝ポケットの内側」という読みが、閉じかけの
            // このグラブでは背面の指袋に乗ってしまう）。紐・指袋は形として出ているので革の一色で足りる。
            Unpainted(m);
        });

        /// <summary>
        /// 捕手ミット（左手用）。原点=中心、ポケット開口が+Z。Tripo（H2.5・画像→3D）の生成物（issue #915）。
        /// 元写真は**ポケット側の正面**（Assets/_AIGearRaw/ref-mitt-front.png）＝開いたポケット・縁の土手・左上のウェブが
        /// 生成の時点で出ている。Tripo は物を仰向けに寝かせて出すので、デシメート時に X+90° で Meshy と同じ姿勢
        /// （正面 -Y・上 +Z）へ回してある（tools/aigear-decimate.py の rot 引数）＝ここは CanonicalizeMeshy をそのまま通す。
        /// 生成物は奥行 0.205m とかなり椀状なので、奥行だけ 0.15m へ軽く詰める（旧 Meshy 版は 0.317→0.145 と
        /// 半分以下へ潰してポケットが消えていた）。紐通し穴が縁を一周するのは旧版と同じ＝remesh 0.0015m → 6,000tri。
        /// </summary>
        public static Mesh Mitt() => Get("AIGear_MittT_lo", m =>
        {
            CanonicalizeMeshy(m, new Vector3(0.258f, 0.300f, 0.150f));
            PaintPocket(m);
        });

        /// <summary>
        /// レガース片脚。原点=中心、膝が上（+Y）・正面+Z。
        /// 縦寸は装着側が脛の実測長へ合わせ直す（DuelStageRig）ので、ここでは素の比率を保つ目安値。
        /// </summary>
        public static Mesh LegGuard() => Get("AIGear_LegGuard1_lo",
            m => CanonicalizeMeshy(m, new Vector3(0.150f, 0.470f, 0.110f)));

        // ---- ロード ------------------------------------------------------------

        private static Mesh Get(string file, System.Action<Mesh> canonicalize)
            => GetAt(Dir + file + ".fbx", canonicalize);

        /// <summary>購入装備の置き場（gitignore。design-18 §3.1）。無い環境では各 getter が生成物へ落ちる。</summary>
        private const string VendorDir = "Assets/DuelSpike/Vendor/Gear/";

        private static Mesh GetAt(string path, System.Action<Mesh> canonicalize)
        {
            if (Cache.TryGetValue(path, out var hit)) return hit;
            var mesh = LoadMeters(path);
            if (mesh != null) canonicalize(mesh);
            Cache[path] = mesh;   // null もキャッシュ（毎フレームのロード再試行を避ける）
            return mesh;
        }

        private static Mesh LoadMeters(string path)
        {
#if UNITY_EDITOR
            var prefab = UnityEditor.AssetDatabase.LoadAssetAtPath<GameObject>(path);
            if (prefab == null) return null;
            var mf = prefab.GetComponentInChildren<MeshFilter>();
            if (mf == null || mf.sharedMesh == null) return null;
            var mesh = Object.Instantiate(mf.sharedMesh);
            mesh.name = System.IO.Path.GetFileNameWithoutExtension(path) + " (gear)";
            mesh.hideFlags = HideFlags.HideAndDontSave;
            // FBXノードのインポート姿勢（スケール100等）を頂点へ焼き、1unit=1mにする
            Apply(mesh, prefab.transform.worldToLocalMatrix * mf.transform.localToWorldMatrix);
            // サブメッシュを1つにまとめる。装備は1マテリアルで描くので、2つ目以降のサブメッシュは**描かれない**。
            // 購入グラブは部品ごとのマテリアル枠が12個残っていて、ウェブと掌の一部が丸ごと消えていた
            // （2026-11-14 監督指摘「おもっきしウェブなくない？」。Blender の絵では全部見えるので気づけない）
            if (mesh.subMeshCount > 1)
            {
                var all = mesh.triangles;   // 全サブメッシュの三角形を連結して返す
                mesh.subMeshCount = 1;
                mesh.SetTriangles(all, 0);
            }
            return mesh;
#else
            return null;
#endif
        }

        // ---- 較正 --------------------------------------------------------------

        private static void CanonicalizeCap(Mesh mesh)
        {
            // つばの向き＝「クラウン鉛直軸から水平距離が最大の頂点帯」の方向（下帯の重心では
            // クラウンの前傾ぶんで180°誤検出した 2026-07-28）。つば先→+Z へヨー
            var b = mesh.bounds;
            var crown = BandCentroid(mesh, b.max.y - b.size.y * 0.35f, b.max.y);
            var verts0 = mesh.vertices;
            float maxR = 0f;
            for (int i = 0; i < verts0.Length; i++)
            {
                var o = verts0[i] - crown; o.y = 0f;
                maxR = Mathf.Max(maxR, o.magnitude);
            }
            var horiz = Vector3.zero;
            for (int i = 0; i < verts0.Length; i++)
            {
                var o = verts0[i] - crown; o.y = 0f;
                if (o.magnitude > maxR * 0.85f) horiz += o;   // つば先帯（最遠15%圏）の平均方向
            }
            horiz.y = 0f;
            if (horiz.sqrMagnitude > 1e-8f)
                Apply(mesh, Matrix4x4.Rotate(Quaternion.FromToRotation(horiz.normalized, Vector3.forward)));
            // 垂れ角の矯正は全体回転でなく LiftBrim（つば頂点だけの変形）に一本化する。
            // 全体を回すとクラウンごと後傾して「開口が上を向くスクープ」になる（2026-07-28 実測）
            // クラウン幅→頭寸、クラウン中心→原点
            b = mesh.bounds;
            crown = BandCentroid(mesh, b.max.y - b.size.y * 0.45f, b.max.y);
            float crownWidth = BandWidthX(mesh, b.max.y - b.size.y * 0.45f, b.max.y);
            // 0.24 は「デカすぎる」（2026-07-28監督指摘）。頭蓋実測幅0.18-0.19 に沿わせて 0.22
            float s = 0.22f / Mathf.Max(0.01f, crownWidth);
            Apply(mesh, Matrix4x4.Scale(Vector3.one * s) * Matrix4x4.Translate(-crown));
            LiftBrim(mesh);
        }

        /// <summary>
        /// つばの持ち上げ。Meshy生成の帽子は「スタンド置き」で、つばが前縁からほぼ真下へ垂れている
        /// （実機で顔全体を覆うフードに見えた 2026-07-28）。全体回転ではクラウンごと傾くため、
        /// クラウン前縁のヒンジ（x軸線）まわりで**つば頂点だけ**を俯角25°まで回して起こす。
        /// </summary>
        private static void LiftBrim(Mesh mesh)
        {
            const float targetDeg = 25f;      // 実物の被り角（俯角）
            var verts = mesh.vertices;
            var b = mesh.bounds;
            // つば＝クラウン底より下に垂れている前方(z>0)の頂点。クラウン底はキャップ全高から推定
            // （クラウン中心=原点・クラウン高さ≒半幅0.12。垂れつば込みの全高がそれより深い分がつば）
            float crownBottom = -0.11f;
            // つば先（最下帯の重心）で現在の垂れ角を実測
            var tip = Vector3.zero; int n = 0;
            for (int i = 0; i < verts.Length; i++)
                if (verts[i].y < b.min.y + b.size.y * 0.12f) { tip += verts[i]; n++; }
            if (n == 0 || b.min.y > crownBottom - 0.02f) return;   // 垂れていない＝矯正不要
            tip /= n;
            // ヒンジ＝つば付け根（クラウン前縁の下端）。回転軸はここを通るX軸線
            var hinge = new Vector3(0f, crownBottom * 0.55f, Mathf.Max(0.02f, tip.z - 0.02f));
            var d = tip - hinge;
            float depression = Mathf.Atan2(-d.y, Mathf.Max(1e-4f, d.z)) * Mathf.Rad2Deg;
            float lift = targetDeg - depression;   // 負=起こす方向（Euler X）
            if (Mathf.Abs(lift) < 3f) return;
            for (int i = 0; i < verts.Length; i++)
            {
                // 後頭部側でも「クラウン縁より下」は垂れつばの端＝持ち上げ対象。
                // 前方(z)ゲートだけだと横〜後ろへ回り込んだつばの端が取り残され、
                // 首筋にタレて「後ろ髪」に見える（2026-07-28監督指摘の再発原因）
                if (verts[i].z < 0.02f && verts[i].y > crownBottom) continue;
                // クラウン底からの深さでなじませる（付け根は僅かに・先端はフルに回す）
                float w = Mathf.Clamp01((hinge.y - verts[i].y) / Mathf.Max(0.02f, hinge.y - tip.y));
                if (w <= 0f) continue;
                var rot = Quaternion.Euler(lift * w, 0f, 0f);
                verts[i] = hinge + rot * (verts[i] - hinge);
            }
            mesh.vertices = verts;
            mesh.RecalculateNormals();
            mesh.RecalculateBounds();
        }

        /// <summary>
        /// Meshy（画像→3D）生成物の正位化。**生成姿勢は資産によらず「正面 -Y・上 +Z」で揃う**
        /// （2026-07-29 実測: 捕手マスク・捕手ヘルメット・胸プロテクター・レガースの4点すべてを
        /// 6面レンダして確認）。ここで装着正位（正面+Z・上+Y）へ回してから、横幅を目標へ合わせ、
        /// 中心を原点へ置く。
        ///
        /// CanonicalizeShell が向きを「装着側の実測ヨーに任せる」設計なのは、寝た姿勢のままでは
        /// 前後も上下も合わないため成立しない。生成物は必ずここを通す。
        /// </summary>
        private static void CanonicalizeMeshy(Mesh mesh, float targetWidth)
        {
            RotateToWearing(mesh);
            var b = mesh.bounds;
            float s = targetWidth / Mathf.Max(0.01f, b.size.x);
            Apply(mesh, Matrix4x4.Scale(Vector3.one * s) * Matrix4x4.Translate(-b.center));
        }

        /// <summary>
        /// 同上の非一様版（軸ごとに目標寸法を指定する）。生成物は素の比率が実物とずれることがあり
        /// （捕手ヘルメットは頭より横に狭く前後に長い・胸プロテクターは厚すぎる）、一様スケールでは
        /// どう置いても body に合わない。10〜40%の軸別補正はピクセル化後には判別できない。
        /// </summary>
        private static void CanonicalizeMeshy(Mesh mesh, Vector3 targetSize)
        {
            RotateToWearing(mesh);
            var b = mesh.bounds;
            var s = new Vector3(
                targetSize.x / Mathf.Max(0.01f, b.size.x),
                targetSize.y / Mathf.Max(0.01f, b.size.y),
                targetSize.z / Mathf.Max(0.01f, b.size.z));
            Apply(mesh, Matrix4x4.Scale(s) * Matrix4x4.Translate(-b.center));
            mesh.RecalculateNormals();   // 非一様スケールでは法線が面へ直交しなくなるため取り直す
        }

        /// <summary>縁の革ひも（黄土色）。sRGB で書いてリニアへ直す（頂点色は SetColor の自動変換を通らない）。</summary>
        private static readonly Color RimLace = new Color(0.80f, 0.62f, 0.30f).linear;

        /// <summary>ポケットの内側（一段暗い革）。</summary>
        private static readonly Color PocketDark = new Color(0.20f, 0.10f, 0.05f).linear;

        /// <summary>
        /// ポケット側が読めるようにする頂点色の塗り分け（issue #822）。一色塗りでは前後の面が
        /// 同じ茶の円盤に見えるので、縁を回る明るい革ひも（全周）と、+Z 面の凹み（ポケットの内側）の
        /// 暗色を頂点色（a=重み）で載せる。位置と法線だけで決める＝手作業で頂点を触らない。
        /// 縁は外周の帯で、ポケット側（+Z）に寄るほど濃い。内側は +Z を向く法線で中心寄りだけ。
        /// </summary>
        private static void PaintPocket(Mesh mesh)
        {
            var verts = mesh.vertices;
            var normals = mesh.normals;
            var b = mesh.bounds;
            var colors = new Color[verts.Length];
            for (int i = 0; i < verts.Length; i++)
            {
                var v = verts[i] - b.center;
                float rx = v.x / Mathf.Max(1e-4f, b.extents.x);
                float ry = v.y / Mathf.Max(1e-4f, b.extents.y);
                float r = Mathf.Sqrt(rx * rx + ry * ry);
                float nz = normals.Length == verts.Length ? normals[i].z : 0f;
                float rim = Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(0.80f, 0.92f, r));
                float inside = Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(0.15f, 0.55f, nz))
                    * (1f - Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(0.60f, 0.80f, r)));
                var c = rim >= inside ? RimLace : PocketDark;
                // 重みは #822 当時より弱める（2026-11-14）。旧メッシュは凹みが無く色で凹みを装う必要があったが、
                // Tripo 製は凹み・縁が形として出ている＝強く塗ると「的」の輪に見えた
                c.a = Mathf.Max(rim * 0.6f, inside * 0.5f);
                colors[i] = c;
            }
            mesh.colors = colors;
        }

        /// <summary>頂点色を「重み0」（＝素の革色のまま）で埋める。</summary>
        private static void Unpainted(Mesh mesh)
        {
            var colors = new Color[mesh.vertexCount];   // 既定値 (0,0,0,0)＝重み0
            mesh.colors = colors;
        }

        /// <summary>生成姿勢（正面-Y・上+Z）→ 装着正位（正面+Z・上+Y）。</summary>
        private static void RotateToWearing(Mesh mesh) =>
            Apply(mesh, Matrix4x4.Rotate(Quaternion.Euler(-90f, 0f, 0f)));

        // 削除済みの較正（いずれも 2026-07-29。生成物の姿勢は既知なので推測コードは要らない）:
        //   CanonicalizeChest / CanonicalizeLegGuard — bpy製の板もの用。軸の長短と重心から上下・前後を
        //     推測するヒューリスティックだった。両方を Meshy 生成物へ差し替えたので不要
        //   CanonicalizeShell — 打者ヘルメット・球審マスクの生成殻用。どちらも生成物を採用せず
        //     プリミティブ構成が正（§3.3.1）と確定したので、getter ごと削除した

        // ---- 幾何ヘルパ --------------------------------------------------------

        private static void Apply(Mesh mesh, Matrix4x4 m)
        {
            var verts = mesh.vertices;
            for (int i = 0; i < verts.Length; i++) verts[i] = m.MultiplyPoint3x4(verts[i]);
            mesh.vertices = verts;
            var normals = mesh.normals;
            if (normals != null && normals.Length == verts.Length)
            {
                for (int i = 0; i < normals.Length; i++) normals[i] = m.MultiplyVector(normals[i]).normalized;
                mesh.normals = normals;
            }
            mesh.RecalculateBounds();
        }

        private static Vector3 BandCentroid(Mesh mesh, float yMin, float yMax)
        {
            var verts = mesh.vertices;
            var sum = Vector3.zero; int n = 0;
            for (int i = 0; i < verts.Length; i++)
                if (verts[i].y >= yMin && verts[i].y <= yMax) { sum += verts[i]; n++; }
            return n > 0 ? sum / n : mesh.bounds.center;
        }

        private static float BandWidthX(Mesh mesh, float yMin, float yMax)
        {
            var verts = mesh.vertices;
            float lo = float.MaxValue, hi = float.MinValue;
            for (int i = 0; i < verts.Length; i++)
                if (verts[i].y >= yMin && verts[i].y <= yMax) { lo = Mathf.Min(lo, verts[i].x); hi = Mathf.Max(hi, verts[i].x); }
            return hi > lo ? hi - lo : 0f;
        }
    }
}
