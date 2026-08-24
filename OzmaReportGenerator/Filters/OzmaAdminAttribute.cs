using System;
using System.Net;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.Extensions.DependencyInjection;
using ReportGenerator.Models.Api;
using ReportGenerator.Services;

namespace ReportGenerator.Filters
{
    /// <summary>
    /// Authorizes API requests with an OzmaDB access token instead of the cookie/OIDC scheme,
    /// so that machine clients get a JSON error rather than a login redirect.
    /// </summary>
    [AttributeUsage(AttributeTargets.Class | AttributeTargets.Method)]
    public sealed class OzmaAdminAttribute : Attribute, IAsyncAuthorizationFilter
    {
        public async Task OnAuthorizationAsync(AuthorizationFilterContext context)
        {
            if (!context.RouteData.Values.TryGetValue("instanceName", out var raw)
                || raw is not string instanceName
                || string.IsNullOrEmpty(instanceName))
            {
                context.Result = new ObjectResult(new ApiError("bad_request", "No instance name in route")) { StatusCode = 400 };
                return;
            }

            var checker = context.HttpContext.RequestServices.GetRequiredService<IOzmaPermissionsChecker>();
            var permissions = await checker.GetPermissions(context.HttpContext, instanceName);

            if (permissions == null || permissions.ResponseCode == HttpStatusCode.Unauthorized)
            {
                context.Result = new ObjectResult(new ApiError("unauthorized", "Valid OzmaDB access token required")) { StatusCode = 401 };
                return;
            }

            if (!permissions.IsAdmin)
            {
                context.Result = new ObjectResult(new ApiError("forbidden", "User has no admin rights for instance " + instanceName)) { StatusCode = 403 };
            }
        }
    }
}
