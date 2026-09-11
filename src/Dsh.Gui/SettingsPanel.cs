using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Cordis;
using Dsh.Boot;
using Dsh.Core;
using Dsh.Interaction;
using AvaloniaTextBlock = Avalonia.Controls.TextBlock;

namespace Dsh.Gui;

public sealed class SettingsPanel : StackPanel
{
    private readonly Context _ctx;
    private readonly HarnessHome _home;
    private readonly ListBox _providersList = new() { Height = 120 };
    private readonly TextBox _providerName = new() { Watermark = "provider 名称" };
    private readonly TextBox _providerType = new() { Watermark = "类型，如 deepseek / openai-compatible / anthropic" };
    private readonly TextBox _baseUrl = new() { Watermark = "baseUrl" };
    private readonly TextBox _apiKey = new() { Watermark = "apiKey" };
    private readonly TextBox _apiKeyEnv = new() { Watermark = "apiKeyEnv" };
    private readonly TextBox _modelIds = new() { Watermark = "modelIds，逗号分隔" };
    private readonly ComboBox _modelCombo = new() { HorizontalAlignment = HorizontalAlignment.Stretch };
    private readonly CheckBox _autoApprove = new() { Content = "autoApprove" };
    private readonly TextBox _blacklist = new() { AcceptsReturn = true, Height = 80, Watermark = "黑名单规则，每行一条" };
    private readonly AvaloniaTextBlock _message = new() { TextWrapping = TextWrapping.Wrap };
    private readonly AvaloniaTextBlock _statusText = new() { TextWrapping = TextWrapping.Wrap };
    private readonly AvaloniaTextBlock _mcpPluginText = new() { TextWrapping = TextWrapping.Wrap, MinHeight = 60 };

    private bool _suppressModelSelection;

    public AgentLoopAgent Agent { get; set; }

    public event Action? SettingsChanged;

    public SettingsPanel(Context ctx, AgentLoopAgent agent, HarnessHome home)
    {
        _ctx = ctx;
        Agent = agent;
        _home = home;
        Orientation = Orientation.Vertical;
        Margin = new Thickness(8);
        Spacing = 6;

        Children.Add(SectionTitle("模型选择"));
        Children.Add(_modelCombo);

        Children.Add(SectionTitle("Provider 管理"));
        Children.Add(_providersList);
        Children.Add(_providerName);
        Children.Add(_providerType);
        Children.Add(_baseUrl);
        Children.Add(_apiKey);
        Children.Add(_apiKeyEnv);
        Children.Add(_modelIds);
        var providerButtons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 4,
            Children =
            {
                CreateButton("新增", () => _ = SaveProviderAsync(requireExisting: false)),
                CreateButton("更新", () => _ = SaveProviderAsync(requireExisting: true)),
                CreateButton("删除", () => _ = DeleteProviderAsync()),
            },
        };
        Children.Add(providerButtons);

        Children.Add(SectionTitle("安全策略"));
        Children.Add(_autoApprove);
        Children.Add(_blacklist);
        Children.Add(CreateButton("保存安全策略", () => _ = SaveSafetyAsync()));

        Children.Add(SectionTitle("MCP / 插件状态"));
        Children.Add(_mcpPluginText);
        Children.Add(CreateButton("刷新状态", () => _ = RefreshStatusAsync()));

        Children.Add(_statusText);
        Children.Add(_message);

        _providersList.SelectionChanged += (_, _) =>
        {
            if (_providersList.SelectedItem is ListBoxItem { Tag: ProviderSettingsRow row })
            {
                _providerName.Text = row.Name;
                _providerType.Text = row.Settings.Type ?? "";
                _baseUrl.Text = row.Settings.Options?.BaseUrl ?? "";
                _apiKey.Text = row.Settings.Options?.ApiKey ?? "";
                _apiKeyEnv.Text = row.Settings.Options?.ApiKeyEnv ?? "";
                _modelIds.Text = row.Settings.Models is { Count: > 0 } models ? string.Join(',', models.Keys) : "";
            }
        };
        _modelCombo.SelectionChanged += async (_, _) =>
        {
            if (_suppressModelSelection || _modelCombo.SelectedItem is not string selection)
                return;
            await RunCommandAsync($"/model {selection}");
        };

