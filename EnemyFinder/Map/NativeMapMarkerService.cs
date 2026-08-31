using System.Numerics;
using Dalamud.Game.Text.SeStringHandling.Payloads;
using Dalamud.Plugin.Services;
using EnemyFinder.Data;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using Lumina.Excel.Sheets;
using AgentMapType = FFXIVClientStructs.FFXIV.Client.UI.Agent.MapType;

namespace EnemyFinder.Mapping;

public sealed class NativeMapMarkerService
{
    /// <summary>Hunt bill icon — distinctive native marker if no purple icon is available.</summary>
    private const uint AreaIconId = 60422;

    private readonly IDataManager dataManager;
    private readonly IGameGui gameGui;
    private readonly IPluginLog log;

    public NativeMapMarkerService(IDataManager dataManager, IGameGui gameGui, IPluginLog log)
    {
        this.dataManager = dataManager;
        this.gameGui = gameGui;
        this.log = log;
    }

    public bool ShowArea(SpawnLocation location)
    {
        if (location.Camps.Count == 0)
        {
            return false;
        }

        var firstWorld = ToWorld(location.TerritoryTypeId, location.MapId, location.MapX, location.MapY);

        unsafe
        {
            var agentMap = AgentMap.Instance();
            if (agentMap == null)
            {
                this.log.Warning("AgentMap is unavailable; falling back to a map flag");
                return this.ShowFlagFallback(location, firstWorld);
            }

            try
            {
                // GatheringLog focuses on the temp marker. AddGatheringTempMarker wants world
                // X/Z in yalms (HaselTweaks/fishing: (pixel - 1024) / scale), not world*16 and
                // not 2048-space pixels. *16 overscrolled northeast; pixels overscrolled southeast.
                agentMap->TempMapMarkerCount = 0;
                var added = 0;
                foreach (var camp in location.Camps)
                {
                    if (added >= 12)
                    {
                        break;
                    }

                    var world = ToWorld(location.TerritoryTypeId, location.MapId, camp.MapX, camp.MapY);
                    var (mapX, mapY, radius) = ToGatheringMarker(location.MapId, world, location.RadiusYalms);
                    agentMap->AddGatheringTempMarker(mapX, mapY, radius, AreaIconId, 4, location.Name);
                    added++;
                }

                agentMap->OpenMap(location.MapId, location.TerritoryTypeId, location.Name, AgentMapType.GatheringLog);
                this.log.Info(
                    "Opened {Territory} map {Map} with {Count} area marker(s) for {Enemy}",
                    location.TerritoryTypeId, location.MapId, added, location.Name);
                return true;
            }
            catch (Exception ex)
            {
                this.log.Warning(ex, "Native area marker failed; falling back to a map flag");
                return this.ShowFlagFallback(location, firstWorld);
            }
        }
    }

    private bool ShowFlagFallback(SpawnLocation location, Vector3 world)
    {
        try
        {
            var opened = this.gameGui.OpenMapWithMapLink(location.TerritoryTypeId, location.MapId, world);
            if (!opened)
            {
                var payload = new MapLinkPayload(location.TerritoryTypeId, location.MapId, location.MapX, location.MapY);
                opened = this.gameGui.OpenMapWithMapLink(payload);
            }

            if (opened)
            {
                this.log.Info("Opened map flag fallback for {Enemy}", location.Name);
            }
            else
            {
                this.log.Error("Failed to open map for {Enemy}", location.Name);
            }

            return opened;
        }
        catch (Exception ex)
        {
            this.log.Error(ex, "Map flag fallback failed for {Enemy}", location.Name);
            return false;
        }
    }

    private (int X, int Y, int Radius) ToGatheringMarker(uint mapId, Vector3 world, float radiusYalms)
    {
        short offsetX = 0;
        short offsetY = 0;
        var sheet = this.dataManager.GetExcelSheet<Map>();
        if (sheet.TryGetRow(mapId, out var map))
        {
            offsetX = map.OffsetX;
            offsetY = map.OffsetY;
        }

        // Same space as ExportedGatheringPoint X/Y and HaselCommon's fishing convert.
        var x = (int)MathF.Round(world.X + offsetX);
        var y = (int)MathF.Round(world.Z + offsetY);
        var radius = Math.Max(1, (int)MathF.Round(radiusYalms));
        return (x, y, radius);
    }

    private Vector3 ToWorld(uint territoryTypeId, uint mapId, float mapX, float mapY)
    {
        // MapLinkPayload uses the same raw-position conversion as the game's map links.
        var payload = new MapLinkPayload(territoryTypeId, mapId, mapX, mapY);
        return new Vector3(payload.RawX / 1000f, 0f, payload.RawY / 1000f);
    }

    public string? GetTerritoryName(uint territoryTypeId)
    {
        var sheet = this.dataManager.GetExcelSheet<TerritoryType>();
        if (!sheet.TryGetRow(territoryTypeId, out var territory))
        {
            return null;
        }

        var name = territory.PlaceName.Value.Name.ExtractText();
        return string.IsNullOrWhiteSpace(name) ? null : name;
    }
}
