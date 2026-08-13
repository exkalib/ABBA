using System.IO;
using System.Text;
using System.Text.RegularExpressions;

namespace NRftWManagerUI.Core;

internal sealed class LocalGameItem
{
    public required string Key { get; init; }
    public required string Name { get; init; }
    public required string Category { get; init; }
    public required string Source { get; init; }
    public string IconPath { get; init; } = string.Empty;
    public string IconResourceName { get; init; } = string.Empty;
    public long Guid { get; init; }
    public bool HasIconReference { get; init; }
    public string Description { get; init; } = "已从当前游戏资源包解析到物品名称；图标、中文描述与生成 GUID 还需要继续解析 Addressables/Unity 资源。";
    public int Rarity { get; init; }
    public int ItemType { get; init; }
    public string Metadata => $"{Category}  ·  GUID {Guid:X16}  ·  {Key}";
    public string DisplayName => Name;
}

internal sealed class LocalGameItemScanResult
{
    public required IReadOnlyList<LocalGameItem> Items { get; init; }
    public required int MatchedIconResources { get; init; }
    public required int ExtractedIcons { get; init; }
}

internal static partial class LocalGameItemScanner
{
    private sealed record LocalizedItemName(string English, string SimplifiedChinese);
    private static readonly string[] ItemBundleHints =
    {
        "qdb_assets",
        "qdb_binary_assets",
        "static_assets"
    };

    public static LocalGameItemScanResult Scan(string installPath)
    {
        var dataPath = Path.Combine(installPath, "NoRestForTheWicked_Data", "StreamingAssets", "aa", "StandaloneWindows64");
        if (!Directory.Exists(dataPath))
        {
            return new LocalGameItemScanResult
            {
                Items = Array.Empty<LocalGameItem>(),
                MatchedIconResources = 0,
                ExtractedIcons = 0
            };
        }

        var localizedItems = ScanLocalizedItems(dataPath);
        var catalogLocalizedItems = localizedItems
            .Where(pair => IsCatalogItem(pair.Key))
            .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.OrdinalIgnoreCase);
        var rawItemNames = catalogLocalizedItems
            .SelectMany(pair => new[] { CanonicalName(GetRawItemName(pair.Key)), CanonicalName(pair.Value.English) })
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var iconResources = ScanIconResources(installPath, rawItemNames);
        var extractedIcons = UnityIconExtractor.Extract(installPath, iconResources.Values.Distinct(StringComparer.OrdinalIgnoreCase).ToArray());
        var spriteIcons = UnityIconExtractor.ExtractSprites(installPath, rawItemNames, CanonicalName);
        var itemDefinitions = UnityIconExtractor.ExtractItemDefinitions(installPath, rawItemNames, CanonicalName);
        var referencedIcons = UnityIconExtractor.ExtractReferencedItemIcons(installPath, itemDefinitions);
        var found = new Dictionary<string, LocalGameItem>(StringComparer.OrdinalIgnoreCase);
        foreach (var (key, localizedName) in catalogLocalizedItems)
        {
            if (TryCreateItem(key, "qdb_assets", iconResources, extractedIcons, spriteIcons, referencedIcons, itemDefinitions, localizedName, out var item))
            {
                found.TryAdd(item.Key, item);
            }
        }

        var items = found.Values
            .Where(item => IsCatalogItem(item.Key) &&
                           item.Guid != 0 &&
                           item.HasIconReference &&
                           !string.IsNullOrWhiteSpace(item.IconPath))
            .OrderBy(item => GetCategoryOrder(item.Category))
            .ThenBy(item => item.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToArray();
        return new LocalGameItemScanResult
        {
            Items = items,
            MatchedIconResources = items.Count(item => !string.IsNullOrWhiteSpace(item.IconPath)),
            ExtractedIcons = items.Count(item => !string.IsNullOrWhiteSpace(item.IconPath))
        };
    }

