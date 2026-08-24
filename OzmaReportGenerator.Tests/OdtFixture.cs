using System.IO;
using System.Threading.Tasks;
using Sandwych.Reporting.OpenDocument;

namespace OzmaReportGenerator.Tests
{
    public static class OdtFixture
    {
        private const string Manifest =
            "<?xml version=\"1.0\" encoding=\"UTF-8\"?>" +
            "<manifest:manifest xmlns:manifest=\"urn:oasis:names:tc:opendocument:xmlns:manifest:1.0\" manifest:version=\"1.2\">" +
            "<manifest:file-entry manifest:full-path=\"/\" manifest:media-type=\"application/vnd.oasis.opendocument.text\"/>" +
            "<manifest:file-entry manifest:full-path=\"content.xml\" manifest:media-type=\"text/xml\"/>" +
            "</manifest:manifest>";

        /// <summary>Escapes text so it survives as ODF paragraph content.</summary>
        public static string Paragraph(string text)
        {
            var escaped = text.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;");
            return "<text:p>" + escaped + "</text:p>";
        }

        public static string Query(string name, string type, string queryText)
        {
            return Paragraph("<query name=\"" + name + "\" type=\"" + type + "\">" + queryText + "</query>");
        }

        /// <summary>Builds a minimal valid ODT whose office:text contains the given paragraphs.</summary>
        public static async Task<OdfDocument> CreateAsync(params string[] paragraphs)
        {
            var content =
                "<?xml version=\"1.0\" encoding=\"UTF-8\"?>" +
                "<office:document-content " +
                "xmlns:office=\"urn:oasis:names:tc:opendocument:xmlns:office:1.0\" " +
                "xmlns:text=\"urn:oasis:names:tc:opendocument:xmlns:text:1.0\" office:version=\"1.2\">" +
                "<office:body><office:text>" + string.Join("", paragraphs) + "</office:text></office:body>" +
                "</office:document-content>";

            var doc = new OdfDocument();
            doc.WriteTextEntry("mimetype", "application/vnd.oasis.opendocument.text");
            doc.WriteTextEntry("META-INF/manifest.xml", Manifest);
            doc.WriteTextEntry(doc.MainContentEntryPath, content);

            // Round-trip through a stream so the document behaves exactly like an uploaded file.
            // SaveAsync closes the stream it writes to, so the bytes are re-wrapped for loading.
            using var output = new MemoryStream();
            await doc.SaveAsync(output);
            using var input = new MemoryStream(output.ToArray());
            return await OdfDocument.LoadFromAsync(input);
        }

        public static async Task<byte[]> BytesAsync(OdfDocument doc)
        {
            using var stream = new MemoryStream();
            await doc.SaveAsync(stream);
            return stream.ToArray();
        }
    }
}
