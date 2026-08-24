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
