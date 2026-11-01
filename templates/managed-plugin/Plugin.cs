using System.Text.Json;
using Dsh.Plugins;

[assembly: DshPlugin("@example/dsh-plugin-template")]

namespace DshPluginTemplate;

// Registration remains IDshPlugin-compatible; initialization receives capabilities, never Context.
public sealed class Plugin : IDshIsolatedPlugin
{
    public ValueTask<IAsyncDisposable?> InitializeAsync(
        IPluginHost host,
        JsonElement configuration,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        List<IDisposable> registrations = [];
        try
        {
            Register("template_read_file", "Read a bounded file chunk through the host.", PluginHostOperations.ReadFile,
                """{"type":"object","required":["path"],"properties":{"path":{"type":"string"},"offset":{"type":"integer","minimum":0},"count":{"type":"integer","minimum":0}},"additionalProperties":false}""");
            Register("template_write_file", "Write base64 bytes through the host permission and settings gate.", PluginHostOperations.WriteFile,
                """{"type":"object","required":["path","data"],"properties":{"path":{"type":"string"},"data":{"type":"string"},"append":{"type":"boolean"}},"additionalProperties":false}""");
            Register("template_http", "Send an HTTP request through the host network capability.", PluginHostOperations.SendHttp,
                """{"type":"object","required":["url"],"properties":{"url":{"type":"string"},"method":{"type":"string"},"headers":{"type":"object","additionalProperties":{"type":"string"}},"body":{"type":"string"}},"additionalProperties":false}""");
            host.Log(1, "example capability tools registered");
            return ValueTask.FromResult<IAsyncDisposable?>(new RegistrationScope([.. registrations]));
        }
        catch
        {
            foreach (var registration in registrations.AsEnumerable().Reverse())
                registration.Dispose();
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

    private sealed class RegistrationScope(IDisposable[] registrations) : IAsyncDisposable
    {
        public ValueTask DisposeAsync()
        {
            foreach (var registration in registrations.Reverse())
                registration.Dispose();
            return ValueTask.CompletedTask;
        }
    }
}
