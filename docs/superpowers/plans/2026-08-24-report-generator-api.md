# Report Template REST API Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Дать отчётному генератору JSON REST API для схем и шаблонов, доступный по Bearer-токену ozmadb, чтобы шаблоны можно было выгружать, править и анализировать программно.

**Architecture:** Новый `TemplateApiController` поверх существующих репозиториев; вся логика разбора ODT вынесена в чистый `TemplateService`, структурный анализ – в `TemplateAnalyzer`, проверка прав – в `IOzmaPermissionsChecker` за фильтром `[OzmaAdmin]`. Админский UI и роут генерации не меняются, схема БД не меняется.

**Tech Stack:** .NET SDK 10.0.103, target `net8.0`, ASP.NET Core MVC, EF Core 8 + Npgsql, MaltReport2 (Sandwych.Reporting) для ODT, xUnit для тестов.

**Spec:** `docs/superpowers/specs/2026-08-24-report-generator-api-design.md`

## Global Constraints

- `global.json` фиксирует SDK `10.0.103` с `rollForward: disable`; target framework проекта – `net8.0`, `LangVersion 10.0`, `Nullable enable`. Тестовый проект использует те же значения.
- CI (`.github/workflows/ozma-report-generator.yml`) выполняет `dotnet restore --locked-mode -r linux-x64`, поэтому у **каждого** проекта в решении должен быть `RestorePackagesWithLockFile=true` и закоммиченный `packages.lock.json`.
- CI выполняет `dotnet format --no-restore --verify-no-changes` по всему репозиторию: после каждой задачи запускать `dotnet format` перед коммитом.
- Стиль существующего кода: блочные `namespace`, четыре пробела, `using`-и сверху файла. Новый код пишется в том же стиле.
- Схема БД (`OzmaReportGenerator/db/db.sql`) не меняется ни в одной задаче.
- Сообщения коммитов – на английском, одной строкой, без тела и без `Co-Authored-By`.
- Все JSON-ошибки API имеют вид `{"error": "<code>", "message": "<text>"}` с кодами `unauthorized`, `forbidden`, `not_found`, `bad_request`, `internal`.

---

### Task 1: Тестовый проект и фикстуры ODT

**Files:**
- Create: `OzmaReportGenerator.Tests/OzmaReportGenerator.Tests.csproj`
- Create: `OzmaReportGenerator.Tests/OdtFixture.cs`
- Create: `OzmaReportGenerator.Tests/OpenDocumentTextFunctionsTests.cs`
- Modify: `OzmaReportGenerator.sln`

**Interfaces:**
- Consumes: существующий `ReportGenerator.OpenDocumentTextFunctions` и `ReportGenerator.Models.ReportTemplateQuery`.
- Produces: `OzmaReportGenerator.Tests.OdtFixture.Create(string bodyXml) -> OdfDocument` и `OdtFixture.Paragraph(string text) -> string` – используются всеми последующими тестами.

- [ ] **Step 1: Создать тестовый проект и подключить его к решению**

```bash
cd /Users/vientooscuro/SyncFolder/ozma-report-generator
dotnet new xunit -o OzmaReportGenerator.Tests -f net8.0
dotnet add OzmaReportGenerator.Tests/OzmaReportGenerator.Tests.csproj reference OzmaReportGenerator/OzmaReportGenerator.csproj
dotnet sln OzmaReportGenerator.sln add OzmaReportGenerator.Tests/OzmaReportGenerator.Tests.csproj
```

- [ ] **Step 2: Привести csproj тестов к требованиям CI**

Открыть `OzmaReportGenerator.Tests/OzmaReportGenerator.Tests.csproj` и убедиться, что `PropertyGroup` содержит именно это (значения `Nullable`, `LangVersion` и `RestorePackagesWithLockFile` добавить, если их нет):

```xml
  <PropertyGroup>
    <TargetFramework>net8.0</TargetFramework>
    <LangVersion>10.0</LangVersion>
    <Nullable>enable</Nullable>
    <IsPackable>false</IsPackable>
    <RestorePackagesWithLockFile>true</RestorePackagesWithLockFile>
  </PropertyGroup>
```

- [ ] **Step 3: Написать хелпер фикстур**

Создать `OzmaReportGenerator.Tests/OdtFixture.cs`. Рецепт проверен: `OdfDocument` требует записи `mimetype`, `META-INF/manifest.xml` и `content.xml`, иначе `SaveAsync` падает с `Entry 'mimetype' not found`.

```csharp
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
            using var stream = new MemoryStream();
            await doc.SaveAsync(stream);
            stream.Position = 0;
            return await OdfDocument.LoadFromAsync(stream);
        }
    }
}
```

- [ ] **Step 4: Написать падающие тесты round-trip запросов**

Создать `OzmaReportGenerator.Tests/OpenDocumentTextFunctionsTests.cs`:

```csharp
using System.Collections.Generic;
using System.Linq;
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
```

- [ ] **Step 5: Запустить тесты**

Run: `dotnet test OzmaReportGenerator.Tests/OzmaReportGenerator.Tests.csproj`
Expected: PASS, три теста. Это характеризационные тесты существующего поведения – они фиксируют контракт, на который опираются последующие задачи. Если какой-то падает, значит фикстура не соответствует реальному формату: чинить фикстуру, а не рабочий код.

- [ ] **Step 6: Сгенерировать lock-файл и проверить сборку решения**

```bash
dotnet restore OzmaReportGenerator.sln
dotnet build OzmaReportGenerator.sln -v q --nologo
```
Expected: `Build succeeded`, файл `OzmaReportGenerator.Tests/packages.lock.json` создан.

- [ ] **Step 7: Форматирование и коммит**

```bash
dotnet format --no-restore
git add OzmaReportGenerator.sln OzmaReportGenerator.Tests
git commit -m "Add test project with ODT fixtures"
```

---

### Task 2: TemplateService – чистая логика разбора шаблона

**Files:**
- Create: `OzmaReportGenerator/Services/TemplateService.cs`
- Create: `OzmaReportGenerator.Tests/TemplateServiceTests.cs`
- Modify: `OzmaReportGenerator/Controllers/AdminController.cs`

