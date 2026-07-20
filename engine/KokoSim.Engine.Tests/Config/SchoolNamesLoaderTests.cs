using System.Text.RegularExpressions;
using KokoSim.Config;
using KokoSim.Engine.Core;
using KokoSim.Engine.Nation;
using Xunit;

namespace KokoSim.Engine.Tests.Config;

/// <summary>
/// data/school-names.yaml（県別地名の大規模版）→ 実生成の検証（school-name-vocab-plan.md §検証計画）。
/// ①47県ぶんの県別地名が読める ②各県内で地名が重複しない ③大規模県でも「第N」フォールバックが起きない容量。
/// </summary>
public sealed class SchoolNamesLoaderTests
{
    private static SchoolNameVocab LoadVocab()
        => SchoolNamesLoader.LoadFromFile(
            Engine.Tests.Balance.BalanceRegressionTests.FindDataFile("school-names.yaml"));

    // UniqueName の連番フォールバックは「第2」等のアラビア数字（public_suffixes の 第一/第二/第三＝漢数字とは別）。
    private static readonly Regex ArabicOrdinalFallback = new(@"第[0-9]");

    [Fact]
    public void RealFile_Provides47PrefecturesWithAmplePlaces()
    {
        var vocab = LoadVocab();
        Assert.Equal(0.30, vocab.PrivateRatio, 6);

        // 47県すべてに県別地名がある（フォールバックに落ちない）。
        for (var id = 0; id < 47; id++)
        {
            Assert.True(vocab.PlacesByPrefecture.ContainsKey(id),
                $"県Id {id} ({PrefectureNames.JisOrder[id]}) の地名が無い");
            var places = vocab.PlacesByPrefecture[id];
            // 容量目安 P≥30（設計計画D）。
            Assert.True(places.Count >= 30,
                $"{PrefectureNames.JisOrder[id]} の地名が {places.Count} 語（30未満）");
            // 県内で地名は一意（同じ母体の乱立を避ける）。
            Assert.Equal(places.Count, places.Distinct().Count());
        }
    }

    [Fact]
    public void RealFile_GeneratesNation_WithoutOrdinalFallback()
    {
        var vocab = LoadVocab();
        var coeff = new NationCoefficients();
        var nation = NationGenerator.Generate(vocab, coeff, new Xoshiro256Random(2026));

        // 全校名が「高校」で終わり、県内一意（既存不変条件の再確認）。
        foreach (var pref in nation.Prefectures)
        {
            var names = nation.InPrefecture(pref.Id).Select(s => s.Name).ToList();
            Assert.Equal(names.Count, names.Distinct().Count());
            Assert.All(names, n => Assert.EndsWith("高校", n));
        }

        // 容量充足の指標: 連番フォールバック（「〇〇第2高校」）が全国で発生しない。
        var fallbackNames = nation.Schools
            .Where(s => ArabicOrdinalFallback.IsMatch(s.Name))
            .Select(s => s.Name)
            .ToList();
        Assert.True(fallbackNames.Count == 0,
            $"連番フォールバックが {fallbackNames.Count} 件発生: {string.Join(", ", fallbackNames.Take(10))}");
    }

    [Fact]
    public void RealFile_LargePrefectures_HaveHighNameDiversity()
    {
        var vocab = LoadVocab();
        var nation = NationGenerator.Generate(vocab, new NationCoefficients(), new Xoshiro256Random(2026));

        // 東京(12)・神奈川(13)・大阪(26)・愛知(22)＝大規模県で distinct 率が高いこと（単調さの解消）。
        foreach (var prefId in new[] { 12, 13, 26, 22 })
        {
            var names = nation.InPrefecture(prefId).Select(s => s.Name).ToList();
            // 県内一意化後は distinct=総数だが、一意化前の素の多様性を base 名で測る。
            var baseNames = nation.InPrefecture(prefId)
                .Select(s => Regex.Replace(s.Name, "高校$", ""))
                .ToList();
            Assert.True(names.Count > 100, $"{PrefectureNames.JisOrder[prefId]} の校数 {names.Count}");
            // 一意化の追い込み（接尾連結）が全体の1割未満に収まる＝容量に余裕（素名の distinct 率 > 0.90）。
            var distinctRatio = (double)baseNames.Distinct().Count() / baseNames.Count;
            Assert.True(distinctRatio > 0.90,
                $"{PrefectureNames.JisOrder[prefId]} の素名 distinct 率 {distinctRatio:F3}");
        }
    }
}
