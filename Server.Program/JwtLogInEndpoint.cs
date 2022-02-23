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

        return endpoints.MapPost("/jwt", async httpContext =>
        {
            var claims = new Claim[]
            {
                new("user", "default")
            };

            var now = DateTime.UtcNow;
            var token = new JwtSecurityToken(
                    issuer: siteUrl,
                    audience: siteUrl,
                    notBefore: now,
                    expires: now.AddYears(1),
                    claims: claims,
                    signingCredentials: credentials);

            var tokenString = tokenHandler.WriteToken(token);

            var httpResponse = httpContext.Response;
            httpResponse.StatusCode = StatusCodes.Status200OK;
            httpResponse.ContentType = "application/jwt";
            httpResponse.ContentLength = tokenString.Length;
            httpResponse.BodyWriter.WriteUtf8String(tokenString);
            await httpResponse.BodyWriter.CompleteAsync().ConfigureAwait(false);
        });
    }
}
