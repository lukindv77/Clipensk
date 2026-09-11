using System.Globalization;
using System.Text;
using Clipensk.Core.Clipboard;

namespace Clipensk.Infrastructure.Clipboard;

public sealed class ManagedClipboardRtfSearchTextConverter : IClipboardRtfSearchTextConverter
{
    private const int MaxGroupDepth = 256;
    private const int MaxUnicodeFallbackCharacters = 32;

    private static readonly HashSet<string> IgnoredDestinations = new(StringComparer.Ordinal)
    {
        "annotation",
        "atnauthor",
        "atndate",
        "atnicn",
        "atnid",
        "atnparent",
        "atnref",
        "atntime",
        "background",
        "colorschememapping",
        "colortbl",
        "datastore",
        "filetbl",
        "fldinst",
        "fonttbl",
        "footer",
        "footerf",
        "footerl",
        "footerr",
        "footnote",
        "generator",
        "header",
        "headerf",
        "headerl",
        "headerr",
        "info",
        "latentstyles",
        "listoverridetable",
        "listtable",
        "mmathPr",
        "nonshppict",
        "object",
        "pict",
        "revtbl",
        "rsidtbl",
        "shp",
        "shpinst",
        "shppict",
        "stylesheet",
        "themedata",
        "xmlnstbl",
    };

    public string? TryConvert(string rtf)
    {
        ArgumentNullException.ThrowIfNull(rtf);

        if (rtf.Length < 6 ||
            !rtf.StartsWith("{\\rtf", StringComparison.Ordinal) ||
            !char.IsAsciiDigit(rtf[5]))
        {
            return null;
        }

        var output = new StringBuilder(rtf.Length);
        var stack = new Stack<ParserState>();
        var state = new ParserState();
        bool rootClosed = false;
        int index = 0;

        while (index < rtf.Length)
        {
            char character = rtf[index];

            if (rootClosed)
            {
                if (!char.IsWhiteSpace(character))
                {
                    return null;
                }

                index++;
                continue;
            }

            if (character == '{')
            {
                if (stack.Count >= MaxGroupDepth)
                {
                    return null;
                }

                stack.Push(state.Clone());
                state = state.CloneForChildGroup();
                index++;
                continue;
            }

            if (character == '}')
            {
                if (stack.Count == 0)
                {
                    return null;
                }

                state = stack.Pop();
                index++;
                if (stack.Count == 0)
                {
                    rootClosed = true;
                }
                continue;
            }

            if (stack.Count == 0)
            {
                return null;
            }

            if (character == '\\')
            {
                if (!TryReadControl(rtf, ref index, state, output))
                {
                    return null;
                }
                continue;
            }

            index++;
            if (character is '\r' or '\n')
            {
                continue;
            }

            AppendCharacter(state, output, character);
        }

        if (!rootClosed || stack.Count != 0)
        {
            return null;
        }

        return NormalizeWhitespace(output.ToString());
    }

    private static bool TryReadControl(
        string rtf,
        ref int index,
        ParserState state,
        StringBuilder output)
    {
        index++;
        if (index >= rtf.Length)
        {
            return false;
        }

        char control = rtf[index];
        if (!char.IsAsciiLetter(control))
        {
            index++;
            return TryHandleControlSymbol(rtf, ref index, control, state, output);
        }

        int wordStart = index;
        while (index < rtf.Length && char.IsAsciiLetter(rtf[index]))
        {
            index++;
        }

        string word = rtf[wordStart..index];
        int parameterStart = index;
        if (index < rtf.Length && rtf[index] == '-')
        {
            index++;
        }
        while (index < rtf.Length && char.IsAsciiDigit(rtf[index]))
        {
            index++;
        }

        bool hasParameter = index > parameterStart &&
            !(index == parameterStart + 1 && rtf[parameterStart] == '-');
        int parameter = 0;
        if (hasParameter &&
            !int.TryParse(
                rtf.AsSpan(parameterStart, index - parameterStart),
                NumberStyles.AllowLeadingSign,
                CultureInfo.InvariantCulture,
                out parameter))
        {
            return false;
        }

        if (index < rtf.Length && rtf[index] == ' ')
        {
            index++;
        }

        if (state.PendingIgnorableDestination)
        {
            state.PendingIgnorableDestination = false;
            state.SkipDestination = true;
            return true;
        }

        if (IgnoredDestinations.Contains(word))
        {
            state.SkipDestination = true;
            return true;
        }

        if (string.Equals(word, "bin", StringComparison.Ordinal))
        {
            return false;
        }

        if (state.SkipDestination)
        {
            return true;
        }

        switch (word)
        {
            case "uc":
                if (!hasParameter || parameter is < 0 or > MaxUnicodeFallbackCharacters)
                {
                    return false;
                }

                state.UnicodeFallbackLength = parameter;
                return true;

            case "u":
                if (!hasParameter || parameter is < short.MinValue or > short.MaxValue)
                {
                    return false;
                }

                if (!state.Hidden)
                {
                    output.Append(unchecked((char)(short)parameter));
                }
                state.PendingUnicodeFallback = state.UnicodeFallbackLength;
                return true;

            case "v":
                state.Hidden = !hasParameter || parameter != 0;
                return true;

            case "plain":
                state.Hidden = false;
                return true;

            case "par":
            case "line":
            case "cell":
            case "row":
            case "tab":
                AppendSeparator(state, output);
                return true;

            case "emdash":
                AppendCharacter(state, output, '—');
                return true;
            case "endash":
                AppendCharacter(state, output, '–');
                return true;
            case "bullet":
                AppendCharacter(state, output, '•');
                return true;
            case "lquote":
            case "rquote":
                AppendCharacter(state, output, '\'');
                return true;
            case "ldblquote":
            case "rdblquote":
                AppendCharacter(state, output, '"');
                return true;
            default:
                return true;
        }
    }

