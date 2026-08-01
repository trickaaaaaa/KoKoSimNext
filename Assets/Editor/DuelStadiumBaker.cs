using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using KokoSim.Engine.Match.Field;
using KokoSim.Unity.Match;
using UnityEditor;
using UnityEngine;

namespace KokoSim.UnityEditorTools
{
    /// <summary>
    /// <c>data/stadiums/&lt;name&gt;.obj</c> ＋ <c>.mtl</c> を <see cref="DuelStadiumMesh"/> へ焼く。
    /// 形の単一ソースは <c>tools/stadium-gen.py</c>（パラメトリック）で、ここはその出力を
    /// Unity の Mesh へ写すだけ。**寸法をここで足したり丸めたりしない。**
    ///
    /// OBJ は自前で読む（Unity のモデルインポータを通さない）。理由は <see cref="DuelStadiumMesh"/> の説明。
    /// 書式は生成側が出す形に限定してよい: <c>v</c> / <c>vn</c> / <c>o</c> / <c>usemtl</c> /
    /// <c>f a//na b//nb c//nc</c>（三角形・法線は面ごとに1つ）。
    ///
    /// ⚠ **焼いたら必ず「球場メッシュを検証する」を回す**。判定に使う寸法（フェンス弧・塁・投手板・
    /// マウンド）が engine の値とずれていても、**絵ではまず気づけない**（フェンスが1m手前でも
    /// 「そういう球場」に見える）。ずれたまま出すと「フェンス直撃なのに本塁打」が起きる。
    /// </summary>
    public static class DuelStadiumBaker
    {
        private const string SourceRelPath = "data/stadiums/standard.obj";
        private const string AssetPath = "Assets/KokoSim/Resources/DuelStadiumMesh.asset";

        /// <summary>フェンス弧を照合する方位[度]（+Z=センターから+X側が正）。生成側の報告と同じ9点。</summary>
        private static readonly double[] FenceCheckDeg = { 0, 11.25, 22.5, 33.75, 45, -11.25, -22.5, -33.75, -45 };

        /// <summary>照合の許容[m]。生成側は倍精度・OBJ は %g（6桁）なので丸めぶんだけ見る。</summary>
        private const double TolM = 0.01;

        [MenuItem("KokoSim/Duel/球場メッシュを焼く")]
        public static void Bake()
        {
            var src = FullPath(SourceRelPath);
            if (!File.Exists(src))
            {
                Debug.LogError($"球場OBJが無い: {src}\n先に `python3 tools/stadium-gen.py data/stadiums/standard` を回す");
                return;
            }

            var obj = Parse(src);
            var dir = Path.GetDirectoryName(AssetPath);
            if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
            if (AssetDatabase.LoadAssetAtPath<DuelStadiumMesh>(AssetPath) != null)
                AssetDatabase.DeleteAsset(AssetPath);   // サブメッシュの残骸を持ち越さない

            var asset = ScriptableObject.CreateInstance<DuelStadiumMesh>();
            asset.SourceObj = SourceRelPath;
            var parts = new List<DuelStadiumMesh.Part>();
            var tris = 0;
            foreach (var g in obj.Groups)
            {
                var part = BuildPart(obj, g);
                if (part == null) continue;
                parts.Add(part);
                tris += part.Mesh.triangles.Length / 3;
            }
            asset.Parts = parts.ToArray();
            asset.Triangles = tris;

            AssetDatabase.CreateAsset(asset, AssetPath);
            foreach (var p in asset.Parts) AssetDatabase.AddObjectToAsset(p.Mesh, asset);
            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();

            Debug.Log($"球場メッシュを焼いた: {asset.Parts.Length} 部位 / {tris} 三角形 → {AssetPath}");
            Verify();
        }