**Interfaces:**
- Consumes: `OdtFixture` из задачи 1.
- Produces:
  - `ReportGenerator.Services.ParsedTemplate` с полями `byte[] OdtWithoutQueries` и `IList<ReportTemplateQuery> Queries`.
  - `Task<ParsedTemplate> TemplateService.ParseUploadAsync(Stream odtStream)`
  - `Task<byte[]> TemplateService.RestoreOdtAsync(byte[] odtWithoutQueries, IList<ReportTemplateQuery> queries)`
  - `string TemplateService.SanitizeName(string name)`

- [ ] **Step 1: Написать падающие тесты**

Создать `OzmaReportGenerator.Tests/TemplateServiceTests.cs`:

```csharp
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
            var stream = new MemoryStream();
            await doc.SaveAsync(stream);
            stream.Position = 0;
            return stream;
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
            byte[] withoutQueries;
            using (var stream = new MemoryStream())
            {
                await odt.SaveAsync(stream);
                withoutQueries = stream.ToArray();
            }

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
```

- [ ] **Step 2: Запустить тесты и убедиться, что они падают**

Run: `dotnet test OzmaReportGenerator.Tests/OzmaReportGenerator.Tests.csproj --filter FullyQualifiedName~TemplateServiceTests`
Expected: ошибка компиляции – `ReportGenerator.Services` не существует.

- [ ] **Step 3: Реализовать сервис**

Создать `OzmaReportGenerator/Services/TemplateService.cs`:

```csharp
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
            var unused = new OdtTemplate(odtWithoutQueries);

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
```

- [ ] **Step 4: Запустить тесты**

Run: `dotnet test OzmaReportGenerator.Tests/OzmaReportGenerator.Tests.csproj --filter FullyQualifiedName~TemplateServiceTests`
Expected: PASS, шесть тестов (три `[Fact]` и три случая `[Theory]`).

- [ ] **Step 5: Перевести AdminController на сервис**

В `OzmaReportGenerator/Controllers/AdminController.cs`:

1. Добавить `using ReportGenerator.Services;`.
2. Удалить приватный метод `RemoveRestrictedSymbols` и заменить оба его вызова на `TemplateService.SanitizeName`.
3. В `AddTemplate` заменить блок разбора ODT (от `OdfDocument odtWithQueries;` до цикла `foreach (var query in queries)` включительно) на:

```csharp
            ParsedTemplate parsed;
            try
            {
                await using var stream = new MemoryStream();
                await UploadedOdtFile.CopyToAsync(stream);
                stream.Position = 0;
                parsed = await TemplateService.ParseUploadAsync(stream);
            }
            catch (Exception e)
            {
                logger.LogError(e, "Failed to add template");
                var msg = e.InnerException != null ? e.InnerException.Message : e.Message;
                return StatusCode(500, msg);
            }

            model.OdtWithoutQueries = parsed.OdtWithoutQueries;
            model.Name = TemplateService.SanitizeName(model.Name);
            foreach (var query in parsed.Queries)
            {
                model.ReportTemplateQueries.Add(query);
            }
```

4. В `UpdateTemplateFile` заменить аналогичный блок на:

```csharp
            ParsedTemplate parsed;
            try
            {
                await using var stream = new MemoryStream();
                await UploadedOdtFile.CopyToAsync(stream);
                stream.Position = 0;
                parsed = await TemplateService.ParseUploadAsync(stream);
            }
            catch (Exception e)
            {
                logger.LogError(e, "Failed to update template");
                var msg = e.InnerException != null ? e.InnerException.Message : e.Message;
                return StatusCode(500, msg);
            }

            using (var repository = new ReportTemplateRepository(configuration, instanceName))
            {
                var model = await repository.LoadTemplate(templateId);
                if (model == null) throw new Exception("Template with id=" + templateId + " not found");
                model.ReportTemplateQueries.Clear();
                foreach (var query in parsed.Queries)
                {
                    model.ReportTemplateQueries.Add(query);
                }
                model.OdtWithoutQueries = parsed.OdtWithoutQueries;
                await repository.UpdateTemplate(model);
                return Ok();
            }
```

5. В `DownloadTemplate` заменить ручную сборку ODT на сервис:

```csharp
                var bytes = await TemplateService.RestoreOdtAsync(template.OdtWithoutQueries, template.ReportTemplateQueries);
                return File(bytes, "application/vnd.oasis.opendocument.text", template.Name + ".odt");
```

- [ ] **Step 6: Собрать решение и прогнать тесты**

Run: `dotnet build OzmaReportGenerator.sln -v q --nologo && dotnet test OzmaReportGenerator.Tests/OzmaReportGenerator.Tests.csproj`
Expected: `Build succeeded`, все тесты PASS.

- [ ] **Step 7: Форматирование и коммит**

```bash
dotnet format --no-restore
git add OzmaReportGenerator/Services/TemplateService.cs OzmaReportGenerator/Controllers/AdminController.cs OzmaReportGenerator.Tests/TemplateServiceTests.cs
git commit -m "Extract template parsing into TemplateService"
```

---

### Task 3: TemplateAnalyzer – структурный разбор шаблона

**Files:**
- Create: `OzmaReportGenerator/Services/TemplateAnalyzer.cs`
- Create: `OzmaReportGenerator.Tests/TemplateAnalyzerTests.cs`
- Modify: `OzmaReportGenerator/ReportTemplateFunctions.cs:88-101`

**Interfaces:**
- Consumes: `OdtFixture`, `TemplateService`.
- Produces:
  - `ReportGenerator.Services.TemplateFinding` (`Severity`, `Code`, `QueryName`, `Field`, `Message`)
  - `ReportGenerator.Services.NamedViewRef` (`Schema`, `Name`)
  - `ReportGenerator.Services.TemplateQueryInfo` (`Name`, `Type`, `Kind`, `NamedView`, `Parameters`, `QueryText`)
  - `ReportGenerator.Services.TemplateExpressionInfo` (`QueryName`, `ImpliedType`, `SubQueryName`, `Fields`)
  - `ReportGenerator.Services.TemplateAnalysis` (`Queries`, `Expressions`, `Findings`)
  - `TemplateAnalysis TemplateAnalyzer.Analyze(OdfDocument odtWithoutQueries, IList<ReportTemplateQuery> queries)`
  - `IList<TemplateFinding> TemplateAnalyzer.CheckStructure(IList<TemplateExpression> expressions, IList<OzmaDBQuery> queries)`

- [ ] **Step 1: Написать падающие тесты**

