using Cordis;
using Dsh.Core;
using Dsh.Llm;

namespace Dsh.Interaction;

public sealed record AskUserQuestionOption(string Label, string? Description = null);

public enum AskUserQuestionIntentKind
{
    PlanReview,
}

public sealed record AskUserQuestionIntent(AskUserQuestionIntentKind Kind, string Approve);

public sealed record AskUserQuestionItem(
    string Id,
    string Question,
    string? Detail = null,
    string? Header = null,
    IReadOnlyList<AskUserQuestionOption>? Options = null,
    bool MultiSelect = false,
    AskUserQuestionIntent? Intent = null);

public sealed record AskUserQuestionAnswerItem(string Id, IReadOnlyList<string> Selected, string? Custom = null);

public sealed record AskUserQuestionAnswer(IReadOnlyList<AskUserQuestionAnswerItem> Answers);

public sealed record AskUserQuestionRequest(IReadOnlyList<AskUserQuestionItem> Questions, IAgent? Agent = null);

public sealed class UserQuestionException : HarnessException
{
    public const string AskAborted = "ASK_ABORTED";
    public const string EmptyQuestions = "EMPTY_QUESTIONS";
    public const string BadIntent = "BAD_INTENT";
    public const string NoProvider = "NO_PROVIDER";
    public const string CallerNotLive = "CALLER_NOT_LIVE";
    public const string DelegatedCaller = "DELEGATED_CALLER";

    public UserQuestionException(string message, string code, Exception? innerException = null)
        : base(message, code, innerException)
    {
    }
}

public sealed class UserQuestionService : Service
{
    public const string ServiceName = "userQuestions";
    public const string RequestEvent = "user-questions/request";

    public UserQuestionService(Context ctx) : base(ctx, ServiceName)
    {
    }

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
            var result = await Ctx.Events.Waterfall(
                carrier, RequestEvent, [request],
                static () => throw new UserQuestionException(
                    "no user-questions answerer accepted the request", UserQuestionException.NoProvider));
            return result as AskUserQuestionAnswer
                ?? throw new UserQuestionException(
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
    // Headless stance: each question resolves to its first option when it declares options, otherwise an empty selection.
    public static IDisposable Headless(Context ctx)
    {
        var remove = ctx.On(
            UserQuestionService.RequestEvent,
            (_, args) =>
            {
                var request = (AskUserQuestionRequest)args[0]!;
                var answers = request.Questions
                    .Select(question => new AskUserQuestionAnswerItem(
                        question.Id,
                        question.Options is { Count: > 0 } options ? [options[0].Label] : []))
                    .ToList();
                return new ValueTask<object?>(new AskUserQuestionAnswer(answers));
            },
            new EventOptions { Global = true });
        return new DisposeAction(() => remove());
    }
}