        RefreshProviders();
        RefreshModelCombo();
        RefreshStatus();
    }

    private AvaloniaTextBlock SectionTitle(string text)
        => new()
        {
            Text = text,
            FontWeight = FontWeight.Bold,
            Margin = new Thickness(0, 8, 0, 0),
        };

    private Button CreateButton(string text, Action click)
    {
        var button = new Button { Content = text, Margin = new Thickness(0, 0, 4, 0) };
        button.Click += (_, _) => click();
        return button;
    }

    private async Task SaveProviderAsync(bool requireExisting)
    {
        var name = _providerName.Text?.Trim() ?? "";
        if (name.Length == 0)
        {
            _message.Text = "Provider 名称不能为空";
            return;
        }

        var args = new List<string> { name };
        AddIfSet(args, "--type", _providerType.Text);
        AddIfSet(args, "--base-url", _baseUrl.Text);
        AddIfSet(args, "--api-key", _apiKey.Text);
        AddIfSet(args, "--api-key-env", _apiKeyEnv.Text);
        AddIfSet(args, "--model-ids", _modelIds.Text);
        var line = $"/provider {(requireExisting ? "edit" : "add")} {string.Join(' ', args)}";
        var output = await RunCommandAsync(line);
        _message.Text = output;
        RefreshProviders();
        SettingsChanged?.Invoke();
    }

    private async Task DeleteProviderAsync()
    {
        var name = _providerName.Text?.Trim() ?? "";
        if (name.Length == 0)
            return;
        var output = await RunCommandAsync($"/provider remove {name}");
        _message.Text = output;
        RefreshProviders();
        SettingsChanged?.Invoke();
    }

    private async Task SaveSafetyAsync()
    {
        var settings = HarnessSettings.Load(_home);
        var updated = new HarnessSettings
        {
            GlobalDefaultModel = settings.GlobalDefaultModel,
            CompactionModel = settings.CompactionModel,
            Subagent = settings.Subagent,
            Providers = settings.Providers,
            Skills = settings.Skills,
            Rules = settings.Rules,
            McpServers = settings.McpServers,
            Compaction = settings.Compaction,
            Memory = settings.Memory,
            Safety = new SafetySettings
            {
                AutoApprove = _autoApprove.IsChecked == true,
                Blacklist = (_blacklist.Text ?? "")
                    .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                    .Select(line => line.Trim())
                    .Where(line => line.Length > 0)
                    .ToList(),
            },
        };
        updated.Save(_home);
        _message.Text = "安全策略已保存（重启后生效）";
    }

    private async Task RefreshStatusAsync()
    {
        _mcpPluginText.Text = "刷新中…";
        var mcp = await RunCommandAsync("/mcp");
        var plugins = await RunCommandAsync("/plugins");
        _mcpPluginText.Text = $"MCP:\n{mcp}\n\nPlugins:\n{plugins}";
    }

    private void RefreshStatus()
    {
        _ = RefreshStatusAsync();
    }

    private void RefreshProviders()
    {
        var settings = HarnessSettings.Load(_home);
        _providersList.Items.Clear();
        foreach (var entry in settings.Providers)
        {
            var item = new ListBoxItem
            {
                Tag = new ProviderSettingsRow(entry.Key, entry.Value),
                Content = new AvaloniaTextBlock
                {
                    Text = $"{entry.Key}: {entry.Value.Type} {entry.Value.Options?.BaseUrl}",
                    TextWrapping = TextWrapping.Wrap,
                    Margin = new Thickness(4),
                },
            };
            _providersList.Items.Add(item);
        }
        _providerName.Text = "";
        _providerType.Text = "";
        _baseUrl.Text = "";
        _apiKey.Text = "";
        _apiKeyEnv.Text = "";
        _modelIds.Text = "";
    }

    private void RefreshModelCombo()
    {
        _suppressModelSelection = true;
        try
        {
            var settings = HarnessSettings.Load(_home);
            _modelCombo.Items.Clear();
            foreach (var provider in settings.Providers)
            {
                foreach (var model in provider.Value.Models.Keys)
                    _modelCombo.Items.Add($"{provider.Key}/{model}");
            }
            if (settings.ResolveDefaultModel() is { } resolved && _modelCombo.Items.Count == 0)
                _modelCombo.Items.Add($"{resolved.Provider}/{resolved.Model}");
            _modelCombo.SelectedIndex = 0;
        }
        finally
        {
            _suppressModelSelection = false;
        }
    }

    private async Task<string> RunCommandAsync(string line)
    {
        var commands = _ctx.Get<CommandsService>(CommandsService.ServiceName);
        if (commands is null)
            return $"未知命令: {line}";
        try
        {
            var execution = await commands.Execute(Agent, line);
            return execution?.Result switch
            {
                CommandResult.Success { Text: { } text } => text,
                CommandResult.Error error => error.Text,
                _ => $"未知命令: {line}",
            };
        }
        catch (Exception error)
        {
            return error.Message;
        }
    }

    private static void AddIfSet(List<string> args, string option, string? value)
    {
        if (!string.IsNullOrWhiteSpace(value))
            args.Add(option);
        if (!string.IsNullOrWhiteSpace(value))
            args.Add(value.Trim());
    }

    private sealed record ProviderSettingsRow(string Name, ProviderSettings Settings);
}