using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using UnityEditor;
using UnityEditor.Build;
using UnityEditor.Build.Reporting;
using UnityEngine;
using Debug = UnityEngine.Debug;

namespace KokoSim.EditorTools
{
    /// <summary>
    /// 製品ビルドを作る入口（issue #1310・docs/release/steam-roadmap.md 段階1）。
    ///
    /// 対象は Windows x64・Mono（Mac から作れる。IL2CPP の Windows 版は Windows 上でしか作れない）。
    /// 3種類を同じ手順で作り分ける。
    ///   製品版   … define なし
    ///   体験版   … KOKOSIM_DEMO を足す（区切りの実装は段階4。今は define を立てるだけ）
    ///   開発ビルド … Development（DEVELOPMENT_BUILD＝デバッグ HUD・デバッグ試合が入る。Profiler 接続可）
    ///
    /// 入口は2つ。
    ///   Editor メニュー: KokoSim/ビルド/…（Editor を開いたまま作るとき）
    ///   コマンドライン: tools/build-player.sh（Editor を閉じて batchmode で作るとき）
    ///     -executeMethod KokoSim.EditorTools.PlayerBuilder.BuildFromCommandLine -kokosimFlavor release|demo|dev [-kokosimOut &lt;dir&gt;]
    ///
    /// ProjectSettings は書き換えない（ビルドのたびに差分が出るのを避ける）。
    /// 体験版の define は BuildPlayerOptions.extraScriptingDefines でそのビルドにだけ足す。
    /// </summary>
    public static class PlayerBuilder
    {
        public enum Flavor { Release, Demo, Dev }

        public const string DemoDefine = "KOKOSIM_DEMO";
        private const BuildTarget Target = BuildTarget.StandaloneWindows64;

        // exe 名は英字に固定する（製品名は日本語の「白球監督録」）。exe 名は隣の <名前>_Data フォルダ名にもなり、
        // steamcmd の起動設定やログの場所にも出るので、日本語を通さない。ウィンドウの題名は製品名のまま。
        public const string ExeBaseName = "HakkyuKantokuroku";

        // 体験版のビルドから外すフォルダ（issue #1318）。場内放送（VOICEVOX:No.7）は商用の相談が済むまで体験版に入れない。
        // Resources 配下は参照の有無に関係なく全部ビルドに入るため、ビルドの間だけ名前の末尾に ~ を付けて
        // Unity から見えなくし、終わったら必ず戻す（.meta ごと動かすので GUID は変わらず、戻した後の再読み込みは
        // 取り込みキャッシュから復元される）。
        private static readonly string[] DemoExcludedResourceDirs =
        {
            "Assets/KokoSim/Audio/Vendor/VoiceVox/Resources",
        };

        // 出力は out/build/（out/ は .gitignore 済み）。
        private static string DefaultOutRoot =>
            Path.GetFullPath(Path.Combine(Application.dataPath, "..", "out", "build"));

        [MenuItem("KokoSim/ビルド/Windows 製品版")]
        private static void MenuRelease() => BuildFromMenu(Flavor.Release);

        [MenuItem("KokoSim/ビルド/Windows 体験版")]
        private static void MenuDemo() => BuildFromMenu(Flavor.Demo);

        [MenuItem("KokoSim/ビルド/Windows 開発ビルド")]
        private static void MenuDev() => BuildFromMenu(Flavor.Dev);

        private static void BuildFromMenu(Flavor flavor)
        {
            var ok = Build(flavor, null, out var message);
            EditorUtility.DisplayDialog(ok ? "ビルド完了" : "ビルド失敗", message, "OK");
            if (ok) EditorUtility.RevealInFinder(message.Split('\n')[0]);
        }

        /// <summary>batchmode 用。失敗したら終了コード 1 で Editor を落とす。</summary>
        public static void BuildFromCommandLine()
        {
            var args = Environment.GetCommandLineArgs();
            var flavorArg = ArgValue(args, "-kokosimFlavor") ?? "release";
            if (!TryParseFlavor(flavorArg, out var flavor))
            {
                Debug.LogError($"[PlayerBuilder] -kokosimFlavor が不正です: {flavorArg}（release / demo / dev）");
                EditorApplication.Exit(1);
                return;
            }
            var ok = Build(flavor, ArgValue(args, "-kokosimOut"), out var message);
            if (ok) Debug.Log("[PlayerBuilder] " + message);
            else Debug.LogError("[PlayerBuilder] " + message);
            EditorApplication.Exit(ok ? 0 : 1);
        }

