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

        /// <summary>捕手マスク。原点=殻中心、幅0.26m。</summary>
        public static Mesh CatcherMask() => Get("AIGear_CatcherMask0_lo", m => CanonicalizeShell(m, 0.26f));

        /// <summary>球審マスク。原点=殻中心、幅0.26m。</summary>
        public static Mesh UmpMask() => Get("AIGear_UmpMask0_lo", m => CanonicalizeShell(m, 0.26f));

        /// <summary>胸プロテクター（bpy製）。原点=中心、縦0.44m・肩当てが上・パッド面+Z。</summary>
        public static Mesh Chest() => Get("AIGear_ChestProtectorP", CanonicalizeChest);

        /// <summary>レガース片脚（bpy製）。原点=中心、膝が上（+Y）・シェル開口が-Z（脛へ被せる）。</summary>
        public static Mesh LegGuard() => Get("AIGear_LegGuardP", CanonicalizeLegGuard);

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

        private static void CanonicalizeShell(Mesh mesh, float targetWidth)
        {
            // 被り殻（ヘルメット・マスク）: 中心を原点へ、横幅を頭寸へ。向きは資産ごとの個体差が
            // 大きいため、装着側の実測ヨー（視線方向）に任せてここでは触らない
            var b = mesh.bounds;
            float s = targetWidth / Mathf.Max(0.01f, b.size.x);
            Apply(mesh, Matrix4x4.Scale(Vector3.one * s) * Matrix4x4.Translate(-b.center));
        }

        private static void CanonicalizeChest(Mesh mesh)
        {
            // bpy出力はZ-up構築→FBX変換で軸が入れ替わる。実測で 最長軸→Y(縦)・最薄軸→Z(奥行) へ写像
            AxisMap(mesh);
            // 上下判定: 肩当てで上端の横幅が広い。下端の方が広ければ180°反転
            var b = mesh.bounds;
            float topW = BandWidthX(mesh, b.max.y - b.size.y * 0.15f, b.max.y);
            float botW = BandWidthX(mesh, b.min.y, b.min.y + b.size.y * 0.15f);
            if (botW > topW) Apply(mesh, Matrix4x4.Rotate(Quaternion.Euler(0f, 0f, 180f)));
            Recenter(mesh);
        }

        private static void CanonicalizeLegGuard(Mesh mesh)
        {
            AxisMap(mesh);
            // 上下判定: 膝皿（球）で上端の奥行きが厚い。下端の方が厚ければ反転
            var b = mesh.bounds;
            float topD = BandDepthZ(mesh, b.max.y - b.size.y * 0.20f, b.max.y);
            float botD = BandDepthZ(mesh, b.min.y, b.min.y + b.size.y * 0.20f);
            if (botD > topD) Apply(mesh, Matrix4x4.Rotate(Quaternion.Euler(180f, 0f, 0f)));
            // 前後判定: シェルの質量は前面（凸側）に寄る。重心が-Z側なら180°ヨー
            b = mesh.bounds;
            if (Centroid(mesh).z < b.center.z) Apply(mesh, Matrix4x4.Rotate(Quaternion.Euler(0f, 180f, 0f)));
            Recenter(mesh);
        }

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

        /// <summary>最長軸→Y・最薄軸→Z・残り→X の回転（行列式+1）を頂点へ焼く。</summary>
        private static void AxisMap(Mesh mesh)
        {
            var size = mesh.bounds.size;
            int longAxis = size.y >= size.x && size.y >= size.z ? 1 : (size.x >= size.z ? 0 : 2);
            int thinAxis = size.y <= size.x && size.y <= size.z ? 1 : (size.x <= size.z ? 0 : 2);
            if (thinAxis == longAxis) thinAxis = (longAxis + 1) % 3;
            int midAxis = 3 - longAxis - thinAxis;
            var m = Matrix4x4.zero;
            m[1, longAxis] = 1f;   // 最長軸 → Y
            m[2, thinAxis] = 1f;   // 最薄軸 → Z
            m[0, midAxis] = 1f;    // 残り → X
            m[3, 3] = 1f;
            if (Vector3.Dot(Vector3.Cross(m.GetRow(0), m.GetRow(1)), m.GetRow(2)) < 0f)
                m.SetRow(0, -m.GetRow(0));   // 鏡映にしない（法線が裏返る）
            Apply(mesh, m);
        }

        private static void Recenter(Mesh mesh) => Apply(mesh, Matrix4x4.Translate(-mesh.bounds.center));

        private static Vector3 Centroid(Mesh mesh)
        {
            var verts = mesh.vertices;
            var sum = Vector3.zero;
            for (int i = 0; i < verts.Length; i++) sum += verts[i];
            return verts.Length > 0 ? sum / verts.Length : Vector3.zero;
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

        private static float BandDepthZ(Mesh mesh, float yMin, float yMax)
        {
            var verts = mesh.vertices;
            float lo = float.MaxValue, hi = float.MinValue;
            for (int i = 0; i < verts.Length; i++)
                if (verts[i].y >= yMin && verts[i].y <= yMax) { lo = Mathf.Min(lo, verts[i].z); hi = Mathf.Max(hi, verts[i].z); }
            return hi > lo ? hi - lo : 0f;
        }
    }
}
