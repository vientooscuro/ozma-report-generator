using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Net;
using System.Threading.Tasks;
using Microsoft.Extensions.Configuration;
using Newtonsoft.Json;
using RestSharp;

namespace ReportGenerator.OzmaDBApi
{
    public class OzmaDBApiConnector
    {
        private readonly IConfiguration configuration;
        private readonly TokenProcessor tokenProcessor;
        private readonly string instanceName;
        private readonly bool allowRefresh;

        public OzmaDBApiConnector(IConfiguration configuration, string instanceName, TokenProcessor tokenProcessor, bool allowRefresh = true)
        {
            this.configuration = configuration;
            this.tokenProcessor = tokenProcessor;
            this.instanceName = instanceName;
            this.allowRefresh = allowRefresh;
        }

        private string GetApiUrl()
        {
            var dbUrl = configuration["OzmaDBSettings:DatabaseServerUrl"]!;
            return dbUrl.Replace("{instanceName}", instanceName);
        }

        public async Task<HttpStatusCode> CheckAccess()
        {
            var values = new Dictionary<string, object>();
            var request = PrepareRequest(values);
            var client = new RestClient(GetApiUrl() + "/check_access");
            var response = await client.ExecuteAsync(request);
            return response.StatusCode;
        }

        public async Task<PermissionsResponse> GetPermissions(int retryCount = 0)
        {
            var result = new PermissionsResponse();
            var values = new Dictionary<string, object>();
            var request = PrepareRequest(values);
            var test = GetApiUrl() + "/permissions";
            Debug.WriteLine("URL: " + test);
            var client = new RestClient(test);
            var response = await client.ExecuteAsync(request);
            result.ResponseCode = response.StatusCode;
            switch (response.StatusCode)
            {
                case System.Net.HttpStatusCode.OK:
                    var responseJson = response.Content!;
                    var permissions = JsonConvert.DeserializeObject<PermissionsResponseJson>(responseJson)!;
                    result.ResponseJson = permissions;
                    result.IsAdmin = permissions.IsRoot;
                    break;
                case System.Net.HttpStatusCode.Forbidden:
                    result.IsAdmin = false;
                    break;
                case System.Net.HttpStatusCode.Unauthorized:
                    if (!allowRefresh) break;
                    if (retryCount == 0)
                    {
                        retryCount++;
                        await tokenProcessor.RefreshToken();
                        result = await GetPermissions(retryCount);
                    }
                    else
                    {
                        await tokenProcessor.SignOut();
                    }
                    break;
                default:
                    throw new Exception("Getting /permissions error. Response status code: " + response.StatusCode);
            }

            return result;
        }

        private RestRequest PrepareRequest(Dictionary<string, object> parameterValues)
        {
            var request = new RestRequest();
            request.Method = Method.Get;
            request.AddHeader("cache-control", "no-cache");
            request.AddHeader("content-type", "application/x-www-form-urlencoded");
            request.AddHeader("authorization", "Bearer " + tokenProcessor.AccessToken);
            foreach (var parameterValue in parameterValues)
            {
                if (parameterValue.Value != null)
                {
                    var value = parameterValue.Value.ToString();
                    if (!string.IsNullOrEmpty(value))
                        request.AddParameter(parameterValue.Key, value, ParameterType.QueryString);
                }
            }
            return request;
        }

        private async Task<string?> ExecuteRequest(string url, RestRequest request, int retryCount = 0)
        {
            var client = new RestClient(url);
            var response = await client.ExecuteAsync(request);
            switch (response.StatusCode)
            {
                case System.Net.HttpStatusCode.OK:
                    return response.Content;
                case System.Net.HttpStatusCode.Unauthorized:
                    if (retryCount == 0)
                    {
                        retryCount++;
                        await tokenProcessor.RefreshToken();
                        return await ExecuteRequest(url, request, retryCount);
                    }
                    else
                    {
                        await tokenProcessor.SignOut();
                        throw new Exception("OzmaDB query execution error: Unauthorized. " + response.Content);
                    }
                default:
                    throw new Exception("OzmaDB query execution error. Response: " + response.Content);
            }
        }

        public async Task<string?> LoadQueryAnonymous(string queryText, Dictionary<string, object> parameterValues)
        {
            string? result = null;
            if (!string.IsNullOrEmpty(tokenProcessor.AccessToken))
            {
                var request = PrepareRequest(parameterValues);
                request.AddParameter("__query", queryText, ParameterType.QueryString);
                result = await ExecuteRequest(GetApiUrl() + "/views/anonymous/entries", request);
            }
            return result;
        }

        public async Task<string?> LoadQueryNamed(string queryText, Dictionary<string, object> parameterValues)
        {
            string? result = null;
            if (!string.IsNullOrEmpty(tokenProcessor.AccessToken))
            {
                var request = PrepareRequest(parameterValues);
                result = await ExecuteRequest(GetApiUrl() + queryText, request);
            }
            return result;
        }
    }
}
