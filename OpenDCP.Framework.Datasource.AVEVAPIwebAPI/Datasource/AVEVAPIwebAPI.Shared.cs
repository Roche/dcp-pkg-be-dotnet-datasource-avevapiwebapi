using Microsoft.Extensions.Configuration;
using OpenDCP.Framework.Configuration.Model.Errors;
using OpenDCP.Framework.Configuration.Model.Responses.Datasource;
using OpenDCP.Framework.Configuration.Model.Warnings;
using OpenDCP.Framework.Datasource.AVEVAPIwebAPI.Constants;
using OpenDCP.Framework.Datasource.AVEVAPIwebAPI.JsonSerializerContexts;
using OpenDCP.Framework.Datasource.Base;
using OpenDCP.Framework.DataTypes.Equipment.Constants;
using OpenDCP.Framework.DataTypes.Equipment.Entities.AssetHierarchy;
using OpenDCP.Framework.Exceptions;
using OpenDCP.Framework.HttpClient;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Tasks;

namespace OpenDCP.Framework.Datasource.AVEVAPIwebAPI.Datasource
{
    public partial class AVEVAPIwebAPIDatasource : BaseDatasource
    {
        private readonly DCPHttpClient _httpClient;
        public AVEVAPIwebAPIDatasource(IConfiguration configuration) : base(configuration)
        {
            _httpClient = new DCPHttpClient(configuration);
        }

        public AVEVAPIwebAPIDatasource(int? id, IConfiguration configuration, JsonObject connectionString) : base(id, connectionString, configuration)
        {
            _httpClient = new DCPHttpClient(configuration);
        }

        private async Task<HttpResponseMessage> GetAsync(string url, TimeSpan? timeoutAfter = null)
        {
            DCPHttpClient httpClient = new DCPHttpClient(_configuration);
            ServicePointManager.ServerCertificateValidationCallback = delegate { return true; };

            var response = await httpClient.GetAsync(url,
                (h) =>
                {
                    h.Authorization = GetAuthHeader();
                    h.Add("X-Requested-With", "XMLHttpRequest");
                });

            return response;
        }

        private async Task<(HttpResponseMessage res, string content, List<DatasourceError> errors)> PostAsync(string url, string body)
        {
            var result = (
                res: new HttpResponseMessage(),
                content: string.Empty,
                errors: new List<DatasourceError>()
            );

            DCPHttpClient httpClient = new DCPHttpClient(_configuration);

            ServicePointManager.ServerCertificateValidationCallback = delegate
            {
                return true;
            };

            var response = await httpClient.PostAsync(
                url,
                body,
                (h) =>
                {
                    h.Authorization = GetAuthHeader();
                    h.Add("X-Requested-With", "XMLHttpRequest");
                });

            string content = await response.Content.ReadAsStringAsync();

            result.content = content;
            result.res = response;

            var contentSections = JsonSerializer
                .Deserialize<Dictionary<string, JsonElement>>(content, DatasourceSerializerContext.Default.DictionaryStringJsonElement);

            if (contentSections != null)
            {
                foreach (var section in contentSections)
                {
                    var sectionContent = section.Value;

                    if (sectionContent.TryGetProperty("Status", out var statusProp) &&
                        statusProp.GetInt32() == (int)HttpStatusCode.NotFound)
                    {
                        result.errors.Add(new DatasourceError
                        {
                            ErrorCode = (byte)DatasourceErrorCode.NotFound,
                            Error = sectionContent.ToString()
                        });

                        return result;
                    }
                }
            }

            if (!response.IsSuccessStatusCode)
            {
                var responseMessage = "Response status code does not indicate success: " + (int)response.StatusCode + " (" + response.StatusCode + " ). ";
            }

            return result;
        }

        private AuthenticationHeaderValue GetAuthHeader()
        {
            var credentials = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{_connectionString!["User"]}:{_connectionString["Password"]}"));
            return new AuthenticationHeaderValue("Basic", credentials);
        }

        private AuthenticationHeaderValue GetAuthHeader(JsonObject connectionString)
        {
            var credentials = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{connectionString["User"]}:{connectionString["Password"]}"));
            return new AuthenticationHeaderValue("Basic", credentials);
        }

