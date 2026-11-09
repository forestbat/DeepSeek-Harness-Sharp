using Dsh.Skills;

namespace Dsh.Tests;

internal sealed class ObservedSkillProvider(string path) : ISkillProvider
{
    public string Name => "unselected-storage-provider";
    public int Reads { get; set; }

    public async Task<SkillProviderObservation> List(SkillLookupOptions options)
    {
        var description = await File.ReadAllTextAsync(path, options.Signal);
        Reads++;
        return SkillProviderObservation.Full([new SkillCandidate
        {
            Name = "unselected-provider-skill", Description = description, Provider = Name, Source = SkillSources.Custom,
            Invocation = new SkillInvocationPolicy(true, true), Path = path,
        }]);
    }

    public async Task<SkillDefinition?> Get(SkillCandidate candidate, SkillLookupOptions options)
    {
        var content = await File.ReadAllTextAsync(path, options.Signal);
        Reads++;
        return new SkillDefinition
        {
            Name = candidate.Name, Description = candidate.Description, Provider = Name, Source = candidate.Source,
            Invocation = candidate.Invocation, Path = path, Content = content,
        };
    }
}
