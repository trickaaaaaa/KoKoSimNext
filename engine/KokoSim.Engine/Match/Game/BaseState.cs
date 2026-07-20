using KokoSim.Engine.Players;

namespace KokoSim.Engine.Match.Game;

/// <summary>塁上の走者状態（1塁=0, 2塁=1, 3塁=2）。</summary>
public sealed class BaseState
{
    private readonly Player?[] _bases = new Player?[3];

    public Player? First { get => _bases[0]; set => _bases[0] = value; }
    public Player? Second { get => _bases[1]; set => _bases[1] = value; }
    public Player? Third { get => _bases[2]; set => _bases[2] = value; }

    public bool Occupied(int baseIndex) => _bases[baseIndex] is not null;
    public int RunnerCount => (First is not null ? 1 : 0) + (Second is not null ? 1 : 0) + (Third is not null ? 1 : 0);

    public void Clear()
    {
        _bases[0] = _bases[1] = _bases[2] = null;
    }
}
