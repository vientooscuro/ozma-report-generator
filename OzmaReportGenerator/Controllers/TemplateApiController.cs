using System;
using System.Collections.Generic;
using System.Data.Common;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using ReportGenerator.Filters;
using ReportGenerator.Models.Api;
using ReportGenerator.OzmaDBApi;
using ReportGenerator.Repositories;
using ReportGenerator.Services;

namespace ReportGenerator.Controllers
{
    [ApiController]
    [OzmaAdmin]
    [Route("api/{instanceName}")]
    public sealed class TemplateApiController : ControllerBase
    {
        private readonly IConfiguration configuration;
        private readonly ILogger<TemplateApiController> logger;

        public TemplateApiController(IConfiguration configuration, ILogger<TemplateApiController> logger)
        {
            this.configuration = configuration;
            this.logger = logger;
        }

        private IActionResult Failure(Exception e, string what)
        {
            logger.LogError(e, what);
            var msg = e.InnerException != null ? e.InnerException.Message : e.Message;
            return StatusCode(500, new ApiError("internal", msg));
        }

        private static IActionResult NotFoundError(string message) =>
            new ObjectResult(new ApiError("not_found", message)) { StatusCode = 404 };

        private static IActionResult BadRequestError(string message) =>
            new ObjectResult(new ApiError("bad_request", message)) { StatusCode = 400 };

        private IActionResult InstanceFailure(Exception e)
        {
            if (e is InstanceForcedException forced)
                return BadRequestError(forced.Message);
            return NotFoundError(e.Message);
        }

        /// <summary>PostgreSQL unique violation, i.e. a name that is already taken.</summary>
        private static bool IsDuplicate(Exception e)
        {
            for (Exception? current = e; current != null; current = current.InnerException)
            {
                if (current is DbException db && db.SqlState == "23505") return true;
            }
            return false;
        }

        [HttpGet]
        [Route("schemas")]
        public async Task<IActionResult> GetSchemas(string instanceName)
        {
            try
            {
                using var repository = new ReportTemplateSchemaRepository(configuration, instanceName);
                var schemas = await repository.LoadAllSchemas();
                return Ok(schemas.Select(s => new SchemaDto { Id = s.Id, Name = s.Name }).ToList());
            }
            catch (InstanceNotFoundException)
            {
                // The instance is configured but has no data yet: nothing to list.
                return Ok(new List<SchemaDto>());
            }
            catch (InstanceForcedException e)
            {
                return BadRequestError(e.Message);
            }
            catch (Exception e)
            {
                return Failure(e, "Failed to list schemas");
            }
        }

        [HttpPost]
        [Route("schemas")]
        public async Task<IActionResult> CreateSchema(string instanceName, [FromBody] CreateSchemaRequest request)
        {
            if (request == null || string.IsNullOrWhiteSpace(request.Name))
                return BadRequestError("Field 'name' is required");

            try
            {
                // The admin panel registers the instance when it is first opened; the API has to do it too.
                using var repository = new ReportTemplateSchemaRepository(configuration, instanceName, true);
                var schema = new Models.ReportTemplateSchema { Name = TemplateService.SanitizeName(request.Name) };
                await repository.AddSchema(schema);
                return Ok(new SchemaDto { Id = schema.Id, Name = schema.Name });
            }
            catch (Exception e) when (IsDuplicate(e))
            {
                return BadRequestError("Schema '" + request.Name + "' already exists");
            }
            catch (Exception e)
            {
                return Failure(e, "Failed to create schema");
            }
        }

        [HttpDelete]
        [Route("schemas/{id:int}")]
        public async Task<IActionResult> DeleteSchema(string instanceName, int id)
        {
            try
            {
                using var repository = new ReportTemplateSchemaRepository(configuration, instanceName);
                var schema = await repository.LoadSchema(id);
                if (schema == null) return NotFoundError("Schema " + id + " not found");
                await repository.DeleteSchema(id);
                return Ok();
            }
            catch (Exception e) when (e is InstanceNotFoundException || e is InstanceForcedException)
            {
                return InstanceFailure(e);
            }
            catch (Exception e)
            {
                return Failure(e, "Failed to delete schema");
            }
        }

        [HttpGet]
        [Route("templates")]
        public async Task<IActionResult> GetTemplates(string instanceName, [FromQuery] string? schema)
        {
            try
            {
                using var repository = new ReportTemplateRepository(configuration, instanceName);
                var templates = await repository.LoadAllTemplates();
                var queryCounts = await repository.LoadQueryCounts();
                var result = new List<TemplateSummaryDto>();
                foreach (var template in templates)
                {
                    if (!string.IsNullOrEmpty(schema) && template.Schema.Name != schema) continue;
                    result.Add(new TemplateSummaryDto
                    {
                        Id = template.Id,
                        SchemaId = template.SchemaId,
                        SchemaName = template.Schema.Name,
                        Name = template.Name,
                        QueryCount = queryCounts.TryGetValue(template.Id, out var count) ? count : 0,
                    });
                }
                return Ok(result);
            }
            catch (InstanceNotFoundException)
            {
                // The instance is configured but has no data yet: nothing to list.
                return Ok(new List<TemplateSummaryDto>());
            }
            catch (InstanceForcedException e)
            {
                return BadRequestError(e.Message);
            }
            catch (Exception e)
            {
                return Failure(e, "Failed to list templates");
            }
        }

