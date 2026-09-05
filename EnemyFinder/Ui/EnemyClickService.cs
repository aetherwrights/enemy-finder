using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Game.Addon.Events;
using Dalamud.Game.Addon.Events.EventDataTypes;
using Dalamud.Game.Addon.Lifecycle;
using Dalamud.Game.Addon.Lifecycle.AddonArgTypes;
using Dalamud.Plugin.Services;
using EnemyFinder.Data;
using EnemyFinder.Mapping;
using FFXIVClientStructs.FFXIV.Client.System.Input;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using FFXIVClientStructs.FFXIV.Component.GUI;
using Lumina.Excel.Sheets;
using RelicNoteState = FFXIVClientStructs.FFXIV.Client.Game.UI.RelicNote;

namespace EnemyFinder.Ui;

public sealed class EnemyClickService : IDisposable
{
    private readonly IAddonEventManager addonEvents;
    private readonly IAddonLifecycle addonLifecycle;
    private readonly IChatGui chatGui;
    private readonly PluginConfig config;
    private readonly IDataManager dataManager;
    private readonly IFramework framework;
    private readonly IPluginLog log;
    private readonly NativeMapMarkerService mapMarkers;
    private readonly System.Action saveConfig;
    private readonly SpawnLocationProvider spawnLocations;
    private readonly List<IAddonEventHandle> otherBookHandles = [];
    private HashSet<string>? bnpcNames;
    private CancellationTokenSource? lookupCts;
    private long lastClickTicks;
    private IReadOnlyList<FateChainStep>? fateChainPrompt;
    private bool focusFateChainPrompt;
    private IReadOnlyList<DutyChoice>? dutyChoicePrompt;
    private bool focusDutyChoicePrompt;

    public EnemyClickService(
        IAddonEventManager addonEvents,
        IAddonLifecycle addonLifecycle,
        IChatGui chatGui,
        PluginConfig config,
        IDataManager dataManager,
        IFramework framework,
        IPluginLog log,
        NativeMapMarkerService mapMarkers,
        System.Action saveConfig,
        SpawnLocationProvider spawnLocations)
    {
        this.addonEvents = addonEvents;
        this.addonLifecycle = addonLifecycle;
        this.chatGui = chatGui;
        this.config = config;
        this.dataManager = dataManager;
        this.framework = framework;
        this.log = log;
        this.mapMarkers = mapMarkers;
        this.saveConfig = saveConfig;
        this.spawnLocations = spawnLocations;

        this.addonLifecycle.RegisterListener(AddonEvent.PostReceiveEvent, "RelicNoteBook", this.OnRelicNoteBookEvent);
        this.addonLifecycle.RegisterListener(AddonEvent.PostSetup, "MYCWarResultNotebook", this.OnOtherBookReady);
        this.addonLifecycle.RegisterListener(AddonEvent.PreFinalize, "MYCWarResultNotebook", this.OnOtherBookClose);
        this.framework.Update += this.OnFrameworkUpdate;
    }

    public void Dispose()
    {
        this.lookupCts?.Cancel();
        this.lookupCts?.Dispose();
        this.framework.Update -= this.OnFrameworkUpdate;
        this.ClearHandles(this.otherBookHandles);
        this.addonLifecycle.UnregisterListener(this.OnRelicNoteBookEvent);
        this.addonLifecycle.UnregisterListener(this.OnOtherBookReady);
        this.addonLifecycle.UnregisterListener(this.OnOtherBookClose);
    }

    public void DrawWindows()
    {
        this.DrawFateChainWindow();
        this.DrawDutyChoiceWindow();
    }