    private static bool IsCatalogItem(string key) =>
        key.StartsWith("items.gear.", StringComparison.OrdinalIgnoreCase) ||
        key.StartsWith("items.consumables.", StringComparison.OrdinalIgnoreCase) ||
        key.StartsWith("items.craftingMaterials.", StringComparison.OrdinalIgnoreCase) ||
        key.StartsWith("items.quest", StringComparison.OrdinalIgnoreCase) ||
        key.StartsWith("items.keys.", StringComparison.OrdinalIgnoreCase);

    private static IEnumerable<string> ExtractAsciiTokens(string path)
    {
        const int bufferSize = 1024 * 1024;
        using var stream = File.OpenRead(path);
        var buffer = new byte[bufferSize];
        var builder = new StringBuilder(160);

        while (true)
        {
            var read = stream.Read(buffer, 0, buffer.Length);
            if (read == 0)
            {
                break;
            }

            for (var index = 0; index < read; index++)
            {
                var value = buffer[index];
                if (value is >= 32 and <= 126)
                {
                    if (builder.Length < 220)
                    {
                        builder.Append((char)value);
                    }
                    continue;
                }

                if (builder.Length >= 4)
                {
                    yield return builder.ToString();
                }
                builder.Clear();
            }
        }

        if (builder.Length >= 4)
        {
            yield return builder.ToString();
        }
    }

    private static IReadOnlyDictionary<string, LocalizedItemName> ScanLocalizedItems(string dataPath)
    {
        var names = new Dictionary<string, LocalizedItemName>(StringComparer.OrdinalIgnoreCase);
        foreach (var bundle in Directory.EnumerateFiles(dataPath, "qdb_assets*.bundle"))
        {
            ScanLocalizationBundle(bundle, names);
        }

        return names;
    }

    private static void ScanLocalizationBundle(string path, IDictionary<string, LocalizedItemName> names)
    {
        const int blockSize = 8 * 1024 * 1024;
        const int overlap = 16 * 1024;
        var marker = "items."u8;
        using var stream = File.OpenRead(path);
        var buffer = new byte[blockSize + overlap];
        var carried = 0;
        while (true)
        {
            var read = stream.Read(buffer, carried, blockSize);
            if (read == 0)
            {
                break;
            }

            var length = carried + read;
            var search = buffer.AsSpan(0, length);
            var consumed = 0;
            while (true)
            {
                var relative = search[consumed..].IndexOf(marker);
                if (relative < 0)
                {
                    break;
                }

                var start = consumed + relative;
                if (TryReadLocalizationRow(search, start, out var key, out var english, out var simplifiedChinese))
                {
                    names.TryAdd(key, new LocalizedItemName(english, simplifiedChinese));
                }
                consumed = start + marker.Length;
            }

            carried = Math.Min(overlap, length);
            buffer.AsSpan(length - carried, carried).CopyTo(buffer);
        }
    }

