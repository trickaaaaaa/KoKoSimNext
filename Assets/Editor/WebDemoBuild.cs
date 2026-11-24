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
    ///   ・IL2CPP のコード生成を「サイズ優先」（wasm 13.7MB→11.1MB）
    /// 途中で落ちて戻し損ねても、次に回したときに最初に戻す（<see cref="RestoreLeftovers"/>）。
    ///
    /// 出力: out/webdemo（Gzip。unityroom は Brotli を受け付けないため）。
    /// 実行: メニュー「KokoSim/Build/ブラウザ体験版をビルド」か
    ///   Unity -batchmode -quit -projectPath . -buildTarget WebGL -executeMethod KokoSim.UnityEditorTools.WebDemoBuild.Build
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

        private const string StadiumDir = "Assets/KokoSim/Resources";
        /// <summary>退避先（Resources の外・gitignore）。中身が残っていれば前回の戻し損ね。</summary>
        internal const string ParkDir = "Assets/WebDemoParked";
        /// <summary>音の設定の戻し用メモ（前回の戻し損ねを戻すため、ファイルに残す）。</summary>
        private const string AudioMemo = "Temp/webdemo-audio-restore.txt";

        [MenuItem("KokoSim/Build/ブラウザ体験版をビルド")]
        public static void Build()
        {
            var root = Path.GetFullPath(Path.Combine(Application.dataPath, ".."));
            var outDir = Path.Combine(root, "out", "webdemo");
            bool batch = Application.isBatchMode;
            BuildSummary summary = default;
            bool ok = false;

            RestoreLeftovers();
            DuelAssetCatalogBaker.Bake();   // 索引は gitignore＝端末ごとに古いことがある（装備・素体の欠けを防ぐ）

            var codeGen = PlayerSettings.GetIl2CppCodeGeneration(NamedBuildTarget.WebGL);
            string[] hidden = Array.Empty<string>();
            try
            {
                hidden = PlayerBuilder.HideDirs(PlayerBuilder.DemoExcludedResourceDirs);
                ParkStadiums();
                LowerAudioRates();
                PlayerSettings.SetIl2CppCodeGeneration(NamedBuildTarget.WebGL, Il2CppCodeGeneration.OptimizeSize);
                PlayerSettings.WebGL.compressionFormat = WebGLCompressionFormat.Gzip;
                PlayerSettings.WebGL.decompressionFallback = true;   // 素の http サーバでも開けるように

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

        // ---- 後始末 ----------------------------------------------------------------

        /// <summary>隠した声・退避した球場・書き換えた音の設定を元へ戻す。何も残っていなければ何もしない。</summary>
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
        }
    }
}
#endif
