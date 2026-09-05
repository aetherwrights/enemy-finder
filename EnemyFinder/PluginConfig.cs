using Dalamud.Configuration;

namespace EnemyFinder;

public sealed class PluginConfig : IPluginConfiguration
{
    public const int MaxHistory = 20;
    public const int MinRadiusYalms = 15;
    public const int MaxRadiusYalms = 80;
    public const int DefaultRadiusYalms = 40;
    public const int MinWikiCacheSize = 0;
    public const int MaxWikiCacheSize = 64;
    public const int DefaultWikiCacheSize = 32;

    public int Version { get; set; } = 1;

    public bool IncludeFateCamps { get; set; }

    /// <summary>
    /// When true, enemies found in both the overworld and a duty prompt for which map to open.
    /// Duty-only enemies always open the duty map.
    /// </summary>
    public bool AskDutyOrOverworld { get; set; }

    public int CircleRadiusYalms { get; set; } = DefaultRadiusYalms;

    public int WikiCacheSize { get; set; } = DefaultWikiCacheSize;

    public bool HuntLogClicks { get; set; } = true;

    public bool RelicBookClicks { get; set; } = true;

    public bool RelicBookFateClicks { get; set; } = true;

    public bool OtherBookClicks { get; set; } = true;

    public List<string> RecentEnemies { get; set; } = [];

    public int ClampedRadiusYalms => Math.Clamp(this.CircleRadiusYalms, MinRadiusYalms, MaxRadiusYalms);

    public int ClampedWikiCacheSize => Math.Clamp(this.WikiCacheSize, MinWikiCacheSize, MaxWikiCacheSize);

    public void Normalize()
    {
        this.RecentEnemies ??= [];
        this.CircleRadiusYalms = this.ClampedRadiusYalms;
        this.WikiCacheSize = this.ClampedWikiCacheSize;
        if (this.RecentEnemies.Count > MaxHistory)
        {
            this.RecentEnemies.RemoveRange(MaxHistory, this.RecentEnemies.Count - MaxHistory);
        }
    }
}
