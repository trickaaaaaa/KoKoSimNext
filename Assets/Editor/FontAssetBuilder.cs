using System.Collections.Generic;
using System.IO;
using System.Text;
using UnityEditor;
using UnityEngine;
using UnityEngine.TextCore.LowLevel;
using UnityEngine.TextCore.Text;

namespace KokoSim.EditorTools
{
    /// <summary>
    /// UI Toolkit 用 SDF フォントアセット（<see cref="FontAsset"/>）の生成ツール（設計書16 F0）。
    ///
    /// UI Toolkit の -unity-font-definition が取るのは TextMeshPro の TMP_FontAsset ではなく
    /// UnityEngine.TextCore.Text.FontAsset。既存 KokoSimFont と同条件（90pt / padding 9 / SDFAA /
    /// 4096角 / Static）で作り、書体を差し替えても文字の鮮鋭度が変わらないようにする。
    ///
    /// Atlas は必ず Static で保存する。Dynamic のままだと Editor 終了時にアトラスが自動クリアされ、
    /// .asset が空のまま git に残る事故が起きる（2026-07-21 に KokoSimFont で発生・対処済み）。
    /// </summary>
    public static class FontAssetBuilder
    {
        // サンプリング解像度は「実際に描く最大サイズ」を基準に決める。SDF は輪郭を距離場で持つので
        // 描画サイズの2倍程度あれば十分で、過剰に上げるとアトラス面積＝アセット容量だけが膨らむ。
        // 既存 KokoSimFont は 90pt だったが、本文は最大16px・見出しでも32pxしか描かないため過剰だった
        // （1615字を90ptで焼くと1書体66MB・6書体で396MBになった）。
        private const int SamplingPointSize = 64;   // 本文・見出し（最大32px描画）
        private const int AtlasPadding = 6;         // 概ねサンプリングの10%
        // 掲示板（DotGothic16）も 64pt に揃える。最大描画は48pxで 0.75倍の縮小に収まり、
        // 90pt にするとアトラスが2枚になって1書体だけ64MBに膨らむ割に見た目が変わらない。
        private const int DotSamplingPointSize = 64;
        private const int DotAtlasPadding = 6;

        // アトラスは「必要なグリフが入る最小の面」にする。90pt＋padding 9 のセルは約108px角なので、
        // 4096角だと1400字ぶんの面を確保してしまい 1書体 34MB になる（検証用15書体で500MB超）。
        // 和文（約400字）は 2048×4096、欧文（ASCIIのみ約95字）は 1024×2048 で足りる。
        private const int JpAtlasW = 2048, JpAtlasH = 4096;
        private const int LatinAtlasW = 1024, LatinAtlasH = 2048;

        // 本番用。約1600字を 90pt のまま収めるため 4096角＋複数アトラス許可にする
        // （サンプリングを落として1枚に詰めると鮮鋭度が変わってしまう）。
        private const int ProdJpAtlas = 4096;

        private const string FontDir = "Assets/UI/Fonts";
        private const string OutDir = "Assets/UI/Fonts/Generated";

        /// <summary>
        /// 書体検証ボード用に、候補フォントすべての SDF アセットを作る（設計書16 F0）。
        /// 採否が決まったら不採用ぶんを消し、採用ぶんだけを本番の文字セットで作り直す。
        /// </summary>
        [MenuItem("KokoSim/Fonts/検証ボード用の SDF を生成")]
        public static void BuildVerificationSet()
        {
            var sources = new[]
            {
                // 第一候補
                "ShipporiMinchoB1-Bold", "ShipporiMinchoB1-ExtraBold",
                "IBMPlexSansJP-Regular", "IBMPlexSansJP-Medium", "IBMPlexSansJP-SemiBold",
                "Oswald-Medium", "Oswald-SemiBold",
                "DotGothic16-Regular",
                // 代替
                "ZenOldMincho-Bold",
                "Murecho-Regular", "Murecho-Medium", "Murecho-SemiBold",
                "ZenKakuGothicNew-Regular",
                "BarlowSemiCondensed-Medium", "BarlowSemiCondensed-SemiBold",
            };

            if (!AssetDatabase.IsValidFolder(OutDir)) AssetDatabase.CreateFolder(FontDir, "Generated");

            var log = new StringBuilder();
            var chars = VerificationCharacterSet();
            log.AppendLine("文字セット " + chars.Length + " 字で生成する");

            for (var i = 0; i < sources.Length; i++)
            {
                EditorUtility.DisplayProgressBar("SDF生成", sources[i], (float)i / sources.Length);
                log.AppendLine(Build(sources[i], chars));
            }
            EditorUtility.ClearProgressBar();

            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();
            Debug.Log("[FontAssetBuilder]\n" + log);
        }

