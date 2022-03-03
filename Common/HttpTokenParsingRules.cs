// Adapted from Microsoft.AspNetCore.Routing, where the following
// types are internal.

// The original code is
// licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System;
using System.Diagnostics;

namespace Hearty.Common;

internal static class HttpTokenParsingRules
{
    private const int MaxNestedCount = 5;

    internal const char CR = '\r';
    internal const char LF = '\n';
    internal const char SP = ' ';
    internal const char Tab = '\t';
    internal const int MaxInt64Digits = 19;
    internal const int MaxInt32Digits = 10;

    private static ReadOnlySpan<byte> TokenCharsBitSet => new byte[]
    {
        0x00, 0x00, 0x00, 0x00, 0xFA, 0x6C, 0xFF, 0x03,
        0xFE, 0xFF, 0xFF, 0xC7, 0xFF, 0xFF, 0xFF, 0x57
    };

    // Above bit-set is the packed result from the following code
    /*
    private static bool[] CreateTokenChars()
    {
        // token = 1*<any CHAR except CTLs or separators>
        // CTL = <any US-ASCII control character (octets 0 - 31) and DEL (127)>

        var tokenChars = new bool[128]; // everything is false

        for (var i = 33; i < 127; i++) // skip Space (32) & DEL (127)
        {
            tokenChars[i] = true;
        }

        // remove separators: these are not valid token characters
        tokenChars[(byte)'('] = false;
        tokenChars[(byte)')'] = false;
        tokenChars[(byte)'<'] = false;
        tokenChars[(byte)'>'] = false;
        tokenChars[(byte)'@'] = false;
        tokenChars[(byte)','] = false;
        tokenChars[(byte)';'] = false;
        tokenChars[(byte)':'] = false;
        tokenChars[(byte)'\\'] = false;
        tokenChars[(byte)'"'] = false;
        tokenChars[(byte)'/'] = false;
        tokenChars[(byte)'['] = false;
        tokenChars[(byte)']'] = false;
        tokenChars[(byte)'?'] = false;
        tokenChars[(byte)'='] = false;
        tokenChars[(byte)'{'] = false;
        tokenChars[(byte)'}'] = false;

        return tokenChars;
    }
    */

    internal static bool IsTokenChar(char character)
    {
        // Must be between 'space' (32) and 'DEL' (127)
        if (character > 127)
            return false;

        var b = (byte)character;
        return (TokenCharsBitSet[b >> 3] & ((byte)1 << (b & 7))) != 0;
    }

    internal static int GetTokenLength(string input, int startIndex, int endIndex)
    {
        Debug.Assert(input != null);
        endIndex = (endIndex < input.Length) ? endIndex : input.Length;

        if (startIndex >= endIndex)
            return 0;

        var current = startIndex;

        while (current < endIndex)
        {
            if (!IsTokenChar(input[current]))
                return current - startIndex;

            current++;
        }

        return endIndex - startIndex;
    }

    internal static int GetWhitespaceLength(string input, int startIndex, int endIndex)
    {
        Debug.Assert(input != null);
        endIndex = (endIndex < input.Length) ? endIndex : input.Length;

        if (startIndex >= endIndex)
            return 0;

        var current = startIndex;

        while (current < endIndex)
        {
            var c = input[current];

            if ((c == SP) || (c == Tab))
            {
                current++;
                continue;
            }

            if (c == CR)
            {
                // If we have a #13 char, it must be followed by #10 and then at least one SP or HT.
                if ((current + 2 < endIndex) && (input[current + 1] == LF))
                {
                    var spaceOrTab = input[current + 2];
                    if ((spaceOrTab == SP) || (spaceOrTab == Tab))
                    {
                        current += 3;
                        continue;
                    }
                }
            }

            return current - startIndex;
        }

        // All characters between startIndex and the end of the string are LWS characters.
        return endIndex - startIndex;
    }

    internal static HttpParseResult GetQuotedStringLength(string input, int startIndex, int endIndex, out int length)
    {
        var nestedCount = 0;
        return GetExpressionLength(input, startIndex, endIndex, '"', '"', false, ref nestedCount, out length);
    }

