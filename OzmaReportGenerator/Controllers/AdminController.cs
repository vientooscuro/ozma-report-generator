using Microsoft.AspNetCore.Mvc;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Rendering;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using ReportGenerator.OzmaDBApi;
using ReportGenerator.Models;
using ReportGenerator.Repositories;
using Sandwych.Reporting.OpenDocument;
using Test.Models;

namespace ReportGenerator.Controllers
{
    [Authorize]
    public class AdminController : BaseController
    {
        ILogger<AdminController> logger;

        public AdminController(IConfiguration configuration, ILogger<AdminController> logger) : base(configuration)
        {
            this.logger = logger;
        }

        [AllowAnonymous]
        [ResponseCache(Duration = 0, Location = ResponseCacheLocation.None, NoStore = true)]
        public IActionResult Error()
        {
            return View(new ErrorViewModel { RequestId = Activity.Current?.Id ?? HttpContext.TraceIdentifier });
        }

        private async Task<PermissionsResponse?> HasPermissionsForInstance(string instanceName)
        {
            var isAuthenticated = CreateTokenProcessor();
            if ((isAuthenticated) && (TokenProcessor != null))
            {
                var ozmaDbApiConnector = new OzmaDBApiConnector(configuration, instanceName, TokenProcessor);
                var permissions = await ozmaDbApiConnector.GetPermissions();
                return permissions;
            }
            return null;
        }

        private async Task<SelectList> LoadSchemaNamesList(string instanceName)
        {
            var list = new List<ReportTemplateSchema>();
            using (var repository = new ReportTemplateSchemaRepository(configuration, instanceName))
            {
                list = await repository.LoadAllSchemas();
            }
            var selectList = new SelectList(list, "Id", "Name");
            return selectList;
        }

        [HttpGet]
        [Route("admin/{instanceName}/GetSchemaNamesList")]
        public async Task<JsonResult> GetSchemaNamesList(string instanceName)
        {
            var selectList = await LoadSchemaNamesList(instanceName);
            return Json(selectList);
        }

        [Route("admin/{instanceName}")]
        public async Task<IActionResult> Index(string instanceName)
        {
            if (string.IsNullOrEmpty(instanceName))
                throw new Exception("Instance name cannot be empty");

            var permissions = await HasPermissionsForInstance(instanceName);
            if ((permissions == null) || (permissions.ResponseCode == System.Net.HttpStatusCode.Unauthorized))
                return RedirectToAction("Index", new { instanceName });
            if (!permissions.IsAdmin) return Unauthorized("User has no admin rights for this instance");

            new ReportTemplateRepository(configuration, instanceName, true);
            ViewBag.SchemaId = await LoadSchemaNamesList(instanceName);
            ViewBag.instanceName = instanceName;
            return View();
        }

        private static string RemoveRestrictedSymbols(string text)
        {
            return text.Replace(" ", "").Replace("/", "").Replace("__", "");
        }

        #region Схемы шаблонов 
        [HttpGet]
        [Route("admin/{instanceName}/LoadSchemas")]
        public async Task<IActionResult> LoadSchemas(string instanceName)
        {
            var permissions = await HasPermissionsForInstance(instanceName);
            if ((permissions == null) || (permissions.ResponseCode == System.Net.HttpStatusCode.Unauthorized))
                return StatusCode(401, "relog");
            if (!permissions.IsAdmin) return Unauthorized("User has no admin rights for this instance");

            var list = new List<ReportTemplateSchema>();
            using (var repository = new ReportTemplateSchemaRepository(configuration, instanceName))
            {
                list = await repository.LoadAllSchemas();
            }
            return PartialView("~/Views/Admin/PartialViews/SchemasListPartial.cshtml", list);
        }

        [HttpPost]
        [Route("admin/{instanceName}/AddSchema")]
        public async Task<IActionResult> AddSchema(string instanceName, ReportTemplateSchema model)
        {
            var permissions = await HasPermissionsForInstance(instanceName);
            if ((permissions == null) || (permissions.ResponseCode == System.Net.HttpStatusCode.Unauthorized))
                return StatusCode(401, "relog");
            if (!permissions.IsAdmin) return Unauthorized("User has no admin rights for this instance");

            using (var repository = new ReportTemplateSchemaRepository(configuration, instanceName))
            {
                model.Name = RemoveRestrictedSymbols(model.Name);
                await repository.AddSchema(model);
                return Ok();
            }
        }

