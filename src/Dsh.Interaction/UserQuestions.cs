using Dsh.Runtime;
using Dsh.Runtime.Events;
using Dsh.Core;
using Dsh.Llm;

namespace Dsh.Interaction;

public sealed record AskUserQuestionOption(string Label, string? Description = null, string? Value = null, bool Disabled = false, bool Default = false);

public enum AskUserQuestionIntentKind
{
    PlanReview
}

public sealed record AskUserQuestionIntent(AskUserQuestionIntentKind Kind, string Approve);

public sealed record AskUserQuestionItem(
    string Id,
    string Question,
    string? Detail = null,
    string? Header = null,
    IReadOnlyList<AskUserQuestionOption>? Options = null,
    bool MultiSelect = false,
    AskUserQuestionIntent? Intent = null,
    bool AllowCustom = true);

public sealed record AskUserQuestionAnswerItem(string Id, IReadOnlyList<string> Selected, string? Custom = null);

public sealed record AskUserQuestionAnswer(IReadOnlyList<AskUserQuestionAnswerItem> Answers, string Status = "answered", string? Reason = null)
{
    public static AskUserQuestionAnswer Dismissed => new([], "dismissed", "user_cancelled");
    public bool IsDismissed => Status == "dismissed";
}

public sealed record AskUserQuestionRequest(IReadOnlyList<AskUserQuestionItem> Questions, IAgent? Agent = null);

public sealed class UserQuestionException(string message, string code, Exception? innerException = null)
    : HarnessException(message, code, innerException)
{
    public const string AskAborted = "ASK_ABORTED";
    public const string EmptyQuestions = "EMPTY_QUESTIONS";
    public const string BadIntent = "BAD_INTENT";
    public const string NoProvider = "NO_PROVIDER";
    public const string CallerNotLive = "CALLER_NOT_LIVE";
    public const string DelegatedCaller = "DELEGATED_CALLER";
}

public sealed class UserQuestionService(Context ctx) : Service(ctx, ServiceName)
{
    public const string ServiceName = "userQuestions";

    public static UserQuestionService Register(Context ctx) => new(ctx);

    public async Task<AskUserQuestionAnswer> Ask(AskUserQuestionRequest request, CancellationToken signal = default)
    {
        if (signal.IsCancellationRequested)
            throw Aborted();
        if (request.Questions.Count == 0)
            throw new UserQuestionException("ask_user_question requires at least one question", UserQuestionException.EmptyQuestions);
        ValidateAgent(request.Agent);
        foreach (var question in request.Questions)
            ValidateIntent(question);
        try
        {
            var carrier = request.Agent is { } agent ? DshScope.ScopeTarget(Ctx, agent.ScopeKey) : Ctx;
            var pending = Ctx.Events.Waterfall(
                carrier, new UserQuestionsRequestNotification(request, signal),
                static () => throw new UserQuestionException(
                    "no user-questions answerer accepted the request", UserQuestionException.NoProvider));
            var result = pending.IsCompletedSuccessfully || !signal.CanBeCanceled
                ? await pending
                : await pending.AsTask().WaitAsync(signal);
            signal.ThrowIfCancellationRequested();
            return result is AskUserQuestionAnswer answer
                ? answer.IsDismissed ? AskUserQuestionAnswer.Dismissed : answer
                : throw new UserQuestionException(
                    "no user-questions answerer accepted the request", UserQuestionException.NoProvider);
        }
        catch (Exception error) when (error is not UserQuestionException)
        {
            if (signal.IsCancellationRequested)
                throw Aborted(error);
            throw;
        }
    }

    private void ValidateAgent(IAgent? agent)
    {
        if (agent is null)
            return;
        var agents = Ctx.Get<AgentRegistry>(AgentRegistry.ServiceName, false);
        if (agents is null || !ReferenceEquals(agents.Get(agent.Id), agent))
        {
            throw new UserQuestionException(
                "human interaction requires the exact live calling agent when an agent is supplied",
                UserQuestionException.CallerNotLive);
        }
        if (!agents.Roots().Contains(agent))
        {
            throw new UserQuestionException(
                "human interaction is unavailable while the calling agent is owned by another live agent; "
                + "include the unresolved question or decision in the child agent's final result",
                UserQuestionException.DelegatedCaller);
        }
    }

    private static void ValidateIntent(AskUserQuestionItem question)
    {
        if (question.Intent is not { } intent)
            return;
        if (question.Options?.Any(option => option.Label == intent.Approve) != true)
        {
            throw new UserQuestionException(
                $"question {question.Id} declares intent {intent.Kind} whose approve label "
                + $"\"{intent.Approve}\" names none of its options",
                UserQuestionException.BadIntent);
        }
        if (question.Detail is null)
        {
            throw new UserQuestionException(
                $"question {question.Id} declares intent {intent.Kind} without the detail it reviews",
                UserQuestionException.BadIntent);
        }
    }

    private static UserQuestionException Aborted(Exception? cause = null)
        => new("ask_user_question was aborted before the user answered", UserQuestionException.AskAborted, cause);
}

public static class UserQuestionAnswerers
{
    // Headless answers honor enabled defaults, then choose the first enabled option when needed.
    public static IDisposable Headless(Context ctx)
    {
        var remove = ctx.OnWaterfall<UserQuestionsRequestNotification>(
            (notification, _) =>
            {
                var request = notification.Request;
                var answers = request.Questions.Select(question =>
                {
                    var state = new ChoicePromptState(ChoicePrompt.FromQuestion(question));
                    if (!state.CanSubmit && state.FocusedIndex >= 0)
                        state.Select(state.FocusedIndex);
                    return state.BuildAnswer(question.Id);
                }).ToList();
                return ValueTask.FromResult<object?>(new AskUserQuestionAnswer(answers));
            },
            new EventOptions { Global = true });
        return new DisposeAction(() => remove());
    }
}
