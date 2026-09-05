namespace EnemyFinder.Data;

public sealed record MapCamp(float MapX, float MapY);

public sealed record SpawnLocation(
    string Name,
    uint TerritoryTypeId,
    uint MapId,
    IReadOnlyList<MapCamp> Camps,
    float RadiusYalms,
    string Source)
{
    public IReadOnlyList<string> Prerequisites { get; init; } = [];

    public float MapX => this.Camps[0].MapX;

    public float MapY => this.Camps[0].MapY;
}

public sealed record EnemySpawnOptions(SpawnLocation? Overworld, IReadOnlyList<SpawnLocation> Duties)
{
    public bool HasChoice => this.Overworld != null && this.Duties.Count > 0;

    public SpawnLocation Preferred =>
        this.Overworld ?? (this.Duties.Count > 0
            ? this.Duties[0]
            : throw new InvalidOperationException("No spawn location."));
}
