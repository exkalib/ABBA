using System.IO;
using AssetsTools.NET;
using AssetsTools.NET.Extra;
using AssetsTools.NET.Texture;

namespace NRftWManagerUI.Core;

internal static class UnityIconExtractor
{
    internal sealed record ItemDefinition(long Guid, string IconCabName, long IconPathId);

    public static IReadOnlyDictionary<string, ItemDefinition> ExtractItemDefinitions(
        string installPath,
        IReadOnlySet<string> wantedCanonicalNames,
        Func<string, string> canonicalize)
    {
        var definitions = new Dictionary<string, ItemDefinition>(StringComparer.OrdinalIgnoreCase);
        var dataPath = Path.Combine(installPath, "NoRestForTheWicked_Data", "StreamingAssets", "aa", "StandaloneWindows64");
        foreach (var bundlePath in Directory.EnumerateFiles(dataPath, "qdb_assets*.bundle"))
        {
            try
            {
                var manager = new AssetsManager();
                var bundle = manager.LoadBundleFile(bundlePath, true);
                for (var fileIndex = 0; fileIndex < bundle.file.BlockAndDirInfo.DirectoryInfos.Count; fileIndex++)
                {
                    var assetsFile = manager.LoadAssetsFileFromBundle(bundle, fileIndex, false);
                    if (assetsFile?.file is null) continue;
                    foreach (var info in assetsFile.file.AssetInfos.Where(info => info.TypeId == (int)AssetClassID.MonoBehaviour))
                    {
                        try
                        {
                            var field = manager.GetBaseField(assetsFile, info, AssetReadFlags.None);
                            var canonicalName = canonicalize(field["m_Name"].AsString);
                            if (!wantedCanonicalNames.Contains(canonicalName)) continue;
                            var itemIcon = field["ItemIcon"];
                            if (field["ItemNameMsg"].IsDummy && field["ItemNameMsgRef"].IsDummy && itemIcon.IsDummy) continue;
                            var guidField = field["AssetGuid"]["Value"];
                            if (guidField.IsDummy) continue;
                            var itemGuid = guidField.AsLong;
                            if (itemGuid == 0) continue;
                            var iconFileId = itemIcon.IsDummy ? 0 : itemIcon["m_FileID"].AsInt;
                            var iconCabName = string.Empty;
                            if (iconFileId > 0 && iconFileId <= assetsFile.file.Metadata.Externals.Count)
                            {
                                iconCabName = Path.GetFileName(assetsFile.file.Metadata.Externals[iconFileId - 1].PathName);
                            }
                            else if (iconFileId == 0)
                            {
                                iconCabName = bundle.file.BlockAndDirInfo.DirectoryInfos[fileIndex].Name;
                            }
                            var candidate = new ItemDefinition(
                                itemGuid,
                                iconCabName,
                                itemIcon.IsDummy ? 0 : itemIcon["m_PathID"].AsLong);
                            if (!definitions.TryGetValue(canonicalName, out var current) ||
                                (current.IconPathId == 0 && candidate.IconPathId != 0))
                            {
                                definitions[canonicalName] = candidate;
                            }
                        }
                        catch { }
                    }
                }
                manager.UnloadAllAssetsFiles(true);
                bundle.file.Close();
            }
            catch { }
        }
        return definitions;
    }

