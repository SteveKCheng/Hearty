using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.IdentityModel.Tokens.Jwt;
using System.Linq;
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
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;

namespace Hearty.Server;

/// <summary>
/// Sets up endpoints for programmatic retrieval of credentials.
/// </summary>
/// <remarks>
/// These endpoints have nothing to do with the functionality of Hearty,
/// but they are useful building blocks for a Hearty server host.
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
        new(mediaType: "application/xhtml+xml", ContentPreference.Fair),
        new(mediaType: "application/jwt+json", ContentPreference.Fair)
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

        /// <summary>
        /// The payload to be encoded by the JSON Web Token, serialized to JSON.
        /// </summary>
        /// <remarks>
        /// This format is used for debugging only.  It does not encrypt
        /// the token, and the result cannot be used to authenticate.
        /// </remarks>
        JwtJson = 4,
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
    /// <para>
    /// The token is in the format of a JSON Web Token in its compact
    /// serialization, but it may be considered an implementation
    /// detail.  The token is an opaque string to clients.
    /// The JSON Web Token is self-issued so nothing else than this
    /// server needs to parse it.
    /// </para>
    /// <para>
    /// The JSON Web Token captures the claims present in 
    /// <see cref="HttpContext.User" />, so that should be set as
    /// desired, through another authentication scheme or middleware
    /// in ASP.NET Core that executes before this endpoint is routed to.
    /// </para>
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

        // Use the same token handler registered in jwtOptions 
        // if it is of the right type, to avoid inconsistencies
        // in how we generate the token versus how authentication
        // in ASP.NET Core would validate it.
        static JwtSecurityTokenHandler? ExtractJwtSecurityTokenHandler(IList<ISecurityTokenValidator> validators)
        {
            foreach (var validator in validators)
            {
                if (validator is JwtSecurityTokenHandler jwtTokenHandler)
                    return jwtTokenHandler;
            }

            return null;
        }
        var tokenHandler = ExtractJwtSecurityTokenHandler(jwtOptions.SecurityTokenValidators)
                            ?? new JwtSecurityTokenHandler();

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
                httpResponse.ContentType = _contentFormatInfo[(int)format].MediaType.ToString();
                httpResponse.Headers.CacheControl = "private";
                httpResponse.Headers.Vary = "Accept";
                return;
            }

            var now = DateTime.UtcNow;

            // The factory method must be used instead of the constructor
            // of JwtSecurityToken because it maps the .NET names of
            // the claims in ClaimsPrincipal to the names normally
            // used in JSON Web Tokens, according to the OpenID standard:
            // see https://openid.net/specs/openid-connect-core-1_0.html#StandardClaims.
            //
            // Also: the .NET names are URLs which makes the resulting
            // JSON Web Token payload bloated, if not mapped to the shorter
            // names everybody else uses.
            //
            // See also: “ASP.NET Core and JSON Web Tokens - where are my claims?”
            // https://mderriey.com/2019/06/23/where-are-my-jwt-claims/.
            //
            // Of particular note: ClaimTypes.NameIdentifier from .NET maps to
            // JwtRegisteredClaimNames.NameId!  The mapping can be found in:
            // https://github.com/AzureAD/azure-activedirectory-identitymodel-extensions-for-dotnet/blob/b5b7ed8fb8ce513469b51b87c5f76314783b74e3/src/System.IdentityModel.Tokens.Jwt/ClaimTypeMapping.cs
            JwtSecurityToken tokenData = tokenHandler.CreateJwtSecurityToken(
                                issuer,
                                audience,
                                notBefore: now,
                                expires: now.AddYears(1),
                                subject: httpContext.User.Identities.FirstOrDefault(),
                                signingCredentials: credentials);

            // N.B. The compact serialization of JSON Web Tokens use
            //      Base64URL encoding so it needs no escaping here.
            //      It uses only the characters: A-Z, a-z, 0-9, - and _
            var tokenString = tokenHandler.WriteToken(tokenData);

            httpResponse.StatusCode = StatusCodes.Status200OK;
            httpResponse.Headers.CacheControl = "private";

            httpResponse.ContentType = _contentFormatInfo[(int)format].MediaType.ToString();

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

                case JwtContentFormat.JwtJson:
                    var tokenDataSerialized = tokenData.Payload.SerializeToJson();
                    httpResponse.ContentLength = tokenDataSerialized.Length;
                    httpResponse.BodyWriter.WriteUtf8String(tokenDataSerialized);
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
    /// <para>
    /// The authentication cookie captures the claims present in 
    /// <see cref="HttpContext.User" />, so that should be set as
    /// desired, through another authentication scheme or middleware
    /// in ASP.NET Core that executes before this endpoint is routed to.
    /// </para>
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

            await httpContext.SignInAsync(authenticationScheme,
                                          httpContext.User).ConfigureAwait(false);
        });
    }

    private static bool IsAbsoluteSiteUrl(string siteUrl)
    {
        if (Uri.IsWellFormedUriString(siteUrl, UriKind.Absolute))
            return true;

        if (Uri.IsWellFormedUriString(siteUrl, UriKind.Relative))
            return false;

        throw new ArgumentException("Supplied URL is neither absolute nor relative. ", nameof(siteUrl));
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
    /// If the URL is relative, it will be appended to the URL from
    /// the web host.
    /// </param>
    /// <param name="authenticationScheme">
    /// The name ASP.NET Core to register the new authentication scheme as.
    /// If this argument is null, it defaults to 
    /// <see cref="JwtBearerDefaults.AuthenticationScheme" />.
    /// </param>
    /// <param name="displayName">
    /// The displayed name or title to the new authentication scheme to 
    /// register.  If null, this method supplies a default.
    /// </param>
    /// <returns>
    /// Returns back <paramref name="authBuilder" />.
    /// </returns>
    public static AuthenticationBuilder 
        AddSelfIssuedJwtBearer(this AuthenticationBuilder authBuilder,
                               string? passphrase = null,
                               string? siteUrl = null,
                               string? displayName = null,
                               string? authenticationScheme = null)
    {
        authenticationScheme ??= JwtBearerDefaults.AuthenticationScheme;
        displayName ??= "Self-issued JSON Web Tokens";

        SecurityKey signingKey;
        if (string.IsNullOrEmpty(passphrase))
            signingKey = new SymmetricSecurityKey(RandomNumberGenerator.GetBytes(256 / 8));
        else
            signingKey = CreateSecurityKeyFromString(passphrase);

        string? relativeUrl = (siteUrl is not null) && !IsAbsoluteSiteUrl(siteUrl)
                                ? siteUrl
                                : null;

        if (siteUrl is null || relativeUrl is not null)
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
                displayName,
                typeof(JwtBearerHandler),
                (JwtBearerOptions options, IServer server) =>
                {
                    var actualSiteUrl = server.Features
                                              .Get<IServerAddressesFeature>()
                                              ?.Addresses
                                              .FirstOrDefault()
                                              ?? "http://localhost/";

                    if (relativeUrl is not null)
                    {
                        bool needSlash = !actualSiteUrl.EndsWith('/') && !relativeUrl.StartsWith('/');
                        actualSiteUrl = actualSiteUrl + (needSlash ? "/" : string.Empty) + relativeUrl;
                    }

                    SetJwtBearerOptions(options, signingKey, actualSiteUrl);
                });
        }
        else
        {
            authBuilder.AddJwtBearer(authenticationScheme, displayName, options =>
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
