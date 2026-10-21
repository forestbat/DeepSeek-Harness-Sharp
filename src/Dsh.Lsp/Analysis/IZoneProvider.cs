using Dsh.Lsp.Client;
using Dsh.Lsp.Model;

namespace Dsh.Lsp.Analysis;

public interface IZoneProvider
{
    IReadOnlyList<LanguageZone> Detect(LspDocument document, string profileId);
}

public sealed class LanguageZoneProvider : IZoneProvider
{
    public IReadOnlyList<LanguageZone> Detect(LspDocument document, string profileId)
        => LanguageZoneDetector.Detect(document, profileId);
}
