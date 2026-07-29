using System.Collections.Generic;
using UnityEngine;

namespace KokoSim.Unity.Match
{
    /// <summary>
    /// 装備メッシュ（Assets/DuelSpike/Generated/AIGear）のロードと「装着正位」への較正。
    /// 出自はMeshy生成＋Blenderデシメート（tools/aigear-decimate.py）と bpy 手続きモデリング
    /// （tools/aigear-plates.py）。素体・クリップと同じくロードはエディタ限定（AssetDatabase）＝
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

        /// <summary>打者ヘルメット。原点=殻中心、幅0.26m。</summary>
        public static Mesh Helmet() => Get("AIGear_Helmet0_lo", m => CanonicalizeShell(m, 0.26f));

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

        /// <summary>球審マスク。原点=殻中心、幅0.26m。</summary>
        public static Mesh UmpMask() => Get("AIGear_UmpMask0_lo", m => CanonicalizeShell(m, 0.26f));

        /// <summary>
        /// 胸プロテクター。原点=中心、正面+Z・上+Y。非一様フィット（幅0.40 / 高0.44 / 奥行0.15）。
        /// 生成物は肩当て・Yストラップ・パッドの段まで持つので、装着側の別置き肩当ては不要になる。
        /// 素の奥行0.242は胸当てとしては厚すぎる（体から浮く）ため、奥行だけ強めに詰めて胸に沿わせる。
        /// </summary>
        public static Mesh Chest() => Get("AIGear_ChestProtector1_lo",
            m => CanonicalizeMeshy(m, new Vector3(0.400f, 0.440f, 0.150f)));

        /// <summary>
        /// 野手グラブ。原点=中心、指が上（+Y）・ポケット開口が+Z（＝装着側で相手を向く）。
        /// 非一様フィット（幅0.245 / 高0.295 / 奥行0.115）＝生成物は写真どおりポケットが開いた状態で
        /// 奥行0.185あり、そのままだと握った手より分厚い塊になる。
        /// **捕手ミットは別物**（指の区切りが無く縁が厚い）なので、これは流用しない。
        /// </summary>
        public static Mesh Glove() => Get("AIGear_Glove1_lo",
            m => CanonicalizeMeshy(m, new Vector3(0.245f, 0.295f, 0.115f)));

        /// <summary>
        /// 捕手ミット。原点=中心、ポケット開口が+Z。非一様フィット（幅0.30 / 高0.33 / 奥行0.145）。
        /// 生成物は 0.330×0.324×0.317 のほぼ球で、実物の「丸くて薄い」比率と大きく違うため奥行を強く詰める。
        /// **6,000tri と装備中で最も重い**が、これは削れないぶん（下の経緯）であって余裕ではない。
        /// </summary>
        public static Mesh Mitt() => Get("AIGear_Mitt1_lo",
            m => CanonicalizeMeshy(m, new Vector3(0.300f, 0.330f, 0.145f)));

        /// <summary>
        /// レガース片脚。原点=中心、膝が上（+Y）・正面+Z。
        /// 縦寸は装着側が脛の実測長へ合わせ直す（DuelStageRig）ので、ここでは素の比率を保つ目安値。
        /// </summary>
        public static Mesh LegGuard() => Get("AIGear_LegGuard1_lo",
            m => CanonicalizeMeshy(m, new Vector3(0.150f, 0.470f, 0.110f)));

        // ---- ロード ------------------------------------------------------------

        private static Mesh Get(string file, System.Action<Mesh> canonicalize)
        {
            if (Cache.TryGetValue(file, out var hit)) return hit;
            var mesh = LoadMeters(file);
            if (mesh != null) canonicalize(mesh);
            Cache[file] = mesh;   // null もキャッシュ（毎フレームのロード再試行を避ける）
            return mesh;
        }

        private static Mesh LoadMeters(string file)
        {
#if UNITY_EDITOR
            var prefab = UnityEditor.AssetDatabase.LoadAssetAtPath<GameObject>(Dir + file + ".fbx");
            if (prefab == null) return null;
            var mf = prefab.GetComponentInChildren<MeshFilter>();
            if (mf == null || mf.sharedMesh == null) return null;
            var mesh = Object.Instantiate(mf.sharedMesh);
            mesh.name = file + " (gear)";
            mesh.hideFlags = HideFlags.HideAndDontSave;
            // FBXノードのインポート姿勢（スケール100等）を頂点へ焼き、1unit=1mにする
            Apply(mesh, prefab.transform.worldToLocalMatrix * mf.transform.localToWorldMatrix);
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

        /// <summary>生成姿勢（正面-Y・上+Z）→ 装着正位（正面+Z・上+Y）。</summary>
        private static void RotateToWearing(Mesh mesh) =>
            Apply(mesh, Matrix4x4.Rotate(Quaternion.Euler(-90f, 0f, 0f)));

        private static void CanonicalizeShell(Mesh mesh, float targetWidth)
        {
            // 被り殻（ヘルメット・マスク）: 中心を原点へ、横幅を頭寸へ。向きは資産ごとの個体差が
            // 大きいため、装着側の実測ヨー（視線方向）に任せてここでは触らない
            var b = mesh.bounds;
            float s = targetWidth / Mathf.Max(0.01f, b.size.x);
            Apply(mesh, Matrix4x4.Scale(Vector3.one * s) * Matrix4x4.Translate(-b.center));
        }

        // 胸プロテクター・レガースの bpy 製メッシュ用にあった CanonicalizeChest / CanonicalizeLegGuard
        // （軸の長短と重心から上下・前後を推測するヒューリスティック）は、両方を Meshy 生成物へ
        // 差し替えた 2026-07-29 に削除した。生成物の姿勢は既知なので推測は要らない。

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
