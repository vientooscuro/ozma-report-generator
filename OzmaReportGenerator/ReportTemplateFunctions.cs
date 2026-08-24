using System;
using System.Collections.Generic;
using System.Dynamic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using ReportGenerator.OzmaDBApi;
using ReportGenerator.Models;
using Sandwych.Reporting;
using Sandwych.Reporting.OpenDocument;
using SkiaSharp;
using TemplateContext = Sandwych.Reporting.TemplateContext;

namespace ReportGenerator
{
    public static class ReportTemplateFunctions
    {
        private const int defaultImageHeight = 35;

        private static List<OzmaDBQuery> GetQueriesAsOzmaDB(ReportTemplate template)
        {
            var result = new List<OzmaDBQuery>();
            if ((template.ReportTemplateQueries != null) && (template.ReportTemplateQueries.Any()))
            {
                foreach (var query in template.ReportTemplateQueries)
                {
                    var ozmaDbQuery = new OzmaDBQuery(query.Name, query.QueryText, (QueryType)query.QueryType);
                    result.Add(ozmaDbQuery);
                }
            }
            return result;
        }

        private static async Task<OdfDocument?> PrepareOdtWithoutQueries(ReportTemplate template)
        {
            OdfDocument? odt = null;
            await using (var stream = new MemoryStream(template.OdtWithoutQueries))
            {
                odt = await OdfDocument.LoadFromAsync(stream);
            }
            return odt;
        }

        public static async Task<OdfDocument?> GenerateReport(OzmaDBApiConnector ozmaDbApiConnector, ReportTemplate template, Dictionary<string, object> parametersWithValues)
        {
            //var parameters = GetParameters(template);
            //foreach (var parameterName in parameters.Select(p => p.Key))
            //{
            //    var passedParameterWithValue = parametersWithValues.FirstOrDefault(p => p.Key == parameterName);
            //    if ((passedParameterWithValue.Key == null) || (passedParameterWithValue.Value == null))
            //    {
            //        throw new Exception("No value passed for required parameter $" + parameterName);
            //    }
            //}

            OdfDocument? result = null;
            var odtWithoutQueries = await PrepareOdtWithoutQueries(template);
            if (odtWithoutQueries == null) throw new Exception("Template odt is null");

            var queriesFromOdt = GetQueriesAsOzmaDB(template);
            if (!queriesFromOdt.Any()) return odtWithoutQueries;

            foreach (var ozmaDbQuery in queriesFromOdt)
            {
                await ozmaDbQuery.LoadDataAsync(ozmaDbApiConnector, parametersWithValues);
            }

            var loadedQueries = queriesFromOdt.Where(p => p.IsLoaded).ToList();
            if (loadedQueries.Count == queriesFromOdt.Count)
            {
                var data = new Dictionary<string, object>();
                foreach (var parameterWithValue in parametersWithValues)
                {
                    //if (parameters.Any(p => p.Key == parameterWithValue.Key))
                    //{
                    if (!data.Any(p => p.Key == parameterWithValue.Key))
                        data.Add(parameterWithValue.Key, parameterWithValue.Value);
                    //}
                }

                var templateExpressions = OpenDocumentTextFunctions.GetTemplateExpressionsFromOdt(odtWithoutQueries);
                if (!templateExpressions.Any())
                    throw new Exception("No {{ }} expressions found in template");

                #region syntax check
                var structureFindings = Services.TemplateAnalyzer.CheckStructure(templateExpressions, loadedQueries);
                var firstError = structureFindings.FirstOrDefault(f => f.Severity == "error");
                if (firstError != null) throw new Exception(firstError.Message);

                foreach (var templateExpression in templateExpressions)
                {
                    var loadedQuery = loadedQueries.First(p => p.Name == templateExpression.QueryName);

                    switch (templateExpression.QueryType)
                    {
                        case QueryType.SingleRow:
                            if (!(loadedQuery.Result is ExpandoObject))
                                throw new Exception("Query " + templateExpression.QueryName +
                                                    " from template return result is not ExpandoObject");
                            foreach (var fieldName in templateExpression.FieldNames)
                            {
                                if (!((ExpandoObject)loadedQuery.Result).Any(p => p.Key == fieldName))
                                    throw new Exception("Query " + templateExpression.QueryName +
                                                        " error: field " + fieldName +
                                                        " not found in OzmaDB query results");
                            }

                            break;
                        case QueryType.ManyRows:
                            if (!(loadedQuery.Result is List<ExpandoObject>))
                                throw new Exception("Query " + templateExpression.QueryName +
                                                    " from template return result is not List<ExpandoObject>");
                            foreach (var fieldName in templateExpression.FieldNames)
                            {
                                foreach (var item in (List<ExpandoObject>)loadedQuery.Result)
                                {
                                    if (!item.Any(p => p.Key == fieldName))
                                        throw new Exception("Query " + templateExpression.QueryName +
                                                            " error: field " + fieldName +
                                                            " not found in OzmaDB query results");
                                }
                            }

                            break;
                    }
                }
                #endregion

                var odtTemplate = new OdtTemplate(odtWithoutQueries);
                var imagesToInsertFileNamesWithImageSize = new Dictionary<string, Size>();

                foreach (var loadedQuery in loadedQueries)
                {
                    if (loadedQuery.Result != null)
                    {
                        if (loadedQuery.BarCodeFieldValues.Any())
                        {
                            var barCodeGenerator = new BarCodeGenerator();
                            foreach (var barCodeFieldValue in loadedQuery.BarCodeFieldValues)
                            {
                                var image = barCodeGenerator.Generate(barCodeFieldValue.CodeType,
                                    barCodeFieldValue.ValueToEncode);
                                var bytes = image.Encode().ToArray();
                                var imageBlob = new ImageBlob("png", bytes);
                                var documentBlob = odtTemplate.TemplateDocument.AddOrGetImage(imageBlob);
                                loadedQuery.ChangeBarCodeFieldValueInResult(barCodeFieldValue,
                                    documentBlob.Blob.FileName);

                                var height = defaultImageHeight;
                                if (barCodeFieldValue.ImageHeightFromAttribute != null)
                                    height = (int)barCodeFieldValue.ImageHeightFromAttribute;
                                var imageRatio = (float)image.Width / image.Height;
                                var width = (int)(height * imageRatio);
                                var size = new Size(width, height);
                                if (!imagesToInsertFileNamesWithImageSize.ContainsKey(documentBlob.Blob.FileName))
                                    imagesToInsertFileNamesWithImageSize.Add(documentBlob.Blob.FileName, size);
                            }
                        }
                        data.Add(loadedQuery.Name, loadedQuery.Result);
                    }
                }
                var context = new TemplateContext(data);
                result = await odtTemplate.RenderAsync(context);
                if (imagesToInsertFileNamesWithImageSize.Any())
                    OpenDocumentTextFunctions.InsertImages(result, imagesToInsertFileNamesWithImageSize);
            }
            else
            {
                throw new Exception("One or more FunDD queries failed");
            }

            return result;
        }
    }
}
