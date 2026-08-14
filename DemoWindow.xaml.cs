using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace NRftWManagerUI;

public partial class DemoWindow : Window
{
    public DemoWindow()
    {
        InitializeComponent();
        GamesList.ItemsSource = new ObservableCollection<DemoGame>
        {
            new("艾尔登法环", "42 项功能", "Assets/live-cover-01.png"), new("赛博朋克 2077", "35 项功能", "Assets/live-cover-02.png"), new("荒野大镖客 2", "28 项功能", "Assets/live-cover-03.png"), new("战神", "30 项功能", "Assets/live-cover-04.png"),
            new("地平线：零之曙光", "25 项功能", "Assets/live-cover-05.png"), new("巫师 3：狂猎", "40 项功能", "Assets/live-cover-06.png"), new("GTA 5", "60 项功能", "Assets/live-cover-07.png"), new("只狼：影逝二度", "32 项功能", "Assets/live-cover-08.png"),
            new("博德之门 3", "38 项功能", "Assets/live-cover-09.png"), new("星空", "31 项功能", "Assets/live-cover-10.png"), new("刺客信条：英灵殿", "27 项功能", "Assets/live-cover-11.png"), new("极限竞速：地平线 5", "24 项功能", "Assets/live-cover-12.png")
        };
    }

    private void SetStatus(string message) => DemoStatus.Text = $"DEMO  ·  {message}";
    private void OnNavClick(object sender, RoutedEventArgs e) => SetStatus($"已打开 {((Button)sender).Tag}");
    private void OnRefreshClick(object sender, RoutedEventArgs e) => SetStatus("游戏库重新扫描完成 · 演示数据已刷新");
    private void OnHeaderClick(object sender, RoutedEventArgs e) => SetStatus($"{((Button)sender).Tag} 已打开");
    private void OnHeroClick(object sender, RoutedEventArgs e) => SetStatus("已定位到游戏库");
    private void OnFilterClick(object sender, RoutedEventArgs e) => SetStatus($"筛选：{((Button)sender).Tag}");
    private void OnViewClick(object sender, RoutedEventArgs e) => SetStatus($"已切换到 {((Button)sender).Tag} 视图");
    private void OnLinkClick(object sender, RoutedEventArgs e) => SetStatus($"{((Button)sender).Tag} 连接状态正常");
    private void OnActivityClick(object sender, RoutedEventArgs e) => SetStatus("活动详情已打开");
    private void OnGameClick(object sender, RoutedEventArgs e)
    {
        var game = ((Button)sender).Tag as DemoGame;
        if (game is null) return;
        RunStateText.Text = $"已选择《{game.Name}》";
        SetStatus($"已选择《{game.Name}》 · 这是无功能演示页面");
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

    private void OnTitleBarMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        for (var current = e.OriginalSource as DependencyObject; current is not null; current = VisualTreeHelper.GetParent(current))
        {
            if (current is Button or TextBox)
                return;
            if (ReferenceEquals(current, sender))
                break;
        }

        DragMove();
    }
}

public sealed record DemoGame(string Name, string Features, string Cover);
