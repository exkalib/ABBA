using Microsoft.Win32;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media.Imaging;

namespace NRftWManagerUI.Core;

internal sealed class InstalledGame
{
    public required string Name { get; init; }
    public required string Platform { get; init; }
    public required string AppId { get; init; }
    public required string InstallPath { get; init; }
    public string? IconPath { get; init; }
    public bool IsInstalled { get; init; } = true;
    public bool IsSupported => AppId == "1371980" ||
                               Name.Contains("No Rest for the Wicked", StringComparison.OrdinalIgnoreCase);
    public string SupportLabel => IsSupported ? "已支持" : "已发现";
    public string DisplayName => $"{Name}  ·  {Platform}";

    public override string ToString() => DisplayName;
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
                IconPath = "Assets/riftctrl-icon.png",
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

                    if (!IsSteamGameEntry(appId, name, installDirectory))
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
                        InstallPath = installPath,
                        IconPath = FindSteamIcon(root, appId, installPath) ?? "Assets/riftctrl-icon.png"
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
                    InstallPath = installPath,
                    IconPath = ExtractExecutableIcon(appId, installPath) ?? "Assets/riftctrl-icon.png"
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

    private static string? FindSteamIcon(string steamRoot, string appId, string installPath)
    {
        var cacheRoot = Path.Combine(steamRoot, "appcache", "librarycache");
        var candidates = new[]
        {
            Path.Combine(cacheRoot, appId, "icon.jpg"),
            Path.Combine(cacheRoot, appId, "logo.png"),
            Path.Combine(cacheRoot, appId, "library_600x900.jpg"),
            Path.Combine(cacheRoot, $"{appId}_icon.jpg"),
            Path.Combine(cacheRoot, $"{appId}_logo.png"),
            Path.Combine(cacheRoot, $"{appId}_library_600x900.jpg")
        };

        var cachedIcon = candidates.FirstOrDefault(File.Exists);
        return cachedIcon ?? ExtractExecutableIcon(appId, installPath);
    }

    private static string? ExtractExecutableIcon(string appId, string installPath)
    {
        if (string.IsNullOrWhiteSpace(installPath) || !Directory.Exists(installPath))
        {
            return null;
        }

        try
        {
            var exePath = Directory.EnumerateFiles(installPath, "*.exe", SearchOption.TopDirectoryOnly)
                .OrderByDescending(path => Path.GetFileNameWithoutExtension(path)
                    .Contains("wicked", StringComparison.OrdinalIgnoreCase))
                .ThenBy(path => Path.GetFileName(path).Contains("launcher", StringComparison.OrdinalIgnoreCase))
                .FirstOrDefault();
            if (exePath is null)
            {
                return null;
            }

            var cacheDirectory = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "RIFTCTRL",
                "GameIcons");
            Directory.CreateDirectory(cacheDirectory);

            var iconPath = Path.Combine(cacheDirectory, $"{SanitizeFileName(appId)}.png");
            if (!TrySaveExecutableIcon(exePath, iconPath))
            {
                return null;
            }

            return iconPath;
        }
        catch (ArgumentException) { return null; }
        catch (IOException) { return null; }
        catch (UnauthorizedAccessException) { return null; }
        catch (ExternalException) { return null; }
    }

    private static bool TrySaveExecutableIcon(string exePath, string iconPath)
    {
        var fileInfo = new ShFileInfo();
        var result = SHGetFileInfo(
            exePath,
            0,
            ref fileInfo,
            (uint)Marshal.SizeOf<ShFileInfo>(),
            ShgfiIcon | ShgfiLargeIcon);

        if (result == IntPtr.Zero || fileInfo.IconHandle == IntPtr.Zero)
        {
            return false;
        }

        try
        {
            var source = Imaging.CreateBitmapSourceFromHIcon(
                fileInfo.IconHandle,
                Int32Rect.Empty,
                BitmapSizeOptions.FromWidthAndHeight(64, 64));
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(source));

            using var stream = File.Create(iconPath);
            encoder.Save(stream);
            return true;
        }
        finally
        {
            DestroyIcon(fileInfo.IconHandle);
        }
    }

    private static string SanitizeFileName(string value) =>
        string.Concat(value.Select(character => Path.GetInvalidFileNameChars().Contains(character) ? '_' : character));

    private static bool IsSteamGameEntry(string appId, string name, string installDirectory)
    {
        // Steam creates normal app manifests for redistributables, runtimes, tools and DLC too.
        // Those are install dependencies, not launchable games for this picker.
        if (appId == "228980") // Steamworks Common Redistributables
        {
            return false;
        }

        var normalizedName = name.Trim();
        var normalizedDirectory = installDirectory.Trim();
        var infrastructurePrefixes = new[]
        {
            "Steamworks Common Redistributables", "Steamworks Shared", "Steamworks SDK Redist",
            "Steam Linux Runtime", "Proton", "SteamVR", "OpenVR", "DirectX",
            "Microsoft Visual C++", "Microsoft VC++"
        };

        if (infrastructurePrefixes.Any(prefix =>
                normalizedName.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) ||
                normalizedDirectory.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)))
        {
            return false;
        }

        var nonGameMarkers = new[]
        {
            " DLC", "DLC ", "Soundtrack", "Original Soundtrack", " OST", "Artbook",
            "Supporter Pack", "Dedicated Server", "原声带", "美术设定集", "支持者包"
        };

        return !nonGameMarkers.Any(marker =>
            normalizedName.Contains(marker, StringComparison.OrdinalIgnoreCase));
    }

    [GeneratedRegex("\"path\"\\s+\"([^\"]+)\"", RegexOptions.IgnoreCase)]
    private static partial Regex SteamLibraryPathRegex();

    private const uint ShgfiIcon = 0x000000100;
    private const uint ShgfiLargeIcon = 0x000000000;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct ShFileInfo
    {
        public IntPtr IconHandle;
        public int IconIndex;
        public uint Attributes;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
        public string DisplayName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 80)]
        public string TypeName;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr SHGetFileInfo(
        string path,
        uint fileAttributes,
        ref ShFileInfo fileInfo,
        uint fileInfoSize,
        uint flags);

    [DllImport("user32.dll")]
    private static extern bool DestroyIcon(IntPtr iconHandle);
}