        /// <summary>
        /// 本番用。F0 で確定した8書体だけを、data/ の語彙を含む全文字セットで焼き直す（設計書16 F0ゲート後）。
        /// 語彙（校名・選手名）を増やしたら必ずこれを回す＝豆腐の再発防止。
        /// </summary>
        [MenuItem("KokoSim/Fonts/本番用の SDF を生成（採用書体・全語彙）")]
        public static void BuildProductionSet()
        {
            var sources = new[]
            {
                "ShipporiMinchoB1-Bold", "ShipporiMinchoB1-ExtraBold",
                "IBMPlexSansJP-Regular", "IBMPlexSansJP-Medium", "IBMPlexSansJP-SemiBold",
                "Oswald-Medium", "Oswald-SemiBold",
                "DotGothic16-Regular",
            };

            if (!AssetDatabase.IsValidFolder(OutDir)) AssetDatabase.CreateFolder(FontDir, "Generated");

            var chars = ProductionCharacterSet();
            var log = new StringBuilder();
            log.AppendLine("文字セット " + chars.Length + " 字（data/ の語彙を含む）");

            for (var i = 0; i < sources.Length; i++)
            {
                EditorUtility.DisplayProgressBar("SDF生成（本番）", sources[i], (float)i / sources.Length);
                log.AppendLine(Build(sources[i], chars, production: true));
            }
            EditorUtility.ClearProgressBar();

            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();
            Debug.Log("[FontAssetBuilder]\n" + log);
        }

        /// <summary>欧文専用フォント（和文グリフを持たない＝ASCIIだけ焼けばよい）。</summary>
        public static bool IsLatinOnly(string fontFileName)
            => fontFileName.StartsWith("Oswald") || fontFileName.StartsWith("BarlowSemiCondensed");

        /// <summary>1書体ぶんを生成して保存する。戻り値はログ1行。</summary>
        public static string Build(string fontFileName, string characters, bool production = false)
        {
            var ttf = AssetDatabase.LoadAssetAtPath<Font>(FontDir + "/" + fontFileName + ".ttf");
            if (ttf == null) return "  失敗 " + fontFileName + ": TTF が見つからない";

            var latin = IsLatinOnly(fontFileName);
            if (latin) characters = AsciiSet();

            var dot = fontFileName.StartsWith("DotGothic");
            var pt = dot ? DotSamplingPointSize : SamplingPointSize;
            var pad = dot ? DotAtlasPadding : AtlasPadding;
            var w = latin ? LatinAtlasW : (production ? ProdJpAtlas : JpAtlasW);
            var h = latin ? LatinAtlasH : (production ? ProdJpAtlas : JpAtlasH);
            var asset = FontAsset.CreateFontAsset(
                ttf, pt, pad, GlyphRenderMode.SDFAA,
                w, h, AtlasPopulationMode.Dynamic, enableMultiAtlasSupport: production && !latin);
            if (asset == null) return "  失敗 " + fontFileName + ": CreateFontAsset が null";

            asset.name = fontFileName;
            string missing;
            asset.TryAddCharacters(characters, out missing);

            // 追加が終わってから Static へ固定する（Dynamic のままだと終了時にクリアされる）。
            asset.atlasPopulationMode = AtlasPopulationMode.Static;

            var path = OutDir + "/" + fontFileName + ".asset";
            AssetDatabase.DeleteAsset(path);
            AssetDatabase.CreateAsset(asset, path);

            // アトラステクスチャとマテリアルはサブアセットとして同じ .asset に格納する
            // （別ファイルにすると参照が切れて豆腐になる）。
            if (asset.atlasTextures != null)
            {
                for (var i = 0; i < asset.atlasTextures.Length; i++)
                {
                    if (asset.atlasTextures[i] == null) continue;
                    asset.atlasTextures[i].name = fontFileName + " Atlas" + (i == 0 ? "" : " " + i);
                    AssetDatabase.AddObjectToAsset(asset.atlasTextures[i], asset);
                }
            }
            if (asset.material != null)
            {
                asset.material.name = fontFileName + " Material";
                AssetDatabase.AddObjectToAsset(asset.material, asset);
            }
            EditorUtility.SetDirty(asset);

            var missingCount = string.IsNullOrEmpty(missing) ? 0 : missing.Length;
            return "  OK " + fontFileName + ": " + asset.characterTable.Count + " 字 / アトラス "
                   + (asset.atlasTextures == null ? 0 : asset.atlasTextures.Length) + " 枚"
                   + (missingCount > 0 ? "（未収録 " + missingCount + " 字）" : "");
        }

