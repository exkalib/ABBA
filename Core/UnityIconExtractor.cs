using System.IO;
using AssetsTools.NET;
using AssetsTools.NET.Extra;
using AssetsTools.NET.Texture;

namespace NRftWManagerUI.Core;

internal static class UnityIconExtractor
{
    public static IReadOnlyDictionary<string, string> Extract(
        string installPath,
        IReadOnlyCollection<string> iconResourceNames)
    {
        if (iconResourceNames.Count == 0)
        {
            return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        }

        var dataPath = Path.Combine(installPath, "NoRestForTheWicked_Data", "StreamingAssets", "aa", "StandaloneWindows64");
        if (!Directory.Exists(dataPath))
        {
            return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        }

        var wanted = iconResourceNames
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Select(name => Path.GetFileNameWithoutExtension(name))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var cachePath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "NRftWManagerUI",
            "item-icons");
        Directory.CreateDirectory(cachePath);

        var extracted = wanted
            .Select(name => (name, path: Path.Combine(cachePath, name + ".png")))
            .Where(item => new FileInfo(item.path) is { Exists: true, Length: > 0 })
            .ToDictionary(item => item.name, item => item.path, StringComparer.OrdinalIgnoreCase);

        wanted.ExceptWith(extracted.Keys);
        if (wanted.Count == 0)
        {
            return extracted;
        }

        foreach (var bundlePath in Directory.EnumerateFiles(dataPath, "*.bundle")
                     .Where(path => Path.GetFileName(path).Contains("qdb_assets", StringComparison.OrdinalIgnoreCase)))
        {
            TryExtractFromBundle(bundlePath, cachePath, wanted, extracted);
            if (wanted.Count == 0)
            {
                break;
            }
        }

        return extracted;
    }

    private static void TryExtractFromBundle(
        string bundlePath,
        string cachePath,
        HashSet<string> wanted,
        Dictionary<string, string> extracted)
    {
        try
        {
            var manager = new AssetsManager();
            var bundle = manager.LoadBundleFile(bundlePath, true);
            var assetFileCount = bundle.file.BlockAndDirInfo.DirectoryInfos.Count;

            for (var fileIndex = 0; fileIndex < assetFileCount && wanted.Count > 0; fileIndex++)
            {
                var assetsFile = manager.LoadAssetsFileFromBundle(bundle, fileIndex, false);
                foreach (var assetInfo in assetsFile.file.AssetInfos)
                {
                    if (assetInfo.TypeId != (int)AssetClassID.Texture2D)
                    {
                        continue;
                    }

                    var baseField = manager.GetBaseField(assetsFile, assetInfo, AssetReadFlags.None);
                    var textureName = baseField["m_Name"].AsString;
                    if (!wanted.Contains(textureName))
                    {
                        continue;
                    }

                    var outPath = Path.Combine(cachePath, textureName + ".png");
                    var textureFile = TextureFile.ReadTextureFile(baseField);
                    var data = textureFile.FillPictureData(assetsFile);
                    if (data.Length == 0)
                    {
                        DeleteEmptyOutput(outPath);
                        continue;
                    }

                    if (textureFile.DecodeTextureImage(data, outPath, ImageExportType.Png, 100))
                    {
                        if (new FileInfo(outPath) is { Exists: true, Length: > 0 })
                        {
                            extracted[textureName] = outPath;
                            wanted.Remove(textureName);
                        }
                        else
                        {
                            DeleteEmptyOutput(outPath);
                        }
                    }
                    else
                    {
                        DeleteEmptyOutput(outPath);
                    }
                }
            }

            manager.UnloadAllAssetsFiles(true);
            bundle.file.Close();
        }
        catch
        {
            // Icon extraction is best-effort. The catalog view still works with category placeholders.
        }
    }

    private static void DeleteEmptyOutput(string path)
    {
        try
        {
            if (new FileInfo(path) is { Exists: true, Length: 0 })
            {
                File.Delete(path);
            }
        }
        catch
        {
            // Ignore cache cleanup failures.
        }
    }
}