    private void DrawFateChainWindow()
    {
        if (this.fateChainPrompt == null || this.fateChainPrompt.Count == 0)
        {
            return;
        }

        var open = true;
        ImGui.SetNextWindowSize(new Vector2(440, 0), ImGuiCond.FirstUseEver);
        if (this.focusFateChainPrompt)
        {
            ImGui.SetNextWindowFocus();
            this.focusFateChainPrompt = false;
        }

        if (ImGui.Begin("Enemy Finder — FATE chain", ref open, ImGuiWindowFlags.AlwaysAutoResize))
        {
            var requested = this.fateChainPrompt[^1];
            var first = this.fateChainPrompt[0];
            ImGui.TextWrapped($"{requested.Name} needs another FATE first. Complete them in order:");
            ImGui.Separator();

            for (var i = 0; i < this.fateChainPrompt.Count; i++)
            {
                var step = this.fateChainPrompt[i];
                var isRequested = i == this.fateChainPrompt.Count - 1;
                var isFirst = i == 0;
                if (ImGui.Button($"Show##fate-chain-{i}"))
                {
                    this.ShowChainStep(step);
                }

                ImGui.SameLine();
                var suffix = isRequested ? " (this FATE)" : isFirst ? " (do this first)" : string.Empty;
                ImGui.TextUnformatted($"{i + 1}. {step.Name}{suffix}");
            }

            ImGui.Separator();
            if (ImGui.Button("Show first FATE"))
            {
                this.ShowChainStep(first);
            }

            ImGui.SameLine();
            if (ImGui.Button("Close"))
            {
                open = false;
            }
        }

        ImGui.End();
        if (!open)
        {
            this.fateChainPrompt = null;
        }
    }

    private void DrawDutyChoiceWindow()
    {
        if (this.dutyChoicePrompt == null || this.dutyChoicePrompt.Count == 0)
        {
            return;
        }

        var open = true;
        ImGui.SetNextWindowSize(new Vector2(440, 0), ImGuiCond.FirstUseEver);
        if (this.focusDutyChoicePrompt)
        {
            ImGui.SetNextWindowFocus();
            this.focusDutyChoicePrompt = false;
        }

        if (ImGui.Begin("Enemy Finder — Duty or overworld", ref open, ImGuiWindowFlags.AlwaysAutoResize))
        {
            var name = this.dutyChoicePrompt[0].Location.Name;
            ImGui.TextWrapped($"{name} is in the overworld and in a duty. Choose which map to open:");
            ImGui.Separator();

            for (var i = 0; i < this.dutyChoicePrompt.Count; i++)
            {
                var choice = this.dutyChoicePrompt[i];
                if (ImGui.Button($"Show##duty-choice-{i}"))
                {
                    this.ShowResolvedLocation(choice.Location);
                    open = false;
                }

                ImGui.SameLine();
                ImGui.TextUnformatted(choice.Label);
            }

            ImGui.Separator();
            if (ImGui.Button("Close"))
            {
                open = false;
            }
        }

        ImGui.End();
        if (!open)
        {
            this.dutyChoicePrompt = null;
        }
    }

    public void ShowEnemyByName(string enemyName)
        => this.StartEnemyLookup(token => this.LookupNameAsync(enemyName, token));

    public void ShowEnemyByBNpcNameId(uint bNpcNameId)
        => this.StartEnemyLookup(token => this.spawnLocations.GetEnemyOptionsAsync(bNpcNameId, this.config.IncludeFateCamps, token));

    public void ShowFateById(uint fateId) => this.StartLookup(token => this.spawnLocations.GetFateLocationAsync(fateId, token));

    public void ShowFateByName(string fateName) => this.StartLookup(token => this.spawnLocations.GetFateLocationAsync(fateName, token));

    private async Task<EnemySpawnOptions> LookupNameAsync(string name, CancellationToken cancellationToken)
    {
        try
        {
            return await this.spawnLocations.GetEnemyOptionsAsync(name, this.config.IncludeFateCamps, cancellationToken).ConfigureAwait(false);
        }
        catch (InvalidOperationException)
        {
            var fate = await this.spawnLocations.GetFateLocationAsync(name, cancellationToken).ConfigureAwait(false);
            return new EnemySpawnOptions(fate, []);
        }
    }

    private void StartEnemyLookup(Func<CancellationToken, Task<EnemySpawnOptions>> lookup)
    {
        this.lookupCts?.Cancel();
        this.lookupCts?.Dispose();
        this.lookupCts = new CancellationTokenSource();
        var token = this.lookupCts.Token;
        _ = this.ShowEnemyOptionsAsync(lookup(token), token);
    }

