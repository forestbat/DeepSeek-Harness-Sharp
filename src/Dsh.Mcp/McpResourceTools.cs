using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization.Metadata;
using Dsh.Core;
using Dsh.Llm;
using Dsh.Runtime;
using ModelContextProtocol;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using ContentBlock = Dsh.Llm.ContentBlock;

namespace Dsh.Mcp;

/**
 * 三个共享资源工具(list_mcp_resources / list_mcp_resource_templates / read_mcp_resource): 按 server 名选来源, 经 ToolRuntime 走统一的审批/dispatch 管道。
 * 文本内容原样交给模型, 超长部分由工具结果落盘机制截断并给出文件路径; 二进制内容存进内容寻址附件库, 结果值里不保留 base64。
 */
internal static class McpResourceTools
{
    public const string ListResourcesName = "list_mcp_resources";
    public const string ListTemplatesName = "list_mcp_resource_templates";
    public const string ReadResourceName = "read_mcp_resource";
    public const string UnknownServerCode = "MCP_SERVER_UNKNOWN";
    public const string SectionName = "tool:mcp_resources";

    private const string ServerParam = "server";
    private const string CursorParam = "cursor";
    private const string UriParam = "uri";
    private const string ContentsField = "contents";
    private const string ImageMediaPrefix = "image/";

    private static readonly JsonObject OutputSchema = new() { ["type"] = "object" };

    private const string ListingParameters = """
        {
          "type": "object",
          "properties": {
            "server": { "type": "string", "description": "MCP server name exactly as configured under mcp in settings.yaml; the MCP resources section of the system prompt lists the servers that expose resources." },
            "cursor": { "type": "string", "description": "Continuation cursor (nextCursor) returned by an earlier call to this tool for the same server; pass it back unchanged. Omit it to collect every page." }
          },
          "required": ["server"]
        }
        """;

    private const string ReadParameters = """
        {
          "type": "object",
          "properties": {
            "server": { "type": "string", "description": "MCP server name exactly as configured under mcp in settings.yaml; use the same name the URI was listed from." },
            "uri": { "type": "string", "description": "Resource URI: one listed by list_mcp_resources, or a resource template from list_mcp_resource_templates with every {variable} filled in." }
          },
          "required": ["server", "uri"]
        }
        """;

    public static IReadOnlyList<IDisposable> Register(Context ctx, McpService service)
    {
        var tools = ctx.Get<ToolRuntime>(ToolRuntime.ServiceName)!;
        var systemPrompt = ctx.Get<SystemPrompt>(SystemPrompt.ServiceName)!;
        return
        [
            systemPrompt.Section(new PromptSection(SectionName, _ => SectionText(service), ToolNames: [ListResourcesName, ListTemplatesName, ReadResourceName])),
            tools.Register(Listing(
                ListResourcesName,
                "List the resources (uri, name, mimeType, description, size) that one connected MCP server exposes. Without cursor every page is collected; with cursor only that page is returned and nextCursor names the next one. Read a listed resource with read_mcp_resource on the same server.",
                (handle, cursor, signal) => handle.CallListingAsync(client => cursor is null
                    ? CollectResourcesAsync(client, signal)
                    : PageAsync(client.ListResourcesAsync(new ListResourcesRequestParams { Cursor = cursor }, signal)), templates: false, complete: cursor is null, signal),
                service)),
            tools.Register(Listing(
                ListTemplatesName,
                "List the parameterized resource URI templates (RFC 6570, such as notes://{name}) that one connected MCP server exposes. Fill in every {variable} yourself and pass the resulting URI to read_mcp_resource on the same server. The cursor works as in list_mcp_resources.",
                (handle, cursor, signal) => handle.CallListingAsync(client => cursor is null
                    ? CollectTemplatesAsync(client, signal)
                    : PageAsync(client.ListResourceTemplatesAsync(new ListResourceTemplatesRequestParams { Cursor = cursor }, signal)), templates: true, complete: cursor is null, signal),
                service)),
            tools.Register(new ToolDefinition
            {
                Name = ReadResourceName,
                Description = "Read one MCP resource by URI from the named server. Text contents are returned as text; when a text is larger than the tool-result limit only its head and tail stay in the result and the full text is saved to a file whose path the result names, so page through that file with read (offset/limit). Binary contents are saved as content-addressed attachments and the result names the local file; view an image with read_image on that path.",
                Parameters = JsonNode.Parse(ReadParameters)!.AsObject(),
                Output = new ToolOutputDefinition(OutputSchema.DeepClone().AsObject(), RenderRead),
                Execute = (args, exec) => ReadAsync(ctx, service, args, exec),
                IsConcurrencySafe = _ => true,
            }),
        ];
    }

