using System;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Xml;
using static System.FormattableString;

namespace Hearty.Common;

public static class RestApiUtilities
{
    public static bool TryParseTimespan(string input, out TimeSpan result)
    {
        try
        {
            if (!string.IsNullOrEmpty(input))
            {
                result = XmlConvert.ToTimeSpan(input);
                return true;
            }
        }
        catch (FormatException)
        {
        }

        result = TimeSpan.Zero;
        return false;
    }

    public static string FormatExpiry(TimeSpan value)
    {
        return Invariant($"P{value.Days}DT{value.Hours}H{value.Minutes}M{value.Seconds}.{value.Milliseconds:D3}S");
    }

    public static bool TryFormatExpiry(TimeSpan value, Span<char> destination, out int charsWritten)
    {
        return destination.TryWrite(
                    null, 
                    $"P{value.Days}DT{value.Hours}H{value.Minutes}M{value.Seconds}.{value.Milliseconds:D3}S", 
                    out charsWritten);
    }

    /// <summary>
    /// Read a named HTTP header, assuming it can only have
    /// zero or one value.
    /// </summary>
    /// <param name="headers">
    /// The collection of HTTP headers, typically in a response
    /// from <see cref="HttpClient" />.
    /// </param>
    /// <param name="name">
    /// The name of the HTTP header.
    /// </param>
    /// <returns>
    /// The single value for the HTTP header, if it exists.
    /// </returns>
    public static string? TryGetSingleValue(this HttpHeaders headers, string name)
    {
        if (!headers.TryGetValues(name, out var values))
            return null;

        var value = (values is string[] array) ? array[0]
                                               : values.First();

        return value;
    }

    /// <summary>
    /// Read a named HTTP header, assuming it can only have
    /// exactly one value.
    /// </summary>
    /// <param name="headers">
    /// The collection of HTTP headers, typically in a response
    /// from <see cref="HttpClient" />.
    /// </param>
    /// <param name="name">
    /// The name of the HTTP header.
    /// </param>
    /// <returns>
    /// The single value for the HTTP header.
    /// </returns>
    public static string GetSingleValue(this HttpHeaders headers, string name)
    {
        if (headers.TryGetValues(name, out var values))
        {
            if (values is string[] array && array.Length == 1)
                return array[0];

            string? value = null;
            foreach (var item in values)
            {
                if (value is not null)
                {
                    value = null;
                    break;
                }

                value = item;
            }

            if (value is not null)
                return value;
        }

        throw new InvalidDataException($"The HTTP response does not have a single value for the header '{name}' as expected. ");
    }

    /// <summary>
    /// Check that a content type is "multipart/parallel",
    /// and extract its boundary parameter.
    /// </summary>
    /// <param name="contentType">
    /// Value of the Content-Type header in a HTTP server's response.
    /// </param>
    /// <returns>
    /// The boundary string that separates the segments in
    /// the multi-part payload.
    /// </returns>
    /// <exception cref="InvalidDataException">
    /// The content type is not what is expected.
    /// </exception>
    public static string GetMultipartBoundary(string? contentType)
    {
        if (contentType is not null)
        {
            var parsed = new ParsedContentType(contentType);
            if (parsed.IsSubsetOf(new ParsedContentType("multipart/parallel")))
            {
                var b = parsed.GetParameter("boundary");
                if (b.HasValue)
                    return b.Value;
            }
        }

        throw new InvalidDataException("The Content-Type header returned in the response is unexpected. ");
    }
}
