using System;
using System.Collections.Generic;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using ReportGenerator.Models.Api;
using ReportGenerator.Repositories;
using ReportGenerator.Services;

namespace ReportGenerator.Controllers
{
    [ApiController]
    public sealed class InstancesApiController : ControllerBase
    {
        private readonly IConfiguration configuration;
        private readonly IOzmaPermissionsChecker permissionsChecker;
        private readonly IMemoryCache cache;
        private readonly ILogger<InstancesApiController> logger;

        public InstancesApiController(
            IConfiguration configuration,
            IOzmaPermissionsChecker permissionsChecker,
            IMemoryCache cache,
            ILogger<InstancesApiController> logger)
        {
            this.configuration = configuration;
            this.permissionsChecker = permissionsChecker;
            this.cache = cache;
            this.logger = logger;
        }

        [HttpGet]
        [Route("api/instances")]
        public async Task<IActionResult> GetInstances()
        {
            try
            {
                var names = new List<string>();
                var forced = configuration.GetValue<string>("OzmaDBSettings:ForceInstance");
                if (!string.IsNullOrEmpty(forced))
                {
                    names.Add(forced);
                }
                else
                {
                    using var repository = new InstanceRepository(configuration);
                    names = await repository.LoadAllInstanceNames();
                }

                var allowed = new List<string>();
                foreach (var name in names)
                {
                    if (await IsAdminFor(name)) allowed.Add(name);
                }
                return Ok(new { instances = allowed });
            }
            catch (Exception e)
            {
                logger.LogError(e, "Failed to list instances");
                var msg = e.InnerException != null ? e.InnerException.Message : e.Message;
                return StatusCode(500, new ApiError("internal", msg));
            }
        }

        private async Task<bool> IsAdminFor(string instanceName)
        {
            var cacheKey = "instance-admin:" + instanceName + ":" + TokenFingerprint();
            if (cache.TryGetValue(cacheKey, out bool cached)) return cached;

            var permissions = await permissionsChecker.GetPermissions(HttpContext, instanceName);
            var isAdmin = permissions != null
                          && permissions.ResponseCode != HttpStatusCode.Unauthorized
                          && permissions.IsAdmin;
            cache.Set(cacheKey, isAdmin, TimeSpan.FromSeconds(60));
            return isAdmin;
        }

        private string TokenFingerprint()
        {
            var header = Request.Headers["Authorization"].ToString();
            if (string.IsNullOrEmpty(header)) header = Request.Cookies[".AspNetCore.Cookies"] ?? "";
            var hash = SHA256.HashData(Encoding.UTF8.GetBytes(header));
            return Convert.ToHexString(hash, 0, 8);
        }
    }
}
