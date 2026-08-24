using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using ReportGenerator.Models;
using ReportGenerator.OzmaDBApi;
using ReportGenerator.Services;
using Xunit;

namespace OzmaReportGenerator.Tests
{
    public class TemplateAnalyzerTests
    {
        private static ReportTemplateQuery Query(string name, QueryType type, string text)
        {
            return new ReportTemplateQuery { Name = name, QueryType = (short)type, QueryText = text };
        }

        [Fact]
        public async Task Analyze_ReportsQueriesExpressionsAndNoFindingsForValidTemplate()
        {
            var odt = await OdtFixture.CreateAsync(
                OdtFixture.Paragraph("{{hdr.number}}"),
                OdtFixture.Paragraph("{% for row in rows %}{{row.sum}}{% endfor %}"));
            var queries = new List<ReportTemplateQuery>
            {
                Query("hdr", QueryType.SingleRow, "{ $id int }: select num as number from public.inv"),
                Query("rows", QueryType.ManyRows, "select s as sum from public.lines"),
            };

            var analysis = TemplateAnalyzer.Analyze(odt, queries);

            Assert.DoesNotContain(analysis.Findings, f => f.Severity == "error");
            Assert.Equal(2, analysis.Queries.Count);
            var header = analysis.Queries.Single(q => q.Name == "hdr");
            Assert.Equal("SingleRow", header.Type);
            Assert.Equal("funql", header.Kind);
            Assert.Equal(new[] { "id" }, header.Parameters);
            var rows = analysis.Expressions.Single(e => e.QueryName == "rows");
            Assert.Equal("ManyRows", rows.ImpliedType);
            Assert.Equal("row", rows.SubQueryName);
            Assert.Equal(new[] { "sum" }, rows.Fields);
        }

        [Fact]
        public async Task Analyze_DetectsUnknownQuery()
        {
            var odt = await OdtFixture.CreateAsync(OdtFixture.Paragraph("{{totals.sum}}"));
            var queries = new List<ReportTemplateQuery>
            {
                Query("hdr", QueryType.SingleRow, "select 1 as a from public.x"),
            };

            var analysis = TemplateAnalyzer.Analyze(odt, queries);

            Assert.Contains(analysis.Findings, f => f.Code == "unknown_query" && f.QueryName == "totals" && f.Severity == "error");
        }

        [Fact]
        public async Task Analyze_DetectsQueryTypeMismatch()
        {
            var odt = await OdtFixture.CreateAsync(OdtFixture.Paragraph("{{rows.sum}}"));
            var queries = new List<ReportTemplateQuery>
            {
                Query("rows", QueryType.ManyRows, "select s as sum from public.lines"),
            };

            var analysis = TemplateAnalyzer.Analyze(odt, queries);

            Assert.Contains(analysis.Findings, f => f.Code == "query_type_mismatch" && f.QueryName == "rows");
        }

        [Fact]
        public async Task Analyze_DetectsUnusedQueryAsWarning()
        {
            var odt = await OdtFixture.CreateAsync(OdtFixture.Paragraph("{{hdr.a}}"));
            var queries = new List<ReportTemplateQuery>
            {
                Query("hdr", QueryType.SingleRow, "select 1 as a from public.x"),
                Query("orphan", QueryType.SingleValue, "select 2 from public.x"),
            };

            var analysis = TemplateAnalyzer.Analyze(odt, queries);

            Assert.Contains(analysis.Findings, f => f.Code == "unused_query" && f.QueryName == "orphan" && f.Severity == "warning");
        }

        [Fact]
        public async Task Analyze_DetectsTemplateWithoutExpressions()
        {
            var odt = await OdtFixture.CreateAsync(OdtFixture.Paragraph("plain text only"));
            var queries = new List<ReportTemplateQuery>
            {
                Query("hdr", QueryType.SingleRow, "select 1 as a from public.x"),
            };

            var analysis = TemplateAnalyzer.Analyze(odt, queries);

            Assert.Contains(analysis.Findings, f => f.Code == "no_expressions" && f.Severity == "error");
        }

        [Fact]
        public async Task Analyze_DetectsDuplicateQueryNames()
        {
            var odt = await OdtFixture.CreateAsync(OdtFixture.Paragraph("{{hdr.a}}"));
            var queries = new List<ReportTemplateQuery>
            {
                Query("hdr", QueryType.SingleRow, "select 1 as a from public.x"),
                Query("hdr", QueryType.SingleRow, "select 2 as a from public.x"),
            };

            var analysis = TemplateAnalyzer.Analyze(odt, queries);

            Assert.Contains(analysis.Findings, f => f.Code == "duplicate_query_name" && f.QueryName == "hdr");
        }

        [Fact]
        public async Task Analyze_RecognizesNamedViewQueries()
        {
            var odt = await OdtFixture.CreateAsync(OdtFixture.Paragraph("{{hdr.a}}"));
            var queries = new List<ReportTemplateQuery>
            {
                Query("hdr", QueryType.SingleRow, "/views/fin/invoice_header"),
            };

            var analysis = TemplateAnalyzer.Analyze(odt, queries);

            var info = Assert.Single(analysis.Queries);
            Assert.Equal("namedView", info.Kind);
            Assert.NotNull(info.NamedView);
            Assert.Equal("fin", info.NamedView!.Schema);
            Assert.Equal("invoice_header", info.NamedView.Name);
        }
    }
}