        [AllowAnonymous]
        [HttpDelete]
        [Route("admin/{instanceName}/DeleteSchema")]
        public async Task<IActionResult> DeleteSchemaAnonymous(string instanceName, int id)
        {
            return await DeleteSchema(instanceName, id);
        }

        [HttpDelete]
        //[Route("admin/{instanceName}/DeleteSchema")]
        public async Task<IActionResult> DeleteSchema(string instanceName, int id)
        {
            var permissions = await HasPermissionsForInstance(instanceName);
            if ((permissions == null) || (permissions.ResponseCode == System.Net.HttpStatusCode.Unauthorized))
                return StatusCode(401, "relog");
            if (!permissions.IsAdmin) return Unauthorized("User has no admin rights for this instance");

            using (var repository = new ReportTemplateSchemaRepository(configuration, instanceName))
            {
                await repository.DeleteSchema(id);
                return Ok();
            }
        }
        #endregion

        #region Шаблоны
        [HttpGet]
        [Route("admin/{instanceName}/LoadTemplates")]
        public async Task<IActionResult> LoadTemplates(string instanceName)
        {
            var permissions = await HasPermissionsForInstance(instanceName);
            if ((permissions == null) || (permissions.ResponseCode == System.Net.HttpStatusCode.Unauthorized))
                return StatusCode(401, "relog");
            if (!permissions.IsAdmin) return Unauthorized("User has no admin rights for this instance");

            var list = new List<VReportTemplate>();
            using (var repository = new ReportTemplateRepository(configuration, instanceName))
            {
                list = await repository.LoadAllTemplates();
            }
            return PartialView("~/Views/Admin/PartialViews/TemplatesListPartial.cshtml", list);
        }

        [HttpPost]
        [Route("admin/{instanceName}/AddTemplate")]
        public async Task<IActionResult> AddTemplate(string instanceName, IFormFile UploadedOdtFile, ReportTemplate model)
        {
            var permissions = await HasPermissionsForInstance(instanceName);
            if ((permissions == null) || (permissions.ResponseCode == System.Net.HttpStatusCode.Unauthorized))
                return StatusCode(401, "relog");
            if (!permissions.IsAdmin) return Unauthorized("User has no admin rights for this instance");

            OdfDocument odtWithQueries;
            OdfDocument odtWithoutQueries;
            IList<OzmaDBQuery> queries;
            try
            {
                await using (var stream = new MemoryStream())
                {
                    await UploadedOdtFile.CopyToAsync(stream);
                    odtWithQueries = await OdfDocument.LoadFromAsync(stream);
                }

                queries = OpenDocumentTextFunctions.GetQueriesFromOdt(odtWithQueries);
                odtWithoutQueries = OpenDocumentTextFunctions.RemoveQueriesFromOdt(odtWithQueries);
                var odtTemplate = new OdtTemplate(odtWithoutQueries);
            }
            catch (Exception e)
            {
                logger.LogError(e, "Failed to add template");
                string msg;
                if (e.InnerException != null) msg = e.InnerException.Message;
                else msg = e.Message;
                return StatusCode(500, msg);
            }

            await using (var stream = new MemoryStream())
            {
                await odtWithoutQueries.SaveAsync(stream);
                model.OdtWithoutQueries = stream.ToArray();
            }
            model.Name = RemoveRestrictedSymbols(model.Name);
            foreach (var query in queries)
            {
                var newQuery = new ReportTemplateQuery
                {
                    Name = query.Name,
                    QueryText = query.QueryTextWithoutParameterValues,
                    QueryType = (short)query.QueryType
                };
                model.ReportTemplateQueries.Add(newQuery);
            }
            using (var repository = new ReportTemplateRepository(configuration, instanceName))
            {
                await repository.AddTemplate(model);
            }
            return Ok();
        }

