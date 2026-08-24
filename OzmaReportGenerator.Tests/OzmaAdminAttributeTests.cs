using System.Collections.Generic;
using System.Net;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Abstractions;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using ReportGenerator.Filters;
using ReportGenerator.Models.Api;
using ReportGenerator.OzmaDBApi;
using ReportGenerator.Services;
using Xunit;

namespace OzmaReportGenerator.Tests
{
    public class OzmaAdminAttributeTests
    {
        private sealed class FakeChecker : IOzmaPermissionsChecker
        {
            private readonly PermissionsResponse? response;

            public FakeChecker(PermissionsResponse? response) => this.response = response;

            public string? LastInstance { get; private set; }

            public Task<PermissionsResponse?> GetPermissions(HttpContext context, string instanceName)
            {
                LastInstance = instanceName;
                return Task.FromResult(response);
            }
        }

        private static AuthorizationFilterContext MakeContext(IOzmaPermissionsChecker checker, string? instanceName)
        {
            var services = new ServiceCollection();
            services.AddSingleton(checker);
            var httpContext = new DefaultHttpContext { RequestServices = services.BuildServiceProvider() };

            var routeData = new RouteData();
            if (instanceName != null) routeData.Values["instanceName"] = instanceName;

            var actionContext = new ActionContext(httpContext, routeData, new ActionDescriptor());
            return new AuthorizationFilterContext(actionContext, new List<IFilterMetadata>());
        }

        [Fact]
        public async Task NoPermissions_Returns401()
        {
            var context = MakeContext(new FakeChecker(null), "gogol");

            await new OzmaAdminAttribute().OnAuthorizationAsync(context);

            var result = Assert.IsType<ObjectResult>(context.Result);
            Assert.Equal(401, result.StatusCode);
            Assert.Equal("unauthorized", Assert.IsType<ApiError>(result.Value).Error);
        }

        [Fact]
        public async Task UnauthorizedFromOzma_Returns401()
        {
            var permissions = new PermissionsResponse { ResponseCode = HttpStatusCode.Unauthorized, IsAdmin = false };
            var context = MakeContext(new FakeChecker(permissions), "gogol");

            await new OzmaAdminAttribute().OnAuthorizationAsync(context);

            var result = Assert.IsType<ObjectResult>(context.Result);
            Assert.Equal(401, result.StatusCode);
        }

        [Fact]
        public async Task NotAdmin_Returns403()
        {
            var permissions = new PermissionsResponse { ResponseCode = HttpStatusCode.OK, IsAdmin = false };
            var context = MakeContext(new FakeChecker(permissions), "gogol");

            await new OzmaAdminAttribute().OnAuthorizationAsync(context);

            var result = Assert.IsType<ObjectResult>(context.Result);
            Assert.Equal(403, result.StatusCode);
            Assert.Equal("forbidden", Assert.IsType<ApiError>(result.Value).Error);
        }

        [Fact]
        public async Task Admin_PassesThrough()
        {
            var permissions = new PermissionsResponse { ResponseCode = HttpStatusCode.OK, IsAdmin = true };
            var checker = new FakeChecker(permissions);
            var context = MakeContext(checker, "gogol");

            await new OzmaAdminAttribute().OnAuthorizationAsync(context);

            Assert.Null(context.Result);
            Assert.Equal("gogol", checker.LastInstance);
        }

        [Fact]
        public async Task MissingInstanceName_Returns400()
        {
            var permissions = new PermissionsResponse { ResponseCode = HttpStatusCode.OK, IsAdmin = true };
            var context = MakeContext(new FakeChecker(permissions), null);

            await new OzmaAdminAttribute().OnAuthorizationAsync(context);

            var result = Assert.IsType<ObjectResult>(context.Result);
            Assert.Equal(400, result.StatusCode);
        }
    }
}
