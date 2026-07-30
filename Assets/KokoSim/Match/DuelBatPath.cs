using System;
using System.Collections.Generic;
using UnityEngine;

namespace KokoSim.Unity.Match
{
    /// <summary>バット軌道の1キー。座標系は**打者GOローカル**（立ち位置・ヨーに依らない）。</summary>
    [Serializable]
    public struct DuelBatKey
    {
        public float T;         // クリップ内秒
        public Vector3 Pos;     // バット中心（メッシュ原点）の打者ローカル位置[m]
        public Vector3 Euler;   // 打者ローカルの姿勢[度]。バットの長軸はローカル+Y（ノブ→バレル）
    }

    /// <summary>
    /// バットをコマごとに手で置くための軌道テーブル（design-18 §7-2 第3弾・2026-07-30 監督指示）。
    ///
    /// なぜ手追従をやめるか: 従来はバット軸を両手を結んだ線 <c>(右手−左手)</c> から作っていたが、
    /// 両手の間隔が詰まる瞬間はこの向きが不安定で、**クリップ時刻が14ms違うだけで軸が飛ぶ**
    /// （2026-07-30 実測。1.0倍と1.4倍で同じ実時刻の絵を比べて判明）。振りの見え方が
    /// 収録の手の揺れに丸ごと引きずられるので、バットを主にして手を従わせる。
    ///
    /// 座標系を打者ローカルにしてあるのは、立ち位置（BatterX/Z）やヨー（BatterYawDeg）を
    /// 動かしてもテーブルが腐らないようにするため。ワールドで持つと較正を触った瞬間に全部打ち直しになる。
    ///
    /// 端は**外挿しない**（クランプ）。外挿すると構えの手前や走り出しの先で
    /// バットが飛んでいく＝キーを打っていない区間を「打ち忘れ」として絵で気づけなくなる。
    /// </summary>
    [Serializable]
    public sealed class DuelBatPath
    {
        public List<DuelBatKey> Keys = new();

        /// <summary>いま使う軌道（null または キー1本以下＝従来の手追従）。</summary>
        public static DuelBatPath Current;

        /// <summary>収録中の軌道（<see cref="Recording"/> が真のとき AlignBat が書き込む）。</summary>
        public static DuelBatPath Recorded;

        /// <summary>
        /// 焼き取りモード。真のあいだ AlignBat は**手追従で出した結果**を <see cref="Recorded"/> へ積む。
        /// 出発点のテーブルを人手でなく現物から作るための入口＝焼いた直後は今と同じ絵になり、
        /// 「テーブル経路が正しいか」を絵で確かめてから編集に入れる。
        /// </summary>
        public static bool Recording;

        public bool HasKeys => Keys != null && Keys.Count >= 2;

        /// <summary>
        /// 時刻 t の姿勢。キーの間は位置を線形・回転を球面で補間する。
        /// 12fpsのコマ打ちを前提にしているので、補間はコマ間の繋ぎでしか効かない。
        /// </summary>
        public bool Evaluate(float t, out Vector3 pos, out Quaternion rot)
        {
            pos = Vector3.zero;
            rot = Quaternion.identity;
            if (Keys == null || Keys.Count == 0) return false;
            if (Keys.Count == 1 || t <= Keys[0].T)
            {
                pos = Keys[0].Pos;
                rot = Quaternion.Euler(Keys[0].Euler);
                return true;
            }
            var last = Keys[Keys.Count - 1];
            if (t >= last.T)
            {
                pos = last.Pos;
                rot = Quaternion.Euler(last.Euler);
                return true;
            }
            for (int i = 1; i < Keys.Count; i++)
            {
                var b = Keys[i];
                if (b.T < t) continue;
                var a = Keys[i - 1];
                float span = b.T - a.T;
                float k = span <= 1e-6f ? 0f : Mathf.Clamp01((t - a.T) / span);
                pos = Vector3.Lerp(a.Pos, b.Pos, k);
                rot = Quaternion.Slerp(Quaternion.Euler(a.Euler), Quaternion.Euler(b.Euler), k);
                return true;
            }
            return false;
        }

        /// <summary>
        /// 時刻 t のキーを置く（同じ時刻があれば差し替え）。時刻の昇順は常に保つ＝
        /// <see cref="Evaluate"/> が線形走査で正しく引ける。
        /// </summary>
        public void Set(float t, Vector3 pos, Vector3 euler)
        {
            Keys ??= new List<DuelBatKey>();
            var key = new DuelBatKey { T = t, Pos = pos, Euler = euler };
            for (int i = 0; i < Keys.Count; i++)
            {
                if (Mathf.Abs(Keys[i].T - t) < 1e-4f) { Keys[i] = key; return; }
                if (Keys[i].T > t) { Keys.Insert(i, key); return; }
            }
            Keys.Add(key);
        }

        /// <summary>時刻 t に最も近いキーを消す（許容内に無ければ何もしない）。</summary>
        public bool Remove(float t, float tolerance = 0.02f)
        {
            if (Keys == null) return false;
            for (int i = 0; i < Keys.Count; i++)
                if (Mathf.Abs(Keys[i].T - t) <= tolerance) { Keys.RemoveAt(i); return true; }
            return false;
        }

        public DuelBatPath Clone()
        {
            var c = new DuelBatPath();
            if (Keys != null) c.Keys = new List<DuelBatKey>(Keys);
            return c;
        }

        public string ToJson() => JsonUtility.ToJson(this, true);

        public static DuelBatPath FromJson(string json)
        {
            if (string.IsNullOrWhiteSpace(json)) return null;
            var p = JsonUtility.FromJson<DuelBatPath>(json);
            return p != null && p.Keys != null && p.Keys.Count > 0 ? p : null;
        }
    }
}