    private static ToolDefinition Listing(
        string name,
        string description,
        Func<McpServerHandle, string?, CancellationToken, ValueTask<JsonElement>> list,
        McpService service)
        => new()
        {
            Name = name,
            Description = description,
            Parameters = JsonNode.Parse(ListingParameters)!.AsObject(),
            Output = new ToolOutputDefinition(OutputSchema.DeepClone().AsObject(), RenderListing),
            Execute = async (args, exec) =>
            {
                var cursor = args.TryGetProperty(CursorParam, out var value) && value.ValueKind == JsonValueKind.String
                    ? value.GetString()
                    : null;
                return await list(Find(service, args), cursor, exec.Signal);
            },
            IsConcurrencySafe = _ => true,
        };

    private static async ValueTask<JsonElement> CollectResourcesAsync(McpClient client, CancellationToken signal)
    {
        var resources = await client.ListResourcesAsync(cancellationToken: signal);
        return ToElement(new ListResourcesResult { Resources = [.. resources.Select(resource => resource.ProtocolResource)] });
    }

    private static async ValueTask<JsonElement> CollectTemplatesAsync(McpClient client, CancellationToken signal)
    {
        var templates = await client.ListResourceTemplatesAsync(cancellationToken: signal);
        return ToElement(new ListResourceTemplatesResult { ResourceTemplates = [.. templates.Select(template => template.ProtocolResourceTemplate)] });
    }

    private static async ValueTask<JsonElement> PageAsync<T>(ValueTask<T> page) => ToElement(await page);

    private static async Task<object?> ReadAsync(Context ctx, McpService service, JsonElement args, ToolRunContext exec)
    {
        var uri = args.GetProperty(UriParam).GetString() ?? "";
        if (uri.Trim().Length == 0)
            throw new ArgumentException("uri must be a non-empty string");
        var result = await Find(service, args).CallResourcesAsync(
            client => client.ReadResourceAsync(uri, cancellationToken: exec.Signal),
            exec.Signal);
        var contents = result.Contents.Select(content => content is BlobResourceContents blob
            ? StoreBlob(ctx, blob)
            : JsonSerializer.SerializeToNode(content, TypeInfo<ResourceContents>()));
        return new JsonObject { [ContentsField] = new JsonArray([.. contents]) };
    }

    /** 二进制内容写进内容寻址附件库, 结果值只记 id、路径与字节数, 会话日志不背 base64。 */
    private static JsonObject StoreBlob(Context ctx, BlobResourceContents blob)
    {
        var bytes = blob.DecodedData.ToArray();
        var node = new JsonObject { [UriParam] = blob.Uri, ["mimeType"] = blob.MimeType, ["bytes"] = bytes.Length };
        if (bytes.Length == 0)
            return node;
        var store = ctx.Get<IAttachmentStore>(FileAttachmentStore.ServiceName, false)
            ?? throw new HarnessException($"cannot keep binary MCP resource \"{blob.Uri}\": no attachment store is mounted", "ATTACHMENTS_UNAVAILABLE");
        var stored = store.PutBytes(bytes);
        node["attachmentId"] = stored.AttachmentId;
        node["path"] = stored.Path;
        return node;
    }

    private static McpServerHandle Find(McpService service, JsonElement args)
    {
        var name = args.GetProperty(ServerParam).GetString() ?? "";
        var handles = service.Handles();
        return handles.FirstOrDefault(handle => handle.Config.Name == name)
            ?? throw new HarnessException(
                $"unknown mcp server \"{name}\"; configured servers: {string.Join(", ", handles.Select(handle => handle.Config.Name))}",
                UnknownServerCode);
    }

