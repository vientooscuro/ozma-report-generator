using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using ReportGenerator.Models;
using ReportGenerator.OzmaDBApi;
using Sandwych.Reporting.OpenDocument;

namespace ReportGenerator.Services
{
    public sealed class ParsedTemplate
    {
        public ParsedTemplate(byte[] odtWithoutQueries, IList<ReportTemplateQuery> queries)
        {
            OdtWithoutQueries = odtWithoutQueries;
            Queries = queries;
        }

        public byte[] OdtWithoutQueries { get; }
        public IList<ReportTemplateQuery> Queries { get; }
    }

    /// <summary>
    /// Pure ODT handling shared by the admin UI and the REST API: no database, no HTTP.
    /// </summary>
    public static class TemplateService
    {
        public static async Task<ParsedTemplate> ParseUploadAsync(Stream odtStream)
        {
            var odtWithQueries = await OdfDocument.LoadFromAsync(odtStream);
            var queries = OpenDocumentTextFunctions.GetQueriesFromOdt(odtWithQueries);
            var odtWithoutQueries = OpenDocumentTextFunctions.RemoveQueriesFromOdt(odtWithQueries);

            // Throws when the stripped document is not a renderable template.
            _ = new OdtTemplate(odtWithoutQueries);

            byte[] bytes;
            await using (var stream = new MemoryStream())
            {
                await odtWithoutQueries.SaveAsync(stream);
                bytes = stream.ToArray();
            }

            var stored = new List<ReportTemplateQuery>();
            foreach (var query in queries)
            {
                stored.Add(new ReportTemplateQuery
                {
                    Name = query.Name,
                    QueryText = query.QueryTextWithoutParameterValues,
                    QueryType = (short)query.QueryType,
                });
            }

            return new ParsedTemplate(bytes, stored);
        }

        public static async Task<byte[]> RestoreOdtAsync(byte[] odtWithoutQueries, IList<ReportTemplateQuery> queries)
        {
            OdfDocument odt;
            await using (var stream = new MemoryStream(odtWithoutQueries))
                odt = await OdfDocument.LoadFromAsync(stream);

            OpenDocumentTextFunctions.RestoreQueriesInOdt(odt, queries);

            await using (var stream = new MemoryStream())
            {
                await odt.SaveAsync(stream);
                return stream.ToArray();
            }
        }

        public static string SanitizeName(string name)
        {
            return name.Replace(" ", "").Replace("/", "").Replace("__", "");
        }
    }
}
