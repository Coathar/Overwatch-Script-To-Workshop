namespace Deltin;
using System;
using System.Text;
using System.Linq;
using System.Collections.Generic;
using Constants = Deltin.Deltinteger.Constants;

static class WorkshopStringUtility
{
    public const char PREVENT_FORMAT_CHARACTER = '⨁';

    /// <summary>Splits a string into 511 bytes long segments, with each of those segments split into 128 byte chunks.
    /// A string literal in the workshop can only be 128 bytes long, but by appending strings you can reach a
    /// maximum of 511 bytes.</summary>
    /// <param name="value">The input string that will be segmented and chunked.</param>
    /// <param name="splitHead">An optional value that is appended to the end of each 511 byte segments.</param>
    /// <param name="splitTail">An optional value that is prepended to the start of each 511 byte segments.</param>
    /// <param name="stringCharacters">An optional value that contains the characters used to start/end strings.
    /// Stubs will not be split mid string.</param>
    /// <returns>A 2d string where the first dimension is the 511 byte segments and the second dimension is the
    /// 128 byte chunks.</returns>
    public static StringChunk[][] ChunkSplit(string value, string splitHead, string splitTail, char[] stringCharacters)
    {
        // Ensure parameters are not null.
        splitHead = splitHead ?? string.Empty;
        splitTail = splitTail ?? string.Empty;
        stringCharacters = stringCharacters ?? new char[0];

        var total = new List<List<StringChunk>>(); // 511 byte chunks
        var stubs = new List<StringChunk>(); // 128 byte stubs
        var currentStub = string.Empty;
        string lastValidDecoratedStub = null;
        var formatHelper = new ChunkedFormatHelper();
        bool stubHasFormatPrevention = false;

        int position = 0;
        while (position < value.Length)
        {
            // four 128 byte strings can fit into 511 bytes, with 1 subtracted from the final stub.
            var isFirstStub = stubs.Count == 0;
            var isLastStub = stubs.Count == 3;

            // Get the next minimum chunk.
            var (addText, newPosition, addedTextNeedsFormatPrevention) = GetMinimumChunk(value, stringCharacters, formatHelper, position);
            currentStub += addText;
            position = newPosition;

            // The workshop uses UTF8 encoding.
            var currentStubWithDecorations = DecorateStub(currentStub, splitHead, splitTail, isFirstStub, isLastStub);
            var currentStubLength = LengthOfStringInWorkshop(currentStubWithDecorations);

            // In theory this should be MAX_STRING_STUB_BYTE_LENGTH or MAX_STRING_STUB_BYTE_LENGTH - 1 if isLastStub,
            // but this seems to work fine.
            var max = Constants.MAX_STRING_STUB_BYTE_LENGTH + 1;

            if (currentStubLength >= max || formatHelper.OverCapacity())
            {
                // If 'previousDecoratedStub' is null, splitHead + splitTail exceeds the workshop's string byte size.
                if (lastValidDecoratedStub == null)
                    throw new System.Exception("The lengths of the splitHead and splitTail parameters combined exceed " + Constants.MAX_STRING_STUB_BYTE_LENGTH + " bytes");

                // Add last valid stub.
                AddStub(stubs, lastValidDecoratedStub, formatHelper, stubHasFormatPrevention);

                // Update the current stub.
                stubHasFormatPrevention = addedTextNeedsFormatPrevention;
                currentStub = addText;
                lastValidDecoratedStub = DecorateStub(currentStub, splitHead, splitTail, isFirstStub, isLastStub);

                if (isLastStub)
                {
                    total.Add(stubs);
                    stubs = new();
                }
            }
            else
            {
                lastValidDecoratedStub = currentStubWithDecorations;
                stubHasFormatPrevention |= addedTextNeedsFormatPrevention;
            }
        }

        // Add remnant stub
        if (currentStub.Length != 0)
        {
            AddStub(stubs, DecorateStub(currentStub, splitHead, splitTail, stubs.Count == 0, true), formatHelper, stubHasFormatPrevention);
        }
        // Add remnant group
        if (stubs.Count != 0)
        {
            total.Add(stubs);
        }

        // List<List<StringChunk>> to StringChunk[][]
        return total.Select(t => t.ToArray()).ToArray();
    }

    static string DecorateStub(string stub, string splitHead, string splitTail, bool isFirstStub, bool isLastStub)
    {
        if (isFirstStub)
            stub = splitTail + stub;
        if (isLastStub)
            stub += splitHead;
        return stub;
    }

