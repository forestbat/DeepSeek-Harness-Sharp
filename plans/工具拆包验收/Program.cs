using System.Text.Json.Nodes;
using Dsh.Boot;
using Dsh.Core;
using Dsh.Plugins;
using Dsh.Runtime;
using Dsh.Runtime.Composition;

if (args.Length >= 3 && args[0] == "capability-catalog")
{
    _ = typeof(Dsh.Host.HarnessEntrypoint).Assembly;
    var ctx = new Context();
    _ = new SystemPrompt(ctx, new SystemPromptConfig());
    var runtime = new ToolRuntime(ctx);
    using var subprocess = new SubprocessService(ctx);
    var host = new PluginHost();
    host.RegisterCompiledIn();
    var entries = new List<PluginEntry>();
    foreach (var package in args.Skip(2).OrderBy(name => name, StringComparer.Ordinal))
    {
        if (!host.Catalog.TryCreateDefinition(package, out var definition))
            throw new InvalidOperationException($"Package not found: {package}");
        entries.Add(new PluginEntry(definition!, null));
    }
    var composition = await Composition.StartAsync(ctx, entries);
    try
    {
        await Capture(runtime, args[1]);
    }
    finally
    {
        composition.DeactivateAll();
    }
    return;
}

if (args is ["catalog-without", var configuredHome, var configuredOutput, var disabledPackage])
{
    var home = new HarnessHome(Path.GetFullPath(configuredHome));
    var settings = HarnessSettings.Load(home);
    settings.Plugins[disabledPackage] = new PluginSetting { Enabled = false };
    settings.SavePlugins(home);
    args = ["catalog", configuredHome, configuredOutput];
}
if (args is ["template"])
{
    _ = typeof(Dsh.Host.HarnessEntrypoint).Assembly;
    await Console.Out.WriteAsync(SettingsSchemaCatalog.RenderTemplateBlock());
    return;
}
if (args is not ["catalog", var homePath, var outputPath])
    throw new ArgumentException("Usage: catalog <home-path> <output-json-path>");
_ = typeof(Dsh.Host.HarnessEntrypoint).Assembly;
using var app = await ConfigBoot.Compose(new HarnessOptions(new HarnessHome(Path.GetFullPath(homePath)), Directory.GetCurrentDirectory()));
var failed = app.Composition!.Activations.Where(activation => activation.State == Dsh.Runtime.ActivationState.Failed).ToArray();
if (failed.Length > 0)
    throw new InvalidOperationException(string.Join("\n", failed.Select(activation => $"{activation.Name}: {activation.Error}")));
var tools = app.Ctx.Get<ToolRuntime>(ToolRuntime.ServiceName)!;
await Capture(tools, outputPath);

static async Task Capture(ToolRuntime tools, string outputPath)
{
    var catalog = new JsonArray();
    foreach (var schema in tools.Schemas())
    {
        catalog.Add(new JsonObject
        {
            ["name"] = schema.Name,
            ["description"] = schema.Description,
            ["parameters"] = schema.Parameters.DeepClone(),
            ["output"] = tools.Get(schema.Name)!.Output.Schema.DeepClone(),
        });
    }
    await File.WriteAllTextAsync(outputPath, catalog.ToJsonString(new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
    await Console.Out.WriteLineAsync($"CATALOG_OK count={catalog.Count} output={outputPath}");
}
