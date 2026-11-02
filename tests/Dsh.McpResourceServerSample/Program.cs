using System.Text.Json.Nodes;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace Dsh.McpResourceServerSample;

/**
 * Test-only stdio MCP server with fixed resources, one URI template and a tool that adds resources at runtime.
 * Started with --no-resources it serves a single tool and declares no resources capability.
 */
public static class Program
{
    public const string NoResourcesFlag = "--no-resources";
    public const string GuideUri = "docs://guide";
    public const string GuideText = "# Guide\nResource text served by the sample MCP server.";
    public const string LargeUri = "docs://large";
    public const string LargeHead = "LARGE-HEAD";
    public const string LargeTail = "LARGE-TAIL";
    public const int LargeFillerLength = 200_000;
    public const string LogoUri = "files://logo.png";
    public const string LogoPngBase64 = "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mNk+M9QDwADhgGAWjR9awAAAABJRU5ErkJggg==";
    public const string DataUri = "files://data.bin";
    public const int DataLength = 256;
    public const string NoteTemplate = "notes://{name}";
    private const string ShutdownNotification = "notifications/test/shutdown";

    public static async Task Main(string[] args)
    {
        using var lifetime = new CancellationTokenSource();
        var startupMarker = Array.IndexOf(args, "--startup-marker");
        if (startupMarker >= 0)
            File.WriteAllText(args[startupMarker + 1], "started");
        var options = new McpServerOptions
        {
            ServerInfo = new Implementation { Name = "resource-sample", Version = "1.0" },
            ProtocolVersion = args.Contains("--legacy") ? "2025-11-25" : "2026-07-28",
            Handlers = new McpServerHandlers
            {
                NotificationHandlers =
                [
                    new(ShutdownNotification, (_, _) =>
                    {
                        lifetime.Cancel();
                        return ValueTask.CompletedTask;
                    }),
                ],
            },
        };
        if (args.Contains(NoResourcesFlag))
        {
            options.ToolCollection = [McpServerTool.Create(() => "pong", new McpServerToolCreateOptions { Name = "ping" })];
        }
        else
        {
            var resources = Resources();
            options.ResourceCollection = resources;
            options.ToolCollection = [AddResourceTool(resources)];
            var changeDuringListing = 0;
            if (args.Contains("--listing-race"))
                options.ToolCollection.Add(McpServerTool.Create((bool fail) =>
                {
                    changeDuringListing = fail ? 2 : 1;
                    return "next listing will notify before returning";
                }, new McpServerToolCreateOptions { Name = "change_during_listing" }));
            options.Filters.Request.ListResourcesFilters.Add(next => async (request, signal) =>
            {
                var cursor = request.Params?.Cursor;
                // SDK 的静态集合只在首页填充; 分页过滤器先取完整集合, 再按自己的游标切页。
                if (request.Params is { } parameters)
                    parameters.Cursor = null;
                var result = await next(request, signal);
                if (request.Params is { } original)
                    original.Cursor = cursor;
                var change = Interlocked.Exchange(ref changeDuringListing, 0);
                if (change != 0)
                {
                    await request.Server.SendNotificationAsync(NotificationMethods.ResourceListChangedNotification, cancellationToken: signal);
                    if (change == 2)
                        throw new McpProtocolException("sample listing failed after notifying", McpErrorCode.InternalError);
                }
                var offset = cursor is not null ? int.Parse(cursor, System.Globalization.CultureInfo.InvariantCulture) : 0;
                return new ListResourcesResult
                {
                    Resources = [.. result.Resources.Skip(offset).Take(2)],
                    NextCursor = offset + 2 < result.Resources.Count ? (offset + 2).ToString(System.Globalization.CultureInfo.InvariantCulture) : null,
                };
            });
            options.Filters.Request.ListResourceTemplatesFilters.Add(next => async (request, signal) =>
            {
                var cursor = request.Params?.Cursor;
                if (request.Params is { } parameters)
                    parameters.Cursor = null;
                var result = await next(request, signal);
                if (request.Params is { } original)
                    original.Cursor = cursor;
                var offset = cursor is not null ? int.Parse(cursor, System.Globalization.CultureInfo.InvariantCulture) : 0;
                return new ListResourceTemplatesResult
                {
                    ResourceTemplates = [.. result.ResourceTemplates.Skip(offset).Take(1)],
                    NextCursor = offset + 1 < result.ResourceTemplates.Count ? (offset + 1).ToString(System.Globalization.CultureInfo.InvariantCulture) : null,
                };
            });
            var endMode = Array.FindIndex(args, value => value is "--listen-fails" or "--listen-ends" or "--listen-denied");
            if (endMode >= 0 && !File.Exists(args[endMode + 1]))
            {
                var ended = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                options.ToolCollection.Add(McpServerTool.Create(() =>
                {
                    File.WriteAllText(args[endMode + 1], "ended");
                    ended.TrySetResult();
                    return "notification stream ended; tools remain connected";
                }, new McpServerToolCreateOptions { Name = "end_listen" }));
                options.Handlers.SubscriptionsListenHandler = async (request, signal) =>
                {
                    await request.Server.SendMessageAsync(new JsonRpcNotification
                    {
                        Method = NotificationMethods.SubscriptionsAcknowledgedNotification,
                        Params = new JsonObject
                        {
                            ["notifications"] = new JsonObject { ["resourcesListChanged"] = args[endMode] != "--listen-denied" },
                            ["_meta"] = new JsonObject { [MetaKeys.SubscriptionId] = request.JsonRpcRequest.Id.ToString() },
                        },
                    }, signal);
                    await ended.Task.WaitAsync(signal);
                    if (args[endMode] == "--listen-fails")
                        throw new McpProtocolException("sample notification stream failed", McpErrorCode.InternalError);
                    return new EmptyResult();
                };
            }
        }
        if (args.Contains("--file-tools"))
        {
            options.ToolCollection =
            [
                McpServerTool.Create((string path, string content) =>
                {
                    File.WriteAllText(path, content);
                    return File.ReadAllText(path);
                }, new McpServerToolCreateOptions { Name = "write_file" }),
                McpServerTool.Create((string path) =>
                {
                    File.AppendAllText(path, "once\n");
                    Environment.Exit(23);
                    return "unreachable";
                }, new McpServerToolCreateOptions { Name = "mutate_then_exit" }),
                McpServerTool.Create(async (string path, CancellationToken cancellationToken) =>
                {
                    await File.WriteAllTextAsync(path, "ready", cancellationToken);
                    await Task.Delay(Timeout.Infinite, cancellationToken);
                    return "unreachable";
                }, new McpServerToolCreateOptions { Name = "wait_file" }),
            ];
            options.ResourceCollection = null;
        }
        await using var server = McpServer.Create(new StdioServerTransport("resource-sample"), options);
        var running = server.RunAsync(lifetime.Token);
        await Console.Error.WriteLineAsync("MCP_RESOURCE_SAMPLE_READY");
        try
        {
            await running;
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested)
        {
            // Fixture teardown requests an orderly exit before disposing the client transport.
        }
    }

