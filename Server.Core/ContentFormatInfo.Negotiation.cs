using System;
using System.Collections.Generic;
using Microsoft.Extensions.Primitives;

namespace Hearty.Server
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
                ContentPreference.Bad => 850,
                ContentPreference.Fair => 900,
                ContentPreference.Good => 950,
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
        public static int Negotiate<TList>(TList responses, StringValues requests)
            where TList : IReadOnlyList<ContentFormatInfo>
        {
            int responsesCount = responses.Count;
            if (responsesCount == 0)
                return -1;

            //
            // It is more natural to process the responses in the outer
            // loop, and the requests in the inner loop.  However, 
            // parsing the requests is complicated and mildly expensive,
            // so we put them in the outer loop instead, and the responses
            // are processed in the inner loop.
            //

            bool hasRequest = false;
            int bestScore = 0;
            int bestCandidate = 0;

            // Loop over each request line
            for (int j = 0; j < requests.Count; ++j)
            {
                StringSegment line = requests[j];
                StringSegment request;

                // Loop over comma-delimited elements in the request line
                while ((request = TryReadNextElement(ref line)).Length > 0)
                {
                    hasRequest = true;
                    var parsedRequest = new ReadOnlyMediaTypeHeaderValue(request);

                    // Loop over available responses to compute a response
                    // candidate matching the current request pattern with
                    // the highest score.
                    int requestScore = 0;
                    int candidate = 0;
                    for (int i = 0; i < responsesCount; ++i)
                    {
                        var response = responses[i];
                        var parsedResponse = new ReadOnlyMediaTypeHeaderValue(response.MediaType);

                        int score = ScoreFormat(parsedResponse, parsedRequest,
                                                response.Preference);

                        // Argument max
                        if (score > requestScore)
                        {
                            requestScore = score;
                            candidate = i;
                        }
                    }

                    // Update best score over the request patterns seen so far.
                    // In case of ties, choose the candidate that occurs first
                    // in the list of responses.
                    if (requestScore > bestScore ||
                        requestScore == bestScore && candidate < bestCandidate)
                    {
                        bestScore = requestScore;
                        bestCandidate = candidate;
                    }
                }
            }

            return (bestScore > 0 || !hasRequest) ? bestCandidate : -1;
        }

        #region Splitting comma-delimited elements in the HTTP Accept header

        /// <summary>
        /// Scan backwards in a string and skip over 
        /// a consecutive run of ASCII whitespace, if any.
        /// </summary>
        private static int SkipWhitespaceBackwards(StringSegment line, int index)
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
        private static int SkipWhitespaceForwards(StringSegment line, int index)
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
        private static int FindNextComma(StringSegment line)
        {
            int start = 0;

            while (true)
            {
                int comma = line.IndexOf(',', start);
                if (comma < 0)
                    return -1;

                int quote = line.IndexOf('"', start, comma - start);
                if (quote < 0)
                    return comma;

                start = SkipPastEndQuote(line, quote + 1);
            }
        }

        /// <summary>
        /// Read the next element in a HTTP header line, that is
        /// terminated by a unquoted comma, trimming empty elements
        /// and whitespace.
        /// </summary>
        /// <param name="input">
        /// The string to parse for the next comma-delimited element.
        /// On return, it will be modified to point to the rest
        /// of the line after the comma, if found.
        /// </param>
        /// <returns>
        /// The next element in the HTTP header line,
        /// or the empty string if there is no next element.
        /// </returns>
        private static StringSegment TryReadNextElement(ref StringSegment input)
        {
            var line = input;

            while (line.Length > 0)
            {
                int split = FindNextComma(line);
                if (split < 0)
                    split = line.Length;

                int left = SkipWhitespaceForwards(line, 0);
                int right = SkipWhitespaceBackwards(line, split);

                ++split;
                var current = line;
                input = line = (split < line.Length) ? line.Substring(split)
                                                     : StringSegment.Empty;

                if (left < right)
                    return current.Substring(left, right - left);
            }

            return StringSegment.Empty;
        }

        #endregion
    }
}
