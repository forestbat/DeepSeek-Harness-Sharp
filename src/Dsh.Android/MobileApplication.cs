using Android.App;
using Android.Runtime;
using Avalonia;
using Avalonia.Android;

namespace Dsh.Android;

[Application]
public class MobileApplication : AvaloniaAndroidApplication<MobileApp>
{
    protected MobileApplication(nint javaReference, JniHandleOwnership transfer) : base(javaReference, transfer)
    {
    }

    protected override AppBuilder CustomizeAppBuilder(AppBuilder builder) => base.CustomizeAppBuilder(builder);
}
