using KokoSim.Unity.Shell;   // NationService

namespace KokoSim.Unity.NewGame
{
    /// <summary>
    /// 県選択画面（日本地図・issue #1410）→新規ゲーム設定画面へ渡す、選んだ本拠地。
    /// <see cref="DistrictChoice"/> は設定画面の県内地区欄の添字（北海道＝北北海道/南北海道・東京＝東東京/西東京は
    /// 地図の拡大図で選んだ区画。他の県は0）。確定は設定画面の <c>SaveGameIO.StartNewSchool</c> の1経路だけで、
    /// ここは「設定画面の初期値」を運ぶだけ。
    /// </summary>
    public static class NewGameSelection
    {
        public static int PrefectureId { get; private set; } = NationService.DefaultPrefectureId;
        public static int DistrictChoice { get; private set; }

        public static void Set(int prefectureId, int districtChoice)
        {
            PrefectureId = prefectureId;
            DistrictChoice = districtChoice;
        }
    }
}
