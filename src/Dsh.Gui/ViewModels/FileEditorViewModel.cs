using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Dsh.Gui.Services;
using Dsh.RemoteHost;

namespace Dsh.Gui.ViewModels;

public enum FileEditorMode
{
    Loading,

    /** 文本文件, 可在编辑器里修改并保存。 */
    Text,

    /** 二进制文件: 只显示信息, 提供下载。 */
    Binary,

    /** 超过可编辑大小上限: 只显示信息, 提供下载。 */
    TooLarge,
}

/**
 * 一个远端文件的轻量查看与编辑。
 * 打开时先 stat 再读内容, 记下(大小, 修改时间)作为基线; 保存前再 stat 一次, 与基线不同说明远端在打开后被改过, 先挂起确认再覆盖。
 * 保存走分块上传, host 写同目录临时文件后原子替换; 上传返回的新 stat 成为下一次保存的基线。
 */
public sealed partial class FileEditorViewModel(IWorkspaceFiles files, string path, RemotePathStyle style, long maxEditableBytes) : OperationViewModel
{
    private TextFileContent? _content;
    private RemoteFileStat? _baseline;
    private bool _settingText;

    public string Path => path;

    public string FileName => RemotePaths.FileName(path, style);

    /** 带点的扩展名(如 ".cs"), 视图据此选语法高亮。 */
    public string Extension => RemotePaths.Extension(path, style);

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsText), nameof(IsInfoOnly))]
    [NotifyCanExecuteChangedFor(nameof(SaveCommand))]
    private FileEditorMode _mode = FileEditorMode.Loading;

    [ObservableProperty]
    private string _text = "";

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SaveCommand))]
    private bool _isDirty;

    /** 二进制或超大文件的说明(类型、大小、修改时间)。 */
    [ObservableProperty]
    private string? _info;

    public bool IsText => Mode == FileEditorMode.Text;

    public bool IsInfoOnly => Mode is FileEditorMode.Binary or FileEditorMode.TooLarge;

    /** 文本被整体替换(打开、重新加载)后触发, 视图据此重置编辑器内容; 用户输入引起的变化不触发。 */
    public event Action? TextReloaded;

    /** 保存成功后触发(远端大小与修改时间已变), 文件树据此刷新所在目录。 */
    public event Action? Saved;

    partial void OnTextChanged(string value)
    {
        if (!_settingText)
            IsDirty = true;
    }

    public Task<bool> LoadAsync() => RunAsync(LoadContentAsync);

    [RelayCommand(CanExecute = nameof(CanSave))]
    private Task SaveAsync() => RunAsync(SaveCheckedAsync);

    private bool CanSave() => IsText && IsDirty;

    /** 放弃本地修改, 重新读取远端内容(有未保存修改时先确认)。 */
    [RelayCommand]
    private Task ReloadAsync()
    {
        if (!IsDirty)
            return LoadAsync();
        Ask($"放弃对 {FileName} 的未保存修改并重新读取远端内容？", "重新加载", LoadAsync);
        return Task.CompletedTask;
    }

    private async Task LoadContentAsync()
    {
        var stat = await files.StatAsync(path);
        if (!stat.Exists || stat.IsDirectory)
            throw new FileNotFoundException($"远端文件不存在或不是文件: {path}", path);
        _baseline = stat;
        if (stat.Size > maxEditableBytes)
        {
            ShowInfo(FileEditorMode.TooLarge, $"文件过大（{FileSizeText.Format(stat.Size)}，超过 {FileSizeText.Format(maxEditableBytes)} 的编辑上限）");
            return;
        }

        using var buffer = new MemoryStream();
        await files.DownloadFileAsync(path, buffer);
        _content = TextFileContent.TryDecode(buffer.GetBuffer().AsSpan(0, (int)buffer.Length));
        if (_content is null)
        {
            ShowInfo(FileEditorMode.Binary, $"二进制文件（{FileSizeText.Format(stat.Size)}）");
            return;
        }

        ReplaceText(_content.Text);
        Mode = FileEditorMode.Text;
        Report($"已打开 {FileName}（{FileSizeText.Format(stat.Size)}，修改于 {FileSizeText.Time(stat.ModifiedAt)}）");
    }

    private async Task SaveCheckedAsync()
    {
        if (_baseline is not { } baseline)
            return;
        var current = await files.StatAsync(path);
        if (current.Exists && current.Size == baseline.Size && current.ModifiedAt == baseline.ModifiedAt)
        {
            await WriteAsync();
            return;
        }

        var change = current.Exists ? $"现在 {FileSizeText.Format(current.Size)}，修改于 {FileSizeText.Time(current.ModifiedAt)}" : "已被删除";
        Ask($"远端文件在打开后已被修改（{change}）。仍要用编辑器里的内容覆盖吗？", "覆盖保存", () => RunAsync(WriteAsync));
        Report("远端文件已变化，等待确认");
    }

    private async Task WriteAsync()
    {
        var bytes = (_content ?? throw new InvalidOperationException("file is not open as text")).Encode(Text);
        _baseline = await files.UploadFileAsync(path, new MemoryStream(bytes));
        IsDirty = false;
        Report($"已保存 {FileName}（{FileSizeText.Format(_baseline.Size)}）");
        Saved?.Invoke();
    }

    private void ShowInfo(FileEditorMode mode, string info)
    {
        _content = null;
        ReplaceText("");
        Info = $"{info}，修改于 {FileSizeText.Time(_baseline?.ModifiedAt ?? 0)}。可下载到本机查看。";
        Mode = mode;
    }

    private void ReplaceText(string text)
    {
        _settingText = true;
        Text = text;
        _settingText = false;
        IsDirty = false;
        TextReloaded?.Invoke();
    }
}

