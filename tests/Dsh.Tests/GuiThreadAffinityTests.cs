using System.Reflection;
using Avalonia.Media;
using Avalonia.Media.Immutable;

namespace Dsh.Tests;

/**
 * GUI 视图的静态字段不得持有带线程归属的 AvaloniaObject。
 *
 * SolidColorBrush 的每个实例都属于创建它的那个 Dispatcher: 一旦进了 static 字段, 就等于把"第一次用到它的 UI 线程"
 * 钉死 —— 之后在另一条 UI 线程上读它(格式化、取属性)会抛 "The calling thread cannot access this object"。
 * 真实触发路径是 headless 测试: 一条用例卡住后会话被换掉, 后续用例跑在新线程上, 于是整批连锁失败;
 * 对产品而言, 任何"换一条 UI 线程重建界面"的路径都会踩同一个坑。颜色都是常量, 一律用不可变实现。
 */
public sealed class GuiThreadAffinityTests
{
    [Fact]
    public void ViewStaticBrushes_AreImmutable()
    {
        var offenders = typeof(Dsh.Gui.Views.MarkdownView).Assembly.GetTypes()
            .SelectMany(type => type.GetFields(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic))
            .Where(field => typeof(IBrush).IsAssignableFrom(field.FieldType))
            .Select(field => (Field: field, Value: field.GetValue(null)))
            .Where(entry => entry.Value is null or not ImmutableSolidColorBrush)
            .Select(entry => $"{entry.Field.DeclaringType!.Name}.{entry.Field.Name} = {entry.Value?.GetType().Name ?? "null"}")
            .ToList();
        Assert.Empty(offenders);
    }
}