        public static bool TryParseFlavor(string s, out Flavor flavor)
        {
            switch ((s ?? "").Trim().ToLowerInvariant())
            {
                case "release": flavor = Flavor.Release; return true;
                case "demo": flavor = Flavor.Demo; return true;
                case "dev": flavor = Flavor.Dev; return true;
                default: flavor = Flavor.Release; return false;
            }
        }

        /// <summary>
        /// ビルドする。成功時の message は1行目が出力フォルダ、以降が要約。
        /// </summary>
        public static bool Build(Flavor flavor, string outRoot, out string message)
        {
            var problems = Preflight();
            if (problems.Length > 0)
            {
                message = "ビルド前の確認で止めました:\n- " + string.Join("\n- ", problems);
                return false;
            }

            var outDir = Path.Combine(string.IsNullOrEmpty(outRoot) ? DefaultOutRoot : Path.GetFullPath(outRoot),
                                      FlavorDirName(flavor));
            if (Directory.Exists(outDir)) Directory.Delete(outDir, true);   // 前回の残りを混ぜない
            Directory.CreateDirectory(outDir);

            var exeName = ExeBaseName + ".exe";
            var options = new BuildPlayerOptions
            {
                scenes = EditorBuildSettings.scenes.Where(s => s.enabled).Select(s => s.path).ToArray(),
                locationPathName = Path.Combine(outDir, exeName),
                target = Target,
                targetGroup = BuildTargetGroup.Standalone,
                options = flavor == Flavor.Dev ? BuildOptions.Development : BuildOptions.None,
                extraScriptingDefines = flavor == Flavor.Demo ? new[] { DemoDefine } : Array.Empty<string>(),
            };

            var sw = Stopwatch.StartNew();
            BuildReport report;
            var hidden = flavor == Flavor.Demo ? HideDirs(DemoExcludedResourceDirs) : Array.Empty<string>();
            try
            {
                report = BuildPipeline.BuildPlayer(options);
            }
            finally
            {
                RestoreDirs(hidden);
            }
            sw.Stop();
            var summary = report.summary;

            if (summary.result != BuildResult.Succeeded)
            {
                message = $"{FlavorLabel(flavor)}のビルドに失敗しました（{summary.result}・エラー {summary.totalErrors} 件）。Console を見てください。";
                return false;
            }

            var warnings = IdentityWarnings();
            WriteBuildInfo(outDir, flavor, summary, sw.Elapsed, warnings);

            var sb = new StringBuilder();
            sb.AppendLine(outDir);
            sb.AppendLine($"{FlavorLabel(flavor)} v{PlayerSettings.bundleVersion}（{GitCommit() ?? "commit 不明"}）");
            sb.AppendLine($"容量 {FolderSizeMb(outDir):F0} MB・所要 {sw.Elapsed.TotalSeconds:F0} 秒");
            foreach (var w in warnings) sb.AppendLine("注意: " + w);
            message = sb.ToString().TrimEnd();
            return true;
        }

        /// <summary>フォルダ（と隣の .meta）の末尾に ~ を付けて Unity から隠す。隠したものの元の名前を返す。</summary>
        private static string[] HideDirs(string[] dirs)
        {
            var root = Path.GetFullPath(Path.Combine(Application.dataPath, ".."));
            var hidden = new System.Collections.Generic.List<string>();
            try
            {
                foreach (var d in dirs)
                {
                    var full = Path.Combine(root, d);
                    if (!Directory.Exists(full)) continue;   // 素材が無い環境（Git 管理外）では何もしない
                    Directory.Move(full, full + "~");
                    if (File.Exists(full + ".meta")) File.Move(full + ".meta", full + "~.meta");
                    hidden.Add(d);
                }
            }
            catch
            {
                RestoreDirs(hidden.ToArray());
                throw;
            }
            if (hidden.Count > 0) AssetDatabase.Refresh(ImportAssetOptions.ForceSynchronousImport);
            return hidden.ToArray();
        }

        private static void RestoreDirs(string[] dirs)
        {
            if (dirs.Length == 0) return;
            var root = Path.GetFullPath(Path.Combine(Application.dataPath, ".."));
            foreach (var d in dirs)
            {
                var full = Path.Combine(root, d);
                try
                {
                    if (Directory.Exists(full + "~") && !Directory.Exists(full)) Directory.Move(full + "~", full);
                    if (File.Exists(full + "~.meta") && !File.Exists(full + ".meta")) File.Move(full + "~.meta", full + ".meta");
                }
                catch (Exception e)
                {
                    Debug.LogError($"[PlayerBuilder] {d} を元に戻せませんでした。手で末尾の ~ を外してください: {e.Message}");
                }
            }
            AssetDatabase.Refresh(ImportAssetOptions.ForceSynchronousImport);
        }

