using CommunityToolkit.Mvvm.ComponentModel;

namespace Dsh.Gui.ViewModels;

/** 输入胶囊的状态: 输入文本、权限/模型标签与提交条件; 具体动作由 MainViewModel 承担。 */
public sealed partial class ComposerViewModel : ObservableObject
{
    [ObservableProperty]
    private string _input = "";

    [ObservableProperty]
    private bool _isBusy;

    [ObservableProperty]
    private string _placeholder = "给智能体发消息";

    [ObservableProperty]
    private bool _canSubmit;

    [ObservableProperty]
    private string _permissionLabel = "Ask（每次审批）";

    [ObservableProperty]
    private string _modelLabel = "";

    [ObservableProperty]
    private string _reasoningLabel = "推理";

    [ObservableProperty]
    private bool _isAttachmentMenuOpen;

    [ObservableProperty]
    private bool _isPermissionMenuOpen;

    [ObservableProperty]
    private bool _isModelMenuOpen;

    [ObservableProperty]
    private bool _isRefreshMenuOpen;

    public void CloseMenus()
    {
        IsAttachmentMenuOpen = false;
        IsPermissionMenuOpen = false;
        IsModelMenuOpen = false;
        IsRefreshMenuOpen = false;
    }

    partial void OnInputChanged(string value) => CanSubmit = !IsBusy && value.Trim().Length > 0;

    partial void OnIsBusyChanged(bool value) => CanSubmit = !value && Input.Trim().Length > 0;
}