        [HttpGet]
        [Route("templates/{id:int}")]
        public async Task<IActionResult> GetTemplate(string instanceName, int id)
        {
            try
            {
                using var repository = new ReportTemplateRepository(configuration, instanceName);
                var template = await repository.LoadTemplate(id);
                if (template == null) return NotFoundError("Template " + id + " not found");

                using var schemaRepository = new ReportTemplateSchemaRepository(configuration, instanceName);
                var schema = await schemaRepository.LoadSchema(template.SchemaId);

                return Ok(new TemplateDto
                {
                    Id = template.Id,
                    Name = template.Name,
                    SchemaId = template.SchemaId,
                    SchemaName = schema?.Name ?? "",
                    Queries = template.ReportTemplateQueries.Select(q => new TemplateQueryDto
                    {
                        Id = q.Id,
                        Name = q.Name,
                        Type = ((QueryType)q.QueryType).ToString(),
                        QueryText = q.QueryText,
                    }).ToList(),
                });
            }
            catch (Exception e) when (e is InstanceNotFoundException || e is InstanceForcedException)
            {
                return InstanceFailure(e);
            }
            catch (Exception e)
            {
                return Failure(e, "Failed to load template");
            }
        }

        [HttpGet]
        [Route("templates/{id:int}/file")]
        public async Task<IActionResult> DownloadTemplate(string instanceName, int id)
        {
            try
            {
                using var repository = new ReportTemplateRepository(configuration, instanceName);
                var template = await repository.LoadTemplate(id);
                if (template == null) return NotFoundError("Template " + id + " not found");

                var bytes = await TemplateService.RestoreOdtAsync(template.OdtWithoutQueries, template.ReportTemplateQueries);
                return File(bytes, "application/vnd.oasis.opendocument.text", template.Name + ".odt");
            }
            catch (Exception e) when (e is InstanceNotFoundException || e is InstanceForcedException)
            {
                return InstanceFailure(e);
            }
            catch (Exception e)
            {
                return Failure(e, "Failed to download template");
            }
        }

        [HttpPost]
        [Route("templates")]
        public async Task<IActionResult> CreateTemplate(
            string instanceName,
            [FromForm] string schemaName,
            [FromForm] string name,
            IFormFile file)
        {
            if (string.IsNullOrWhiteSpace(schemaName)) return BadRequestError("Field 'schemaName' is required");
            if (string.IsNullOrWhiteSpace(name)) return BadRequestError("Field 'name' is required");
            if (file == null || file.Length == 0) return BadRequestError("File 'file' is required");

            ParsedTemplate parsed;
            try
            {
                parsed = await ParseUploadedFile(file);
            }
            catch (Exception e)
            {
                logger.LogError(e, "Failed to parse uploaded template");
                var msg = e.InnerException != null ? e.InnerException.Message : e.Message;
                return BadRequestError("Uploaded file is not a valid ODT template: " + msg);
            }

            try
            {
                using var schemaRepository = new ReportTemplateSchemaRepository(configuration, instanceName);
                var schemas = await schemaRepository.LoadAllSchemas();
                var schema = schemas.FirstOrDefault(s => s.Name == schemaName);
                if (schema == null) return NotFoundError("Schema '" + schemaName + "' not found");

                var model = new Models.ReportTemplate
                {
                    SchemaId = schema.Id,
                    Name = TemplateService.SanitizeName(name),
                    OdtWithoutQueries = parsed.OdtWithoutQueries,
                };
                foreach (var query in parsed.Queries) model.ReportTemplateQueries.Add(query);

                using var repository = new ReportTemplateRepository(configuration, instanceName);
                await repository.AddTemplate(model);
                return Ok(new TemplateSummaryDto
                {
                    Id = model.Id,
                    SchemaId = schema.Id,
                    SchemaName = schema.Name,
                    Name = model.Name,
                    QueryCount = model.ReportTemplateQueries.Count,
                });
            }
            catch (Exception e) when (IsDuplicate(e))
            {
                return BadRequestError("Template '" + name + "' already exists in schema '" + schemaName + "'");
            }
            catch (Exception e) when (e is InstanceNotFoundException || e is InstanceForcedException)
            {
                return InstanceFailure(e);
            }
            catch (Exception e)
            {
                return Failure(e, "Failed to create template");
            }
        }

