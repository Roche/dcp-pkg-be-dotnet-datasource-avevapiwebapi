using OpenDCP.Framework.Configuration.Model.Errors;
using OpenDCP.Framework.Configuration.Model.Interfaces;
using OpenDCP.Framework.Configuration.Model.Responses.Datasource;
using OpenDCP.Framework.Configuration.Model.Warnings;
using OpenDCP.Framework.Datasource.AVEVAPIwebAPI.Constants;
using OpenDCP.Framework.Datasource.AVEVAPIwebAPI.Helpers;
using OpenDCP.Framework.Datasource.AVEVAPIwebAPI.JsonSerializerContexts;
using OpenDCP.Framework.DataTypes.Equipment.Constants;
using OpenDCP.Framework.DataTypes.Equipment.Entities.EventframeContext;
using OpenDCP.Framework.DataTypes.Equipment.Entities.SensorData;
using OpenDCP.Framework.DataTypes.Equipment.Interfaces;
using OpenDCP.Framework.DataTypes.Equipment.Requests.DataBundle;
using OpenDCP.Framework.DataTypes.Equipment.Requests.EventFrameContext;
using OpenDCP.Framework.DataTypes.Equipment.Responses.AssetHierarchy;
using OpenDCP.Framework.DataTypes.Equipment.Responses.EventFrameContext;
using OpenDCP.Framework.DataTypes.Equipment.Responses.SensorData;
using OpenDCP.Framework.Exceptions;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;

namespace OpenDCP.Framework.Datasource.AVEVAPIwebAPI.Datasource
{
    public partial class AVEVAPIwebAPIDatasource : IEventframeContextDataSource, IEventTranslation
    {
        public async Task<BatchContextListResponse> SearchBatchContextByTimeAsync(BatchContextTimeQueryDTO model, CancellationToken cancellationToken = default)
        {
            var result = new BatchContextListResponse
            {
                Eventframes = new List<BatchContextDTO>()
            };

            var urlTemplate = $"{GetMandatoryField("BaseURL")}elements/{{{{DEVICEWEBID}}}}/eventframes" +
                              $"?startTime={model.StartTime.ToIso8601String()}&endTime={model.EndTime.ToIso8601String()}" +
                              $"&searchMode={model.SearchMode}" +
                              $"&categoryName=DC_Asset" +
                              $"&maxCount=3000" +
                              $"&selectedFields=Items.WebId;Items.StartTime;Items.EndTime;Items.Id;Items.RefElementWebIds;Items.HasChildren" +
                              $"&webIdType=IDOnly";

            var templateParameterSets = model.DeviceWebId
                .Select((x, index) => new
                {
                    Value = new Dictionary<string, string> {
                      {  "DEVICEWEBID", x },
                },
                    Id = index.ToString()
                })
                .ToDictionary(x => x.Id, z => z.Value);

            var sections = await PerformMultiGetAsync<List<(BatchContextDTO batchContext, bool hasChildren)>>(urlTemplate, templateParameterSets, this.ProcessBatchContextList);

            if (sections.SelectMany(x => x.Value.errors).Any(err => err.ErrorCode == (byte)ErrorCode.NotFound))
            {
                var sectionList = sections.ToList();
                for (int i = 0; i < sectionList.Count; i++)
                {
                    var section = sectionList[i];
                    if (section.Value.errors.Any(err => err.ErrorCode == (byte)ErrorCode.NotFound))
                    {
                        result.Errors.Add(new DatasourceError
                        {
                            Identifier = model.DeviceWebId[i],
                            ErrorCode = (byte)DatasourceErrorCode.NotFoundWebId,
                            Error = section.Value.errors.FirstOrDefault()?.Error
                        });
                    }
                }

                if (sections.SelectMany(x => x.Value.errors).All(err => err.ErrorCode == (byte)ErrorCode.NotFound) && result.Errors.Count() == model.DeviceWebId.Count())
                {
                    return result;
                }
            }
            else
            {
                result.Errors = sections.SelectMany(x => x.Value.errors).ToList();
            }

            var allData = sections
                .Where(x => x.Value.data != null)
                .SelectMany(x => x.Value.data)
                .ToList();

            result.Eventframes = allData
                .Select(x => x.batchContext)
                .ToList();

            var contextsWithChildren = allData
                .Where(x => x.hasChildren)
                .ToDictionary(
                    x => EventFrameId2WebId(x.batchContext.Id),
                    x => x.batchContext.DeviceWebId
                );

            if (contextsWithChildren.Count > 0)
            {
                await GetChildBatchContextByIdAsync(contextsWithChildren, result, cancellationToken);
            }

            FilterBatchContext(result, model);

            result.Warnings = sections.SelectMany(x => x.Value.warnings).ToList();

            return result;
        }

        public async Task<EventframeListResponse> SearchEventframeContextByTimeAsync(EventframeContextTimeQueryDTO model, CancellationToken cancellationToken = default)
        {
            if (model.DeviceWebId.Count == 1)
            {
                return await SearchEventframeContextByTimeSingleDeviceAsync(model, cancellationToken);
            }
            else if (model.DeviceWebId.Count > 1)
            {
                return await SearchEventframeContextByTimeMultiDeviceAsync(model, cancellationToken);
            }

            throw new DCPBadRequestException(AvevaErrorMessages.NotValidWebId);
        }