        [MenuItem("KokoSim/Duel/球場メッシュを検証する")]
        public static void Verify()
        {
            var asset = AssetDatabase.LoadAssetAtPath<DuelStadiumMesh>(AssetPath);
            if (asset == null) { Debug.LogError("球場メッシュがまだ焼かれていない"); return; }

            var sb = new StringBuilder();
            sb.AppendLine($"球場メッシュ検証 — {asset.Parts.Length} 部位 / {asset.Triangles} 三角形");
            var bad = 0;

            // ---- フェンス弧（本塁打判定の距離そのもの） ----
            var fence = Points(asset, "fence_outfield", y => y < 0.01f);
            foreach (var deg in FenceCheckDeg)
            {
                var want = FieldDiagramGeometry.FenceRadius(deg * Mathf.Deg2Rad, 95.0, 118.0);
                var got = RadiusAt(fence, deg);
                var d = System.Math.Abs(got - want);
                if (d > TolM) bad++;
                sb.AppendLine($"  フェンス {deg,7:F2}°  実測 {got,8:F3} m / engine {want,8:F3} m  差 {d:F4}"
                              + (d > TolM ? "  ← ずれている" : ""));
            }

            // ---- 塁・投手板（走者の到達点と投手の立ち位置） ----
            // 一塁・三塁は中心を見ない。engine の座標は**塁線と塁間線の交点＝塁の外側の辺**で、
            // 中心はそこから半辺ぶんフェア側に寄る（実物も90フィートは塁の外角まで）
            bad += CheckCornerBase(sb, asset, "base_first", +1.0, FieldDiagramGeometry.First);
            bad += CheckCornerBase(sb, asset, "base_third", -1.0, FieldDiagramGeometry.Third);
            bad += CheckSpot(sb, asset, "base_second", FieldDiagramGeometry.Second);
            var p = FieldGeometry.StandardPosition(FieldPosition.Pitcher);
            bad += CheckSpot(sb, asset, "pitcher_rubber", (p.X, p.Z));

            // ---- マウンドの断面（Unity 側の接地高 FieldGroundY と同じ形でなければならない） ----
            var mound = Points(asset, "mound", _ => true);
            if (mound.Count > 0)
            {
                float apex = 0f, rmax = 0f;
                foreach (var v in mound)
                {
                    if (v.y > apex) apex = v.y;
                    var r = Mathf.Sqrt(v.x * v.x + (v.z - 18.44f) * (v.z - 18.44f));
                    if (r > rmax) rmax = r;
                }
                var okA = Mathf.Abs(apex - 0.254f) <= TolM;
                var okR = Mathf.Abs(rmax - 2.74f) <= TolM;
                if (!okA || !okR) bad++;
                sb.AppendLine($"  マウンド 頂点高 {apex:F3} m（要 0.254）／落ち切り半径 {rmax:F3} m（要 2.74）"
                              + (okA && okR ? "" : "  ← ずれている"));
            }
            else { bad++; sb.AppendLine("  マウンドの部位が無い"); }

            if (bad == 0) Debug.Log(sb.ToString());
            else Debug.LogError(sb.ToString() + $"\n{bad} 件ずれている。tools/stadium-gen.py の寸法と engine の値を突き合わせること");
        }

        /// <summary>
        /// 一塁・三塁の置き方を見る。中心の一致ではなく、次の3つ:
        ///   ①塁の全部がフェア地域にある（塁線をまたがない） ②外側の辺が塁線に乗っている
        ///   ③塁線に沿った位置が engine の座標と一致する
        /// 中心で照合すると「塁線に半分はみ出した正しくない置き方」が合格してしまう。
        /// </summary>
        /// <param name="sx">一塁側なら +1、三塁側なら −1。塁線方向 u=(sx,1)/√2・フェア側法線 n=(−sx,1)/√2</param>
        private static int CheckCornerBase(StringBuilder sb, DuelStadiumMesh a, string group,
            double sx, (double X, double Y) want)
        {
            var pts = Points(a, group, _ => true);
            if (pts.Count == 0) { sb.AppendLine($"  {group} の部位が無い"); return 1; }

            var q = System.Math.Sqrt(2.0) / 2.0;
            double ux = sx * q, uz = q;        // 塁線方向
            double nx = -sx * q, nz = q;       // フェア側
            double minN = double.MaxValue, sumU = 0.0;
            foreach (var v in pts)
            {
                var dn = v.x * nx + v.z * nz;
                if (dn < minN) minN = dn;
                sumU += v.x * ux + v.z * uz;
            }
            var alongU = sumU / pts.Count;
            var wantU = want.X * ux + want.Y * uz;

            var fair = minN >= -TolM;                       // またいでいない
            var onLine = System.Math.Abs(minN) <= TolM;     // 外側の辺が線に乗っている
            var along = System.Math.Abs(alongU - wantU) <= TolM;
            var ok = fair && onLine && along;
            sb.AppendLine($"  {group,-16} 塁線からの最小 {minN,6:F3} m（要 0・負ならファウル側へはみ出し）"
                          + $"／線に沿った位置 {alongU,7:F3} m（engine {wantU:F3}）" + (ok ? "" : "  ← ずれている"));
            return ok ? 0 : 1;
        }

