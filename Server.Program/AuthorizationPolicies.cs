using idunno.Authentication.Basic;

namespace Hearty.Server.Program
{
    internal static class AuthorizationPolicies
    {
        /// <summary>
        /// Basic authentication for the initial signing in of a client.
        /// </summary>
        public const string Basic = BasicAuthenticationDefaults.AuthenticationScheme;

        /// <summary>
        /// Require an administrator-level JSON Web Token or cookie.
        /// </summary>
        public const string Admin = "Admin";
    }

    internal static class AuthenticationClaims
    {
        /// <summary>
        /// Value for a "claims role" for denoting that a user has administrative rights.
        /// </summary>
        public const string Admin = "admin";
    }
}
