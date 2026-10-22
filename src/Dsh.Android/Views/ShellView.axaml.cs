using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Dsh.Android.ViewModels;
using Dsh.Llm;

namespace Dsh.Android.Views;

public sealed partial class ShellView : UserControl
{
    private MobileShellViewModel? _viewModel;

    public ShellView()
    {
        InitializeComponent();
        DataContextChanged += (_, _) => Attach();
    }

    protected override void OnAttachedToVisualTree(Avalonia.VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        Attach();
        Dispatcher.UIThread.Post(ScrollToEnd, DispatcherPriority.Loaded);
    }

    protected override void OnDetachedFromVisualTree(Avalonia.VisualTreeAttachmentEventArgs e)
    {
        Detach();
        base.OnDetachedFromVisualTree(e);
    }

    private void Attach()
    {
        if (_viewModel is not null)
            _viewModel.TranscriptChanged -= ScrollToEnd;
        _viewModel = DataContext as MobileShellViewModel;
        if (_viewModel is not null)
            _viewModel.TranscriptChanged += ScrollToEnd;
    }

    private void Detach()
    {
        if (_viewModel is null)
            return;
        _viewModel.TranscriptChanged -= ScrollToEnd;
        _viewModel = null;
    }

    private void ScrollToEnd()
    {
        if (_viewModel is null || _viewModel.Messages.Count == 0)
            return;
        MessageScroll.ScrollToEnd();
    }

    /** 图片选择走 Android 的系统选择器; 只把字节读进内存, 不保留 content URI 或流。 */
    private async void OnPickAttachment(object? sender, RoutedEventArgs e)
    {
        if (_viewModel is null)
            return;
        var storage = TopLevel.GetTopLevel(this)?.StorageProvider;
        if (storage is null)
            return;
        try
        {
            var files = await storage.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                Title = "选择图片",
                AllowMultiple = false,
                FileTypeFilter = [FilePickerFileTypes.ImageAll],
            });
            if (files.Count == 0)
                return;
            var file = files[0];
            using var buffer = new MemoryStream();
            await using (var stream = await file.OpenReadAsync())
                await stream.CopyToAsync(buffer);
            if (buffer.Length > ImageAttachments.MaxBytes)
            {
                _viewModel.ReportError($"图片不能超过 {ImageAttachments.MaxBytes / (1024 * 1024)} MB");
                return;
            }
            var bytes = buffer.ToArray();
            var mediaType = ImageAttachments.Sniff(bytes)
                ?? ImageAttachments.MediaTypeForExtension(Path.GetExtension(file.Name));
            if (mediaType is null)
            {
                _viewModel.ReportError("不支持的图片格式");
                return;
            }
            _viewModel.SetAttachment(file.Name, mediaType, bytes);
        }
        catch (Exception error)
        {
            _viewModel.ReportError($"选择图片失败: {error.Message}");
        }
    }

    private void OnClearAttachment(object? sender, RoutedEventArgs e) => _viewModel?.ClearAttachment();
}
