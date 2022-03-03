// Adapted from Microsoft.AspNetCore.Routing.ReadOnlyMediaTypeHeaderValue
// which is an internal type in the Microsoft library.

// The original code is licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System;
using System.Text;
using Microsoft.Extensions.Primitives;

namespace Hearty.Common;

/// <summary>
/// Decomposition of the string for an IANA media type, with optional parameters.
/// </summary>
/// <remarks>
/// Most properties are parsed on demand without string allocations.
/// There are also methods to help implement content negotiation.
/// </remarks>
public readonly struct ParsedContentType
{
    /// <summary>
    /// Implicitly parses a string into a <see cref="ParsedContentType"/> instance,
    /// typically used for string literals.
    /// </summary>
    /// <param name="mediaType">The <see cref="string"/> with the media type.</param>
    public static implicit operator ParsedContentType(string mediaType)
        => new ParsedContentType(mediaType);

    /// <summary>
    /// Initializes a <see cref="ParsedContentType"/> instance.
    /// </summary>
    /// <param name="mediaType">The <see cref="string"/> with the media type.</param>
    public ParsedContentType(string mediaType)
        : this(new StringSegment(mediaType))
    {
    }

    /// <summary>
    /// Initializes a <see cref="ParsedContentType"/> instance.
    /// </summary>
    /// <param name="mediaType">The <see cref="StringSegment"/> with the media type.</param>
    public ParsedContentType(StringSegment mediaType)
    {
        this = default;

        if (!mediaType.HasValue)
            mediaType = StringSegment.Empty;

        string buffer = mediaType.Buffer;
        int offset = mediaType.Offset;
        int end = offset + mediaType.Length;

        Input = mediaType;

        var typeLength = GetTypeLength(buffer, offset, end,
                                       out var type);
        if (typeLength == 0)
            return;

        _typeOffset = type.Offset;
        _typeLength = type.Length;

        var subTypeLength = GetSubtypeLength(buffer, offset + typeLength, end,
                                             out var subType);
        if (subTypeLength == 0)
            return;

        _subTypeOffset = subType.Offset;
        _subTypeLength = subType.Length;

        TryGetSuffixLength(subType, out _subTypeSuffixLength);

        _parametersOffset = offset + typeLength + subTypeLength;
    }

    /// <summary>
    /// Whether the type and sub-type of the media type could
    /// be parsed successfully.
    /// </summary>
    /// <remarks>
    /// This property is still true if the parameters are not
    /// in a valid syntax.  Use <see cref="IsValid" />
    /// to check the parameters also.
    /// </remarks>
    public bool HasValidType => _typeLength > 0 && _subTypeLength > 0;

    /// <summary>
    /// Whether all the parameters can be successfully parsed.
    /// </summary>
    public bool HasValidParameters => 
        TryGetLastParameter(string.Empty, out _);

    /// <summary>
    /// Whether the media type could be successfully parsed.
    /// </summary>
    public bool IsValid => HasValidType && HasValidParameters;

    // All GetXXXLength methods work in the same way. They expect to be on the right position for
    // the token they are parsing, for example, the beginning of the media type or the delimiter
    // from a previous token, like '/', ';' or '='.
    // Each method consumes the delimiter token if any, the leading whitespace, then the given token
    // itself, and finally the trailing whitespace.
    private static int GetTypeLength(string input, int offset, int end, out StringSegment type)
    {
        if (offset < 0 || offset >= end)
        {
            type = default(StringSegment);
            return 0;
        }

        var current = offset + HttpTokenParsingRules.GetWhitespaceLength(input, offset, end);

        // Parse the type, i.e. <type> in media type string "<type>/<subtype>; param1=value1; param2=value2"
        var typeLength = HttpTokenParsingRules.GetTokenLength(input, current, end);
        if (typeLength == 0)
        {
            type = default(StringSegment);
            return 0;
        }

        type = new StringSegment(input, current, typeLength);

        current += typeLength;
        current += HttpTokenParsingRules.GetWhitespaceLength(input, current, end);

        return current - offset;
    }

    private static int GetSubtypeLength(string input, int offset, int end, out StringSegment subType)
    {
        var current = offset;

        // Parse the separator between type and subtype
        if (current < 0 || current >= end || input[current] != '/')
        {
            subType = default(StringSegment);
            return 0;
        }

        current++; // skip delimiter.
        current += HttpTokenParsingRules.GetWhitespaceLength(input, current, end);

        var subtypeLength = HttpTokenParsingRules.GetTokenLength(input, current, end);
        if (subtypeLength == 0)
        {
            subType = default(StringSegment);
            return 0;
        }

        subType = new StringSegment(input, current, subtypeLength);

        current += subtypeLength;
        current += HttpTokenParsingRules.GetWhitespaceLength(input, current, end);

        return current - offset;
    }

    private static bool TryGetSuffixLength(StringSegment subType, out int suffixLength)
    {
        int index = subType.LastIndexOf('+');
        if (index >= 0)
        {
            suffixLength = subType.Length - index;
            return true;
        }
        else
        {
            suffixLength = 0;
            return false;
        }
    }

    /// <summary>
    /// The offset within the buffer of <see cref="Input" /> for the first
    /// character of the major type identifier, excluding whitespace.
    /// </summary>
    private readonly int _typeOffset;

    /// <summary>
    /// The length in characters for the major type identifier, 
    /// excluding whitespace at the end.
    /// </summary>
    private readonly int _typeLength;

    /// <summary>
    /// The offset within the buffer of <see cref="Input" /> for the first
    /// character of the sub-type identifier, excluding whitespace.
    /// </summary>
    private readonly int _subTypeOffset;

    /// <summary>
    /// The length in characters for the sub-type identifier, 
    /// excluding whitespace at the end.
    /// </summary>
    private readonly int _subTypeLength;

    /// <summary>
    /// The length of the suffix in the sub-type string, 
    /// including the separator '+', or zero if there is no suffix.
    /// </summary>
    private readonly int _subTypeSuffixLength;

    /// <summary>
    /// The offset within the buffer of <see cref="Input" /> where
    /// parameters might start.
    /// </summary>
    private readonly int _parametersOffset;

    /// <inheritdoc cref="object.ToString" />
    public override string ToString() => Input.ToString();

    /// <summary>
    /// The original input string for the media type
    /// that had been passed to the constructor.
    /// </summary>
    public StringSegment Input { get; }

    /// <summary>
    /// Gets the type of the <see cref="ParsedContentType"/>.
    /// </summary>
    /// <example>
    /// For the media type <c>"application/json"</c>, this property gives the value <c>"application"</c>.
    /// </example>
    public StringSegment Type => new StringSegment(Input.Buffer, _typeOffset, _typeLength);

    /// <summary>
    /// Gets whether this <see cref="ParsedContentType"/> matches all types.
    /// </summary>
    public bool MatchesAllTypes => Type.Equals("*", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Gets the subtype of the <see cref="ParsedContentType"/>.
    /// </summary>
    /// <example>
    /// For the media type <c>"application/vnd.example+json"</c>, this property gives the value
    /// <c>"vnd.example+json"</c>.
    /// </example>
    public StringSegment SubType => new StringSegment(Input.Buffer, _subTypeOffset, _subTypeLength);

    /// <summary>
    /// Gets the subtype of the <see cref="ParsedContentType"/>, excluding any structured syntax suffix.
    /// </summary>
    /// <example>
    /// For the media type <c>"application/vnd.example+json"</c>, this property gives the value
    /// <c>"vnd.example"</c>.
    /// </example>
    public StringSegment SubTypeWithoutSuffix
        => new StringSegment(Input.Buffer,
                             _subTypeOffset,
                             _subTypeLength - _subTypeSuffixLength);

    /// <summary>
    /// Gets the structured syntax suffix of the <see cref="ParsedContentType"/> if it has one.
    /// </summary>
    /// <example>
    /// For the media type <c>"application/vnd.example+json"</c>, this property gives the value
    /// <c>"json"</c>.
    /// </example>
    public StringSegment SubTypeSuffix
        => _subTypeSuffixLength > 0
            ? new StringSegment(Input.Buffer,
                                _subTypeOffset + _subTypeLength - (_subTypeSuffixLength - 1),
                                _subTypeSuffixLength - 1)
            : new StringSegment();

    private MediaTypeParameterParser ParameterParser
        => new MediaTypeParameterParser(Input.Buffer,
                                        _parametersOffset,
                                        Input.Offset + Input.Length);

    /// <summary>
    /// Gets whether this <see cref="ParsedContentType"/> matches all subtypes.
    /// </summary>
    /// <example>
    /// For the media type <c>"application/*"</c>, this property is <c>true</c>.
    /// </example>
    /// <example>
    /// For the media type <c>"application/json"</c>, this property is <c>false</c>.
    /// </example>
    public bool MatchesAllSubTypes => SubType.Equals("*", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Gets whether this <see cref="ParsedContentType"/> matches all subtypes, ignoring any structured syntax suffix.
    /// </summary>
    /// <example>
    /// For the media type <c>"application/*+json"</c>, this property is <c>true</c>.
    /// </example>
    /// <example>
    /// For the media type <c>"application/vnd.example+json"</c>, this property is <c>false</c>.
    /// </example>
    public bool MatchesAllSubTypesWithoutSuffix => SubTypeWithoutSuffix.Equals("*", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Gets the <see cref="System.Text.Encoding"/> of the <see cref="ParsedContentType"/> if it has one.
    /// </summary>
    public Encoding? Encoding => GetEncodingFromCharset(GetParameter("charset"));

    /// <summary>
    /// Gets the charset parameter of the <see cref="ParsedContentType"/> if it has one.
    /// </summary>
    public StringSegment Charset => GetParameter("charset");

    /// <summary>
    /// Determines whether the current <see cref="ParsedContentType"/> contains a wildcard.
    /// </summary>
    /// <returns>
    /// <c>true</c> if this <see cref="ParsedContentType"/> contains a wildcard; otherwise <c>false</c>.
    /// </returns>
    public bool HasWildcard
    {
        get
        {
            return MatchesAllTypes ||
                MatchesAllSubTypesWithoutSuffix ||
                GetParameter("*").Equals("*", StringComparison.OrdinalIgnoreCase);
        }
    }

    /// <summary>
    /// Determines whether the current <see cref="ParsedContentType"/> is a subset of the <paramref name="set"/>
    /// <see cref="ParsedContentType"/>.
    /// </summary>
    /// <param name="set">The set <see cref="ParsedContentType"/>.</param>
    /// <returns>
    /// <c>true</c> if this <see cref="ParsedContentType"/> is a subset of <paramref name="set"/>; otherwise <c>false</c>.
    /// </returns>
    public bool IsSubsetOf(ParsedContentType set)
    {
        return MatchesType(set) &&
            MatchesSubtype(set) &&
            ContainsAllParameters(set.ParameterParser);
    }

    /// <summary>
    /// Gets the parameter <paramref name="parameterName"/> of the media type.
    /// </summary>
    /// <param name="parameterName">The name of the parameter to retrieve.</param>
    /// <returns>
    /// The <see cref="StringSegment"/>for the given <paramref name="parameterName"/> if found; otherwise
    /// <c>null</c>.
    /// </returns>
    public StringSegment GetParameter(string parameterName)
    {
        return GetParameter(new StringSegment(parameterName));
    }

    /// <summary>
    /// Gets the parameter <paramref name="parameterName"/> of the media type.
    /// </summary>
    /// <param name="parameterName">The name of the parameter to retrieve.</param>
    /// <returns>
    /// The <see cref="StringSegment"/>for the given <paramref name="parameterName"/> if found; otherwise
    /// <c>null</c>.
    /// </returns>
    public StringSegment GetParameter(StringSegment parameterName)
    {
        var parametersParser = ParameterParser;

        while (parametersParser.ParseNextParameter(out var parameter))
        {
            if (parameter.HasName(parameterName))
            {
                return parameter.Value;
            }
        }

        return new StringSegment();
    }

    /// <summary>
    /// Gets the last parameter <paramref name="parameterName"/> of the media type.
    /// </summary>
    /// <param name="parameterName">The name of the parameter to retrieve.</param>
    /// <param name="parameterValue">The value for the last parameter</param>
    /// <returns>
    /// <see langword="true"/> if parsing succeeded.
    /// </returns>
    public bool TryGetLastParameter(StringSegment parameterName, out StringSegment parameterValue)
    {
        var parametersParser = ParameterParser;

        parameterValue = default;
        while (parametersParser.ParseNextParameter(out var parameter))
        {
            if (parameter.HasName(parameterName))
            {
                parameterValue = parameter.Value;
            }
        }

        return !parametersParser.ParsingFailed;
    }

    private static Encoding? GetEncodingFromCharset(StringSegment charset)
    {
        if (charset.Equals("utf-8", StringComparison.OrdinalIgnoreCase))
        {
            // This is an optimization for utf-8 that prevents the Substring caused by
            // charset.Value
            return Encoding.UTF8;
        }

        try
        {
            // charset.Value might be an invalid encoding name as in charset=invalid.
            // For that reason, we catch the exception thrown by Encoding.GetEncoding
            // and return null instead.
            return charset.HasValue ? Encoding.GetEncoding(charset.Value) : null;
        }
        catch (Exception)
        {
            return null;
        }
    }

    private bool MatchesType(ParsedContentType set)
    {
        return set.MatchesAllTypes ||
            set.Type.Equals(Type, StringComparison.OrdinalIgnoreCase);
    }

    private bool MatchesSubtype(ParsedContentType set)
    {
        if (set.MatchesAllSubTypes)
        {
            return true;
        }

        if (set.SubTypeSuffix.HasValue)
        {
            if (SubTypeSuffix.HasValue)
            {
                // Both the set and the media type being checked have suffixes, so both parts must match.
                return MatchesSubtypeWithoutSuffix(set) && MatchesSubtypeSuffix(set);
            }
            else
            {
                // The set has a suffix, but the media type being checked doesn't. We never consider this to match.
                return false;
            }
        }
        else
        {
            // If this subtype or suffix matches the subtype of the set,
            // it is considered a subtype.
            // Ex: application/json > application/val+json
            return MatchesEitherSubtypeOrSuffix(set);
        }
    }

    private bool MatchesSubtypeWithoutSuffix(ParsedContentType set)
    {
        return set.MatchesAllSubTypesWithoutSuffix ||
            set.SubTypeWithoutSuffix.Equals(SubTypeWithoutSuffix, StringComparison.OrdinalIgnoreCase);
    }

    private bool MatchesSubtypeSuffix(ParsedContentType set)
    {
        // We don't have support for wildcards on suffixes alone (e.g., "application/entity+*")
        // because there's no clear use case for it.
        return set.SubTypeSuffix.Equals(SubTypeSuffix, StringComparison.OrdinalIgnoreCase);
    }

    private bool MatchesEitherSubtypeOrSuffix(ParsedContentType set)
    {
        return set.SubType.Equals(SubType, StringComparison.OrdinalIgnoreCase) ||
            set.SubType.Equals(SubTypeSuffix, StringComparison.OrdinalIgnoreCase);
    }

    private bool ContainsAllParameters(MediaTypeParameterParser setParameters)
    {
        var parameterFound = true;
        while (setParameters.ParseNextParameter(out var setParameter) && parameterFound)
        {
            if (setParameter.HasName("q"))
            {
                // "q" and later parameters are not involved in media type matching. Quoting the RFC: The first
                // "q" parameter (if any) separates the media-range parameter(s) from the accept-params.
                break;
            }

            if (setParameter.HasName("*"))
            {
                // A parameter named "*" has no effect on media type matching, as it is only used as an indication
                // that the entire media type string should be treated as a wildcard.
                continue;
            }

            // Copy the parser as we need to iterate multiple times over it.
            // We can do this because it's a struct
            var subSetParameters = ParameterParser;
            parameterFound = false;
            while (subSetParameters.ParseNextParameter(out var subSetParameter) && !parameterFound)
            {
                parameterFound = subSetParameter.Equals(setParameter);
            }
        }

        return parameterFound;
    }

    private struct MediaTypeParameterParser
    {
        private readonly string _mediaTypeBuffer;
        private readonly int _end;

        public MediaTypeParameterParser(string mediaTypeBuffer, int offset, int end)
        {
            _mediaTypeBuffer = mediaTypeBuffer;
            _end = end;
            CurrentOffset = offset;
            ParsingFailed = false;
        }

        public int CurrentOffset { get; private set; }

        public bool ParsingFailed { get; private set; }

        public bool ParseNextParameter(out MediaTypeParameter result)
        {
            if (_mediaTypeBuffer == null)
            {
                ParsingFailed = true;
                result = default(MediaTypeParameter);
                return false;
            }

            var parameterLength = GetParameterLength(_mediaTypeBuffer, CurrentOffset, _end, out result);
            CurrentOffset += parameterLength;

            if (parameterLength == 0)
            {
                ParsingFailed = (CurrentOffset < _end);
                return false;
            }

            return true;
        }

        private static int GetParameterLength(string input, int startIndex, int endIndex, out MediaTypeParameter parsedValue)
        {
            if (OffsetIsOutOfRange(startIndex, endIndex) || input[startIndex] != ';')
            {
                parsedValue = default(MediaTypeParameter);
                return 0;
            }

            var nameLength = GetNameLength(input, startIndex, endIndex, out var name);

            var current = startIndex + nameLength;

            if (nameLength == 0 || OffsetIsOutOfRange(current, endIndex) || input[current] != '=')
            {
                if (current == endIndex && name.Equals("*", StringComparison.OrdinalIgnoreCase))
                {
                    // As a special case, we allow a trailing ";*" to indicate a wildcard
                    // string allowing any other parameters. It's the same as ";*=*".
                    var asterisk = new StringSegment("*");
                    parsedValue = new MediaTypeParameter(asterisk, asterisk);
                    return current - startIndex;
                }
                else
                {
                    parsedValue = default(MediaTypeParameter);
                    return 0;
                }
            }

            var valueLength = GetValueLength(input, current, endIndex, out var value);

            parsedValue = new MediaTypeParameter(name, value);
            current += valueLength;

            return current - startIndex;
        }

        private static int GetNameLength(string input, int startIndex, int endIndex, out StringSegment name)
        {
            var current = startIndex;

            current++; // skip ';'
            current += HttpTokenParsingRules.GetWhitespaceLength(input, current, endIndex);

            var nameLength = HttpTokenParsingRules.GetTokenLength(input, current, endIndex);
            if (nameLength == 0)
            {
                name = default(StringSegment);
                return 0;
            }

            name = new StringSegment(input, current, nameLength);

            current += nameLength;
            current += HttpTokenParsingRules.GetWhitespaceLength(input, current, endIndex);

            return current - startIndex;
        }

        private static int GetValueLength(string input, int startIndex, int endIndex, out StringSegment value)
        {
            var current = startIndex;

            current++; // skip '='.
            current += HttpTokenParsingRules.GetWhitespaceLength(input, current, endIndex);

            var valueLength = HttpTokenParsingRules.GetTokenLength(input, current, endIndex);

            if (valueLength == 0)
            {
                // A value can either be a token or a quoted string. Check if it is a quoted string.
                var result = HttpTokenParsingRules.GetQuotedStringLength(input, current, endIndex, out valueLength);
                if (result != HttpParseResult.Parsed)
                {
                    // We have an invalid value. Reset the name and return.
                    value = default(StringSegment);
                    return 0;
                }

                // Quotation marks are not part of a quoted parameter value.
                value = new StringSegment(input, current + 1, valueLength - 2);
            }
            else
            {
                value = new StringSegment(input, current, valueLength);
            }

            current += valueLength;
            current += HttpTokenParsingRules.GetWhitespaceLength(input, current, endIndex);

            return current - startIndex;
        }

        private static bool OffsetIsOutOfRange(int offset, int length)
        {
            return offset < 0 || offset >= length;
        }
    }

    private readonly struct MediaTypeParameter : IEquatable<MediaTypeParameter>
    {
        public MediaTypeParameter(StringSegment name, StringSegment value)
        {
            Name = name;
            Value = value;
        }

        public StringSegment Name { get; }

        public StringSegment Value { get; }

        public bool HasName(string name)
        {
            return HasName(new StringSegment(name));
        }

        public bool HasName(StringSegment name)
        {
            return Name.Equals(name, StringComparison.OrdinalIgnoreCase);
        }

        public bool Equals(MediaTypeParameter other)
        {
            return HasName(other.Name) && Value.Equals(other.Value, StringComparison.OrdinalIgnoreCase);
        }

        public override string ToString() => $"{Name}={Value}";
    }
}