    /// <summary>Adds a stub to a list of stubs.</summary>
    /// <param name="stubs">The list containing the stubs.</param>
    /// <param name="lastValidDecoratedStub">The stub text.</param>
    /// <param name="formatHelper">The stub's text formats.</param>
    static void AddStub(List<StringChunk> stubs, string lastValidDecoratedStub, ChunkedFormatHelper formatHelper, bool hasFormatPrevention)
    {
        stubs.Add(new(lastValidDecoratedStub, formatHelper.ExtractStub(), hasFormatPrevention));
    }

    /// <summary>
    /// When ChunkSplit is splitting text, the text shouldn't be split inside some elements.
    /// For example, element identifiers, strings, and formats would have issues if they were cut in the middle.
    /// GetMinimumCharacters will get the next set of characters at the provided position which shouldn't be separated.
    /// </summary>
    /// <param name="str">The input text.</param>
    /// <param name="stringCharacters">The characters which start and end strings such as ' and ".</param>
    /// <param name="formatHelper">The formats in the current stub.</param>
    /// <param name="position">The current text position.</param>
    static TextProgress GetMinimumChunk(
        string str,
        char[] stringCharacters,
        ChunkedFormatHelper formatHelper,
        int position)
    {

        // Check for format.
        var formatChunk = GetFormatChunk(str, formatHelper, position);
        if (formatChunk.HasValue)
            return formatChunk.Value;

        // Check for string.
        var stringChunk = GetStringChunk(str, stringCharacters, formatHelper, position);
        if (stringChunk.HasValue)
            return stringChunk.Value;

        // Check for line comment.
        var commentChunk = GetCommentChunk(str, position);
        if (commentChunk.HasValue)
            return commentChunk.Value;

        // Capture all text until next whitespace.
        string captured = string.Empty;
        do
        {
            captured += str[position];
            position++;
        }
        while (
            // Ensure the position does not go out of range.
            position < str.Length &&
            // Do not consume any opening curly brackets in case it is the start of a format.
            str[position] != '{' &&
            // Do not consume any slashes in case it is the start of a line comment.
            str[position] != '/' &&
            // Do not consume any pounds in case it is the start of a line comment.
            str[position] != '#' &&
            // Do not consume quotes
            !stringCharacters.Contains(str[position]) &&
            // This is only possible on the first 'do' iteration. If we get a whitespace, only return that whitespace.
            !char.IsWhiteSpace(captured.Last()) &&
            // The next character is a whitespace, we can stop here.
            !char.IsWhiteSpace(str[position]));

        return new(captured, position);
    }

    /// <summary>Checks for a format at the provided text position.</summary>
    static Nullable<TextProgress> GetFormatChunk(string str, ChunkedFormatHelper formatHelper, int position)
    {
        var formatPattern = FindFormatPattern(str, position);
        if (!formatPattern.HasValue)
            return null;

        var (formatInput, newPosition) = formatPattern.Value;
        int formatWorkshop = formatHelper.GetFormat(formatInput);
        return new("{" + formatWorkshop + "}", newPosition);
    }

    /// <summary>Checks for a string at the provided text position.</summary>
    static Nullable<TextProgress> GetStringChunk(string str, char[] stringCharacters, ChunkedFormatHelper formatHelper, int position)
    {
        // Do nothing if not start of string.
        if (!stringCharacters.Contains(str[position]))
            return null;
        // Start chunk using string terminator.
        string chunk = string.Empty;
        // Escape if it is a double quote.
        if (str[position] == '"')
            chunk = "\\";
        // Add opening string character.
        chunk += str[position].ToString();
        // Save the character used to start the string.
        char terminateCharacter = str[position];
        bool escaping = false;
        bool hasFormatPrevention = false;
        // Progress past starting string character.
        position++;
        for (; position < str.Length; position++)
        {
            if (escaping)
            {
                escaping = false;
                // Add 2 extra backslashes if there is a " in a double quoted string.
                if (terminateCharacter == '"' && str[position] == '"')
                    chunk += @"\\";
                chunk += "\\" + str[position];
            }
            else if (str[position] == '\\')
                escaping = true;
            else if (str[position] == terminateCharacter)
            {
                // End of string found.
                if (terminateCharacter == '"')
                    chunk += '\\';
                chunk += str[position];
                break;
            }
            else
            {
                var format = FindFormatPattern(str, position);
                if (format.HasValue && format.Value.FormatValue <= 2)
                {
                    hasFormatPrevention = true;
                    chunk += PREVENT_FORMAT_CHARACTER.ToString() + format.Value.FormatValue + "}";
                    position = format.Value.EndOfFormatPosition - 1;
                }
                else
                    chunk += str[position];
            }
        }
        return new(chunk, position + 1, hasFormatPrevention);
    }

