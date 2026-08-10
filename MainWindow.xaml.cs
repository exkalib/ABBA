using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;

namespace NRftWManagerUI;

public partial class MainWindow : Window
{
    private readonly Dictionary<string, FrameworkElement> _views;
    private readonly Dictionary<string, (string Title, string Subtitle)> _pageText = new()
    {
        ["overview"] = ("概览", "先验证连接与版本；功能尚未写入游戏。"),
        ["items"] = ("物品生成", "按名称、分类和数量创建新物品。"),
        ["equipment"] = ("装备与词条", "品质、词条、宝石与词条品质的预览配置。"),
        ["character"] = ("角色", "货币、属性与洗点的逐项验证入口。"),
        ["loadouts"] = ("配置与快照", "保存已有配置，或从 Planner Build 生成等价新装备。"),
        ["logs"] = ("活动记录", "只记录本界面的预览操作。")
    };

    public MainWindow()
    {
        InitializeComponent();
        _views = new Dictionary<string, FrameworkElement>
        {
            ["overview"] = OverviewView,
            ["items"] = ItemsView,
            ["equipment"] = EquipmentView,
            ["character"] = CharacterView,
            ["loadouts"] = LoadoutsView,
            ["logs"] = LogsView
        };

        AddLog("界面启动：未连接游戏，所有动作均为预览。");
    }

    private void OnNavigate(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string page })
        {
            ShowPage(page);
        }
    }

    private void ShowPage(string page)
    {
        foreach (var view in _views)
        {
            view.Value.Visibility = view.Key == page ? Visibility.Visible : Visibility.Collapsed;
        }

        var text = _pageText[page];
        PageTitle.Text = text.Title;
        PageSubtitle.Text = text.Subtitle;
    }

    private void OnRarityClick(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string rarity })
        {
            return;
        }

        RarityHint.Text = rarity switch
        {
            "Common" => "Common：无词条的基础装备。",
            "Magical" => "Magical：可配置正面词条。",
            "Plagued" => "Plagued：需要 4 条正面词条与 1 条负面词条。",
            "Gold" => "Gold：不进入通用自定义流程，仅作为目录中的原生金色武器验证。",
            _ => "尚未选择品质。"
        };
        SetPreviewStatus($"已选择 {rarity} 品质预览。");
    }

    private void OnCurrencyClick(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string currency })
        {
            SetPreviewStatus($"已选择 {currency}；金额操作仍处于预览模式。");
        }
    }

    private void OnPreviewAction(object sender, RoutedEventArgs e)
    {
        var action = sender is Button { Tag: string tag } ? tag : "未命名操作";
        SetPreviewStatus($"预览：{action}。未读取进程，也未修改游戏数据。");
    }

    private void SetPreviewStatus(string message)
    {
        ActionStatusText.Text = message;
        AddLog(message);
    }

    private void AddLog(string message)
    {
        ActivityLog.Items.Insert(0, $"{DateTime.Now:HH:mm:ss}  {message}");
    }
}
