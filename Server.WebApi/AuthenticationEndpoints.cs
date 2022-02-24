using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.IdentityModel.Tokens.Jwt;
using System.Linq;
using System.Security.Claims;
using System.Security.Cryptography;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Cryptography.KeyDerivation;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;

namespace JobBank.Server;

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

/// <summary>
/// Sets up endpoints for programmatic retrieval of credentials.
/// </summary>
/// <remarks>
/// These endpoints have nothing to do with the functionality of Job Bank,
/// but they are useful building blocks for a Job Bank server host.
/// </remarks>
public static class AuthenticationEndpoints
{
    /// <summary>
    /// Derive a key for symmetric encryption from a text string.
    /// </summary>
    /// <param name="password">
    /// The password or passphrase.
    /// </param>
    /// <returns>
    /// 256-bit key derived from running the PBKDF2 algorithm on
    /// <paramref name="password" />.
    /// </returns>
    private static SymmetricSecurityKey CreateSecurityKeyFromString(string password)
    {
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

    private static IReadOnlyList<Claim> CreateClaims(HttpContext httpContext)
    {
        var user = httpContext.User.FindFirstValue(ClaimTypes.NameIdentifier);

        var claims = new Claim[]
        {
            new("user", string.IsNullOrWhiteSpace(user) ? "default" : user)
        };

        return claims;
    }

    /// <summary>
    /// Add an endpoint to get a bearer token for authenticating
    /// other API endpoints.
    /// </summary>
    /// <param name="endpoints">
    /// Endpoint route builder from the ASP.NET Core framework.
    /// </param>
    /// <param name="path">
    /// The path for the endpoint.  If null, defaults to "/auth/token".
    /// </param>
    /// <param name="authenticationScheme">
    /// The name of the authentication scheme in ASP.NET Core.
    /// The scheme must refer to JSON Web Token authentication, 
    /// with the required settings for self-issued tokens.
    /// If this argument is null, it defaults to 
    /// <see cref="JwtBearerDefaults.AuthenticationScheme" />.
    /// </param>
    /// <remarks>
    /// The token is in the format of a JSON Web Token in its compact
    /// serialization, but it may be considered an implementation
    /// detail.  The token is an opaque string to clients.
    /// The JSON Web Token is self-issued so nothing else than this
    /// server needs to parse it.
    /// </remarks>
    /// <returns>Builder specific to the new job executor's endpoint that may be
    /// used to customize its handling by the ASP.NET Core framework.
    /// In particular, this endpoint may be set to require authorization,
    /// via some other authentication scheme, to determine the user
    /// identity that the cookie should be issued for.
    /// </returns>
    public static IEndpointConventionBuilder
        MapAuthTokenRetrieval(this IEndpointRouteBuilder endpoints,
                              string? path = null,
                              string? authenticationScheme = null)
    {
        path ??= "/auth/token";
        authenticationScheme ??= JwtBearerDefaults.AuthenticationScheme;

        var jwtOptions = endpoints.ServiceProvider
                                  .GetRequiredService<IOptionsMonitor<JwtBearerOptions>>()
                                  .Get(authenticationScheme);
        var audience = jwtOptions.Audience;

        if (string.IsNullOrEmpty(audience))
        {
            throw new InvalidOperationException(
                "JwtBearerOptions.Audience is required for the endpoint " +
                "to retrieve the authentication token, but has not been set. ");
        }

        var validationParameters = jwtOptions.TokenValidationParameters;
        var signingKey = validationParameters.IssuerSigningKey;
        var issuer = validationParameters.ValidIssuer;

        if (signingKey is not SymmetricSecurityKey)
        {
            throw new InvalidOperationException(
                "TokenValidationParameters.IssuerSigningKey is required for the endpoint " +
                "to retrieve the authentication token, but has not been set to a key " +
                "for symmetric encryption. ");
        }

        if (string.IsNullOrEmpty(issuer))
        {
            throw new InvalidOperationException(
                "TokenValidationParameters.ValidIssuer is required for the endpoint " +
                "to retrieve the authentication token, but has not been set. ");
        }

        var tokenHandler = new JwtSecurityTokenHandler();
        var credentials = new SigningCredentials(
                            key: signingKey,
                            algorithm: SecurityAlgorithms.HmacSha256);

        return endpoints.MapMethods(path, new[] { HttpMethods.Get, HttpMethods.Head }, async httpContext =>
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

            if (IsHeadRequest(httpRequest))
            {
                httpResponse.StatusCode = StatusCodes.Status200OK;
                httpResponse.ContentType = _contentFormatInfo[(int)format].MediaType;
                httpResponse.Headers.CacheControl = "private";
                httpResponse.Headers.Vary = "Accept";
                return;
            }

            var claims = CreateClaims(httpContext);

            var now = DateTime.UtcNow;
            var token = new JwtSecurityToken(
                    issuer,
                    audience,
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

    private static bool IsHeadRequest(HttpRequest httpRequest)
        => string.Equals(httpRequest.Method,
                         HttpMethods.Head,
                         StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Add an endpoint to set an authentication cookie 
    /// on the connecting HTTP client.
    /// </summary>
    /// <param name="endpoints">
    /// Endpoint route builder from the ASP.NET Core framework.
    /// </param>
    /// <param name="path">
    /// The path for the endpoint.  If null, defaults to "/auth/cookie".
    /// </param>
    /// <param name="authenticationScheme">
    /// The name of the authentication scheme in ASP.NET Core.
    /// The scheme must refer to cookie authentication, with whatever
    /// desired options.  If null, defaults to 
    /// <see cref="CookieAuthenticationDefaults.AuthenticationScheme" />.
    /// </param>
    /// <returns>Builder specific to the new job executor's endpoint that may be
    /// used to customize its handling by the ASP.NET Core framework.
    /// In particular, this endpoint may be set to require authorization,
    /// via some other authentication scheme, to determine the user
    /// identity that the cookie should be issued for.
    /// </returns>
    public static IEndpointConventionBuilder
        MapAuthCookieRetrieval(this IEndpointRouteBuilder endpoints, 
                               string? path = null,
                               string? authenticationScheme = null)
    {
        path ??= "/auth/cookie";
        authenticationScheme ??= CookieAuthenticationDefaults.AuthenticationScheme;

        return endpoints.MapMethods("/auth/cookie", new[] { HttpMethods.Get, HttpMethods.Head }, async httpContext =>
        {
            var httpRequest = httpContext.Request;
            var httpResponse = httpContext.Response;

            var isHead = IsHeadRequest(httpRequest);

            httpResponse.StatusCode = isHead ? StatusCodes.Status200OK
                                             : StatusCodes.Status204NoContent;
            httpResponse.Headers.CacheControl = "private";

            if (isHead)
                return;

            var claims = CreateClaims(httpContext);

            var claimsIdentity = new ClaimsIdentity(claims, 
                                                    authenticationScheme);

            var principal = new ClaimsPrincipal(claimsIdentity);

            await httpContext.SignInAsync(authenticationScheme,
                                          principal).ConfigureAwait(false);
        });
    }

    /// <summary>
    /// Configure the web server host to accept JSON Web Tokens served by itself.
    /// </summary>
    /// <param name="authBuilder">
    /// Builder for authentication schemes in ASP.NET Core.
    /// </param>
    /// <param name="passphrase">
    /// Passphrase used to generate the secret key that the JSON Web Tokens
    /// will be signed with.  If null, the secret key will be randomly
    /// generated, and it will not be persisted beyond the lifetime of the
    /// web server host.
    /// </param>
    /// <param name="siteUrl">
    /// URL of the site to identify the self-generated tokens with.
    /// If null, the URL is automatically determined from the web host.
    /// </param>
    /// <param name="authenticationScheme">
    /// The name ASP.NET Core to register the new authentication scheme as.
    /// If this argument is null, it defaults to 
    /// <see cref="JwtBearerDefaults.AuthenticationScheme" />.
    /// </param>
    /// <returns></returns>
    public static AuthenticationBuilder 
        AddSelfIssuedJwtBearer(this AuthenticationBuilder authBuilder,
                               string? passphrase = null,
                               string? siteUrl = null,
                               string? authenticationScheme = null)
    {
        //siteUrl ??= "http://localhost/";
        authenticationScheme ??= JwtBearerDefaults.AuthenticationScheme;

        SecurityKey signingKey;
        if (string.IsNullOrEmpty(passphrase))
            signingKey = new SymmetricSecurityKey(RandomNumberGenerator.GetBytes(256 / 8));
        else
            signingKey = CreateSecurityKeyFromString(passphrase);

        if (siteUrl is null)
        {
            // Need dependency injection to get IServer to determine server
            // address if the caller did not specify it. 
            //
            // Unfortunately neither authBuilder.AddJwtBearer nor
            // authBuilder.AddScheme support injecting a dependency to
            // initialize JwtBearerOptions even though IServiceCollection
            // supports such functionality, so we have to re-implement
            // authBuilder.AddJwtBearer here.

            authBuilder.Services
                       .TryAddEnumerable(
                            ServiceDescriptor.Singleton<IPostConfigureOptions<JwtBearerOptions>, 
                                                        JwtBearerPostConfigureOptions>());

            authBuilder.AddSchemeWithDependency(
                authenticationScheme,
                "Self-issued JSON Web Token",
                typeof(JwtBearerHandler),
                (JwtBearerOptions options, IServer server) =>
                {
                    var actualSiteUrl = server.Features
                                              .Get<IServerAddressesFeature>()
                                              ?.Addresses
                                              .FirstOrDefault()
                                              ?? "http://localhost/";

                    SetJwtBearerOptions(options, signingKey, actualSiteUrl);
                });
        }
        else
        {
            authBuilder.AddJwtBearer(authenticationScheme, options =>
            {
                SetJwtBearerOptions(options, signingKey, siteUrl);
            });
        }

        return authBuilder;
    }

    private static void SetJwtBearerOptions(JwtBearerOptions options, SecurityKey signingKey, string siteUrl)
    {
        var p = options.TokenValidationParameters;
        p.IssuerSigningKey = signingKey;
        p.ValidateIssuerSigningKey = true;
        p.ValidateIssuer = true;
        p.ValidIssuer = siteUrl;
        options.Audience = siteUrl;
        options.RequireHttpsMetadata = false;
    }

    private static void
        AddSchemeWithDependency<TOptions, TService>
            (
                this AuthenticationBuilder authBuilder, 
                string authenticationScheme, 
                string? displayName,
                [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors)] Type handlerType,
                Action<TOptions, TService> configureOptions
            )
            where TOptions : AuthenticationSchemeOptions, new()
            where TService : class
    {
        IServiceCollection services = authBuilder.Services;

        services.Configure<AuthenticationOptions>(o =>
        {
            o.AddScheme(authenticationScheme, scheme =>
            {
                scheme.HandlerType = handlerType;
                scheme.DisplayName = displayName;
            });
        });

        services.AddOptions<TOptions>(authenticationScheme)
                .Validate(o =>
                {
                    o.Validate(authenticationScheme);
                    return true;
                })
                .Configure<TService>(configureOptions);

        services.AddTransient(handlerType);
    }
}
