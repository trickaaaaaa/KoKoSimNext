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
        /// <summary>Resources からの読み出し名（＝ファイル名）。汎用の standard はこの名前のまま。</summary>
        public const string ResourceName = "DuelStadiumMesh";

        /// <summary>汎用（伸縮して全球場に使う）モデルの名前。</summary>
        public const string StandardModel = "standard";

        /// <summary>
        /// モデル名 → Resources の読み出し名（issue #904）。standard は従来どおり <see cref="ResourceName"/>、
        /// それ以外は <c>DuelStadiumMesh_&lt;model&gt;</c>（例: 神宮＝DuelStadiumMesh_jingu）。
        /// </summary>
        public static string ResourceNameFor(string model)
            => string.IsNullOrEmpty(model) || model == StandardModel ? ResourceName : ResourceName + "_" + model;

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

        [Tooltip("焼き元の相対パス（data/stadiums/<model>.obj）")]
        public string SourceObj;

        [Tooltip("モデル名（data/stadiums.yaml の model。standard＝汎用）")]
        public string Model = StandardModel;

        // 焼いたメッシュの寸法（issue #904）。試合の球場寸法へ外野を変形するときの基準。
        // 両翼・中堅は判定と同じ値で作ってある（「球場メッシュを検証する」が照合する）。
        // フェンス高はモデルのフェンス本体の高さ（判定の高さとは違ってよい＝変形で合わせる）。
        [Tooltip("焼いたフェンスの両翼[m]")]
        public float BakedWingM = 95f;
        [Tooltip("焼いたフェンスの中堅[m]")]
        public float BakedCenterM = 118f;
        [Tooltip("焼いたフェンス本体の高さ[m]")]
        public float BakedFenceHeightM = 3.2f;

        public Part[] Parts = new Part[0];

        [Tooltip("三角形の総数。焼き直したときに増減が分かるように残す")]
        public int Triangles;

        [Tooltip("観客の人数（満員＝全層の合計。issue #705）")]
        public int CrowdPeople;

        [Tooltip("観客の三角形数（Triangles の内数）")]
        public int CrowdTriangles;
    }
}
