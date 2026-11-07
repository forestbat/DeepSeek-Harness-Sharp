using System.Text.Json;
using Dsh.Core;
using Dsh.Plugins;
using Dsh.Runtime;

[assembly: DshPlugin("@example/dsh-plugin-template")]

namespace DshPluginTemplate;

// A plugin is a composition unit whose Apply result releases its registered effects on unload.
// Keep one IDshPlugin implementation per assembly, with a public parameterless or package-name constructor.
public sealed class Plugin : IDshPlugin
{
    public string[] Inject => [];

    // Dependencies gate activation and rebuild this plugin when a required service is replaced.
    public Type[] InjectTypes => [typeof(SystemPrompt), typeof(ToolRuntime), typeof(IPluginHostFactory)];

    // The host binds this plugin's parameters to the declared configuration type before Apply.
    public Type ConfigType => typeof(TemplateConfig);

    public IDisposable Apply(Context ctx, object? config)
    {
        var options = config as TemplateConfig ?? new TemplateConfig();
        var prompt = ctx.Get<SystemPrompt>()!;
        var host = ctx.Get<IPluginHostFactory>()!.Create("@example/dsh-plugin-template", ctx);
        List<IDisposable> registrations = [];
        try
        {
            registrations.Add(prompt.Section(PromptSection.Literal("template:hello", options.Message)));
            Register("template_read_file", "Read a bounded file chunk through the host.", PluginHostOperations.ReadFile,
                """{"type":"object","required":["path"],"properties":{"path":{"type":"string"},"offset":{"type":"integer","minimum":0},"count":{"type":"integer","minimum":0}},"additionalProperties":false}""");
            Register("template_write_file", "Write base64 bytes through the host permission and settings gate.", PluginHostOperations.WriteFile,
                """{"type":"object","required":["path","data"],"properties":{"path":{"type":"string"},"data":{"type":"string"},"append":{"type":"boolean"}},"additionalProperties":false}""");
            Register("template_http", "Send an HTTP request through the host network capability.", PluginHostOperations.SendHttp,
                """{"type":"object","required":["url"],"properties":{"url":{"type":"string"},"method":{"type":"string"},"headers":{"type":"object","additionalProperties":{"type":"string"}},"body":{"type":"string"}},"additionalProperties":false}""");
            host.Log(1, "example capability tools registered");
            return new RegistrationScope([.. registrations]);
        }
        catch
        {
            for (var index = registrations.Count - 1; index >= 0; index--)
                registrations[index].Dispose();
            throw;
        }

        void Register(string name, string description, string operation, string schema)
        {
            using var document = JsonDocument.Parse(schema);
            registrations.Add(host.RegisterTool(new PluginToolDefinition(
                name, description, document.RootElement.Clone(),
                (arguments, token) => host.CallAsync(operation, arguments, token))));
        }
    }

    private sealed class RegistrationScope(IDisposable[] registrations) : IDisposable
    {
        public void Dispose()
        {
            for (var index = registrations.Length - 1; index >= 0; index--)
                registrations[index].Dispose();
        }
    }
}

public sealed record TemplateConfig
{
    public string Message { get; init; } = "This deployment includes the example template plugin.";
}

// Tool names that are valid C# identifiers support tools.<name>(args); other names use tools.call(name, args).
// A duplicate tool name is renamed to <package-suffix>-<name> with a warning.
// Declare DshSharedDependency for assemblies whose types cross plugin boundaries, including typed service contracts.
// Keep implementation-only dependencies private to the plugin instead of sharing them.
