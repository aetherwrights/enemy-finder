using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Game.Command;
using Dalamud.IoC;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using EnemyFinder.Data;
using EnemyFinder.Mapping;
using EnemyFinder.Ui;

namespace EnemyFinder;

public sealed class Plugin : IDalamudPlugin
{
    private const string CommandName = "/efind";

    [PluginService] internal static IDalamudPluginInterface PluginInterface { get; private set; } = null!;
    [PluginService] internal static ICommandManager CommandManager { get; private set; } = null!;
    [PluginService] internal static IDataManager DataManager { get; private set; } = null!;
    [PluginService] internal static IGameGui GameGui { get; private set; } = null!;
    [PluginService] internal static IChatGui ChatGui { get; private set; } = null!;
    [PluginService] internal static IFramework Framework { get; private set; } = null!;
    [PluginService] internal static IPluginLog Log { get; private set; } = null!;
    [PluginService] internal static IAddonLifecycle AddonLifecycle { get; private set; } = null!;
    [PluginService] internal static IAddonEventManager AddonEventManager { get; private set; } = null!;

    private readonly PluginConfig config;
    private readonly SpawnLocationProvider spawnLocations;
    private readonly NativeMapMarkerService mapMarkers;
    private readonly EnemyClickService enemyClicks;
    private bool configVisible;

    public Plugin()
    {
        this.config = PluginInterface.GetPluginConfig() as PluginConfig ?? new PluginConfig();
        this.config.Normalize();

        this.spawnLocations = new SpawnLocationProvider(DataManager, Log, this.config);
        this.mapMarkers = new NativeMapMarkerService(DataManager, GameGui, Log);
        this.enemyClicks = new EnemyClickService(
            AddonEventManager,
            AddonLifecycle,
            ChatGui,
            this.config,
            DataManager,
            Framework,
            Log,
            this.mapMarkers,
            this.SaveConfig,
            this.spawnLocations);

        CommandManager.AddHandler(CommandName, new CommandInfo(this.OnCommand)
        {
            HelpMessage = "Look up an enemy by name. With no name, opens the Enemy Finder window.",
        });

        PluginInterface.UiBuilder.Draw += this.Draw;
        PluginInterface.UiBuilder.OpenConfigUi += this.OpenConfigUi;
        PluginInterface.UiBuilder.OpenMainUi += this.OpenConfigUi;

        Log.Info("{Plugin} loaded. Click a hunting log enemy name, a relic book enemy, or use {Command} Name.", PluginInterface.Manifest.Name, CommandName);
    }

    public void Dispose()
    {
        PluginInterface.UiBuilder.Draw -= this.Draw;
        PluginInterface.UiBuilder.OpenConfigUi -= this.OpenConfigUi;
        PluginInterface.UiBuilder.OpenMainUi -= this.OpenConfigUi;

        CommandManager.RemoveHandler(CommandName);
        this.enemyClicks.Dispose();
        this.spawnLocations.Dispose();
        this.SaveConfig();
    }

    private void OpenConfigUi() => this.configVisible = true;

    private void SaveConfig() => PluginInterface.SavePluginConfig(this.config);

    private void Draw()
    {
        this.enemyClicks.DrawWindows();
        if (!this.configVisible)
        {
            return;
        }

        ImGui.SetNextWindowSize(new Vector2(420, 520), ImGuiCond.FirstUseEver);
        if (ImGui.Begin("Enemy Finder", ref this.configVisible))
        {
            ImGui.TextWrapped("Click an enemy in the hunting log, a Trials of the Braves relic book (enemies or FATEs), or Bozja field records to show its spawn on the map.");
            ImGui.TextWrapped("In the hunting log, click the enemy's name. The icon, kill count, and empty space do nothing.");
            ImGui.TextUnformatted("Command: /efind Enemy Name");
            ImGui.Separator();
            ImGui.TextUnformatted("Click sources");
            ImGui.TextDisabled("Turn a source off if it conflicts with another plugin.");

            var huntLog = this.config.HuntLogClicks;
            if (ImGui.Checkbox("Hunting log name clicks", ref huntLog))
            {
                this.config.HuntLogClicks = huntLog;
                this.SaveConfig();
            }

            var relic = this.config.RelicBookClicks;
            if (ImGui.Checkbox("Relic book enemy clicks", ref relic))
            {
                this.config.RelicBookClicks = relic;
                this.SaveConfig();
            }

            var relicFates = this.config.RelicBookFateClicks;
            if (ImGui.Checkbox("Relic book FATE clicks", ref relicFates))
            {
                this.config.RelicBookFateClicks = relicFates;
                this.SaveConfig();
            }

            var other = this.config.OtherBookClicks;
            if (ImGui.Checkbox("Other enemy books (Bozja field records)", ref other))
            {
                this.config.OtherBookClicks = other;
                this.SaveConfig();
            }

            ImGui.Separator();

            var fates = this.config.IncludeFateCamps;
            if (ImGui.Checkbox("Include FATE camps", ref fates))
            {
                this.config.IncludeFateCamps = fates;
                this.SaveConfig();
            }

            var radius = this.config.CircleRadiusYalms;
            if (ImGui.SliderInt("Circle radius (yalms)", ref radius, PluginConfig.MinRadiusYalms, PluginConfig.MaxRadiusYalms))
            {
                this.config.CircleRadiusYalms = radius;
                this.SaveConfig();
            }

            var cacheSize = this.config.WikiCacheSize;
            if (ImGui.SliderInt("Wiki cache size", ref cacheSize, PluginConfig.MinWikiCacheSize, PluginConfig.MaxWikiCacheSize))
            {
                this.config.WikiCacheSize = cacheSize;
                this.spawnLocations.TrimCacheToLimit();
                this.SaveConfig();
            }

            ImGui.TextDisabled($"Cached lookups: {this.spawnLocations.CachedCount} / {this.config.ClampedWikiCacheSize}");
            ImGui.SameLine();
            if (ImGui.SmallButton("Clear cache"))
            {
                this.spawnLocations.ClearCache();
            }

            ImGui.Separator();
            ImGui.TextUnformatted("History");
            ImGui.SameLine();
            if (this.config.RecentEnemies.Count > 0 && ImGui.SmallButton("Clear"))
            {
                this.config.RecentEnemies.Clear();
                this.SaveConfig();
            }

            if (ImGui.BeginChild("efind-history", new Vector2(0, 180), true))
            {
                if (this.config.RecentEnemies.Count == 0)
                {
                    ImGui.TextDisabled("Looked-up enemies will appear here.");
                }
                else
                {
                    foreach (var name in this.config.RecentEnemies.ToArray())
                    {
                        if (ImGui.Selectable(name))
                        {
                            this.enemyClicks.ShowEnemyByName(name);
                        }
                    }
                }
            }

            ImGui.EndChild();
        }

        ImGui.End();
    }

    private void OnCommand(string command, string args)
    {
        var name = args.Trim();
        if (string.IsNullOrWhiteSpace(name))
        {
            this.OpenConfigUi();
            return;
        }

        this.enemyClicks.ShowEnemyByName(name);
    }
}
