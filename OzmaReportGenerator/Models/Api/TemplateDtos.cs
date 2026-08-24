using System.Collections.Generic;

namespace ReportGenerator.Models.Api
{
    public sealed class SchemaDto
    {
        public int Id { get; set; }
        public string Name { get; set; } = null!;
    }

    public sealed class CreateSchemaRequest
    {
        public string Name { get; set; } = null!;
    }

    public sealed class TemplateSummaryDto
    {
        public int Id { get; set; }
        public int SchemaId { get; set; }
        public string SchemaName { get; set; } = null!;
        public string Name { get; set; } = null!;
        public int QueryCount { get; set; }
    }

    public sealed class TemplateQueryDto
    {
        public int Id { get; set; }
        public string Name { get; set; } = null!;
        public string Type { get; set; } = null!;
        public string QueryText { get; set; } = null!;
    }

    public sealed class TemplateDto
    {
        public int Id { get; set; }
        public string Name { get; set; } = null!;
        public int SchemaId { get; set; }
        public string SchemaName { get; set; } = null!;
        public IList<TemplateQueryDto> Queries { get; set; } = new List<TemplateQueryDto>();
    }

    public sealed class UpdateQueryRequest
    {
        public string QueryText { get; set; } = null!;
    }
}
