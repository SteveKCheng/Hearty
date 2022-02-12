using System;
using System.Buffers;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using Microsoft.Extensions.Primitives;

namespace JobBank.Server
{
    public readonly partial struct ContentFormatInfo
    {
        /// <summary>
        /// Parse the "quality" parameter according to 
        /// the textual representation of an IANA media type (pattern).
        /// </summary>
        /// <param name="spec">
        /// Parsed representation of the media type or type pattern.
        /// </param>
        /// <returns>
        /// The quality value expressed in parts of 1000.
        /// It is parsed from the "q" parameter, following the syntax
        /// of RFC 7231, §5.3.1.
        /// </returns>
        private static int? TryParseQuality(in ReadOnlyMediaTypeHeaderValue spec)
        {
            // qvalue := ( "0" [ "." *3DIGIT ] )
            //        OR ( "1" [ "." *3"0" ] )

            var s = spec.GetParameter("q").AsSpan();
            if (s.Length == 0)
                return null;

            var c = s[0];
            if (c != '1' && c != '0')
                return null;

            if (s.Length == 1)
                return c == '1' ? 1000 : 0;

            if (s[1] != '.' || s.Length > 4)
                return null;

            if (c == '1')
            {
                for (int i = 2; i < s.Length; ++i)
                {
                    if (s[i] != '0')
                        return null;
                }

                return 1000;
            }

            int q = 0;
            for (int i = 2; i <= 4; ++i)
            {
                char d = i < s.Length ? s[i] : '0';
                var v = (int)d - (int)'0';
                if ((uint)v > 9)
                    return null;

                q = (q * 10) + v;
            }

            return q;
        }

        /// <summary>
        /// Score a response format against a request pattern,
        /// for content negotiation.
        /// </summary>
        /// <param name="response">Parsed representation of the
        /// response media type. </param>
        /// <param name="request">Parsed representation of the
        /// request media type pattern. </param>
        /// <param name="responsePreference">
        /// The server's preference for the response format.
        /// From worst to best, the scores associated to the
        /// members of <see cref="ContentPreference" /> are:
        /// 10, 100, 500, 1000.
        /// </param>
        /// <returns>
        /// Zero if the request pattern does not match the response type.
        /// If it does match, then the associated quality values for
        /// the request and response are multiplied together.
        /// This multiplied value is in the range from 0 to 1 million;
        /// higher values indicate more preferable formats.
        /// </returns>
        private static int ScoreFormat(in ReadOnlyMediaTypeHeaderValue response, 
                                       in ReadOnlyMediaTypeHeaderValue request,
                                       ContentPreference responsePreference)
        {
            if (!response.IsSubsetOf(request))
                return 0;

            var requestQuality = TryParseQuality(request) 
                                ?? (request.MatchesAllTypes ? 10 :
                                    request.MatchesAllSubTypes ? 20 :
                                    1000);

            var responseQuality = responsePreference switch
            {
                ContentPreference.Bad => 10,
                ContentPreference.Fair => 100,
                ContentPreference.Good => 500,
                _ => 1000
            };

            return requestQuality * responseQuality;
        }

        /// <summary>
        /// Choose a format from the available responses,
        /// that best matches a set of requested patterns.
        /// </summary>
        /// <typeparam name="TList">
        /// The list type for <paramref name="responses" />; this parameter
        /// is generic only to avoid boxing for a frequent operation.
        /// </typeparam>
        /// <param name="responses">
        /// List of available formats.  If non-empty, the first 
        /// element is the default when the list of patterns
        /// in <paramref name="requests" /> is empty.
        /// </param>
        /// <param name="requests">
        /// The list of IANA media types, or patterns of media types,
        /// accepted by the client.  The strings follow the format 
        /// of the "Accept" header in the HTTP/1.1 specification.
        /// Within each string, there may be multiple patterns
        /// separated by unquoted commas.
        /// If there are no elements, this method simply returns 0, 
        /// referring to the first element of <paramref name="responses" />,
        /// unless that array is empty, in which case this method
        /// returns -1.
        /// </param>
        /// <returns>
        /// The index of the format in <paramref name="responses" />
        /// that best matches the client's requests, or 
        /// -1 if none of the available formats is acceptable
        /// to the client.
        /// </returns>
        /// <remarks>
        /// This function uses an algorithm for content negotiation
        /// simplified from that used by the Apache Web Server:
        /// https://httpd.apache.org/docs/current/en/content-negotiation.html#algorithm.
        /// A pattern from <paramref name="requests" /> matches
        /// <see cref="ContentFormatInfo.MediaType" /> if 
        /// the former has the same type, or is a wildcard pattern
        /// satisfied by the latter type; and the parameters
        /// in the latter have to be all present in the former.
        /// To each pairs of matching response types
        /// and request patterns, a score is assigned by
        /// <see cref="ScoreFormat" />.  The response type
        /// which has the highest score against its 
        /// matching patterns is selected as the best candidate.
        /// </remarks>
        [SkipLocalsInit]
        public static int Negotiate<TList>(TList responses, StringValues requests)
            where TList : IReadOnlyList<ContentFormatInfo>
        {
            int responsesCount = responses.Count;

            // Split up comma-delimited elements in each "line" of requests,
            // without allocating memory for sub-strings or arrays.
            Span<(int,int)> segments = stackalloc (int,int)[64];
            int segmentsCount = 0;
            for (int j = 0; j < requests.Count; ++j)
            {
                // Stores offset+length for each element
                segmentsCount += SplitElements(requests[j], segments[segmentsCount..]);

                // Mark end of this line
                CheckedAppend(segments, (-1, 0), ref segmentsCount);
            }

            if (segmentsCount <= requests.Count)
                return responsesCount > 0 ? 0 : -1;

            int bestScore = 0;
            int bestCandidate = 0;

            for (int i = 0; i < responsesCount; ++i)
            {
                var response = responses[i];
                var parsedResponse = new ReadOnlyMediaTypeHeaderValue(response.MediaType);

                int currentScore = 0;
                int requestsIndex = 0;

                // Loop over each request element after splitting at commas
                for (int k = 0; k < segmentsCount; ++k)
                {
                    var (offset, length) = segments[k];

                    // Marker indicates to go to the next line
                    if (offset < 0)
                    {
                        ++requestsIndex;
                        continue;
                    }

                    var parsedRequest = new ReadOnlyMediaTypeHeaderValue(
                                            requests[requestsIndex], offset, length);
                    int score = ScoreFormat(parsedResponse, parsedRequest, 
                                            response.Preference);
                    currentScore = Math.Max(currentScore, score);
                }

                if (currentScore > bestScore)
                {
                    bestScore = currentScore;
                    bestCandidate = i;
                }
            }

            return bestScore > 0 ? bestCandidate : -1;
        }

        #region Splitting comma-delimited elements in the HTTP Accept header

        /// <summary>
        /// Append to a linear list inside a buffer, checking if the buffer has room first.
        /// </summary>
        /// <typeparam name="T">The type of items to append. </typeparam>
        /// <param name="buffer">Storage for the elements of the list. </param>
        /// <param name="value">The value to append to the list. </param>
        /// <param name="count">
        /// The current count of items in the list. 
        /// On successful return, it is incremented.
        /// </param>
        /// <exception cref="InvalidOperationException">
        /// There is not enough room in the buffer.
        /// </exception>
        private static void CheckedAppend<T>(in Span<T> buffer, T value, ref int count)
        {
            int c = count;
            int i = c++;
            if (i >= buffer.Length)
                throw new InvalidOperationException("The number of elements exceeds implementation-defined limits. ");
            buffer[i] = value;
            count = c;
        }

        /// <summary>
        /// Scan backwards in a string and skip over 
        /// a consecutive run of ASCII whitespace, if any.
        /// </summary>
        private static int SkipWhitespaceBackwards(string line, int index)
        {
            int stop;
            for (stop = index; stop > 0; --stop)
            {
                char c = line[stop - 1];
                bool isWhitespace = (c == '\r' || c == '\n' || c == ' ' || c == '\t');
                if (!isWhitespace)
                    break;
            }

            return stop;
        }

        /// <summary>
        /// Scan forward in a string and skip over 
        /// a consecutive run of ASCII whitespace, if any.
        /// </summary>
        private static int SkipWhitespaceForwards(string line, int index)
        {
            int stop;
            for (stop = index; index < line.Length; ++stop)
            {
                char c = line[stop];
                bool isWhitespace = (c == '\r' || c == '\n' || c == ' ' || c == '\t');
                if (!isWhitespace)
                    break;
            }

            return stop;
        }

        /// <summary>
        /// Scan forward for the first ending double-quote character 
        /// that has not been escaped, and skip past it.
        /// </summary>
        private static int SkipPastEndQuote(in StringSegment line, int start)
        {
            int stop;

            for (stop = start; stop < line.Length; ++stop)
            {
                char c = line[stop];
                if (c == '"')
                {
                    ++stop;
                    break;
                }

                if (c == '\\')
                {
                    ++stop;
                    if (stop >= line.Length)
                        break;
                }
            }

            return stop;
        }

        /// <summary>
        /// Find the next comma in a string that would terminate
        /// the current comma-delimited element, if any.
        /// </summary>
        private static int SplitAtFirstComma(string line, int start)
        {
            while (true)
            {
                int comma = line.IndexOf(',', start);
                if (comma < 0)
                    return -1;

                int quote = line.IndexOf('"', start);
                if (quote < 0 || quote > comma)
                    return comma;

                start = SkipPastEndQuote(line, quote + 1);
            }
        }

        /// <summary>
        /// Determine all the comma-delimited elements in a HTTP header line,
        /// trimming empty elements and whitespace.
        /// </summary>
        /// <param name="line">
        /// The string value in a HTTP header line.
        /// </param>
        /// <param name="segments">
        /// A buffer which holds the specification of sub-strings for
        /// each of the comma-delimited elements found.  This function
        /// will write to this buffer starting from the beginning,
        /// where each element in the buffer is the tuple (offset, length)
        /// where offset is the index in <paramref name="line" />
        /// where the sub-string starts, and length is the length of
        /// that sub-string.
        /// </param>
        /// <returns>
        /// The number of comma-delimited elements found and recorded
        /// into <paramref name="segments" />.
        /// </returns>
        private static int SplitElements(string line, in Span<(int, int)> segments)
        {
            int count = 0;
            int start = 0;

            do
            {
                int end = SplitAtFirstComma(line, start);
                if (end < 0)
                    end = line.Length;

                int left = SkipWhitespaceForwards(line, start);
                int right = SkipWhitespaceBackwards(line, end);
                if (left < right)
                    CheckedAppend(segments, (left, right - left), ref count);

                start = end + 1;
            } while (start < line.Length);

            return count;
        }

        #endregion
    }
}
