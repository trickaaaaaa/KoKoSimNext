namespace KokoSim.Engine.Players;

/// <summary>
/// 見極めで開示された性格の読み（設計書23 §1.5・Q37）。軸ごとに「開示済みか」と「高い側と見えたか」を持つ。
/// 読みは誤認もあり得る（育成眼が低いと逆側に見える）＝本当の軸の値とは独立に保持する。
/// 4軸そろうと <see cref="KnownType"/> がタイプ名を返す。既定値＝何も開示されていない。
/// </summary>
public readonly record struct PersonalityReading(int KnownMask, int HighMask)
{
    private static int Bit(PersonalityAxis axis) => 1 << (int)axis;

    /// <summary>その軸が開示済みか。</summary>
    public bool IsKnown(PersonalityAxis axis) => (KnownMask & Bit(axis)) != 0;

    /// <summary>その軸が高い側と見えたか（未開示なら null）。</summary>
    public bool? SeenHigh(PersonalityAxis axis) => IsKnown(axis) ? (HighMask & Bit(axis)) != 0 : null;

    /// <summary>軸の読みを記録した新しい値（同じ軸を読み直したら上書き＝最新の見立て）。</summary>
    public PersonalityReading With(PersonalityAxis axis, bool high)
        => new(KnownMask | Bit(axis), high ? HighMask | Bit(axis) : HighMask & ~Bit(axis));

    /// <summary>4軸すべて開示済みか。</summary>
    public bool IsComplete => (KnownMask & 0xF) == 0xF;

    /// <summary>4軸そろっていれば、読みから導出したタイプ名（誤認込み）。そろっていなければ null。</summary>
    public Personality? KnownType
    {
        get
        {
            if (!IsComplete) return null;
            var high = HighMask;
            int V(PersonalityAxis a) => (high & Bit(a)) != 0 ? 75 : 25;
            return Personalities.Classify(V(PersonalityAxis.Leadership), V(PersonalityAxis.Receptiveness),
                V(PersonalityAxis.Diligence), V(PersonalityAxis.Selflessness));
        }
    }
}