    private void StartLookup(Func<CancellationToken, Task<SpawnLocation>> lookup)
    {
        this.lookupCts?.Cancel();
        this.lookupCts?.Dispose();
        this.lookupCts = new CancellationTokenSource();
        var token = this.lookupCts.Token;
        _ = this.ShowLookupAsync(lookup(token), token);
    }

    private async Task ShowEnemyOptionsAsync(Task<EnemySpawnOptions> lookup, CancellationToken cancellationToken)
    {
        try
        {
            var options = await lookup.ConfigureAwait(false);
            if (this.config.AskDutyOrOverworld && options.HasChoice)
            {
                await this.framework.RunOnFrameworkThread(() =>
                {
                    if (cancellationToken.IsCancellationRequested)
                    {
                        return;
                    }

                    this.dutyChoicePrompt = this.BuildDutyChoices(options);
                    this.focusDutyChoicePrompt = true;
                    this.RecordHistory(options.Preferred.Name);
                    this.chatGui.Print(
                        $"Enemy Finder: {options.Preferred.Name} is in the overworld and in a duty. Pick which map to open.");
                });
                return;
            }

            await this.PresentLocationAsync(options.Preferred, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Replaced by a newer lookup.
        }
        catch (Exception ex)
        {
            this.log.Error(ex, "Failed to show enemy location");
            await this.framework.RunOnFrameworkThread(() =>
            {
                this.chatGui.PrintError("Enemy Finder: could not find a spawn location.");
            });
        }
    }

    private List<DutyChoice> BuildDutyChoices(EnemySpawnOptions options)
    {
        var choices = new List<DutyChoice>();
        if (options.Overworld != null)
        {
            var zone = this.mapMarkers.GetTerritoryName(options.Overworld.TerritoryTypeId) ?? "overworld";
            choices.Add(new DutyChoice($"{zone} (overworld)", options.Overworld));
        }

        foreach (var duty in options.Duties)
        {
            var zone = this.mapMarkers.GetTerritoryName(duty.TerritoryTypeId) ?? "duty";
            choices.Add(new DutyChoice($"{zone} (duty)", duty));
        }

        return choices;
    }

    private async Task PresentLocationAsync(SpawnLocation location, CancellationToken cancellationToken)
    {
        if (location.Prerequisites.Count > 0)
        {
            var chain = await this.ResolveFateChainAsync(location, cancellationToken).ConfigureAwait(false);
            if (chain.Count > 1)
            {
                await this.framework.RunOnFrameworkThread(() =>
                {
                    if (cancellationToken.IsCancellationRequested)
                    {
                        return;
                    }

                    this.fateChainPrompt = chain;
                    this.focusFateChainPrompt = true;
                    this.RecordHistory(location.Name);
                    this.chatGui.Print(
                        $"Enemy Finder: {location.Name} needs {chain[0].Name} first. Pick which FATE to show on the map.");
                });
                return;
            }
        }

        await this.framework.RunOnFrameworkThread(() =>
        {
            if (!cancellationToken.IsCancellationRequested)
            {
                this.ShowResolvedLocation(location);
            }
        });
    }

    private async Task ShowLookupAsync(Task<SpawnLocation> lookup, CancellationToken cancellationToken)
    {
        try
        {
            var location = await lookup.ConfigureAwait(false);
            await this.PresentLocationAsync(location, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Replaced by a newer lookup.
        }
        catch (Exception ex)
        {
            this.log.Error(ex, "Failed to show enemy location");
            await this.framework.RunOnFrameworkThread(() =>
            {
                this.chatGui.PrintError("Enemy Finder: could not find a spawn location.");
            });
        }
    }

    private async Task<List<FateChainStep>> ResolveFateChainAsync(SpawnLocation requested, CancellationToken cancellationToken)
    {
        var chain = new List<FateChainStep> { new(requested.Name, requested) };
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { requested.Name };
        var currentPrereqs = requested.Prerequisites;
        while (currentPrereqs.Count > 0 && chain.Count < 8)
        {
            var previousName = currentPrereqs[0];
            if (previousName.Equals(requested.Name, StringComparison.OrdinalIgnoreCase) || !seen.Add(previousName))
            {
                break;
            }

            SpawnLocation? previous = null;
            try
            {
                previous = await this.spawnLocations.GetFateLocationAsync(previousName, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                this.log.Warning(ex, "Could not resolve prerequisite FATE {Fate}", previousName);
            }

            chain.Insert(0, new FateChainStep(previous?.Name ?? previousName, previous));
            currentPrereqs = previous?.Prerequisites ?? [];
        }

        return chain;
    }

    private void ShowChainStep(FateChainStep step)
    {
        if (step.Location != null)
        {
            this.ShowResolvedLocation(step.Location);
            return;
        }

        this.ShowFateByName(step.Name);
    }

    private void ShowResolvedLocation(SpawnLocation location)
    {
        location = location with { RadiusYalms = this.config.ClampedRadiusYalms };
        var opened = this.mapMarkers.ShowArea(location);
        var zoneName = this.mapMarkers.GetTerritoryName(location.TerritoryTypeId) ?? "the target zone";
        if (opened)
        {
            this.RecordHistory(location.Name);
            this.chatGui.Print(
                location.Camps.Count > 1
                    ? $"Enemy Finder: {location.Name} in {zoneName} ({location.Camps.Count} camps, {location.Source})."
                    : $"Enemy Finder: {location.Name} in {zoneName} ({location.Source}).");
        }
        else
        {
            this.chatGui.PrintError("Enemy Finder: could not open the map.");
        }
    }

    private void RecordHistory(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return;
        }

        this.config.RecentEnemies.RemoveAll(entry => entry.Equals(name, StringComparison.OrdinalIgnoreCase));
        this.config.RecentEnemies.Insert(0, name);
        while (this.config.RecentEnemies.Count > PluginConfig.MaxHistory)
        {
            this.config.RecentEnemies.RemoveAt(this.config.RecentEnemies.Count - 1);
        }

        this.saveConfig();
    }

    private unsafe void OnRelicNoteBookEvent(AddonEvent type, AddonArgs args)
    {
        try
        {
            if (args is not AddonReceiveEventArgs receiveEventArgs)
            {
                return;
            }

            var atkType = (AtkEventType)receiveEventArgs.AtkEventType;
            if (atkType is not AtkEventType.ButtonClick and not AtkEventType.MouseClick)
            {
                return;
            }

            var addon = (AddonRelicNoteBook*)receiveEventArgs.Addon.Address;
            var atkEvent = (AtkEvent*)receiveEventArgs.AtkEvent;
            if (addon == null || atkEvent == null || addon->CategoryList == null)
            {
                return;
            }

            var tab = addon->CategoryList->SelectedItemIndex;
            if (tab == 2)
            {
                if (!this.config.RelicBookFateClicks)
                {
                    return;
                }

                this.TryShowRelicFate(addon, atkEvent->Target, atkType, receiveEventArgs.EventParam);
                return;
            }

            // 0 = enemies. Dungeons / leves are handled by other plugins.
            if (!this.config.RelicBookClicks || tab != 0)
            {
                return;
            }

            var enemyIndex = GetRelicEnemyIndex(addon, atkEvent->Target);
            if (enemyIndex == null &&
                atkType is AtkEventType.ButtonClick &&
                receiveEventArgs.EventParam is >= 0 and <= 9)
            {
                enemyIndex = receiveEventArgs.EventParam;
            }

            if (enemyIndex == null)
            {
                return;
            }

            var bNpcNameId = this.GetRelicEnemyBNpcNameId(enemyIndex.Value);
            if (bNpcNameId == null || bNpcNameId == 0)
            {
                this.log.Warning("Relic book enemy {Index} has no BNpcName", enemyIndex.Value);
                return;
            }

            this.log.Info("Relic book click: enemy index {Index}, BNpcName {Id}", enemyIndex.Value, bNpcNameId.Value);
            this.ShowEnemyByBNpcNameId(bNpcNameId.Value);
        }
        catch (Exception ex)
        {
            this.log.Error(ex, "RelicNoteBook click handling failed");
        }
    }

    private unsafe void TryShowRelicFate(AddonRelicNoteBook* addon, AtkEventTarget* target, AtkEventType atkType, int eventParam)
    {
        var fateIndex = GetRelicFateIndex(addon, target);
        if (fateIndex == null &&
            atkType is AtkEventType.ButtonClick &&
            eventParam is >= 0 and <= 2 &&
            IsUnderNode(target, addon->FateContainer))
        {
            fateIndex = eventParam;
        }

        if (fateIndex == null)
        {
            return;
        }

        var fateId = this.GetRelicFateId(fateIndex.Value);
        if (fateId == null || fateId == 0)
        {
            this.log.Warning("Relic book FATE {Index} has no Fate id", fateIndex.Value);
            return;
        }

        this.log.Info("Relic book click: FATE index {Index}, Fate {Id}", fateIndex.Value, fateId.Value);
        this.ShowFateById(fateId.Value);
    }

    private static unsafe int? GetRelicEnemyIndex(AddonRelicNoteBook* addon, AtkEventTarget* target)
    {
        if (IsClickOnSlot(target, addon->Enemy0)) return 0;
        if (IsClickOnSlot(target, addon->Enemy1)) return 1;
        if (IsClickOnSlot(target, addon->Enemy2)) return 2;
        if (IsClickOnSlot(target, addon->Enemy3)) return 3;
        if (IsClickOnSlot(target, addon->Enemy4)) return 4;
        if (IsClickOnSlot(target, addon->Enemy5)) return 5;
        if (IsClickOnSlot(target, addon->Enemy6)) return 6;
        if (IsClickOnSlot(target, addon->Enemy7)) return 7;
        if (IsClickOnSlot(target, addon->Enemy8)) return 8;
        if (IsClickOnSlot(target, addon->Enemy9)) return 9;
        return null;
    }

    private static unsafe int? GetRelicFateIndex(AddonRelicNoteBook* addon, AtkEventTarget* target)
    {
        if (IsClickOnSlot(target, addon->Fate0)) return 0;
        if (IsClickOnSlot(target, addon->Fate1)) return 1;
        if (IsClickOnSlot(target, addon->Fate2)) return 2;
        return null;
    }

    private static unsafe bool IsUnderNode(AtkEventTarget* target, AtkResNode* ancestor)
    {
        if (ancestor == null || target == null)
        {
            return false;
        }

        var node = (AtkResNode*)target;
        while (node != null)
        {
            if (node == ancestor)
            {
                return true;
            }

            node = node->ParentNode;
        }

        return false;
    }

    private static unsafe bool IsClickOnSlot(AtkEventTarget* target, AddonRelicNoteBook.TargetNode slot)
    {
        if (target == null)
        {
            return false;
        }

        if (IsOwnerNode(target, slot.CheckBox))
        {
            return true;
        }

        var node = (AtkResNode*)target;
        while (node != null)
        {
            if (slot.ResNode != null && node == slot.ResNode)
            {
                return true;
            }

            if (slot.CheckBox != null && node == (AtkResNode*)slot.CheckBox->AtkComponentButton.OwnerNode)
            {
                return true;
            }

            if (slot.ImageNode != null && node == (AtkResNode*)slot.ImageNode)
            {
                return true;
            }

            if (slot.CounterTextNode != null && node == (AtkResNode*)slot.CounterTextNode)
            {
                return true;
            }

            node = node->ParentNode;
        }

        return false;
    }

    private unsafe uint? GetRelicEnemyBNpcNameId(int enemyIndex)
    {
        var state = RelicNoteState.Instance();
        if (state == null)
        {
            return null;
        }

        var sheet = this.dataManager.GetExcelSheet<RelicNote>();
        if (!sheet.TryGetRow(state->RelicNoteId, out var book))
        {
            return null;
        }

        if (enemyIndex < 0 || enemyIndex >= book.MonsterNoteTargetCommon.Count)
        {
            return null;
        }

        var targetRef = book.MonsterNoteTargetCommon[enemyIndex];
        if (!targetRef.IsValid)
        {
            return null;
        }

        return targetRef.Value.BNpcName.RowId;
    }

    private unsafe uint? GetRelicFateId(int fateIndex)
    {
        var state = RelicNoteState.Instance();
        if (state == null)
        {
            return null;
        }

        var sheet = this.dataManager.GetExcelSheet<RelicNote>();
        if (!sheet.TryGetRow(state->RelicNoteId, out var book))
        {
            return null;
        }

        if (fateIndex < 0 || fateIndex >= book.Fate.Count)
        {
            return null;
        }

        var fateRef = book.Fate[fateIndex];
        return fateRef.IsValid ? fateRef.RowId : null;
    }

    private void OnFrameworkUpdate(IFramework _)
    {
        try
        {
            if (!this.config.HuntLogClicks || ImGui.GetIO().WantCaptureMouse)
            {
                return;
            }

            var (x, y, pressed) = ReadGameCursor();
            if (!pressed)
            {
                return;
            }

            unsafe
            {
                var addon = GetHuntLogAddon();
                if (addon == null)
                {
                    return;
                }

                this.TryShowHuntLogAtMouse(addon, x, y);
            }
        }
        catch (Exception ex)
        {
            this.log.Error(ex, "Hunt log click hit-test failed");
        }
    }

    private static (int X, int Y, bool LeftPressed) ReadGameCursor()
    {
        unsafe
        {
            var input = UIInputData.Instance();
            if (input == null)
            {
                return (0, 0, false);
            }

            var cursor = input->CursorInputs;
            return (
                cursor.PositionX,
                cursor.PositionY,
                cursor.MouseButtonPressedFlags.HasFlag(MouseButtonFlags.LBUTTON));
        }
    }

    private static unsafe AtkUnitBase* GetHuntLogAddon()
    {
        var agent = AgentMonsterNote.Instance();
        if (agent == null || !agent->IsAddonShown())
        {
            return null;
        }

        var addonId = agent->GetAddonId();
        if (addonId == 0)
        {
            return null;
        }

        var manager = RaptureAtkUnitManager.Instance();
        if (manager == null)
        {
            return null;
        }

        var addon = manager->GetAddonById((ushort)addonId);
        return addon == null || !addon->IsReady ? null : addon;
    }

    private unsafe bool TryShowHuntLogAtMouse(AtkUnitBase* addon, int x, int y)
    {
        var names = this.CollectHuntLogNames();
        if (names.Count == 0)
        {
            return false;
        }

        uint? bestId = null;
        string? bestText = null;
        var bestArea = int.MaxValue;

        unsafe void Consider(AtkResNode* node)
        {
            if (node->Type != NodeType.Text || !node->IsVisible() || !AtkNodeUtil.ContainsPoint(node, x, y))
            {
                return;
            }

            var text = AtkNodeUtil.CleanText(((AtkTextNode*)node)->NodeText.ToString());
            if (text == null || !this.NameMatchesSet(text, names))
            {
                return;
            }

            var area = node->Width * node->Height;
            if (area >= bestArea)
            {
                return;
            }

            var id = this.MatchHuntLogName(text);
            if (id == null)
            {
                return;
            }

            bestId = id;
            bestText = text;
            bestArea = area;
        }

        AtkNodeUtil.ForEachNode(addon, Consider);
        return bestId != null && bestText != null && this.ShowHuntLogMatch(bestId.Value, bestText);
    }

    private unsafe uint? MatchHuntLogName(string text)
    {
        var agent = AgentMonsterNote.Instance();
        return agent == null ? null : this.FindHuntLogBNpcNameId(agent, text, eventParam: -1, AtkEventType.MouseClick);
    }

    private bool ShowHuntLogMatch(uint bNpcNameId, string text)
    {
        if (!this.ShouldAcceptClick())
        {
            return true;
        }

        this.log.Info("Hunt log click: BNpcName {Id} text '{Text}'", bNpcNameId, text);
        this.ShowEnemyByBNpcNameId(bNpcNameId);
        return true;
    }

    private void OnOtherBookReady(AddonEvent type, AddonArgs args)
    {
        try
        {
            this.BindNamedNodes(args, this.otherBookHandles, this.GetBnpcNames(), this.OnOtherBookNodeEvent);
        }
        catch (Exception ex)
        {
            this.log.Error(ex, "Field record click binding failed");
        }
    }

    private void OnOtherBookClose(AddonEvent type, AddonArgs args) => this.ClearHandles(this.otherBookHandles);

    private unsafe void BindNamedNodes(AddonArgs args, List<IAddonEventHandle> handles, HashSet<string>? names, IAddonEventManager.AddonEventDelegate handler)
    {
        this.ClearHandles(handles);

        var addon = (AtkUnitBase*)args.Addon.Address;
        if (addon == null)
        {
            return;
        }

        var bound = new HashSet<nint>();
        AtkNodeUtil.ForEachNode(addon, node =>
        {
            if (node->Type != NodeType.Text)
            {
                return;
            }

            var text = AtkNodeUtil.CleanText(((AtkTextNode*)node)->NodeText.ToString());
            if (text == null || (names != null && !this.NameMatchesSet(text, names)))
            {
                return;
            }

            this.BindNode(addon, node, handles, bound, handler);
        });

        this.log.Info("Bound {Count} clickable nodes on hunt/book UI", bound.Count);
    }

    private unsafe void BindNode(
        AtkUnitBase* addon,
        AtkResNode* node,
        List<IAddonEventHandle> handles,
        HashSet<nint> bound,
        IAddonEventManager.AddonEventDelegate handler)
    {
        var ptr = (nint)node;
        if (node == null || !bound.Add(ptr))
        {
            return;
        }

        AtkNodeUtil.MakeClickable(node);
        AddHandle(handles, this.addonEvents.AddEvent((nint)addon, ptr, AddonEventType.MouseOver, handler));
        AddHandle(handles, this.addonEvents.AddEvent((nint)addon, ptr, AddonEventType.MouseOut, handler));
        AddHandle(handles, this.addonEvents.AddEvent((nint)addon, ptr, AddonEventType.MouseClick, handler));
    }

    private static void AddHandle(List<IAddonEventHandle> handles, IAddonEventHandle? handle)
    {
        if (handle != null)
        {
            handles.Add(handle);
        }
    }

    private void ClearHandles(List<IAddonEventHandle> handles)
    {
        foreach (var handle in handles)
        {
            this.addonEvents.RemoveEvent(handle);
        }

        handles.Clear();
    }

    private unsafe void OnOtherBookNodeEvent(AddonEventType type, AddonEventData data)
    {
        this.HandleBoundPointer(type, data, enabled: this.config.OtherBookClicks, ptr => this.TryShowNamedEnemy((AtkResNode*)ptr));
    }

    private void HandleBoundPointer(AddonEventType type, AddonEventData data, bool enabled, Func<nint, bool> onClick)
    {
        switch (type)
        {
            case AddonEventType.MouseOver:
                if (enabled)
                {
                    this.addonEvents.SetCursor(AddonCursorType.Clickable);
                }

                break;
            case AddonEventType.MouseOut:
                this.addonEvents.ResetCursor();
                break;
            case AddonEventType.MouseClick:
                if (!enabled)
                {
                    return;
                }

                onClick(data.NodeTargetPointer);
                break;
        }
    }

    private unsafe bool TryShowNamedEnemy(AtkResNode* node)
    {
        var names = this.GetBnpcNames();
        foreach (var text in AtkNodeUtil.CollectTexts(node))
        {
            if (!this.NameMatchesSet(text, names))
            {
                continue;
            }

            if (!this.ShouldAcceptClick())
            {
                return true;
            }

            this.log.Info("Book click: '{Text}'", text);
            this.ShowEnemyByName(text);
            return true;
        }

        return false;
    }

    private bool ShouldAcceptClick()
    {
        var now = Environment.TickCount64;
        if (now - this.lastClickTicks < 250)
        {
            return false;
        }

        this.lastClickTicks = now;
        return true;
    }

    private unsafe HashSet<string> CollectHuntLogNames()
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var agent = AgentMonsterNote.Instance();
        if (agent == null)
        {
            return names;
        }

        var sheet = this.dataManager.GetExcelSheet<MonsterNote>();
        for (var noteIndex = 0; noteIndex < 10; noteIndex++)
        {
            var noteId = agent->GetMonsterNoteIdForIndex(noteIndex);
            if (!sheet.TryGetRow(noteId, out var note))
            {
                continue;
            }

            for (var i = 0; i < note.MonsterNoteTarget.Count; i++)
            {
                var bNpcNameId = this.GetNoteTargetId(note, i);
                if (bNpcNameId == null)
                {
                    continue;
                }

                var name = this.spawnLocations.GetBNpcName(bNpcNameId.Value);
                if (!string.IsNullOrWhiteSpace(name))
                {
                    names.Add(name);
                }
            }
        }

        return names;
    }

    private HashSet<string> GetBnpcNames()
    {
        if (this.bnpcNames != null)
        {
            return this.bnpcNames;
        }

        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var row in this.dataManager.GetExcelSheet<BNpcName>())
        {
            var name = row.Singular.ExtractText();
            if (!string.IsNullOrWhiteSpace(name) && name.Trim().Length >= 3)
            {
                names.Add(name.Trim());
            }
        }

        this.bnpcNames = names;
        return names;
    }

