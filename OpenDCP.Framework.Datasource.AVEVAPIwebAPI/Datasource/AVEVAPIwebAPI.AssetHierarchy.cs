using OpenDCP.Framework.Configuration.Model.Errors;
using OpenDCP.Framework.Configuration.Model.Warnings;
using OpenDCP.Framework.Datasource.AVEVAPIwebAPI.Constants;
using OpenDCP.Framework.Datasource.AVEVAPIwebAPI.Helpers;
using OpenDCP.Framework.Datasource.AVEVAPIwebAPI.JsonSerializerContexts;
using OpenDCP.Framework.DataTypes.Equipment.Constants;
using OpenDCP.Framework.DataTypes.Equipment.Entities.AssetHierarchy;
using OpenDCP.Framework.DataTypes.Equipment.Interfaces;
using OpenDCP.Framework.DataTypes.Equipment.Requests.AssetHierarchy;
using OpenDCP.Framework.DataTypes.Equipment.Requests.SensorData;
using OpenDCP.Framework.DataTypes.Equipment.Responses.AssetHierarchy;
using OpenDCP.Framework.Exceptions;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace OpenDCP.Framework.Datasource.AVEVAPIwebAPI.Datasource
{
    public partial class AVEVAPIwebAPIDatasource : IAssetHierarchyDataSource
    {
        public async Task<SensorListResponse> GetMultipleSensorsAsync(SensorQueryDTO model, CancellationToken cancellationToken = default)
        {
            var url = $"{GetMandatoryField("BaseURL")}attributes/multiple" +
                $"?webid={string.Join("&webId=", model.WebId)}" +
                $"&selectedFields=Items.Object.Links.Element;Items.Object.Name;Items.Object.Description;Items.Object.WebId;Items.Object.ValueType;" +
                $"Items.Object.DefaultUnitsNameAbbreviation;Items.Object.DefaultUnitsName;" +
                $"Items.Object.CategoryNames;Items.Object.Type;Items.Object.Step;Items.Exception" +
                $"&webIdType=IDOnly";

            var response = await GetAsync(url);
            var content = await ValidateResponse(response);

            var result = new SensorListResponse
            {
                Sensors = new List<SensorDTO>()
            };

            if (!content.content.HasValue)
            {
                result.Errors = content.res.Errors;
                return result;
            }

            var items = content.content.Value.GetProperty("Items");

            var hiddenCategories = GetHiddenCategories();

            foreach (var item in items.EnumerateArray())
            {
                var itemErrors = item.ToSensorDatasourceErrors();
                result.Errors.AddRange(itemErrors);

                if (itemErrors.Count == 0)
                {
                    var obj = item.GetProperty("Object");
                    var sensor = obj.ToSensor(hiddenCategories);
                    result.Sensors.Add(sensor);
                }
            }

            return result;
        }

        public async Task<SensorResponse> GetSingleSensorAsync(string webId, CancellationToken cancellationToken = default)
        {
            var url = $"{GetMandatoryField("BaseURL")}attributes/{webId}" +
                $"?selectedFields=Links.Element;Name;Description;WebId;" +
                $"DefaultUnitsNameAbbreviation;DefaultUnitsName;" +
                $"CategoryNames;Step;Type" +
                $"&webIdType=IDOnly";

            var response = await GetAsync(url);
            var content = await ValidateResponse(response);

            var result = new SensorResponse();

            if (content.res.Errors.Count != 0 && content.res.Errors.Any(e => e.ErrorCode == (byte)ErrorCode.NotFound))
            {
                result.Errors.Add(new DatasourceError
                {
                    Identifier = webId,
                    ErrorCode = (byte)ErrorCode.DeleteSensor,
                    Error = content.res.Errors.FirstOrDefault()?.Error
                });

                return result;
            }
            else if (content.res.Errors.Count != 0)
            {
                result.Errors = content.res.Errors;

                return result;
            }

            var item = content.content!.Value;
            result.Errors = item.ToSensorDatasourceErrors();
            var hiddenCategories = GetHiddenCategories();

            if (!result.Errors.Any())
            {
                result.Sensor = item.ToSensor(hiddenCategories);
            }

            return result;
        }

        public async Task<DeviceResponse> GetSingleDeviceAsync(string webId, CancellationToken cancellationToken = default)
        {
            var url = $"{GetMandatoryField("BaseURL")}elements/{webId}" +
                $"?selectedFields=Name;Description;Path;WebId;CategoryNames" +
                $"&webIdType=IDOnly";

            var response = await GetAsync(url);
            var content = await ValidateResponse(response);

            var result = new DeviceResponse();

            if (content.res.Errors.Count != 0 && content.res.Errors.Any(e => e.ErrorCode == (byte)ErrorCode.NotFound))
            {
                result.Errors.Add(new DatasourceError
                {
                    Identifier = webId,
                    ErrorCode = (byte)ErrorCode.DeleteDevice,
                    Error = content.res.Errors.FirstOrDefault()?.Error
                });

                return result;
            }
            else if (content.res.Errors.Count != 0)
            {
                result.Errors = content.res.Errors;

                return result;
            }

            var path = string.Empty;

            if (!string.IsNullOrEmpty(GetField("GlobalFilterRoot")))
            {
                var urlForPath = $"{GetMandatoryField("BaseURL")}elements/{GetField("GlobalFilterRoot")}/elements" +
                            "?selectedFields=Items.Path";

                var pathResponse = await GetAsync(urlForPath);
                var pathContent = await ValidateResponse(pathResponse);

                if (!pathContent.content.HasValue)
                {
                    result.Errors.AddRange(pathContent.res.Errors);
                }

                path = pathContent.content!.Value
                    .GetProperty("Items")
                    .EnumerateArray()
                    .Select(i => i.GetProperty("Path").GetString())
                    .FirstOrDefault();
            }

            var hiddenCategories = GetHiddenCategories();
            var filterType = GetMandatoryField("SensorFilterType");
            result.Errors.AddRange(content.content!.Value.ToDeviceDatasourceErrors());
            result.Device = content.content.Value.ToDevice(hiddenCategories, filterType, path);

            return result;
        }

        public async Task<DeviceListResponse> GetMultipleDevicesAsync(MultiWebIdQueryDTO model, CancellationToken cancellationToken = default)
        {
            var result = new DeviceListResponse
            {
                Devices = new List<DeviceDTO>()
            };

            var url = $"{GetMandatoryField("BaseURL")}elements/multiple" +
               $"?webId={string.Join("&webId=", model.WebId)}" +
               $"&selectedFields=Items.Object.Name;Items.Object.Description;Items.Object.Path;Items.Object.WebId;Items.Object.CategoryNames;Items.Exception" +
               $"&webIdType=IDOnly";

            var response = await GetAsync(url);
            var content = await ValidateResponse(response);

            if (!content.content.HasValue)
            {
                result.Errors = content.res.Errors;
                return result;
            }

            var elements = content.content.Value
                .GetProperty("Items")
                .EnumerateArray();

            var path = string.Empty;

            if (!string.IsNullOrEmpty(GetField("GlobalFilterRoot")))
            {
                var urlForPath = $"{GetMandatoryField("BaseURL")}elements/{GetField("GlobalFilterRoot")}/elements" +
                            "?selectedFields=Items.Path";

                var pathResponse = await GetAsync(urlForPath);
                var pathContent = await ValidateResponse(pathResponse);

                if (!pathContent.content.HasValue)
                {
                    result.Errors.AddRange(pathContent.res.Errors);
                }

                path = pathContent.content!.Value
                    .GetProperty("Items")
                    .EnumerateArray()
                    .Select(i => i.GetProperty("Path").GetString())
                    .FirstOrDefault();
            }

            var hiddenCategories = GetHiddenCategories();
            var filterType = GetMandatoryField("SensorFilterType");

            foreach (var element in elements)
            {
                result.Errors.AddRange(element.ToDeviceDatasourceErrors());
                if (element.TryGetProperty("Object", out var objectProperty))
                {
                    var device = objectProperty.ToDevice(hiddenCategories, filterType);
                    if (device != null)
                    {
                        result.Devices.Add(device);
                    }
                }
            }

            return result;
        }

        public async Task<DeviceListResponse> SearchDevicesAsync(DeviceQueryDTO model, CancellationToken cancellationToken = default)
        {
            var result = new DeviceListResponse
            {
                Devices = new List<DeviceDTO>()
            };

            var url = $"{GetMandatoryField("BaseURL")}assetdatabases/{GetMandatoryField("WebId")}/elements" +
                "?selectedFields=Items.Name;Items.Description;Items.Path;Items.WebId;Items.CategoryNames" +
                "&searchFullHierarchy=True" +
                "&maxCount=100000" +
                "&webIdType=IDOnly" +
                "&categoryName=DC_Asset";

            if (model.NameQuery != null && !string.IsNullOrEmpty(model.NameQuery))
            {
                url += $"&nameFilter=*{model.NameQuery}*";
            }

            var response = await GetAsync(url);
            var content = await ValidateResponse(response);

            if (!content.content.HasValue)
            {
                result.Errors = content.res.Errors;
                return result;
            }

            var items = content.content.Value
                .GetProperty("Items")
                .EnumerateArray();

            var path = string.Empty;

            if (!string.IsNullOrEmpty(GetField("GlobalFilterRoot")))
            {
                var urlForPath = $"{GetMandatoryField("BaseURL")}elements/{GetField("GlobalFilterRoot")}/elements" +
                            "?selectedFields=Items.Path";

                var pathResponse = await GetAsync(urlForPath);
                var pathContent = await ValidateResponse(pathResponse);

                if (!pathContent.content.HasValue)
                {
                    result.Errors.AddRange(pathContent.res.Errors);
                }

                path = pathContent.content!.Value
                    .GetProperty("Items")
                    .EnumerateArray()
                    .Select(i => i.GetProperty("Path").GetString())
                    .FirstOrDefault();
            }

            var hiddenCategories = GetHiddenCategories();
            var filterType = GetMandatoryField("SensorFilterType");
            foreach (var item in items)
            {
                result.Devices.Add(item.ToDevice(hiddenCategories, filterType, path));
            }

            return result;
        }

        public async Task<DatasourceUnitResponse> GetUnitsOfMeasureAsync(CancellationToken cancellationToken = default)
        {
            var template = GetAvevaPIWebApiTemplate("DatasourceUnits.json");
            var templateParameters = new Dictionary<string, string>
            {
                {"BASEURL", GetMandatoryField("BaseURL")},
                {"ASSETSERVERWEBID", $"I1RS{GetMandatoryField("WebId").Substring(4,22)}"}
            };

            template = ReplaceTemplatePlaceholders(template, templateParameters);

            var response = await PostAsync($"{GetMandatoryField("BaseURL")}/batch", template);

            var result = new DatasourceUnitResponse
            {
                Units = new List<DatasourceUnit>()
            };

            var responseSections = JsonSerializer
                .Deserialize<Dictionary<string, JsonElement>>(response.content, DatasourceSerializerContext.Default.DictionaryStringJsonElement);

            if (responseSections == null)
            {
                result.Errors.Add(new DatasourceError
                {
                    ErrorCode = (byte)DatasourceErrorCode.EmptyReponse,
                });

                return result;
            }

            var unitClasses = responseSections["0"].GetProperty("Content").GetProperty("Items");
            var unitsPerClass = responseSections["1"].GetProperty("Content").GetProperty("Items");
            int i = 0;

            foreach (var uomClass in unitClasses.EnumerateArray())
            {
                var className = uomClass.GetProperty("Name").GetString();

                foreach (var uom in unitsPerClass[i].GetProperty("Content").GetProperty("Items").EnumerateArray())
                {
                    var datasourceUnit = new DatasourceUnit
                    {
                        Name = uom.GetProperty("Name").GetString(),
                        Abbreviation = uom.GetProperty("Abbreviation").GetString(),
                        Class = className,
                    };

                    result.Units.Add(datasourceUnit);

                }
                i++;
            }

            return result;
        }

        public async Task<ElementMetaDataResponse> GetElementMetaDataAsync(string webId, CancellationToken cancellationToken = default)
        {
            var result = new ElementMetaDataResponse
            {
                MetaData = new List<ElementMetaDataDTO>()
            };

            if (!string.Equals(webId.Substring(2, 2), "Em", StringComparison.OrdinalIgnoreCase))
            {
                return result;
            }

            var template = GetAvevaPIWebApiTemplate("ElementMetadata.json");
            var templateParameters = new Dictionary<string, string>
            {
                {"BASEURL", GetMandatoryField("BaseURL")},
                {"ELEMENTWEBID", webId}
            };

            template = ReplaceTemplatePlaceholders(template, templateParameters);

            var response = await PostAsync($"{GetMandatoryField("BaseURL")}/batch", template);

            if (response.errors.Count != 0 && response.errors.Any(e => e.ErrorCode == (byte)DatasourceErrorCode.NotFound))
            {
                result.Errors.Add(new DatasourceError
                {
                    Identifier = webId,
                    ErrorCode = (byte)ErrorCode.DeleteDevice,
                    Error = response.errors.FirstOrDefault()?.Error
                });

                return result;
            }
            else if (response.errors.Count != 0)
            {
                result.Errors = response.errors;

                return result;
            }

            var responseSections = JsonSerializer
                .Deserialize<Dictionary<string, JsonElement>>(response.content, DatasourceSerializerContext.Default.DictionaryStringJsonElement) ?? throw new InvalidOperationException("Failed to deserialize content");

            var tags = responseSections["0"]
                .GetProperty("Content")
                .GetProperty("Items");

            if (tags.EnumerateArray().Count() == 0)
            {
                return result;
            }

            var values = responseSections["1"]
                .GetProperty("Content")
                .GetProperty("Items");

            int i = 0;

            foreach (var tag in tags.EnumerateArray())
            {
                if (tag.GetProperty("IsConfigurationItem").GetBoolean())
                {
                    var value = values[i].GetProperty("Content");
                    var element = new ElementMetaDataDTO
                    {
                        Name = tag.GetProperty("Name").GetString(),
                        Value = value.GetProperty("Good").GetBoolean() ?
                                    value.GetProperty("Value").ToString() :
                                    AvevaErrorMessages.BadValue,
                        Description = tag.GetProperty("Description").GetString()
                    };
                    result.MetaData.Add(element);
                }
                ;

                i++;
            }

            return result;
        }

        private (List<GlobalFilterElementDTO> data, List<DatasourceError> errors, List<DatasourceWarning> warnings) ProcessGlobalFilterElementResponse(JsonElement element)
        {
            var result = (
                data: new List<GlobalFilterElementDTO>(),
                errors: new List<DatasourceError>(),
                warnings: new List<DatasourceWarning>()
            );

            var filterType = GetMandatoryField("SensorFilterType");
            var hiddenCategories = GetHiddenCategories();

            var items = element
                    .GetProperty("Items")
                    .EnumerateArray();

            foreach (var item in items)
            {
                var gFilter = new GlobalFilterElementDTO();

                if (item.TryGetProperty("Object", out JsonElement itemObject))
                {
                    gFilter = itemObject.ToLevelGlobalFilterElement(hiddenCategories);
                }
                else
                {
                    gFilter = item.ToLevelGlobalFilterElement(hiddenCategories);
                }

                gFilter = ApplyGlobalFilterBusinessRules(gFilter, filterType);

                result.data.Add(gFilter);
            }

            return result;
        }

        private (GlobalFilterElementDTO data, List<DatasourceError> errors, List<DatasourceWarning> warnings) ProcessSingleGlobalFilterElementResponse(JsonElement element)
        {
            var result = (
                data: new GlobalFilterElementDTO(),
                errors: new List<DatasourceError>(),
                warnings: new List<DatasourceWarning>()
            );

            var filterType = GetMandatoryField("SensorFilterType");
            var hiddenCategories = GetHiddenCategories();

            var hasParent =
                element.TryGetProperty("Links", out var links) &&
                links.TryGetProperty("Parent", out _);

            var gFilter = element.ToLevelGlobalFilterElement(hiddenCategories);

            gFilter = ApplyGlobalFilterBusinessRules(gFilter, filterType, hasParent);

            result.data = gFilter;

            return result;
        }

        private GlobalFilterElementDTO ApplyGlobalFilterBusinessRules(GlobalFilterElementDTO gFilter, string filterType, bool hasParent = false)
        {
            if (gFilter.CategoryNames.Contains("DC_Asset"))
            {
                gFilter.IsDevice = true;
            }

            if (gFilter.CategoryNames.Contains("ChromUnit"))
            {
                gFilter.IsChromUnit = true;
            }

            if (!gFilter.IsDevice && !hasParent)
            {
                gFilter.Link = $"{gFilter.WebId}/level";
            }
            else if ((gFilter.IsDevice || hasParent) && filterType != "NoSensorFilter")
            {
                gFilter.Link = $"{gFilter.WebId}/group";
            }
            else if (gFilter.IsDevice && filterType == "NoSensorFilter")
            {
                gFilter.Link = $"{gFilter.WebId}/sensor";
            }

            return gFilter;
        }

        private (List<GlobalFilterElementDTO> data, List<DatasourceError> errors, List<DatasourceWarning> warnings) ProcessGlobalFilterAttributeResponse(JsonElement element)
        {
            var result = (
                data: new List<GlobalFilterElementDTO>(),
                errors: new List<DatasourceError>(),
                warnings: new List<DatasourceWarning>()
            );

            var hiddenCategories = GetHiddenCategories();

            var items = element
                    .GetProperty("Items")
                    .EnumerateArray();

            foreach (var item in items)
            {
                //ConfigurationItems are marked as excluded and should be skipped
                if (!item.GetProperty("IsConfigurationItem").GetBoolean())
                {
                    var gFilter = item.ToSensorGlobalFilterElement(hiddenCategories);
                    result.data.Add(gFilter);
                }
            }

            return result;
        }

        private (GlobalFilterElementDTO data, List<DatasourceError> errors, List<DatasourceWarning> warnings) ProcessSingleGlobalFilterAttributeResponse(JsonElement element)
        {
            var result = (
                data: new GlobalFilterElementDTO(),
                errors: new List<DatasourceError>(),
                warnings: new List<DatasourceWarning>()
            );

            var hiddenCategories = GetHiddenCategories();

            //ConfigurationItems are marked as excluded and should be skipped
            if (!(element.TryGetProperty("IsConfigurationItem", out var value) && value.GetBoolean()))
            {
                var gFilter = element.ToSensorGlobalFilterElement(hiddenCategories);
                result.data = gFilter;
            }

            return result;
        }

        private (List<GlobalFilterElementDTO> data, List<DatasourceError> errors, List<DatasourceWarning> warnings) ProcessGlobalFilterAttributeGroupResponse(JsonElement element, string elementWebId)
        {
            var result = (
                data: new List<GlobalFilterElementDTO>(),
                errors: new List<DatasourceError>(),
                warnings: new List<DatasourceWarning>()
            );

            var hiddenCategories = GetHiddenCategories();

            var items = element
                    .GetProperty("Items")
                    .EnumerateArray();

            var categories = new List<string>();

            foreach (var item in items)
            {
                categories.AddRange(item.GetProperty("CategoryNames").Deserialize<List<string>>(DatasourceSerializerContext.Default.ListString));
            }
            var linkBase = $"{elementWebId}/sensor?groupName=";

            result.data = categories
                .Distinct()
                .Except(hiddenCategories)
                .Select(x => new GlobalFilterElementDTO
                {
                    CategoryNames = new List<string>(),
                    Description = string.Empty,
                    IsChromUnit = false,
                    IsDevice = false,
                    Name = x,
                    Step = false,
                    UoM = null,
                    WebId = x,
                    ValueType = null,
                    Link = linkBase + x
                })
                .ToList();

            return result;

        }

        private (List<GlobalFilterElementDTO> data, List<DatasourceError> errors, List<DatasourceWarning> warnings) ProcessGlobalFilterElementGroupResponse(JsonElement element, string elementWebId)
        {
            var result = (
                data: new List<GlobalFilterElementDTO>(),
                errors: new List<DatasourceError>(),
                warnings: new List<DatasourceWarning>()
            );

            var items = element
                    .GetProperty("Items")
                    .EnumerateArray();

            foreach (var item in items)
            {
                var gFilter = new GlobalFilterElementDTO
                {
                    WebId = item.GetProperty("WebId").GetString(),
                    Name = item.GetProperty("Name").GetString(),
                    Link = $"{elementWebId}/sensor?groupName={item.GetProperty("WebId").GetString()}",
                    Description = item.GetProperty("Description").GetString(),
                    Step = false,
                    UoM = null,
                    ValueType = null,
                    CategoryNames = new List<string>(),
                };

                result.data.Add(gFilter);
            }

            return result;

        }

        public async Task<GlobalFilterListResponse> GetGlobalFilterAsync(string? link, CancellationToken cancellationToken = default)
        {
            var url = $"{GetMandatoryField("BaseURL")}";
            var gFilterType = GetMandatoryField("SensorFilterType");

            Func<JsonElement, (List<GlobalFilterElementDTO> data, List<DatasourceError> errors, List<DatasourceWarning> warnings)> function;

            var result = new GlobalFilterListResponse
            {
                Elements = new List<GlobalFilterElementDTO>()
            };

            if (string.IsNullOrEmpty(link))
            {
                if (string.IsNullOrEmpty(GetField("GlobalFilterRoot")))
                {
                    url = $"{url}assetdatabases/{GetMandatoryField("WebId")}/elements" +
                          "?selectedFields=Items.WebId;Items.Name;Items.Description;Items.HasChildren;Items.CategoryNames" +
                          "&webIdType=IDOnly";
                }
                else
                {
                    url = $"{url}elements/{GetMandatoryField("GlobalFilterRoot")}/elements" +
                          "?selectedFields=Items.WebId;Items.Name;Items.Description;Items.HasChildren;Items.CategoryNames" +
                          "&webIdType=IDOnly";
                }

                function = this.ProcessGlobalFilterElementResponse;

            }
            else if (link.Contains("/level"))
            {
                url = $"{url}elements/{link.Split('/').First()}/elements" +
                      "?selectedFields=Items.WebId;Items.Name;Items.Description;Items.HasChildren;Items.CategoryNames" +
                      "&webIdType=IDOnly";

                function = this.ProcessGlobalFilterElementResponse;
            }
            else if (link.Contains("/group"))
            {
                string webId = link.Split('/').First();

                if (gFilterType == "AttributeFilter")
                {

                    url = $"{url}elements/{webId}/attributes" +
                      "?maxcount=4000" +
                      "&selectedFields=Items.WebId;Items.Name;Items.Description;Items.Type;" +
                      "Items.DefaultUnitsNameAbbreviation;Items.DefaultUnitsName;" +
                      "Items.HasChildren;Items.Step;Items.IsConfigurationItem;Items.CategoryNames" +
                      "&webIdType=IDOnly";

                    function = element =>
                        ProcessGlobalFilterAttributeGroupResponse(element, webId);
                }
                else if (gFilterType == "ElementFilter")
                {
                    url = $"{url}elements/{webId}/elements" +
                          "?maxcount=4000" +
                          "&selectedFields=Items.WebId;Items.Name;Items.Description;" +
                          "&webIdType=IDOnly";

                    function = element =>
                        ProcessGlobalFilterElementGroupResponse(element, webId);
                }
                else
                {
                    throw new ArgumentException("Invalid Link");
                }
            }
            else if (link.Contains("/sensor"))
            {
                var webId = link.Split('/').First();

                if (gFilterType == "ElementFilter")
                {
                    Match match = Regex.Match(link, @"groupName=([^;,]+)");

                    if (match.Success)
                        webId = match.Groups[1].Value;
                }

                url = $"{url}elements/{webId}/attributes" +
                      "?maxcount=4000" +
                      "&selectedFields=Items.WebId;Items.Name;Items.Description;Items.Type;" +
                      "Items.DefaultUnitsNameAbbreviation;Items.DefaultUnitsName;" +
                      "Items.HasChildren;Items.Step;Items.IsConfigurationItem;Items.CategoryNames" +
                      "&webIdType=IDOnly";

                if (gFilterType == "AttributeFilter")
                {
                    Match match = Regex.Match(link, @"groupName=([^;,]+)");

                    if (match.Success)
                        url += $"&categoryName={match.Groups[1].Value}";
                }

                function = this.ProcessGlobalFilterAttributeResponse;
            }
            else
            {
                throw new ArgumentException("Not valid link!");
            }


            var response = await GetAsync(url);
            var content = await ValidateResponse(response);

            if (!content.content.HasValue)
            {
                result.Errors = content.res.Errors;
                return result;
            }

            var section = function(content.content.Value);

            result.Elements = section.data;
            result.Warnings = section.warnings;
            result.Errors = section.errors;

            return result;
        }

        public async Task<GlobalFilterListResponse> GetGlobalFilterBackwardsAsync(string webId, CancellationToken cancellationToken = default)
        {

            var result = new GlobalFilterListResponse
            {
                Elements = new List<GlobalFilterElementDTO>()
            };

            var response = await RequestFilterElementAsync(webId);

            if (response.res.Errors.Count != 0 && response.res.Errors.Any(e => e.ErrorCode == (byte)DatasourceErrorCode.NotFoundWebId))
            {
                result.Errors = response.res.Errors;

                return result;
            }

            result.Elements.Add(response.element);

            //If attribute filter is used a special filter level for the sensor group needs to be added
            if (string.Equals(webId.Substring(2, 2), "Ab", StringComparison.OrdinalIgnoreCase)
                && GetMandatoryField("SensorFilterType") == "AttributeFilter")
            {

                var sensorGroup = response.element.CategoryNames.FirstOrDefault();

                result.Elements.Add(
                        new GlobalFilterElementDTO
                        {
                            Name = sensorGroup,
                            Description = string.Empty,
                            WebId = sensorGroup,
                            Link = $"{response.parent}/sensor?groupName={sensorGroup}",
                            UoM = null,
                            IsChromUnit = false,
                            IsDevice = false,
                            CategoryNames = new List<string>(),
                            Step = false,
                            ValueType = null,
                        }
                    );
            }
            //If element filter is used a special filter level for the sensor group needs to be added
            else if (string.Equals(webId.Substring(2, 2), "Ab", StringComparison.OrdinalIgnoreCase)
                && GetMandatoryField("SensorFilterType") == "ElementFilter")
            {
                var elementWebId = response.parent;
                response = await RequestFilterElementAsync(elementWebId);

                if (response.res.Errors.Count != 0 && response.res.Errors.Any(e => e.ErrorCode == (byte)DatasourceErrorCode.NotFoundWebId))
                {
                    result.Errors = response.res.Errors;

                    return result;
                }

                result.Elements.Add(
                        new GlobalFilterElementDTO
                        {
                            Name = response.element.Name,
                            Description = string.Empty,
                            WebId = response.element.WebId,
                            Link = $"{response.parent}/sensor?groupName={response.element.WebId}",
                            UoM = null,
                            IsChromUnit = false,
                            IsDevice = false,
                            CategoryNames = response.element.CategoryNames,
                            Step = false,
                            ValueType = null,
                        }
                    );
            }

            if (response.parent != null)
            {
                await GetGlobalFilterFromElementRecusiveAsync(response.parent, result.Elements);
            }

            result.Elements.Reverse();

            return result;
        }

        public async Task<GlobalFilterResponse> GetPreviousGlobalFilterAsync(string link, CancellationToken cancellationToken = default)
        {
            var webId = link.Split('/').FirstOrDefault();

            if (webId is null)
                throw new ArgumentException("Invalid Link");


            var result = new GlobalFilterResponse
            {
                Element = new GlobalFilterElementDTO()
            };

            var response = await RequestFilterElementAsync(webId);

            if (response.res.Errors.Count != 0 && response.res.Errors.Any(e => e.ErrorCode == (byte)DatasourceErrorCode.NotFoundWebId))
            {
                result.Errors = response.res.Errors;

                return result;
            }

            if (string.IsNullOrEmpty(response.parent) || response.parent == GetMandatoryField("GlobalFilterRoot"))
            {
                result.Element = null;
                return result;
            }
            else if (!link.Contains("groupName"))
            {
                response = await RequestFilterElementAsync(response.parent);

                if (response.res.Errors.Count != 0 && response.res.Errors.Any(e => e.ErrorCode == (byte)DatasourceErrorCode.NotFoundWebId))
                {
                    result.Errors = response.res.Errors;

                    return result;
                }
            }

            result.Element = response.element;

            return result;

        }

        private async Task<List<GlobalFilterElementDTO>> GetGlobalFilterFromElementRecusiveAsync(string webId, List<GlobalFilterElementDTO> elements)
        {
            var nextElement = await RequestFilterElementAsync(webId);

            elements.Add(nextElement.element);

            if (nextElement.parent == GetField("GlobalFilterRoot")
                || nextElement.parent == null)
            {
                return elements;
            }
            else
            {
                return await GetGlobalFilterFromElementRecusiveAsync(nextElement.parent, elements);
            }

        }

        public async Task<SensorListResponse> GetDeviceSensorsAsync(string webId, string? groupName, CancellationToken cancellationToken = default)
        {
            var result = new SensorListResponse
            {
                Sensors = new List<SensorDTO>()
            };

            var sensorFilterType = GetMandatoryField("SensorFilterType");

            string sensorFilterCondition = string.Empty;

            if (string.Equals(groupName, "ProcessValue", StringComparison.OrdinalIgnoreCase))
            {
                sensorFilterCondition = GetField("SensorFilterCondition") ?? "ProcessValue";
            }
            else if (groupName is null)
            {
                sensorFilterCondition = "*";
            }
            else
            {
                sensorFilterCondition = groupName;
            }

            if (string.IsNullOrEmpty(sensorFilterCondition))
            {
                throw new DCPBadRequestException(ErrorMessages.UnknownGroupType);
            }

            var hiddenCategories = GetHiddenCategories();

            if (string.Equals(sensorFilterType, GlobalFilterConstants.AttributeFilter, StringComparison.OrdinalIgnoreCase))
            {
                var sensorsDataUrl = $"{GetMandatoryField("BaseURL")}elements/{webId}/attributes" +
                                    $"?maxcount=4000" +
                                    $"&selectedFields=Items.WebId;Items.Name;Items.Description;Items.Type;" +
                                    $"Items.DefaultUnitsNameAbbreviation;Items.DefaultUnitsName;" +
                                    $"Items.HasChildren;Items.Step;Items.Links.Element;Items.CategoryNames;Items.IsConfigurationItem" +
                                    $"&categoryName={sensorFilterCondition}" +
                                    $"&webIdType=IDOnly";

                var attributeResponse = await GetAsync(sensorsDataUrl);

                var contentAttribute = await ValidateResponse(attributeResponse);

                if (!contentAttribute.content.HasValue)
                {
                    result.Errors.AddRange(contentAttribute.res.Errors.Where(x => x.ErrorCode == (byte)ErrorCode.NotFound)
                     .Select(x => new DatasourceError
                     {
                         Identifier = webId,
                         Error = x.Error,
                         ErrorCode = (byte)ErrorCode.DeleteDevice
                     }).ToList());

                    return result;
                }

                result.Errors.AddRange(contentAttribute.content.Value.ToDeviceDatasourceErrors());
                var itemsObject = contentAttribute.content.Value.GetProperty("Items");

                foreach (var item in itemsObject.EnumerateArray())
                {
                    if (item.TryGetProperty("IsConfigurationItem", out JsonElement isConfigElement)
                        && isConfigElement.ValueKind == JsonValueKind.False)
                    {
                        var sensor = item.ToSensor(hiddenCategories);
                        result.Sensors.Add(sensor);
                    }
                }
            }
            else if (string.Equals(sensorFilterType, GlobalFilterConstants.ElementFilter, StringComparison.OrdinalIgnoreCase))
            {
                var template = GetAvevaPIWebApiTemplate("DeviceSensorsByGroupTemplate.json");
                var templateParameters = new Dictionary<string, string>
                    {
                        {"BASEURL", GetMandatoryField("BaseURL")},
                        {"DEVICEWEBID", webId},
                        {"SENSORFILTERCONDITION", sensorFilterCondition},
                    };

                template = ReplaceTemplatePlaceholders(template, templateParameters);

                var responseSensorGroup = await PostAsync($"{GetMandatoryField("BaseURL")}/batch", template);

                var contentSensorGroup = JsonDocument.Parse(responseSensorGroup.content).RootElement;

                if (contentSensorGroup.TryGetProperty("0", out var level0) &&
                   level0.TryGetProperty("Content", out var content0Dertails) &&
                   content0Dertails.TryGetProperty("Items", out var item0Details) &&
                   item0Details.ValueKind == JsonValueKind.Array &&
                   item0Details.GetArrayLength() == 0)
                {
                    return result;
                }

                result.Errors.AddRange(contentSensorGroup.GetProperty("0").GetProperty("Content").ToDeviceDatasourceErrors().ToList());

                if (result.Errors.Count > 0)
                {
                    return result;
                }

                if (contentSensorGroup.TryGetProperty("1", out var level1) &&
                    level1.TryGetProperty("Content", out var contentDertails) &&
                    contentDertails.TryGetProperty("Items", out var itemDetails) &&
                    itemDetails.ValueKind == JsonValueKind.Array)
                {
                    foreach (var device in itemDetails.EnumerateArray())
                    {
                        if (device.TryGetProperty("Content", out var deviceContent) &&
                            deviceContent.TryGetProperty("Items", out var items) &&
                            items.ValueKind == JsonValueKind.Array)
                        {
                            foreach (var item in items.EnumerateArray())
                            {
                                if (item.TryGetProperty("IsConfigurationItem", out var isConfigElement) &&
                                    isConfigElement.ValueKind == JsonValueKind.False)
                                {
                                    var sensor = item.ToSensor(hiddenCategories);
                                    result.Sensors.Add(sensor);
                                }
                            }
                        }
                    }
                }
            }
            else if (string.Equals(sensorFilterType, GlobalFilterConstants.NoSensorFilter, StringComparison.OrdinalIgnoreCase))
            {
                var sensorsDataUrl = $"{GetMandatoryField("BaseURL")}elements/{webId}/attributes" +
                                    $"?maxcount=4000&selectedFields=Items.WebId;Items.Links.Element;Items.CategoryNames;Items.Name;Items.Description;Items.Type;" +
                                    $"Items.DefaultUnitsNameAbbreviation;Items.DefaultUnitsName;" +
                                    $"Items.Links.Element;Items.CategoryNames;Items.IsConfigurationItem;Items.Step;Items.HasChildren" +
                                    $"&webIdType=IDOnly";

                var attributeResponse = await GetAsync(sensorsDataUrl);

                var contentAttribute = await ValidateResponse(attributeResponse);

                if (!contentAttribute.content.HasValue)
                {
                    result.Errors.AddRange(contentAttribute.res.Errors.Where(x => x.ErrorCode == (byte)ErrorCode.NotFound)
                     .Select(x => new DatasourceError
                     {
                         Identifier = webId,
                         Error = x.Error,
                         ErrorCode = (byte)ErrorCode.DeleteDevice
                     }).ToList());

                    return result;
                }

                result.Errors.AddRange(contentAttribute.content.Value.ToDeviceDatasourceErrors());
                var itemsObject = contentAttribute.content.Value.GetProperty("Items");

                foreach (var item in itemsObject.EnumerateArray())
                {
                    if (item.TryGetProperty("IsConfigurationItem", out JsonElement isConfigElement)
                        && isConfigElement.ValueKind == JsonValueKind.False)
                    {
                        var sensor = item.ToSensor(hiddenCategories);
                        result.Sensors.Add(sensor);
                    }

                }
            }
            else
            {
                throw new DCPBadRequestException(ErrorMessages.UnknownSensorFilterType);
            }

            return result;
        }

        public async Task<SensorDeviceInfoResponse> GetSensorsDeviceAsync(List<string> webId, CancellationToken cancellationToken = default)
        {
            var result = new SensorDeviceInfoResponse
            {
                SensorDeviceInfo = new List<SensorDeviceInfoDTO>()
            };

            if (webId.Count == 0)
            {
                result.Errors.Add(new DatasourceError
                {
                    Error = "'webId' item must not contain values that are null, empty or whitespace.",
                });

                return result;
            }

            var filterType = GetMandatoryField("SensorFilterType");

            var sensorToDeviceWebId = webId
                    .Where(id => !string.IsNullOrWhiteSpace(id))
                    .ToDictionary(
                        sensorWebId => sensorWebId,
                        sensorWebId => "I1Em" + sensorWebId.Substring(5, 44)
                    );

            var path = string.Empty;

            if (!string.IsNullOrEmpty(GetField("GlobalFilterRoot")))
            {
                var urlForPath = $"{GetMandatoryField("BaseURL")}elements/{GetMandatoryField("GlobalFilterRoot")}/elements" +
                            "?selectedFields=Items.Path";

                var pathResponse = await GetAsync(urlForPath);
                var pathContent = await ValidateResponse(pathResponse);

                if (!pathContent.content.HasValue)
                {
                    result.Errors.AddRange(pathContent.res.Errors);
                }
                else
                {
                    path = pathContent.content.Value
                            .GetProperty("Items")
                            .EnumerateArray()
                            .Select(i => i.GetProperty("Path").GetString())
                            .FirstOrDefault();
                }
            }

            if (filterType != GlobalFilterConstants.ElementFilter)
            {
                var request = new MultiWebIdQueryDTO
                {
                    WebId = sensorToDeviceWebId.Values.Distinct().ToList()
                };

                var devicesResponse = await GetMultipleDevicesAsync(request, cancellationToken);

                result.Errors.AddRange(devicesResponse.Errors
                    .Where(e => e.ErrorCode == (byte)ErrorCode.DeleteDevice)
                    .Select(e => new DatasourceError
                    {
                        Identifier = sensorToDeviceWebId
                            .FirstOrDefault(x => x.Value == e.Identifier)
                            .Key,
                        ErrorCode = (byte)ErrorCode.DeleteSensor,
                        Error = $"WebID {e.Identifier} is not found."
                    }));

                if (result.Errors.Count() == webId.Count)
                {
                    return result;
                }

                var devicesByWebId = devicesResponse.Devices
                    .ToDictionary(d => d.WebId);

                foreach (var kvp in sensorToDeviceWebId)
                {
                    var sensorWebId = kvp.Key;
                    var deviceWebId = kvp.Value;

                    if (!devicesByWebId.TryGetValue(deviceWebId, out var device))
                        continue;

                    result.SensorDeviceInfo.Add(
                        new SensorDeviceInfoDTO
                        {
                            WebId = sensorWebId,
                            Device = device
                        });
                }
            }
            else
            {
                var template = GetAvevaPIWebApiTemplate("SensorsDevice.json");
                var templateParameters = new Dictionary<string, string>
                {
                    {"BASEURL", GetMandatoryField("BaseURL")},
                    {"DEVICEWEBIDS", string.Join("&webId=", sensorToDeviceWebId.Values.Distinct().ToList())},
                };

                template = ReplaceTemplatePlaceholders(template, templateParameters);

                var response = await PostAsync($"{GetMandatoryField("BaseURL")}/batch", template);

                var responseSections = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(response.content, DatasourceSerializerContext.Default.DictionaryStringJsonElement) ?? throw new InvalidOperationException("Failed to deserialize content");

                var identifiers = responseSections["0"].GetProperty("Content").GetProperty("Items").EnumerateArray();

                var identifierByDeviceWebId = new Dictionary<string, string>();
                var invalidIdentifiers = new List<string>();

                foreach (var i in identifiers)
                {
                    var identifier = i.GetProperty("Identifier").GetString()!;

                    if (!i.TryGetProperty("Object", out var objectProp) ||
                        !objectProp.TryGetProperty("Links", out var linksProp) ||
                        !linksProp.TryGetProperty("Parent", out var parentProp))
                    {
                        invalidIdentifiers.Add(identifier);
                        continue;
                    }

                    var parentLink = parentProp.GetString();
                    if (string.IsNullOrWhiteSpace(parentLink))
                    {
                        invalidIdentifiers.Add(identifier);
                        continue;
                    }

                    var deviceWebId = parentLink.Split('/').Last();

                    identifierByDeviceWebId[deviceWebId] = identifier;
                }

                foreach (var id in invalidIdentifiers)
                {
                    var sensorWebId = sensorToDeviceWebId
                            .FirstOrDefault(x => x.Value == id)
                            .Key;

                    result.Errors.Add(new DatasourceError
                    {
                        Identifier = sensorWebId,
                        ErrorCode = (byte)ErrorCode.DeleteSensor,
                        Error = $"WebID {sensorWebId} is not found."
                    });
                }

                if (result.Errors.Count() == webId.Count)
                {
                    return result;
                }

                var devices = responseSections["1"].GetProperty("Content").GetProperty("Items").EnumerateArray();

                var hiddenCategories = GetHiddenCategories();
                foreach (var deviceElement in devices)
                {
                    if (deviceElement.TryGetProperty("Content", out var objectProperty))
                    {
                        var device = objectProperty.ToDevice(hiddenCategories, filterType, path);
                        if (device != null)
                        {
                            identifierByDeviceWebId.TryGetValue(device.WebId, out var identifier);

                            var sensorWebId = sensorToDeviceWebId
                            .FirstOrDefault(x => x.Value == identifier)
                            .Key;

                            result.SensorDeviceInfo.Add(
                            new SensorDeviceInfoDTO
                            {
                                WebId = sensorWebId,
                                Device = device
                            });
                        }
                    }
                }
            }

            return result;
        }

        public async Task<GlobalFilterResponse> GetGlobalFilterElementAsync(GlobalFilterElementByPathRequest model, CancellationToken cancellationToken = default)
        {
            if (model?.Path == null || model.Path.Count == 0)
                throw new ArgumentException("Invalid input model");

            Func<JsonElement, (GlobalFilterElementDTO data, List<DatasourceError> errors, List<DatasourceWarning> warnings)> function;

            var result = new GlobalFilterResponse
            {
                Element = new GlobalFilterElementDTO(),
                Errors = new List<DatasourceError>(),
                Warnings = new List<DatasourceWarning>()
            };

            string gFilterType = GetMandatoryField("SensorFilterType");

            if (gFilterType == "AttributeFilter")
            {
                model.Path.RemoveAll(x => string.Equals(x.Level, "group", StringComparison.OrdinalIgnoreCase));
            }

            var names = model.Path
                    .Where(p => !string.IsNullOrWhiteSpace(p.Name))
                    .Select(p => p.Name)
                    .ToList();

            var (basePath, errors) = await GetRootPathAsync();

            if (errors.Any())
            {
                result.Errors.AddRange(errors);
                return result;
            }

            string encodedPath;
            string url;
            HttpResponseMessage response;

            if (model.Path.Last().Level == "level" || model.Path.Last().Level == "group")
            {
                function = this.ProcessSingleGlobalFilterElementResponse;

                var pathPart = string.Join("\\", names);

                encodedPath = Uri.EscapeDataString($"{basePath}\\{pathPart}");

                if (gFilterType == "ElementFilter")
                {
                    url = $"{GetMandatoryField("BaseURL")}elements" +
                      $"?path={encodedPath}" +
                      "&selectedFields=WebId;Name;Description;HasChildren;CategoryNames;Links.Elements;Links.Attributes;Links.Parent" +
                      "&webIdType=IDOnly";
                }
                else
                {
                    url = $"{GetMandatoryField("BaseURL")}elements" +
                      $"?path={encodedPath}" +
                      "&selectedFields=WebId;Name;Description;HasChildren;CategoryNames;Links.Elements;Links.Attributes" +
                      "&webIdType=IDOnly";
                }

                response = await GetAsync(url);
            }
            else
            {
                function = this.ProcessSingleGlobalFilterAttributeResponse;

                var attributePath = $"{basePath}\\{string.Join("\\", names.Take(names.Count - 1))}|{names.Last()}";
                encodedPath = Uri.EscapeDataString(attributePath);

                url = $"{GetMandatoryField("BaseURL")}attributes?path={encodedPath}" +
                      "&selectedFields=WebId;Name;Description;Type;" +
                      "DefaultUnitsNameAbbreviation;DefaultUnitsName;" +
                      "HasChildren;Step;IsConfigurationItem;CategoryNames" +
                      "&webIdType=IDOnly";

                response = await GetAsync(url);
            }

            var content = await ValidateResponse(response);

            if (!content.content.HasValue)
            {
                result.Errors = content.res.Errors;
                return result;
            }

            var section = function(content.content.Value);

            result.Element = section.data;
            result.Warnings = section.warnings;
            result.Errors = section.errors;

            return result;
        }

        public async Task<GlobalFilterListResponse> SearchGlobalFilterElementsAsync(GlobalFilterElementQueryDTO model, CancellationToken cancellationToken = default)
        {
            var result = new GlobalFilterListResponse
            {
                Elements = new List<GlobalFilterElementDTO>(),
                Errors = new List<DatasourceError>(),
                Warnings = new List<DatasourceWarning>()
            };

            Func<JsonElement, (List<GlobalFilterElementDTO> data, List<DatasourceError> errors, List<DatasourceWarning> warnings)> function;

            if (model?.MetaData == null || model.MetaData.Count == 0)
                throw new ArgumentException("MetaData required");

            string urlTemplate = $"{GetMandatoryField("BaseURL")}/assetdatabases/{GetMandatoryField("WebId")}/elementattributes" +
                                 $"?searchFullHierarchy=true" +
                                 $"&attributeNameFilter={{{{FILTERNAME}}}}" +
                                 "&selectedFields=Items.Name;Items.Links.Element;Items.WebId" +
                                 "&webIdType=IDOnly";

            var templateParameterSets = model.MetaData
                .Select((x, index) => new
                {
                    Value = new Dictionary<string, string> {
                      {  "FILTERNAME", x.Name },
                },
                    Id = index.ToString()
                })
                .ToDictionary(x => x.Id, z => z.Value);

            var sections = await PerformMultiGetAsync(
                urlTemplate,
                templateParameterSets,
                this.ParseElementAttributes);

            var data = sections.Where(x => x.Value.data != null).SelectMany(x => x.Value.data).ToList();

            var matchingElements = data
                .Where(g => g.Value.Count == model.MetaData.Count)
                .ToDictionary(k => k.Key, v => v.Value);

            var request = new InterpolatedAtTimeDataQueryDTO
            {
                Time = new List<DateTime> { DateTime.UtcNow },
                WebId = matchingElements.SelectMany(m => m.Value).ToList(),
            };

            var values = await GetInterpolatedDataAtTimesAsync(request, cancellationToken);

            var attributeValueLookup = values.Data.ToDictionary(
                d => d.WebId,
                d => d.DataPoints.FirstOrDefault()?.Value
            );

            var filteredElements = matchingElements
                .Where(element =>
                    element.Value.All(attributeWebId =>
                    {

                        if (!attributeValueLookup.TryGetValue(attributeWebId, out var attr))
                            return false;

                        var meta = model.MetaData.FirstOrDefault(m =>
                            string.Equals(m.Value, attr, StringComparison.OrdinalIgnoreCase)
                        );

                        if (meta == null) return false;

                        return true;
                    })
                )
                .Select(k => k.Key).ToList();

            if (filteredElements != null && filteredElements.Count != 0)
            {
                var url = $"{GetMandatoryField("BaseURL")}elements/multiple" +
               $"?webId={string.Join("&webId=", filteredElements)}" +
               $"&selectedFields=Items.Object.WebId;Items.Object.Name;Items.Object.Description;Items.Object.HasChildren;Items.Object.CategoryNames;Items.Object.Links.Elements;Items.Object.Links.Attributes;Items.Exception" +
               $"&webIdType=IDOnly";

                function = this.ProcessGlobalFilterElementResponse;

                var elementResponse = await GetAsync(url);
                var elementContent = await ValidateResponse(elementResponse);

                if (!elementContent.content.HasValue)
                {
                    result.Errors = elementContent.res.Errors;
                    return result;
                }

                var section = function(elementContent.content.Value);

                result.Elements = section.data;
                result.Warnings = section.warnings;
                result.Errors = section.errors;
            }

            return result;
        }

        private (Dictionary<string, List<string>> data, List<DatasourceError> errors, List<DatasourceWarning> warnings) ParseElementAttributes(JsonElement jsonElement, string key)
        {
            var result = (
                data: new Dictionary<string, List<string>>(),
                errors: new List<DatasourceError>(),
                warnings: new List<DatasourceWarning>()
            );

            result.errors = jsonElement.ToEventframeDatasourceErrors();

            if (result.errors.Any())
            {
                return result;
            }
            var elementGroups = new Dictionary<string, List<string>>();

            if (jsonElement.ValueKind == JsonValueKind.Object && jsonElement.TryGetProperty("Items", out var items))
            {
                foreach (var item in items.EnumerateArray())
                {
                    var elementLink = item.GetProperty("Links").GetProperty("Element").GetString() ?? throw new InvalidOperationException("Missing element link");
                    var elementWebId = elementLink.Split('/').Last();
                    var attributeWebId = item.GetProperty("WebId").GetString() ?? throw new InvalidOperationException("Missing attribute web id");

                    if (!elementGroups.ContainsKey(elementWebId))
                        elementGroups[elementWebId] = new List<string>();

                    elementGroups[elementWebId].Add(attributeWebId);
                }
            }

            result.data = elementGroups;

            return result;
        }

        public async Task<GroupedSensorListResponse> GetGroupedDeviceSensorsAsync(string webId, CancellationToken cancellationToken = default)
        {
            var result = new GroupedSensorListResponse
            {
                Groups = new List<SensorGroupDTO>()
            };

            var sensorFilterType = GetMandatoryField("SensorFilterType");
            var hiddenCategories = GetHiddenCategories();

            if (string.Equals(sensorFilterType, GlobalFilterConstants.AttributeFilter, StringComparison.OrdinalIgnoreCase)
                || string.Equals(sensorFilterType, GlobalFilterConstants.NoSensorFilter, StringComparison.OrdinalIgnoreCase))
            {
                var sensorsDataUrl = $"{GetMandatoryField("BaseURL")}elements/{webId}/attributes" +
                                     $"?maxcount=4000&selectedFields=Items.WebId;Items.Name;Items.Description;Items.Type;" +
                                     $"Items.DefaultUnitsNameAbbreviation;Items.DefaultUnitsName;" +
                                     $"Items.HasChildren;Items.Step;Items.Links.Element;Items.CategoryNames;Items.IsConfigurationItem" +
                                     $"&webIdType=IDOnly";

                var attributeResponse = await GetAsync(sensorsDataUrl);

                var contentAttribute = await ValidateResponse(attributeResponse);

                if (!contentAttribute.content.HasValue)
                {
                    result.Errors.AddRange(contentAttribute.res.Errors.Where(x => x.ErrorCode == (byte)ErrorCode.NotFound)
                     .Select(x => new DatasourceError
                     {
                         Identifier = webId,
                         Error = x.Error,
                         ErrorCode = (byte)ErrorCode.DeleteDevice
                     }).ToList());

                    return result;
                }

                result.Errors.AddRange(contentAttribute.content.Value.ToDeviceDatasourceErrors());
                var itemsObject = contentAttribute.content.Value.GetProperty("Items");

                var sensors = new List<SensorDTO>();

                foreach (var item in itemsObject.EnumerateArray())
                {
                    if (item.TryGetProperty("IsConfigurationItem", out JsonElement isConfigElement)
                        && isConfigElement.ValueKind == JsonValueKind.False)
                    {
                        var sensor = item.ToSensor(hiddenCategories);
                        sensors.Add(sensor);
                    }
                }

                result.Groups = sensors
                    .GroupBy(x => x.CategoryNames.FirstOrDefault())
                    .Select(x => new SensorGroupDTO
                    {
                        GroupName = x.Key,
                        Sensors = x.ToList()
                    })
                    .ToList();
            }
            else if (string.Equals(sensorFilterType, GlobalFilterConstants.ElementFilter, StringComparison.OrdinalIgnoreCase))
            {
                var template = GetAvevaPIWebApiTemplate("DeviceSensorsByGroupTemplate.json");
                var templateParameters = new Dictionary<string, string>
                    {
                        {"BASEURL", GetMandatoryField("BaseURL")},
                        {"DEVICEWEBID", webId},
                        {"SENSORFILTERCONDITION", "*"},
                    };

                template = ReplaceTemplatePlaceholders(template, templateParameters);

                var responseSensorGroup = await PostAsync($"{GetMandatoryField("BaseURL")}/batch", template);

                var contentSensorGroup = JsonDocument.Parse(responseSensorGroup.content).RootElement;

                // Read level 0 group metadata
                if (contentSensorGroup.TryGetProperty("0", out var level0) &&
                   level0.TryGetProperty("Content", out var content0Dertails) &&
                   content0Dertails.TryGetProperty("Items", out var item0Details) &&
                   item0Details.ValueKind == JsonValueKind.Array &&
                   item0Details.GetArrayLength() == 0)
                {
                    return result;
                }

                result.Errors.AddRange(contentSensorGroup.GetProperty("0").GetProperty("Content").ToDeviceDatasourceErrors().ToList());

                if (result.Errors.Count > 0)
                {
                    return result;
                }

                // Read level 1 groups
                if (contentSensorGroup.TryGetProperty("1", out var level1) &&
                    level1.TryGetProperty("Content", out var contentDertails) &&
                    contentDertails.TryGetProperty("Items", out var itemDetails) &&
                    itemDetails.ValueKind == JsonValueKind.Array)
                {

                    using var groupMetaEnumerator = contentSensorGroup.GetProperty("0").GetProperty("Content").GetProperty("Items").EnumerateArray();
                    using var groupEnumerator = contentSensorGroup.GetProperty("1").GetProperty("Content").GetProperty("Items").EnumerateArray();

                    while (groupMetaEnumerator.MoveNext() && groupEnumerator.MoveNext())
                    {
                        var groupMeta = groupMetaEnumerator.Current;
                        var group = groupEnumerator.Current;

                        var sensorGroup = new SensorGroupDTO
                        {
                            GroupName = groupMeta.TryGetProperty("Name", out var name)
                                ? name.GetString()
                                : string.Empty,
                            Sensors = new List<SensorDTO>()
                        };

                        if (group.TryGetProperty("Content", out var deviceContent) &&
                            deviceContent.TryGetProperty("Items", out var sensors) &&
                            sensors.ValueKind == JsonValueKind.Array)
                        {
                            foreach (var item in sensors.EnumerateArray())
                            {
                                if (item.TryGetProperty("IsConfigurationItem", out var isConfig) &&
                                    isConfig.ValueKind == JsonValueKind.False)
                                {
                                    sensorGroup.Sensors.Add(item.ToSensor(hiddenCategories));
                                }
                            }
                        }

                        result.Groups.Add(sensorGroup);
                    }
                }

            }
            else
            {
                throw new DCPBadRequestException(ErrorMessages.UnknownSensorFilterType);
            }

            return result;
        }

        public async Task<string> GetPreviousGlobalFilterWebIdAsync(string webId, CancellationToken cancellationToken = default)
        {
            var url = $"{GetMandatoryField("BaseURL")}elements/{webId}" +
                $"?selectedFields=Links.Parent" +
                $"&webIdType=IDOnly";

            var response = await GetAsync(url);
            var content = await ValidateResponse(response);

            return content.content!.Value
                .GetProperty("Links")
                .GetProperty("Parent")
                .GetString()!
                .Split('/')
                .LastOrDefault();

        }

        public async Task<GlobalFilterLevelResponse> GetFullHierarchyLevelsAsync(string webId, CancellationToken cancellationToken = default)
        {
            var result = new GlobalFilterLevelResponse
            {
                Levels = new List<GlobalFilterLevelDTO>(),
                Errors = new List<DatasourceError>(),
            };

            var baseUrl = GetMandatoryField("BaseURL");

            bool isSensorWebId = string.Equals(webId.Substring(2, 2), "Ab", StringComparison.OrdinalIgnoreCase);
            string resourceType = isSensorWebId ? "attributes" : "elements";

            var pathResponse = await GetAsync($"{baseUrl}{resourceType}/{webId}?selectedFields=Path");
            var pathContent = await ValidateResponse(pathResponse);
            if (!pathContent.content.HasValue && pathContent.res.Errors.Count != 0 && pathContent.res.Errors.Any(e => e.ErrorCode == (byte)ErrorCode.NotFound))
            {
                result.Errors.Add(new DatasourceError
                {
                    Identifier = webId,
                    ErrorCode = (byte)DatasourceErrorCode.NotFoundWebId,
                    Error = pathContent.res.Errors.FirstOrDefault()?.Error
                });

                return result;
            }
            else if (!pathContent.content.HasValue)
            {
                result.Errors = pathContent.res.Errors;

                return result;
            }

            string fullPath = pathContent.content.Value.GetProperty("Path").GetString()
            ?? throw new Exception("Missing full path value");

            var (rootPath, errors) = await GetRootPathAsync();

            if (errors.Any())
            {
                result.Errors.AddRange(errors);
                return result;
            }

            var batchRequest = BuildHierarchyBatchRequest(fullPath, rootPath, isSensorWebId);

            var jsonBody = JsonSerializer.Serialize(batchRequest);
            var batchResponse = await PostAsync($"{baseUrl}batch", jsonBody);

            if (batchResponse.errors != null && batchResponse.errors.Any())
            {
                result.Errors.AddRange(batchResponse.errors);
                return result;
            }

            int pathDepth = fullPath.Replace(rootPath, "").Split(new[] { '\\' }, StringSplitOptions.RemoveEmptyEntries).Length;

            return await ProcessBatchHierarchyResponse(batchResponse.res, pathDepth, result, webId, isSensorWebId);
        }

        private Dictionary<string, object> BuildHierarchyBatchRequest(string fullPath, string rootPath, bool isSensorWebId)
        {
            string[] elementParts;
            if (isSensorWebId)
            {
                var mainSplit = fullPath.Split('|');
                var pathWithoutSensor = mainSplit[0].Replace(rootPath, "").TrimStart('\\');
                elementParts = pathWithoutSensor.Split(new[] { '\\' }, StringSplitOptions.RemoveEmptyEntries);
            }
            else
            {
                var pathWithoutRoot = fullPath.Replace(rootPath, "").TrimStart('\\');
                elementParts = pathWithoutRoot.Split(new[] { '\\' }, StringSplitOptions.RemoveEmptyEntries);
            }

            var batchRequest = new Dictionary<string, object>();

            string rootResource = string.IsNullOrEmpty(GetField("GlobalFilterRoot"))
            ? $"{GetMandatoryField("BaseURL")}assetdatabases/{GetMandatoryField("WebId")}/elements"
            : $"{GetMandatoryField("BaseURL")}elements/{GetField("GlobalFilterRoot")}/elements";

            batchRequest.Add("root", new
            {
                Method = "GET",
                Resource = $"{rootResource}?selectedFields=Items.WebId;Items.Name;Items.Description;Items.HasChildren;Items.CategoryNames;Items.Links.Elements;Items.Links.Parent" +
                $"&webIdType=IDonly"
            });

            string currentPathBuilder = rootPath;
            var pathElementIndex = isSensorWebId ? elementParts.Length : elementParts.Length - 1;

            for (int i = 0; i < pathElementIndex; i++)
            {
                currentPathBuilder += "\\" + elementParts[i];

                var escapedPath = Uri.EscapeDataString(currentPathBuilder);

                string metaKey = $"meta_{i + 1}";
                string optionsKey = $"options_{i + 1}";

                batchRequest.Add(metaKey, new
                {
                    Method = "GET",
                    Resource = $"{GetMandatoryField("BaseURL")}elements?path={escapedPath}&selectedFields=WebId&webIdType=IDonly"
                });

                if (isSensorWebId && i == pathElementIndex - 1)
                {
                    batchRequest.Add(optionsKey, new
                    {
                        Method = "GET",
                        Resource = $"{GetMandatoryField("BaseURL")}elements/{{0}}/attributes" +
                        $"?selectedFields=Items.WebId;Items.Name;Items.Description;Items.Type;" +
                        $"Items.DefaultUnitsNameAbbreviation;Items.DefaultUnitsName;" +
                        $"Items.HasChildren;Items.Step;Items.IsConfigurationItem;Items.CategoryNames" +
                        $"&webIdType=IDOnly",
                        Parameters = new[] { $"$.{metaKey}.Content.WebId" },
                        ParentIds = new[] { metaKey }
                    });
                }
                else
                {
                    batchRequest.Add(optionsKey, new
                    {
                        Method = "GET",
                        Resource = $"{GetMandatoryField("BaseURL")}elements/{{0}}/elements" +
                        $"?selectedFields=Items.WebId;Items.Name;Items.Description;Items.HasChildren;Items.CategoryNames;Items.Links.Elements;Items.Links.Parent" +
                        $"&webIdType=IDonly",
                        Parameters = new[] { $"$.{metaKey}.Content.WebId" },
                        ParentIds = new[] { metaKey }
                    });
                }
            }

            return batchRequest;
        }

        private async Task<GlobalFilterLevelResponse> ProcessBatchHierarchyResponse(
        HttpResponseMessage batchResponse,
        int pathDepth,
        GlobalFilterLevelResponse result,
        string originalWebId,
        bool isSensorWebId)
        {
            string gFilterType = GetMandatoryField("SensorFilterType");

            var content = await ValidateResponse(batchResponse);
            if (!content.content.HasValue)
            {
                result.Errors = content.res.Errors;
                return result;
            }

            var batchJson = content.content.Value;

            if (batchJson.TryGetProperty("root", out JsonElement rootSection) &&
                rootSection.GetProperty("Status").GetInt32() == (int)HttpStatusCode.OK)
            {
                string selectedId = string.Empty;

                if (batchJson.TryGetProperty("meta_1", out JsonElement m1))
                {
                    int metaStatus = m1.GetProperty("Status").GetInt32();

                    if (metaStatus == (int)HttpStatusCode.OK)
                    {
                        selectedId = m1.GetProperty("Content").GetProperty("WebId").GetString()
                            ?? throw new ArgumentNullException("Missing webId");
                    }
                    else
                    {
                        throw new ArgumentNullException("Meta request failed");
                    }
                }
                else
                {
                    // Batch request don't have a meta request for the final WebId (as we already have it from the initial request)
                    selectedId = originalWebId;
                }

                result.Levels.Add(new GlobalFilterLevelDTO
                {
                    Id = selectedId,
                    Options = ProcessGlobalFilterElementResponse(rootSection.GetProperty("Content")).data
                });
            }

            for (int i = 1; i <= pathDepth; i++)
            {
                string optionsKey = $"options_{i}";
                string metaKey = $"meta_{i + 1}";

                if (batchJson.TryGetProperty(optionsKey, out JsonElement optSection) &&
                    optSection.GetProperty("Status").GetInt32() == (int)HttpStatusCode.OK)
                {
                    string selectedId = string.Empty;

                    if (batchJson.TryGetProperty(metaKey, out JsonElement m1))
                    {
                        int metaStatus = m1.GetProperty("Status").GetInt32();

                        if (metaStatus == (int)HttpStatusCode.OK)
                        {
                            selectedId = m1.GetProperty("Content").GetProperty("WebId").GetString()
                                ?? throw new ArgumentNullException("Missing webId");
                        }
                        else
                        {
                            throw new ArgumentNullException("Meta request failed");
                        }
                    }
                    else
                    {
                        // Batch request don't have a meta request for the final WebId (as we already have it from the initial request)
                        selectedId = originalWebId;
                    }

                    var parent = result.Levels.Last();
                    var parentIsDevice = parent.Options.First(x => x.WebId == parent.Id).IsDevice;

                    if (isSensorWebId && selectedId == originalWebId)
                    {
                        var sensors = new GlobalFilterLevelDTO
                        {
                            Id = selectedId,
                            Options = ProcessGlobalFilterAttributeResponse(optSection.GetProperty("Content")).data
                        };

                        if (gFilterType == "AttributeFilter")
                        {
                            result.Levels.Add(new GlobalFilterLevelDTO
                            {
                                Id = sensors.Options.First(x => x.WebId == selectedId).CategoryNames.First(),
                                Options = ProcessGlobalFilterAttributeGroupResponse(optSection.GetProperty("Content"), parent.Id).data
                            });
                        }

                        result.Levels.Add(sensors);
                    }
                    else if (parentIsDevice && gFilterType == "ElementFilter")
                    {
                        result.Levels.Add(new GlobalFilterLevelDTO
                        {
                            Id = selectedId,
                            Options = ProcessGlobalFilterElementGroupResponse(optSection.GetProperty("Content"), parent.Id).data
                        });
                    }
                    else
                    {
                        result.Levels.Add(new GlobalFilterLevelDTO
                        {
                            Id = selectedId,
                            Options = ProcessGlobalFilterElementResponse(optSection.GetProperty("Content")).data
                        });
                    }
                }
            }

            return result;
        }

        private async Task<(string path, List<DatasourceError> errors)> GetRootPathAsync()
        {
            var errors = new List<DatasourceError>();
            string? rootId = GetField("GlobalFilterRoot");
            string baseUrl = GetMandatoryField("BaseURL");

            string basePathUrl = string.IsNullOrEmpty(rootId)
                ? $"{baseUrl}assetdatabases/{GetMandatoryField("WebId")}?selectedFields=Path"
                : $"{baseUrl}elements/{rootId}?selectedFields=Path";

            var responsePath = await GetAsync(basePathUrl);
            var contentPath = await ValidateResponse(responsePath);

            if (!contentPath.content.HasValue)
            {
                return (string.Empty, contentPath.res.Errors);
            }

            var jsonObject = contentPath.content.Value;

            if (!jsonObject.TryGetProperty("Path", out JsonElement pathElement))
            {
                errors.Add(new DatasourceError
                {
                    Error = "Path not found in response",
                    ErrorCode = (byte)ErrorCode.NotFound
                });
                return (string.Empty, errors);
            }

            return (pathElement.GetString() ?? string.Empty, errors);
        }

        private List<string> GetHiddenCategories()
        {
            return _connectionString?["HiddenCategories"]
               ?.Deserialize<List<string>>(DatasourceSerializerContext.Default.ListString)
               ?? new List<string>();
        }
    }
}