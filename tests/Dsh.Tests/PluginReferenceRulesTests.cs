using System.Xml.Linq;

namespace Dsh.Tests;

/** Guards the plugin split boundaries that the RemoteHost/Fleet plan established. */
public sealed class PluginReferenceRulesTests
{
    [Fact]
    public void RemoteHostServerDoesNotReferenceClientOrGitImplementation()
    {
        var references = References("src/Dsh.RemoteHost.Server/Dsh.RemoteHost.Server.csproj");
        Assert.DoesNotContain("Dsh.RemoteHost.Client", references);
        Assert.DoesNotContain("Dsh.GitInspect", references);
    }

    [Fact]
    public void GuiDoesNotReferenceRemoteHostServerGitInspectOrIroh()
    {
        var references = References("src/Dsh.Gui/Dsh.Gui.csproj");
        Assert.DoesNotContain("Dsh.RemoteHost.Server", references);
        Assert.DoesNotContain("Dsh.GitInspect", references);
        Assert.DoesNotContain("Dsh.Fleet.Iroh", references);
    }

    [Fact]
    public void FleetDoesNotReferenceServerOrIroh()
    {
        var references = References("src/Dsh.Fleet/Dsh.Fleet.csproj");
        Assert.DoesNotContain("Dsh.RemoteHost.Server", references);
        Assert.DoesNotContain("Dsh.Fleet.Iroh", references);
    }

    private static HashSet<string> References(string relative)
    {
        var document = XDocument.Load(Path.Combine(Root(), relative));
        return [.. document.Descendants("ProjectReference")
            .Select(element => element.Attribute("Include")?.Value)
            .OfType<string>()
            .Select(path => Path.GetFileNameWithoutExtension(path))];
    }

    private static string Root()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
            if (File.Exists(Path.Combine(directory.FullName, "DeepSeek-Harness-Sharp.slnx")))
                return directory.FullName;
        throw new InvalidOperationException("repository root not found");
    }
}