        /// <summary>
        /// 検証ボードに出す文字だけを焼く。本番の全文字ベイクは書体確定後（設計書16 F4）に行う。
        /// 欧文フォント（Oswald / Barlow）は和文グリフを持たないので未収録が出るが、それが正しい
        /// （和文は本文フォントへフォールバックさせる設計）。
        /// </summary>
        /// <summary>
        /// 本番用の文字セット。ASCII＋かな全域＋記号に加え、<c>data/</c> の YAML から
        /// 実際に使う漢字（校名語彙・選手名・県名・イベント文・特殊能力名・球場名）を全部拾う。
        ///
        /// ここを固定文字列にしないのは、語彙を増やしたときに豆腐（□）が出るのを防ぐため。
        /// 現行 KokoSimFont は「動的生成された分だけ Static 固定」された結果、data/ が要求する
        /// 1206字のうち758字が欠けていた（2026-07-21 に判明）。同じ事故を繰り返さない。
        /// </summary>
        public static string ProductionCharacterSet()
        {
            var set = new HashSet<char>();
            void Add(string s) { if (s != null) foreach (var c in s) set.Add(c); }

            for (var c = ' '; c <= '~'; c++) set.Add(c);           // ASCII 印字可能
            for (var c = '぀'; c <= 'ヿ'; c++) set.Add(c); // ひらがな＋カタカナ全域
            Add("　、。・ー「」『』（）〔〕％￥±—…‐×÷≦≧←→↑↓△▲▽▼○●◯□■☆★§¶†");
            Add("０１２３４５６７８９");                            // 全角数字（YAML 由来の表記ゆれ対策）

            // data/ の YAML から漢字と かな を総取りする（プロジェクト直下の data/）。
            var dataDir = Path.GetFullPath(Path.Combine(Application.dataPath, "..", "data"));
            if (Directory.Exists(dataDir))
            {
                foreach (var path in Directory.GetFiles(dataDir, "*.yaml", SearchOption.AllDirectories))
                {
                    foreach (var c in File.ReadAllText(path))
                    {
                        // CJK統合漢字＋かな＋CJK記号だけ拾う（ASCII は上で入れている）。
                        if ((c >= '一' && c <= '鿿') || (c >= '぀' && c <= 'ヿ')
                            || (c >= '　' && c <= '〿') || (c >= '＀' && c <= '￯'))
                            set.Add(c);
                    }
                }
            }
            else
            {
                Debug.LogWarning("[FontAssetBuilder] data/ が見つからない: " + dataDir + "（校名・選手名の漢字が焼けない）");
            }

            // UI 側の固定文言（画面見出し・ラベル・状態語）。data/ には出てこないので明示する。
            Add(VerificationCharacterSet());

            var sb = new StringBuilder(set.Count);
            foreach (var c in set) sb.Append(c);
            return sb.ToString();
        }

        public static string AsciiSet()
        {
            var sb = new StringBuilder();
            for (var c = ' '; c <= '~'; c++) sb.Append(c);
            return sb.ToString();
        }

        public static string VerificationCharacterSet()
        {
            var set = new HashSet<char>();
            void Add(string s) { foreach (var c in s) set.Add(c); }

            for (var c = ' '; c <= '~'; c++) set.Add(c);          // ASCII 印字可能
            for (var c = 'ぁ'; c <= 'ん'; c++) set.Add(c);         // ひらがな
            for (var c = 'ァ'; c <= 'ヶ'; c++) set.Add(c);         // カタカナ
            Add("、。・ー「」（）　％￥±—…‐");

            // 画面に実際に出ている語（校名・大会名・見出し・ラベル・状態）。
            Add("県立桜丘高校北都大付属享栄神奈川");
            Add("年月週日目残選手権秋季大会甲子園回戦優勝敗退開幕");
            Add("部実力成績内順位個別指導故障者今出来事次試合通知");
            Add("打率本塁安点防御奪三振投球盗勝利通算公式総合撃守備機動層精神");
            Add("軽度中重肩肘腰膝足首復帰名該当無");
            Add("練習計画配分育成能値上限春夏冬合宿");
            Add("部費残高声信頼進検証見本組");

            var sb = new StringBuilder(set.Count);
            foreach (var c in set) sb.Append(c);
            return sb.ToString();
        }
    }
}