    private static McpServerResourceCollection Resources()
        =>
        [
            McpServerResource.Create(
                () => new TextResourceContents { Uri = GuideUri, MimeType = "text/markdown", Text = GuideText },
                new McpServerResourceCreateOptions { UriTemplate = GuideUri, Name = "guide", MimeType = "text/markdown" }),
            McpServerResource.Create(
                () => new TextResourceContents { Uri = LargeUri, MimeType = "text/plain", Text = $"{LargeHead}{new string('x', LargeFillerLength)}{LargeTail}" },
                new McpServerResourceCreateOptions { UriTemplate = LargeUri, Name = "large", MimeType = "text/plain" }),
            McpServerResource.Create(
                () => BlobResourceContents.FromBytes(Convert.FromBase64String(LogoPngBase64), LogoUri, "image/png"),
                new McpServerResourceCreateOptions { UriTemplate = LogoUri, Name = "logo", MimeType = "image/png" }),
            McpServerResource.Create(
                () => BlobResourceContents.FromBytes(Enumerable.Range(0, DataLength).Select(value => (byte)value).ToArray(), DataUri, "application/octet-stream"),
                new McpServerResourceCreateOptions { UriTemplate = DataUri, Name = "data", MimeType = "application/octet-stream" }),
            McpServerResource.Create(
                (string name) => $"note:{name}",
                new McpServerResourceCreateOptions { UriTemplate = NoteTemplate, Name = "note", MimeType = "text/plain" }),
            McpServerResource.Create(
                (string name) => $"archive:{name}",
                new McpServerResourceCreateOptions { UriTemplate = "archive://{name}", Name = "archive", MimeType = "text/plain" }),
        ];

    private static McpServerTool AddResourceTool(McpServerResourceCollection resources)
        => McpServerTool.Create(
            (string uri, string text) =>
            {
                resources.Add(McpServerResource.Create(
                    () => new TextResourceContents { Uri = uri, MimeType = "text/plain", Text = text },
                    new McpServerResourceCreateOptions { UriTemplate = uri, Name = uri, MimeType = "text/plain" }));
                return "added";
            },
            new McpServerToolCreateOptions { Name = "add_resource", Description = "Adds a text resource." });
}
