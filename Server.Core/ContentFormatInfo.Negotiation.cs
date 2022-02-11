using System;
using System.Collections.Generic;
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
        /// List of available formats.  There must be at least one,
        /// and the first must be the default if <paramref name="requests" />
        /// is empty.
        /// </param>
        /// <param name="requests">
        /// The list of IANA media types, or patterns of media types,
        /// accepted by the client.  The strings follow the format 
        /// of the "Accept" header in the HTTP/1.1 specification.
        /// If empty, this method simply returns 0, referring
        /// to the first element of <paramref name="responses" />.
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

            if (requests.Count == 0)
                return 0;

            int bestScore = 0;
            int bestCandidate = 0;

            for (int i = 0; i < responsesCount; ++i)
            {
                var response = responses[i];
                var parsedResponse = new ReadOnlyMediaTypeHeaderValue(response.MediaType);

                int currentScore = 0;
                for (int j = 0; j < requests.Count; ++j)
                {
                    var parsedRequest = new ReadOnlyMediaTypeHeaderValue(requests[j]);
                    int score = ScoreFormat(parsedResponse, parsedRequest, response.Preference);
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
    }
}