Создать `OzmaReportGenerator.Tests/TemplateAnalyzerTests.cs`:

```csharp
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

            Assert.Empty(analysis.Findings.Where(f => f.Severity == "error"));
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
```

- [ ] **Step 2: Запустить тесты и убедиться, что они падают**

Run: `dotnet test OzmaReportGenerator.Tests/OzmaReportGenerator.Tests.csproj --filter FullyQualifiedName~TemplateAnalyzerTests`
Expected: ошибка компиляции – `TemplateAnalyzer` не существует.

- [ ] **Step 3: Реализовать анализатор**

Создать `OzmaReportGenerator/Services/TemplateAnalyzer.cs`:

```csharp
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using ReportGenerator.Models;
using ReportGenerator.OzmaDBApi;
using Sandwych.Reporting.OpenDocument;

namespace ReportGenerator.Services
{
    public sealed class NamedViewRef
    {
        public NamedViewRef(string schema, string name)
        {
            Schema = schema;
            Name = name;
        }

        public string Schema { get; }
        public string Name { get; }
    }

    public sealed class TemplateQueryInfo
    {
        public string Name { get; set; } = null!;
        public string Type { get; set; } = null!;
        public string Kind { get; set; } = null!;
        public NamedViewRef? NamedView { get; set; }
        public IList<string> Parameters { get; set; } = new List<string>();
        public string QueryText { get; set; } = null!;
    }

    public sealed class TemplateExpressionInfo
    {
        public string QueryName { get; set; } = null!;
        public string ImpliedType { get; set; } = null!;
        public string? SubQueryName { get; set; }
        public IList<string> Fields { get; set; } = new List<string>();
    }

    public sealed class TemplateFinding
    {
        public TemplateFinding(string severity, string code, string? queryName, string? field, string message)
        {
            Severity = severity;
            Code = code;
            QueryName = queryName;
            Field = field;
            Message = message;
        }

        public string Severity { get; }
        public string Code { get; }
        public string? QueryName { get; }
        public string? Field { get; }
        public string Message { get; }
    }

    public sealed class TemplateAnalysis
    {
        public IList<TemplateQueryInfo> Queries { get; set; } = new List<TemplateQueryInfo>();
        public IList<TemplateExpressionInfo> Expressions { get; set; } = new List<TemplateExpressionInfo>();
        public IList<TemplateFinding> Findings { get; set; } = new List<TemplateFinding>();
    }

    public static class TemplateAnalyzer
    {
        private static readonly Regex NamedViewRegex =
            new Regex(@"^/views/(?<schema>[^/]+)/(?<name>[^/]+)$", RegexOptions.Singleline | RegexOptions.Compiled);

        private static readonly Regex ArgumentsBlockRegex =
            new Regex(@"^\s*{(?<args>[^}]*)}\s*:", RegexOptions.Singleline | RegexOptions.Compiled);

        private static readonly Regex ParameterRegex =
            new Regex(@"\$(?<name>[A-Za-z_][A-Za-z0-9_]*)", RegexOptions.Compiled);

        public static TemplateAnalysis Analyze(OdfDocument odtWithoutQueries, IList<ReportTemplateQuery> queries)
        {
            var analysis = new TemplateAnalysis();

            foreach (var query in queries)
            {
                analysis.Queries.Add(Describe(query));
            }

            foreach (var duplicate in queries.GroupBy(q => q.Name).Where(g => g.Count() > 1))
            {
                analysis.Findings.Add(new TemplateFinding(
                    "error", "duplicate_query_name", duplicate.Key, null,
                    "Query '" + duplicate.Key + "' is defined " + duplicate.Count() + " times in the template"));
            }

            var expressions = OpenDocumentTextFunctions.GetTemplateExpressionsFromOdt(odtWithoutQueries);
            foreach (var expression in expressions)
            {
                analysis.Expressions.Add(new TemplateExpressionInfo
                {
                    QueryName = expression.QueryName,
                    ImpliedType = expression.QueryType.ToString(),
                    SubQueryName = expression.SubQueryName,
                    Fields = expression.FieldNames.ToList(),
                });
            }

            if (expressions.Count == 0)
            {
                analysis.Findings.Add(new TemplateFinding(
                    "error", "no_expressions", null, null,
                    "Template contains no {{ }} expressions, nothing will be substituted"));
            }

            var asOzmaQueries = queries
                .Select(q => new OzmaDBQuery(q.Name, q.QueryText, (QueryType)q.QueryType))
                .ToList();
            foreach (var finding in CheckStructure(expressions, asOzmaQueries))
            {
                analysis.Findings.Add(finding);
            }

            foreach (var query in queries)
            {
                if (!expressions.Any(e => e.QueryName == query.Name))
                {
                    analysis.Findings.Add(new TemplateFinding(
                        "warning", "unused_query", query.Name, null,
                        "Query '" + query.Name + "' is never referenced by an expression"));
                }
            }

            return analysis;
        }

        /// <summary>
        /// Checks that every expression maps to a query of a matching type.
        /// Shared with report generation, which throws on the first error instead of collecting.
        /// </summary>
        public static IList<TemplateFinding> CheckStructure(IList<TemplateExpression> expressions, IList<OzmaDBQuery> queries)
        {
            var findings = new List<TemplateFinding>();
            foreach (var expression in expressions)
            {
                var query = queries.FirstOrDefault(q => q.Name == expression.QueryName);
                if (query == null)
                {
                    findings.Add(new TemplateFinding(
                        "error", "unknown_query", expression.QueryName, null,
                        "Expression references query '" + expression.QueryName + "' which is not defined in the template"));
                    continue;
                }
                if (query.QueryType != expression.QueryType)
                {
                    findings.Add(new TemplateFinding(
                        "error", "query_type_mismatch", expression.QueryName, null,
                        "Query '" + expression.QueryName + "' is declared as " + query.QueryType +
                        " but used as " + expression.QueryType + " in the template"));
                }
            }
            return findings;
        }

        private static TemplateQueryInfo Describe(ReportTemplateQuery query)
        {
            var info = new TemplateQueryInfo
            {
                Name = query.Name,
                Type = ((QueryType)query.QueryType).ToString(),
                QueryText = query.QueryText,
                Kind = "funql",
                Parameters = ExtractParameters(query.QueryText),
            };

            var match = NamedViewRegex.Match(query.QueryText.Trim());
            if (match.Success)
            {
                info.Kind = "namedView";
                info.NamedView = new NamedViewRef(match.Groups["schema"].Value, match.Groups["name"].Value);
            }

            return info;
        }

        private static IList<string> ExtractParameters(string queryText)
        {
            var source = queryText;
            var block = ArgumentsBlockRegex.Match(queryText);
            if (block.Success) source = block.Groups["args"].Value;

            var names = new List<string>();
            foreach (Match match in ParameterRegex.Matches(source))
            {
                var name = match.Groups["name"].Value;
                if (!names.Contains(name)) names.Add(name);
            }
            return names;
        }
    }
}
```