    private bool NameMatchesSet(string text, HashSet<string> names)
    {
        if (names.Contains(text))
        {
            return true;
        }

        foreach (var name in names)
        {
            if (NamePrefixed(text, name))
            {
                return true;
            }
        }

        return false;
    }

    private unsafe uint? FindHuntLogBNpcNameId(AgentMonsterNote* agent, string? clickedText, int eventParam, AtkEventType atkType)
    {
        var sheet = this.dataManager.GetExcelSheet<MonsterNote>();

        if (!string.IsNullOrWhiteSpace(clickedText))
        {
            for (var noteIndex = 0; noteIndex < 10; noteIndex++)
            {
                var noteId = agent->GetMonsterNoteIdForIndex(noteIndex);
                if (!sheet.TryGetRow(noteId, out var note))
                {
                    continue;
                }

                var match = this.MatchNoteTarget(note, clickedText);
                if (match != null)
                {
                    return match;
                }
            }
        }

        if (string.IsNullOrWhiteSpace(clickedText) && atkType is AtkEventType.ButtonClick && eventParam is >= 0 and < 4)
        {
            var noteId = agent->GetMonsterNoteIdForIndex(agent->MonsterNote);
            if (sheet.TryGetRow(noteId, out var note))
            {
                return this.GetNoteTargetId(note, eventParam);
            }
        }

        return null;
    }