    private static bool TryReadLocalizationRow(ReadOnlySpan<byte> data, int start, out string key, out string english, out string simplifiedChinese)
    {
        key = string.Empty;
        english = string.Empty;
        simplifiedChinese = string.Empty;
        var keyEnd = data[start..].IndexOf((byte)0);
        if (keyEnd <= 0 || keyEnd > 260)
        {
            return false;
        }

        key = Encoding.UTF8.GetString(data.Slice(start, keyEnd)).Trim('.');
        if (!ItemLocalizationKeyRegex().IsMatch(key) || key.EndsWith(".Description", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var position = start + keyEnd + 1;
        while ((position & 3) != 0 && position < data.Length && data[position] == 0)
        {
            position++;
        }

        for (var languageIndex = 0; languageIndex < 8; languageIndex++)
        {
            if (position + sizeof(int) > data.Length)
            {
                return false;
            }
            var textLength = System.Buffers.Binary.BinaryPrimitives.ReadInt32LittleEndian(data.Slice(position, sizeof(int)));
            position += sizeof(int);
            if (textLength < 0 || textLength > 4096 || position + textLength > data.Length)
            {
                return false;
            }
            if (languageIndex == 7)
            {
                simplifiedChinese = Encoding.UTF8.GetString(data.Slice(position, textLength));
            }
            else if (languageIndex == 0)
            {
                english = Encoding.UTF8.GetString(data.Slice(position, textLength));
            }
            position += textLength;
            while ((position & 3) != 0 && position < data.Length && data[position] == 0)
            {
                position++;
            }
        }

        return !string.IsNullOrWhiteSpace(simplifiedChinese);
    }

    private static IReadOnlyDictionary<string, string> ScanIconResources(string installPath, IReadOnlySet<string> rawItemNames)
    {
        var catalogPath = Path.Combine(installPath, "NoRestForTheWicked_Data", "StreamingAssets", "aa", "catalog.bin");
        if (!File.Exists(catalogPath))
        {
            return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        }

        var icons = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var token in ExtractAsciiTokens(catalogPath))
        {
            foreach (Match match in IconResourceRegex().Matches(token))
            {
                var resourceName = match.Value;
                var normalizedName = CanonicalName(StripIconPrefix(Path.GetFileNameWithoutExtension(resourceName)));
                if (normalizedName.Length > 0 && rawItemNames.Contains(normalizedName))
                {
                    icons.TryAdd(normalizedName, resourceName);
                }
            }
        }

        return icons;
    }

    private static bool TryCreateItem(
        string key,
        string source,
        IReadOnlyDictionary<string, string> iconResources,
        IReadOnlyDictionary<string, string> extractedIcons,
        IReadOnlyDictionary<string, string> spriteIcons,
        IReadOnlyDictionary<string, string> referencedIcons,
        IReadOnlyDictionary<string, UnityIconExtractor.ItemDefinition> itemDefinitions,
        LocalizedItemName localizedName,
        out LocalGameItem item)
    {
        item = default!;
        if (!ItemLocalizationKeyRegex().IsMatch(key))
        {
            return false;
        }

        key = key.Trim('.');
        if (key.EndsWith(".Description", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var rawName = GetRawItemName(key);
        var category = Categorize(key);
        var rawCanonicalName = CanonicalName(rawName);
        var englishCanonicalName = CanonicalName(localizedName.English);
        itemDefinitions.TryGetValue(rawCanonicalName, out var definition);
        if (definition is null)
        {
            itemDefinitions.TryGetValue(englishCanonicalName, out definition);
        }
        var guid = definition?.Guid ?? 0;
        iconResources.TryGetValue(rawCanonicalName, out var iconResourceName);
        if (string.IsNullOrWhiteSpace(iconResourceName))
        {
            iconResources.TryGetValue(englishCanonicalName, out iconResourceName);
        }
        var iconPath = !string.IsNullOrWhiteSpace(iconResourceName) &&
                       extractedIcons.TryGetValue(Path.GetFileNameWithoutExtension(iconResourceName), out var extractedIconPath)
            ? extractedIconPath
            : referencedIcons.GetValueOrDefault(rawCanonicalName,
                referencedIcons.GetValueOrDefault(englishCanonicalName,
                    spriteIcons.GetValueOrDefault(rawCanonicalName, spriteIcons.GetValueOrDefault(englishCanonicalName, string.Empty))));
        item = new LocalGameItem
        {
            Key = key,
            Name = string.IsNullOrWhiteSpace(localizedName.SimplifiedChinese) ? ToDisplayName(rawName) : localizedName.SimplifiedChinese,
            Category = category,
            Source = Path.GetFileName(source),
            IconPath = iconPath,
            IconResourceName = iconResourceName ?? string.Empty,
            Guid = guid,
            HasIconReference = definition is not null && definition.IconPathId != 0,
            ItemType = GetCategoryOrder(category),
            Rarity = 0,
            Description = string.IsNullOrWhiteSpace(iconResourceName)
                ? $"{key}.Description · 来源 {Path.GetFileName(source)}"
                : $"{key}.Description · 图标资源 {iconResourceName} · 来源 {Path.GetFileName(source)}"
        };
        return true;
    }

    private static string GetRawItemName(string key)
    {
        var parts = key.Split('.', StringSplitOptions.RemoveEmptyEntries);
        return parts.LastOrDefault(part =>
            !part.Equals("Name", StringComparison.OrdinalIgnoreCase) &&
            !part.Equals("Title", StringComparison.OrdinalIgnoreCase)) ?? key;
    }

    private static string ToDisplayName(string name)
    {
        name = NameBoundaryRegex().Replace(name, " $1");
        return string.Join(' ', name.Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Select(part => part.Length <= 1
                ? part.ToUpperInvariant()
                : char.ToUpperInvariant(part[0]) + part[1..]));
    }

    private static string NormalizeName(string value) =>
        new(value.Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray());

    private static string CanonicalName(string value)
    {
        var normalized = NormalizeName(value);
        foreach (var noise in new[] { "hero", "male", "female" })
        {
            normalized = normalized.Replace(noise, string.Empty, StringComparison.OrdinalIgnoreCase);
        }
        if (normalized.StartsWith("icon", StringComparison.OrdinalIgnoreCase))
        {
            normalized = normalized["icon".Length..];
        }
        return normalized;
    }

    private static string StripIconPrefix(string name)
    {
        if (name.StartsWith("uiIconItem", StringComparison.OrdinalIgnoreCase))
        {
            return name["uiIconItem".Length..];
        }

        if (name.StartsWith("icon", StringComparison.OrdinalIgnoreCase))
        {
            return name["icon".Length..];
        }

        return name;
    }

    private static string Categorize(string key)
    {
        // The localization key mirrors the game's own item folders. Use that structure before
        // looking at individual words: material names such as "blood ring/stone" must not become
        // jewelry merely because their final name contains "ring".
        if (key.StartsWith("items.craftingMaterials.", StringComparison.OrdinalIgnoreCase))
        {
            return "材料";
        }

        if (key.StartsWith("items.consumables.", StringComparison.OrdinalIgnoreCase))
        {
            return "消耗品";
        }

        if (key.StartsWith("items.quest", StringComparison.OrdinalIgnoreCase) ||
            key.StartsWith("items.keys.", StringComparison.OrdinalIgnoreCase))
        {
            return "任务/钥匙";
        }

        if (key.Contains(".weapons.", StringComparison.OrdinalIgnoreCase) ||
            key.Contains(".offHands.bows.", StringComparison.OrdinalIgnoreCase) ||
            key.Contains(".offHands.whips.", StringComparison.OrdinalIgnoreCase))
        {
            return "武器";
        }

        if (key.Contains(".armor.", StringComparison.OrdinalIgnoreCase) ||
            key.Contains(".armors.", StringComparison.OrdinalIgnoreCase) ||
            key.Contains("armor", StringComparison.OrdinalIgnoreCase))
        {
            return "防具";
        }

        if (key.Contains(".rings.", StringComparison.OrdinalIgnoreCase) ||
            key.Contains(".amulets.", StringComparison.OrdinalIgnoreCase) ||
            key.Contains(".accessories.", StringComparison.OrdinalIgnoreCase))
        {
            return "饰品";
        }

        if (key.Contains("recipe", StringComparison.OrdinalIgnoreCase) ||
            key.Contains("blueprint", StringComparison.OrdinalIgnoreCase) ||
            key.Contains("pattern", StringComparison.OrdinalIgnoreCase))
        {
            return "图纸/配方";
        }

        return "其他";
    }

    private static int GetCategoryOrder(string category) => category switch
    {
        "武器" => 0,
        "防具" => 1,
        "饰品" => 2,
        "消耗品" => 3,
        "图纸/配方" => 4,
        "材料" => 5,
        "任务/钥匙" => 6,
        _ => 9
    };

    [GeneratedRegex(@"items\.[A-Za-z0-9_.-]+\.(?:Name|Title)", RegexOptions.IgnoreCase)]
    private static partial Regex ItemLocalizationKeyRegex();

    [GeneratedRegex("(?<!^)([A-Z])")]
    private static partial Regex NameBoundaryRegex();

    [GeneratedRegex(@"[A-Za-z0-9_-]+\.png", RegexOptions.IgnoreCase)]
    private static partial Regex IconResourceRegex();
}
