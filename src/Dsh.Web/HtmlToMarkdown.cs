using System.Net;
using System.Text;

namespace Dsh.Web;

internal static class HtmlToMarkdown
{
    private sealed record Attribute(string Name, string Value);

    private sealed class Element
    {
        public string? Tag { get; }
        public List<Attribute> Attributes { get; }
        public List<object> Children { get; } = [];

        public Element(string? tag, List<Attribute> attributes)
        {
            Tag = tag;
            Attributes = attributes;
        }
    }

    private sealed record TextNode(string Text);

    private static readonly IReadOnlySet<string> VoidElements = new HashSet<string>(StringComparer.Ordinal)
    {
        "area", "base", "br", "col", "embed", "hr", "img", "input",
        "link", "meta", "param", "source", "track", "wbr",
    };

    private static readonly IReadOnlySet<string> RawTextElements = new HashSet<string>(StringComparer.Ordinal)
    {
        "script", "style", "noscript",
    };

    private static readonly IReadOnlySet<string> RemovedElements = new HashSet<string>(StringComparer.Ordinal)
    {
        "script", "style", "noscript", "template", "iframe", "object", "embed",
    };

    private static readonly IReadOnlySet<string> BlockTags = new HashSet<string>(StringComparer.Ordinal)
    {
        "address", "article", "aside", "blockquote", "div", "dl", "fieldset", "footer", "form",
        "h1", "h2", "h3", "h4", "h5", "h6", "header", "hr", "li", "main", "nav", "ol", "p", "pre",
        "section", "table", "ul",
    };

    public static string Convert(string html)
    {
        var root = Parse(html);
        return RenderChildren(root, block: true).Trim();
    }

    private static Element Parse(string html)
    {
        var root = new Element(null, []);
        var stack = new Stack<Element>();
        stack.Push(root);
        var offset = 0;

        while (offset < html.Length)
        {
            var start = html.IndexOf('<', offset);
            if (start < 0)
            {
                AppendText(stack.Peek(), html[offset..]);
                break;
            }
            if (start > offset)
                AppendText(stack.Peek(), html[offset..start]);

            if (html.AsSpan(start).StartsWith("<!--".AsSpan(), StringComparison.Ordinal))
            {
                var end = html.IndexOf("-->", start + 4, StringComparison.Ordinal);
                offset = end < 0 ? html.Length : end + 3;
                continue;
            }

            var tagEnd = FindTagEnd(html, start);
            if (tagEnd < 0)
            {
                AppendText(stack.Peek(), html[start..]);
                break;
            }

            var tagText = html[(start + 1)..tagEnd].Trim();
            if (tagText.Length == 0)
            {
                offset = tagEnd + 1;
                continue;
            }

            var closing = tagText[0] == '/';
            var nameStart = closing ? 1 : 0;
            var nameEnd = nameStart;
            while (nameEnd < tagText.Length && (IsAsciiLetterOrDigit(tagText[nameEnd]) || tagText[nameEnd] == '-'))
                nameEnd += 1;
            var rawName = tagText[nameStart..nameEnd].ToLowerInvariant();
            if (rawName.Length == 0)
            {
                offset = tagEnd + 1;
                continue;
            }

            if (closing)
            {
                if (stack.Count > 1 && stack.Peek().Tag == rawName)
                    stack.Pop();
                offset = tagEnd + 1;
                continue;
            }

            var attributes = ParseAttributes(tagText[nameEnd..]);
            var selfClosing = tagText.EndsWith('/');
            if (VoidElements.Contains(rawName) || selfClosing)
            {
                stack.Peek().Children.Add(new Element(rawName, attributes));
                offset = tagEnd + 1;
                continue;
            }

            if (RawTextElements.Contains(rawName))
            {
                var rawEnd = FindRawTextEnd(html, rawName, tagEnd + 1);
                offset = rawEnd < 0 ? html.Length : rawEnd + rawName.Length + 3;
                continue;
            }

            var element = new Element(rawName, attributes);
            stack.Peek().Children.Add(element);
            stack.Push(element);
            offset = tagEnd + 1;
        }

        return root;
    }

    private static int FindTagEnd(string html, int start)
    {
        char? quote = null;
        for (var index = start + 1; index < html.Length; index++)
        {
            var character = html[index];
            if (quote is not null)
            {
                if (character == quote)
                    quote = null;
            }
            else if (character is '"' or '\'')
            {
                quote = character;
            }
            else if (character == '>')
            {
                return index;
            }
        }
        return -1;
    }

