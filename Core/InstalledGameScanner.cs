using Microsoft.Win32;
using System.IO;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace NRftWManagerUI.Core;

internal sealed class InstalledGame
{
    public required string Name { get; init; }
    public required string Platform { get; init; }
    public required string AppId { get; init; }
    public required string InstallPath { get; init; }
    public bool IsInstalled { get; init; } = true;
    public bool IsSupported => AppId == "1371980" ||
                               Name.Contains("No Rest for the Wicked", StringComparison.OrdinalIgnoreCase);
    public string DisplayName => $"{Name}  ·  {Platform}";
}

internal static partial class InstalledGameScanner
{
    public static IReadOnlyList<InstalledGame> Scan()
    {
        var games = new List<InstalledGame>();
        ScanSteam(games);
        ScanEpic(games);

        var result = games
            .GroupBy(game => $"{game.Platform}|{game.AppId}|{game.InstallPath}", StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .OrderByDescending(game => game.IsSupported)
            .ThenBy(game => game.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToList();

        if (!result.Any(game => game.IsSupported))
        {
            result.Insert(0, new InstalledGame
            {
                Name = "No Rest for the Wicked",
                Platform = "未检测到安装",
                AppId = "1371980",
                InstallPath = string.Empty,
                IsInstalled = false
            });
        }

        return result;
    }

    private static void ScanSteam(List<InstalledGame> games)
    {
        var roots = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        AddDirectory(roots, Registry.GetValue(@"HKEY_CURRENT_USER\Software\Valve\Steam", "SteamPath", null) as string);
        AddDirectory(roots, Registry.GetValue(@"HKEY_LOCAL_MACHINE\SOFTWARE\WOW6432Node\Valve\Steam", "InstallPath", null) as string);
        AddDirectory(roots, Registry.GetValue(@"HKEY_LOCAL_MACHINE\SOFTWARE\Valve\Steam", "InstallPath", null) as string);

        foreach (var root in roots.ToArray())
        {
            var libraryFile = Path.Combine(root, "steamapps", "libraryfolders.vdf");
            if (!File.Exists(libraryFile))
            {
                continue;
            }

            string libraryText;
            try
            {
                libraryText = File.ReadAllText(libraryFile);
            }
            catch (IOException) { continue; }
            catch (UnauthorizedAccessException) { continue; }

            foreach (Match match in SteamLibraryPathRegex().Matches(libraryText))
            {
                AddDirectory(roots, match.Groups[1].Value.Replace("\\\\", "\\"));
            }
        }

        foreach (var root in roots)
        {
            var steamApps = Path.Combine(root, "steamapps");
            if (!Directory.Exists(steamApps))
            {
                continue;
            }

            foreach (var manifest in SafeEnumerateFiles(steamApps, "appmanifest_*.acf"))
            {
                try
                {
                    var text = File.ReadAllText(manifest);
                    var appId = ReadSteamValue(text, "appid");
                    var name = ReadSteamValue(text, "name");
                    var installDirectory = ReadSteamValue(text, "installdir");
                    if (string.IsNullOrWhiteSpace(appId) || string.IsNullOrWhiteSpace(name))
                    {
                        continue;
                    }

                    var installPath = Path.Combine(steamApps, "common", installDirectory);
                    if (!Directory.Exists(installPath))
                    {
                        continue;
                    }

                    games.Add(new InstalledGame
                    {
                        Name = name,
                        Platform = "Steam",
                        AppId = appId,
                        InstallPath = installPath
                    });
                }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
        }
    }

    private static void ScanEpic(List<InstalledGame> games)
    {
        var programData = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
        var manifestDirectory = Path.Combine(programData, "Epic", "EpicGamesLauncher", "Data", "Manifests");
        if (!Directory.Exists(manifestDirectory))
        {
            return;
        }

        foreach (var manifest in SafeEnumerateFiles(manifestDirectory, "*.item"))
        {
            try
            {
                using var document = JsonDocument.Parse(File.ReadAllText(manifest));
                var root = document.RootElement;
                var name = ReadJsonString(root, "DisplayName");
                var installPath = ReadJsonString(root, "InstallLocation");
                var appId = ReadJsonString(root, "CatalogItemId");
                if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(installPath) || !Directory.Exists(installPath))
                {
                    continue;
                }

                games.Add(new InstalledGame
                {
                    Name = name,
                    Platform = "Epic",
                    AppId = appId,
                    InstallPath = installPath
                });
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
            catch (JsonException) { }
        }
    }

    private static void AddDirectory(HashSet<string> directories, string? path)
    {
        if (!string.IsNullOrWhiteSpace(path) && Directory.Exists(path))
        {
            directories.Add(Path.GetFullPath(path));
        }
    }

    private static IEnumerable<string> SafeEnumerateFiles(string directory, string pattern)
    {
        try
        {
            return Directory.EnumerateFiles(directory, pattern).ToArray();
        }
        catch (IOException) { return Array.Empty<string>(); }
        catch (UnauthorizedAccessException) { return Array.Empty<string>(); }
    }

    private static string ReadSteamValue(string text, string key)
    {
        var match = Regex.Match(text, $"\"{Regex.Escape(key)}\"\\s+\"([^\"]*)\"", RegexOptions.IgnoreCase);
        return match.Success ? match.Groups[1].Value : string.Empty;
    }

    private static string ReadJsonString(JsonElement element, string propertyName) =>
        element.TryGetProperty(propertyName, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? string.Empty
            : string.Empty;

    [GeneratedRegex("\"path\"\\s+\"([^\"]+)\"", RegexOptions.IgnoreCase)]
    private static partial Regex SteamLibraryPathRegex();
}