        private static int CheckSpot(StringBuilder sb, DuelStadiumMesh a, string group, (double X, double Y) want)
        {
            var pts = Points(a, group, _ => true);
            if (pts.Count == 0) { sb.AppendLine($"  {group} の部位が無い"); return 1; }
            double sx = 0, sz = 0;
            foreach (var v in pts) { sx += v.x; sz += v.z; }
            sx /= pts.Count; sz /= pts.Count;
            var d = System.Math.Sqrt((sx - want.X) * (sx - want.X) + (sz - want.Y) * (sz - want.Y));
            var ok = d <= TolM;
            sb.AppendLine($"  {group,-16} 実測 ({sx,7:F3}, {sz,7:F3}) / engine ({want.X,7:F3}, {want.Y,7:F3})  差 {d:F4}"
                          + (ok ? "" : "  ← ずれている"));
            return ok ? 0 : 1;
        }

        /// <summary>
        /// 方位[度]における、フェンスの**打球が当たる面**までの本塁からの距離。
        ///
        /// ⚠ 同じ方位に頂点が2つ以上ある（内側の面と、外へ 0.3m 出た天板の外縁）。
        /// 「方位がいちばん近い1点」を採ると**どちらを拾うかが頂点の並び順で決まり、0.3m 揺れる**
        /// （2026-08-01 実測: 9点中4点だけ +0.300 とずれて出た）。判定に効くのは内側なので、
        /// 方位が一致する頂点のうち**最小半径**を採る。
        /// </summary>
        private static double RadiusAt(List<Vector3> pts, double deg)
        {
            const double eps = 0.05;                       // 同一方位とみなす角度[度]
            var best = double.MaxValue;
            foreach (var v in pts)
                best = System.Math.Min(best, System.Math.Abs(Mathf.Atan2(v.x, v.z) * Mathf.Rad2Deg - deg));

            var r = double.MaxValue;
            foreach (var v in pts)
            {
                if (System.Math.Abs(Mathf.Atan2(v.x, v.z) * Mathf.Rad2Deg - deg) > best + eps) continue;
                r = System.Math.Min(r, System.Math.Sqrt(v.x * v.x + v.z * v.z));
            }
            return r;
        }

        private static List<Vector3> Points(DuelStadiumMesh a, string group, System.Func<float, bool> yFilter)
        {
            var outp = new List<Vector3>();
            foreach (var part in a.Parts)
            {
                if (part.Name != group || part.Mesh == null) continue;
                foreach (var v in part.Mesh.vertices) if (yFilter(v.y)) outp.Add(v);
            }
            return outp;
        }

        // ---- OBJ / MTL の読み取り -------------------------------------------------

        private sealed class ObjFile
        {
            public readonly List<Vector3> V = new List<Vector3>();
            public readonly List<Vector3> N = new List<Vector3>();
            public readonly List<Group> Groups = new List<Group>();
            public readonly Dictionary<string, Color> Mtl = new Dictionary<string, Color>();
        }

        private sealed class Group
        {
            public string Name;
            /// <summary>マテリアル名 → 面（v,n の添字を3組）。並び順を保つため List で持つ</summary>
            public readonly List<string> MatOrder = new List<string>();
            public readonly Dictionary<string, List<int>> Faces = new Dictionary<string, List<int>>();
        }