    /// <summary>Checks for a line comment at the provided text position.</summary>
    static Nullable<TextProgress> GetCommentChunk(string str, int position)
    {
        string chunk;

        // # string
        if (position < str.Length && str[position] == '#')
        {
            chunk = "#";
            position += 1;
        }
        // Double slash string
        else if (position < str.Length - 1 && str[position] == '/' && str[position + 1] == '/')
        {
            chunk = "//";
            position += 2;
        }
        // Not a string
        else return null;

        // Consume until end of line.
        bool stopAppendingChunk = false;
        for (; position < str.Length; position++)
        {
            // End at newline
            if (str[position] == '\n')
                break;

            // Stop adding to chunk once the maximum length has been reached.
            if (!stopAppendingChunk && LengthOfStringInWorkshop(chunk + str[position] + '\n') >= Constants.MAX_STRING_STUB_BYTE_LENGTH)
                stopAppendingChunk = true;

            if (!stopAppendingChunk)
                chunk += str[position];
        }
        return new(chunk + '\n', position + 1);
    }

    static Nullable<(int FormatValue, int EndOfFormatPosition)> FindFormatPattern(string str, int position)
    {
        // Format start
        // 'str.Length - 1' would also technically work, this would catch {} at eof though.
        if (str[position] == '{' && position < str.Length - 2)
        {
            position++;
            string number = string.Empty;
            // Get format number
            while (position < str.Length && char.IsNumber(str[position]))
            {
                number += str[position];
                position++;
            }
            // If number.Length == 0, then the text at position is just "{}" and is not a format.
            // The text at the end of the number must be '}'.
            if (number.Length > 0 && position < str.Length && str[position] == '}')
            {
                // The number in the format.
                int formatInput = int.Parse(number);
                return new(formatInput, position + 1);
            }
        }
        return null;
    }

    readonly record struct TextProgress(string Text, int NewPosition, bool HasFormatPrevention = false);

    struct ChunkedFormatHelper
    {
        List<int> currentFormats;

        public ChunkedFormatHelper()
        {
            currentFormats = new List<int>();
        }

        public int GetFormat(int userInput)
        {
            int workshopValue = currentFormats.IndexOf(userInput);
            if (workshopValue == -1)
            {
                currentFormats.Add(userInput);
                workshopValue = currentFormats.Count - 1;
            }
            return workshopValue % 3;
        }

        public bool OverCapacity() => currentFormats.Count > 3;

        public int[] ExtractStub()
        {
            var stubFormats = currentFormats.Take(3).ToArray();
            currentFormats.RemoveRange(0, Math.Min(3, currentFormats.Count));
            return stubFormats;
        }
    }

    /// <summary>Gets the byte count of a string once it is imported into the workshop.</summary>
    static int LengthOfStringInWorkshop(string str)
    {
        return Encoding.UTF8.GetByteCount(str.Replace("\r", "\\r").Replace("\n", "\\n"));
    }

    /// <summary>Makes an OSTW string more compatible with the workshop.
    /// Escaped single quotes are unescaped, unescaped double quotes are escaped.</summary>
    /// <param name="raw">The OSTW string. First and last character should be ' or ".</param>
    public static string WorkshopStringFromRawText(string raw)
    {
        // Single or double quoted string?
        bool isSingle = raw.StartsWith('\'');
        // Trim starting and ending quotations.
        string withoutQuotes = raw.Substring(1, raw.Length - 2);

        // No special processing needs to happen with double quotes.
        if (!isSingle)
            return withoutQuotes;

        // Single quoted strings will be escaped, they do not need to be.
        withoutQuotes = withoutQuotes.Replace("\\'", "'");
        // Conversely, double quotes may not be escaped though they should be.
        return EscapedDoubleQuotes(withoutQuotes);
    }

    static string EscapedDoubleQuotes(string input)
    {
        string result = string.Empty;
        bool escaping = false;
        for (int i = 0; i < input.Length; i++)
        {
            if (escaping)
                escaping = false;
            else if (input[i] == '\\')
                escaping = true;
            else if (input[i] == '"')
                result += '\\';
            result += input[i];
        }
        return result;
    }
}

readonly record struct StringChunk(string Value, int[] Parameters, bool HasFormatPrevention);