    // quoted-pair = "\" CHAR
    // CHAR = <any US-ASCII character (octets 0 - 127)>
    private static HttpParseResult GetQuotedPairLength(string input, int startIndex, int endIndex, out int length)
    {
        Debug.Assert(input != null);
        Debug.Assert((startIndex >= 0) && (startIndex < endIndex));
        endIndex = (endIndex < input.Length) ? endIndex : input.Length;

        length = 0;

        if (input[startIndex] != '\\')
            return HttpParseResult.NotParsed;

        // Quoted-char has 2 characters. Check whether there are 2 chars left ('\' + char)
        // If so, check whether the character is in the range 0-127. If not, it's an invalid value.
        if ((startIndex + 2 > endIndex) || (input[startIndex + 1] > 127))
            return HttpParseResult.InvalidFormat;

        // We don't care what the char next to '\' is.
        length = 2;
        return HttpParseResult.Parsed;
    }

    // TEXT = <any OCTET except CTLs, but including LWS>
    // LWS = [CRLF] 1*( SP | HT )
    // CTL = <any US-ASCII control character (octets 0 - 31) and DEL (127)>
    //
    // Since we don't really care about the content of a quoted string or comment, we're more tolerant and
    // allow these characters. We only want to find the delimiters ('"' for quoted string and '(', ')' for comment).
    //
    // 'nestedCount': Comments can be nested. We allow a depth of up to 5 nested comments, i.e. something like
    // "(((((comment)))))". If we wouldn't define a limit an attacker could send a comment with hundreds of nested
    // comments, resulting in a stack overflow exception. In addition having more than 1 nested comment (if any)
    // is unusual.
    private static HttpParseResult GetExpressionLength(
        string input,
        int startIndex,
        int endIndex,
        char openChar,
        char closeChar,
        bool supportsNesting,
        ref int nestedCount,
        out int length)
    {
        Debug.Assert(input != null);
        Debug.Assert((startIndex >= 0) && (startIndex < endIndex));
        endIndex = (endIndex < input.Length) ? endIndex : input.Length;

        length = 0;

        if (input[startIndex] != openChar)
            return HttpParseResult.NotParsed;

        var current = startIndex + 1; // Start parsing with the character next to the first open-char
        while (current < endIndex)
        {
            // Only check whether we have a quoted char, if we have at least 3 characters left to read (i.e.
            // quoted char + closing char). Otherwise the closing char may be considered part of the quoted char.
            if ((current + 2 < endIndex) &&
                (GetQuotedPairLength(input, current, endIndex, out var quotedPairLength) == HttpParseResult.Parsed))
            {
                // We ignore invalid quoted-pairs. Invalid quoted-pairs may mean that it looked like a quoted pair,
                // but we actually have a quoted-string: e.g. "\ü" ('\' followed by a char >127 - quoted-pair only
                // allows ASCII chars after '\'; qdtext allows both '\' and >127 chars).
                current = current + quotedPairLength;
                continue;
            }

            // If we support nested expressions and we find an open-char, then parse the nested expressions.
            if (supportsNesting && (input[current] == openChar))
            {
                nestedCount++;
                try
                {
                    // Check if we exceeded the number of nested calls.
                    if (nestedCount > MaxNestedCount)
                    {
                        return HttpParseResult.InvalidFormat;
                    }

                    var nestedResult = GetExpressionLength(
                        input,
                        current,
                        endIndex,
                        openChar,
                        closeChar,
                        supportsNesting,
                        ref nestedCount,
                        out var nestedLength);

                    switch (nestedResult)
                    {
                        case HttpParseResult.Parsed:
                            current += nestedLength; // add the length of the nested expression and continue.
                            break;

                        case HttpParseResult.NotParsed:
                            Debug.Fail("'NotParsed' is unexpected: We started nested expression " +
                                "parsing, because we found the open-char. So either it's a valid nested " +
                                "expression or it has invalid format.");
                            break;

                        case HttpParseResult.InvalidFormat:
                            // If the nested expression is invalid, we can't continue, so we fail with invalid format.
                            return HttpParseResult.InvalidFormat;

                        default:
                            Debug.Fail("Unknown enum result: " + nestedResult);
                            break;
                    }
                }
                finally
                {
                    nestedCount--;
                }
            }

            if (input[current] == closeChar)
            {
                length = current - startIndex + 1;
                return HttpParseResult.Parsed;
            }
            current++;
        }

        // We didn't see the final quote, therefore we have an invalid expression string.
        return HttpParseResult.InvalidFormat;
    }
}

internal enum HttpParseResult
{
    Parsed,
    NotParsed,
    InvalidFormat,
}