        private async Task<(GlobalFilterElementDTO element, string parent, DatasourceResponse res)> RequestFilterElementAsync(string webId)
        {
            var result = (
               element: new GlobalFilterElementDTO(),
               parent: (string)null!,
               res: new DatasourceResponse()
           );

            var hiddenCategories = GetHiddenCategories();

            if (string.Equals(webId.Substring(2, 2), "Em", StringComparison.OrdinalIgnoreCase))
            {
                var filterType = GetMandatoryField("SensorFilterType");

                var url = $"{GetMandatoryField("BaseURL")}elements/{webId}" +
                    $"?selectedFields=Name;Description;WebId;CategoryNames;Links.Parent" +
                    $"&webIdType=IdOnly";

                var response = await GetAsync(url);
                var content = await ValidateResponse(response);

                if (!content.content.HasValue && content.res.Errors.Count != 0 && content.res.Errors.Any(e => e.ErrorCode == (byte)ErrorCode.NotFound))
                {
                    result.res.Errors.Add(new DatasourceError
                    {
                        Identifier = webId,
                        ErrorCode = (byte)DatasourceErrorCode.NotFoundWebId,
                        Error = content.res.Errors.FirstOrDefault()?.Error
                    });

                    return result;
                }
                else if (!content.content.HasValue)
                {
                    result.res.Errors = content.res.Errors;

                    return result;
                }

                var element = content.content.Value;

                result.element = new GlobalFilterElementDTO
                {
                    Name = element.GetProperty("Name").GetString(),
                    Description = element.GetProperty("Description").GetString(),
                    Step = false,
                    WebId = element.GetProperty("WebId").GetString(),
                    CategoryNames = element.GetProperty("CategoryNames")
                                            .Deserialize<List<string>>(DatasourceSerializerContext.Default.ListString)
                                            .Except(hiddenCategories)
                                            .ToList()
                };

                result.element = ApplyGlobalFilterBusinessRules(result.element, filterType);

                if (element.GetProperty("Links").TryGetProperty("Parent", out var parentProperty))
                {
                    result.parent = parentProperty.GetString()!
                        .Split("/")!
                        .Last();
                }
            }
            else if (string.Equals(webId.Substring(2, 2), "Ab", StringComparison.OrdinalIgnoreCase))
            {
                var url = $"{GetMandatoryField("BaseURL")}attributes/{webId}" +
                   $"?selectedFields=Name;Description;WebId;CategoryNames;" +
                   $"DefaultUnitsNameAbbreviation;DefaultUnitsName;" +
                   $"Type;Step;Links.Element" +
                   $"&webIdType=IdOnly";

                var response = await GetAsync(url);
                var content = await ValidateResponse(response);

                var element = content.content!.Value;

                if (!content.content.HasValue && content.res.Errors.Count != 0 && content.res.Errors.Any(e => e.ErrorCode == (byte)ErrorCode.NotFound))
                {
                    result.res.Errors.Add(new DatasourceError
                    {
                        Identifier = webId,
                        ErrorCode = (byte)DatasourceErrorCode.NotFoundWebId,
                        Error = content.res.Errors.FirstOrDefault()?.Error
                    });

                    return result;
                }
                else if (!content.content.HasValue)
                {
                    result.res.Errors = content.res.Errors;

                    return result;
                }

                string? uom = null;
                if (element.TryGetProperty("DefaultUnitsNameAbbreviation", out var abbrProp) && abbrProp.ValueKind != JsonValueKind.Null)
                {
                    uom = abbrProp.GetString();
                }
                else if (element.TryGetProperty("DefaultUnitsName", out var nameProp) && nameProp.ValueKind != JsonValueKind.Null)
                {
                    uom = nameProp.GetString();
                }

                result.element = new GlobalFilterElementDTO
                {
                    Name = element.GetProperty("Name").GetString(),
                    Description = element.GetProperty("Description").GetString(),
                    Step = element.GetProperty("Step").GetBoolean(),
                    IsDevice = false,
                    IsChromUnit = false,
                    UoM = uom ?? string.Empty,
                    ValueType = element.GetProperty("Type").GetString(),
                    WebId = element.GetProperty("WebId").GetString(),
                    CategoryNames = element.GetProperty("CategoryNames")
                                            .Deserialize<List<string>>(DatasourceSerializerContext.Default.ListString)
                                            .Except(hiddenCategories)
                                            .ToList(),
                    Link = null
                };

                if (element.TryGetProperty("Links", out var links) && links.TryGetProperty("Element", out var elementProp))
                {
                    string? elementValue = elementProp.GetString();
                    if (!string.IsNullOrEmpty(elementValue))
                    {
                        result.parent = elementValue.Split("/").Last();
                    }
                }

            }
            else
            {
                throw new DCPBadRequestException(ErrorMessages.InvalidWebId);
            }

            return result;
        }
        private string GetAvevaPIWebApiTemplate(string templateName)
        {
            var assembly = typeof(AVEVAPIwebAPIDatasource).Assembly;
            var assemblyDir = Path.GetDirectoryName(assembly.Location);

            if (assemblyDir is null)
                throw new Exception();

            string templateFilePath = Path.Combine(assemblyDir, "Templates", templateName);

            return ReadTemplate(templateFilePath);
        }

