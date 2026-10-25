using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Dsh.Gui.Services;
using Dsh.RemoteHost;

namespace Dsh.Gui.ViewModels;

/**
 * 一台机器上某个根目录(通常是会话的项目路径)的文件浏览与轻量编辑, 全部经 RemoteFilesClient 的 host.file.* / host.fs.* 完成。
 * 用法: new FilesViewModel(files, rootPath, style) 后 await LoadAsync(); 本地文件选择由视图负责, 选好的本地路径交给 UploadAsync / DownloadAsync。
 * 远端路径按远端平台的分隔符拼接(RemotePaths), 不借用本机 OS 的 Path。
 */
public sealed partial class FilesViewModel(RemoteFilesClient files, string rootPath, RemotePathStyle style) : OperationViewModel
{
    /** 超过这个大小的文件不进编辑器, 只显示信息并提供下载。 */
    public const long MaxEditableBytes = 2 * 1024 * 1024;

    private const int LocalBufferBytes = 81920;
    private const string PartialSuffix = ".part";

    private readonly RemotePathStyle _style = style;

    public string RootPath => rootPath;

    /** 树的顶层: 只有根目录一个节点。 */
    public ObservableCollection<FileNodeViewModel> Roots { get; } = [];

    public ObservableCollection<FileTransferViewModel> Transfers { get; } = [];

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RenameCommand), nameof(DeleteCommand), nameof(OpenCommand))]
    private FileNodeViewModel? _selected;

    /** 新建文件夹 / 重命名使用的名称输入。 */
    [ObservableProperty]
    private string _nameInput = "";

    [ObservableProperty]
    private FileEditorViewModel? _editor;

    /** 新建、上传、刷新的目标目录: 选中目录本身, 选中文件时取其所在目录, 未选中时取根目录。 */
    public FileNodeViewModel? TargetDirectory => Selected is { IsDirectory: true } directory ? directory : Selected?.Parent ?? Roots.FirstOrDefault();

    public Task<bool> LoadAsync() => RunAsync(async () =>
    {
        var listing = await files.ListDirectoryAsync(rootPath);
        var name = RemotePaths.FileName(listing.Path.TrimEnd('/', '\\'), _style);
        var root = new FileNodeViewModel(this, new RemoteDirectoryEntry(name.Length == 0 ? listing.Path : name, listing.Path, true), null);
        root.ReplaceChildren(listing.Entries);
        Roots.Clear();
        Roots.Add(root);
        root.IsExpanded = true;
        Selected = root;
    });

    /** 列举(或重新列举)一个目录的子项; 展开目录时自动调用。 */
    public Task<bool> LoadChildrenAsync(FileNodeViewModel directory) => RunAsync(async () =>
    {
        var listing = await files.ListDirectoryAsync(directory.Path);
        directory.ReplaceChildren(listing.Entries);
    });

    /** 把本地文件逐个上传到 TargetDirectory(同名文件被替换); 每个文件一条可取消的传输记录。 */
    public async Task UploadAsync(IReadOnlyList<string> localPaths)
    {
        if (TargetDirectory is not { } directory)
            return;
        foreach (var localPath in localPaths)
            await UploadOneAsync(localPath, directory.Path);
        await LoadChildrenAsync(directory);
    }

    /** 下载远端文件到本地路径: 先写同目录的 .part 文件, 完成后再替换目标, 取消或失败时删除 .part; 远端文件不存在时返回 null。 */
    public async Task<FileTransferViewModel?> DownloadAsync(string remotePath, string localPath)
    {
        RemoteFileStat? stat = null;
        if (!await RunAsync(async () => stat = await files.StatAsync(remotePath)) || stat is not { Exists: true, IsDirectory: false })
            return null;
        var transfer = Track(new FileTransferViewModel(TransferDirection.Download, RemotePaths.FileName(remotePath, _style), remotePath, localPath, stat.Size));
        var partial = $"{localPath}{PartialSuffix}";
        await transfer.RunAsync(async cancellationToken =>
        {
            long copied;
            await using (var file = new FileStream(partial, FileMode.Create, FileAccess.Write, FileShare.None, LocalBufferBytes, useAsync: true))
                copied = await files.DownloadFileAsync(remotePath, new TransferProgressStream(file, transfer.Report), cancellationToken);
            File.Move(partial, localPath, overwrite: true);
            return copied;
        });
        if (transfer.State != TransferState.Completed && File.Exists(partial))
            File.Delete(partial);
        return transfer;
    }

    [RelayCommand]
    private Task RefreshAsync() => TargetDirectory is { } directory ? LoadChildrenAsync(directory) : LoadAsync();

    [RelayCommand]
    private Task CreateFolderAsync() => RunAsync(async () =>
    {
        if (TargetDirectory is not { } directory)
            return;
        var name = ValidName();
        await files.CreateDirectoryAsync(RemotePaths.Resolve(directory.Path, name, _style));
        NameInput = "";
        await ReloadAsync(directory, $"已新建文件夹 {name}");
    });

    [RelayCommand(CanExecute = nameof(IsBelowRoot))]
    private Task RenameAsync() => RunAsync(async () =>
    {
        if (Selected is not { Parent: { } parent } node)
            return;
        var name = ValidName();
        await files.RenameAsync(node.Path, RemotePaths.Resolve(parent.Path, name, _style));
        NameInput = "";
        Selected = parent;
        await ReloadAsync(parent, $"已重命名为 {name}");
    });

    /** 文件直接删除; 目录连同内容一起删除, 先挂起确认。 */
    [RelayCommand(CanExecute = nameof(IsBelowRoot))]
    private Task DeleteAsync()
    {
        if (Selected is not { Parent: not null } node)
            return Task.CompletedTask;
        if (!node.IsDirectory)
            return DeleteNowAsync(node);
        Ask($"删除目录 {node.Path} 及其中的全部内容？此操作不能撤销。", "删除目录", () => DeleteNowAsync(node));
        return Task.CompletedTask;
    }

    [RelayCommand(CanExecute = nameof(IsFileSelected))]
    private async Task OpenAsync()
    {
        if (Selected is not { IsDirectory: false } node)
            return;
        var editor = new FileEditorViewModel(files, node.Path, _style, MaxEditableBytes);
        if (node.Parent is { } parent)
            editor.Saved += () => _ = LoadChildrenAsync(parent);
        Editor = editor;
        await editor.LoadAsync();
    }

    [RelayCommand]
    private void CloseEditor() => Editor = null;

    [RelayCommand]
    private void ClearFinishedTransfers()
    {
        foreach (var transfer in Transfers.Where(item => !item.IsRunning).ToList())
            Transfers.Remove(transfer);
    }

    private bool IsBelowRoot() => Selected is { Parent: not null };

    private bool IsFileSelected() => Selected is { IsDirectory: false, IsPlaceholder: false };

    private Task DeleteNowAsync(FileNodeViewModel node) => RunAsync(async () =>
    {
        await files.DeleteAsync(node.Path, recursive: node.IsDirectory);
        if (Editor?.Path == node.Path)
            Editor = null;
        Selected = node.Parent;
        await ReloadAsync(node.Parent!, $"已删除 {node.Name}");
    });

    private async Task UploadOneAsync(string localPath, string remoteDirectory)
    {
        var name = Path.GetFileName(localPath);
        var target = RemotePaths.Resolve(remoteDirectory, name, _style);
        var local = new FileInfo(localPath);
        var transfer = Track(new FileTransferViewModel(TransferDirection.Upload, name, target, localPath, local.Exists ? local.Length : 0));
        await transfer.RunAsync(async cancellationToken =>
        {
            await using var file = new FileStream(localPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, LocalBufferBytes, useAsync: true);
            return (await files.UploadFileAsync(target, new TransferProgressStream(file, transfer.Report), cancellationToken: cancellationToken)).Size;
        });
    }

    private FileTransferViewModel Track(FileTransferViewModel transfer)
    {
        Transfers.Add(transfer);
        return transfer;
    }

    private async Task ReloadAsync(FileNodeViewModel directory, string done)
    {
        var listing = await files.ListDirectoryAsync(directory.Path);
        directory.ReplaceChildren(listing.Entries);
        Report(done);
    }

    /** 名称输入必须是单个路径段。 */
    private string ValidName()
    {
        var name = NameInput.Trim();
        if (name.Length == 0 || name is "." or ".." || name.IndexOfAny(['/', '\\']) >= 0)
            throw new ArgumentException("请输入一个不含路径分隔符的名称");
        return name;
    }
}

