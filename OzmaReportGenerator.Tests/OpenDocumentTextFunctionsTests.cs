using System.Collections.Generic;
using System.Threading.Tasks;
using ReportGenerator;
using ReportGenerator.Models;
using ReportGenerator.OzmaDBApi;
using Xunit;

namespace OzmaReportGenerator.Tests
{
    public class OpenDocumentTextFunctionsTests
    {
        [Fact]
        public async Task GetQueriesFromOdt_ReadsNameTypeAndText()
        {
            var odt = await OdtFixture.CreateAsync(
                OdtFixture.Query("hdr", "SingleRow", "select 1 as a from public.x"),
                OdtFixture.Paragraph("{{hdr.a}}"));

            var queries = OpenDocumentTextFunctions.GetQueriesFromOdt(odt);

            var query = Assert.Single(queries);
            Assert.Equal("hdr", query.Name);
            Assert.Equal(QueryType.SingleRow, query.QueryType);
            Assert.Equal("select 1 as a from public.x", query.QueryTextWithoutParameterValues);
        }

        [Fact]
        public async Task RemoveQueriesFromOdt_DropsQueriesButKeepsExpressions()
        {
            var odt = await OdtFixture.CreateAsync(
                OdtFixture.Query("hdr", "SingleRow", "select 1 as a from public.x"),
                OdtFixture.Paragraph("{{hdr.a}}"));

            var stripped = OpenDocumentTextFunctions.RemoveQueriesFromOdt(odt);
            var text = stripped.ReadMainContentXml().DocumentElement!.InnerText;

            Assert.DoesNotContain("<query", text);
            Assert.Contains("{{hdr.a}}", text);
        }

        [Fact]
        public async Task RestoreQueriesInOdt_RoundTripsQueries()
        {
            var odt = await OdtFixture.CreateAsync(
                OdtFixture.Query("rows", "ManyRows", "select id as num from public.y"),
                OdtFixture.Paragraph("{% for row in rows %}{{row.num}}{% endfor %}"));

            var stripped = OpenDocumentTextFunctions.RemoveQueriesFromOdt(odt);
            var stored = new List<ReportTemplateQuery>
            {
                new ReportTemplateQuery
                {
                    Name = "rows",
                    QueryText = "select id as num from public.y",
                    QueryType = (short)QueryType.ManyRows,
                },
            };

            var restored = OpenDocumentTextFunctions.RestoreQueriesInOdt(stripped, stored);
            var again = OpenDocumentTextFunctions.GetQueriesFromOdt(restored);

            var query = Assert.Single(again);
            Assert.Equal("rows", query.Name);
            Assert.Equal(QueryType.ManyRows, query.QueryType);
            Assert.Equal("select id as num from public.y", query.QueryTextWithoutParameterValues);
        }
    }
}