        [HttpPost]
        [Route("admin/{instanceName}/UpdateTemplateFile")]
        public async Task<IActionResult> UpdateTemplateFile(string instanceName, int templateId, IFormFile UploadedOdtFile)
        {
            var permissions = await HasPermissionsForInstance(instanceName);
            if ((permissions == null) || (permissions.ResponseCode == System.Net.HttpStatusCode.Unauthorized))
                return StatusCode(401, "relog");
            if (!permissions.IsAdmin) return Unauthorized("User has no admin rights for this instance");

            OdfDocument odtWithQueries;
            OdfDocument odtWithoutQueries;
            IList<OzmaDBQuery> queries;
            try
            {
                await using (var stream = new MemoryStream())
                {
                    await UploadedOdtFile.CopyToAsync(stream);
                    odtWithQueries = await OdfDocument.LoadFromAsync(stream);
                }
                queries = OpenDocumentTextFunctions.GetQueriesFromOdt(odtWithQueries);
                odtWithoutQueries = OpenDocumentTextFunctions.RemoveQueriesFromOdt(odtWithQueries);
                var odtTemplate = new OdtTemplate(odtWithoutQueries);
            }
            catch (Exception e)
            {
                logger.LogError(e, "Failed to update template");
                string msg;
                if (e.InnerException != null) msg = e.InnerException.Message;
                else msg = e.Message;
                return StatusCode(500, msg);
            }

            using (var repository = new ReportTemplateRepository(configuration, instanceName))
            {
                var model = await repository.LoadTemplate(templateId);
                if (model == null) throw new Exception("Template with id=" + templateId + " not found");
                model.ReportTemplateQueries.Clear();
                foreach (var query in queries)
                {
                    var newQuery = new ReportTemplateQuery
                    {
                        Name = query.Name,
                        QueryText = query.QueryTextWithoutParameterValues,
                        QueryType = (short)query.QueryType
                    };
                    model.ReportTemplateQueries.Add(newQuery);
                }
                await using (var stream = new MemoryStream())
                {
                    await odtWithoutQueries.SaveAsync(stream);
                    model.OdtWithoutQueries = stream.ToArray();
                }
                await repository.UpdateTemplate(model);
                return Ok();
            }
        }

        [HttpGet]
        [Route("admin/{instanceName}/DownloadTemplate")]
        public async Task<IActionResult> DownloadTemplate(string instanceName, int id)
        {
            var permissions = await HasPermissionsForInstance(instanceName);
            if ((permissions == null) || (permissions.ResponseCode == System.Net.HttpStatusCode.Unauthorized))
                return StatusCode(401, "relog");
            if (!permissions.IsAdmin) return Unauthorized("User has no admin rights for this instance");

            using (var repository = new ReportTemplateRepository(configuration, instanceName))
            {
                var template = await repository.LoadTemplate(id);
                if (template == null) return NotFound();

                OdfDocument odt;
                await using (var stream = new MemoryStream(template.OdtWithoutQueries))
                    odt = await OdfDocument.LoadFromAsync(stream);

                OpenDocumentTextFunctions.RestoreQueriesInOdt(odt, template.ReportTemplateQueries);

                byte[] bytes;
                await using (var stream = new MemoryStream())
                {
                    await odt.SaveAsync(stream);
                    bytes = stream.ToArray();
                }
                return File(bytes, "application/vnd.oasis.opendocument.text", template.Name + ".odt");
            }
        }

        [HttpDelete]
        [Route("admin/{instanceName}/DeleteTemplate")]
        public async Task<IActionResult> DeleteTemplate(string instanceName, int id)
        {
            var permissions = await HasPermissionsForInstance(instanceName);
            if ((permissions == null) || (permissions.ResponseCode == System.Net.HttpStatusCode.Unauthorized))
                return StatusCode(401, "relog");
            if (!permissions.IsAdmin) return Unauthorized("User has no admin rights for this instance");

            using (var repository = new ReportTemplateRepository(configuration, instanceName))
            {
                await repository.DeleteTemplate(id);
                return Ok();
            }
        }
        #endregion
    }
}