    private static List<Attribute> ParseAttributes(string text)
    {
        var attributes = new List<Attribute>();
        var index = 0;
        while (index < text.Length)
        {
            while (index < text.Length && char.IsWhiteSpace(text[index]))
                index += 1;
            if (index >= text.Length)
                break;
            var nameStart = index;
            while (index < text.Length && text[index] != '=' && !char.IsWhiteSpace(text[index]))
                index += 1;
            var name = text[nameStart..index].ToLowerInvariant();
            while (index < text.Length && char.IsWhiteSpace(text[index]))
                index += 1;
            string? value = null;
            if (index < text.Length && text[index] == '=')
            {
                index += 1;
                while (index < text.Length && char.IsWhiteSpace(text[index]))
                    index += 1;
                if (index < text.Length && text[index] is '"' or '\'')
                {
                    var quote = text[index];
                    index += 1;
                    var valueStart = index;
                    while (index < text.Length && text[index] != quote)
                        index += 1;
                    value = text[valueStart..index];
                    if (index < text.Length)
                        index += 1;
                }
                else
                {
                    var valueStart = index;
                    while (index < text.Length && !char.IsWhiteSpace(text[index]))
                        index += 1;
                    value = text[valueStart..index];
                }
            }
            if (name.Length > 0)
                attributes.Add(new Attribute(name, value ?? ""));
        }
        return attributes;
    }

    private static int FindRawTextEnd(string html, string name, int from)
    {
        var prefix = $"</{name}";
        var candidate = html.IndexOf(prefix, from, StringComparison.OrdinalIgnoreCase);
        while (candidate != -1 && candidate + prefix.Length < html.Length && !IsTagBoundary(html[candidate + prefix.Length]))
            candidate = html.IndexOf(prefix, candidate + prefix.Length, StringComparison.OrdinalIgnoreCase);
        return candidate;
    }

    private static bool IsTagBoundary(char character)
        => character is '>' or '/' || char.IsWhiteSpace(character);

    private static void AppendText(Element parent, string text)
    {
        if (text.Length > 0)
            parent.Children.Add(new TextNode(text));
    }

    private static string RenderChildren(Element parent, bool block)
    {
        var blocks = new List<string>();
        var inline = new StringBuilder();
        foreach (var child in parent.Children)
        {
            switch (child)
            {
                case Element element when ShouldRemove(element):
                    break;
                case Element element when element.Tag is not null && BlockTags.Contains(element.Tag):
                    if (inline.Length > 0)
                    {
                        blocks.Add(inline.ToString().Trim());
                        inline.Clear();
                    }
                    var rendered = RenderBlock(element);
                    if (rendered.Length > 0)
                        blocks.Add(rendered);
                    break;
                case Element element:
                    inline.Append(RenderInline(element));
                    break;
                case TextNode text:
                    inline.Append(WebUtility.HtmlDecode(text.Text));
                    break;
            }
        }
        if (inline.Length > 0)
            blocks.Add(inline.ToString().Trim());
        return string.Join("\n\n", blocks);
    }

    private static string RenderBlock(Element element)
    {
        var tag = element.Tag!;
        if (tag.Length == 2 && tag[0] == 'h' && tag[1] is >= '1' and <= '6')
        {
            var level = tag[1] - '0';
            return $"{new string('#', level)} {RenderInlineChildren(element)}";
        }
        return tag switch
        {
            "p" => RenderInlineChildren(element),
            "ul" or "ol" => RenderList(element),
            "blockquote" => RenderBlockquote(element),
            "pre" => RenderPre(element),
            "table" => RenderTable(element),
            "hr" => "---",
            "li" => RenderListItem(element),
            _ => RenderChildren(element, block: true).Trim(),
        };
    }

    private static string RenderInline(Element element)
    {
        var tag = element.Tag!;
        return tag switch
        {
            "strong" or "b" => $"**{RenderInlineChildren(element)}**",
            "em" or "i" => $"_{RenderInlineChildren(element)}_",
            "a" => $"[{RenderInlineChildren(element)}]({AttributeValue(element, "href")})",
            "code" => $"`{RenderInlineChildren(element)}`",
            "br" => "\n",
            "img" => $"![{AttributeValue(element, "alt")}]({AttributeValue(element, "src")})",
            _ => RenderInlineChildren(element),
        };
    }

    private static string RenderInlineChildren(Element element)
    {
        var builder = new StringBuilder();
        foreach (var child in element.Children)
        {
            switch (child)
            {
                case Element childElement when ShouldRemove(childElement):
                    break;
                case Element childElement when childElement.Tag is not null && BlockTags.Contains(childElement.Tag):
                    builder.Append(RenderBlock(childElement));
                    break;
                case Element childElement:
                    builder.Append(RenderInline(childElement));
                    break;
                case TextNode text:
                    builder.Append(WebUtility.HtmlDecode(text.Text));
                    break;
            }
        }
        return builder.ToString().Trim();
    }

    private static string RenderList(Element element)
    {
        var items = element.Children.OfType<Element>()
            .Where(child => child.Tag == "li" && !ShouldRemove(child))
            .ToList();
        return string.Join("\n", items.Select(item => $"-   {RenderListItem(item)}"));
    }

