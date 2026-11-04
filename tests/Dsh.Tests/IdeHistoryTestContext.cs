using Dsh.Core;
using Dsh.Ide;
using Dsh.Ide.Catalog;
using Dsh.Runtime;

namespace Dsh.Tests;

internal static class IdeHistoryTestContext
{
    internal static Context CreateHistoryContext(Context? context = null, EditorOptions? options = null)
    {
        context ??= new Context();
        options ??= EditorOptions.From(null);
        context.Provide(IEditorCatalog.ServiceName, new EditorCatalog(options.Editors,
            new EditorDetector(options.InstallationRoots, options.BundleRoots, options.DesktopRoots)));
        return context;
    }
}
