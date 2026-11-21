namespace KokoSim.Engine.Core;

/// <summary>試合ごとの独立ストリームの鍵（年度・週・その週の連番）。連番が大きくても別の週と衝突しないようビットで詰める。</summary>
public static class StreamKey
{
    public static ulong Of(int yearIndex, int week, int seq)
        => ((ulong)(uint)yearIndex << 40) ^ ((ulong)(uint)week << 32) ^ (ulong)(uint)seq;
}
