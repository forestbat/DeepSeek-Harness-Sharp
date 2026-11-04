using System.Text.Json;
using Dsh.Core;
using Dsh.Interaction;
using Dsh.Runtime;
using Dsh.Ide.Triggers;
using Dsh.Ide.Bridge;

namespace Dsh.Ide.Surfaces;

public static class IdeCommand
{
    public const string Name = "ide";

    public static IDisposable Register(Context ctx, IEditorService editors, EditorReviewService reviews)
        => ctx.Get<CommandsService>(CommandsService.ServiceName)!.Register(new CommandDefinition
        {
            Name = Name,
            Description = "Open files or workspaces in configured editors and explain editor selection",
            Input = new("open [path] [--editor id] [--pick] | list | status | why | doctor | diff --git|--checkpoint | bridge ENDPOINT [--token-file PATH] | lsp [neovim|vscode|helix]"),
            RecordInput = false,
            Subcommands = Descriptors(editors.Catalog),
            Handler = invocation => ExecuteAsync(ctx, editors, reviews, invocation),
        });

    private static IReadOnlyList<CommandDescriptor> Descriptors(IEditorCatalog catalog)
        => [
            new("open", "Open a file, project or workspace", ArgumentSchemas:
            [
                new("path", "text", "Optional path; quote spaces"),
                new("editor", "select", "Configured editor", "--editor", catalog.Descriptors.Select(descriptor => descriptor.Id.Value).ToArray()),
            ]),
            new("list", "List detected editors and capabilities"),
            new("status", "Inspect workspace preferences, binding and trigger rules"),
            new("why", "Explain the latest editor selection"),
            new("doctor", "Check configuration, installations, display and terminal hosts"),
            new("diff", "Review differences with Git HEAD or a checkpoint"),
            new("bridge", "Generate authenticated extension protocol configuration without reading tokens"),
            new("lsp", "Generate standard stdio LSP client configuration"),
        ];

    public static async Task<CommandResult> ExecuteAsync(Context ctx, IEditorService editors, EditorReviewService reviews, CommandInvocation invocation)
    {
        try
        {
            var input = EditorCommandInput.Parse(invocation.RawInput);
            var workspace = invocation.Agent.Session.WorkingDirectory ?? Environment.CurrentDirectory;
            return input.Action switch
            {
                "list" => new CommandResult.Success(await ListAsync(editors, invocation.Signal).ConfigureAwait(false)),
                "status" => new CommandResult.Success(Status(ctx, editors, workspace, invocation.Agent.Id.Value)),
                "why" => new CommandResult.Success(JsonSerializer.Serialize(editors.LastDecision, EditorJsonContext.Default.EditorDecision)),
                "doctor" => new CommandResult.Success(JsonSerializer.Serialize(await editors.DoctorAsync(workspace, invocation.Signal).ConfigureAwait(false), EditorJsonContext.Default.IReadOnlyListEditorDiagnostic)),
                "diff" => await DiffAsync(ctx, editors, reviews, input, invocation, workspace).ConfigureAwait(false),
                "open" or "reveal" => await OpenAsync(ctx, editors, input, invocation, workspace).ConfigureAwait(false),
                "bridge" => new CommandResult.Success(EditorIntegrationConfiguration.GenerateExtension(input.Path!, input.TokenFile)),
                "lsp" => new CommandResult.Success(EditorIntegrationConfiguration.GenerateLsp(input.Path ?? "neovim")),
                _ => new CommandResult.Error("Usage: /ide open [path] | list | status | why | doctor | diff --git|--checkpoint"),
            };
        }
        catch (Exception error) when (error is ArgumentException or InvalidOperationException or IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception or Dsh.GitInspect.GitCommandException)
        {
            return new CommandResult.Error(error.Message);
        }
    }

    private static async Task<string> ListAsync(IEditorService editors, CancellationToken cancellationToken)
    {
        var installed = await editors.Catalog.DetectAsync(cancellationToken: cancellationToken).ConfigureAwait(false);
        return installed.Count == 0 ? "No configured editor was detected. Use /ide doctor or configure a command template."
            : string.Join('\n', installed.Select(installation => $"{installation.Editor}: {installation.Executable} | {installation.Version ?? "version unknown"} | {editors.Catalog.Find(installation.Editor)?.Capabilities}"));
    }

