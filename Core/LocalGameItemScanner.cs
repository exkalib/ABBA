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
    public string Description { get; init; } = "已从当前游戏资源包解析到物品名称；图标、中文描述与生成 GUID 还需要继续解析 Addressables/Unity 资源。";
    public int Rarity { get; init; }
    public int ItemType { get; init; }
    public string Metadata => string.IsNullOrWhiteSpace(IconResourceName)
        ? $"{Category}  ·  本地候选  ·  {Key}"
        : $"{Category}  ·  图标资源 {IconResourceName}  ·  {Key}";
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

        var rawItemNames = ScanRawItemNames(dataPath);
        var iconResources = ScanIconResources(installPath, rawItemNames);
        var extractedIcons = UnityIconExtractor.Extract(installPath, iconResources.Values.Distinct(StringComparer.OrdinalIgnoreCase).ToArray());
        var found = new Dictionary<string, LocalGameItem>(StringComparer.OrdinalIgnoreCase);
        foreach (var bundle in Directory.EnumerateFiles(dataPath, "*.bundle")
                     .Where(path => ItemBundleHints.Any(hint => Path.GetFileName(path).Contains(hint, StringComparison.OrdinalIgnoreCase))))
        {
            foreach (var token in ExtractAsciiTokens(bundle))
            {
                if (!TryCreateItem(token, bundle, iconResources, extractedIcons, out var item))
                {
                    continue;
                }

                found.TryAdd(item.Key, item);
            }
        }

        var items = found.Values
            .OrderBy(item => GetCategoryOrder(item.Category))
            .ThenBy(item => item.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToArray();
        return new LocalGameItemScanResult
        {
            Items = items,
            MatchedIconResources = items.Count(item => !string.IsNullOrWhiteSpace(item.IconResourceName)),
            ExtractedIcons = items.Count(item => !string.IsNullOrWhiteSpace(item.IconPath))
        };
    }

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

    private static IReadOnlySet<string> ScanRawItemNames(string dataPath)
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var bundle in Directory.EnumerateFiles(dataPath, "*.bundle")
                     .Where(path => ItemBundleHints.Any(hint => Path.GetFileName(path).Contains(hint, StringComparison.OrdinalIgnoreCase))))
        {
            foreach (var token in ExtractAsciiTokens(bundle))
            {
                var match = ItemLocalizationKeyRegex().Match(token);
                if (match.Success)
                {
                    names.Add(NormalizeName(GetRawItemName(match.Value.Trim('.'))));
                }
            }
        }

        return names;
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
                var normalizedName = NormalizeName(StripIconPrefix(Path.GetFileNameWithoutExtension(resourceName)));
                if (normalizedName.Length > 0 && rawItemNames.Contains(normalizedName))
                {
                    icons.TryAdd(normalizedName, resourceName);
                }
            }
        }

        return icons;
    }

    private static bool TryCreateItem(
        string token,
        string source,
        IReadOnlyDictionary<string, string> iconResources,
        IReadOnlyDictionary<string, string> extractedIcons,
        out LocalGameItem item)
    {
        item = default!;
        var match = ItemLocalizationKeyRegex().Match(token);
        if (!match.Success)
        {
            return false;
        }

        var key = match.Value.Trim('.');
        if (key.EndsWith(".Description", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var rawName = GetRawItemName(key);
        var category = Categorize(key);
        iconResources.TryGetValue(NormalizeName(rawName), out var iconResourceName);
        var iconPath = !string.IsNullOrWhiteSpace(iconResourceName) &&
                       extractedIcons.TryGetValue(Path.GetFileNameWithoutExtension(iconResourceName), out var extractedIconPath)
            ? extractedIconPath
            : string.Empty;
        item = new LocalGameItem
        {
            Key = key,
            Name = ToDisplayName(rawName),
            Category = category,
            Source = Path.GetFileName(source),
            IconPath = iconPath,
            IconResourceName = iconResourceName ?? string.Empty,
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
        if (key.Contains(".weapons.", StringComparison.OrdinalIgnoreCase) ||
            key.Contains("weapon", StringComparison.OrdinalIgnoreCase))
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
            key.Contains("ring", StringComparison.OrdinalIgnoreCase))
        {
            return "饰品";
        }

        if (key.Contains(".food", StringComparison.OrdinalIgnoreCase) ||
            key.Contains("consumable", StringComparison.OrdinalIgnoreCase) ||
            key.Contains("potion", StringComparison.OrdinalIgnoreCase))
        {
            return "消耗品";
        }

        if (key.Contains("recipe", StringComparison.OrdinalIgnoreCase) ||
            key.Contains("blueprint", StringComparison.OrdinalIgnoreCase) ||
            key.Contains("pattern", StringComparison.OrdinalIgnoreCase))
        {
            return "图纸/配方";
        }

        if (key.Contains("quest", StringComparison.OrdinalIgnoreCase) ||
            key.Contains("key", StringComparison.OrdinalIgnoreCase))
        {
            return "任务/钥匙";
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
        "任务/钥匙" => 5,
        _ => 9
    };

    [GeneratedRegex(@"items\.[A-Za-z0-9_.-]+\.(?:Name|Title)", RegexOptions.IgnoreCase)]
    private static partial Regex ItemLocalizationKeyRegex();

    [GeneratedRegex("(?<!^)([A-Z])")]
    private static partial Regex NameBoundaryRegex();

    [GeneratedRegex(@"[A-Za-z0-9_-]+\.png", RegexOptions.IgnoreCase)]
    private static partial Regex IconResourceRegex();
}
