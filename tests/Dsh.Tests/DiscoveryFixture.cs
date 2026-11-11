using Dsh.Runtime;
using Dsh.Boot;
using Dsh.Core;
using Dsh.Interaction;
using Dsh.Llm;
using Dsh.Skills;
using Dsh.Agent;

namespace Dsh.Tests;

internal sealed class DiscoveryFixture : IAsyncDisposable
{
    private readonly ToolRuntime _tools;
    private readonly AgentLoopAgent _agent;
    private readonly bool _ownsRoot;
    private readonly IDisposable? _runtimeRegistration;
    private readonly IDisposable _filesystemRegistration;
    private readonly IDisposable? _providerRegistration;
    private readonly FileSystemSkillProvider _filesystem;
    private readonly SkillCatalog _catalog;
    private readonly EffectHandle? _filesystemService;

    public DiscoveryFixture(bool allowTool = false)
        : this(TempTree.CreateDirectory("skill-discovery-policy"), allowTool: allowTool)
    {
        _ownsRoot = true;
        var custom = Directory.CreateDirectory(Path.Combine(Root, "custom")).FullName;
        var skill = Directory.CreateDirectory(Path.Combine(custom, "private-file-skill")).FullName;
        File.WriteAllText(Path.Combine(skill, "SKILL.md"), "---\nname: private-file-skill\ndescription: private discovery description\n---\nprivate-discovery-body\n");
        var providerPath = Path.Combine(Root, "provider-content.txt");
        File.WriteAllText(providerPath, "private-discovery-body");
        FileSystem = new ObservedSkillFileSystem();
        _filesystemService = Context.Provide("fs", FileSystem);
        Provider = new ObservedSkillProvider(providerPath);
        _providerRegistration = Registry.RegisterProvider(_ => Provider);
        _runtimeRegistration = Registry.Register(new SkillRegistration
        {
            Name = "runtime-selected", Description = "Selected public runtime skill", Source = SkillSources.Runtime,
            Content = "public-runtime-selected-body",
        });
    }

    public DiscoveryFixture(string root, IReadOnlyList<string>? customDirs = null,
        Func<HarnessSettings>? readPolicy = null, bool allowTool = false)
    {
        Root = root;
        Context = new Context();
        _ = new SystemPrompt(Context, new SystemPromptConfig());
        _ = new LlmRuntime(Context);
        _tools = new ToolRuntime(Context);
        _ = ApprovalService.Register(Context);
        if (readPolicy is null)
        {
            var home = HarnessHome.Resolve(Path.Combine(Root, "home"));
            Directory.CreateDirectory(home.Root);
            File.WriteAllText(home.SettingsFile, "safety:\n  autoApprove: false\n");
            var settings = HarnessSettings.Load(home);
            if (allowTool) (settings.Safety ??= new SafetySettings()).Whitelist["discovery-invocation"] = new() { Tool = [SkillCatalog.ToolName] };
            settings.Save(home);
            readPolicy = HarnessSettings.CreatePolicyReader(home, settings);
        }
        _ = new SafetyPolicySource(Context, readPolicy);
        Registry = new SkillRegistry(Context, new SkillRegistryConfig());
        FileSystemSkillProvider? filesystem = null;
        SkillProviderControl? providerControl = null;
        _filesystemRegistration = Registry.RegisterProvider(control =>
        {
            providerControl = control;
            return filesystem = new FileSystemSkillProvider(Context, control, new SkillFilesystemConfig
            {
                ProviderName = "observed-filesystem", IncludeDefaultRoots = false,
                CustomSkillDirs = customDirs ?? [Path.Combine(Root, "custom")], Watch = false,
            });
        });
        _filesystem = filesystem!;
        ProviderControl = providerControl!;
        _catalog = new SkillCatalog(Context, Registry);
        var id = SessionId.Create(Guid.NewGuid().ToString("N"));
        Session = Session.Create(id, null, new SessionHeader
        {
            Version = SessionHeader.SessionFormatVersion, Id = id, Cwd = Root, CreatedAt = 0, IsSeeded = false,
        });
        Session.Append(new TurnStartPayload(1));
        _agent = new AgentLoopAgent(Context, id, new AgentOptions(), Session, _ => 0);
    }

    public string Root { get; }
    public Context Context { get; }
    public Session Session { get; }
    public SkillRegistry Registry { get; }
    public SkillProviderControl ProviderControl { get; }
    public ObservedSkillFileSystem FileSystem { get; } = null!;
    public ObservedSkillProvider Provider { get; } = null!;
    public ToolCallId LastCallId { get; private set; }

    public Task<ToolExecutionResult> Execute(CancellationToken signal, string name = "runtime-selected") => _tools.Execute(new ToolExecutionInput
    {
        CallId = LastCallId = ToolCallId.Create(Guid.NewGuid().ToString("N")), Name = SkillCatalog.ToolName,
        Arguments = System.Text.Json.JsonSerializer.SerializeToElement(new { name }), Agent = _agent, Signal = signal,
    });

    public void ResetObservations()
    {
        FileSystem.ResetObservations();
        Provider.Reads = 0;
    }

    public async ValueTask DisposeAsync()
    {
        if (_filesystemService is not null)
        {
            FileSystem.ResumeRead.TrySetResult(true);
            if (FileSystem.ReadEntered.Task.IsCompleted)
                await FileSystem.ReadFinished.Task.WaitAsync(TestContext.Current.CancellationToken);
        }
        _catalog.Dispose();
        _runtimeRegistration?.Dispose();
        _providerRegistration?.Dispose();
        _filesystemRegistration.Dispose();
        await _filesystem.Dispose();
        _filesystemService?.Dispose();
        _agent.Dispose();
        if (_ownsRoot) TempTree.Delete(Root);
    }
}
