using System;
using System.IO;
using KokoSim.Engine.Season;
using UnityEngine;

namespace KokoSim.Unity.Shell
{
    /// <summary>
    /// 大会名の表（data/display-names.yaml・issue #1319）の Unity 側プロバイダ。
    /// 起動時（<see cref="AppBootstrap"/>）に1回だけ Resources の YAML（DataFiles） を読み、
    /// <see cref="DisplayNames.Use"/> で engine の表を差し替える。解釈は engine の <see cref="DisplayNamesParser"/>（テストと同一コード）。
    /// IO は Shell 層に隔離（不変条件#3）。
    ///
    /// <para>読めないときは実名の既定値のまま続けるが、<b>エラーを出す</b>（架空名に替えた後で表が読めないと、
    /// 黙って実名に戻ってしまうため。他の語彙の読み込みは警告どまりだが、ここは権利に関わる）。</para>
    /// </summary>
    public static class DisplayNamesProvider
    {
        private const string RelativePath = "KokoSim/display-names.yaml";

        public static void Load()
        {
            var path = DataFiles.Describe(RelativePath);
            try
            {
                if (!DataFiles.TryReadText(RelativePath, out var text))
                {
                    Debug.LogError($"[大会名] Resources に display-names.yaml が無い（{path}）。実名の既定値で続ける。tools/sync-engine-dll.sh を回すこと。");
                    DisplayNames.Use(null);
                    return;
                }
                DisplayNames.Use(DisplayNamesParser.Parse(text));
            }
            catch (Exception e)
            {
                Debug.LogError($"[大会名] display-names.yaml を読めない（{e.Message}）。実名の既定値で続ける。");
                DisplayNames.Use(null);
            }
        }
    }
}
