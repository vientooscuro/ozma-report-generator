using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using ReportGenerator.OzmaDBApi;

namespace ReportGenerator.Services
{
    public interface IOzmaPermissionsChecker
    {
        /// <summary>Returns null when no usable access token is present in the request.</summary>
        Task<PermissionsResponse?> GetPermissions(HttpContext context, string instanceName);
    }
}