        private static ObjFile Parse(string objPath)
        {
            var o = new ObjFile();
            var mtlPath = Path.ChangeExtension(objPath, ".mtl");
            if (File.Exists(mtlPath))
            {
                string cur = null;
                foreach (var raw in File.ReadAllLines(mtlPath))
                {
                    var t = raw.Split(' ');
                    if (t[0] == "newmtl") cur = t[1];
                    else if (t[0] == "Kd" && cur != null)
                        o.Mtl[cur] = new Color(F(t[1]), F(t[2]), F(t[3]));
                }
            }

            Group g = null;
            string mat = null;
            foreach (var raw in File.ReadAllLines(objPath))
            {
                if (raw.Length == 0 || raw[0] == '#') continue;
                var t = raw.Split(' ');
                switch (t[0])
                {
                    case "v": o.V.Add(new Vector3(F(t[1]), F(t[2]), F(t[3]))); break;
                    case "vn": o.N.Add(new Vector3(F(t[1]), F(t[2]), F(t[3]))); break;
                    case "o":
                        g = new Group { Name = t[1] };
                        o.Groups.Add(g);
                        mat = null;
                        break;
                    case "usemtl":
                        mat = t[1];
                        if (g != null && !g.Faces.ContainsKey(mat))
                        {
                            g.Faces[mat] = new List<int>();
                            g.MatOrder.Add(mat);
                        }
                        break;
                    case "f":
                        if (g == null || mat == null) break;
                        var list = g.Faces[mat];
                        for (var k = 1; k <= 3; k++)
                        {
                            var s = t[k].Split('/');            // "v//n"
                            list.Add(int.Parse(s[0], CultureInfo.InvariantCulture));
                            list.Add(int.Parse(s[2], CultureInfo.InvariantCulture));
                        }
                        break;
                }
            }
            return o;
        }

        private static float F(string s) => float.Parse(s, CultureInfo.InvariantCulture);

        /// <summary>
        /// 1グループ＝1メッシュ。マテリアルごとにサブメッシュへ割る。
        /// 面ごとの法線をそのまま使う＝**フラットシェード**なので、頂点は (位置, 法線) の組で作る
        /// （<c>RecalculateNormals</c> は呼ばない。design-18 の掟）。
        /// </summary>
        private static DuelStadiumMesh.Part BuildPart(ObjFile o, Group g)
        {
            var verts = new List<Vector3>();
            var norms = new List<Vector3>();
            var index = new Dictionary<long, int>();
            var subs = new List<int[]>();
            var colors = new List<Color>();

            foreach (var m in g.MatOrder)
            {
                var flat = g.Faces[m];
                if (flat.Count == 0) continue;
                var idx = new int[flat.Count / 2];
                for (var i = 0; i < idx.Length; i++)
                {
                    var vi = flat[i * 2] - 1;
                    var ni = flat[i * 2 + 1] - 1;
                    var key = (long)vi * 1000003L + ni;
                    if (!index.TryGetValue(key, out var at))
                    {
                        at = verts.Count;
                        verts.Add(o.V[vi]);
                        norms.Add(o.N[ni]);
                        index[key] = at;
                    }
                    idx[i] = at;
                }
                subs.Add(idx);
                colors.Add(o.Mtl.TryGetValue(m, out var c) ? c : Color.magenta);
            }
            if (subs.Count == 0) return null;

            var mesh = new Mesh { name = g.Name, indexFormat = UnityEngine.Rendering.IndexFormat.UInt32 };
            mesh.SetVertices(verts);
            mesh.SetNormals(norms);
            mesh.subMeshCount = subs.Count;
            for (var i = 0; i < subs.Count; i++) mesh.SetTriangles(subs[i], i);
            mesh.RecalculateBounds();

            return new DuelStadiumMesh.Part { Name = g.Name, Mesh = mesh, Colors = colors.ToArray() };
        }

        private static string FullPath(string rel) =>
            Path.GetFullPath(Path.Combine(Application.dataPath, "..", rel));
    }
}
