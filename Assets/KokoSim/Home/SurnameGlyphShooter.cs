#if UNITY_EDITOR
using System.Collections;
using System.IO;
using KokoSim.Unity.Shell;
using UnityEngine;
using UnityEngine.UIElements;

namespace KokoSim.Unity.Home
{
    /// <summary>
    /// 【開発用・エディタ限定・使い捨て】issue #819 の確認撮影。苗字の語彙を増やしたときに、新しく入った字が
    /// 豆腐（□）にならず、3文字姓が名前セルで見切れないかを確かめる。部員の名前を、フォントを作り直して
    /// 初めて入った字を含む苗字へ一時的に差し替えて選手一覧を撮る（セーブはしない）。
    /// </summary>
    public sealed class SurnameGlyphShooter : MonoBehaviour
    {
        private const string Dir = "screenshots/surname-819";
        private const int W = 1600;
        private const int H = 900;

        private static readonly string[] Names =
        {
            "鯉沼　翔太", "濱田　大輝", "笹川　蓮", "堀籠　悠真", "綿貫　陽翔", "横溝　湊",
            "富樫　颯", "楠本　陸", "宮澤　大和", "山縣　樹", "冨永　蒼", "渥美　海斗",
        };

        private IEnumerator Start()
        {
            Application.runInBackground = true;
            Directory.CreateDirectory(Dir);
            var roster = RosterService.Roster;
            var prop = typeof(KokoSim.Engine.Season.DevelopingPlayer).GetProperty("Name");
            for (int i = 0; i < roster.Count && i < Names.Length; i++) prop.SetValue(roster[i], Names[i]);

            UIDocument doc = null;
            foreach (var d in Resources.FindObjectsOfTypeAll<UIDocument>())
            {
                if (!d.gameObject.scene.IsValid()) continue;
                var on = d.gameObject.name == "PlayerList";
                d.gameObject.SetActive(false);
                if (on) doc = d;
            }
            if (doc == null) { Debug.LogError("[i819] PlayerList が無い"); yield break; }
            doc.gameObject.SetActive(true);
            yield return null; yield return null; yield return null;
            yield return new WaitForEndOfFrame();
            ScreenshotCapture.Capture("PlayerList", W, H, Path.Combine(Dir, "players.png"));
            for (int i = 0; i < 12; i++) yield return null;
            Debug.Log("[i819] DONE → " + Dir);
        }
    }
}
#endif
