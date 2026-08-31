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
