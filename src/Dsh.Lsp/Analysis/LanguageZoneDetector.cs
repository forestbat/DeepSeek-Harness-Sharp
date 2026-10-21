using System.Text.RegularExpressions;
using Dsh.Lsp.Catalog;
using Dsh.Lsp.Client;
using Dsh.Lsp.Model;
using Dsh.Lsp.Protocol;

namespace Dsh.Lsp.Analysis;

/** Finds the common embedded regions without requiring an editor or a language-specific parser. */
public static partial class LanguageZoneDetector
{
    public static IReadOnlyList<LanguageZone> Detect(LspDocument document, string profileId)
    {
        return profileId switch
        {
            "markdown" => MarkdownZones(document),
            "html" => HtmlZones(document),
            _ => [],
        };
    }

    private static IReadOnlyList<LanguageZone> MarkdownZones(LspDocument document)
    {
        var zones = new List<LanguageZone>();
        foreach (Match match in MarkdownFence().Matches(document.Text))
        {
            var language = match.Groups["lang"].Value;
            var profile = LanguageCatalog.FindByLanguageId(language);
            if (profile is null || profile.Id == "markdown")
                continue;
            var group = match.Groups["code"];
            var range = new LspRange(document.PositionAt(group.Index), document.PositionAt(group.Index + group.Length));
            zones.Add(new LanguageZone(
                $"markdown-{zones.Count}",
                ZonePath(document, profile.Extensions[0], zones.Count),
                profile.LanguageIds[0],
                group.Value,
                range,
                document.Uri));
        }

        return zones;
    }

    private static IReadOnlyList<LanguageZone> HtmlZones(LspDocument document)
    {
        var zones = new List<LanguageZone>();
        AddHtmlZones(document, HtmlScript(), "javascript", zones);
        AddHtmlZones(document, HtmlStyle(), "css", zones);
        return zones;
    }

    private static void AddHtmlZones(LspDocument document, Regex regex, string languageId, List<LanguageZone> zones)
    {
        var profile = LanguageCatalog.FindByLanguageId(languageId);
        if (profile is null)
            return;
        foreach (Match match in regex.Matches(document.Text))
        {
            var group = match.Groups["code"];
            if (group.Length == 0)
                continue;
            var range = new LspRange(document.PositionAt(group.Index), document.PositionAt(group.Index + group.Length));
            zones.Add(new LanguageZone(
                $"html-{languageId}-{zones.Count}",
                ZonePath(document, profile.Extensions[0], zones.Count),
                profile.LanguageIds[0],
                group.Value,
                range,
                document.Uri));
        }
    }

    [GeneratedRegex(@"```(?<lang>[A-Za-z0-9_+#.-]+)[^\r\n]*\r?\n(?<code>.*?)```", RegexOptions.Singleline)]
    private static partial Regex MarkdownFence();

    [GeneratedRegex(@"<script[^>]*>(?<code>.*?)</script\s*>", RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex HtmlScript();

    [GeneratedRegex(@"<style[^>]*>(?<code>.*?)</style\s*>", RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex HtmlStyle();

    private static string ZonePath(LspDocument document, string extension, int index)
    {
        if (LspDocumentUri.TryGetPath(document.Uri, out var path))
        {
            var directory = Path.GetDirectoryName(path) ?? Path.GetTempPath();
            var stem = Path.GetFileNameWithoutExtension(path);
            return Path.Combine(directory, $"{stem}.zone{index}{extension}");
        }

        return $"{document.Uri}.zone{index}{extension}";
    }
}
