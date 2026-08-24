using System;
using System.Net.Http;
using System.Security.Claims;
using System.Threading.Tasks;
using System.Security.Authentication;
using IdentityModel.Client;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;

namespace ReportGenerator
{
    public class TokenProcessor
    {
        public string AccessToken { get; private set; }

        /// <summary>True when the token came from the Authorization header, i.e. there is no refresh token claim.</summary>
        public bool IsFromHeader { get; private set; }
        private readonly HttpContext httpContext;
        private readonly IConfiguration configuration;

        private TokenProcessor(IConfiguration configuration, HttpContext httpContext, string accessToken, bool isFromHeader)
        {
            this.httpContext = httpContext;
            AccessToken = accessToken;
            this.configuration = configuration;
            IsFromHeader = isFromHeader;
        }

        public static TokenProcessor Create(IConfiguration configuration, HttpContext httpContext)
        {
            // Token is in headers for regular generation requests and in identity for admin panel.
            var accessTokenFromHeader = httpContext.Request.Headers["Authorization"];
            if (accessTokenFromHeader.Count > 0)
            {
                var accessToken = accessTokenFromHeader.ToString().Split(' ');
                if (accessToken.Length != 2) throw new AuthenticationException("Wrong access token format");
                return new TokenProcessor(configuration, httpContext, accessToken[1], true);
            }
            else
            {
                var principal = httpContext.User;
                var identity = (ClaimsIdentity)principal.Identity!;
                var accessTokenFromIdentity = identity.FindFirst("access_token");
                if (accessTokenFromIdentity != null)
                {
                    // await httpContext.GetTokenAsync(OpenIdConnectDefaults.AuthenticationScheme, "access_token");
                    return new TokenProcessor(configuration, httpContext, accessTokenFromIdentity.Value, false);
                }
                else
                {
                    throw new AuthenticationException("Access token not found");
                }
            }
        }

        public async Task RefreshToken()
        {
            var principal = httpContext.User;
            var identity = (ClaimsIdentity)principal.Identity!;
            var accessTokenClaim = identity.FindFirst("access_token");
            var refreshTokenClaim = identity.FindFirst("refresh_token");
            //var refreshToken = await httpContext.GetTokenAsync(OpenIdConnectDefaults.AuthenticationScheme, "refresh_token");
            if (refreshTokenClaim == null) throw new Exception("Refresh token not found in HttpContext"); ;
            var refreshToken = refreshTokenClaim.Value;
            var response = await new HttpClient().RequestRefreshTokenAsync(new RefreshTokenRequest
            {
                Address = configuration["AuthSettings:OpenIdConnectUrl"] + "protocol/openid-connect/token",
                ClientId = configuration["AuthSettings:ClientId"]!,
                ClientSecret = configuration["AuthSettings:ClientSecret"],
                RefreshToken = refreshToken
            });
            if (!response.IsError)
            {
                AccessToken = response.AccessToken!;
                identity.RemoveClaim(accessTokenClaim);
                identity.RemoveClaim(refreshTokenClaim);
                identity.AddClaims(new[]
                {
                    new Claim("access_token", response.AccessToken!),
                    new Claim("refresh_token", response.RefreshToken!)
                });
                await httpContext.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme, principal);
            }
            else await SignOut();
        }

        public async Task SignOut()
        {
            await httpContext.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
            await httpContext.SignOutAsync(OpenIdConnectDefaults.AuthenticationScheme);
        }
    }
}
