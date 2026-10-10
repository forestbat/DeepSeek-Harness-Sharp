using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Dsh.RemoteHost;

namespace Dsh.Gui.ViewModels;

/** “选择远端目录”对话框状态: 经 IRemoteHost 逐层浏览远端目录树, 选定即回填路径。 */
public sealed partial class RemoteFolderPickerViewModel(IRemoteHost host) : ObservableObject
{
    private string? _parent;

    public ObservableCollection<RemoteDirectoryEntry> Directories { get; } = [];

    [ObservableProperty]
    private string _path = "";

    [ObservableProperty]
    private string? _status;

    [ObservableProperty]
    private bool _busy;

    public string? Result { get; private set; }

    public event Action? Accepted;

    public event Action? Cancelled;

    public async Task NavigateAsync(string? target)
    {
        if (Busy)
            return;
        Busy = true;
        Status = null;
        try
        {
            var listing = await host.ListDirectoryAsync(string.IsNullOrWhiteSpace(target) ? "~" : target);
            _parent = listing.Parent;
            Path = listing.Path;
            Directories.Clear();
            foreach (var entry in listing.Entries.Where(item => item.IsDirectory))
                Directories.Add(entry);
        }
        catch (Exception error)
        {
            Status = error.Message;
        }
        finally
        {
            Busy = false;
        }
    }

    [RelayCommand]
    private Task GoAsync() => NavigateAsync(Path);

    [RelayCommand]
    private Task RefreshAsync() => NavigateAsync(Path);

    [RelayCommand]
    private Task UpAsync() => NavigateAsync(_parent ?? Path);

    [RelayCommand]
    private Task OpenAsync(RemoteDirectoryEntry entry) => NavigateAsync(entry.Path);

    [RelayCommand]
    private void Accept()
    {
        Result = Path;
        Accepted?.Invoke();
    }

    [RelayCommand]
    private void Cancel() => Cancelled?.Invoke();
}
