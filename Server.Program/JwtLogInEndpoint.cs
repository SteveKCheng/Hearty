using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Cryptography.KeyDerivation;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.Tokens;
using System;
using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;

namespace JobBank.Server.Program;

/// <summary>
/// Settings for a web server to issue its own JSON Web Tokens,
/// intended to be accessible from <see cref="IConfiguration" />.
/// </summary>
public class JwtSiteConfiguration
{
    /// <summary>
    /// URL used for both the "audience" and "issuer" in the
    /// self-issued JSON Web Token.
    /// </summary>
    public string SiteUrl { get; set; } = "http://localhost/";

    /// <summary>
    /// Secret string key used to encrypt JSON Web Tokens to prevent forgery.
    /// </summary>
    /// <remarks>
    /// This string is passed through the PBKDF2+HMACSHA256 algorithm to derive
    /// the actual 256-bit key in the HMAC algorithm to create the JSON
    /// Web Token.
    /// </remarks>
    public string SigningKey { get; set; } = string.Empty;
}

public static class JwtLogInEndpoint
{
    internal static SymmetricSecurityKey CreateSecurityKeyFromString(string? password)
    {
        if (string.IsNullOrEmpty(password))
            password = "Unset password";

        byte[] bytes = KeyDerivation.Pbkdf2(password,
                                            Array.Empty<byte>(),
                                            KeyDerivationPrf.HMACSHA256,
                                            iterationCount: 16,
                                            numBytesRequested: 256 / 8);
        return new SymmetricSecurityKey(bytes);
    }

    /// <summary>
    /// Supports content negotiation for the endpoint returning the JSON Web Token.
    /// </summary>
    private static readonly ContentFormatInfo[] _contentFormatInfo = new ContentFormatInfo[]
    {
        new(mediaType: "application/jwt", ContentPreference.Best),
        new(mediaType: "application/json", ContentPreference.Good),
        new(mediaType: "text/plain", ContentPreference.Fair),
        new(mediaType: "application/xhtml+xml", ContentPreference.Fair)
    };

    /// <summary>
    /// The variant formats that JSON Web Token could be presented in.
    /// </summary>
    private enum JwtContentFormat
    {
        /// <summary>
        /// Client requested a format that is not supported by the server.
        /// </summary>
        Unsupported = -1,

        /// <summary>
        /// The compact text serialization of the JSON Web Token,
        /// presented as "application/jwt".
        /// </summary>
        Jwt = 0,

        /// <summary>
        /// JSON wrapper around the compact text serialization JSON Web Token,
        /// for clients that cannot decode "application/jwt" directly.
        /// </summary>
        Json = 1,

        /// <summary>
        /// The compact text serialization of the JSON Web Token,
        /// presented as "text/plain".
        /// </summary>
        Text = 2,

        /// <summary>
        /// JSON Web Token wrapped in XHTML, for compatibility with Web browsers
        /// </summary>
        XHtml = 3,
    }

    public static IEndpointConventionBuilder
        MapJwtLogin(this IEndpointRouteBuilder endpoints)
    {
        var siteConfig = endpoints.ServiceProvider
                                  .GetRequiredService<IConfiguration>()
                                  .GetRequiredSection("JsonWebToken")
                                  .Get<JwtSiteConfiguration>();

        var tokenHandler = new JwtSecurityTokenHandler();
        var credentials = new SigningCredentials(
                            key: CreateSecurityKeyFromString(siteConfig.SigningKey),
                            algorithm: SecurityAlgorithms.HmacSha256);

        var siteUrl = siteConfig.SiteUrl;

        return endpoints.MapMethods("/jwt", new[] { HttpMethods.Get, HttpMethods.Head }, async httpContext =>
        {
            var httpRequest = httpContext.Request;
            var httpResponse = httpContext.Response;

            var format = (JwtContentFormat)ContentFormatInfo.Negotiate(_contentFormatInfo,
                                                                       httpRequest.Headers.Accept);
            if (format == JwtContentFormat.Unsupported)
            {
                httpResponse.StatusCode = StatusCodes.Status406NotAcceptable;
                httpResponse.Headers.CacheControl = "private";
                httpResponse.Headers.Vary = "Accept";
                return;
            }

            if (string.Equals(httpRequest.Method, HttpMethods.Head, StringComparison.OrdinalIgnoreCase))
            {
                httpResponse.StatusCode = StatusCodes.Status200OK;
                httpResponse.ContentType = _contentFormatInfo[(int)format].MediaType;
                httpResponse.Headers.CacheControl = "private";
                httpResponse.Headers.Vary = "Accept";
                return;
            }

            var user = httpContext.User.FindFirstValue(ClaimTypes.NameIdentifier);

            var claims = new Claim[]
            {
                new("user", string.IsNullOrWhiteSpace(user) ? "default" : user)
            };

            var now = DateTime.UtcNow;
            var token = new JwtSecurityToken(
                    issuer: siteUrl,
                    audience: siteUrl,
                    notBefore: now,
                    expires: now.AddYears(1),
                    claims: claims,
                    signingCredentials: credentials);

            // N.B. The compact serialization of JSON Web Tokens use
            //      Base64URL encoding so it needs no escaping here.
            //      It uses only the characters: A-Z, a-z, 0-9, - and _
            var tokenString = tokenHandler.WriteToken(token);

            httpResponse.StatusCode = StatusCodes.Status200OK;
            httpResponse.Headers.CacheControl = "private";

            httpResponse.ContentType = _contentFormatInfo[(int)format].MediaType;

            switch (format)
            {
                case JwtContentFormat.Jwt:
                case JwtContentFormat.Text:
                    httpResponse.ContentLength = tokenString.Length;
                    httpResponse.BodyWriter.WriteUtf8String(tokenString);
                    break;

                case JwtContentFormat.Json:
                    string jsonPrefix = @"{ ""token"": """;
                    string jsonSuffix = @""" }";
                    httpResponse.ContentLength = jsonPrefix.Length + tokenString.Length + jsonSuffix.Length;
                    httpResponse.BodyWriter.WriteUtf8String(jsonPrefix);
                    httpResponse.BodyWriter.WriteUtf8String(tokenString);
                    httpResponse.BodyWriter.WriteUtf8String(jsonSuffix);
                    break;

                case JwtContentFormat.XHtml:
                    string htmlPrefix = @"<?xml version=""1.0"" encoding=""utf-8"" ?>
<!DOCTYPE html PUBLIC ""-//W3C//DTD XHTML 1.0 Strict//EN"" ""http://www.w3.org/TR/xhtml1/DTD/xhtml1-strict.dtd"" >
<html xmlns=""http://www.w3.org/1999/xhtml"">
  <head>
    <title>JSON Web Token</title>
  </head>
  <body>
    <p><samp style=""word-wrap: break-word"">
";
                    string htmlSuffix = @"
    </samp></p>
  </body>
</html>
";
                    httpResponse.ContentLength = htmlPrefix.Length + tokenString.Length + htmlSuffix.Length;
                    httpResponse.BodyWriter.WriteUtf8String(htmlPrefix);
                    httpResponse.BodyWriter.WriteUtf8String(tokenString);
                    httpResponse.BodyWriter.WriteUtf8String(htmlSuffix);
                    break;
            }

            await httpResponse.BodyWriter.CompleteAsync().ConfigureAwait(false);
        });
    }
}
