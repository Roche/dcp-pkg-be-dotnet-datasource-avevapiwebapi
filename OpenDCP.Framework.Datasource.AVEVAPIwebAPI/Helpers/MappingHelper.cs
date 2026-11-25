using OpenDCP.Framework.Configuration.Model.Errors;
using OpenDCP.Framework.Datasource.AVEVAPIwebAPI.Constants;
using OpenDCP.Framework.Datasource.AVEVAPIwebAPI.JsonSerializerContexts;
using OpenDCP.Framework.DataTypes.Equipment.Constants;
using OpenDCP.Framework.DataTypes.Equipment.Entities.AssetHierarchy;
using OpenDCP.Framework.DataTypes.Equipment.Entities.EventframeContext;
using OpenDCP.Framework.DataTypes.Equipment.Entities.SensorData;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace OpenDCP.Framework.Datasource.AVEVAPIwebAPI.Helpers
{
    internal static class MappingHelper
    {
        public static SensorDTO ToSensor(this JsonElement el, List<string> hiddenCategories)
        {
            static string? GetString(JsonElement obj, string name) =>
             obj.TryGetProperty(name, out var p) &&
             p.ValueKind != JsonValueKind.Null &&
             p.ValueKind != JsonValueKind.Undefined
                 ? p.GetString()
                 : null;

            static bool GetBool(JsonElement obj, string name) =>
                obj.TryGetProperty(name, out var p) &&
                (p.ValueKind == JsonValueKind.True || p.ValueKind == JsonValueKind.False)
                    ? p.GetBoolean()
                    : false;

            static List<string> GetStringList(JsonElement obj, string name)
            {
                if (!obj.TryGetProperty(name, out var p) || p.ValueKind != JsonValueKind.Array)
                    return new List<string>();

                var list = new List<string>();
                foreach (var item in p.EnumerateArray())
                {
                    if (item.ValueKind == JsonValueKind.String)
                        list.Add(item.GetString()!);
                }
                return list;
            }

            string? deviceWebId = null;
            if (el.TryGetProperty("Links", out var links) &&
                links.ValueKind == JsonValueKind.Object &&
                links.TryGetProperty("Element", out var elementLink) &&
                elementLink.ValueKind == JsonValueKind.String)
            {
                var href = elementLink.GetString();
                if (!string.IsNullOrEmpty(href))
                    deviceWebId = href.Split('/', StringSplitOptions.RemoveEmptyEntries).LastOrDefault();
            }

            var uom = GetString(el, "DefaultUnitsNameAbbreviation");
            if (string.IsNullOrEmpty(uom))
            {
                uom = GetString(el, "DefaultUnitsName");
            }

            return new SensorDTO
            {
                Name = GetString(el, "Name"),
                WebId = GetString(el, "WebId"),
                Description = GetString(el, "Description"),
                ValueType = GetString(el, "Type"),
                UoM = uom,
                IsStep = GetBool(el, "Step"),
                CategoryNames = GetStringList(el, "CategoryNames")
                                .Except(hiddenCategories)
                                .ToList(),
                DeviceWebId = deviceWebId
            };
        }

        public static DeviceDTO ToDevice(this JsonElement el, List<string> hiddenCategories, string filterType, string? basePath = null)
        {
            var webId = el.GetProperty("WebId").GetString();

            var device = new DeviceDTO
            {
                Name = el.GetProperty("Name").GetString(),
                WebId = webId,
                Link = filterType == "NoSensorFilter" ? $"{webId}/sensor" : $"{webId}/group",
                Description = el.GetProperty("Description").GetString(),
                CategoryNames = el.GetProperty("CategoryNames")
                .Deserialize<List<string>>(DatasourceSerializerContext.Default.ListString)
                .Except(hiddenCategories)
                .ToList()
            };

            var rawPath = el.GetProperty("Path").GetString();
            var pathParts = new List<string>();

            if (string.IsNullOrWhiteSpace(basePath))
            {
                pathParts = rawPath?
                    .Split(new[] { "\\" }, StringSplitOptions.RemoveEmptyEntries)
                    .Select(p => p.Trim())
                    .Skip(2)
                    .ToList() ?? new List<string>();
            }
            else if (!string.IsNullOrWhiteSpace(rawPath) && rawPath.Length > basePath.Length)
            {
                var finalPath = rawPath.Substring(basePath.Length).TrimStart('\\');

                pathParts = finalPath
                    .Split(new[] { "\\" }, StringSplitOptions.RemoveEmptyEntries)
                    .Select(p => p.Trim())
                    .ToList();
            }

            device.Path = pathParts
                .Select(p => new ElementPathDTO
                {
                    Name = p.Trim(),
                })
                .ToList() ?? new List<ElementPathDTO>();

            return device;
        }


        public static GlobalFilterElementDTO ToSensorGlobalFilterElement(this JsonElement el, List<string>? hiddenCategories = null)
        {
            var categories = el.TryGetProperty("CategoryNames", out var catProp)
                ? catProp.Deserialize(DatasourceSerializerContext.Default.ListString) ?? new List<string>()
                : new List<string>();

            if (hiddenCategories != null && hiddenCategories.Any())
                categories = categories.Except(hiddenCategories, StringComparer.OrdinalIgnoreCase).ToList();

            string? uom = null;
            if (el.TryGetProperty("DefaultUnitsNameAbbreviation", out var abbrProp) && abbrProp.ValueKind != JsonValueKind.Null)
            {
                uom = abbrProp.GetString();
            }
            else if (el.TryGetProperty("DefaultUnitsName", out var nameProp) && nameProp.ValueKind != JsonValueKind.Null)
            {
                uom = nameProp.GetString();
            }

            var gFilter = new GlobalFilterElementDTO
            {
                WebId = el.GetProperty("WebId").GetString(),
                Name = el.GetProperty("Name").GetString(),
                Description = el.GetProperty("Description").GetString(),
                Step = el.GetProperty("Step").GetBoolean(),
                UoM = uom ?? string.Empty,
                ValueType = el.GetProperty("Type").GetString(),
                CategoryNames = categories,
                IsDevice = false,
                IsChromUnit = false,
                Link = null
            };

            return gFilter;
        }

        public static GlobalFilterElementDTO ToLevelGlobalFilterElement(this JsonElement el, List<string> hiddenCategories)
        {
            var gFilter = new GlobalFilterElementDTO
            {
                WebId = el.GetProperty("WebId").GetString(),
                Name = el.GetProperty("Name").GetString(),
                Description = el.GetProperty("Description").GetString(),
                Step = false,
                UoM = null,
                ValueType = null,
                CategoryNames = el.GetProperty("CategoryNames")
                                        .Deserialize<List<string>>(DatasourceSerializerContext.Default.ListString)
                                        .Except(hiddenCategories)
                                        .ToList(),
            };

            return gFilter;
        }

        public static ValueDTO ToValue(this JsonElement el)
        {
            var good = el.GetProperty("Good").GetBoolean();

            dynamic value;

            if (!good)
            {
                value = double.NaN;
            }
            else
            {
                var valueElement = el.GetProperty("Value");

                value = valueElement.ValueKind == JsonValueKind.Object
                    ? valueElement.ToEnumValue()
                    : GetJsonElementBaseValue(valueElement) ?? throw new NullReferenceException("Missing value");
            }

            var dataPoint = new ValueDTO
            {
                ObsId = DateTimeOffset.Parse(el.GetProperty("Timestamp").GetString()).UtcDateTime.RoundToMillisecondsUtc(),
                Good = good,
                Value = value,
            };

            return dataPoint;
        }


        public static EnumValueDTO ToEnumValue(this JsonElement el)
        {
            var dataPoint = new EnumValueDTO
            {
                Name = el.GetProperty("Name").GetString(),
                Value = GetJsonElementBaseValue(el.GetProperty("Value")),
            };

            return dataPoint;
        }
        public static SensorStatisticValueDTO ToSummaryValue(this JsonElement el)
        {
            var type = el.GetProperty("Type").ToString();

            var dataPointForAdd = new SensorStatisticValueDTO
            {
                Good = el.GetProperty("Value").GetProperty("Good").GetBoolean(),
                ObsId = DateTimeOffset.Parse(el.GetProperty("Value").GetProperty("Timestamp").ToString()).UtcDateTime,
                Value = el.GetProperty("Value").GetProperty("Value").AsDynamic(),
                SummaryType = type == "TotalWithUOM" ? "Total" : type,
            };

            return dataPointForAdd;
        }

        public static EventFrameValueDTO ToEventframeValue(this JsonElement el)
        {
            var good = el.GetProperty("Value").GetProperty("Good").GetBoolean();

            dynamic value;

            if (!good)
            {
                value = double.NaN;
            }
            else
            {
                var valueElement = el.GetProperty("Value");

                value = GetJsonElementBaseValue(valueElement.ValueKind == JsonValueKind.Object
                    ? valueElement.GetProperty("Value")
                    : valueElement) ?? double.NaN;

                if (value is double d && double.IsNaN(d))
                    good = false;
            }

            var val = new EventFrameValueDTO
            {
                Name = el.GetProperty("Name").GetString()?.Trim('"'),
                Good = good,
                Value = value
            };

            return val;
        }


        public static BatchContextDTO ToBatchContext(this JsonElement el)
        {
            var item = new BatchContextDTO
            {
                Id = el.GetProperty("Id").GetString(),
                StartTime = el.GetProperty("StartTime").GetString(),
                EndTime = el.GetProperty("EndTime").GetString(),
                DeviceWebId = el.GetProperty("RefElementWebIds")[0].GetString(),
            };

            return item;
        }

        public static BatchContextDTO ToChildBatchContext(this JsonElement el, Dictionary<string, string> parentData)
        {
            var parentWebId = string.Empty;

            if (el.TryGetProperty("Links", out var links) && links.TryGetProperty("Parent", out var parentElement))
            {
                string? parentUrl = parentElement.GetString();
                if (!string.IsNullOrEmpty(parentUrl))
                {
                    parentWebId = parentUrl.Split("/").Last();
                }
            }

            parentWebId = WebIdFormatConverter.ConvertFullWebIdToIdOnly(parentWebId);

            var deviceWebId = string.Empty;

            if (!string.IsNullOrEmpty(parentWebId) && parentData.TryGetValue(parentWebId, out var parentDeviceWebId))
            {
                deviceWebId = parentDeviceWebId;
            }

            var item = new BatchContextDTO
            {
                Id = el.GetProperty("Id").GetString(),
                StartTime = el.GetProperty("StartTime").GetString(),
                EndTime = el.GetProperty("EndTime").GetString(),
                DeviceWebId = deviceWebId,
            };

            return item;
        }

        public static List<DatasourceError> ToSensorDatasourceErrors(this JsonElement el)
        {
            return el.ToDatasourceErrors().Where(x => x.ErrorCode == (byte)DatasourceErrorCode.InvalidWebId ||
                     x.ErrorCode == (byte)DatasourceErrorCode.NotFoundWebId)
                     .Select(x => new DatasourceError
                     {
                         Identifier = x.Identifier,
                         Error = x.Error,
                         ErrorCode = (byte)ErrorCode.DeleteSensor
                     }).ToList();
        }
        public static List<DatasourceError> ToEventframeDatasourceErrors(this JsonElement el)
        {
            return el.ToDatasourceErrors().Where(x => x.ErrorCode == (byte)DatasourceErrorCode.InvalidWebId ||
                     x.ErrorCode == (byte)DatasourceErrorCode.NotFoundWebId)
                     .Select(x => new DatasourceError
                     {
                         Identifier = x.Identifier,
                         Error = x.Error,
                         ErrorCode = (byte)ErrorCode.DeletedEventFrameWebId
                     }).ToList();
        }
        public static List<DatasourceError> ToDeviceDatasourceErrors(this JsonElement el)
        {
            return el.ToDatasourceErrors().Where(x => x.ErrorCode == (byte)DatasourceErrorCode.InvalidWebId ||
                     x.ErrorCode == (byte)DatasourceErrorCode.NotFoundWebId)
                     .Select(x => new DatasourceError
                     {
                         Identifier = x.Identifier,
                         Error = x.Error,
                         ErrorCode = (byte)ErrorCode.DeleteDevice
                     }).ToList();
        }
        public static List<DatasourceError> ToTemplateDatasourceErrors(this JsonElement el)
        {
            return el.ToDatasourceErrors().Where(x => x.ErrorCode == (byte)DatasourceErrorCode.InvalidWebId ||
                     x.ErrorCode == (byte)DatasourceErrorCode.NotFoundWebId)
                     .Select(x => new DatasourceError
                     {
                         Identifier = x.Identifier,
                         Error = x.Error,
                         ErrorCode = (byte)ErrorCode.DeletedTemplate
                     }).ToList();
        }

        public static List<DatasourceError> ToDatasourceErrors(this JsonElement el)
        {
            var result = new List<DatasourceError>();

            if (el.TryGetProperty("Exception", out JsonElement exceptionElement))
            {
                el = exceptionElement;
            }

            if (el.TryGetProperty("Errors", out JsonElement errorElement))
            {
                var errorStrings = errorElement.Deserialize(DatasourceSerializerContext.Default.ListString);

                if (errorStrings != null)
                {
                    foreach (var error in errorStrings)
                    {
                        var datasourceError = new DatasourceError
                        {
                            Error = error
                        };

                        result.Add(datasourceError);

                        if (Regex.IsMatch(error, @"Unknown or invalid WebID format:"))
                        {
                            datasourceError.ErrorCode = (byte)DatasourceErrorCode.InvalidWebId;
                            datasourceError.Identifier = Regex.Match(error, @"'([^']*)'").Groups[1].Value;
                        }

                        if (Regex.IsMatch(error, @"WebID '.*' is not found"))
                        {
                            datasourceError.ErrorCode = (byte)DatasourceErrorCode.NotFoundWebId;
                            datasourceError.Identifier = Regex.Match(error, @"'([^']*)'").Groups[1].Value;
                        }

                    }
                }
            }

            return result;
        }

        public static EventFrameDTO ToEventframe(this JsonElement el, bool includeName)
        {
            var item = new EventFrameDTO
            {
                Id = el.GetProperty("Id").GetString(),
                StartTime = el.GetProperty("StartTime").GetString(),
                EndTime = el.GetProperty("EndTime").GetString(),
                Values = new List<EventFrameValueDTO>()
            };

            if (includeName)
            {
                item.Values.Add(new EventFrameValueDTO
                {
                    Name = "Name",
                    Value = el.GetProperty("Name").GetString(),
                    Good = true
                });
            }

            if (el.TryGetProperty("RefElementWebIds", out var refProp) &&
                refProp.ValueKind == JsonValueKind.Array &&
                refProp.GetArrayLength() > 0)
            {
                item.RefWebId = refProp[0].GetString() ?? string.Empty;
            }
            else if (string.IsNullOrEmpty(item.RefWebId) && el.TryGetProperty("Links", out var links) && links.TryGetProperty("Parent", out var parentElement))
            {
                string? parentUrl = parentElement.GetString();
                if (!string.IsNullOrEmpty(parentUrl))
                {
                    item.RefWebId = parentUrl.Split("/").Last();
                }
            }

            return item;
        }

        public static (EventFrameDTO eventframe, string? parentWebId) ToEventframeWithParent(this JsonElement el, bool includeName)
        {
            var item = new EventFrameDTO
            {
                Id = el.GetProperty("Id").GetString(),
                StartTime = el.GetProperty("StartTime").GetString(),
                EndTime = el.GetProperty("EndTime").GetString(),
                Values = new List<EventFrameValueDTO>()
            };

            if (includeName)
            {
                item.Values.Add(new EventFrameValueDTO
                {
                    Name = "Name",
                    Value = el.GetProperty("Name").GetString(),
                    Good = true
                });
            }

            if (el.TryGetProperty("Links", out var links) && links.TryGetProperty("Parent", out var parentElement))
            {
                string? parentUrl = parentElement.GetString();
                if (!string.IsNullOrEmpty(parentUrl))
                {
                    item.RefWebId = parentUrl.Split("/").Last();
                }

                return (item, item.RefWebId);
            }
            else
            {
                item.RefWebId = el.GetProperty("RefElementWebIds")[0].GetString();
            }

            return (item, null);
        }

        public static List<(EventFrameDTO eventFrame, bool hasChildren, bool isDevice)> ToChildEventframe(this JsonElement el, bool includeName)
        {
            var result = new List<(EventFrameDTO, bool, bool)>();

            var items = el.GetProperty("Items").EnumerateArray();

            foreach (var item in items)
            {
                var itemToAdd = new EventFrameDTO
                {
                    Id = item.GetProperty("Id").GetString(),
                    StartTime = item.GetProperty("StartTime").GetString(),
                    EndTime = item.GetProperty("EndTime").GetString(),
                    Values = new List<EventFrameValueDTO>()
                };

                if (includeName)
                {
                    itemToAdd.Values.Add(new EventFrameValueDTO
                    {
                        Name = "Name",
                        Value = item.GetProperty("Name").GetString(),
                        Good = true
                    });
                }

                if (item.TryGetProperty("Links", out var links) && links.TryGetProperty("Parent", out var parentElement))
                {
                    string? parentUrl = parentElement.GetString();
                    if (!string.IsNullOrEmpty(parentUrl))
                    {
                        itemToAdd.RefWebId = WebIdFormatConverter.ConvertFullWebIdToIdOnly(parentUrl.Split("/").Last());
                    }
                }

                bool hasChildrenValue = false;

                if (item.TryGetProperty("HasChildren", out var hasChildren) && hasChildren.GetBoolean())
                {
                    hasChildrenValue = true;
                }

                bool isDevice = item.TryGetProperty("CategoryNames", out var categoriesProp) &&
                   categoriesProp.ValueKind == JsonValueKind.Array &&
                   categoriesProp.EnumerateArray().Any(cat => string.Equals(cat.GetString(), "DC_Asset", StringComparison.OrdinalIgnoreCase));

                result.Add((itemToAdd, hasChildrenValue, isDevice));
            }

            return result;
        }

        public static dynamic? GetJsonElementBaseValue(this JsonElement? element)
        {
            if (element == null)
            {
                return null;
            }

            return element.Value.ValueKind switch
            {
                JsonValueKind.String => element.Value.GetString()!,
                JsonValueKind.Number => element.Value.TryGetInt64(out long l) ? l : element.Value.GetDouble(),
                JsonValueKind.True => true,
                JsonValueKind.False => false,
                JsonValueKind.Null => null,
                _ => throw new InvalidOperationException($"Unsupported JsonValueKind: {element.Value.ValueKind}")
            };
        }
        public static dynamic? AsDynamic(this JsonElement element)
        {
            switch (element.ValueKind)
            {
                case JsonValueKind.Number:
                    // Use raw text so we see exactly how the number appeared in JSON,
                    // e.g. "986" vs "986.0" vs "9.86e2".
                    var raw = element.GetRawText();

                    // If the literal contains no decimal point or exponent, treat as integer.
                    if (!raw.Contains('.') && !raw.Contains('e') && !raw.Contains('E'))
                    {
                        if (long.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out long li))
                            return li;
                    }

                    // Try decimal first (keeps precision). If it has no fractional part, return long.
                    if (decimal.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out decimal dec))
                    {
                        if (dec % 1 == 0) // e.g. 986.0 -> true
                            return (long)dec;
                        return dec;
                    }

                    // Fallback to double
                    if (double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out double d))
                        return d;

                    // Ultimate fallback: let JsonElement produce a double
                    return element.GetDouble();

                case JsonValueKind.String:
                    return element.GetString()!;

                case JsonValueKind.True:
                    return true;

                case JsonValueKind.False:
                    return false;

                case JsonValueKind.Null:
                    return null;

                case JsonValueKind.Object:
                case JsonValueKind.Array:
                    // Keep complex structures as JsonElement (or you can deserialize to object if needed)
                    return element;

                default:
                    return element.ToString();
            }
        }
        private static DateTime RoundToMillisecondsUtc(this DateTime value)
        {
            const long ticksPerMillisecond = TimeSpan.TicksPerMillisecond;
            var dto = new DateTimeOffset(value);
            long utcTicks = dto.UtcTicks;

            // Round to nearest millisecond
            long roundedTicks = (utcTicks + 5000) / ticksPerMillisecond * ticksPerMillisecond;

            return new DateTime(roundedTicks, DateTimeKind.Utc);
        }
    }
}
