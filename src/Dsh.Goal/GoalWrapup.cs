using System.Text.Encodings.Web;
using System.Text.Json;
using Dsh.Llm;

namespace Dsh.Goal;

public static class GoalWrapup
{
    private const string Grounding =
        "Report only what earlier rounds and tool results in this session actually establish; "
        + "when a detail is not in the session, say so instead of inventing it. ";

    private const string CompleteText =
        "The goal is marked complete and this autonomous run is ending. Write the closing "
        + "message to the user now: state the outcome, summarize what was done and how it was "
        + "verified, and point to the concrete results (files, commits, or other artifacts). ";

    private const string CompleteTail =
        "Note anything the user should review or do next. Address the user directly. Do not "
        + "call any more tools in this run; further work waits for the user's next instruction.\n"
        + "</goal_complete>";

    private const string BlockedText =
        "The goal is marked blocked and this autonomous run is ending. Write the closing "
        + "message to the user now: state what has been completed so far, describe the concrete "
        + "blocking condition and what you tried, and say exactly what you need from the user to "
        + "continue. ";

    private const string BlockedTail =
        "Address the user directly. Do not call any more tools in this run; further work "
        + "waits for the user's next instruction.\n"
        + "</goal_blocked>";

    public static IReadOnlyList<ContentBlock> RenderContext(string objective, string? blockedReason = null)
    {
        var heading = $"Objective: {JsonString(objective)}\n";
        var text = blockedReason is null
            ? $"<goal_complete>\n{heading}{CompleteText}{Grounding}{CompleteTail}"
            : $"<goal_blocked>\n{heading}Blocked: {JsonString(blockedReason)}\n{BlockedText}{Grounding}{BlockedTail}";
        return [new TextBlock(text)];
    }

    private static string JsonString(string value)
        => JsonSerializer.Serialize(value, new JsonSerializerOptions { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping });
}