- [ ] **Step 4: Запустить тесты**

Run: `dotnet test OzmaReportGenerator.Tests/OzmaReportGenerator.Tests.csproj --filter FullyQualifiedName~TemplateAnalyzerTests`
Expected: PASS, семь тестов.

- [ ] **Step 5: Перевести генерацию на общий анализатор**

В `OzmaReportGenerator/ReportTemplateFunctions.cs` внутри региона `#region syntax check` заменить две первые проверки (поиск `loadedQuery` и сравнение `QueryType`) на вызов анализатора, оставив проверки полей по загруженным данным без изменений:

```csharp
                #region syntax check
                var structureFindings = Services.TemplateAnalyzer.CheckStructure(templateExpressions, loadedQueries);
                var firstError = structureFindings.FirstOrDefault(f => f.Severity == "error");
                if (firstError != null) throw new Exception(firstError.Message);

                foreach (var templateExpression in templateExpressions)
                {
                    var loadedQuery = loadedQueries.First(p => p.Name == templateExpression.QueryName);
```

Дальше по коду остаётся существующий `switch (templateExpression.QueryType)` с проверками `ExpandoObject` и полей.

- [ ] **Step 6: Собрать и прогнать все тесты**

Run: `dotnet build OzmaReportGenerator.sln -v q --nologo && dotnet test OzmaReportGenerator.Tests/OzmaReportGenerator.Tests.csproj`
Expected: `Build succeeded`, все тесты PASS.

- [ ] **Step 7: Форматирование и коммит**

```bash
dotnet format --no-restore
git add OzmaReportGenerator/Services/TemplateAnalyzer.cs OzmaReportGenerator/ReportTemplateFunctions.cs OzmaReportGenerator.Tests/TemplateAnalyzerTests.cs
git commit -m "Add template analyzer and reuse it in report generation"
```

---

### Task 4: Проверка прав за интерфейсом и фильтр OzmaAdmin

**Files:**
- Create: `OzmaReportGenerator/Services/IOzmaPermissionsChecker.cs`
- Create: `OzmaReportGenerator/Services/OzmaPermissionsChecker.cs`
- Create: `OzmaReportGenerator/Filters/OzmaAdminAttribute.cs`
- Create: `OzmaReportGenerator/Models/Api/ApiError.cs`
- Create: `OzmaReportGenerator.Tests/OzmaAdminAttributeTests.cs`
- Modify: `OzmaReportGenerator/TokenProcessor.cs`
- Modify: `OzmaReportGenerator/OzmaDBApi/OzmaDBApiConnector.cs:17-22,38-77`
- Modify: `OzmaReportGenerator/Startup.cs:88`
- Modify: `OzmaReportGenerator/Controllers/AdminController.cs:38-48`

**Interfaces:**
- Consumes: `PermissionsResponse` из `ReportGenerator.OzmaDBApi`.
- Produces:
  - `ReportGenerator.Services.IOzmaPermissionsChecker` с методом `Task<PermissionsResponse?> GetPermissions(HttpContext context, string instanceName)`
  - `ReportGenerator.Filters.OzmaAdminAttribute` – атрибут-фильтр авторизации, читающий `instanceName` из route values
  - `ReportGenerator.Models.Api.ApiError` с полями `Error` и `Message`
  - `bool TokenProcessor.IsFromHeader { get; }`

- [ ] **Step 1: Написать падающие тесты фильтра**

Создать `OzmaReportGenerator.Tests/OzmaAdminAttributeTests.cs`:

```csharp
using System.Collections.Generic;
using System.Net;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Abstractions;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using ReportGenerator.Filters;
using ReportGenerator.Models.Api;
using ReportGenerator.OzmaDBApi;
using ReportGenerator.Services;
using Xunit;

namespace OzmaReportGenerator.Tests
{
    public class OzmaAdminAttributeTests
    {
        private sealed class FakeChecker : IOzmaPermissionsChecker
        {
            private readonly PermissionsResponse? response;

            public FakeChecker(PermissionsResponse? response) => this.response = response;

            public string? LastInstance { get; private set; }

            public Task<PermissionsResponse?> GetPermissions(HttpContext context, string instanceName)
            {
                LastInstance = instanceName;
                return Task.FromResult(response);
            }
        }

        private static AuthorizationFilterContext MakeContext(IOzmaPermissionsChecker checker, string? instanceName)
        {
            var services = new ServiceCollection();
            services.AddSingleton(checker);
            var httpContext = new DefaultHttpContext { RequestServices = services.BuildServiceProvider() };

            var routeData = new RouteData();
            if (instanceName != null) routeData.Values["instanceName"] = instanceName;

            var actionContext = new ActionContext(httpContext, routeData, new ActionDescriptor());
            return new AuthorizationFilterContext(actionContext, new List<IFilterMetadata>());
        }

        [Fact]
        public async Task NoPermissions_Returns401()
        {
            var context = MakeContext(new FakeChecker(null), "gogol");

            await new OzmaAdminAttribute().OnAuthorizationAsync(context);

            var result = Assert.IsType<ObjectResult>(context.Result);
            Assert.Equal(401, result.StatusCode);
            Assert.Equal("unauthorized", Assert.IsType<ApiError>(result.Value).Error);
        }

        [Fact]
        public async Task UnauthorizedFromOzma_Returns401()
        {
            var permissions = new PermissionsResponse { ResponseCode = HttpStatusCode.Unauthorized, IsAdmin = false };
            var context = MakeContext(new FakeChecker(permissions), "gogol");

            await new OzmaAdminAttribute().OnAuthorizationAsync(context);

            var result = Assert.IsType<ObjectResult>(context.Result);
            Assert.Equal(401, result.StatusCode);
        }

        [Fact]
        public async Task NotAdmin_Returns403()
        {
            var permissions = new PermissionsResponse { ResponseCode = HttpStatusCode.OK, IsAdmin = false };
            var context = MakeContext(new FakeChecker(permissions), "gogol");

            await new OzmaAdminAttribute().OnAuthorizationAsync(context);

            var result = Assert.IsType<ObjectResult>(context.Result);
            Assert.Equal(403, result.StatusCode);
            Assert.Equal("forbidden", Assert.IsType<ApiError>(result.Value).Error);
        }

        [Fact]
        public async Task Admin_PassesThrough()
        {
            var permissions = new PermissionsResponse { ResponseCode = HttpStatusCode.OK, IsAdmin = true };
            var checker = new FakeChecker(permissions);
            var context = MakeContext(checker, "gogol");

            await new OzmaAdminAttribute().OnAuthorizationAsync(context);

            Assert.Null(context.Result);
            Assert.Equal("gogol", checker.LastInstance);
        }

        [Fact]
        public async Task MissingInstanceName_Returns400()
        {
            var permissions = new PermissionsResponse { ResponseCode = HttpStatusCode.OK, IsAdmin = true };
            var context = MakeContext(new FakeChecker(permissions), null);

            await new OzmaAdminAttribute().OnAuthorizationAsync(context);

            var result = Assert.IsType<ObjectResult>(context.Result);
            Assert.Equal(400, result.StatusCode);
        }
    }
}
```