        private async Task<Dictionary<string, (T data, List<DatasourceError> errors, List<DatasourceWarning> warnings)>>
         PerformMultiGetAsync<T>(
         string urlTemplate,
         Dictionary<string, Dictionary<string, string>> parameterSets,
         Func<JsonElement, string, (T data, List<DatasourceError> errors, List<DatasourceWarning> warnings)> function)
        {
            if (GetField("BulkRequestStrategy") == "Parallel"
                || parameterSets.Count == 1)
            {
                var tasks = parameterSets.Select(async set =>
                {
                    var url = ReplaceTemplatePlaceholders(urlTemplate, set.Value);
                    var response = await GetAsync(url);
                    var content = await ValidateResponse(response);

                    if (!content.content.HasValue)
                    {
                        return new KeyValuePair<string, (T data, List<DatasourceError> errors, List<DatasourceWarning> warnings)>(
                            set.Key,
                            (default!, content.res.Errors, content.res.Warnings)
                        );
                    }

                    var jsonElement = content.content.Value;

                    return new KeyValuePair<string, (T data, List<DatasourceError> errors, List<DatasourceWarning> warnings)>(
                        set.Key,
                        function(jsonElement, set.Key)
                    );
                });
                //We need to know which task is 
                var result = await Task.WhenAll(tasks);
                return result.ToDictionary(pair => pair.Key, pair => pair.Value);
            }
            else if (GetField("BulkRequestStrategy") == "Bulk"
                || GetField("BulkRequestStrategy") == "Mixed")
            {
                var tempalte = WrapMultipleRequestsIntoTemplate(urlTemplate, parameterSets);

                var response = await PostAsync($"{GetMandatoryField("BaseURL")}/batch", tempalte);

                var sections = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(response.content, DatasourceSerializerContext.Default.DictionaryStringJsonElement);

                return ApplyProcessingFunctionToMultiRequestTempalte<T>(
                    sections!,
                    function
                );
            }
            else
            {
                throw new DCPInternalServerErrorException(AvevaErrorMessages.NotValidRequestStrategy);
            }
        }



        public override string DisplayConnectionString()
        {
            return $"Database: {GetMandatoryField("WebId")} via {GetMandatoryField("BaseURL")}";
        }

        protected string? GetField(string key)
        {
            if (_connectionString is null)
                throw new ArgumentNullException(nameof(_connectionString));

            return _connectionString[key]?.ToString();
        }

        protected static string GetMandatoryField(JsonObject? obj, string key)
        {
            if (obj is null)
                throw new ArgumentNullException(nameof(obj));

            return obj[key]?.ToString() ?? throw new DCPBadRequestException($"Missing field: {key}");
        }

        protected string GetMandatoryField(string key)
        {
            return GetMandatoryField(_connectionString, key);
        }
    }
}
