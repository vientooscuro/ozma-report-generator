using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
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
                using var repository = new ReportTemplateSchemaRepository(configuration, instanceName);
                var schema = new Models.ReportTemplateSchema { Name = TemplateService.SanitizeName(request.Name) };
                await repository.AddSchema(schema);
                return Ok(new SchemaDto { Id = schema.Id, Name = schema.Name });
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
            catch (Exception e)
            {
                return Failure(e, "Failed to load template");
            }
        }
    }
}