    private static bool TryHandleControlSymbol(
        string rtf,
        ref int index,
        char control,
        ParserState state,
        StringBuilder output)
    {
        switch (control)
        {
            case '\\':
            case '{':
            case '}':
                AppendCharacter(state, output, control);
                return true;

            case '\'':
                if (index + 2 > rtf.Length ||
                    !TryParseHexByte(rtf.AsSpan(index, 2), out byte value))
                {
                    return false;
                }
                index += 2;

                if (state.SkipDestination || state.Hidden)
                {
                    return true;
                }

                if (state.PendingUnicodeFallback > 0)
                {
                    state.PendingUnicodeFallback--;
                    return true;
                }

                if (value > 0x7F)
                {
                    return false;
                }

                output.Append((char)value);
                return true;

            case '*':
                state.PendingIgnorableDestination = true;
                return true;

            case '~':
                AppendSeparator(state, output);
                return true;

            case '_':
                AppendCharacter(state, output, '-');
                return true;

            case '-':
                if (state.PendingUnicodeFallback > 0)
                {
                    state.PendingUnicodeFallback--;
                }
                return true;

            case '\r':
                if (index < rtf.Length && rtf[index] == '\n')
                {
                    index++;
                }
                return true;

            case '\n':
                return true;

            default:
                if (state.PendingUnicodeFallback > 0)
                {
                    state.PendingUnicodeFallback--;
                }
                return true;
        }
    }

    private static void AppendCharacter(
        ParserState state,
        StringBuilder output,
        char character)
    {
        if (state.SkipDestination || state.Hidden)
        {
            return;
        }

        if (state.PendingUnicodeFallback > 0)
        {
            state.PendingUnicodeFallback--;
            return;
        }

        output.Append(character);
    }

    private static void AppendSeparator(ParserState state, StringBuilder output)
    {
        if (state.SkipDestination || state.Hidden)
        {
            return;
        }

        if (state.PendingUnicodeFallback > 0)
        {
            state.PendingUnicodeFallback--;
            return;
        }

        if (output.Length > 0 && !char.IsWhiteSpace(output[^1]))
        {
            output.Append(' ');
        }
    }

    private static bool TryParseHexByte(ReadOnlySpan<char> value, out byte result)
    {
        return byte.TryParse(
            value,
            NumberStyles.AllowHexSpecifier,
            CultureInfo.InvariantCulture,
            out result);
    }

    private static string NormalizeWhitespace(string value)
    {
        var output = new StringBuilder(value.Length);
        bool pendingWhitespace = false;
        foreach (char character in value)
        {
            if (char.IsWhiteSpace(character))
            {
                pendingWhitespace = output.Length > 0;
                continue;
            }

            if (pendingWhitespace)
            {
                output.Append(' ');
                pendingWhitespace = false;
            }

            output.Append(character);
        }

        return output.ToString();
    }

    private sealed class ParserState
    {
        public bool SkipDestination { get; set; }
        public bool PendingIgnorableDestination { get; set; }
        public bool Hidden { get; set; }
        public int UnicodeFallbackLength { get; set; } = 1;
        public int PendingUnicodeFallback { get; set; }

        public ParserState Clone() => new()
        {
            SkipDestination = SkipDestination,
            PendingIgnorableDestination = PendingIgnorableDestination,
            Hidden = Hidden,
            UnicodeFallbackLength = UnicodeFallbackLength,
            PendingUnicodeFallback = PendingUnicodeFallback,
        };

        public ParserState CloneForChildGroup() => new()
        {
            SkipDestination = SkipDestination,
            PendingIgnorableDestination = false,
            Hidden = Hidden,
            UnicodeFallbackLength = UnicodeFallbackLength,
            PendingUnicodeFallback = PendingUnicodeFallback,
        };
    }
}
