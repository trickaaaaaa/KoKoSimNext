#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using KokoSim.EditorTools;
using KokoSim.Unity.Match;
using UnityEditor;
using UnityEditor.Build;
using UnityEditor.Build.Reporting;
using UnityEngine;

namespace KokoSim.UnityEditorTools
{
    /// <summary>
    /// ブラウザ体験版（unityroom 向け）のビルド（Web 体験版 #1323・steam-roadmap 段階5）。
    ///
    /// 容量を削るために、**ビルドの間だけ**次を変えて、終わったら必ず元へ戻す（製品版＝Windows には効かせない）:
    ///   ・球場メッシュは <see cref="KeptStadiumModels"/> だけ残す（ほかは Resources の外へ退避＝ビルドに入らない。
    ///     試合では標準の球場を寸法へ伸縮して描く既存の代わりに落ちる＝DuelStageRig.LoadStadiumAsset）
    ///   ・場内放送の声（VOICEVOX:No.7）を外す。Windows 体験版（PlayerBuilder）と同じ表・同じ隠し方。
    ///     No.7 は商用の相談が済むまで体験版に入れない（issue #1318。体験版では MatchAnnouncer.Available=false で鳴らない）
    ///   ・曲の取り込みの品質とサンプルレートを下げる（Web には音の機種別設定が無いので既定値を一時的に書き換える。
    ///     効くのは品質で、サンプルレートだけ下げても変わらなかった。品質0.6/22.05kHz の音をオーナーが聞いて確認済み）
    ///   ・写真系の画像（イベント背景・立ち絵・タイトルの水彩）の WebGL 向け最大サイズを下げる（<see cref="TextureCaps"/>）。
    ///     Web の画面は 960×600 で、背景を 1600px・立ち絵（表示幅 208px）を 1024px のまま積む意味が無い
    ///   ・IL2CPP のコード生成を「サイズ優先」（wasm 13.7MB→11.1MB）
    /// 途中で落ちて戻し損ねても、次に回したときに最初に戻す（<see cref="RestoreLeftovers"/>）。
    ///
    /// 出力は2通り（どちらも Gzip。unityroom は Brotli を受け付けないため）:
    ///   ・手元で確かめる用: out/webdemo（Decompression Fallback あり＝素の http サーバでも開ける）
    ///   ・unityroom へ上げる用: out/unityroom（Decompression Fallback なし＝unityroom のアップロード画面の指定。
    ///     上げるのは中の Build フォルダ）
    /// 実行: メニュー「KokoSim/Build/ブラウザ体験版をビルド」「…（unityroom へ上げる用）」か
    ///   Unity -batchmode -quit -projectPath . -buildTarget WebGL -executeMethod KokoSim.UnityEditorTools.WebDemoBuild.Build
    ///   （unityroom 用は末尾を BuildForUnityroom に）
    /// </summary>
    public static class WebDemoBuild
    {
        /// <summary>残す球場モデル（それ以外の DuelStadiumMesh_*.asset は外す）。standard は Resources 名に接尾辞が無い。</summary>
        internal static readonly string[] KeptStadiumModels = { DuelStadiumMesh.StandardModel, "koshien" };

        /// <summary>音のフォルダ → ビルドの間だけ使う品質とサンプルレート（声は外すので載せない）。</summary>
        internal static readonly (string Dir, float Quality, uint Rate)[] AudioRates =
        {
            ("Assets/KokoSim/Audio/Vendor/Suno", 0.6f, 22050),       // 応援の曲（素材は 44.1kHz）
        };

        /// <summary>
        /// 画像のフォルダと検索語 → ビルドの間だけ使う WebGL 向けの最大サイズ（長辺）。形式・圧縮は元のまま。
        /// タイトルのロゴは文字の輪郭が甘くなるうえ gzip で 0.5MB しかないので触らない（水彩だけ）。
        /// </summary>
        internal static readonly (string Dir, string Filter, int MaxSize)[] TextureCaps =
        {
            ("Assets/UI/Images/event-bg", "t:Texture2D", 1024),                            // イベント会話の背景（画面いっぱい・元は 1600×893）
            ("Assets/UI/Images/title", "t:Texture2D title-koshien-watercolor", 1024),     // タイトルの水彩（画面いっぱい・元は 1376×768）
            ("Assets/UI/Images/portraits", "t:Texture2D", 512),                           // 立ち絵（表示は幅 208px・元は 768×1024 前後）
        };

        private const string StadiumDir = "Assets/KokoSim/Resources";
        /// <summary>退避先（Resources の外・gitignore）。中身が残っていれば前回の戻し損ね。</summary>
        internal const string ParkDir = "Assets/WebDemoParked";
        /// <summary>音の設定の戻し用メモ（前回の戻し損ねを戻すため、ファイルに残す）。</summary>
        private const string AudioMemo = "Temp/webdemo-audio-restore.txt";
        /// <summary>
        /// 画像の設定の戻し先。書き換える前の .meta をそのまま写しておき、戻すときはファイルごと書き戻す
        /// （設定を API で戻すと、Unity が既定の機種別の項目を書き足して .meta に差分が残るため）。
        /// </summary>
        private const string TextureMetaBackupDir = "Temp/webdemo-texture-meta";
        private const string WebGLPlatform = "WebGL";