- [ ] **Step 2: Запустить тесты и убедиться, что они падают**

Run: `dotnet test OzmaReportGenerator.Tests/OzmaReportGenerator.Tests.csproj --filter FullyQualifiedName~OzmaAdminAttributeTests`
Expected: ошибка компиляции – `ReportGenerator.Filters` и `ReportGenerator.Models.Api` не существуют.

- [ ] **Step 3: Добавить DTO ошибки**

Создать `OzmaReportGenerator/Models/Api/ApiError.cs`:

```csharp
namespace ReportGenerator.Models.Api
{
    public sealed class ApiError
    {
        public ApiError(string error, string message)
        {
            Error = error;
            Message = message;
        }

        public string Error { get; }
        public string Message { get; }
    }
}
```

- [ ] **Step 4: Пометить токены, пришедшие заголовком**

В `OzmaReportGenerator/TokenProcessor.cs` добавить свойство и выставлять его в `Create`:

1. В список свойств добавить `public bool IsFromHeader { get; private set; }`.
2. Приватный конструктор дополнить параметром `bool isFromHeader` и присвоением `IsFromHeader = isFromHeader;`.
3. В `Create` вызывать `new TokenProcessor(configuration, httpContext, accessToken[1], true)` для ветки заголовка и `new TokenProcessor(configuration, httpContext, accessTokenFromIdentity.Value, false)` для ветки cookie-identity.

Это нужно, потому что `GetPermissions` при 401 пытается обновить токен через refresh-клейм, которого у API-клиента нет, и падает с `Refresh token not found in HttpContext` вместо честного 401.

- [ ] **Step 5: Научить коннектор не обновлять токен**

В `OzmaReportGenerator/OzmaDBApi/OzmaDBApiConnector.cs`:

1. Добавить поле `private readonly bool allowRefresh;` и необязательный параметр конструктора:

```csharp
        public OzmaDBApiConnector(IConfiguration configuration, string instanceName, TokenProcessor tokenProcessor, bool allowRefresh = true)
        {
            this.configuration = configuration;
            this.tokenProcessor = tokenProcessor;
            this.instanceName = instanceName;
            this.allowRefresh = allowRefresh;
        }
```

2. В `GetPermissions` в ветке `case System.Net.HttpStatusCode.Unauthorized:` первой строкой добавить:

```csharp
                    if (!allowRefresh) break;
```

- [ ] **Step 6: Реализовать чекер прав**

Создать `OzmaReportGenerator/Services/IOzmaPermissionsChecker.cs`:

```csharp
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using ReportGenerator.OzmaDBApi;

namespace ReportGenerator.Services
{
    public interface IOzmaPermissionsChecker
    {
        /// <summary>Returns null when no usable access token is present in the request.</summary>
        Task<PermissionsResponse?> GetPermissions(HttpContext context, string instanceName);
    }
}
```

Создать `OzmaReportGenerator/Services/OzmaPermissionsChecker.cs`:

```csharp
using System.Security.Authentication;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using ReportGenerator.OzmaDBApi;

namespace ReportGenerator.Services
{
    public sealed class OzmaPermissionsChecker : IOzmaPermissionsChecker
    {
        private readonly IConfiguration configuration;

        public OzmaPermissionsChecker(IConfiguration configuration)
        {
            this.configuration = configuration;
        }

        public async Task<PermissionsResponse?> GetPermissions(HttpContext context, string instanceName)
        {
            TokenProcessor tokenProcessor;
            try
            {
                tokenProcessor = TokenProcessor.Create(configuration, context);
            }
            catch (AuthenticationException)
            {
                return null;
            }

            var connector = new OzmaDBApiConnector(configuration, instanceName, tokenProcessor, !tokenProcessor.IsFromHeader);
            return await connector.GetPermissions();
        }
    }
}
```

- [ ] **Step 7: Реализовать фильтр**

Создать `OzmaReportGenerator/Filters/OzmaAdminAttribute.cs`:

```csharp
using System;
using System.Net;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.Extensions.DependencyInjection;
using ReportGenerator.Models.Api;
using ReportGenerator.Services;

namespace ReportGenerator.Filters
{
    /// <summary>
    /// Authorizes API requests with an OzmaDB access token instead of the cookie/OIDC scheme,
    /// so that machine clients get a JSON error rather than a login redirect.
    /// </summary>
    [AttributeUsage(AttributeTargets.Class | AttributeTargets.Method)]
    public sealed class OzmaAdminAttribute : Attribute, IAsyncAuthorizationFilter
    {
        public async Task OnAuthorizationAsync(AuthorizationFilterContext context)
        {
            if (!context.RouteData.Values.TryGetValue("instanceName", out var raw)
                || raw is not string instanceName
                || string.IsNullOrEmpty(instanceName))
            {
                context.Result = new ObjectResult(new ApiError("bad_request", "No instance name in route")) { StatusCode = 400 };
                return;
            }

            var checker = context.HttpContext.RequestServices.GetRequiredService<IOzmaPermissionsChecker>();
            var permissions = await checker.GetPermissions(context.HttpContext, instanceName);

            if (permissions == null || permissions.ResponseCode == HttpStatusCode.Unauthorized)
            {
                context.Result = new ObjectResult(new ApiError("unauthorized", "Valid OzmaDB access token required")) { StatusCode = 401 };
                return;
            }

            if (!permissions.IsAdmin)
            {
                context.Result = new ObjectResult(new ApiError("forbidden", "User has no admin rights for instance " + instanceName)) { StatusCode = 403 };
            }
        }
    }
}
```