    private static string RenderListItem(Element element)
    {
        var inline = RenderInlineChildren(element);
        return inline.Length > 0 ? inline : RenderChildren(element, block: true).Trim();
    }

    private static string RenderBlockquote(Element element)
    {
        var inner = RenderChildren(element, block: true).Trim();
        return inner.Length == 0
            ? ""
            : string.Join("\n", inner.Split('\n').Select(line => $"> {line}"));
    }

    private static string RenderPre(Element element)
    {
        var text = string.Concat(element.Children.OfType<TextNode>().Select(node => node.Text)).Trim();
        return $"```\n{text}\n```";
    }

    private static string RenderTable(Element element)
    {
        var rows = CollectRows(element);
        if (rows.Count == 0)
            return "";
        var builder = new StringBuilder();
        for (var rowIndex = 0; rowIndex < rows.Count; rowIndex++)
        {
            var row = rows[rowIndex];
            var cells = row.Children.OfType<Element>()
                .Where(child => child.Tag is "th" or "td" && !ShouldRemove(child))
                .ToList();
            if (cells.Count == 0)
                continue;
            var line = string.Concat(cells.Select((cell, index) => RenderTableCell(RenderInlineChildren(cell), index)));
            builder.Append(line);
            if (rowIndex == 0 && cells.All(cell => cell.Tag == "th"))
            {
                var border = string.Concat(cells.Select((cell, index) => RenderTableCell(TableBorder(cell), index)));
                builder.Append('\n').Append(border);
            }
            if (rowIndex < rows.Count - 1)
                builder.Append('\n');
        }
        return builder.ToString();
    }

    private static List<Element> CollectRows(Element element)
    {
        var rows = new List<Element>();
        foreach (var child in element.Children)
        {
            if (child is not Element childElement || ShouldRemove(childElement))
                continue;
            if (childElement.Tag == "tr")
                rows.Add(childElement);
            else if (childElement.Tag is "thead" or "tbody" or "tfoot")
                rows.AddRange(CollectRows(childElement));
        }
        return rows;
    }

    private static string RenderTableCell(string content, int index)
    {
        var prefix = index == 0 ? "| " : " ";
        var escaped = content.Trim()
            .Replace("\n\r", "<br>")
            .Replace("\n", "<br>")
            .Replace("|", "\\|")
            .PadRight(3);
        return $"{prefix}{escaped} |";
    }

    private static string TableBorder(Element cell)
    {
        var align = (AttributeValue(cell, "align") ?? StyleTextAlign(cell) ?? "").ToLowerInvariant();
        return align switch
        {
            "left" => ":---",
            "right" => "---:",
            "center" => ":---:",
            _ => "---",
        };
    }

    private static string? StyleTextAlign(Element element)
    {
        var style = AttributeValue(element, "style");
        if (style is null)
            return null;
        foreach (var declaration in style.Split(';'))
        {
            var separator = declaration.IndexOf(':');
            if (separator < 0)
                continue;
            var property = declaration[..separator].Trim().ToLowerInvariant();
            var value = declaration[(separator + 1)..].Trim().ToLowerInvariant();
            if (property == "text-align")
                return value;
        }
        return null;
    }

    private static bool ShouldRemove(Element element)
    {
        var tag = element.Tag ?? "";
        if (RemovedElements.Contains(tag))
            return true;
        if (AttributeValue(element, "hidden") is not null)
            return true;
        if (string.Equals(AttributeValue(element, "aria-hidden"), "true", StringComparison.OrdinalIgnoreCase))
            return true;
        if (tag == "input" && string.Equals(AttributeValue(element, "type"), "hidden", StringComparison.OrdinalIgnoreCase))
            return true;
        var style = AttributeValue(element, "style");
        if (style is not null)
        {
            foreach (var declaration in style.Split(';'))
            {
                var separator = declaration.IndexOf(':');
                if (separator < 0)
                    continue;
                var property = declaration[..separator].Trim().ToLowerInvariant();
                var value = declaration[(separator + 1)..].Trim().ToLowerInvariant()
                    .Replace("!important", "").Trim();
                if (property == "display" && value == "none")
                    return true;
                if (property == "visibility" && value is "hidden" or "collapse")
                    return true;
            }
        }
        return false;
    }

    private static string? AttributeValue(Element element, string name)
    {
        foreach (var attribute in element.Attributes)
        {
            if (attribute.Name == name)
                return attribute.Value;
        }
        return null;
    }

    private static bool IsAsciiLetter(char character)
        => character is >= 'a' and <= 'z' or >= 'A' and <= 'Z';

    private static bool IsAsciiLetterOrDigit(char character)
        => IsAsciiLetter(character) || character is >= '0' and <= '9';
}