    internal static string Status(Context ctx, IEditorService editors, string workspace, string? sessionId)
    {
        var profile = JsonSerializer.Serialize(editors.InspectWorkspace(workspace), EditorJsonContext.Default.EditorWorkspace);
        var binding = JsonSerializer.Serialize(editors.GetBinding(workspace, sessionId), EditorJsonContext.Default.EditorBinding);
        var triggers = ctx.Get<EditorTriggerService>(EditorTriggerService.ServiceName, false);
        var rules = JsonSerializer.Serialize(triggers?.Rules ?? [], EditorJsonContext.Default.IReadOnlyListEditorTrigger);
        var outcomes = JsonSerializer.Serialize(triggers?.LastOutcomes ?? [], EditorJsonContext.Default.IReadOnlyListEditorTriggerOutcome);
        return $"workspace: {profile}\nbinding: {binding}\ntriggers: {rules}\nlastTriggers: {outcomes}";
    }

    private static async Task<CommandResult> OpenAsync(Context ctx, IEditorService editors, EditorCommandInput input, CommandInvocation invocation, string workspace)
    {
        var request = input.Request(workspace, invocation.Agent.Id.Value, EditorSessionContext.TurnId(ctx, invocation.Agent.Session)) with { AllowOsDefault = true };
        if (input.Pick)
            request = request with { Editor = await PickAsync(ctx, editors, invocation).ConfigureAwait(false) };
        var paths = input.Changed ? EditorChangeSet.PreviousTurnFiles(invocation.Agent.Session) : [request.Path!];
        if (paths.Count == 0)
            return new CommandResult.Success("No files changed in the previous completed turn.");
        var results = new List<EditorResult>();
        foreach (var path in paths)
        {
            var target = request with { Path = path };
            if (input.Changed)
                target = target with { Action = EditorAction.OpenFile };
            results.Add(await editors.LaunchAsync(target, invocation.Signal).ConfigureAwait(false));
        }
        return Result(results);
    }


    private static async Task<string> PickAsync(Context ctx, IEditorService editors, CommandInvocation invocation)
    {
        var installed = await editors.Catalog.DetectAsync(cancellationToken: invocation.Signal).ConfigureAwait(false);
        if (installed.Count == 0)
            throw new InvalidOperationException("No installed editor is available to pick.");
        var questions = ctx.Get<UserQuestionService>(UserQuestionService.ServiceName, false)
            ?? throw new InvalidOperationException("The editor picker requires an interactive user-question provider; use --editor instead.");
        var options = installed.Select(item => new AskUserQuestionOption(item.Editor.Value, editors.Catalog.Find(item.Editor)?.DisplayName)).ToArray();
        var answer = await questions.Ask(new([new("editor", "Open with", Options: options)], invocation.Agent), invocation.Signal).ConfigureAwait(false);
        var selected = answer.Answers.FirstOrDefault(item => item.Id == "editor")?.Selected.SingleOrDefault();
        return selected is not null && installed.Any(item => item.Editor.Value == selected) ? selected
            : throw new InvalidOperationException("No installed editor was selected.");
    }

    private static async Task<CommandResult> DiffAsync(Context ctx, IEditorService editors, EditorReviewService reviews, EditorCommandInput input, CommandInvocation invocation, string workspace)
    {
        var files = input.Git
            ? await reviews.PrepareGitAsync(workspace, invocation.Signal).ConfigureAwait(false)
            : await reviews.PrepareCheckpointAsync(invocation.Agent.Session, workspace, EditorChangeSet.PreviousTurnStart(invocation.Agent.Session), invocation.Signal).ConfigureAwait(false);
        if (files.Count == 0)
            return new CommandResult.Success("No file differences to open.");
        var request = input.Request(workspace, invocation.Agent.Id.Value, EditorSessionContext.TurnId(ctx, invocation.Agent.Session)) with { Action = EditorAction.OpenDiff };
        if (input.Pick)
            request = request with { Editor = await PickAsync(ctx, editors, invocation).ConfigureAwait(false) };
        var results = new List<EditorResult>();
        foreach (var file in files)
            results.Add(await editors.LaunchAsync(request with { Path = file.Path, Left = file.Left, Right = file.Right }, invocation.Signal).ConfigureAwait(false));
        return Result(results);
    }

    private static CommandResult Result(IReadOnlyList<EditorResult> results)
    {
        var text = string.Join('\n', results.Select(result => JsonSerializer.Serialize(result, EditorJsonContext.Default.EditorResult)));
        return results.All(result => result.Success) ? new CommandResult.Success(text) : new CommandResult.Error(text);
    }
}
