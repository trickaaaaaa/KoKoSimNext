using KokoSim.Engine.Nation;

namespace KokoSim.Config;

/// <summary>
/// data/school-names.yaml のローダ（設計書05 §2.1）。IOはこの層に隔離し、解釈はエンジンの純パーサ
/// <see cref="SchoolNameVocabParser"/> に委譲する（Balance と Unity で同一コード＝決定論保証）。
/// </summary>
public static class SchoolNamesLoader
{
    public static SchoolNameVocab LoadFromFile(string path) => Parse(File.ReadAllText(path));

    public static SchoolNameVocab Parse(string yaml) => SchoolNameVocabParser.Parse(yaml);
}