    public static IReadOnlyDictionary<string, string> ExtractReferencedItemIcons(
        string installPath,
        IReadOnlyDictionary<string, ItemDefinition> definitions)
    {
        var extracted = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var unresolved = definitions
            .Where(pair => pair.Value.IconPathId != 0 && !string.IsNullOrWhiteSpace(pair.Value.IconCabName))
            .ToArray();
        if (unresolved.Length == 0) return extracted;

        var wantedCabs = unresolved.Select(pair => pair.Value.IconCabName).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var dataPath = Path.Combine(installPath, "NoRestForTheWicked_Data", "StreamingAssets", "aa", "StandaloneWindows64");
        var cachePath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "NRftWManagerUI", "item-icons");
        Directory.CreateDirectory(cachePath);
        var diagnosticPath = Path.Combine(cachePath, "referenced-icon.log");
        File.WriteAllText(diagnosticPath, string.Empty);
        foreach (var bundlePath in Directory.EnumerateFiles(dataPath, "*.bundle"))
        {
            try
            {
                var manager = new AssetsManager();
                var bundle = manager.LoadBundleFile(bundlePath, true);
                for (var fileIndex = 0; fileIndex < bundle.file.BlockAndDirInfo.DirectoryInfos.Count; fileIndex++)
                {
                    var cabName = bundle.file.BlockAndDirInfo.DirectoryInfos[fileIndex].Name;
                    if (!wantedCabs.Contains(cabName)) continue;
                    var assetsFile = manager.LoadAssetsFileFromBundle(bundle, fileIndex, false);
                    if (assetsFile?.file is null) continue;
                    var infos = assetsFile.file.AssetInfos.ToDictionary(info => info.PathId);
                    foreach (var pair in unresolved.Where(pair => pair.Value.IconCabName.Equals(cabName, StringComparison.OrdinalIgnoreCase)))
                    {
                        try
                        {
                            if (!infos.TryGetValue(pair.Value.IconPathId, out var spriteInfo))
                            {
                                SafeAppend(diagnosticPath, $"missing item={pair.Key} guid={pair.Value.Guid:X16} cab={cabName} path={pair.Value.IconPathId}");
                                continue;
                            }
                            if (spriteInfo.TypeId != (int)AssetClassID.Sprite)
                            {
                                SafeAppend(diagnosticPath, $"type item={pair.Key} guid={pair.Value.Guid:X16} cab={cabName} path={pair.Value.IconPathId} type={spriteInfo.TypeId}");
                                continue;
                            }
                            var sprite = manager.GetBaseField(assetsFile, spriteInfo, AssetReadFlags.None);
                            var textureReference = sprite["m_RD"]["texture"];
                            var textureFileId = textureReference["m_FileID"].AsInt;
                            if (textureFileId != 0)
                            {
                                var textureCab = textureFileId <= assetsFile.file.Metadata.Externals.Count
                                    ? Path.GetFileName(assetsFile.file.Metadata.Externals[textureFileId - 1].PathName)
                                    : "out-of-range";
                                SafeAppend(diagnosticPath, $"external-texture item={pair.Key} guid={pair.Value.Guid:X16} spriteCab={cabName} textureCab={textureCab} texturePath={textureReference["m_PathID"].AsLong}");
                                continue;
                            }
                            var texturePathId = textureReference["m_PathID"].AsLong;
                            if (!infos.TryGetValue(texturePathId, out var textureInfo))
                            {
                                SafeAppend(diagnosticPath, $"missing-texture item={pair.Key} guid={pair.Value.Guid:X16} cab={cabName} path={texturePathId}");
                                continue;
                            }
                            var safeKey = new string(pair.Key.Select(character => char.IsLetterOrDigit(character) ? character : '_').ToArray());
                            var outPath = Path.Combine(cachePath, $"item-{safeKey}.png");
                            if (new FileInfo(outPath) is not { Exists: true, Length: > 0 })
                            {
                                var textureField = manager.GetBaseField(assetsFile, textureInfo, AssetReadFlags.None);
                                var texture = TextureFile.ReadTextureFile(textureField);
                                var data = texture.FillPictureData(assetsFile);
                                if (data.Length == 0 || !texture.DecodeTextureImage(data, outPath, ImageExportType.Png, 100))
                                {
                                    SafeAppend(diagnosticPath, $"decode-failed item={pair.Key} guid={pair.Value.Guid:X16} cab={cabName} path={texturePathId}");
                                    continue;
                                }
                            }
                            if (new FileInfo(outPath) is { Exists: true, Length: > 0 }) extracted[pair.Key] = outPath;
                        }
                        catch (Exception exception)
                        {
                            SafeAppend(diagnosticPath, $"error item={pair.Key} guid={pair.Value.Guid:X16} cab={cabName} path={pair.Value.IconPathId}: {exception.Message}");
                        }
                    }
                }
                manager.UnloadAllAssetsFiles(true);
                bundle.file.Close();
            }
            catch (Exception exception)
            {
                SafeAppend(diagnosticPath, $"bundle-error bundle={Path.GetFileName(bundlePath)}: {exception.Message}");
            }
        }
        return extracted;
    }

    public static IReadOnlyDictionary<string, string> ExtractSprites(
        string installPath,
        IReadOnlySet<string> wantedCanonicalNames,
        Func<string, string> canonicalize)
    {
        var extracted = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (wantedCanonicalNames.Count == 0)
        {
            return extracted;
        }

        var dataPath = Path.Combine(installPath, "NoRestForTheWicked_Data", "StreamingAssets", "aa", "StandaloneWindows64");
        var cachePath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "NRftWManagerUI", "item-icons");
        Directory.CreateDirectory(cachePath);
        foreach (var bundlePath in Directory.EnumerateFiles(dataPath, "qdb_assets*.bundle"))
        {
            try
            {
                var manager = new AssetsManager();
                var bundle = manager.LoadBundleFile(bundlePath, true);
                for (var fileIndex = 0; fileIndex < bundle.file.BlockAndDirInfo.DirectoryInfos.Count; fileIndex++)
                {
                    var assetsFile = manager.LoadAssetsFileFromBundle(bundle, fileIndex, false);
                    if (assetsFile?.file is null) continue;
                    var assetsByPathId = assetsFile.file.AssetInfos.ToDictionary(info => info.PathId);
                    foreach (var spriteInfo in assetsFile.file.AssetInfos.Where(info => info.TypeId == (int)AssetClassID.Sprite))
                    {
                        try
                        {
                            var sprite = manager.GetBaseField(assetsFile, spriteInfo, AssetReadFlags.None);
                            var spriteName = sprite["m_Name"].AsString;
                            var canonicalName = canonicalize(spriteName);
                            if (!wantedCanonicalNames.Contains(canonicalName)) continue;

                            var outputPath = Path.Combine(cachePath, spriteName + ".png");
                            if (new FileInfo(outputPath) is not { Exists: true, Length: > 0 })
                            {
                                var textureReference = sprite["m_RD"]["texture"];
                                var textureFileId = textureReference["m_FileID"].AsInt;
                                var texturePathId = textureReference["m_PathID"].AsLong;
                                if (textureFileId != 0)
                                {
                                    continue;
                                }
                                if (texturePathId == 0 || !assetsByPathId.TryGetValue(texturePathId, out var textureInfo))
                                {
                                    continue;
                                }
                                var textureField = manager.GetBaseField(assetsFile, textureInfo, AssetReadFlags.None);
                                var textureFile = TextureFile.ReadTextureFile(textureField);
                                var data = textureFile.FillPictureData(assetsFile);
                                if (data.Length == 0 || !textureFile.DecodeTextureImage(data, outputPath, ImageExportType.Png, 100)) continue;
                            }

                            if (new FileInfo(outputPath) is { Exists: true, Length: > 0 })
                            {
                                extracted[canonicalName] = outputPath;
                            }
                        }
                        catch
                        {
                            // A malformed sprite must not prevent the rest of the catalog loading.
                        }
                    }
                }
                manager.UnloadAllAssetsFiles(true);
                bundle.file.Close();
            }
            catch
            {
                // Leave unresolved icons blank and continue with the localized item catalog.
            }
        }
        return extracted;
    }

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
        var diagnosticsPath = Path.Combine(cachePath, "extract.log");
        SafeAppend(diagnosticsPath, $"Scan {DateTime.Now:yyyy-MM-dd HH:mm:ss} wanted={wanted.Count}");

        var extracted = wanted
            .Select(name => (name, path: Path.Combine(cachePath, name + ".png")))
            .Where(item => new FileInfo(item.path) is { Exists: true, Length: > 0 })
            .ToDictionary(item => item.name, item => item.path, StringComparer.OrdinalIgnoreCase);

        wanted.ExceptWith(extracted.Keys);
        if (wanted.Count == 0)
        {
            return extracted;
        }


        // A completed first scan leaves a cache. Avoid reopening multi-gigabyte Unity bundles
        // on every refresh merely for resources that are Sprite/Atlas-backed rather than direct
        // Texture2D assets; the sprite extractor handles those separately.
        if (extracted.Count > 0)
        {
            return extracted;
        }

        foreach (var bundlePath in FindCandidateBundles(dataPath, wanted))
        {
            TryExtractFromBundle(bundlePath, cachePath, diagnosticsPath, wanted, extracted);
            if (wanted.Count == 0)
            {
                break;
            }
        }

        return extracted;
    }

    private static IEnumerable<string> FindCandidateBundles(string dataPath, IReadOnlySet<string> wanted)
    {
        // Item UI textures live in these shared asset bundles. Probing every 7–20 GB world
        // bundle byte-by-byte made a local refresh take several minutes.
        return Directory.EnumerateFiles(dataPath, "*.bundle")
            .Where(path =>
                Path.GetFileName(path).Contains("qdb_assets", StringComparison.OrdinalIgnoreCase) ||
                Path.GetFileName(path).Contains("duplicateassetisolation", StringComparison.OrdinalIgnoreCase))
            .OrderBy(path => Path.GetFileName(path).Contains("qdb_assets", StringComparison.OrdinalIgnoreCase) ? 0 : 1);
    }

    private static bool FileContainsAny(string path, IReadOnlySet<string> wanted)
    {
        const int bufferSize = 1024 * 1024;
        using var stream = File.OpenRead(path);
        var buffer = new byte[bufferSize];
        var builder = new System.Text.StringBuilder(160);

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

                if (TokenContainsWantedName(builder, wanted))
                {
                    return true;
                }

                builder.Clear();
            }
        }

        return TokenContainsWantedName(builder, wanted);
    }

    private static bool TokenContainsWantedName(System.Text.StringBuilder builder, IReadOnlySet<string> wanted)
    {
        if (builder.Length == 0)
        {
            return false;
        }

        var token = builder.ToString();
        return wanted.Any(name => token.Contains(name, StringComparison.OrdinalIgnoreCase));
    }

    private static void TryExtractFromBundle(
        string bundlePath,
        string cachePath,
        string diagnosticsPath,
        HashSet<string> wanted,
        Dictionary<string, string> extracted)
    {
        try
        {
            var manager = new AssetsManager();
            var bundle = manager.LoadBundleFile(bundlePath, true);
            var assetFileCount = bundle.file.BlockAndDirInfo.DirectoryInfos.Count;
            SafeAppend(diagnosticsPath, $"Bundle {Path.GetFileName(bundlePath)} dirs={assetFileCount} wanted={wanted.Count}");

            for (var fileIndex = 0; fileIndex < assetFileCount && wanted.Count > 0; fileIndex++)
            {
                try
                {
                    var assetsFile = manager.LoadAssetsFileFromBundle(bundle, fileIndex, false);

                    if (assetsFile.file is null)
                    {
                        SafeAppend(diagnosticsPath, $"  skip fileIndex={fileIndex}: no assets file");
                        continue;
                    }

                    SafeAppend(diagnosticsPath, $"  assets fileIndex={fileIndex} assets={assetsFile.file.AssetInfos.Count}");
                    foreach (var assetInfo in assetsFile.file.AssetInfos)
                    {
                        try
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

                            SafeAppend(diagnosticsPath, $"  found texture {textureName} pathId={assetInfo.PathId}");
                            var outPath = Path.Combine(cachePath, textureName + ".png");
                            var textureFile = TextureFile.ReadTextureFile(baseField);
                            var data = textureFile.FillPictureData(assetsFile);
                            SafeAppend(diagnosticsPath, $"    texture {textureName} data={data.Length} size={textureFile.m_Width}x{textureFile.m_Height} format={textureFile.m_TextureFormat}");
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
                                    SafeAppend(diagnosticsPath, $"    decode returned true but output empty");
                                    DeleteEmptyOutput(outPath);
                                }
                            }
                            else
                            {
                                SafeAppend(diagnosticsPath, $"    decode returned false");
                                DeleteEmptyOutput(outPath);
                            }
                        }
                        catch (Exception exception)
                        {
                            SafeAppend(diagnosticsPath, $"  texture pathId={assetInfo.PathId} failed: {exception}");
                        }
                    }
                }
                catch (Exception exception)
                {
                    SafeAppend(diagnosticsPath, $"  skip fileIndex={fileIndex}: {exception}");
                }
            }

            manager.UnloadAllAssetsFiles(true);
            bundle.file.Close();
        }
        catch (Exception exception)
        {
            SafeAppend(diagnosticsPath, $"Bundle failed {Path.GetFileName(bundlePath)}: {exception}");
        }
    }

    private static void SafeAppend(string path, string message)
    {
        try
        {
            File.AppendAllText(path, message + Environment.NewLine);
        }
        catch
        {
            // Ignore diagnostics write failures.
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