        /// <summary>ビルドに入る前に止めるべき問題。空なら進めてよい。</summary>
        private static string[] Preflight()
        {
            var list = new System.Collections.Generic.List<string>();

            if (!BuildPipeline.IsBuildTargetSupported(BuildTargetGroup.Standalone, Target))
                list.Add("Windows Build Support (Mono) が入っていません。Unity Hub → インストール → この版の「モジュールを加える」で入れてください。");

            // engine DLL と Resources/KokoSimData の YAML は Git 管理外の生成物（issue #785）。無いと起動直後に落ちる。
            var root = Path.GetFullPath(Path.Combine(Application.dataPath, ".."));
            if (!File.Exists(Path.Combine(root, "Assets/Plugins/KokoSim/KokoSim.Engine.dll")))
                list.Add("engine DLL がありません。bash tools/sync-engine-dll.sh を回してください。");
            if (!File.Exists(Path.Combine(root, "Assets/Resources/KokoSimData/school-names.yaml")))
                list.Add("Resources/KokoSimData の YAML がありません。bash tools/sync-engine-dll.sh を回してください。");

            if (!EditorBuildSettings.scenes.Any(s => s.enabled))
                list.Add("Build Settings に有効なシーンがありません。");

            var backend = PlayerSettings.GetScriptingBackend(NamedBuildTarget.Standalone);
            if (backend != ScriptingImplementation.Mono2x)
                list.Add($"Standalone のスクリプトバックエンドが {backend} です。Mac から Windows 版を作るには Mono が要ります。");

            return list.ToArray();
        }

        /// <summary>
        /// 社名・製品名が仮のままなら注意を出す。セーブの置き場（persistentDataPath）はこの2つで決まるので、
        /// 最初に人へ配るビルドより前に確定し、以後は変えない（steam-roadmap 段階1・タイトルは #1308 で決める）。
        /// 確定前でも計測・動作確認用には作れるよう、止めはしない。
        /// </summary>
        private static string[] IdentityWarnings()
        {
            var list = new System.Collections.Generic.List<string>();
            if (PlayerSettings.companyName == "DefaultCompany")
                list.Add("社名が DefaultCompany のままです（人に配る前に確定する。#1308）。");
            if (PlayerSettings.productName == "KoKoSimNext")
                list.Add("製品名が仮の KoKoSimNext のままです（#1308 で「白球監督録」に確定済み。main を取り込んでください）。");
            return list.ToArray();
        }

        private static void WriteBuildInfo(string outDir, Flavor flavor, BuildSummary summary, TimeSpan elapsed, string[] warnings)
        {
            var sb = new StringBuilder();
            sb.AppendLine($"flavor: {flavor.ToString().ToLowerInvariant()}");
            sb.AppendLine($"version: {PlayerSettings.bundleVersion}");
            sb.AppendLine($"commit: {GitCommit() ?? "unknown"}");
            sb.AppendLine($"built_at: {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
            sb.AppendLine($"unity: {Application.unityVersion}");
            sb.AppendLine($"target: {summary.platform}");
            sb.AppendLine($"company: {PlayerSettings.companyName}");
            sb.AppendLine($"product: {PlayerSettings.productName}");
            sb.AppendLine($"size_mb: {FolderSizeMb(outDir):F1}");
            sb.AppendLine($"build_seconds: {elapsed.TotalSeconds:F0}");
            foreach (var w in warnings) sb.AppendLine($"warning: {w}");
            File.WriteAllText(Path.Combine(outDir, "build-info.txt"), sb.ToString(), new UTF8Encoding(false));
        }

        private static string FlavorDirName(Flavor f) => "windows-" + f.ToString().ToLowerInvariant();

        private static string FlavorLabel(Flavor f) =>
            f == Flavor.Release ? "製品版" : f == Flavor.Demo ? "体験版" : "開発ビルド";

        private static string ArgValue(string[] args, string name)
        {
            var i = Array.IndexOf(args, name);
            return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
        }

        private static double FolderSizeMb(string dir) =>
            new DirectoryInfo(dir).EnumerateFiles("*", SearchOption.AllDirectories).Sum(f => f.Length) / (1024.0 * 1024.0);

        private static string GitCommit()
        {
            try
            {
                var psi = new ProcessStartInfo("git", "rev-parse --short HEAD")
                {
                    WorkingDirectory = Path.GetFullPath(Path.Combine(Application.dataPath, "..")),
                    RedirectStandardOutput = true,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                };
                using (var p = Process.Start(psi))
                {
                    var s = p.StandardOutput.ReadToEnd().Trim();
                    p.WaitForExit(3000);
                    return p.ExitCode == 0 && s.Length > 0 ? s : null;
                }
            }
            catch { return null; }
        }
    }
}
