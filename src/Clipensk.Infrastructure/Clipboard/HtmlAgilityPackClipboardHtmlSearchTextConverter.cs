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

        HtmlNodeCollection? excludedNodes = document.DocumentNode.SelectNodes(
            "//script|//style|//noscript|//template");
        if (excludedNodes is not null)
        {
            foreach (HtmlNode node in excludedNodes.ToArray())
            {
                node.Remove();
            }
        }

        string decoded = HtmlEntity.DeEntitize(document.DocumentNode.InnerText);
        return NormalizeWhitespace(decoded);
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