    private static IReadOnlyList<ContentBlock> RenderListing(JsonElement args, JsonElement value)
        => [new TextBlock($"MCP server: {args.GetProperty(ServerParam).GetString()}\n{value.GetRawText()}")];

    /** 每段内容各成一个文本块, 使落盘机制按段截断。 */
    private static IReadOnlyList<ContentBlock> RenderRead(JsonElement args, JsonElement value)
    {
        var server = args.GetProperty(ServerParam).GetString();
        var blocks = value.GetProperty(ContentsField).EnumerateArray()
            .Select(content => (ContentBlock)new TextBlock(RenderContent(server, content)))
            .ToList();
        return blocks.Count > 0
            ? blocks
            : [new TextBlock($"MCP server \"{server}\" returned no contents for {args.GetProperty(UriParam).GetString()}")];
    }

    private static string RenderContent(string? server, JsonElement content)
    {
        var mimeType = content.TryGetProperty("mimeType", out var mime) ? mime.GetString() : null;
        var header = $"MCP resource {content.GetProperty(UriParam).GetString()} from server \"{server}\"{(mimeType is null ? "" : $" ({mimeType})")}";
        if (content.TryGetProperty("text", out var text))
            return $"{header}:\n{text.GetString()}";
        if (!content.TryGetProperty("path", out var path))
            return $"{header} is an empty binary resource.";
        var hint = mimeType?.StartsWith(ImageMediaPrefix, StringComparison.OrdinalIgnoreCase) == true
            ? "call read_image with that path to view it"
            : "inspect that file with a tool suited to its format";
        return $"{header} is binary: {content.GetProperty("bytes").GetInt64()} bytes saved as content-addressed attachment {content.GetProperty("attachmentId").GetString()} at {path.GetString()}; {hint}.";
    }

    /** 动态段: 连接状态与 list_changed 通知变化时, 以 system-reminder 告知模型, 不改动静态提示词前缀。 */
    private static string SectionText(McpService service)
    {
        var connected = service.Handles().Where(handle => handle.Connected).OrderBy(handle => handle.Config.Name, StringComparer.Ordinal).ToList();
        var withResources = connected.Where(handle => handle.SupportsResources).ToList();
        var without = connected.Where(handle => !handle.SupportsResources).Select(handle => handle.Config.Name).ToList();
        var stale = withResources.Where(handle => handle.ResourceListStale).Select(handle => handle.Config.Name).ToList();
        var staleTemplates = withResources.Where(handle => handle.ResourceTemplatesStale).Select(handle => handle.Config.Name).ToList();
        List<string> lines =
        [
            "## MCP resources",
            withResources.Count > 0
                ? $"MCP servers with resources: {string.Join(", ", withResources.Select(handle => handle.Config.Name))}. Discover URIs with list_mcp_resources or list_mcp_resource_templates, then read one with read_mcp_resource using the same server name."
                : "No connected MCP server exposes resources.",
        ];
        if (without.Count > 0)
            lines.Add($"MCP servers without resources (the resource tools fail for them): {string.Join(", ", without)}.");
        if (stale.Count > 0)
            lines.Add($"The resource list changed on {string.Join(", ", stale)} after your last listing; call list_mcp_resources again before relying on it.");
        if (staleTemplates.Count > 0)
            lines.Add($"The resource template list changed on {string.Join(", ", staleTemplates)} after your last full template listing; call list_mcp_resource_templates again before relying on it.");
        foreach (var handle in withResources.Where(handle => handle.ResourceNotificationError is not null))
            lines.Add($"Resource change notifications are unavailable on {handle.Config.Name}: {handle.ResourceNotificationError}. The next MCP call will reconnect once; until then do not assume either resource list is current.");
        return string.Join('\n', lines);
    }

    private static JsonElement ToElement<T>(T value) => JsonSerializer.SerializeToElement(value, TypeInfo<T>());

    private static JsonTypeInfo<T> TypeInfo<T>() => (JsonTypeInfo<T>)McpJsonUtilities.DefaultOptions.GetTypeInfo(typeof(T));
}
