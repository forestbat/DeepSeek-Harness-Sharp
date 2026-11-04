using System.Text;
using System.Text.Json;
using Dsh.RemoteHost;

namespace Dsh.Ide.Bridge;

public static class EditorIntegrationConfiguration
{
    /// <summary>Configuration for an extension using the authenticated Host transport, not a shell or a workspace command.</summary>
    public static string GenerateExtension(string endpoint, string? tokenFile = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(endpoint);
        if (endpoint.Any(char.IsControl) || tokenFile?.Any(char.IsControl) == true)
            throw new ArgumentException("IDE bridge configuration cannot contain control characters.");
        return WriteJson(writer =>
        {
            writer.WriteStartObject();
            writer.WriteNumber("protocolVersion", EditorProtocol.Version);
            writer.WriteString("endpoint", endpoint);
            if (tokenFile is not null) writer.WriteString("tokenFile", tokenFile);
            writer.WriteString("framing", "uint32-big-endian-length-prefixed-utf8-jsonrpc-2.0");
            writer.WriteNumber("maxFrameBytes", 16 * 1024 * 1024);
            writer.WriteString("helloMethod", HostProtocol.MethodHello);
            writer.WriteNumber("hostProtocolVersion", HostProtocol.Version);
            writer.WriteString("capability", EditorProtocol.Capability);
            writer.WriteString("clientCapability", EditorProtocol.ClientCapability);
            writer.WriteString("registerMethod", EditorProtocol.MethodRegister);
            writer.WriteString("unregisterMethod", EditorProtocol.MethodUnregister);
            writer.WriteString("selectSessionMethod", EditorProtocol.MethodSelect);
            writer.WriteString("openMethod", EditorProtocol.MethodOpen);
            writer.WriteString("cancelMethod", EditorProtocol.MethodCancel);
            writer.WriteString("clientOpenMethod", EditorProtocol.MethodClientOpen);
            writer.WriteString("clientCancelMethod", EditorProtocol.MethodClientCancel);
            writer.WriteEndObject();
        });
    }

    /// <summary>Returns client configuration only; never modifies a workspace or the user's IDE settings.</summary>
    public static string GenerateLsp(string client, string executable = "dsharp")
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(executable);
        if (executable.Any(char.IsControl)) throw new ArgumentException("LSP executable cannot contain control characters.");
        if (client.Equals("neovim", StringComparison.OrdinalIgnoreCase) || client.Equals("nvim", StringComparison.OrdinalIgnoreCase))
        {
            var quoted = JsonString(executable);
            return $"vim.lsp.config('dsh', {{ cmd = {{ {quoted}, 'host', 'serve', '--stdio' }}, filetypes = {{ 'c', 'cpp', 'cs', 'go', 'java', 'javascript', 'lua', 'python', 'rust', 'typescript' }}, root_markers = {{ '.git', '.dsh' }} }})\nvim.lsp.enable('dsh')\n";
        }
        if (client.Equals("vscode", StringComparison.OrdinalIgnoreCase))
        {
            // vscode-languageclient ServerOptions, usable directly by an extension activation function.
            return WriteJson(writer =>
            {
                writer.WriteStartObject();
                writer.WritePropertyName("serverOptions");
                writer.WriteStartObject();
                writer.WriteString("command", executable);
                writer.WritePropertyName("args"); writer.WriteStartArray();
                writer.WriteStringValue("host"); writer.WriteStringValue("serve");
                writer.WriteStringValue("--stdio");
                writer.WriteEndArray();
                writer.WriteEndObject();
                writer.WritePropertyName("clientOptions"); writer.WriteStartObject();
                writer.WritePropertyName("documentSelector"); writer.WriteStartArray();
                writer.WriteStartObject(); writer.WriteString("scheme", "file"); writer.WriteEndObject();
                writer.WriteEndArray(); writer.WriteEndObject(); writer.WriteEndObject();
            });
        }
        if (client.Equals("helix", StringComparison.OrdinalIgnoreCase))
            return $"[language-server.dsh]\ncommand = {JsonString(executable)}\nargs = [\"host\", \"serve\", \"--stdio\"]\n\n[[language]]\nname = \"c-sharp\"\nlanguage-servers = [\"dsh\"]\n";
        throw new ArgumentException("Supported LSP configuration clients: neovim, vscode, helix.");
    }

    private static string JsonString(string value) => WriteJson(writer => writer.WriteStringValue(value));
    private static string WriteJson(Action<Utf8JsonWriter> write)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping })) write(writer);
        return Encoding.UTF8.GetString(stream.GetBuffer(), 0, checked((int)stream.Length));
    }
}