- [ ] **Step 8: Зарегистрировать чекер в DI и перевести на него AdminController**

1. В `OzmaReportGenerator/Startup.cs` в `ConfigureServices` рядом с `services.AddControllersWithViews();` добавить:

```csharp
            services.AddSingleton<Services.IOzmaPermissionsChecker, Services.OzmaPermissionsChecker>();
            services.AddMemoryCache();
```

2. В `OzmaReportGenerator/Controllers/AdminController.cs` заменить приватный `HasPermissionsForInstance` на вызов чекера: добавить в конструктор параметр `IOzmaPermissionsChecker permissionsChecker`, сохранить в поле и переписать метод:

```csharp
        private async Task<PermissionsResponse?> HasPermissionsForInstance(string instanceName)
        {
            return await permissionsChecker.GetPermissions(HttpContext, instanceName);
        }
```

Метод `CreateTokenProcessor` в `BaseController` остаётся: им пользуется `GenerateController`.

- [ ] **Step 9: Запустить тесты и сборку**

Run: `dotnet build OzmaReportGenerator.sln -v q --nologo && dotnet test OzmaReportGenerator.Tests/OzmaReportGenerator.Tests.csproj`
Expected: `Build succeeded`, все тесты PASS, включая пять новых.

- [ ] **Step 10: Форматирование и коммит**

```bash
dotnet format --no-restore
git add OzmaReportGenerator/Services OzmaReportGenerator/Filters OzmaReportGenerator/Models/Api OzmaReportGenerator/TokenProcessor.cs OzmaReportGenerator/OzmaDBApi/OzmaDBApiConnector.cs OzmaReportGenerator/Startup.cs OzmaReportGenerator/Controllers/AdminController.cs OzmaReportGenerator.Tests/OzmaAdminAttributeTests.cs
git commit -m "Add token-based admin authorization filter for the API"
```

---

### Task 5: Эндпоинт GET /api/instances

**Files:**
- Create: `OzmaReportGenerator/Repositories/DatabaseBootstrap.cs`
- Create: `OzmaReportGenerator/Repositories/InstanceRepository.cs`
- Create: `OzmaReportGenerator/Controllers/InstancesApiController.cs`
- Modify: `OzmaReportGenerator/Repositories/Repository.cs:29-48`

**Interfaces:**
- Consumes: `IOzmaPermissionsChecker`, `ApiError`.
- Produces:
  - `ReportGenerator.Repositories.DatabaseBootstrap.EnsureSchema(ReportGeneratorContext dbContext)` – создаёт таблицы из `db/db.sql`, если их нет
  - `ReportGenerator.Repositories.InstanceRepository.LoadAllInstanceNames() -> Task<List<string>>`
  - маршрут `GET /api/instances`, ответ `{"instances": ["<name>", ...]}`

- [ ] **Step 1: Вынести бутстрап схемы в отдельный класс**

Создать `OzmaReportGenerator/Repositories/DatabaseBootstrap.cs`:

```csharp
using System;
using System.IO;
using Microsoft.EntityFrameworkCore;
using ReportGenerator.Models;

namespace ReportGenerator.Repositories
{
    public static class DatabaseBootstrap
    {
        /// <summary>Applies db/db.sql when the tables are missing (PostgreSQL error 42P01).</summary>
        public static void ApplySchemaScript(ReportGeneratorContext dbContext)
        {
            var dbScript = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "db", "db.sql");
            var dbScriptContent = File.ReadAllText(dbScript);
            dbContext.Database.ExecuteSqlRaw(dbScriptContent);
        }
    }
}
```

В `OzmaReportGenerator/Repositories/Repository.cs` в блоке `catch (DbException e)` заменить три строки чтения и выполнения скрипта на `DatabaseBootstrap.ApplySchemaScript(dbContext);`, оставив остальную логику как есть.

- [ ] **Step 2: Добавить репозиторий инстансов**

Создать `OzmaReportGenerator/Repositories/InstanceRepository.cs`:

```csharp
using System;
using System.Collections.Generic;
using System.Data.Common;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using ReportGenerator.Models;

namespace ReportGenerator.Repositories
{
    /// <summary>Reads the instance list without binding to a particular instance.</summary>
    public sealed class InstanceRepository : IDisposable
    {
        private readonly ReportGeneratorContext dbContext;

        public InstanceRepository(IConfiguration configuration)
        {
            dbContext = new ReportGeneratorContext(configuration);
        }

        public async Task<List<string>> LoadAllInstanceNames()
        {
            try
            {
                return await dbContext.Instances.AsNoTracking().Select(p => p.Name).ToListAsync();
            }
            catch (DbException e) when (e.SqlState == "42P01")
            {
                DatabaseBootstrap.ApplySchemaScript(dbContext);
                return await dbContext.Instances.AsNoTracking().Select(p => p.Name).ToListAsync();
            }
        }

        public void Dispose()
        {
            dbContext.Dispose();
        }
    }
}
```

- [ ] **Step 3: Реализовать контроллер**

Создать `OzmaReportGenerator/Controllers/InstancesApiController.cs`:

