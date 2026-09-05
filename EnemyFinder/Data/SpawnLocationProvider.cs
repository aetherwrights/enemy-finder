using System.Globalization;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.RegularExpressions;
using Dalamud.Plugin.Services;
using Lumina.Excel;
using Lumina.Excel.Sheets;
using Lumina.Text.ReadOnly;

namespace EnemyFinder.Data;

public sealed class SpawnLocationProvider : IDisposable
{
    private const float DefaultRadiusYalms = 40f;

    private static readonly Regex HtmlComment = new(@"<!--.*?-->", RegexOptions.Singleline | RegexOptions.CultureInvariant);
    private static readonly Regex ConsoleGamesLocation = new(
        @"\{\{\s*NPC location info\|(?<zone>[^|]+)\|(?<coords>[^|]+)\|(?<level>[^}]*)\}\}",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex ConsoleGamesInfoboxLocation = new(
        @"\|\s*location\s*=\s*(.+)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex ConsoleGamesInfoboxCoords = new(
        @"\|\s*coordinates\s*=\s*(.+)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex GamerEscapeMobRow = new(
        @"\{\{\s*ARR Mob Row\b(?<body>[\s\S]*?)\}\}",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex GamerEscapeField = new(
        @"\|\s*(?<key>Coordinates|Location|FATE|Quest|Levels)\s*=\s*(?<value>[^\n|]*)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex CoordPair = new(
        @"(?:X\s*:\s*)?(?<x>\d+(?:\.\d+)?)\s*[,;\-]\s*(?:Y\s*:\s*)?(?<y>\d+(?:\.\d+)?)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex LeadingLevel = new(
        @"^(?<level>\d+)",
        RegexOptions.CultureInvariant);
    private static readonly Regex GamerEscapeFateLocation = new(
        @"\|\s*Location(?<n>\s+\d+)?\s*=\s*(?<value>[^\n|]+)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex GamerEscapeFateCoords = new(
        @"\|\s*(?:Location(?<n>\s+\d+)\s+)?Coordinates\s*=\s*(?<value>[^\n|]+)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex ConsoleGamesFateX = new(
        @"\|\s*location-x\s*=\s*(?<x>\d+(?:\.\d+)?)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex ConsoleGamesFateY = new(
        @"\|\s*location-y\s*=\s*(?<y>\d+(?:\.\d+)?)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex ConsoleGamesPrevFate = new(
        @"\|\s*prev(?:ious)?[-_\s]+fate\s*=[ \t]*(?<value>[^\n|]*)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex GamerEscapeRequiredStatus = new(
        @"\|\s*Required Status\s*=[ \t]*(?<value>[^\n|]*)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex WikiLinkName = new(
        @"\[\[(?<name>[^\]|#]+)",
        RegexOptions.CultureInvariant);
    private static readonly Regex ItemTemplateName = new(
        @"\{\{\s*i\s*\|\s*(?<name>[^}|]+)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private readonly PluginConfig config;
    private readonly IDataManager dataManager;
    private readonly IPluginLog log;
    private readonly HttpClient httpClient;
    private readonly Dictionary<string, SpawnLocation> cache = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, EnemySpawnOptions> enemyCache = new(StringComparer.OrdinalIgnoreCase);
    private readonly LinkedList<string> cacheOrder = new();
    private readonly object cacheLock = new();

    public SpawnLocationProvider(IDataManager dataManager, IPluginLog log, PluginConfig config)
    {
        this.dataManager = dataManager;
        this.log = log;
        this.config = config;
        this.httpClient = new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(10),
        };
        this.httpClient.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("EnemyFinder", "0.1.1"));
        this.httpClient.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
    }

    public async Task<SpawnLocation> GetLocationAsync(string enemyName, bool includeFateCamps, CancellationToken cancellationToken = default)
    {
        var options = await this.GetEnemyOptionsAsync(enemyName, includeFateCamps, cancellationToken).ConfigureAwait(false);
        return options.Preferred;
    }

    public async Task<EnemySpawnOptions> GetEnemyOptionsAsync(string enemyName, bool includeFateCamps, CancellationToken cancellationToken = default)
    {
        var cacheKey = $"{enemyName.Trim().ToLowerInvariant()}|{(includeFateCamps ? "fate" : "standing")}";
        if (this.TryGetEnemyCached(cacheKey, out var cached))
        {
            this.log.Verbose("Spawn cache hit for {Enemy}", enemyName);
            return cached;
        }

        var options = await this.LookupUncachedEnemyAsync(enemyName, includeFateCamps, cancellationToken).ConfigureAwait(false);
        if (this.config.ClampedWikiCacheSize > 0)
        {
            this.RememberEnemy(cacheKey, options);
        }

        return options;
    }

    public int CachedCount
    {
        get
        {
            lock (this.cacheLock)
            {
                return this.cache.Count + this.enemyCache.Count;
            }
        }
    }

    public void ClearCache()
    {
        lock (this.cacheLock)
        {
            this.cache.Clear();
            this.enemyCache.Clear();
            this.cacheOrder.Clear();
        }

        this.log.Info("Spawn lookup cache cleared");
    }

    public void TrimCacheToLimit()
    {
        lock (this.cacheLock)
        {
            this.EvictOverflowLocked();
        }
    }

    public async Task<EnemySpawnOptions> GetEnemyOptionsAsync(uint bNpcNameId, bool includeFateCamps, CancellationToken cancellationToken = default)
    {
        var name = this.GetBNpcName(bNpcNameId) ?? $"BNpcName#{bNpcNameId}";
        return await this.GetEnemyOptionsAsync(name, includeFateCamps, cancellationToken).ConfigureAwait(false);
    }

    public async Task<SpawnLocation> GetLocationAsync(uint bNpcNameId, bool includeFateCamps, CancellationToken cancellationToken = default)
    {
        var options = await this.GetEnemyOptionsAsync(bNpcNameId, includeFateCamps, cancellationToken).ConfigureAwait(false);
        return options.Preferred;
    }

    public async Task<SpawnLocation> GetFateLocationAsync(uint fateId, CancellationToken cancellationToken = default)
    {
        var name = this.GetFateName(fateId) ?? $"FATE#{fateId}";
        return await this.GetFateLocationAsync(name, cancellationToken).ConfigureAwait(false);
    }

    public async Task<SpawnLocation> GetFateLocationAsync(string fateName, CancellationToken cancellationToken = default)
    {
        var cacheKey = $"fate|{fateName.Trim().ToLowerInvariant()}";
        if (this.TryGetCached(cacheKey, out var cached))
        {
            this.log.Verbose("Spawn cache hit for FATE {Fate}", fateName);
            return cached;
        }

        var location = await this.LookupUncachedFateAsync(fateName, cancellationToken).ConfigureAwait(false);
        if (this.config.ClampedWikiCacheSize > 0)
        {
            this.Remember(cacheKey, location);
        }

        return location;
    }

    private async Task<EnemySpawnOptions> LookupUncachedEnemyAsync(string enemyName, bool includeFateCamps, CancellationToken cancellationToken)
    {
        var gamerEscapeTask = this.FetchGamerEscapeSpawnsAsync(enemyName, cancellationToken);
        var consoleGamesTask = this.FetchConsoleGamesSpawnsAsync(enemyName, cancellationToken);

        var gamerEscape = await gamerEscapeTask.ConfigureAwait(false);
        var consoleGames = await consoleGamesTask.ConfigureAwait(false);
        MarkMatchingFates(consoleGames, gamerEscape);

        var standing = gamerEscape.Where(spawn => !spawn.IsFate)
            .Concat(consoleGames.Where(spawn => !spawn.IsFate))
            .ToList();
        var options = this.BuildEnemyOptions(enemyName, standing);
        if (!options.HasChoice && options.Overworld == null && options.Duties.Count == 0 && includeFateCamps)
        {
            var fateSpawns = gamerEscape.Count > 0 ? gamerEscape : consoleGames;
            var fateSource = gamerEscape.Count > 0 ? "Gamer Escape (FATE)" : "Console Games Wiki";
            this.log.Info("No overworld spawn for {Enemy}; using FATE locations from {Source}", enemyName, fateSource);
            var fateLocation = this.SelectSpawn(enemyName, fateSpawns, fateSource);
            if (fateLocation != null)
            {
                options = this.IsOverworldTerritory(fateLocation.TerritoryTypeId)
                    ? new EnemySpawnOptions(fateLocation, [])
                    : new EnemySpawnOptions(null, [fateLocation]);
            }
        }
        else if (includeFateCamps)
        {
            var extra = gamerEscape.Concat(consoleGames).Where(spawn => spawn.IsFate);
            options = new EnemySpawnOptions(
                options.Overworld == null ? null : this.AddSameMapCamps(options.Overworld, extra),
                options.Duties.Select(duty => this.AddSameMapCamps(duty, extra)).ToList());
        }

        if (options.Overworld == null && options.Duties.Count == 0)
        {
            throw new InvalidOperationException($"No spawn location found for '{enemyName}' on Console Games Wiki or Gamer Escape.");
        }

        if (options.Overworld != null)
        {
            this.LogResolved(options.Overworld);
        }

        foreach (var duty in options.Duties)
        {
            this.LogResolved(duty);
        }

        return options;
    }

    private async Task<SpawnLocation> LookupUncachedFateAsync(string fateName, CancellationToken cancellationToken)
    {
        var gamerEscapeTask = this.FetchGamerEscapeFateAsync(fateName, cancellationToken);
        var consoleGamesTask = this.FetchConsoleGamesFateAsync(fateName, cancellationToken);

        var gamerEscape = await gamerEscapeTask.ConfigureAwait(false);
        var consoleGames = await consoleGamesTask.ConfigureAwait(false);

        // Console Games Wiki's prev-fate field is the structured prerequisite; prefer it.
        var prerequisites = consoleGames.Prerequisites.Count > 0
            ? consoleGames.Prerequisites
            : gamerEscape.Prerequisites;
        var spawns = gamerEscape.Spawns.Count > 0 ? gamerEscape.Spawns : consoleGames.Spawns;
        var source = gamerEscape.Spawns.Count > 0 ? "Gamer Escape" : "Console Games Wiki";

        var location = this.SelectSpawn(fateName, spawns, source);
        if (location == null)
        {
            throw new InvalidOperationException($"No FATE location found for '{fateName}' on Console Games Wiki or Gamer Escape.");
        }

        if (prerequisites.Count > 0)
        {
            location = location with
            {
                Prerequisites = prerequisites
                    .Where(name => !name.Equals(fateName, StringComparison.OrdinalIgnoreCase))
                    .ToList(),
            };
        }

        this.LogResolved(location);
        return location;
    }

    private async Task<FateWikiParse> FetchGamerEscapeFateAsync(string fateName, CancellationToken cancellationToken)
    {
        try
        {
            var text = await this.TryFetchGamerEscapeAsync(fateName, cancellationToken, preferEnemyTitle: false).ConfigureAwait(false);
            return string.IsNullOrWhiteSpace(text) ? FateWikiParse.Empty : TagFateSource(ParseGamerEscapeFate(text), "Gamer Escape");
        }
        catch (Exception ex)
        {
            this.log.Warning(ex, "Gamer Escape FATE lookup failed for {Fate}", fateName);
            return FateWikiParse.Empty;
        }
    }

    private async Task<FateWikiParse> FetchConsoleGamesFateAsync(string fateName, CancellationToken cancellationToken)
    {
        try
        {
            var text = await this.TryFetchConsoleGamesWikiAsync(fateName, cancellationToken, preferEnemyTitle: false).ConfigureAwait(false);
            return string.IsNullOrWhiteSpace(text) ? FateWikiParse.Empty : TagFateSource(ParseConsoleGamesFate(text), "Console Games Wiki");
        }
        catch (Exception ex)
        {
            this.log.Warning(ex, "Console Games Wiki FATE lookup failed for {Fate}", fateName);
            return FateWikiParse.Empty;
        }
    }

    private async Task<List<RawSpawn>> FetchGamerEscapeSpawnsAsync(string enemyName, CancellationToken cancellationToken)
    {
        try
        {
            var text = await this.TryFetchGamerEscapeAsync(enemyName, cancellationToken).ConfigureAwait(false);
            return string.IsNullOrWhiteSpace(text) ? [] : TagSource(ParseGamerEscape(text), "Gamer Escape");
        }
        catch (Exception ex)
        {
            this.log.Warning(ex, "Gamer Escape lookup failed for {Enemy}", enemyName);
            return [];
        }
    }

    private async Task<List<RawSpawn>> FetchConsoleGamesSpawnsAsync(string enemyName, CancellationToken cancellationToken)
    {
        try
        {
            var text = await this.TryFetchConsoleGamesWikiAsync(enemyName, cancellationToken).ConfigureAwait(false);
            return string.IsNullOrWhiteSpace(text) ? [] : TagSource(ParseConsoleGamesWiki(text), "Console Games Wiki");
        }
        catch (Exception ex)
        {
            this.log.Warning(ex, "Console Games Wiki lookup failed for {Enemy}", enemyName);
            return [];
        }
    }

    private void LogResolved(SpawnLocation location)
    {
        this.log.Info(
            "Resolved {Enemy} via {Source} at {Camps} camp(s) on territory {Territory} map {Map} (first {X:0.0}, {Y:0.0})",
            location.Name, location.Source, location.Camps.Count, location.TerritoryTypeId, location.MapId,
            location.MapX, location.MapY);
    }

    private bool TryGetEnemyCached(string key, out EnemySpawnOptions options)
    {
        lock (this.cacheLock)
        {
            if (this.enemyCache.TryGetValue(key, out options!))
            {
                this.cacheOrder.Remove(key);
                this.cacheOrder.AddFirst(key);
                return true;
            }

            options = null!;
            return false;
        }
    }

    private void RememberEnemy(string key, EnemySpawnOptions options)
    {
        lock (this.cacheLock)
        {
            if (this.enemyCache.ContainsKey(key) || this.cache.ContainsKey(key))
            {
                this.cacheOrder.Remove(key);
            }

            this.cache.Remove(key);
            this.enemyCache[key] = options;
            this.cacheOrder.AddFirst(key);
            this.EvictOverflowLocked();
        }
    }

    private bool TryGetCached(string key, out SpawnLocation location)
    {
        lock (this.cacheLock)
        {
            if (this.cache.TryGetValue(key, out location!))
            {
                this.cacheOrder.Remove(key);
                this.cacheOrder.AddFirst(key);
                return true;
            }

            location = null!;
            return false;
        }
    }

    private void Remember(string key, SpawnLocation location)
    {
        lock (this.cacheLock)
        {
            if (this.cache.ContainsKey(key))
            {
                this.cacheOrder.Remove(key);
            }

            this.cache[key] = location;
            this.cacheOrder.AddFirst(key);
            this.EvictOverflowLocked();
        }
    }

    private void EvictOverflowLocked()
    {
        var limit = this.config.ClampedWikiCacheSize;
        if (limit <= 0)
        {
            this.cache.Clear();
            this.enemyCache.Clear();
            this.cacheOrder.Clear();
            return;
        }

        while (this.cache.Count + this.enemyCache.Count > limit && this.cacheOrder.Last != null)
        {
            var evict = this.cacheOrder.Last.Value;
            this.cacheOrder.RemoveLast();
            this.cache.Remove(evict);
            this.enemyCache.Remove(evict);
        }
    }

    private async Task<string?> TryFetchConsoleGamesWikiAsync(string pageName, CancellationToken cancellationToken, bool preferEnemyTitle = true)
        => await this.FetchMediaWikiWikitextAsync(
            "https://ffxiv.consolegameswiki.com/mediawiki/api.php",
            pageName,
            cancellationToken,
            preferEnemyTitle).ConfigureAwait(false);

    private async Task<string?> TryFetchGamerEscapeAsync(string pageName, CancellationToken cancellationToken, bool preferEnemyTitle = true)
        => await this.FetchMediaWikiWikitextAsync(
            "https://ffxiv.gamerescape.com/w/api.php",
            pageName,
            cancellationToken,
            preferEnemyTitle).ConfigureAwait(false);

    private async Task<string?> FetchMediaWikiWikitextAsync(string apiUrl, string pageName, CancellationToken cancellationToken, bool preferEnemyTitle)
    {
        foreach (var title in CandidateTitles(pageName, preferEnemyTitle))
        {
            var text = await this.ParseWikiPageAsync(apiUrl, title, cancellationToken).ConfigureAwait(false);
            if (!string.IsNullOrWhiteSpace(text))
            {
                return text;
            }
        }

        var searched = await this.SearchWikiTitleAsync(apiUrl, pageName, cancellationToken, preferEnemyTitle).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(searched))
        {
            return null;
        }

        return await this.ParseWikiPageAsync(apiUrl, searched, cancellationToken).ConfigureAwait(false);
    }