    private uint? MatchNoteTarget(MonsterNote note, string clickedText)
    {
        uint? startsWithMatch = null;
        for (var i = 0; i < note.MonsterNoteTarget.Count; i++)
        {
            var bNpcNameId = this.GetNoteTargetId(note, i);
            if (bNpcNameId == null)
            {
                continue;
            }

            var name = this.spawnLocations.GetBNpcName(bNpcNameId.Value);
            if (string.IsNullOrWhiteSpace(name))
            {
                continue;
            }

            if (clickedText.Equals(name, StringComparison.OrdinalIgnoreCase) ||
                StripArticle(clickedText).Equals(name, StringComparison.OrdinalIgnoreCase) ||
                clickedText.Equals(StripArticle(name), StringComparison.OrdinalIgnoreCase))
            {
                return bNpcNameId;
            }

            if (startsWithMatch == null && NamePrefixed(clickedText, name))
            {
                startsWithMatch = bNpcNameId;
            }
        }

        return startsWithMatch;
    }

    private uint? GetNoteTargetId(MonsterNote note, int index)
    {
        if (index < 0 || index >= note.MonsterNoteTarget.Count)
        {
            return null;
        }

        var targetRef = note.MonsterNoteTarget[index];
        if (!targetRef.IsValid)
        {
            return null;
        }

        var id = targetRef.Value.BNpcName.RowId;
        return id == 0 ? null : id;
    }

    private static bool NamePrefixed(string clickedText, string enemyName)
    {
        clickedText = StripArticle(clickedText);
        enemyName = StripArticle(enemyName);
        if (!clickedText.StartsWith(enemyName, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return clickedText.Length == enemyName.Length || char.IsWhiteSpace(clickedText[enemyName.Length]);
    }

    private static string StripArticle(string text)
        => text.StartsWith("the ", StringComparison.OrdinalIgnoreCase) ? text[4..].Trim() : text;

    private static unsafe bool IsOwnerNode(AtkEventTarget* target, AtkComponentCheckBox* checkbox)
        => checkbox != null && target == checkbox->AtkComponentButton.OwnerNode;

    private sealed record FateChainStep(string Name, SpawnLocation? Location);

    private sealed record DutyChoice(string Label, SpawnLocation Location);
}
