#if UNITY_EDITOR
using System.Reflection;
using System.Text;
using KokoSim.Engine.Nation;
using KokoSim.Unity.Shell;
using UnityEditor;
using UnityEngine;

namespace KokoSim.Unity.EditorTools
{
    /// <summary>
    /// 【開発用・エディタ限定】issue #618 の配線検証。
    ///
    /// <para>「自校の配色が試合へ渡らない」は <see cref="MatchStartFlow"/> の private ヘルパの
    /// 分岐ミス（番兵 -1 の多重定義）だったので、**本体をリフレクションで直接呼んで**確かめる
    /// （検証用にロジックを写すと、写した側だけ直っていても気づけない）。</para>
    /// </summary>
    public static class TeamColorWiringProbe
    {
        [MenuItem("KokoSim/Probe/チーム配色の配線を検証 (#618)")]
        public static void Run()
        {
            var flow = typeof(MatchStartFlow);
            var manager = flow.GetMethod("ManagerColors", BindingFlags.NonPublic | BindingFlags.Static);
            var opponent = flow.GetMethod("OpponentColorsOf", BindingFlags.NonPublic | BindingFlags.Static);
            var sb = new StringBuilder("[#618] チーム配色の配線検証\n");

            if (manager == null || opponent == null)
            {
                Debug.LogError("[#618] ManagerColors / OpponentColorsOf が見つからない（旧 TeamColorsOf のまま？）");
                return;
            }

            // ① 上書き無し（新規ゲームで配色を触らなかった場合）＝校IDからの決定論割り当てが返ること。
            var saved = TeamColorProvider.ManagerOverride;
            TeamColorProvider.ManagerOverride = null;
            var auto = (TeamColors)manager.Invoke(null, null);
            sb.Append("① 上書き無しの自校: ").Append(Describe(auto)).Append('\n');

            // ② プレイヤーが選んだ配色（設定画面と同じ入口＝ManagerOverride）が返ること。
            var picked = TeamColorProvider.Palette.ColorsFor(7);
            TeamColorProvider.ManagerOverride = picked;
            var overridden = (TeamColors)manager.Invoke(null, null);
            sb.Append("② 上書き有りの自校: ").Append(Describe(overridden))
              .Append(ReferenceEquals(overridden, picked) ? "  ← 選んだ配色そのもの" : "  ← ★選んだ配色が返っていない").Append('\n');

            // ③ 相手校（実在ID）＝パレット表の決定論配色。自校と別物に見えること。
            var opp = (TeamColors)opponent.Invoke(null, new object[] { 1234 });
            sb.Append("③ 相手校(id=1234): ").Append(Describe(opp)).Append('\n');

            // ④ 相手校ID不明（練習試合の保険経路）だけが null になること。
            var unknown = (TeamColors)opponent.Invoke(null, new object[] { -1 });
            sb.Append("④ 相手校ID不明(-1): ").Append(unknown == null ? "null（想定どおり）" : "★nullでない").Append('\n');

            TeamColorProvider.ManagerOverride = saved;

            var ok = auto != null && overridden != null && ReferenceEquals(overridden, picked)
                     && opp != null && unknown == null && !SameLook(overridden, opp);
            sb.Append(ok ? "=> 合格: 自校は常に非null・相手校と別配色・ID不明だけnull"
                         : "=> ★不合格");
            if (ok) Debug.Log(sb.ToString()); else Debug.LogError(sb.ToString());
        }

        private static string Describe(TeamColors c)
            => c == null ? "null（★自校がnullなら不合格）"
                : $"シャツ{Hex(c.Shirt)} ズボン{Hex(c.Pants)} アンダー{Hex(c.Undershirt)} 帽子{Hex(c.CapCrown)} 縦縞{(c.Stripe.On ? "有" : "無")}";

        private static string Hex(RgbColor c) => $"#{c.R:X2}{c.G:X2}{c.B:X2}";

        private static bool SameLook(TeamColors a, TeamColors b)
            => a != null && b != null
               && a.Shirt.R == b.Shirt.R && a.Shirt.G == b.Shirt.G && a.Shirt.B == b.Shirt.B
               && a.Pants.R == b.Pants.R && a.Pants.G == b.Pants.G && a.Pants.B == b.Pants.B;
    }
}
#endif