        [HttpPut]
        [Route("templates/{id:int}/file")]
        public async Task<IActionResult> ReplaceTemplateFile(string instanceName, int id, IFormFile file)
        {
            if (file == null || file.Length == 0) return BadRequestError("File 'file' is required");

            ParsedTemplate parsed;
            try
            {
                parsed = await ParseUploadedFile(file);
            }
            catch (Exception e)
            {
                logger.LogError(e, "Failed to parse uploaded template");
                var msg = e.InnerException != null ? e.InnerException.Message : e.Message;
                return BadRequestError("Uploaded file is not a valid ODT template: " + msg);
            }

            try
            {
                using var repository = new ReportTemplateRepository(configuration, instanceName);
                var model = await repository.LoadTemplate(id);
                if (model == null) return NotFoundError("Template " + id + " not found");

                model.ReportTemplateQueries.Clear();
                foreach (var query in parsed.Queries) model.ReportTemplateQueries.Add(query);
                model.OdtWithoutQueries = parsed.OdtWithoutQueries;
                await repository.UpdateTemplate(model);

                using var schemaRepository = new ReportTemplateSchemaRepository(configuration, instanceName);
                var schema = await schemaRepository.LoadSchema(model.SchemaId);

                return Ok(new TemplateSummaryDto
                {
                    Id = model.Id,
                    SchemaId = model.SchemaId,
                    SchemaName = schema?.Name ?? "",
                    Name = model.Name,
                    QueryCount = model.ReportTemplateQueries.Count,
                });
            }
            catch (Exception e) when (e is InstanceNotFoundException || e is InstanceForcedException)
            {
                return InstanceFailure(e);
            }
            catch (Exception e)
            {
                return Failure(e, "Failed to replace template file");
            }
        }

        [HttpDelete]
        [Route("templates/{id:int}")]
        public async Task<IActionResult> DeleteTemplate(string instanceName, int id)
        {
            try
            {
                using var repository = new ReportTemplateRepository(configuration, instanceName);
                var template = await repository.LoadTemplate(id);
                if (template == null) return NotFoundError("Template " + id + " not found");
                await repository.DeleteTemplate(id);
                return Ok();
            }
            catch (Exception e) when (e is InstanceNotFoundException || e is InstanceForcedException)
            {
                return InstanceFailure(e);
            }
            catch (Exception e)
            {
                return Failure(e, "Failed to delete template");
            }
        }

        [HttpPut]
        [Route("templates/{id:int}/queries/{queryId:int}")]
        public async Task<IActionResult> UpdateQuery(string instanceName, int id, int queryId, [FromBody] UpdateQueryRequest request)
        {
            if (request == null || string.IsNullOrWhiteSpace(request.QueryText))
                return BadRequestError("Field 'queryText' is required");

            try
            {
                using var repository = new ReportTemplateRepository(configuration, instanceName);
                var updated = await repository.UpdateQueryText(id, queryId, request.QueryText);
                if (!updated) return NotFoundError("Query " + queryId + " of template " + id + " not found");

                var template = await repository.LoadTemplate(id);
                var query = template!.ReportTemplateQueries.First(q => q.Id == queryId);
                return Ok(new TemplateQueryDto
                {
                    Id = query.Id,
                    Name = query.Name,
                    Type = ((QueryType)query.QueryType).ToString(),
                    QueryText = query.QueryText,
                });
            }
            catch (Exception e) when (e is InstanceNotFoundException || e is InstanceForcedException)
            {
                return InstanceFailure(e);
            }
            catch (Exception e)
            {
                return Failure(e, "Failed to update query");
            }
        }

        [HttpPost]
        [Route("templates/{id:int}/analyze")]
        public async Task<IActionResult> AnalyzeTemplate(string instanceName, int id)
        {
            try
            {
                using var repository = new ReportTemplateRepository(configuration, instanceName);
                var template = await repository.LoadTemplate(id);
                if (template == null) return NotFoundError("Template " + id + " not found");

                using var schemaRepository = new ReportTemplateSchemaRepository(configuration, instanceName);
                var schema = await schemaRepository.LoadSchema(template.SchemaId);

                Sandwych.Reporting.OpenDocument.OdfDocument odt;
                await using (var stream = new MemoryStream(template.OdtWithoutQueries))
                    odt = await Sandwych.Reporting.OpenDocument.OdfDocument.LoadFromAsync(stream);

                var analysis = TemplateAnalyzer.Analyze(odt, template.ReportTemplateQueries);
                return Ok(new
                {
                    templateId = template.Id,
                    schemaName = schema?.Name ?? "",
                    name = template.Name,
                    queries = analysis.Queries,
                    expressions = analysis.Expressions,
                    findings = analysis.Findings,
                });
            }
            catch (Exception e) when (e is InstanceNotFoundException || e is InstanceForcedException)
            {
                return InstanceFailure(e);
            }
            catch (Exception e)
            {
                return Failure(e, "Failed to analyze template");
            }
        }

        private static async Task<ParsedTemplate> ParseUploadedFile(IFormFile file)
        {
            await using var stream = new MemoryStream();
            await file.CopyToAsync(stream);
            stream.Position = 0;
            return await TemplateService.ParseUploadAsync(stream);
        }
    }
}
