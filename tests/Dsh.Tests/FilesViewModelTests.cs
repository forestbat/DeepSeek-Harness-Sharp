using System.Text;
using Dsh.Gui.ViewModels;
using Dsh.RemoteHost;

namespace Dsh.Tests;

/** 文件视图的视图模型对真实 host(进程内 harness 后端 + loopback 监听)的上传下载、目录操作与编辑保存。 */
[Collection(SerialProcessCollection.Name)]
public sealed class FilesViewModelTests : IDisposable
{
    private readonly string _local = TempTree.CreateDirectory("files-vm");

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public void Dispose() => TempTree.Delete(_local);

    [Fact(Timeout = 5000)]
    public async Task BinaryFileLargerThanOneChunk_RoundTripsThroughUploadAndDownload()
    {
        await using var host = await HarnessHostFixture.CreateAsync("fvt", TestContext.Current.CancellationToken);
        var (viewModel, root) = await OpenAsync(host);
        var content = new byte[(HostFileSystem.TransferChunkBytes * 5 / 2) + 17];
        new Random(20261015).NextBytes(content);
        var source = Path.Combine(_local, "blob.bin");
        await File.WriteAllBytesAsync(source, content, Ct);

        await viewModel.UploadAsync([source]);
        var upload = Assert.Single(viewModel.Transfers);
        var node = Assert.Single(viewModel.Roots[0].Children, child => child.Name == "blob.bin");
        var target = Path.Combine(_local, "copy.bin");
        var download = await viewModel.DownloadAsync(node.Path, target);

        Assert.Equal((TransferState.Completed, content.Length, 100d), (upload.State, upload.TransferredBytes, upload.Percent));
        Assert.Equal(content, await File.ReadAllBytesAsync(Path.Combine(root, "blob.bin"), Ct));
        Assert.Equal(content.Length, node.Entry.Size);
        Assert.NotNull(node.Entry.ModifiedAt);
        Assert.Equal((TransferState.Completed, (long)content.Length), (download!.State, download.TransferredBytes));
        Assert.Equal(content, await File.ReadAllBytesAsync(target, Ct));
        Assert.Equal(["blob.bin", "copy.bin"], Directory.GetFiles(_local).Select(Path.GetFileName).Order());
    }

    [Fact(Timeout = 5000)]
    public async Task CancelledUpload_LeavesNoRemoteFile()
    {
        await using var host = await HarnessHostFixture.CreateAsync("fvc", TestContext.Current.CancellationToken);
        var (viewModel, root) = await OpenAsync(host);
        var source = Path.Combine(_local, "large.bin");
        await File.WriteAllBytesAsync(source, new byte[HostFileSystem.TransferChunkBytes * 64], Ct);
        viewModel.Transfers.CollectionChanged += (_, change) =>
        {
            var transfer = (FileTransferViewModel)change.NewItems![0]!;
            transfer.PropertyChanged += (_, _) =>
            {
                if (transfer.TransferredBytes > 0 && transfer.IsRunning)
                    transfer.CancelCommand.Execute(null);
            };
        };

        await viewModel.UploadAsync([source]);

        Assert.Equal(TransferState.Cancelled, Assert.Single(viewModel.Transfers).State);
        await FleetConnectionTests.WaitForAsync(() => Directory.GetFileSystemEntries(root).Length == 0);
        Assert.DoesNotContain(viewModel.Roots[0].Children, child => child.Name == "large.bin");
    }

    [Fact(Timeout = 5000)]
    public async Task FolderOperations_CreateRenameAndDeleteOnlyAfterConfirmingDirectories()
    {
        await using var host = await HarnessHostFixture.CreateAsync("fvd", TestContext.Current.CancellationToken);
        var (viewModel, root) = await OpenAsync(host);
        await File.WriteAllTextAsync(Path.Combine(root, "note.txt"), "note", Ct);

        viewModel.NameInput = "docs";
        await viewModel.CreateFolderCommand.ExecuteAsync(null);
        viewModel.Selected = viewModel.Roots[0].Children.Single(child => child.Name == "docs");
        viewModel.NameInput = "manuals";
        await viewModel.RenameCommand.ExecuteAsync(null);
        var renamed = viewModel.Roots[0].Children.Single(child => child.Name == "manuals");
        await File.WriteAllTextAsync(Path.Combine(renamed.Path, "inside.txt"), "x", Ct);
        viewModel.Selected = viewModel.Roots[0].Children.Single(child => child.Name == "note.txt");
        await viewModel.DeleteCommand.ExecuteAsync(null);
        viewModel.Selected = renamed;
        await viewModel.DeleteCommand.ExecuteAsync(null);
        var confirmation = viewModel.Confirmation;
        var existedBeforeConfirming = Directory.Exists(renamed.Path);
        await confirmation!.AcceptCommand.ExecuteAsync(null);

        Assert.True(existedBeforeConfirming);
        Assert.Contains("manuals", confirmation.Message, StringComparison.Ordinal);
        Assert.Null(viewModel.Confirmation);
        Assert.Empty(Directory.GetFileSystemEntries(root));
        Assert.Empty(viewModel.Roots[0].Children);
        Assert.False(viewModel.StatusIsError, viewModel.Status);
    }