        [MenuItem("KokoSim/Build/ブラウザ体験版をビルド")]
        public static void Build() => Build("webdemo", decompressionFallback: true);

        [MenuItem("KokoSim/Build/ブラウザ体験版をビルド（unityroom へ上げる用）")]
        public static void BuildForUnityroom() => Build("unityroom", decompressionFallback: false);

        private static void Build(string outName, bool decompressionFallback)
        {
            var root = Path.GetFullPath(Path.Combine(Application.dataPath, ".."));
            var outDir = Path.Combine(root, "out", outName);
            bool batch = Application.isBatchMode;
            BuildSummary summary = default;
            bool ok = false;

            RestoreLeftovers();
            DuelAssetCatalogBaker.Bake();   // 索引は gitignore＝端末ごとに古いことがある（装備・素体の欠けを防ぐ）

            var codeGen = PlayerSettings.GetIl2CppCodeGeneration(NamedBuildTarget.WebGL);
            bool fallbackBefore = PlayerSettings.WebGL.decompressionFallback;
            string[] hidden = Array.Empty<string>();
            try
            {
                hidden = PlayerBuilder.HideDirs(PlayerBuilder.DemoExcludedResourceDirs);
                ParkStadiums();
                LowerAudioRates();
                CapTextureSizes();
                PlayerSettings.SetIl2CppCodeGeneration(NamedBuildTarget.WebGL, Il2CppCodeGeneration.OptimizeSize);
                PlayerSettings.WebGL.compressionFormat = WebGLCompressionFormat.Gzip;
                PlayerSettings.WebGL.decompressionFallback = decompressionFallback;

                if (Directory.Exists(outDir)) Directory.Delete(outDir, true);
                var report = BuildPipeline.BuildPlayer(new BuildPlayerOptions
                {
                    scenes = EditorBuildSettings.scenes.Where(s => s.enabled).Select(s => s.path).ToArray(),
                    locationPathName = outDir,
                    target = BuildTarget.WebGL,
                    targetGroup = BuildTargetGroup.WebGL,
                    options = BuildOptions.None,
                    extraScriptingDefines = new[] { "KOKOSIM_DEMO" },
                });
                summary = report.summary;
                ok = summary.result == BuildResult.Succeeded;
            }
            finally
            {
                PlayerSettings.SetIl2CppCodeGeneration(NamedBuildTarget.WebGL, codeGen);
                PlayerSettings.WebGL.decompressionFallback = fallbackBefore;
                PlayerBuilder.RestoreDirs(hidden);
                RestoreLeftovers();
            }

            long files = ok ? Directory.GetFiles(Path.Combine(outDir, "Build")).Sum(f => new FileInfo(f).Length) : 0;
            Debug.Log($"[WebDemo] result={summary.result} errors={summary.totalErrors} "
                      + $"download={files / 1048576.0:F1}MB out={outDir}");
            if (batch) EditorApplication.Exit(ok ? 0 : 1);
        }

        // ---- 球場 ----------------------------------------------------------------

        /// <summary>外す球場の Resources 内のパス（残すもの以外の DuelStadiumMesh*.asset）。</summary>
        internal static List<string> StadiumsToPark()
        {
            var kept = new HashSet<string>(KeptStadiumModels.Select(m => DuelStadiumMesh.ResourceNameFor(m) + ".asset"));
            return AssetDatabase.FindAssets("t:DuelStadiumMesh", new[] { StadiumDir })
                .Select(AssetDatabase.GUIDToAssetPath)
                .Where(p => !kept.Contains(Path.GetFileName(p)))
                .ToList();
        }

        private static void ParkStadiums()
        {
            if (!AssetDatabase.IsValidFolder(ParkDir)) AssetDatabase.CreateFolder("Assets", ParkDir.Substring("Assets/".Length));
            foreach (var p in StadiumsToPark())
            {
                var err = AssetDatabase.MoveAsset(p, ParkDir + "/" + Path.GetFileName(p));
                if (!string.IsNullOrEmpty(err)) throw new InvalidOperationException("球場の退避に失敗: " + p + " " + err);
            }
        }

        // ---- 音 ------------------------------------------------------------------

