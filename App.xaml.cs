using System.IO;
using System.Windows;

namespace NRftWManagerUI;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        if (e.Args.Length >= 2 && e.Args[0].Equals("--scan-icons", StringComparison.OrdinalIgnoreCase))
        {
            var installPath = string.Join(' ', e.Args.Skip(1));
            var result = NRftWManagerUI.Core.LocalGameItemScanner.Scan(installPath);
            var cachePath = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "NRftWManagerUI",
                "item-icons");
            Directory.CreateDirectory(cachePath);
            File.WriteAllText(
                Path.Combine(cachePath, "scan-icons-result.txt"),
                $"path={installPath}{Environment.NewLine}items={result.Items.Count} matchedIcons={result.MatchedIconResources} extractedIcons={result.ExtractedIcons} guids={result.Items.Count(item => item.Guid != 0)}{Environment.NewLine}" +
                string.Join(Environment.NewLine, result.Items.GroupBy(item => item.Category).Select(group => $"category.{group.Key}={group.Count()} icons={group.Count(item => !string.IsNullOrWhiteSpace(item.IconPath))}")) + Environment.NewLine +
                string.Join(Environment.NewLine, result.Items.Select(item => $"item={item.Name}|{item.Category}|{item.Key}|{item.IconResourceName}")) + Environment.NewLine +
                string.Join(Environment.NewLine, result.Items.Where(item => string.IsNullOrWhiteSpace(item.IconPath)).Select(item => $"missingIcon={item.Name}|{item.Key}|guid={item.Guid:X16}|iconRef={item.HasIconReference}")) + Environment.NewLine);
            Environment.Exit(result.ExtractedIcons > 0 ? 0 : 2);
            return;
        }

        base.OnStartup(e);
    }
}