    [Fact(Timeout = 5000)]
    public async Task Editor_SavesAtomically_KeepsCrlfAndBom_AndAsksBeforeOverwritingARemoteChange()
    {
        await using var host = await HarnessHostFixture.CreateAsync("fve", TestContext.Current.CancellationToken);
        var (viewModel, root) = await OpenAsync(host);
        var path = Path.Combine(root, "app.cs");
        byte[] original = [.. Encoding.UTF8.Preamble, .. "line1\r\nline2\r\n"u8];
        await File.WriteAllBytesAsync(path, original, Ct);
        await viewModel.LoadChildrenAsync(viewModel.Roots[0]);
        viewModel.Selected = viewModel.Roots[0].Children.Single();

        await viewModel.OpenCommand.ExecuteAsync(null);
        var editor = viewModel.Editor!;
        var opened = (editor.Mode, editor.Text, editor.IsDirty, editor.Extension);
        editor.Text += "line3\r\n";
        await editor.SaveCommand.ExecuteAsync(null);
        var saved = await File.ReadAllBytesAsync(path, Ct);

        await File.WriteAllTextAsync(path, "changed elsewhere, and longer\n", Ct);
        editor.Text += "line4\r\n";
        await editor.SaveCommand.ExecuteAsync(null);
        var conflict = editor.Confirmation;
        var untouched = await File.ReadAllTextAsync(path, Ct);
        await conflict!.AcceptCommand.ExecuteAsync(null);
        var overwritten = await File.ReadAllBytesAsync(path, Ct);
        editor.Text += "line5\r\n";
        await editor.SaveCommand.ExecuteAsync(null);

        Assert.Equal((FileEditorMode.Text, "line1\r\nline2\r\n", false, ".cs"), opened);
        Assert.Equal([.. Encoding.UTF8.Preamble, .. "line1\r\nline2\r\nline3\r\n"u8], saved);
        Assert.Equal("changed elsewhere, and longer\n", untouched);
        Assert.Equal([.. Encoding.UTF8.Preamble, .. "line1\r\nline2\r\nline3\r\nline4\r\n"u8], overwritten);
        Assert.Null(editor.Confirmation);
        Assert.False(editor.IsDirty);
        Assert.Equal("line1\r\nline2\r\nline3\r\nline4\r\nline5\r\n", Encoding.UTF8.GetString(await File.ReadAllBytesAsync(path, Ct)).TrimStart('﻿'));
        Assert.Empty(Directory.GetFiles(root, "*.part"));
    }

    [Fact(Timeout = 5000)]
    public async Task Editor_ShowsBinaryAndOversizedFilesAsInformationOnly()
    {
        await using var host = await HarnessHostFixture.CreateAsync("fvb", TestContext.Current.CancellationToken);
        var (viewModel, root) = await OpenAsync(host);
        await File.WriteAllBytesAsync(Path.Combine(root, "image.bin"), [0x89, 0x50, 0x00, 0x47], Ct);
        await File.WriteAllBytesAsync(Path.Combine(root, "huge.log"), Encoding.ASCII.GetBytes(new string('a', (int)FilesViewModel.MaxEditableBytes + 1)), Ct);
        await viewModel.LoadChildrenAsync(viewModel.Roots[0]);

        var modes = new List<(FileEditorMode, bool, string)>();
        foreach (var node in viewModel.Roots[0].Children)
        {
            viewModel.Selected = node;
            await viewModel.OpenCommand.ExecuteAsync(null);
            modes.Add((viewModel.Editor!.Mode, viewModel.Editor.IsInfoOnly, viewModel.Editor.Text));
        }

        Assert.Equal([(FileEditorMode.TooLarge, true, ""), (FileEditorMode.Binary, true, "")], modes);
    }

    [Fact(Timeout = 5000)]
    public async Task PathsOutsideHostRoots_AreRejectedByTheHost_NotTheFileView()
    {
        await using var host = await HarnessHostFixture.CreateAsync("fvp", Ct);
        var client = await host.ConnectAsync("files-view", Ct);
        var outside = Path.GetPathRoot(host.Environment.App.Home.Root)!;
        var viewModel = new FilesViewModel(client.Files, outside, RemotePaths.StyleFor((await client.InfoAsync(TestContext.Current.CancellationToken)).Platform));

        Assert.False(await viewModel.LoadAsync());
        Assert.True(viewModel.StatusIsError);
        Assert.Contains("outside", viewModel.Status, StringComparison.Ordinal);
        Assert.Empty(viewModel.Roots);
        Assert.Null(await viewModel.DownloadAsync(Path.Combine(outside, "dsh-forbidden-probe"), Path.Combine(_local, "copy.bin")));
        Assert.Contains("outside", viewModel.Status, StringComparison.Ordinal);
        Assert.Empty(viewModel.Transfers);
        Assert.Empty(Directory.GetFileSystemEntries(_local));
    }

    /** 文件放在 host 的 harness home 下(属于 host 的允许根)。 */
    private static async Task<(FilesViewModel ViewModel, string Root)> OpenAsync(HarnessHostFixture host)
    {
        var root = Directory.CreateDirectory(Path.Combine(host.Environment.App.Home.Root, "project")).FullName;
        var client = await host.ConnectAsync("files-view", Ct);
        var viewModel = new FilesViewModel(client.Files, root, RemotePaths.StyleFor((await client.InfoAsync(Ct)).Platform));
        Assert.True(await viewModel.LoadAsync(), viewModel.Status);
        return (viewModel, root);
    }
}
