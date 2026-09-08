using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Dsh.Core;
using AvaloniaTextBlock = Avalonia.Controls.TextBlock;

namespace Dsh.Gui;

public sealed class ApprovalDialog : Window
{
    public ApprovalDialog(ApprovalRequest request, Action<ApprovalOutcome> onResult)
    {
        Title = "审批请求";
        Width = 520;
        Height = 220;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        var text = new AvaloniaTextBlock
        {
            Text = $"批准工具 \"{request.ToolName}\"？{(request.Reason is null ? "" : $"\n{request.Reason}")}",
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(12),
        };
        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Center,
            Spacing = 8,
            Children =
            {
                new Button { Content = "允许", Width = 80 },
                new Button { Content = "拒绝", Width = 80 },
                new Button { Content = "取消", Width = 80 },
            },
        };
        var dock = new DockPanel();
        DockPanel.SetDock(buttons, Dock.Bottom);
        DockPanel.SetDock(text, Dock.Top);
        dock.Children.Add(text);
        dock.Children.Add(buttons);
        Content = dock;

        ((Button)buttons.Children[0]).Click += (_, _) => Complete(ApprovalOutcome.AllowedOnce);
        ((Button)buttons.Children[1]).Click += (_, _) => Complete(ApprovalOutcome.Rejected);
        ((Button)buttons.Children[2]).Click += (_, _) => Complete(ApprovalOutcome.Cancelled);

        void Complete(ApprovalOutcome outcome)
        {
            onResult(outcome);
            Close();
        }
    }
}