using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.IdentityModel.Tokens;
using System;
using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;

namespace JobBank.Server.Program;

public static class JwtLogInEndpoint
{
    public static IEndpointConventionBuilder
        MapJwtLogin(this IEndpointRouteBuilder endpoints)
    {
        var tokenHandler = new JwtSecurityTokenHandler();
        var credentials = new SigningCredentials(
                            key: new SymmetricSecurityKey(Startup._jwtSigningKey),
                            algorithm: SecurityAlgorithms.HmacSha256);

        return endpoints.MapPost("/jwt", async httpContext =>
        {
            var claims = new Claim[]
            {
                new("user", "default")
            };

            var now = DateTime.UtcNow;
            var token = new JwtSecurityToken(
                    issuer: "http://localhost:5000/",
                    audience: "https://localhost:5001/",
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