        private static void LowerAudioRates()
        {
            var memo = new List<string>();
            try
            {
                foreach (var (dir, quality, rate) in AudioRates)
                {
                    foreach (var guid in AssetDatabase.FindAssets("t:AudioClip", new[] { dir }))
                    {
                        var path = AssetDatabase.GUIDToAssetPath(guid);
                        var imp = AssetImporter.GetAtPath(path) as AudioImporter;
                        if (imp == null) continue;
                        var s = imp.defaultSampleSettings;
                        memo.Add(string.Join("\t", path, (int)s.sampleRateSetting, s.sampleRateOverride,
                            s.quality.ToString("R", System.Globalization.CultureInfo.InvariantCulture)));
                        s.quality = quality;
                        s.sampleRateSetting = AudioSampleRateSetting.OverrideSampleRate;
                        s.sampleRateOverride = rate;
                        imp.defaultSampleSettings = s;
                        imp.SaveAndReimport();
                    }
                }
            }
            finally
            {
                // 途中で落ちても、書き換えた分は必ず戻せるようにメモを残す
                Directory.CreateDirectory(Path.GetDirectoryName(AudioMemo));
                File.WriteAllLines(AudioMemo, memo);
            }
        }

        // ---- 画像 ----------------------------------------------------------------

        internal static void CapTextureSizes()
        {
            Directory.CreateDirectory(TextureMetaBackupDir);
            foreach (var (dir, filter, maxSize) in TextureCaps)
            {
                foreach (var guid in AssetDatabase.FindAssets(filter, new[] { dir }))
                {
                    var path = AssetDatabase.GUIDToAssetPath(guid);
                    var imp = AssetImporter.GetAtPath(path) as TextureImporter;
                    if (imp == null) continue;
                    var after = imp.GetPlatformTextureSettings(WebGLPlatform);
                    if (!after.overridden)
                    {
                        // 上書きが無い画像は既定の設定で焼かれている＝既定を写して大きさだけ変える
                        var def = imp.GetDefaultPlatformTextureSettings();
                        after.format = TextureImporterFormat.Automatic;
                        after.textureCompression = def.textureCompression;
                        after.compressionQuality = def.compressionQuality;
                        after.crunchedCompression = def.crunchedCompression;
                        after.resizeAlgorithm = def.resizeAlgorithm;
                        after.maxTextureSize = def.maxTextureSize;
                        after.overridden = true;
                    }
                    if (after.maxTextureSize <= maxSize) continue;
                    // 書き換える前に .meta を写す（ファイル名は GUID・中身は元の .meta そのまま）
                    File.Copy(path + ".meta", Path.Combine(TextureMetaBackupDir, guid), overwrite: true);
                    after.maxTextureSize = maxSize;
                    imp.SetPlatformTextureSettings(after);
                    imp.SaveAndReimport();
                }
            }
        }

        // ---- 後始末 ----------------------------------------------------------------

        /// <summary>隠した声・退避した球場・書き換えた音と画像の設定を元へ戻す。何も残っていなければ何もしない。</summary>
        internal static void RestoreLeftovers()
        {
            var root = Path.GetFullPath(Path.Combine(Application.dataPath, ".."));
            var leftHidden = PlayerBuilder.DemoExcludedResourceDirs
                .Where(d => Directory.Exists(Path.Combine(root, d) + "~")).ToArray();
            if (leftHidden.Length > 0) PlayerBuilder.RestoreDirs(leftHidden);

            if (AssetDatabase.IsValidFolder(ParkDir))
            {
                foreach (var guid in AssetDatabase.FindAssets("", new[] { ParkDir }))
                {
                    var p = AssetDatabase.GUIDToAssetPath(guid);
                    var err = AssetDatabase.MoveAsset(p, StadiumDir + "/" + Path.GetFileName(p));
                    if (!string.IsNullOrEmpty(err)) Debug.LogError("[WebDemo] 球場を戻せない: " + p + " " + err);
                }
                AssetDatabase.DeleteAsset(ParkDir);
            }

            if (File.Exists(AudioMemo))
            {
                foreach (var line in File.ReadAllLines(AudioMemo))
                {
                    var f = line.Split('\t');
                    if (f.Length != 4) continue;
                    var imp = AssetImporter.GetAtPath(f[0]) as AudioImporter;
                    if (imp == null) continue;
                    var s = imp.defaultSampleSettings;
                    s.sampleRateSetting = (AudioSampleRateSetting)int.Parse(f[1]);
                    s.sampleRateOverride = uint.Parse(f[2]);
                    s.quality = float.Parse(f[3], System.Globalization.CultureInfo.InvariantCulture);
                    imp.defaultSampleSettings = s;
                    imp.SaveAndReimport();
                }
                File.Delete(AudioMemo);
            }

            if (Directory.Exists(TextureMetaBackupDir))
            {
                foreach (var backup in Directory.GetFiles(TextureMetaBackupDir))
                {
                    var path = AssetDatabase.GUIDToAssetPath(Path.GetFileName(backup));
                    if (string.IsNullOrEmpty(path)) { Debug.LogError("[WebDemo] 画像の設定を戻せない: " + backup); continue; }
                    File.Copy(backup, path + ".meta", overwrite: true);
                    AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceUpdate);
                }
                Directory.Delete(TextureMetaBackupDir, true);
            }
        }
    }
}
#endif