```csharp
using System;
using System.Collections.Generic;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using ReportGenerator.Models.Api;
using ReportGenerator.Repositories;
using ReportGenerator.Services;

namespace ReportGenerator.Controllers
{
    [ApiController]
    public sealed class InstancesApiController : ControllerBase
    {
        private readonly IConfiguration configuration;
        private readonly IOzmaPermissionsChecker permissionsChecker;
        private readonly IMemoryCache cache;
        private readonly ILogger<InstancesApiController> logger;

        public InstancesApiController(
            IConfiguration configuration,
            IOzmaPermissionsChecker permissionsChecker,
            IMemoryCache cache,
            ILogger<InstancesApiController> logger)
        {
            this.configuration = configuration;
            this.permissionsChecker = permissionsChecker;
            this.cache = cache;
            this.logger = logger;
        }

        [HttpGet]
        [Route("api/instances")]
        public async Task<IActionResult> GetInstances()
        {
            try
            {
                var names = new List<string>();
                var forced = configuration.GetValue<string>("OzmaDBSettings:ForceInstance");
                if (!string.IsNullOrEmpty(forced))
                {
                    names.Add(forced);
                }
                else
                {
                    using var repository = new InstanceRepository(configuration);
                    names = await repository.LoadAllInstanceNames();
                }

                var allowed = new List<string>();
                foreach (var name in names)
                {
                    if (await IsAdminFor(name)) allowed.Add(name);
                }
                return Ok(new { instances = allowed });
            }
            catch (Exception e)
            {
                logger.LogError(e, "Failed to list instances");
                var msg = e.InnerException != null ? e.InnerException.Message : e.Message;
                return StatusCode(500, new ApiError("internal", msg));
            }
        }

        private async Task<bool> IsAdminFor(string instanceName)
        {
            var cacheKey = "instance-admin:" + instanceName + ":" + TokenFingerprint();
            if (cache.TryGetValue(cacheKey, out bool cached)) return cached;

            var permissions = await permissionsChecker.GetPermissions(HttpContext, instanceName);
            var isAdmin = permissions != null
                          && permissions.ResponseCode != HttpStatusCode.Unauthorized
                          && permissions.IsAdmin;
            cache.Set(cacheKey, isAdmin, TimeSpan.FromSeconds(60));
            return isAdmin;
        }

        private string TokenFingerprint()
        {
            var header = Request.Headers["Authorization"].ToString();
            if (string.IsNullOrEmpty(header)) header = Request.Cookies[".AspNetCore.Cookies"] ?? "";
            var hash = SHA256.HashData(Encoding.UTF8.GetBytes(header));
            return Convert.ToHexString(hash, 0, 8);
        }
    }
}
```

- [ ] **Step 4: Собрать и прогнать тесты**

Run: `dotnet build OzmaReportGenerator.sln -v q --nologo && dotnet test OzmaReportGenerator.Tests/OzmaReportGenerator.Tests.csproj`
Expected: `Build succeeded`, все тесты PASS.

- [ ] **Step 5: Форматирование и коммит**

```bash
dotnet format --no-restore
git add OzmaReportGenerator/Repositories OzmaReportGenerator/Controllers/InstancesApiController.cs
git commit -m "Add instances listing endpoint"
```

---

### Task 6: API схем и чтение шаблонов

**Files:**
- Create: `OzmaReportGenerator/Models/Api/TemplateDtos.cs`
- Create: `OzmaReportGenerator/Controllers/TemplateApiController.cs`
- Test: ручная проверка (см. шаг 4); юнит-тестами покрыты сервисы, а не контроллер – он тонкий и работает с живой БД

**Interfaces:**
- Consumes: `TemplateService`, `TemplateAnalyzer`, `OzmaAdminAttribute`, `ApiError`, репозитории `ReportTemplateRepository` и `ReportTemplateSchemaRepository`.
- Produces:
  - `ReportGenerator.Models.Api.SchemaDto` (`Id`, `Name`)
  - `ReportGenerator.Models.Api.CreateSchemaRequest` (`Name`)
  - `ReportGenerator.Models.Api.TemplateSummaryDto` (`Id`, `SchemaId`, `SchemaName`, `Name`, `QueryCount`)
  - `ReportGenerator.Models.Api.TemplateQueryDto` (`Id`, `Name`, `Type`, `QueryText`)
  - `ReportGenerator.Models.Api.TemplateDto` (`Id`, `Name`, `SchemaId`, `SchemaName`, `Queries`)
  - `ReportGenerator.Models.Api.UpdateQueryRequest` (`QueryText`)
  - маршруты `GET/POST /api/{instanceName}/schemas`, `DELETE /api/{instanceName}/schemas/{id}`, `GET /api/{instanceName}/templates`, `GET /api/{instanceName}/templates/{id}`

- [ ] **Step 1: Добавить DTO**

Создать `OzmaReportGenerator/Models/Api/TemplateDtos.cs`:

```csharp
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
```

- [ ] **Step 2: Реализовать контроллер со схемами и чтением шаблонов**

Создать `OzmaReportGenerator/Controllers/TemplateApiController.cs`:

```csharp
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using ReportGenerator.Filters;
using ReportGenerator.Models;
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
                var schema = new ReportTemplateSchema { Name = TemplateService.SanitizeName(request.Name) };
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
                var result = new List<TemplateSummaryDto>();
                foreach (var template in templates)
                {
                    if (!string.IsNullOrEmpty(schema) && template.Schema.Name != schema) continue;
                    var full = await repository.LoadTemplate(template.Id);
                    result.Add(new TemplateSummaryDto
                    {
                        Id = template.Id,
                        SchemaId = template.SchemaId,
                        SchemaName = template.Schema.Name,
                        Name = template.Name,
                        QueryCount = full?.ReportTemplateQueries.Count ?? 0,
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
```

- [ ] **Step 3: Собрать и прогнать тесты**

Run: `dotnet build OzmaReportGenerator.sln -v q --nologo && dotnet test OzmaReportGenerator.Tests/OzmaReportGenerator.Tests.csproj`
Expected: `Build succeeded`, все тесты PASS.

- [ ] **Step 4: Проверить, что роуты не конфликтуют с генерацией**

Запустить приложение локально не требуется: проверить сопоставление маршрутов логически и убедиться, что роут генерации
(`api/{instanceName}/{schemaName}/{templateName}/generate/{fileName}.{format}`) требует шесть сегментов и литерал `generate`,
а новые роуты – три или четыре сегмента с литералами `schemas`/`templates`. Конфликта нет.

- [ ] **Step 5: Форматирование и коммит**

```bash
dotnet format --no-restore
git add OzmaReportGenerator/Models/Api/TemplateDtos.cs OzmaReportGenerator/Controllers/TemplateApiController.cs
git commit -m "Add schemas and templates read API"
```

---

### Task 7: Файловые операции с шаблонами

