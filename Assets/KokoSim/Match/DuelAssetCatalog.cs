using UnityEngine;

namespace KokoSim.Unity.Match
{
    /// <summary>
    /// 対決／守備ビューが使う素体・メッシュ・クリップを1個の資産にまとめた索引（打球処理の3D化 F8）。
    ///
    /// <b>なぜ要るか</b>: <see cref="DuelStageRig"/> のロードは全部 <c>AssetDatabase</c> ＝
    /// エディタ限定なので、Player ビルドでは <c>Available=false</c> になり3Dが一切出ない。
    ///
    /// <b>なぜこの形か</b>（他の選択肢を落とした理由）:
    ///   ・素体とクリップを丸ごと Resources へ … 未参照でも全部ビルドに入り、起動時のシリアライズも
    ///     重い（Unity 非推奨）
    ///   ・Addressables … パッケージ追加に加えて**ロードが非同期になる**＝<c>DuelStageRig</c> の
    ///     コンストラクタが同期でなくなり、呼び出し側を全部作り替えることになる
    /// この索引なら **Resources に置くのはこの1個だけ**で、参照した資産だけがビルドに入る。
    ///
    /// ⚠ **この .asset 自体は gitignore する**。購入素体・購入クリップの GUID 参照＝実質的な
    /// 再配布経路になり得るため（design-18 §3.1/§3.2.1 の「購入FBXを公開リポジトリに含めない」と同じ理由）。
    /// 手元に購入資産が無い環境では索引も無く、従来どおり <c>Available=false</c> に落ちる＝現状から後退しない。
    ///
    /// 中身は <c>DuelAssetCatalogBaker</c>（エディタ）が**コード側の名前表から自動で埋める**。
    /// 名前の単一ソースはコードのままで、この資産は焼き付けた索引でしかない。
    /// </summary>
    public sealed class DuelAssetCatalog : ScriptableObject
    {
        /// <summary>Resources からの読み出し名（＝ファイル名）。</summary>
        public const string ResourceName = "DuelAssetCatalog";

        [Tooltip("購入素体（選手用）。無ければ U2 素体へ落ちる")]
        public GameObject VendorBody;

        [Tooltip("U2生成素体（球審・購入素体が無い環境のフォールバック）")]
        public GameObject FallbackBody;

        [Tooltip("U2素体に被せる着衣メッシュ（DuelUniformBaker のベイク資産）")]
        public Mesh UniformMesh;

        [Tooltip("購入素体の領域IDつきメッシュ。Renderer名と並び順で対応する")]
        public string[] RegionNames = new string[0];
        public Mesh[] RegionMeshes = new Mesh[0];

        [Tooltip("使うクリップ。名前と並び順で対応する")]
        public string[] ClipNames = new string[0];
        public AnimationClip[] Clips = new AnimationClip[0];

        /// <summary>Renderer 名から領域IDつきメッシュを引く（無ければ null）。</summary>
        public Mesh FindRegionMesh(string rendererName)
        {
            if (RegionNames == null || RegionMeshes == null) return null;
            int n = Mathf.Min(RegionNames.Length, RegionMeshes.Length);
            for (int i = 0; i < n; i++)
                if (RegionNames[i] == rendererName) return RegionMeshes[i];
            return null;
        }

        /// <summary>クリップ名から引く（無ければ null）。</summary>
        public AnimationClip FindClip(string clipName)
        {
            if (ClipNames == null || Clips == null) return null;
            int n = Mathf.Min(ClipNames.Length, Clips.Length);
            for (int i = 0; i < n; i++)
                if (ClipNames[i] == clipName) return Clips[i];
            return null;
        }
    }
}
