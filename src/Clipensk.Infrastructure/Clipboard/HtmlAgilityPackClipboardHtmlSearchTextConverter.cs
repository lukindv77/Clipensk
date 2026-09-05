using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Clipensk.Core.Clipboard;
using HtmlAgilityPack;

namespace Clipensk.Infrastructure.Clipboard;

public sealed partial class HtmlAgilityPackClipboardHtmlSearchTextConverter : IClipboardHtmlSearchTextConverter
{
    private static readonly UTF8Encoding StrictUtf8 = new(
        encoderShouldEmitUTF8Identifier: false,
        throwOnInvalidBytes: true);

    private static readonly HashSet<string> ExcludedElementNames = new(
        StringComparer.OrdinalIgnoreCase)
    {
        "script",
        "style",
        "noscript",
        "template",
    };

    private static readonly HashSet<string> SeparatorElementNames = new(
        StringComparer.OrdinalIgnoreCase)
    {
        "address", "article", "aside", "blockquote", "br", "dd", "div", "dl", "dt",
        "fieldset", "figcaption", "figure", "footer", "form", "h1", "h2", "h3", "h4",
        "h5", "h6", "header", "hr", "li", "main", "nav", "ol", "p", "pre", "section",
        "table", "tbody", "td", "tfoot", "th", "thead", "tr", "ul",
    };

    public string? TryConvert(string clipboardHtml)
    {
        ArgumentNullException.ThrowIfNull(clipboardHtml);

        if (!TryExtractFragment(clipboardHtml, out string fragment))
        {
            return null;
        }

        var document = new HtmlDocument
        {
            OptionFixNestedTags = true,
        };
        document.LoadHtml(fragment);

        var visibleText = new StringBuilder(fragment.Length);
        AppendVisibleText(document.DocumentNode, visibleText);
        return NormalizeWhitespace(visibleText.ToString());
    }

    private static void AppendVisibleText(HtmlNode node, StringBuilder builder)
    {
        if (node.NodeType == HtmlNodeType.Comment)
        {
            return;
        }

        if (node.NodeType == HtmlNodeType.Text)
        {
            builder.Append(HtmlEntity.DeEntitize(node.InnerText));
            return;
        }

        if (node.NodeType == HtmlNodeType.Element && ExcludedElementNames.Contains(node.Name))
        {
            return;
        }

        bool separates = node.NodeType == HtmlNodeType.Element &&
            SeparatorElementNames.Contains(node.Name);
        if (separates)
        {
            AppendSeparator(builder);
        }

        foreach (HtmlNode child in node.ChildNodes)
        {
            AppendVisibleText(child, builder);
        }

        if (separates)
        {
            AppendSeparator(builder);
        }
    }

    private static void AppendSeparator(StringBuilder builder)
    {
        if (builder.Length > 0 && !char.IsWhiteSpace(builder[^1]))
        {
            builder.Append(' ');
        }
    }

    private static bool TryExtractFragment(string clipboardHtml, out string fragment)
    {
        Match startMarker = StartFragmentMarker().Match(clipboardHtml);
        if (startMarker.Success)
        {
            Match endMarker = EndFragmentMarker().Match(
                clipboardHtml,
                startMarker.Index + startMarker.Length);
            if (endMarker.Success)
            {
                fragment = clipboardHtml.Substring(
                    startMarker.Index + startMarker.Length,
                    endMarker.Index - (startMarker.Index + startMarker.Length));
                return true;
            }
        }

        return TryExtractFragmentByByteOffsets(clipboardHtml, out fragment);
    }

    private static bool TryExtractFragmentByByteOffsets(
        string clipboardHtml,
        out string fragment)
    {
        fragment = string.Empty;
        if (!TryReadHeaderOffset(clipboardHtml, "StartFragment", out long startOffset) ||
            !TryReadHeaderOffset(clipboardHtml, "EndFragment", out long endOffset) ||
            startOffset < 0 ||
            endOffset < startOffset)
        {
            return false;
        }

        byte[] utf8 = Encoding.UTF8.GetBytes(clipboardHtml);
        if (endOffset > utf8.LongLength)
        {
            return false;
        }

        int start = checked((int)startOffset);
        int length = checked((int)(endOffset - startOffset));
        try
        {
            fragment = StrictUtf8.GetString(utf8, start, length);
            return true;
        }
        catch (DecoderFallbackException)
        {
            fragment = string.Empty;
            return false;
        }
    }

    private static bool TryReadHeaderOffset(
        string clipboardHtml,
        string key,
        out long value)
    {
        value = 0;
        int headerEnd = clipboardHtml.IndexOf('<');
        if (headerEnd < 0)
        {
            headerEnd = Math.Min(clipboardHtml.Length, 8192);
        }
        else
        {
            headerEnd = Math.Min(headerEnd, 8192);
        }

        string prefix = key + ":";
        int index = clipboardHtml.IndexOf(prefix, 0, headerEnd, StringComparison.Ordinal);
        if (index < 0)
        {
            return false;
        }

        index += prefix.Length;
        while (index < headerEnd && char.IsWhiteSpace(clipboardHtml[index]) &&
               clipboardHtml[index] is not '\r' and not '\n')
        {
            index++;
        }

        int numberStart = index;
        if (index < headerEnd && clipboardHtml[index] == '-')
        {
            index++;
        }
        while (index < headerEnd && char.IsAsciiDigit(clipboardHtml[index]))
        {
            index++;
        }

        if (index == numberStart ||
            (clipboardHtml[numberStart] == '-' && index == numberStart + 1))
        {
            return false;
        }

        return long.TryParse(
            clipboardHtml.AsSpan(numberStart, index - numberStart),
            NumberStyles.AllowLeadingSign,
            CultureInfo.InvariantCulture,
            out value);
    }

    private static string NormalizeWhitespace(string value)
    {
        if (value.Length == 0)
        {
            return string.Empty;
        }

        var builder = new StringBuilder(value.Length);
        bool pendingWhitespace = false;
        foreach (char character in value)
        {
            if (char.IsWhiteSpace(character))
            {
                pendingWhitespace = builder.Length > 0;
                continue;
            }

            if (pendingWhitespace)
            {
                builder.Append(' ');
                pendingWhitespace = false;
            }
            builder.Append(character);
        }

        return builder.ToString();
    }

    [GeneratedRegex("<!--\\s*StartFragment\\s*-->", RegexOptions.CultureInvariant)]
    private static partial Regex StartFragmentMarker();

    [GeneratedRegex("<!--\\s*EndFragment\\s*-->", RegexOptions.CultureInvariant)]
    private static partial Regex EndFragmentMarker();
}