    private async Task<string?> ParseWikiPageAsync(string apiUrl, string pageTitle, CancellationToken cancellationToken)
    {
        var url =
            $"{apiUrl}?action=parse&page={Uri.EscapeDataString(pageTitle)}&prop=wikitext&redirects=1&format=json";
        using var response = await this.httpClient.GetAsync(url, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            return null;
        }

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken).ConfigureAwait(false);
        var root = doc.RootElement;
        if (root.TryGetProperty("error", out _))
        {
            return null;
        }

        if (!root.TryGetProperty("parse", out var parse) ||
            !parse.TryGetProperty("wikitext", out var wikitext) ||
            !wikitext.TryGetProperty("*", out var text))
        {
            return null;
        }

        if (parse.TryGetProperty("title", out var titleElement))
        {
            var resolvedTitle = titleElement.GetString();
            if (!string.IsNullOrEmpty(resolvedTitle) &&
                resolvedTitle.StartsWith("Category:", StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }
        }

        return text.GetString();
    }

    private async Task<string?> SearchWikiTitleAsync(string apiUrl, string enemyName, CancellationToken cancellationToken, bool preferEnemyTitle = true)
    {
        var url =
            $"{apiUrl}?action=query&list=search&srsearch={Uri.EscapeDataString(enemyName)}&srnamespace=0&srlimit=5&format=json";
        using var response = await this.httpClient.GetAsync(url, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            return null;
        }

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken).ConfigureAwait(false);
        if (!doc.RootElement.TryGetProperty("query", out var query) ||
            !query.TryGetProperty("search", out var results) ||
            results.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        string? enemyTitle = null;
        string? prefix = null;
        string? fuzzy = null;
        foreach (var hit in results.EnumerateArray())
        {
            if (!hit.TryGetProperty("title", out var titleElement))
            {
                continue;
            }

            var title = titleElement.GetString();
            if (string.IsNullOrWhiteSpace(title))
            {
                continue;
            }

            if (title.Equals(enemyName, StringComparison.OrdinalIgnoreCase))
            {
                return title;
            }

            if (preferEnemyTitle &&
                enemyTitle == null &&
                title.EndsWith("(Enemy)", StringComparison.OrdinalIgnoreCase) &&
                AllWordsPresent(enemyName, title))
            {
                enemyTitle = title;
                continue;
            }

            if (prefix == null && TitlePrefixedBy(title, enemyName))
            {
                prefix = title;
                continue;
            }

            fuzzy ??= AllWordsPresent(enemyName, title) ? title : null;
        }

        return enemyTitle ?? prefix ?? fuzzy;
    }

