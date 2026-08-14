using System.Windows;
using System.Windows.Controls;

namespace NRftWManagerUI;

public partial class ReplicaWindow : Window
{
    public ReplicaWindow() => InitializeComponent();

    private void SetStatus(string message) => DemoStatus.Text = $"DEMO · {message}";
    private void OnNavClick(object sender, RoutedEventArgs e) => SetStatus($"已打开 {((Button)sender).Tag}");
    private void OnRefreshClick(object sender, RoutedEventArgs e) => SetStatus("游戏库重新扫描完成");
    private void OnHeaderClick(object sender, RoutedEventArgs e) => SetStatus($"{((Button)sender).Tag} 已打开");
    private void OnHeroClick(object sender, RoutedEventArgs e) => SetStatus("已定位到游戏库");
    private void OnFilterClick(object sender, RoutedEventArgs e) => SetStatus($"筛选：{((Button)sender).Tag}");
    private void OnViewClick(object sender, RoutedEventArgs e) => SetStatus($"已切换到 {((Button)sender).Tag} 视图");
    private void OnLinkClick(object sender, RoutedEventArgs e) => SetStatus($"{((Button)sender).Tag} 连接状态正常");
    private void OnActivityClick(object sender, RoutedEventArgs e) => SetStatus("活动详情已打开");
    private void OnGameClick(object sender, RoutedEventArgs e)
    {
        var game = ((Button)sender).Tag?.ToString() ?? "游戏";
        RunStateText.Text = $"已选择《{game}》";
        SetStatus($"已选择《{game}》");
    }
    private void OnSearchChanged(object sender, TextChangedEventArgs e) => SetStatus($"搜索：{SearchBox.Text}");
    private void OnWindowClick(object sender, RoutedEventArgs e)
    {
        switch (((Button)sender).Tag as string)
        {
            case "min": WindowState = WindowState.Minimized; break;
            case "max": WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized; break;
            case "close": Close(); break;
        }
    }
}