**Files:**
- Modify: `OzmaReportGenerator/Controllers/TemplateApiController.cs`

**Interfaces:**
- Consumes: `TemplateService.ParseUploadAsync`, `TemplateService.RestoreOdtAsync`.
- Produces: маршруты `GET /api/{instanceName}/templates/{id}/file`, `POST /api/{instanceName}/templates`, `PUT /api/{instanceName}/templates/{id}/file`, `DELETE /api/{instanceName}/templates/{id}`.

- [ ] **Step 1: Добавить методы в контроллер**

Дописать в `TemplateApiController` (в конец класса) следующие методы:

```csharp
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
                await using var stream = new System.IO.MemoryStream();
                await file.CopyToAsync(stream);
                stream.Position = 0;
                parsed = await TemplateService.ParseUploadAsync(stream);
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

                var model = new ReportTemplate
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
                await using var stream = new System.IO.MemoryStream();
                await file.CopyToAsync(stream);
                stream.Position = 0;
                parsed = await TemplateService.ParseUploadAsync(stream);
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

                return Ok(new TemplateSummaryDto
                {
                    Id = model.Id,
                    SchemaId = model.SchemaId,
                    SchemaName = "",
                    Name = model.Name,
                    QueryCount = model.ReportTemplateQueries.Count,
                });
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
            catch (Exception e)
            {
                return Failure(e, "Failed to delete template");
            }
        }
```

Добавить в начало файла `using Microsoft.AspNetCore.Http;` (для `IFormFile`).

- [ ] **Step 2: Собрать и прогнать тесты**

Run: `dotnet build OzmaReportGenerator.sln -v q --nologo && dotnet test OzmaReportGenerator.Tests/OzmaReportGenerator.Tests.csproj`
Expected: `Build succeeded`, все тесты PASS.

- [ ] **Step 3: Форматирование и коммит**

```bash
dotnet format --no-restore
git add OzmaReportGenerator/Controllers/TemplateApiController.cs
git commit -m "Add template file endpoints to the API"
```

---

### Task 8: Правка запроса и анализ шаблона

**Files:**
- Modify: `OzmaReportGenerator/Controllers/TemplateApiController.cs`
- Modify: `OzmaReportGenerator/Repositories/ReportTemplateRepository.cs`

**Interfaces:**
- Consumes: `TemplateAnalyzer.Analyze`.
- Produces:
  - `ReportTemplateRepository.UpdateQueryText(int templateId, int queryId, string queryText) -> Task<bool>` – `false`, если запрос не найден
  - маршруты `PUT /api/{instanceName}/templates/{id}/queries/{queryId}` и `POST /api/{instanceName}/templates/{id}/analyze`

- [ ] **Step 1: Добавить метод репозитория**

В `OzmaReportGenerator/Repositories/ReportTemplateRepository.cs` дописать:

```csharp
        public async Task<bool> UpdateQueryText(int templateId, int queryId, string queryText)
        {
            var template = await dbContext.ReportTemplates.Include(p => p.ReportTemplateQueries)
                .FirstOrDefaultAsync(p => (p.Schema.InstanceId == instance.Id) && (p.Id == templateId));
            if (template == null) return false;

            var query = template.ReportTemplateQueries.FirstOrDefault(p => p.Id == queryId);
            if (query == null) return false;

            query.QueryText = queryText;
            await dbContext.SaveChangesAsync();
            return true;
        }
```

- [ ] **Step 2: Добавить эндпоинты в контроллер**

Дописать в `TemplateApiController`:

```csharp
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
                await using (var stream = new System.IO.MemoryStream(template.OdtWithoutQueries))
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
            catch (Exception e)
            {
                return Failure(e, "Failed to analyze template");
            }
        }
```

- [ ] **Step 3: Собрать и прогнать тесты**

Run: `dotnet build OzmaReportGenerator.sln -v q --nologo && dotnet test OzmaReportGenerator.Tests/OzmaReportGenerator.Tests.csproj`
Expected: `Build succeeded`, все тесты PASS.

- [ ] **Step 4: Форматирование и коммит**

```bash
dotnet format --no-restore
git add OzmaReportGenerator/Controllers/TemplateApiController.cs OzmaReportGenerator/Repositories/ReportTemplateRepository.cs
git commit -m "Add query update and template analysis endpoints"
```

---

### Task 9: Документация API

**Files:**
- Create: `docs/api.md`

**Interfaces:**
- Consumes: все маршруты из задач 5–8.
- Produces: справочник по API для клиентов, на который ссылается план MCP.

- [ ] **Step 1: Написать справочник**

Создать `docs/api.md` с описанием всех маршрутов из задач 5–8: метод, путь, параметры, пример запроса `curl` с заголовком
`Authorization: Bearer <ozmadb token>`, пример успешного ответа и таблица кодов ошибок
(`unauthorized`, `forbidden`, `not_found`, `bad_request`, `internal`). Для `/analyze` привести полный пример ответа
из спеки (раздел «Контракты»).

- [ ] **Step 2: Коммит**

```bash
git add docs/api.md
git commit -m "Document report template API"
```

---

### Task 10: Проверка на боевом стенде

**Files:**
- Ничего не меняется; задача проверочная.

**Interfaces:**
- Consumes: развёрнутый экземпляр API.

- [ ] **Step 1: Собрать релизную сборку**

Run: `dotnet publish -c Release -o out OzmaReportGenerator/OzmaReportGenerator.csproj`
Expected: сборка успешна.

- [ ] **Step 2: Прогнать проверки, которые выполняет CI**

```bash
dotnet restore --locked-mode -r linux-x64
dotnet format --no-restore --verify-no-changes
```
Expected: обе команды завершаются без ошибок. Если `--locked-mode` ругается на отсутствующий lock-файл тестового проекта,
выполнить `dotnet restore OzmaReportGenerator.sln` и закоммитить `OzmaReportGenerator.Tests/packages.lock.json`.

- [ ] **Step 3: Зафиксировать результат**

Если что-то в шагах 1–2 потребовало правок – закоммитить их одним коммитом:

```bash
git add -A
git commit -m "Fix CI checks for the template API"
```

Развёртывание на `ozma.gogol.school` выполняется отдельно от этого плана; ручная проверка API против живого стенда
описана в плане MCP (`2026-08-24-mcp-report-tools.md`, последняя задача).