    private static IEnumerable<string> CandidateTitles(string enemyName, bool preferEnemyTitle = true)
    {
        var trimmed = enemyName.Trim();
        if (trimmed.Length == 0)
        {
            yield break;
        }

        yield return trimmed;
        var titled = CultureInfo.InvariantCulture.TextInfo.ToTitleCase(trimmed.ToLowerInvariant());
        if (!titled.Equals(trimmed, StringComparison.Ordinal))
        {
            yield return titled;
        }
        if (preferEnemyTitle && !titled.EndsWith("(Enemy)", StringComparison.OrdinalIgnoreCase))
        {
            yield return titled + " (Enemy)";
        }
    }

    private static bool TitlePrefixedBy(string title, string name)
    {
        if (!title.StartsWith(name, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return title.Length == name.Length ||
               char.IsWhiteSpace(title[name.Length]) ||
               title[name.Length] == '(';
    }

    private static bool AllWordsPresent(string query, string title)
    {
        var words = query.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return words.Length > 0 && words.All(word => title.Contains(word, StringComparison.OrdinalIgnoreCase));
    }

    private static List<RawSpawn> ParseConsoleGamesWiki(string wikitext)
    {
        wikitext = HtmlComment.Replace(wikitext, string.Empty);
        var results = new List<RawSpawn>();

        foreach (Match match in ConsoleGamesLocation.Matches(wikitext))
        {
            var zone = match.Groups["zone"].Value.Trim();
            var level = ParseMinLevel(match.Groups["level"].Value);
            foreach (var (x, y) in ParseCoordList(match.Groups["coords"].Value))
            {
                results.Add(new RawSpawn(zone, x, y, false, level));
            }
        }

        if (results.Count > 0)
        {
            return results;
        }

        var infoboxZone = FirstGroup(ConsoleGamesInfoboxLocation, wikitext);
        var infoboxCoords = FirstGroup(ConsoleGamesInfoboxCoords, wikitext);
        if (infoboxZone != null && infoboxCoords != null)
        {
            foreach (var (x, y) in ParseCoordList(infoboxCoords))
            {
                results.Add(new RawSpawn(infoboxZone.Trim(), x, y, false, 0));
            }
        }

        return results;
    }

    private static FateWikiParse ParseConsoleGamesFate(string wikitext)
    {
        wikitext = HtmlComment.Replace(wikitext, string.Empty);
        var results = new List<RawSpawn>();
        var zone = FirstGroup(ConsoleGamesInfoboxLocation, wikitext);
        var xMatch = ConsoleGamesFateX.Match(wikitext);
        var yMatch = ConsoleGamesFateY.Match(wikitext);
        if (zone != null &&
            xMatch.Success &&
            yMatch.Success &&
            float.TryParse(xMatch.Groups["x"].Value, CultureInfo.InvariantCulture, out var x) &&
            float.TryParse(yMatch.Groups["y"].Value, CultureInfo.InvariantCulture, out var y))
        {
            results.Add(new RawSpawn(zone.Trim(), x, y, true, 0));
        }

        return new FateWikiParse(results, ParsePrerequisiteNames(NamedGroup(ConsoleGamesPrevFate, wikitext, "value")));
    }

    private static FateWikiParse ParseGamerEscapeFate(string wikitext)
    {
        wikitext = HtmlComment.Replace(wikitext, string.Empty);
        var locations = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (Match match in GamerEscapeFateLocation.Matches(wikitext))
        {
            var key = FateFieldIndex(match.Groups["n"].Value);
            var zone = match.Groups["value"].Value.Trim();
            if (zone.Length > 0)
            {
                locations[key] = zone;
            }
        }

        var results = new List<RawSpawn>();
        foreach (Match match in GamerEscapeFateCoords.Matches(wikitext))
        {
            var key = FateFieldIndex(match.Groups["n"].Value);
            if (!locations.TryGetValue(key, out var zone))
            {
                continue;
            }

            foreach (var (x, y) in ParseCoordList(match.Groups["value"].Value))
            {
                results.Add(new RawSpawn(zone, x, y, true, 0));
            }
        }

        return new FateWikiParse(results, ParsePrerequisiteNames(NamedGroup(GamerEscapeRequiredStatus, wikitext, "value")));
    }

    private static IReadOnlyList<string> ParsePrerequisiteNames(string? field)
    {
        if (string.IsNullOrWhiteSpace(field))
        {
            return [];
        }

        var names = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (Match match in WikiLinkName.Matches(field))
        {
            AddPrerequisiteName(names, seen, match.Groups["name"].Value);
        }

        foreach (Match match in ItemTemplateName.Matches(field))
        {
            AddPrerequisiteName(names, seen, match.Groups["name"].Value);
        }

        if (names.Count == 0 && IsPlausibleFateName(field.Trim()))
        {
            AddPrerequisiteName(names, seen, field);
        }

        return names;
    }

    private static void AddPrerequisiteName(List<string> names, HashSet<string> seen, string raw)
    {
        var name = WikiLinkName.Replace(raw, "${name}").Trim();
        name = ItemTemplateName.Replace(name, "${name}").Trim();
        name = name.Trim(' ', '.', ',', ';', '"', '\'');
        if (!IsPlausibleFateName(name) || !seen.Add(name))
        {
            return;
        }

        names.Add(name);
    }

    private static bool IsPlausibleFateName(string name)
    {
        if (name.Length < 3 ||
            name.Equals("none", StringComparison.OrdinalIgnoreCase) ||
            name.IndexOfAny(['|', '{', '}', '=', '<', '>', '[', ']']) >= 0)
        {
            return false;
        }

        return name.Any(char.IsLetter);
    }

    private static string FateFieldIndex(string raw)
        => string.IsNullOrWhiteSpace(raw) ? "0" : raw.Trim();

    private static List<RawSpawn> ParseGamerEscape(string wikitext)
    {
        wikitext = HtmlComment.Replace(wikitext, string.Empty);
        var results = new List<RawSpawn>();

        foreach (Match row in GamerEscapeMobRow.Matches(wikitext))
        {
            string? location = null;
            string? coordinates = null;
            string? fate = null;
            string? quest = null;
            string? levels = null;
            foreach (Match field in GamerEscapeField.Matches(row.Groups["body"].Value))
            {
                var value = field.Groups["value"].Value.Trim();
                var key = field.Groups["key"].Value;
                if (key.Equals("Location", StringComparison.OrdinalIgnoreCase))
                {
                    location = value;
                }
                else if (key.Equals("Coordinates", StringComparison.OrdinalIgnoreCase))
                {
                    coordinates = value;
                }
                else if (key.Equals("FATE", StringComparison.OrdinalIgnoreCase))
                {
                    fate = value;
                }
                else if (key.Equals("Quest", StringComparison.OrdinalIgnoreCase))
                {
                    quest = value;
                }
                else if (key.Equals("Levels", StringComparison.OrdinalIgnoreCase))
                {
                    levels = value;
                }
            }

            if (string.IsNullOrWhiteSpace(location) ||
                string.IsNullOrWhiteSpace(coordinates) ||
                !string.IsNullOrWhiteSpace(quest))
            {
                continue;
            }

            var isFate = !string.IsNullOrWhiteSpace(fate);
            var minLevel = levels == null ? 0 : ParseMinLevel(levels);
            foreach (var (x, y) in ParseCoordList(coordinates))
            {
                results.Add(new RawSpawn(location, x, y, isFate, minLevel));
            }
        }

        return results;
    }

    private EnemySpawnOptions BuildEnemyOptions(string enemyName, List<RawSpawn> spawns)
    {
        var groups = this.CollectSpawnGroups(enemyName, spawns);
        var overworld = groups
            .Where(group => group.Overworld)
            .OrderByDescending(group => group.MinLevel)
            .ThenByDescending(group => group.Location.Camps.Count)
            .Select(group => group.Location)
            .FirstOrDefault();
        var duties = groups
            .Where(group => !group.Overworld)
            .OrderByDescending(group => group.MinLevel)
            .ThenByDescending(group => group.Location.Camps.Count)
            .Select(group => group.Location)
            .ToList();
        return new EnemySpawnOptions(overworld, duties);
    }

    private SpawnLocation? SelectSpawn(string enemyName, List<RawSpawn> spawns, string? fallbackSource = null)
    {
        var groups = this.CollectSpawnGroups(enemyName, spawns, fallbackSource);
        if (groups.Count == 0)
        {
            return null;
        }

        var pool = groups.Exists(group => group.Overworld)
            ? groups.FindAll(group => group.Overworld)
            : groups;

        var selected = pool
            .OrderByDescending(group => group.MinLevel)
            .ThenByDescending(group => group.Location.Camps.Count)
            .First();
        return selected.Location;
    }

    private List<SpawnGroup> CollectSpawnGroups(string enemyName, List<RawSpawn> spawns, string? fallbackSource = null)
    {
        var resolved = new List<(uint TerritoryId, uint MapId, float X, float Y, int MinLevel, string Source, bool Overworld)>();
        foreach (var spawn in spawns)
        {
            var sourceName = string.IsNullOrEmpty(spawn.Source) ? fallbackSource ?? "wiki" : spawn.Source;
            if (!this.TryResolveTerritoryByPlaceName(spawn.Place, out var territoryId, out var mapId))
            {
                this.log.Verbose("Could not map place '{Place}' from {Source}", spawn.Place, sourceName);
                continue;
            }

            resolved.Add((territoryId, mapId, spawn.X, spawn.Y, spawn.MinLevel, sourceName, this.IsOverworldTerritory(territoryId)));
        }

        if (resolved.Count == 0)
        {
            return [];
        }

        return resolved
            .GroupBy(entry => (entry.TerritoryId, entry.MapId))
            .Select(group =>
            {
                var points = group
                    .Select(entry => new MapCamp(entry.X, entry.Y))
                    .Distinct()
                    .ToList();
                var location = new SpawnLocation(
                    enemyName,
                    group.Key.TerritoryId,
                    group.Key.MapId,
                    points,
                    DefaultRadiusYalms,
                    group.First().Source);
                return new SpawnGroup(location, group.Max(entry => entry.MinLevel), group.First().Overworld);
            })
            .ToList();
    }

    private bool IsOverworldTerritory(uint territoryId)
    {
        var sheet = this.dataManager.GetExcelSheet<TerritoryType>();
        return sheet.TryGetRow(territoryId, out var territory) && territory.ContentFinderCondition.RowId == 0;
    }

    private SpawnLocation AddSameMapCamps(SpawnLocation location, IEnumerable<RawSpawn> extra)
    {
        var camps = location.Camps.ToList();
        foreach (var spawn in extra)
        {
            if (!this.TryResolveTerritoryByPlaceName(spawn.Place, out var territoryId, out var mapId) ||
                territoryId != location.TerritoryTypeId ||
                mapId != location.MapId)
            {
                continue;
            }

            var camp = new MapCamp(spawn.X, spawn.Y);
            if (!camps.Contains(camp))
            {
                camps.Add(camp);
            }
        }

        return camps.Count == location.Camps.Count
            ? location
            : location with { Camps = camps };
    }

    private bool TryResolveTerritoryByPlaceName(string placeName, out uint territoryId, out uint mapId)
    {
        territoryId = 0;
        mapId = 0;
        if (string.IsNullOrWhiteSpace(placeName))
        {
            return false;
        }

        if (this.TryResolveOverworldTerritory(row => NamesMatch(row.PlaceName.ValueNullable, placeName), out territoryId, out mapId))
        {
            return true;
        }

        var maps = this.dataManager.GetExcelSheet<Map>();
        var markers = this.dataManager.GetSubrowExcelSheet<MapMarker>();
        Map? bestMap = null;
        foreach (var map in maps)
        {
            if (map.TerritoryType.RowId == 0)
            {
                continue;
            }

            if (!NamesMatch(map.PlaceName.ValueNullable, placeName) &&
                !NamesMatch(map.PlaceNameSub.ValueNullable, placeName) &&
                !MapMarkersIncludePlace(markers, map, placeName))
            {
                continue;
            }

            if (bestMap == null || map.RowId < bestMap.Value.RowId)
            {
                bestMap = map;
            }
        }

        if (bestMap == null)
        {
            return false;
        }

        territoryId = bestMap.Value.TerritoryType.RowId;
        mapId = bestMap.Value.RowId;
        return true;
    }

    private bool TryResolveOverworldTerritory(Func<TerritoryType, bool> match, out uint territoryId, out uint mapId)
    {
        territoryId = 0;
        mapId = 0;
        TerritoryType? best = null;
        foreach (var territory in this.dataManager.GetExcelSheet<TerritoryType>())
        {
            if (territory.Map.RowId == 0 || !match(territory))
            {
                continue;
            }

            if (territory.ContentFinderCondition.RowId != 0)
            {
                continue;
            }

            if (best == null || territory.RowId < best.Value.RowId)
            {
                best = territory;
            }
        }

        if (best == null)
        {
            return false;
        }

        territoryId = best.Value.RowId;
        mapId = best.Value.Map.RowId;
        return true;
    }

    private static void MarkMatchingFates(List<RawSpawn> consoleGames, List<RawSpawn> gamerEscape)
    {
        var fateCoords = gamerEscape.Where(spawn => spawn.IsFate).ToList();
        if (fateCoords.Count == 0)
        {
            return;
        }

        for (var i = 0; i < consoleGames.Count; i++)
        {
            var candidate = consoleGames[i];
            if (fateCoords.Any(fate => SameCamp(candidate, fate)))
            {
                consoleGames[i] = candidate with { IsFate = true };
            }
        }
    }

    private static List<RawSpawn> TagSource(List<RawSpawn> spawns, string source)
        => spawns.ConvertAll(spawn => spawn with { Source = source });

    private static FateWikiParse TagFateSource(FateWikiParse parse, string source)
        => parse.Spawns.Count == 0 ? parse : parse with { Spawns = TagSource(parse.Spawns, source) };

    private static bool SameCamp(RawSpawn left, RawSpawn right)
        => MathF.Abs(left.X - right.X) < 0.6f && MathF.Abs(left.Y - right.Y) < 0.6f;

    private static int ParseMinLevel(string text)
    {
        var match = LeadingLevel.Match(text.Trim());
        return match.Success && int.TryParse(match.Groups["level"].Value, CultureInfo.InvariantCulture, out var level)
            ? level
            : 0;
    }

    private static bool MapMarkersIncludePlace(SubrowExcelSheet<MapMarker> markers, Map map, string placeName)
    {
        if (!markers.TryGetRow(map.MapMarkerRange, out var rows))
        {
            return false;
        }

        foreach (var marker in rows)
        {
            if (NamesMatch(marker.PlaceNameSubtext.ValueNullable, placeName))
            {
                return true;
            }
        }

        return false;
    }

    public string? GetBNpcName(uint bNpcNameId)
    {
        var sheet = this.dataManager.GetExcelSheet<BNpcName>();
        if (!sheet.TryGetRow(bNpcNameId, out var row))
        {
            return null;
        }

        var name = row.Singular.ExtractText();
        return string.IsNullOrWhiteSpace(name) ? null : name;
    }

    public string? GetFateName(uint fateId)
    {
        var sheet = this.dataManager.GetExcelSheet<Fate>();
        if (!sheet.TryGetRow(fateId, out var row))
        {
            return null;
        }

        var name = row.Name.ExtractText();
        return string.IsNullOrWhiteSpace(name) ? null : name.Trim();
    }

    private static IEnumerable<(float X, float Y)> ParseCoordList(string text)
    {
        foreach (Match match in CoordPair.Matches(text))
        {
            if (float.TryParse(match.Groups["x"].Value, CultureInfo.InvariantCulture, out var x) &&
                float.TryParse(match.Groups["y"].Value, CultureInfo.InvariantCulture, out var y))
            {
                yield return (x, y);
            }
        }
    }

    private static string? NamedGroup(Regex regex, string text, string groupName)
    {
        var match = regex.Match(text);
        if (!match.Success)
        {
            return null;
        }

        var value = match.Groups[groupName].Value.Trim();
        return value.Length == 0 ? null : value;
    }

    private static string? FirstGroup(Regex regex, string text)
    {
        var match = regex.Match(text);
        return match.Success ? match.Groups[1].Value.Trim() : null;
    }

    private static bool NamesMatch(PlaceName? place, string expected)
    {
        if (place == null)
        {
            return false;
        }

        var name = place.Value.Name.ExtractText();
        var noArticle = place.Value.NameNoArticle.ExtractText();
        return NamesMatch(name, expected) || NamesMatch(noArticle, expected);
    }

    private static bool NamesMatch(string? actual, string expected)
        => !string.IsNullOrWhiteSpace(actual) &&
           actual.Trim().Equals(expected.Trim(), StringComparison.OrdinalIgnoreCase);

    public void Dispose()
    {
        this.ClearCache();
        this.httpClient.Dispose();
    }

    private readonly record struct RawSpawn(string Place, float X, float Y, bool IsFate, int MinLevel, string Source = "");

    private readonly record struct SpawnGroup(SpawnLocation Location, int MinLevel, bool Overworld);

    private readonly record struct FateWikiParse(List<RawSpawn> Spawns, IReadOnlyList<string> Prerequisites)
    {
        public static FateWikiParse Empty { get; } = new([], []);
    }
}