        private async Task<EventframeListResponse> SearchEventframeContextByTimeSingleDeviceAsync(EventframeContextTimeQueryDTO model, CancellationToken cancellationToken)
        {
            var result = new EventframeListResponse
            {
                Eventframes = new List<EventFrameDTO>(),
                Attributes = new List<EventFrameAttributeDTO>()
            };

            var template = GetAvevaPIWebApiTemplate("SingleDeviceEventframes.json");

            var templateParameters = new Dictionary<string, string>
            {
                {"BASEURL", GetMandatoryField("BaseURL")},
                {"DEVICEWEBID", model.DeviceWebId.First()},
                {"STARTTIME", model.StartTime.ToIso8601String() },
                {"ENDTIME", model.EndTime.ToIso8601String()},
                {"SEARCHMODE", model.SearchMode.ToString()},
            };

            template = ReplaceTemplatePlaceholders(template, templateParameters);

            var response = await PostAsync($"{GetMandatoryField("BaseURL")}/batch", template);

            if (response.errors.Count != 0 && response.errors.Any(e => e.ErrorCode == (byte)DatasourceErrorCode.NotFound))
            {
                result.Errors.Add(new DatasourceError
                {
                    Identifier = model.DeviceWebId.FirstOrDefault(),
                    ErrorCode = (byte)ErrorCode.DeleteDevice,
                    Error = response.content
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

            var eventframes = responseSections["0"]
                .GetProperty("Content")
                .GetProperty("Items")
                .EnumerateArray();

            if (eventframes.Count() == 0)
            {
                return result;
            }

            var eventframesValues = responseSections["1"]
                .GetProperty("Content")
                .GetProperty("Items");

            var usedTemplates = new List<string>();
            int i = 0;


            var eventFrameWithChildrenIds = new List<string>();
            var parentWebIds = new List<string>();

            bool includeName = GetIncludeName(_connectionString!);

            var allFetchedNodes = new Dictionary<string, EventFrameDTO>();

            foreach (var eventframe in eventframes)
            {
                var item = new EventFrameDTO
                {
                    Id = eventframe.GetProperty("Id").GetString(),
                    StartTime = eventframe.GetProperty("StartTime").GetString(),
                    EndTime = eventframe.GetProperty("EndTime").GetString(),
                    RefWebId = model.DeviceWebId.First(),
                    Values = new List<EventFrameValueDTO>()
                };

                if (includeName)
                {
                    item.Values.Add(new EventFrameValueDTO
                    {
                        Name = "Name",
                        Value = eventframe.GetProperty("Name").GetString(),
                        Good = true
                    });
                }

                if (eventframe.TryGetProperty("Links", out var links))
                {
                    if (links.TryGetProperty("Template", out var templateProp))
                    {
                        string? templateUrl = templateProp.GetString();
                        if (!string.IsNullOrEmpty(templateUrl))
                        {
                            usedTemplates.Add(templateUrl.Split("/").Last());
                        }
                    }

                    if (links.TryGetProperty("Parent", out var parentProp))
                    {
                        string? parentUrl = parentProp.GetString();
                        if (!string.IsNullOrEmpty(parentUrl))
                        {
                            parentWebIds.Add(parentUrl.Split("/").Last());
                            item.RefWebId = parentUrl.Split("/").Last();
                        }
                    }
                }

                foreach (var value in eventframesValues[i].GetProperty("Content").GetProperty("Items").EnumerateArray())
                {
                    var val = value.ToEventframeValue();

                    item.Values.Add(val);
                }

                i++;

                if (eventframe.TryGetProperty("HasChildren", out var hasChildren) && hasChildren.GetBoolean())
                {
                    eventFrameWithChildrenIds.Add(item.Id!);
                }

                allFetchedNodes[EventFrameId2WebId(item.Id!)] = item;

                result.Eventframes.Add(item);
            }

            HashSet<string> requestedWebIds = new HashSet<string>();

            if (eventFrameWithChildrenIds.Any() || parentWebIds.Any())
            {
                await GetChildEventframeContextByIdAsync(eventFrameWithChildrenIds, parentWebIds, result, allFetchedNodes, requestedWebIds, cancellationToken);
            }

            FilterEventFrames(result, model);

            foreach (var ef in result.Eventframes)
            {
                ResolveInheritance(ef, allFetchedNodes);
                ef.RefWebId = model.DeviceWebId.First();
            }

            if (model.IncludeAttributeSettings
                && result.Eventframes.Count > 0)
            {

                var templates = await GetEventframeTemplatesAsync(usedTemplates.Distinct().ToList(), cancellationToken);

                result.Warnings.AddRange(templates.Warnings);
                result.Errors.AddRange(templates.Errors);

                result.Attributes = templates.Templates
                    .SelectMany(x => x.Attributes)
                    .GroupBy(x => x.Name)
                    .Select(g => new EventFrameAttributeDTO
                    {
                        Name = g.Key,
                        Description = g.First().Description,
                        UoM = g.First().UoM,
                        ValueType = DataTypePromoting(g.Select(x => x.ValueType).ToList()),
                    })
                    .ToList();

                if (includeName)
                {
                    result.Attributes.Add(new EventFrameAttributeDTO
                    {
                        Name = "Name",
                        Description = "EventFrame Name",
                        UoM = "dim",
                        ValueType = "String"
                    });
                }
            }

            result.Eventframes = result.Eventframes
                .OrderBy(ef => ef.StartTime)
                .ToList();

            return result;
        }

        private async Task<EventframeListResponse> SearchEventframeContextByTimeMultiDeviceAsync(EventframeContextTimeQueryDTO model, CancellationToken cancellationToken)
        {
            var result = new EventframeListResponse
            {
                Eventframes = new List<EventFrameDTO>(),
                Attributes = new List<EventFrameAttributeDTO>()
            };

            var template = GetAvevaPIWebApiTemplate("MultiDeviceEventframes.json");
            var templateParameters = new Dictionary<string, string>
            {
                {"BASEURL", GetMandatoryField("BaseURL")},
                {"DEVICEWEBIDS", string.Join("&webId=", model.DeviceWebId)},
                {"STARTTIME", model.StartTime.ToIso8601String()},
                {"ENDTIME", model.EndTime.ToIso8601String()},
                {"SEARCHMODE", model.SearchMode.ToString()},
            };

            template = ReplaceTemplatePlaceholders(template, templateParameters);

            var response = await PostAsync($"{GetMandatoryField("BaseURL")}/batch", template);

            var responseSections = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(response.content, DatasourceSerializerContext.Default.DictionaryStringJsonElement) ?? throw new InvalidOperationException("Failed to deserialize content");

            result.Errors.AddRange(responseSections["1"]
            .GetProperty("Content")
            .GetProperty("Items")
            .EnumerateArray().SelectMany(x => x.ToDeviceDatasourceErrors()).ToList());

            var devices = responseSections["2"].GetProperty("Content").GetProperty("Items").EnumerateArray();

            if (responseSections["3"].GetProperty("Status").GetInt16() == 400)
            {
                return result;
            }

            var eventframesValues = responseSections["3"].GetProperty("Content").GetProperty("Items");

            var usedTemplates = new List<string>();
            var eventFrameWithChildrenIds = new List<string>();
            var parentWebIds = new List<string>();
            var eventFrameToDeviceMap = new Dictionary<string, string>();
            var allFetchedNodes = new Dictionary<string, EventFrameDTO>();

            int valueIndex = 0;

            bool includeName = GetIncludeName(_connectionString!);

            foreach (var deviceElement in devices)
            {
                var eventframes = deviceElement.GetProperty("Content").GetProperty("Items").EnumerateArray();

                foreach (var eventframe in eventframes)
                {
                    string? matchedRefWebId = null;

                    if (eventframe.TryGetProperty("RefElementWebIds", out var refElementWebIdsProp) &&
                        refElementWebIdsProp.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var refId in refElementWebIdsProp.EnumerateArray())
                        {
                            string? refIdString = refId.GetString();

                            if (refIdString != null && model.DeviceWebId.Contains(refIdString))
                            {
                                matchedRefWebId = refIdString;
                                break;
                            }
                        }
                    }

                    var ef = new EventFrameDTO
                    {
                        Id = eventframe.GetProperty("Id").GetString(),
                        StartTime = eventframe.GetProperty("StartTime").GetString(),
                        EndTime = eventframe.GetProperty("EndTime").GetString(),
                        RefWebId = matchedRefWebId,
                        Values = new List<EventFrameValueDTO>()
                    };

                    eventFrameToDeviceMap[ef.Id!] = ef.RefWebId;

                    if (includeName)
                    {
                        ef.Values.Add(new EventFrameValueDTO
                        {
                            Name = "Name",
                            Value = eventframe.GetProperty("Name").GetString(),
                            Good = true
                        });
                    }

                    // Track used templates
                    if (eventframe.TryGetProperty("Links", out var links))
                    {
                        if (links.TryGetProperty("Template", out var templateProp))
                        {
                            string? templateUrl = templateProp.GetString();
                            if (!string.IsNullOrEmpty(templateUrl))
                            {
                                usedTemplates.Add(templateUrl.Split("/").Last());
                            }
                        }

                        if (links.TryGetProperty("Parent", out var parentProp))
                        {
                            string? parentUrl = parentProp.GetString();
                            if (!string.IsNullOrEmpty(parentUrl))
                            {
                                parentWebIds.Add(parentUrl.Split("/").Last());
                                ef.RefWebId = parentUrl.Split("/").Last();
                            }
                        }
                    }

                    // Add values from eventframesValues
                    foreach (var value in eventframesValues[valueIndex].GetProperty("Content").GetProperty("Items").EnumerateArray())
                    {
                        ef.Values.Add(value.ToEventframeValue());
                    }

                    valueIndex++;

                    // Track parent IDs with children

                    if (eventframe.TryGetProperty("HasChildren", out var hasChildren) && hasChildren.GetBoolean())
                    {
                        eventFrameWithChildrenIds.Add(ef.Id!);
                    }

                    allFetchedNodes[EventFrameId2WebId(ef.Id!)] = ef;

                    result.Eventframes.Add(ef);
                }
            }

            HashSet<string> requestedWebIds = new HashSet<string>();

            // Call recursion for child batches if any
            if (eventFrameWithChildrenIds.Any() || parentWebIds.Any())
            {
                await GetChildEventframeContextByIdAsync(eventFrameWithChildrenIds, parentWebIds, result, allFetchedNodes, requestedWebIds, cancellationToken);
            }

            FilterEventFrames(result, model);

            foreach (var ef in result.Eventframes)
            {
                ResolveInheritance(ef, allFetchedNodes);
                ef.RefWebId = FindRootDeviceWebId(ef, allFetchedNodes, eventFrameToDeviceMap);
            }

            // Include attribute settings if requested
            if (model.IncludeAttributeSettings && result.Eventframes.Count > 0)
            {
                var templates = await GetEventframeTemplatesAsync(usedTemplates.Distinct().ToList(), cancellationToken);
                result.Warnings.AddRange(templates.Warnings);
                result.Errors.AddRange(templates.Errors);

                result.Attributes = templates.Templates
                    .SelectMany(t => t.Attributes)
                    .GroupBy(a => a.Name)
                    .Select(g => new EventFrameAttributeDTO
                    {
                        Name = g.Key,
                        Description = g.First().Description,
                        UoM = g.First().UoM,
                        ValueType = DataTypePromoting(g.Select(a => a.ValueType).ToList())
                    })
                    .ToList();

                if (includeName)
                {
                    result.Attributes.Add(new EventFrameAttributeDTO
                    {
                        Name = "Name",
                        Description = "EventFrame Name",
                        UoM = "dim",
                        ValueType = "String"
                    });
                }
            }

            result.Eventframes = result.Eventframes
                .OrderBy(ef => ef.StartTime)
                .ToList();

            return result;
        }

        private async Task GetChildEventframeContextByIdAsync(
            List<string> eventFrameIds,
            List<string> parentWebIds,
            EventframeListResponse result,
            Dictionary<string, EventFrameDTO> nodes,
            HashSet<string> requestedWebIds,
            CancellationToken cancellationToken)
        {
            var childWebIds = eventFrameIds
                .Select(EventFrameId2WebId)
                .ToList();

            var incomingWebIds = new HashSet<string>(childWebIds.Concat(parentWebIds).Distinct().ToList());

            //Remove webIds that have already been requested in previous recursion steps
            incomingWebIds.ExceptWith(requestedWebIds);
            var webIds = incomingWebIds.ToList();
            requestedWebIds.UnionWith(webIds);

            if (!webIds.Any())
                return;

            //In OSiSoftPI 2018-SP3 selectedFields is not working/any query parameters cannot be added

            var urlTemplateMeta = $"{GetMandatoryField("BaseURL")}eventframes/{{{{EVENTFRAMEWEBID}}}}/eventframes";

            var templateParameterSets = webIds
                .Select((x, index) => new
                {
                    Value = new Dictionary<string, string> { { "EVENTFRAMEWEBID", x } },
                    Id = x.ToString()
                })
                .ToDictionary(x => x.Id, z => z.Value);

            bool includeName = GetIncludeName(_connectionString!);

            var metaSections = await PerformMultiGetAsync<List<(EventFrameDTO eventFrame, bool hasChildren, bool isDevice)>>(
                urlTemplateMeta,
                templateParameterSets,
                (element, _) => ProcessChildEventframeMetaData(element, string.Empty, includeName)
            );

            var allErrors = metaSections.SelectMany(x => x.Value.errors).ToList();
            if (allErrors.Any())
            {
                result.Errors.AddRange(allErrors);

                var notFoundSections = metaSections
                    .Where(s => s.Value.errors.Any(e => e.ErrorCode == (byte)ErrorCode.NotFound))
                    .ToList();

                foreach (var nf in notFoundSections.SelectMany(s => s.Value.errors))
                {
                    result.Errors.Add(new DatasourceError
                    {
                        Identifier = nf.Identifier ?? "Unknown",
                        ErrorCode = (byte)ErrorCode.DeletedEventFrameWebId,
                        Error = nf.Error
                    });
                }

                // If ALL failed with NotFound → stop recursion completely
                if (notFoundSections.Count == metaSections.Count)
                    return;

                // filter out failed ones so we only recurse on successful data
                metaSections = metaSections
                    .Where(s => !s.Value.errors.Any(e => e.ErrorCode == (byte)ErrorCode.NotFound))
                    .ToDictionary(x => x.Key, x => x.Value);
            }

            var childEventFrameWebIds = metaSections.Values
                .SelectMany(x => x.data.Select(tuple => EventFrameId2WebId(tuple.eventFrame.Id)))
                .Distinct()
                .ToList();

            var urlTemplateValues = $"{GetMandatoryField("BaseURL")}streamsets/{{{{EVENTFRAMEWEBID}}}}/value" +
                "?selectedFields=Items.Name;Items.Value.Value;Items.Value.Good" +
                "&webIdType=IDonly";

            var childTemplateParameterSets = childEventFrameWebIds
                .Select((x, index) => new
                {
                    Value = new Dictionary<string, string> { { "EVENTFRAMEWEBID", x } },
                    Id = x.ToString()
                })
                .ToDictionary(x => x.Id, z => z.Value);

            var valueSections = await PerformMultiGetAsync<List<EventFrameValueDTO>>(
                urlTemplateValues,
                childTemplateParameterSets,
                this.ProcessEventframeValuesData
            );

            result.Errors.AddRange(valueSections.SelectMany(x => x.Value.errors));
            result.Warnings.AddRange(valueSections.SelectMany(x => x.Value.warnings));

            var nextLevelParents = new List<string>();

            foreach (var section in metaSections.Where(m => m.Value.data != null))
            {
                bool isOutputTarget = childWebIds.Contains(section.Key);

                foreach (var tuple in section.Value.data)
                {
                    var ef = tuple.eventFrame;
                    var webId = EventFrameId2WebId(ef.Id);

                    if (valueSections.TryGetValue(webId, out var vals))
                    {
                        ef.Values.AddRange(vals.data);
                    }

                    if (nodes.TryGetValue(section.Key, out var parentNode))
                    {
                        foreach (var parentVal in parentNode.Values)
                        {
                            var existingChildVal = ef.Values.FirstOrDefault(v => v.Name == parentVal.Name);

                            if (existingChildVal == null)
                            {
                                ef.Values.Add(parentVal);
                                continue;
                            }

                            var val = existingChildVal.Value;
                            bool isNotSet = val == null || (val is string s && string.IsNullOrWhiteSpace(s));

                            if (existingChildVal.Good && isNotSet)
                            {
                                existingChildVal.Value = parentVal.Value;
                                existingChildVal.Good = parentVal.Good;
                            }
                        }
                    }

                    if (!nodes.ContainsKey(webId))
                        nodes[webId] = ef;

                    if (isOutputTarget && tuple.isDevice && !result.Eventframes.Any(x => x.Id == ef.Id))
                    {
                        result.Eventframes.Add(ef);
                    }

                    if (!string.IsNullOrEmpty(ef.RefWebId))
                        nextLevelParents.Add(ef.RefWebId);
                }
            }

            var eventframeWebIdsWithChildren = metaSections
                .Where(x => childWebIds.Contains(x.Key))
                .SelectMany(section => section.Value.data)
                .Where(tuple => tuple.hasChildren)
                .Select(tuple => tuple.eventFrame.Id)
                .Distinct()
                .Where(id => !requestedWebIds.Contains(EventFrameId2WebId(id)))
                .ToList();

            nextLevelParents = nextLevelParents
                .Where(cId => !requestedWebIds.Contains(cId))
                .Distinct().ToList();

            if (eventframeWebIdsWithChildren.Any() || nextLevelParents.Any())
            {
                await GetChildEventframeContextByIdAsync(eventframeWebIdsWithChildren, nextLevelParents, result, nodes, requestedWebIds, cancellationToken);
            }
        }

        private async Task GetChildBatchContextByIdAsync(Dictionary<string, string> batchContextData, BatchContextListResponse result, CancellationToken cancellationToken)
        {
            // Filter only those parent IDs we haven’t already processed
            var webIds = batchContextData
                .Select(x => x.Key).ToList();

            if (!webIds.Any())
                return;

            var urlTemplateMeta = $"{GetMandatoryField("BaseURL")}eventframes/{{{{EVENTFRAMEWEBID}}}}/eventframes";

            var templateParameterSets = webIds
                .Select((x, index) => new
                {
                    Value = new Dictionary<string, string> { { "EVENTFRAMEWEBID", x } },
                    Id = index.ToString()
                })
                .ToDictionary(x => x.Id, z => z.Value);

            var sections = await PerformMultiGetAsync<List<(BatchContextDTO batchContext, bool hasChildren, bool isDevice)>>(
                urlTemplateMeta,
                templateParameterSets,
                (element, _) => ProcessChildBatchContextList(element, string.Empty, batchContextData)
            );

            var allErrors = sections.SelectMany(x => x.Value.errors).ToList();
            if (allErrors.Any())
            {
                result.Errors.AddRange(allErrors);

                var notFoundSections = sections
                    .Where(s => s.Value.errors.Any(e => e.ErrorCode == (byte)ErrorCode.NotFound))
                    .ToList();

                foreach (var nf in notFoundSections.SelectMany(s => s.Value.errors))
                {
                    result.Errors.Add(new DatasourceError
                    {
                        Identifier = nf.Identifier ?? "Unknown",
                        ErrorCode = (byte)ErrorCode.DeletedEventFrameWebId,
                        Error = nf.Error
                    });
                }

                // If ALL failed with NotFound → stop recursion completely
                if (notFoundSections.Count == sections.Count)
                    return;

                // filter out failed ones so we only recurse on successful data
                sections = sections
                    .Where(s => !s.Value.errors.Any(e => e.ErrorCode == (byte)ErrorCode.NotFound))
                    .ToDictionary(x => x.Key, x => x.Value);
            }

            var allData = sections
                .Where(x => x.Value.data != null)
                .SelectMany(x => x.Value.data)
                .ToList();

            result.Eventframes.AddRange(
                allData
                .Where(x => x.isDevice)
                .Select(x => x.batchContext)
                .Where(bc => !result.Eventframes.Select(x => x.Id).Contains(bc.Id))
                .ToList());

            var contextsWithChildren = allData
                .Where(x => x.hasChildren)
                .ToDictionary(
                    x => EventFrameId2WebId(x.batchContext.Id),
                    x => x.batchContext.DeviceWebId
                );

            if (contextsWithChildren.Count > 0)
            {
                await GetChildBatchContextByIdAsync(contextsWithChildren, result, cancellationToken);
            }
        }

        public async Task<EventframeListResponse> SearchEventframeContextByIdAsync(EventframeContextIdQueryDTO model, CancellationToken cancellationToken = default)
        {
            var result = new EventframeListResponse
            {
                Eventframes = new List<EventFrameDTO>()
            };

            var webIds = model.Id
                .Select(x => EventFrameId2WebId(x))
                .Concat(model.WebId)
                .Distinct()
                .ToList();

            var urlTemplateMeta = $"{GetMandatoryField("BaseURL")}eventframes/{{{{EVENTFRAMEWEBID}}}}" +
                                  "?selectedFields=Name;StartTime;EndTime;Id;RefElementWebIds;Links.Parent" +
                                  "&webIdType=IDonly";

            var urlTemplateValues = $"{GetMandatoryField("BaseURL")}streamsets/{{{{EVENTFRAMEWEBID}}}}/value" +
                    "?selectedFields=Items.Name;Items.Value.Value;Items.Value.Good" +
                    "&webIdType=IDonly";


            var templateParameterSets = webIds
               .Select((x, index) => new
               {
                   Value = new Dictionary<string, string> {
                      {  "EVENTFRAMEWEBID", x },
               },
                   Id = index.ToString()
               })
               .ToDictionary(x => x.Id, z => z.Value);

            bool includeName = GetIncludeName(_connectionString!);

            var metaSections = await PerformMultiGetAsync<(EventFrameDTO eventframe, string? parentWebId)>(urlTemplateMeta, templateParameterSets,
                (element, _) => ProcessEventframeMetaDataWithParent(element, string.Empty, includeName));

            var parentWebIds = metaSections.Values.Where(x => x.data.parentWebId != null).Select(x => x.data.parentWebId).Except(webIds).Distinct().ToList();

            var nextIndex = templateParameterSets.Count;

            foreach (var parentWebId in parentWebIds)
            {
                templateParameterSets.Add(
                    nextIndex.ToString(),
                    new Dictionary<string, string>
                    {
                        { "EVENTFRAMEWEBID", parentWebId }
                    });

                nextIndex++;
            }

            var valueSections = await PerformMultiGetAsync<List<EventFrameValueDTO>>(urlTemplateValues, templateParameterSets, this.ProcessEventframeValuesData);

            if (metaSections.SelectMany(x => x.Value.errors).Any(err => err.ErrorCode == (byte)ErrorCode.NotFound))
            {
                var metaSectionList = metaSections.ToList();
                for (int i = 0; i < metaSectionList.Count; i++)
                {
                    var metaSection = metaSectionList[i];
                    if (metaSection.Value.errors.Any(err => err.ErrorCode == (byte)ErrorCode.NotFound))
                    {
                        result.Errors.Add(new DatasourceError
                        {
                            Identifier = model.Id[i],
                            ErrorCode = (byte)DatasourceErrorCode.NotFoundWebId,
                            Error = metaSection.Value.errors.FirstOrDefault()?.Error
                        });
                    }
                }

                if (metaSections.SelectMany(x => x.Value.errors).Where(err => err.ErrorCode == (byte)ErrorCode.NotFound).Count() == metaSections.Count())
                {
                    return result;
                }
            }
            else
            {
                result.Errors = metaSections.SelectMany(x => x.Value.errors).ToList();
                result.Errors.Concat(valueSections.SelectMany(x => x.Value.errors).ToList());
            }

            var metaByWebId = metaSections
            .Where(x => x.Value.data.eventframe != null)
            .ToDictionary(
                x => EventFrameId2WebId(x.Value.data.eventframe.Id),
                x => new
                {
                    EventFrame = x.Value.data.eventframe,
                    ParentWebId = x.Value.data.parentWebId
                });

            var requestKeyToWebId = templateParameterSets
            .ToDictionary(
                x => x.Key,
                x => x.Value["EVENTFRAMEWEBID"]);

            var valuesByWebId = valueSections
            .Where(x => x.Value.data != null)
            .ToDictionary(
                x => requestKeyToWebId[x.Key],
                x => x.Value.data);

            result.Eventframes = metaByWebId
            .Select(x =>
            {
                var ef = x.Value.EventFrame;
                var webId = EventFrameId2WebId(ef.Id);

                var values = valuesByWebId.ContainsKey(webId)
                    ? valuesByWebId[webId]
                    : new List<EventFrameValueDTO>();

                if (!string.IsNullOrEmpty(x.Value.ParentWebId) && valuesByWebId.ContainsKey(x.Value.ParentWebId))
                {
                    var parentValues = valuesByWebId[x.Value.ParentWebId];

                    if (parentValues != null)
                    {
                        foreach (var item in parentValues)
                        {
                            var existingValue = values.FirstOrDefault(v => v.Name == item.Name);

                            if (existingValue == null)
                            {
                                values.Add(new EventFrameValueDTO
                                {
                                    Name = item.Name,
                                    Good = item.Good,
                                    Value = item.Value,
                                });
                                continue;
                            }

                            var val = existingValue.Value;
                            bool isNotSet = val == null || (val is string s && string.IsNullOrWhiteSpace(s));

                            if (existingValue.Good && isNotSet)
                            {
                                existingValue.Value = item.Value;
                                existingValue.Good = item.Good;
                            }
                        }
                    }
                }

                ef.Values.AddRange(values);
                return ef;
            })
            .ToList();

            result.Warnings.Concat(valueSections.SelectMany(x => x.Value.warnings).ToList());

            return result;
        }

        private (List<EventFrameValueDTO> data, List<DatasourceError> errors, List<DatasourceWarning> warnings) ProcessEventframeValuesData(JsonElement item, string key)
        {
            var result = (
                data: new List<EventFrameValueDTO>(),
                errors: new List<DatasourceError>(),
                warnings: new List<DatasourceWarning>()
            );

            foreach (var value in item.GetProperty("Items").EnumerateArray())
            {
                var val = value.ToEventframeValue();

                result.data.Add(val);
            }

            return result;
        }

        private (EventFrameDTO data, List<DatasourceError> errors, List<DatasourceWarning> warnings) ProcessEventframeMetaData(JsonElement item, string key, bool includeName)
        {
            return (
                data: item.ToEventframe(includeName),
                errors: new List<DatasourceError>(),
                warnings: new List<DatasourceWarning>()
              );
        }
        private ((EventFrameDTO eventframe, string? parentWebId) data, List<DatasourceError> errors, List<DatasourceWarning> warnings) ProcessEventframeMetaDataWithParent(JsonElement item, string key, bool includeName)
        {
            return (
                data: item.ToEventframeWithParent(includeName),
                errors: new List<DatasourceError>(),
                warnings: new List<DatasourceWarning>()
              );
        }
        private (List<(EventFrameDTO eventFrameDetails, bool hasChildren, bool isDevice)> data, List<DatasourceError> errors, List<DatasourceWarning> warnings) ProcessChildEventframeMetaData(JsonElement item, string key, bool includeName)
        {
            return (
                data: item.ToChildEventframe(includeName),
                errors: new List<DatasourceError>(),
                warnings: new List<DatasourceWarning>()
              );
        }

        private (List<(BatchContextDTO batchContext, bool hasChildren)> data, List<DatasourceError> errors, List<DatasourceWarning> warnings) ProcessBatchContextList(JsonElement element, string key)
        {
            var result = (
                data: new List<(BatchContextDTO batchContext, bool hasChildren)>(),
                errors: new List<DatasourceError>(),
                warnings: new List<DatasourceWarning>()
            );

            result.errors = element.ToDeviceDatasourceErrors();

            if (result.errors.Any())
            {
                return result;
            }

            var eventframes = element.GetProperty("Items");

            foreach (var eventframe in eventframes.EnumerateArray())
            {
                var item = eventframe.ToBatchContext();

                var hasChildrenValue = false;

                if (eventframe.TryGetProperty("HasChildren", out var hasChildren) && hasChildren.GetBoolean())
                {
                    hasChildrenValue = true;
                }

                result.data.Add((item, hasChildrenValue));
            }
            return result;
        }

        private (List<(BatchContextDTO batchContext, bool hasChildren, bool isDevice)> data, List<DatasourceError> errors, List<DatasourceWarning> warnings) ProcessChildBatchContextList(JsonElement element, string key, Dictionary<string, string> parentData)
        {
            var result = (
                data: new List<(BatchContextDTO batchContext, bool hasChildren, bool isDevice)>(),
                errors: new List<DatasourceError>(),
                warnings: new List<DatasourceWarning>()
            );

            result.errors = element.ToEventframeDatasourceErrors();

            if (result.errors.Any())
            {
                return result;
            }

            var eventframes = element.GetProperty("Items");

            foreach (var eventframe in eventframes.EnumerateArray())
            {
                var item = eventframe.ToChildBatchContext(parentData);

                var hasChildrenValue = false;

                if (eventframe.TryGetProperty("HasChildren", out var hasChildren) && hasChildren.GetBoolean())
                {
                    hasChildrenValue = true;
                }

                bool isDevice = eventframe.TryGetProperty("CategoryNames", out var categoriesProp) &&
                   categoriesProp.ValueKind == JsonValueKind.Array &&
                   categoriesProp.EnumerateArray().Any(cat => string.Equals(cat.GetString(), "DC_Asset", StringComparison.OrdinalIgnoreCase));

                result.data.Add((item, hasChildrenValue, isDevice));
            }
            return result;
        }

        public async Task<EventFrameResponse> GetEventframeAsync(string id, CancellationToken cancellationToken = default)
        {
            var webId = EventFrameId2WebId(id);

            var template = GetAvevaPIWebApiTemplate("SingleEventframe.json");

            var templateParameters = new Dictionary<string, string>
            {
                {"BASEURL", GetMandatoryField("BaseURL") },
                {"EVENTFRAMEWEBID", webId}
            };

            template = ReplaceTemplatePlaceholders(template, templateParameters);

            var response = await PostAsync($"{GetMandatoryField("BaseURL")}batch", template);

            var result = new EventFrameResponse();

            if (response.errors.Count != 0 && response.errors.Any(e => e.ErrorCode == (byte)DatasourceErrorCode.NotFound))
            {
                result.Errors.Add(new DatasourceError
                {
                    Identifier = id,
                    ErrorCode = (byte)ErrorCode.DeletedEventFrameWebId,
                    Error = response.content
                });

                return result;
            }
            else if (response.errors.Count != 0)
            {
                result.Errors = response.errors;

                return result;
            }

            using var document = JsonDocument.Parse(response.content);
            var responseData = document.RootElement;

            bool includeName = GetIncludeName(_connectionString!);

            var ef = ProcessEventframeMetaData(responseData.GetProperty("1").GetProperty("Content"), string.Empty, includeName);
            var values = ProcessEventframeValuesData(responseData.GetProperty("0").GetProperty("Content"), string.Empty);

            if (includeName)
            {
                values.data.AddRange(ef.data.Values);
            }

            if (responseData.GetProperty("1").GetProperty("Content").TryGetProperty("Links", out var links) && links.TryGetProperty("Parent", out var parentElement))
            {
                string parentWebId = string.Empty;
                string? parentUrl = parentElement.GetString();
                if (!string.IsNullOrEmpty(parentUrl))
                {
                    parentWebId = parentUrl.Split("/").Last();
                }

                var url = $"{GetMandatoryField("BaseURL")}streamsets/{parentWebId}/value?selectedFields=Items.Name;Items.Value.Value;Items.Value.Good&webIdType=IDonly";

                var parentResponse = await GetAsync(url);
                var parentContent = await ValidateResponse(parentResponse);

                if (!parentContent.content.HasValue)
                {
                    result.Errors = parentContent.res.Errors;

                    return result;
                }

                var parentItem = parentContent.content.Value;

                var parentValues = ProcessEventframeValuesData(parentItem, string.Empty);

                foreach (var parentVal in parentValues.data)
                {
                    var existingChildVal = values.data.FirstOrDefault(v => v.Name == parentVal.Name);

                    if (existingChildVal == null)
                    {
                        values.data.Add(parentVal);
                        continue;
                    }

                    var val = existingChildVal.Value;
                    bool isNotSet = val == null || (val is string s && string.IsNullOrWhiteSpace(s));

                    if (existingChildVal.Good && isNotSet)
                    {
                        existingChildVal.Value = parentVal.Value;
                        existingChildVal.Good = parentVal.Good;
                    }
                }
            }

            result.Eventframe = ef.data;
            result.Eventframe.Values = values.data;
            result.Errors = ef.errors.Concat(values.errors).ToList();
            result.Warnings = ef.warnings.Concat(values.warnings).ToList();

            return result;
        }

        public async Task<EventframeValuesResponse> GetEventframeValuesAsync(string id, CancellationToken cancellationToken = default)
        {
            var result = new EventframeValuesResponse
            {
                Values = new List<EventFrameValueDTO>()
            };

            var webId = EventFrameId2WebId(id);

            var template = GetAvevaPIWebApiTemplate("SingleEventframe.json");

            var templateParameters = new Dictionary<string, string>
            {
                {"BASEURL", GetMandatoryField("BaseURL") },
                {"EVENTFRAMEWEBID", webId}
            };

            template = ReplaceTemplatePlaceholders(template, templateParameters);

            var response = await PostAsync($"{GetMandatoryField("BaseURL")}batch", template);

            if (response.errors.Count != 0 && response.errors.Any(e => e.ErrorCode == (byte)DatasourceErrorCode.NotFound))
            {
                result.Errors.Add(new DatasourceError
                {
                    Identifier = id,
                    ErrorCode = (byte)ErrorCode.DeletedEventFrameWebId,
                    Error = response.content
                });

                return result;
            }
            else if (response.errors.Count != 0)
            {
                result.Errors = response.errors;

                return result;
            }

            using var document = JsonDocument.Parse(response.content);

            var responseData = document.RootElement.Clone();

            (List<EventFrameValueDTO> data, List<DatasourceError> errors, List<DatasourceWarning> warnings) section;

            if (responseData.GetProperty("1").GetProperty("Content").TryGetProperty("Links", out var links) && links.TryGetProperty("Parent", out var parentElement))
            {
                string parentWebId = string.Empty;
                string? parentUrl = parentElement.GetString();
                if (!string.IsNullOrEmpty(parentUrl))
                {
                    parentWebId = parentUrl.Split("/").Last();
                }

                section = ProcessEventframeValuesData(responseData.GetProperty("0").GetProperty("Content"), string.Empty);

                var url = $"{GetMandatoryField("BaseURL")}streamsets/{parentWebId}/value?selectedFields=Items.Name;Items.Value.Value;Items.Value.Good&webIdType=IDonly";

                var parentResponse = await GetAsync(url);
                var parentContent = await ValidateResponse(parentResponse);

                if (!parentContent.content.HasValue)
                {
                    result.Errors = parentContent.res.Errors;

                    return result;
                }

                var parentItem = parentContent.content.Value;

                var parentValues = ProcessEventframeValuesData(parentItem, string.Empty);

                section.data.AddRange(parentValues.data);
            }
            else
            {
                section = ProcessEventframeValuesData(responseData.GetProperty("0").GetProperty("Content"), string.Empty);
            }

            bool includeName = GetIncludeName(_connectionString!);

            if (includeName)
            {
                section.data.Add(new EventFrameValueDTO
                {
                    Name = "Name",
                    Value = responseData.GetProperty("1").GetProperty("Content").GetProperty("Name").GetString(),
                    Good = true
                });
            }

            result.Warnings = section.warnings;
            result.Errors = section.errors;
            result.Values = section.data;

            return result;
        }

        public async Task<EventframeAttributesResponse> GetEventframeAttributesAsync(string deviceWebId, CancellationToken cancellationToken = default)
        {
            var result = new EventframeAttributesResponse
            {
                Attributes = new List<EventFrameAttributeDTO>()
            };

            var templateWebIds = await GetEventframeTemplatesByDeviceWebId(deviceWebId);

            if (templateWebIds.errors.Count != 0)
            {
                result.Errors = templateWebIds.errors;
                return result;
            }

            var templates = await GetEventframeTemplatesAsync(templateWebIds.data, cancellationToken);

            if (templates.Errors.Count != 0)
            {
                result.Errors = templates.Errors;
                return result;
            }

            //Merge and perform dataType promoting
            var attributes = templates.Templates
                .SelectMany(x => x.Attributes)
                .GroupBy(x => x.Name)
                .Select(x => new EventFrameAttributeDTO
                {
                    Name = x.Key,
                    Description = x.Select(x => x.Description).First(),
                    UoM = x.Select(x => x.UoM).First(),
                    ValueType = DataTypePromoting(x.Select(dt => dt.ValueType).ToList())
                })
                .ToList();

            bool includeName = GetIncludeName(_connectionString!);

            if (includeName)
            {
                attributes.Add(new EventFrameAttributeDTO
                {
                    Name = "Name",
                    Description = "EventFrame Name",
                    UoM = "dim",
                    ValueType = "String"
                });
            }

            result.Attributes = attributes;

            return result;
        }

        public async Task<SensorDataWithBatchContextResponse> GetPlotDataWithBatchContext(PlotDataWithBatchContextQueryDTO model)
        {
            var result = new SensorDataWithBatchContextResponse
            {
                SensorData = new List<SensorDataDTO>(),
                BatchData = new List<BatchDataDTO>()
            };

            var template = GetAvevaPIWebApiTemplate("PlotDataWithBatchContext.json");
            var templateParameters = new Dictionary<string, string>
            {
                {"BASEURL", GetMandatoryField("BaseURL")},
                {"DEVICEWEBID", string.Join("&webId=", model.DeviceWebId)},
                {"STARTTIME", model.StartTime.ToIso8601String()},
                {"ENDTIME", model.EndTime.ToIso8601String()},
                {"SENSORWEBID", string.Join("&webId=", model.WebId)},
            };

            template = ReplaceTemplatePlaceholders(template, templateParameters);

            var response = await PostAsync($"{GetMandatoryField("BaseURL")}/batch", template);

            using var document = JsonDocument.Parse(response.content);
            var content = document.RootElement;

            var telemetry = ProcessAndMapStreamsetResponse(content.GetProperty("0").GetProperty("Content"));

            result.SensorData = telemetry.Data;
            result.Errors = telemetry.Errors;
            result.Warnings = telemetry.Warnings;

            var availableContext = new List<BatchContextDTO>();

            foreach (var device in content.GetProperty("3").GetProperty("Content").GetProperty("Items").EnumerateArray())
            {
                foreach (var item in device.GetProperty("Content").GetProperty("Items").EnumerateArray())
                {
                    availableContext.Add(item.ToBatchContext());
                }
            }

            result.BatchData = availableContext
                .GroupBy(x => x.DeviceWebId)
                .Select(x => new BatchDataDTO
                {
                    DeviceWebId = x.Key,
                    EventFrames = x.Select(ef => new BatchContextDTO
                    {
                        Id = ef.Id,
                        DeviceWebId = x.Key,
                        StartTime = ef.StartTime,
                        EndTime = ef.EndTime,
                    }).ToList()
                }).ToList();

            return result;
        }

        private async Task<(List<string> data, List<DatasourceError> errors, List<DatasourceWarning> warnings)> GetEventframeTemplatesByDeviceWebId(string deviceWebId)
        {
            var result = (
                data: new List<string>(),
                errors: new List<DatasourceError>(),
                warnings: new List<DatasourceWarning>()
            );

            var url = $"{GetMandatoryField("BaseURL")}elements/{deviceWebId}/eventframes" +
                $"?starttime=*" +
                $"&maxcount=100" +
                $"&searchmode=BackwardFromStartTime" +
                $"&selectedFields=Items.Links.Template" +
                $"&categoryName=DC_Asset" +
                $"&webIdType=IDOnly";


            var response = await GetAsync(url);
            var content = await ValidateResponse(response);

            if (!content.content.HasValue && content.res.Errors.Count != 0 && content.res.Errors.Any(e => e.ErrorCode == (byte)ErrorCode.NotFound))
            {
                result.errors.Add(new DatasourceError
                {
                    Identifier = deviceWebId,
                    ErrorCode = (byte)ErrorCode.DeleteDevice,
                    Error = content.res.Errors.FirstOrDefault()?.Error
                });

                return result;
            }
            else if (!content.content.HasValue)
            {
                result.errors = content.res.Errors;

                return result;
            }

            var usedTemplates = new List<string>();

            var items = content.content.Value
                .GetProperty("Items")
                .EnumerateArray();

            foreach (var item in items)
            {
                if (item.TryGetProperty("Links", out var links) && links.TryGetProperty("Template", out var templateProp))
                {
                    string? templateUrl = templateProp.GetString();
                    if (!string.IsNullOrEmpty(templateUrl))
                    {
                        usedTemplates.Add(templateUrl.Split("/").Last());
                    }
                }
            }

            result.data = usedTemplates.Distinct().ToList();

            return result;
        }

        private (List<EventFrameAttributeDTO>, List<DatasourceError> errors, List<DatasourceWarning> warnings) ProcessEventframeTemplateAttribute(JsonElement content, string key)
        {
            var result = (
                data: new List<EventFrameAttributeDTO>(),
                errors: new List<DatasourceError>(),
                warnings: new List<DatasourceWarning>()
            );

            try
            {
                if (!content.TryGetProperty("Items", out var items))
                {
                    result.errors.Add(new DatasourceError { Error = "Missing or invalid 'Items' property in response." });
                    return result;
                }

                foreach (var attribute in items.EnumerateArray())
                {
                    string? uom = null;
                    if (attribute.TryGetProperty("DefaultUnitsNameAbbreviation", out var abbrProp) && abbrProp.ValueKind != JsonValueKind.Null)
                    {
                        uom = abbrProp.GetString();
                    }
                    else if (attribute.TryGetProperty("DefaultUnitsName", out var nameProp) && nameProp.ValueKind != JsonValueKind.Null)
                    {
                        uom = nameProp.GetString();
                    }

                    var item = new EventFrameAttributeDTO
                    {
                        Name = attribute.GetProperty("Name").GetString(),
                        Description = attribute.GetProperty("Description").GetString(),
                        UoM = uom ?? string.Empty,
                        ValueType = attribute.GetProperty("Type").GetString(),
                    };

                    result.data.Add(item);
                }
            }
            catch (Exception ex)
            {
                result.errors.Add(new DatasourceError { Error = $"Failed to process template attributes: {ex.Message}" });
            }

            return result;
        }
        private (List<EventFrameAttributeDTO>, List<DatasourceError> errors, List<DatasourceWarning> warnings) ProcessTemplateDetails(JsonElement content, string key)
        {
            var result = (
                data: new List<EventFrameAttributeDTO>(),
                errors: new List<DatasourceError>(),
                warnings: new List<DatasourceWarning>()
            );

            try
            {
                if (!content.TryGetProperty("Items", out var items))
                {
                    result.errors.Add(new DatasourceError { Error = "Missing or invalid 'Items' property in response." });
                    return result;
                }


                foreach (var sensorDetails in items.EnumerateArray())
                {
                    var itemToAdd = new EventFrameAttributeDTO
                    {
                        Name = sensorDetails.GetProperty("Name").ToString(),
                        Description = sensorDetails.GetProperty("Description").ToString(),
                        ValueType = sensorDetails.GetProperty("Type").ToString()
                    };

                    result.data.Add(itemToAdd);
                }
            }
            catch (Exception ex)
            {
                result.errors.Add(new DatasourceError { Error = $"Failed to process template attributes: {ex.Message}" });
            }

            return result;
        }
        public string EventFrameId2WebId(string eventFrameId)
        {
            if (string.IsNullOrEmpty(eventFrameId))
            {
                throw new ArgumentNullException(nameof(eventFrameId));
            }

            var webId = WebIdFormatConverter.ConvertFullWebIdToIdOnly(GetMandatoryField("WebId"));
            var encodedSystemId = webId.Substring(4, 22);

            // Parse the Guid
            Guid guid = Guid.Parse(eventFrameId);

            // We know the exact length of the final PI WebId string:
            // "I1Fm" (4) + encodedSystemId (22) + eventFrameEncoded (22) = 48 characters.
            // string.Create allows us to allocate the final string exactly once.
            return string.Create(48, (encodedSystemId, guid), (span, state) =>
            {
                // 1. Write the static prefix
                "I1Fm".AsSpan().CopyTo(span.Slice(0, 4));

                // 2. Write the System ID
                state.encodedSystemId.AsSpan().CopyTo(span.Slice(4, 22));

                // 3. Extract Guid bytes without allocation
                Span<byte> guidBytes = stackalloc byte[16];
                state.guid.TryWriteBytes(guidBytes);

                // 4. Convert to standard Base64
                Span<char> base64Chars = stackalloc char[24];
                Convert.TryToBase64Chars(guidBytes, base64Chars, out _);

                // 5. Map Base64 to Base64Url directly into the final string
                // We only loop 22 times, automatically dropping the "==" padding
                for (int i = 0; i < 22; i++)
                {
                    char c = base64Chars[i];
                    if (c == '+') span[26 + i] = '-';
                    else if (c == '/') span[26 + i] = '_';
                    else span[26 + i] = c;
                }
            });
        }
        public string WebId2EventFrameId(string webId)
        {
            if (string.IsNullOrEmpty(webId))
            {
                throw new ArgumentNullException(nameof(webId));
            }

            // Grab the 22 characters that represent the Guid as a read-only span
            ReadOnlySpan<char> guidPart = webId.AsSpan(26, 22);

            // Prepare a buffer for the standard Base64 string (needs to be 24 chars)
            Span<char> base64Chars = stackalloc char[24];

            for (int i = 0; i < 22; i++)
            {
                char c = guidPart[i];
                if (c == '-') base64Chars[i] = '+';
                else if (c == '_') base64Chars[i] = '/';
                else base64Chars[i] = c;
            }

            // Add the padding back
            base64Chars[22] = '=';
            base64Chars[23] = '=';

            // Convert back to bytes, then to Guid
            Span<byte> guidBytes = stackalloc byte[16];
            Convert.TryFromBase64Chars(base64Chars, guidBytes, out _);

            return new Guid(guidBytes).ToString();
        }

        private Dictionary<string, (T data, List<DatasourceError> errors, List<DatasourceWarning> warnings)>
        ApplyProcessingFunctionToMultiRequestTempalte<T>(
        Dictionary<string, JsonElement> sections,
        Func<JsonElement, string, (T data, List<DatasourceError> errors, List<DatasourceWarning> warnings)> function)
        {
            var result = new Dictionary<string, (T data, List<DatasourceError> errors, List<DatasourceWarning> warnings)>();

            // Iterate through each key-value pair in the sections dictionary
            foreach (var section in sections)
            {
                // Apply the function to the JsonElement and store the result
                var processedValue = function(section.Value.GetProperty("Content"), section.Key);
                result[section.Key] = processedValue;
            }

            return result;
        }
        public async Task<EventframeTemplateListResponse> GetEventframeTemplatesAsync(List<string> webIds, CancellationToken cancellationToken = default)
        {
            var result = new EventframeTemplateListResponse
            {
                Templates = new List<EventframeTemplateDetailsDTO>()
            };
            //TODO
            var urlTemplate = $"{GetMandatoryField("BaseURL")}elementtemplates/{{{{EVENTFRAMETEMPLATEWEBID}}}}/attributetemplates" +
                                "?selectedFields=Items.Name;Items.Type;" +
                                "Items.DefaultUnitsNameAbbreviation;Items.DefaultUnitsName;" +
                                "Items.Description" +
                                "&showInherited=true" +
                                "&webIdType=IDonly";

            var templateParameterSets = webIds
           .Select((x, index) => new
           {
               Value = new Dictionary<string, string> {
                      {  "EVENTFRAMETEMPLATEWEBID", x },
           },
               Id = index.ToString()
           })
           .ToDictionary(x => x.Id, z => z.Value);

            var sections = await PerformMultiGetAsync<List<EventFrameAttributeDTO>>(urlTemplate, templateParameterSets, this.ProcessEventframeTemplateAttribute);

            if (sections.SelectMany(x => x.Value.errors).Any(err => err.ErrorCode == (byte)ErrorCode.NotFound))
            {
                var sectionList = sections.ToList();
                for (int i = 0; i < sectionList.Count; i++)
                {
                    var metaSection = sectionList[i];
                    if (metaSection.Value.errors.Any(err => err.ErrorCode == (byte)ErrorCode.NotFound))
                    {
                        result.Errors.Add(new DatasourceError
                        {
                            Identifier = webIds[i],
                            ErrorCode = (byte)ErrorCode.DeletedTemplate,
                            Error = metaSection.Value.errors.FirstOrDefault()?.Error
                        });
                    }
                }

                if (sections.SelectMany(x => x.Value.errors).Where(err => err.ErrorCode == (byte)ErrorCode.NotFound).Count() == sections.Count())
                {
                    return result;
                }
            }
            else
            {
                result.Errors = sections.SelectMany(x => x.Value.errors).ToList();
            }

            result.Templates.AddRange(
                sections
                .Where(x => x.Value.data != null).Select(x => new EventframeTemplateDetailsDTO
                {
                    WebId = x.Key,
                    Attributes = x.Value.data
                })
            );

            result.Warnings = sections.SelectMany(x => x.Value.warnings).ToList();

            return result;
        }

        private string WrapMultipleRequestsIntoTemplate(string url, Dictionary<string, Dictionary<string, string>> parameterSets)
        {
            var template = new JsonObject();
            foreach (var parameterSet in parameterSets)
            {
                template[parameterSet.Key] = new JsonObject
                {
                    ["Method"] = "GET",
                    ["Resource"] = ReplaceTemplatePlaceholders(url, parameterSet.Value),
                };
            }

            return template.ToJsonString();
        }

        public async Task<GlobalFilterResponse> GetGlobalFilterElementAsync(string webId, CancellationToken cancellationToken = default)
        {
            var result = new GlobalFilterResponse();

            var response = await RequestFilterElementAsync(webId);

            if (response.res.Errors.Count != 0 && response.res.Errors.Any(e => e.ErrorCode == (byte)DatasourceErrorCode.NotFoundWebId))
            {
                result.Errors = response.res.Errors;

                return result;
            }

            result.Element = response.element;

            return result;
        }

        public string ExtractDisplayEventId(List<EventFrameValueDTO> values, string id)
        {
            var displayId = GetField("IdentifierKey");

            if (displayId != null)
            {
                var val = values.FirstOrDefault(v => v.Name == displayId);

                if (val != null)
                {
                    if (val.Good)
                    {
                        return val.Value?.ToString() ?? string.Empty;
                    }
                    else
                    {
                        return "BadValue";
                    }
                }
            }

            return DateTime.UtcNow.ToString();

        }

        private async Task<(JsonElement? content, DatasourceResponse res)> ValidateResponse(HttpResponseMessage res)
        {
            var result = (
                content: (JsonElement?)null,
                res: new DatasourceResponse()
            );

            if (res.StatusCode == HttpStatusCode.Unauthorized)
            {
                throw new DCPUnauthorizedException("Unauthorized access to the datasource");
            }

            string content = await res.Content.ReadAsStringAsync();
            using var document = JsonDocument.Parse(content);

            var root = document.RootElement.Clone();

            if (res.StatusCode == HttpStatusCode.NotFound)
            {
                result.res.Errors.Add(new DatasourceError
                {
                    ErrorCode = (byte)ErrorCode.NotFound,
                    Error = (root).ToDatasourceErrors().FirstOrDefault()?.Error
                });
                return result;
            }

            if (!res.IsSuccessStatusCode)
            {
                result.res.Errors.Add(new DatasourceError
                {
                    Error = content
                });
                return result;
            }

            result.content = root;

            return result;
        }

        public async Task<EventframeAttributesResponse> GetTemplateDetailsAsync(List<string> templateWebIds, CancellationToken cancellationToken = default)
        {
            if (templateWebIds == null && templateWebIds!.Count == 0)
            {
                throw new DCPBadRequestException(ErrorMessages.TemplateWebIdsRequired);
            }

            var result = new EventframeAttributesResponse
            {
                Attributes = new List<EventFrameAttributeDTO>()
            };


            var urlTemplate = $"{GetMandatoryField("BaseURL")}elementtemplates/{{{{TEMPLATEWEBID}}}}/attributetemplates" +
                                "?showInherited=true&selectedFields=Items.Name;Items.Description;Items.Type&webIdType=IDOnly";

            var templateParameterSets = templateWebIds
           .Select((x, index) => new
           {
               Value = new Dictionary<string, string> {
                      {  "TEMPLATEWEBID", x },
           },
               Id = index.ToString()
           })
           .ToDictionary(x => x.Id, z => z.Value);

            var sections = await PerformMultiGetAsync<List<EventFrameAttributeDTO>>(urlTemplate, templateParameterSets, this.ProcessTemplateDetails);


            if (sections.SelectMany(x => x.Value.errors).Any(err => err.ErrorCode == (byte)ErrorCode.NotFound))
            {
                var sectionList = sections.ToList();
                for (int i = 0; i < sectionList.Count; i++)
                {
                    var section = sectionList[i];
                    if (section.Value.errors.Any(err => err.ErrorCode == (byte)ErrorCode.NotFound))
                    {
                        result.Errors.Add(new DatasourceError
                        {
                            Identifier = templateWebIds[i],
                            ErrorCode = (byte)ErrorCode.DeletedTemplate,
                            Error = section.Value.errors.FirstOrDefault()?.Error
                        });
                    }
                }
            }
            else
            {
                result.Errors = sections.SelectMany(x => x.Value.errors).ToList();
            }

            var groupedList = sections.Where(x => x.Value.data != null).SelectMany(x => x.Value.data).ToList()
                .GroupBy(x => x.Name)
                .Select(g => new EventFrameAttributeDTO
                {
                    Name = g.Key,
                    Description = g.First().Description,
                    ValueType = DataTypePromoting(g.Select(x => x.ValueType).ToList()),
                    UoM = g.First().UoM
                })
                .ToList();

            bool includeName = GetIncludeName(_connectionString!);

            if (includeName)
            {
                groupedList.Add(new EventFrameAttributeDTO
                {
                    Name = "Name",
                    Description = "EventFrame Name",
                    UoM = "dim",
                    ValueType = "String"
                });
            }

            result.Attributes = groupedList;
            result.Warnings = sections.SelectMany(x => x.Value.warnings).ToList();

            return result;
        }

        public static bool GetIncludeName(JsonObject json, bool defaultValue = false)
        {
            if (json.TryGetPropertyValue("IncludeName", out JsonNode? node) &&
                node is JsonValue value &&
                value.TryGetValue<bool>(out bool result))
            {
                return result;
            }

            return defaultValue;
        }

        private void FilterEventFrames(EventframeListResponse response, EventframeContextTimeQueryDTO model)
        {
            if (response.Eventframes == null) return;

            response.Eventframes = response.Eventframes
                .Where(ef => IsSearchModeCorrect(ef.StartTime, ef.EndTime, model.StartTime, model.EndTime, model.SearchMode))
                .ToList();
        }

        private void FilterBatchContext(BatchContextListResponse response, BatchContextTimeQueryDTO model)
        {
            if (response.Eventframes == null) return;

            response.Eventframes = response.Eventframes
                .Where(ef => IsSearchModeCorrect(ef.StartTime, ef.EndTime, model.StartTime, model.EndTime, model.SearchMode))
                .ToList();
        }

        private bool IsSearchModeCorrect(string rawStart, string rawEnd, DateTime start, DateTime end, EventframeSearchMode searchMode)
        {
            if (!DateTimeOffset.TryParse(rawStart, out DateTimeOffset offsetStart) ||
                !DateTimeOffset.TryParse(rawEnd, out DateTimeOffset offsetEnd))
            {
                return false;
            }

            DateTime efStart = offsetStart.UtcDateTime;
            DateTime efEnd = offsetEnd.UtcDateTime;

            return searchMode switch
            {
                EventframeSearchMode.StartInclusive =>
                    efStart >= start && efStart <= end,

                EventframeSearchMode.EndInclusive =>
                    efEnd >= start && efEnd <= end,

                EventframeSearchMode.Inclusive =>
                    (efStart >= start && efStart <= end) && (efEnd >= start && efEnd <= end),

                EventframeSearchMode.Overlapped =>
                    (efStart >= start && efStart <= end) || (efEnd >= start && efEnd <= end) || (efStart <= start && efEnd >= end),

                EventframeSearchMode.InProgress =>
                    efStart >= start && efStart <= end && efEnd >= DateTime.MaxValue.AddDays(-1),

                _ => true
            };
        }

        private void ResolveInheritance(EventFrameDTO item, Dictionary<string, EventFrameDTO> cache)
        {
            var current = item;
            var parentValues = new List<EventFrameValueDTO>();

            // Climb up to collect all values from parents
            while (!string.IsNullOrEmpty(current.RefWebId))
            {
                if (cache.TryGetValue(current.RefWebId, out var parent))
                {
                    foreach (var pVal in parent.Values)
                    {
                        // closer parents override further ones
                        if (!parentValues.Any(v => v.Name == pVal.Name))
                            parentValues.Add(pVal);
                    }
                    current = parent; // Keep climbing
                }
                else break;
            }

            // Merge parent values into the eventfram,e if the item doesn't have its own
            foreach (var val in parentValues)
            {
                if (!item.Values.Any(v => v.Name == val.Name))
                {
                    item.Values.Add(new EventFrameValueDTO
                    {
                        Name = val.Name,
                        Value = val.Value,
                        Good = val.Good
                    });
                }
            }
        }

        private string FindRootDeviceWebId(
            EventFrameDTO ef,
            Dictionary<string, EventFrameDTO> nodes,
            Dictionary<string, string> eventframeToDeviceMap)
        {
            if (eventframeToDeviceMap.TryGetValue(ef.Id, out var deviceWebId))
            {
                return deviceWebId;
            }

            var current = ef;

            while (!string.IsNullOrEmpty(current.RefWebId))
            {
                var parentId = WebId2EventFrameId(current.RefWebId);

                if (eventframeToDeviceMap.TryGetValue(parentId, out var rootDeviceId))
                {
                    return rootDeviceId;
                }

                if (nodes.TryGetValue(parentId, out var parentNode))
                {
                    current = parentNode;
                }
                else
                {
                    //fallback
                    return current.RefWebId;
                }
            }

            return current.RefWebId;
        }

        public List<string> EventFrameId2WebId(List<string> id)
        {
            return id.Select(x => EventFrameId2WebId(x)).ToList();
        }

        public List<string> WebId2EventFrameId(List<string> webId)
        {
            return webId.Select(x => WebId2EventFrameId(x)).ToList();
        }

        public Task<List<string>> FindByAttributesAsync(Dictionary<string, object> attributes)
        {
            throw new NotImplementedException();
        }

        public async Task<Dictionary<string, object>> GetAttribtuesAsync(string id)
        {
            var ef = await GetEventframeAsync(id);
            return ef.Eventframe.Values.ToDictionary(v => v.Name, v => v.Value);
        }


        public Task<Dictionary<string, List<string>>> FindByAttributesAsync(Dictionary<string, Dictionary<string, object>> attributes)
        {
            throw new NotImplementedException();
        }

        public async Task<Dictionary<string, Dictionary<string, object>>> GetAttribtuesAsync(List<string> id)
        {
            var model = new EventframeContextIdQueryDTO
            {
                Id = id,
            };

            var eventframes = await SearchEventframeContextByIdAsync(model);

            return eventframes.Eventframes
                .ToDictionary(x => x.Id, x => x.Values.ToDictionary(v => v.Name, v => v.Value));
        }
    }
}