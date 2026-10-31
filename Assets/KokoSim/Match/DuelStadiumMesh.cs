using UnityEngine;

namespace KokoSim.Unity.Match
{
    /// <summary>
    /// 球場の形（1球場ぶん）を焼いた資産。<c>data/stadiums/&lt;name&gt;.obj</c> ＋ <c>.mtl</c> を
    /// <c>DuelStadiumBaker</c>（エディタ）が読んで作る。
    ///
    /// <b>なぜ OBJ をそのまま Assets へ置かないか</b>: Assets 内の .obj は Unity のモデルインポータが
    /// 勝手に取り込み、マテリアルまで作る。この球場の面は**自前の平面ランバート**
    /// （<c>KokoSim/DuelStageProxyLambert</c>）1本で塗るので、インポータが作る URP Lit の材質は要らない。
    /// data/ に置いて自前で読めば、形の単一ソースが <c>tools/stadium-gen.py</c> のままになる。
    ///
    /// <b>なぜ Resources に置いてよいか</b>: 中身は全部**生成物**で、購入資産の GUID を1つも参照しない
    /// （<see cref="DuelAssetCatalog"/> を gitignore している理由がここには無い）。1球場ぶんで
    /// 約39,000 三角形＋観客（満員12,898人・約206,000 三角形。issue #705）＝ビルドに丸ごと入っても軽い。
    ///
    /// 部位を <c>o</c> グループ単位で分けてあるのは、後から一部だけ隠す／差し替えるため
    /// （例: 対決ビューでネット裏スタンドだけ落とす）。材質はグループの中のサブメッシュに割る。
    /// </summary>
    public sealed class DuelStadiumMesh : ScriptableObject
    {
        /// <summary>Resources からの読み出し名（＝ファイル名）。</summary>
        public const string ResourceName = "DuelStadiumMesh";

        [System.Serializable]
        public sealed class Part
        {
            [Tooltip("OBJ の o グループ名（turf_fair / fence_outfield / stand_backstop …）")]
            public string Name;

            [Tooltip("サブメッシュはマテリアルごと。Colors と同数・同順")]
            public Mesh Mesh;

            [Tooltip("サブメッシュごとの色。MTL の Kd をそのまま入れる（sRGB）")]
            public Color[] Colors = new Color[0];
        }

        [Tooltip("焼き元の相対パス（data/stadiums/standard.obj）")]
        public string SourceObj;

        public Part[] Parts = new Part[0];

        [Tooltip("三角形の総数。焼き直したときに増減が分かるように残す")]
        public int Triangles;

        [Tooltip("観客の人数（満員＝全層の合計。issue #705）")]
        public int CrowdPeople;

        [Tooltip("観客の三角形数（Triangles の内数）")]
        public int CrowdTriangles;
    }
}
