using System.Security.Authentication;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using ReportGenerator.OzmaDBApi;

namespace ReportGenerator.Services
{
    public sealed class OzmaPermissionsChecker : IOzmaPermissionsChecker
    {
        private readonly IConfiguration configuration;

        public OzmaPermissionsChecker(IConfiguration configuration)
        {
            this.configuration = configuration;
        }

        public async Task<PermissionsResponse?> GetPermissions(HttpContext context, string instanceName)
        {
            TokenProcessor tokenProcessor;
            try
            {
                tokenProcessor = TokenProcessor.Create(configuration, context);
            }
            catch (AuthenticationException)
            {
                return null;
            }

            // Bearer clients have no refresh token claim, so refreshing would throw instead of failing with 401.
            var connector = new OzmaDBApiConnector(configuration, instanceName, tokenProcessor, !tokenProcessor.IsFromHeader);
            return await connector.GetPermissions();
        }
    }
}
