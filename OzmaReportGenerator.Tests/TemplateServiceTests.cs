using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using ReportGenerator.Models;
using ReportGenerator.OzmaDBApi;
using ReportGenerator.Services;
using Sandwych.Reporting.OpenDocument;
using Xunit;

namespace OzmaReportGenerator.Tests
{
    public class TemplateServiceTests
    {
        private static async Task<Stream> ToStreamAsync(OdfDocument doc)
        {
            return new MemoryStream(await OdtFixture.BytesAsync(doc));
        }

        [Fact]
        public async Task ParseUploadAsync_ExtractsQueriesAndStripsThemFromDocument()
        {
            var odt = await OdtFixture.CreateAsync(
                OdtFixture.Query("hdr", "SingleRow", "select 1 as a from public.x"),
                OdtFixture.Paragraph("{{hdr.a}}"));

            var parsed = await TemplateService.ParseUploadAsync(await ToStreamAsync(odt));

            var query = Assert.Single(parsed.Queries);
            Assert.Equal("hdr", query.Name);
            Assert.Equal((short)QueryType.SingleRow, query.QueryType);

            using var stored = new MemoryStream(parsed.OdtWithoutQueries);
            var reloaded = await OdfDocument.LoadFromAsync(stored);
            var text = reloaded.ReadMainContentXml().DocumentElement!.InnerText;
            Assert.DoesNotContain("<query", text);
            Assert.Contains("{{hdr.a}}", text);
        }

        [Fact]
        public async Task ParseUploadAsync_ThrowsOnGarbageInput()
        {
            using var garbage = new MemoryStream(new byte[] { 1, 2, 3, 4 });

            await Assert.ThrowsAnyAsync<System.Exception>(
                () => TemplateService.ParseUploadAsync(garbage));
        }

        [Fact]
        public async Task RestoreOdtAsync_PutsQueriesBackIntoDocument()
        {
            var odt = await OdtFixture.CreateAsync(OdtFixture.Paragraph("{{hdr.a}}"));
            var withoutQueries = await OdtFixture.BytesAsync(odt);

            var queries = new List<ReportTemplateQuery>
            {
                new ReportTemplateQuery
                {
                    Name = "hdr",
                    QueryText = "select 1 as a from public.x",
                    QueryType = (short)QueryType.SingleRow,
                },
            };

            var restored = await TemplateService.RestoreOdtAsync(withoutQueries, queries);

            using var restoredStream = new MemoryStream(restored);
            var reloaded = await OdfDocument.LoadFromAsync(restoredStream);
            var text = reloaded.ReadMainContentXml().DocumentElement!.InnerText;
            Assert.Contains("<query name=\"hdr\" type=\"SingleRow\">", text);
        }

        [Theory]
        [InlineData("my name", "myname")]
        [InlineData("a/b", "ab")]
        [InlineData("a__b", "ab")]
        public void SanitizeName_RemovesRestrictedSymbols(string input, string expected)
        {
            Assert.Equal(expected, TemplateService.SanitizeName(input));
        }
    }
}
