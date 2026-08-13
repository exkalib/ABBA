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
    public string IconPath { get; init; } = "Assets/riftctrl-icon.png";
    public string Description { get; init; } = "从当前游戏资源包实时扫描得到。图标与完整描述解析接入中。";
    public int Rarity { get; init; }
    public int ItemType { get; init; }
    public string Metadata => $"{Category}  ·  本地候选  ·  {Key}";
    public string DisplayName => Name;
}

internal static partial class LocalGameItemScanner
{
    private static readonly string[] ItemBundleHints =
    {
        "qdb_assets",
        "qdb_binary_assets",
        "static_assets"
    };

    public static IReadOnlyList<LocalGameItem> Scan(string installPath)
    {
        var dataPath = Path.Combine(installPath, "NoRestForTheWicked_Data", "StreamingAssets", "aa", "StandaloneWindows64");
        if (!Directory.Exists(dataPath))
        {
            return Array.Empty<LocalGameItem>();
        }

        var found = new Dictionary<string, LocalGameItem>(StringComparer.OrdinalIgnoreCase);
        foreach (var bundle in Directory.EnumerateFiles(dataPath, "*.bundle")
                     .Where(path => ItemBundleHints.Any(hint => Path.GetFileName(path).Contains(hint, StringComparison.OrdinalIgnoreCase))))
        {
            foreach (var token in ExtractAsciiTokens(bundle))
            {
                if (!TryCreateItem(token, bundle, out var item))
                {
                    continue;
                }

                found.TryAdd(item.Key, item);
            }
        }

        return found.Values
            .OrderBy(item => GetCategoryOrder(item.Category))
            .ThenBy(item => item.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToArray();
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

    private static bool TryCreateItem(string token, string source, out LocalGameItem item)
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

        var category = Categorize(key);
        item = new LocalGameItem
        {
            Key = key,
            Name = ToDisplayName(key),
            Category = category,
            Source = Path.GetFileName(source),
            ItemType = GetCategoryOrder(category),
            Rarity = 0,
            Description = $"{key}.Description · 来源 {Path.GetFileName(source)}"
        };
        return true;
    }

    private static string ToDisplayName(string key)
    {
        var name = key.Split('.', StringSplitOptions.RemoveEmptyEntries).LastOrDefault() ?? key;
        name = NameBoundaryRegex().Replace(name, " $1");
        return string.Join(' ', name.Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Select(part => part.Length <= 1
                ? part.ToUpperInvariant()
                : char.ToUpperInvariant(part[0]) + part[1..]));
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
}
