using OpenDCP.Framework.Configuration.Model.Errors;
using OpenDCP.Framework.Configuration.Model.Responses.Datasource;
using OpenDCP.Framework.Configuration.Model.Warnings;
using OpenDCP.Framework.Datasource.AVEVAPIwebAPI.Helpers;
using OpenDCP.Framework.DataTypes.Equipment.Constants;
using OpenDCP.Framework.DataTypes.Equipment.Entities.SensorData;
using OpenDCP.Framework.DataTypes.Equipment.Interfaces;
using OpenDCP.Framework.DataTypes.Equipment.Requests.SensorData;
using OpenDCP.Framework.DataTypes.Equipment.Responses.SensorData;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace OpenDCP.Framework.Datasource.AVEVAPIwebAPI.Datasource
{
    public partial class AVEVAPIwebAPIDatasource : ISensorTelemetryDataSource
    {
        public const int MaxPaginationIterations = 20;

        public async Task<SensorDataResponse> GetInterpolatedDataAsync(InterpolatedDataQueryDTO model, CancellationToken cancellationToken = default)
        {
            var result = new SensorDataResponse
            {
                Data = new List<SensorDataDTO>()
            };

            var interval = IntervalHelper.ParseIntervalToTimeSpan(model.Interval);

            DateTime queryEndTime = model.EndTime;

            if (model.BoundaryType == BoundaryType.Outside)
            {
                queryEndTime = model.EndTime.AddSeconds(interval.TotalSeconds);
            }

            bool needsLastPoint = (queryEndTime - model.StartTime).TotalSeconds % interval.TotalSeconds != 0;

            result = await ExecuteWithCachingNotFoundWebIdsAsync(
                    model.WebId,
                    async (webIds) =>
                    {
                        var url = $"{GetMandatoryField("BaseURL")}streamsets/interpolated" +
                                    $"?startTime={model.StartTime.ToIso8601String()}" +
                                    $"&endTime={queryEndTime.ToIso8601String()}" +
                                    $"&interval={model.Interval}" +
                                    $"&webid={string.Join("&webId=", webIds)}" +
                                    $"&selectedFields=Items.Name;Items.WebId;Items.Items.UnitsAbbreviation;Items.UnitsAbbreviation;Items.Items.Value;Items.Items.Timestamp;Items.Path;Items.Items.Good" +
                                    $"&webIdType=IDOnly";

                        var response = await GetAsync(url);
                        return await ValidateResponse(response);
                    },
                    ProcessAndMapStreamsetResponse
                );

            if (needsLastPoint && model.BoundaryType == BoundaryType.Interpolated)
            {
                var last = await GetInterpolatedDataAtTimesAsync(new InterpolatedAtTimeDataQueryDTO
                {
                    Time = new List<DateTime> { queryEndTime },
                    WebId = model.WebId
                });

                foreach (var lastItem in last.Data)
                {
                    var target = result.Data.FirstOrDefault(x => x.WebId == lastItem.WebId);
                    if (target != null)
                    {
                        foreach (var point in lastItem.DataPoints)
                        {
                            if (!target.DataPoints.Any(p => p.ObsId == point.ObsId))
                                target.DataPoints.Add(point);
                        }
                    }
                    else
                    {
                        result.Data.Add(lastItem);
                    }
                }

                if (last.Errors != null && last.Errors.Any())
                {
                    result.Errors.AddRange(last.Errors);
                }

                if (last.Warnings != null && last.Warnings.Any())
                {
                    result.Warnings.AddRange(last.Warnings);
                }
            }

            return result;
        }

        public async Task<SensorDataResponse> GetRecordedDataAsync(RecordedDataQueryDTO model, CancellationToken cancellationToken = default)
        {
            var result = new SensorDataResponse
            {
                Data = new List<SensorDataDTO>()
            };

            result = await ExecuteWithCachingNotFoundWebIdsAsync(
                model.WebId,
                async (webIds) =>
                {
                    var baseUrl = $"{GetMandatoryField("BaseURL")}streamsets/recorded";
                    var url = $"{baseUrl}?startTime={model.StartTime.ToIso8601String()}" +
                              $"&endTime={model.EndTime.ToIso8601String()}" +
                              $"&maxCount={model.MaxCount}" +
                              $"&webid={string.Join("&webId=", webIds)}" +
                              $"&boundaryType={model.BoundaryType}" +
                              $"&selectedFields=Items.Name;Items.WebId;Items.Items.UnitsAbbreviation;Items.UnitsAbbreviation;Items.Items.Value;Items.Items.Timestamp;Items.Path;Items.Items.Good" +
                              $"&webIdType=IDOnly";

                    if (!string.IsNullOrEmpty(model.FilterExpression))
                    {
                        url += $"&filterExpression={model.FilterExpression}" +
                               $"&includeFilteredValues={model.IncludeFilteredValues}";
                    }

                    var response = await GetAsync(url);
                    return await ValidateResponse(response);
                },
                ProcessAndMapStreamsetResponse
            );

            if (model.SynchronizeTimestamps)
            {
                var times = result.Data
                    .SelectMany(x => x.DataPoints.Select(dp => dp.ObsId))
                    .Distinct()
                    .ToList();

                var request = new InterpolatedAtTimeDataQueryDTO
                {
                    Time = times,
                    WebId = model.WebId
                };

                result = await GetInterpolatedDataAtTimesAsync(request);
            }

            return result;
        }

        public async Task<SensorDataResponse> GetPlotDataAsync(PlotDataQueryDTO model, CancellationToken cancellationToken = default)
        {
            var result = new SensorDataResponse
            {
                Data = new List<SensorDataDTO>()
            };

            result = await ExecuteWithCachingNotFoundWebIdsAsync(
                    model.WebId,
                    async (webIds) =>
                    {
                        var url = $"{GetMandatoryField("BaseURL")}streamsets/plot" +
                                    $"?startTime={model.StartTime.ToIso8601String()}" +
                                    $"&endTime={model.EndTime.ToIso8601String()}" +
                                    $"&intervals={model.Intervals}" +
                                    $"&webid={string.Join("&webId=", webIds)}" +
                                    $"&selectedFields=Items.Name;Items.WebId;Items.Items.UnitsAbbreviation;Items.UnitsAbbreviation;Items.Items.Value;Items.Items.Timestamp;Items.Path;Items.Items.Good" +
                                    $"&webIdType=IDOnly";

                        var response = await GetAsync(url);
                        return await ValidateResponse(response);
                    },
                    ProcessAndMapStreamsetResponse
                );

            return result;
        }

        public async Task<SensorDataResponse> GetInterpolatedDataAtTimesAsync(InterpolatedAtTimeDataQueryDTO model, CancellationToken cancellationToken = default)
        {
            var result = new SensorDataResponse
            {
                Data = new List<SensorDataDTO>()
            };

            result = await ExecuteWithCachingNotFoundWebIdsAsync(
                    model.WebId,
                    async (webIds) =>
                    {
                        var url = $"{GetMandatoryField("BaseURL")}streamsets/interpolatedAtTimes" +
                                 $"?time={string.Join("&time=", model.Time.Select(x => x.ToIso8601String()))}" +
                                 $"&webid={string.Join("&webId=", webIds)}" +
                                 $"&selectedFields=Items.Name;Items.WebId;Items.Items.UnitsAbbreviation;Items.UnitsAbbreviation;Items.Items.Value;Items.Items.Timestamp;Items.Path;Items.Items.Good" +
                                 $"&webIdType=IDOnly";

                        var response = await GetAsync(url);
                        return await ValidateResponse(response);
                    },
                    ProcessAndMapStreamsetResponse
                );

            return result;
        }

        public async Task<SensorDataResponse> GetRecordedDataAtTimesAsync(RecordedAtTimeDataQueryDTO model, CancellationToken cancellationToken = default)
        {

            var result = new SensorDataResponse
            {
                Data = new List<SensorDataDTO>()
            };

            result = await ExecuteWithCachingNotFoundWebIdsAsync(
                    model.WebId,
                    async (webIds) =>
                    {
                        var url = $"{GetMandatoryField("BaseURL")}streamsets/recordedAtTimes" +
                                 $"?time={string.Join("&time=", model.Time.Select(x => x.ToIso8601String()))}" +
                                 $"&retrievalMode={model.RetrievalMode}" +
                                 $"&webid={string.Join("&webId=", webIds)}" +
                                 $"&selectedFields=Items.Name;Items.WebId;Items.Items.UnitsAbbreviation;Items.UnitsAbbreviation;Items.Items.Value;Items.Items.Timestamp;Items.Path;Items.Items.Good" +
                                 $"&webIdType=IDOnly";

                        var response = await GetAsync(url);
                        return await ValidateResponse(response);
                    },
                    ProcessAndMapStreamsetResponse
                );

            return result;
        }

        private async Task<SensorDataResponse> ExecuteWithCachingNotFoundWebIdsAsync(
        List<string> initialWebIds,
        Func<List<string>, Task<(JsonElement? content, DatasourceResponse res)>> requestFunc,
        Func<JsonElement, SensorDataResponse> resultMapper)
        {
            var result = new SensorDataResponse { Data = new List<SensorDataDTO>() };
            var remainingWebIds = new List<string>(initialWebIds);
            var failedWebIds = new List<DatasourceError>();

            while (remainingWebIds.Any())
            {
                var (content, res) = await requestFunc(remainingWebIds);

                if (content.HasValue)
                {
                    var mapped = resultMapper(content.Value);
                    result.Data.AddRange(mapped.Data);
                    break;
                }

                if (res.Errors.Any(e => e.ErrorCode == (byte)ErrorCode.NotFound))
                {
                    var error = res.Errors.First();
                    string failedWebId = remainingWebIds.FirstOrDefault(id => error.Error.Contains(id)) ?? remainingWebIds.First();

                    failedWebIds.Add(new DatasourceError
                    {
                        Identifier = failedWebId,
                        ErrorCode = (byte)ErrorCode.DeleteSensor,
                        Error = error.Error
                    });

                    remainingWebIds.Remove(failedWebId);
                    continue;
                }

                result.Errors.AddRange(res.Errors);
                return result;
            }

            result.Errors.AddRange(failedWebIds);
            return result;
        }
        private SensorDataResponse ProcessAndMapStreamsetResponse(JsonElement el)
        {
            var result = new SensorDataResponse
            {
                Data = new List<SensorDataDTO>(),
            };

            if (el.TryGetProperty("Items", out JsonElement attributes))
            {
                foreach (var attribute in attributes.EnumerateArray())
                {
                    var sensorData = new SensorDataDTO
                    {
                        Name = attribute.GetProperty("Name").GetString(),
                        UoM = attribute.GetProperty("UnitsAbbreviation").GetString(),
                        WebId = attribute.GetProperty("WebId").GetString(),
                        DataPoints = new List<ValueDTO>(),
                    };

                    ValueDTO? previousDataPoint = null;

                    foreach (var value in attribute.GetProperty("Items").EnumerateArray())
                    {
                        var dataPoint = value.ToValue();

                        if (previousDataPoint != null && previousDataPoint.Good && !dataPoint.Good)
                        {
                            var interpolatedPoint = new ValueDTO
                            {
                                ObsId = dataPoint.ObsId.AddMilliseconds(-1),
                                Good = true,
                                Value = previousDataPoint.Value
                            };

                            sensorData.DataPoints.Add(interpolatedPoint);
                        }

                        sensorData.DataPoints.Add(dataPoint);

                        previousDataPoint = dataPoint;
                    }

                    result.Data.Add(sensorData);
                }
            }
            else if (el.TryGetProperty("Errors", out JsonElement errors))
            {
                result.Errors.AddRange(errors.EnumerateArray().Select(x => new DatasourceError
                {
                    Error = x.ToString()
                }));
            }
            ;

            return result;
        }

        public async Task<SensorDataSummaryStatisticsChunksResponse> GetSummaryDataChunksAsync(SummaryDataChunksRequest model, CancellationToken cancellationToken = default)
        {
            var result = new SensorDataSummaryStatisticsChunksResponse
            {
                Chunks = new List<SensorSummaryDataChunkDTO>(),
                Errors = new List<DatasourceError>()
            };

            var urlTemplate = $"{GetMandatoryField("BaseURL")}streamsets/summary" +
               $"?startTime={{{{STARTTIME}}}}" +
               $"&endTime={{{{ENDTIME}}}}" +
               $"&webid={{{{SENSORWEBIDS}}}}" +
               $"&summaryType={{{{SUMMARYTYPE}}}}" +
               $"&webIdType=IDOnly" +
               $"&selectedFields=Items.WebId;Items.Name;Items.Items.Value.Timestamp;Items.Items.Value.Value;Items.Items.Value.UnitsAbbreviation;Items.Items.Value.Good;Items.Items.Type;Items.Value.Error";


            var chunksParameterSets = model.Chunks
                .Select(x => new
                {
                    Value = new Dictionary<string, string> {
                      {  "STARTTIME", x.StartTime.ToIso8601String() },
                      {  "ENDTIME", x.EndTime.ToIso8601String() },
                      {  "SENSORWEBIDS", string.Join("&webid=", x.WebId) },
                      {  "SUMMARYTYPE", string.Join("&summaryType=", x.SummaryTypes.Select(s => s.Replace("Total", "TotalWithUoM", StringComparison.OrdinalIgnoreCase)).ToList())},
                },
                    Id = x.ChunkId
                })
                .ToDictionary(x => x.Id, z => z.Value);

            var sections = await PerformMultiGetAsync<List<SensorSummaryDataChunkDTO>>(urlTemplate, chunksParameterSets, this.ProcessSummaryDataChunk);

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
                            Identifier = model.Chunks[i].ChunkId,
                            ErrorCode = (byte)ErrorCode.DeleteSensor,
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

            result.Chunks = sections.Where(x => x.Value.data != null).SelectMany(x => x.Value.data).ToList();

            result.Warnings = sections.SelectMany(x => x.Value.warnings).ToList();

            return result;
        }

        public async Task<SensorDataChunksResponse> GetRecordedDataChunksAsync(RecordedDataChunksRequest model, CancellationToken cancellationToken = default)
        {
            var result = new SensorDataChunksResponse
            {
                Chunks = new List<SensorDataChunkDTO>(),
                Errors = new List<DatasourceError>()
            };

            var urlTemplate = $"{GetMandatoryField("BaseURL")}streamsets/recorded" +
               $"?startTime={{{{STARTTIME}}}}" +
               $"&endTime={{{{ENDTIME}}}}" +
               $"&webid={{{{SENSORWEBIDS}}}}" +
               $"&boundaryType=Interpolated" +
               $"&webIdType=IDOnly" +
               $"&selectedFields=Items.WebId;Items.Name;Items.Items.Timestamp;Items.Items.Value;Items.Items.UnitsAbbreviation;Items.Items.Good;Items.Error" +
               $"&maxCount={{{{MAXCOUNT}}}}";


            var chunksParameterSets = model.Chunks
                  .Select(x => new
                  {
                      Value = new Dictionary<string, string> {
                      {  "STARTTIME", x.StartTime.ToIso8601String() },
                      {  "ENDTIME", x.EndTime.ToIso8601String() },
                      {  "SENSORWEBIDS", string.Join("&webid=", x.WebId) },
                      {  "MAXCOUNT", (150000 / x.WebId.Count).ToString()},
                  },
                      Id = x.ChunkId
                  })
                  .ToDictionary(x => x.Id, z => z.Value);

            var sections = await PerformMultiGetAsync<List<SensorDataChunkDTO>>(urlTemplate, chunksParameterSets, this.ProcessDataChunks);

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
                            Identifier = model.Chunks[i].ChunkId,
                            ErrorCode = (byte)ErrorCode.DeleteSensor,
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

            result.Chunks = sections.Where(x => x.Value.data != null).SelectMany(x => x.Value.data).ToList();
            result.Warnings = sections.SelectMany(x => x.Value.warnings).ToList();

            await AddRemainingChunksAsync(model, result, urlTemplate, cancellationToken);

            return result;
        }

        private async Task AddRemainingChunksAsync(RecordedDataChunksRequest model, SensorDataChunksResponse result, string urlTemplate, CancellationToken cancellationToken)
        {
            var latestBatchChunks = result.Chunks;
            int iterationCount = 0;
            var paginationTimeout = TimeSpan.FromMinutes(1);
            var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutCts.CancelAfter(paginationTimeout);

            try
            {
                while (true)
                {
                    timeoutCts.Token.ThrowIfCancellationRequested();

                    if (++iterationCount > MaxPaginationIterations)
                    {
                        result.Warnings.Add(new DatasourceWarning
                        {
                            Warning = $"Pagination stopped because the maximum iteration limit ({MaxPaginationIterations}) was reached. Returned data may be incomplete."
                        });
                        break;
                    }

                    var paginatedParameterSets = new Dictionary<string, Dictionary<string, string>>();

                    foreach (var originalChunk in model.Chunks)
                    {
                        var recentChunk = latestBatchChunks.FirstOrDefault(c => c.ChunkId == originalChunk.ChunkId);
                        if (recentChunk?.ChunkData == null || recentChunk.ChunkData.Count == 0)
                        {
                            continue;
                        }

                        var saturatedSensors = new List<(SensorDataDTO Sensor, DateTime LatestTimestamp)>();

                        foreach (var sensor in recentChunk.ChunkData)
                        {
                            if (sensor.DataPoints == null || sensor.DataPoints.Count == 0)
                            {
                                continue;
                            }

                            int previousSensorLimit = Math.Min(150000, 150000 / Math.Max(1, recentChunk.ChunkData.Count));

                            if (sensor.DataPoints.Count >= previousSensorLimit)
                            {
                                var maxObsId = sensor.DataPoints.Max(dp => dp.ObsId);
                                if (maxObsId < originalChunk.EndTime)
                                {
                                    saturatedSensors.Add((sensor, maxObsId));
                                }
                            }
                        }

                        if (saturatedSensors.Count > 0)
                        {
                            var saturatedWebIds = saturatedSensors.Select(s => s.Sensor.WebId).Distinct().ToList();

                            var earliestOfLatestTimestamps = saturatedSensors.Min(s => s.LatestTimestamp);

                            int newMaxCountPerSensor = Math.Min(150000, 150000 / Math.Max(1, saturatedWebIds.Count));

                            paginatedParameterSets[originalChunk.ChunkId] = new Dictionary<string, string>
                            {
                                { "STARTTIME", earliestOfLatestTimestamps.ToIso8601String() },
                                { "ENDTIME", originalChunk.EndTime.ToIso8601String() },
                                { "SENSORWEBIDS", string.Join("&webid=", saturatedWebIds) },
                                { "MAXCOUNT", newMaxCountPerSensor.ToString() }
                            };
                        }
                    }

                    if (paginatedParameterSets.Count == 0)
                    {
                        break;
                    }

                    var continuationSections = await PerformMultiGetAsync<List<SensorDataChunkDTO>>(urlTemplate, paginatedParameterSets, this.ProcessDataChunks);

                    var newChunks = continuationSections
                        .Where(x => x.Value.data != null)
                        .SelectMany(x => x.Value.data)
                        .ToList();

                    if (newChunks.Count == 0)
                    {
                        break;
                    }

                    bool addedNewPoints = false;

                    foreach (var newChunk in newChunks)
                    {
                        var existingChunk = result.Chunks.FirstOrDefault(c => c.ChunkId == newChunk.ChunkId);
                        if (existingChunk == null)
                        {
                            result.Chunks.Add(newChunk);
                            addedNewPoints = true;
                            continue;
                        }

                        if (existingChunk.ChunkData == null)
                        {
                            existingChunk.ChunkData = new List<SensorDataDTO>();
                        }

                        foreach (var newSensor in newChunk.ChunkData ?? Enumerable.Empty<SensorDataDTO>())
                        {
                            var existingSensor = existingChunk.ChunkData.FirstOrDefault(s => s.WebId == newSensor.WebId);
                            if (existingSensor == null)
                            {
                                existingChunk.ChunkData.Add(newSensor);
                                addedNewPoints = true;
                            }
                            else if (newSensor.DataPoints != null && newSensor.DataPoints.Count > 0)
                            {
                                var lastExistingTimestamp = existingSensor.DataPoints[^1].ObsId;
                                int startIndex = (newSensor.DataPoints[0].ObsId == lastExistingTimestamp) ? 1 : 0;
                                int countToAdd = newSensor.DataPoints.Count - startIndex;

                                if (countToAdd > 0)
                                {
                                    existingSensor.DataPoints.EnsureCapacity(existingSensor.DataPoints.Count + countToAdd);

                                    for (int i = startIndex; i < newSensor.DataPoints.Count; i++)
                                    {
                                        existingSensor.DataPoints.Add(newSensor.DataPoints[i]);
                                    }

                                    addedNewPoints = true;
                                }
                            }
                        }
                    }

                    result.Warnings.AddRange(continuationSections.SelectMany(x => x.Value.warnings));

                    latestBatchChunks = newChunks;

                    if (!addedNewPoints)
                    {
                        break;
                    }
                }
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                result.Warnings.Add(new DatasourceWarning
                {
                    Warning = $"Pagination stopped because the operation exceeded the time limit of {paginationTimeout.TotalMinutes} minutes. Returned data may be incomplete."
                });
            }
        }

        public async Task<SensorDataChunksResponse> GetInterpolatedDataChunksAsync(InterpolatedDataChunksRequest model, CancellationToken cancellationToken = default)
        {
            var result = new SensorDataChunksResponse
            {
                Chunks = new List<SensorDataChunkDTO>(),
                Errors = new List<DatasourceError>()
            };

            var urlTemplate = $"{GetMandatoryField("BaseURL")}streamsets/interpolated" +
                $"?startTime={{{{STARTTIME}}}}" +
                $"&endTime={{{{ENDTIME}}}}" +
                $"&webid={{{{SENSORWEBIDS}}}}" +
                $"&webIdType=IDOnly" +
                $"&interval={{{{INTERVAL}}}}" +
                $"&selectedFields=Items.WebId;Items.Name;Items.Items.Timestamp;Items.Items.Value;Items.Items.UnitsAbbreviation;Items.Items.Good;Items.Error";


            var chunksParameterSets = model.Chunks
                 .Select(x => new
                 {
                     Value = new Dictionary<string, string> {
                      {  "INTERVAL", x.Interval.ToString() },
                      {  "STARTTIME", x.StartTime.ToIso8601String() },
                      {  "ENDTIME", x.EndTime.ToIso8601String() },
                      {  "SENSORWEBIDS", string.Join("&webid=", x.WebId) },
                 },
                     Id = x.ChunkId
                 })
                 .ToDictionary(x => x.Id, z => z.Value);


            var sections = await PerformMultiGetAsync<List<SensorDataChunkDTO>>(urlTemplate, chunksParameterSets, this.ProcessDataChunks);

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
                            Identifier = model.Chunks[i].ChunkId,
                            ErrorCode = (byte)ErrorCode.DeleteSensor,
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

            result.Chunks = sections.Where(x => x.Value.data != null).SelectMany(x => x.Value.data).ToList();
            result.Warnings = sections.SelectMany(x => x.Value.warnings).ToList();

            return result;
        }

        private (List<SensorDataChunkDTO>, List<DatasourceError> errors, List<DatasourceWarning> warnings) ProcessDataChunks(JsonElement content, string key)
        {
            var result = (
                data: new List<SensorDataChunkDTO>(),
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

                var itemToAdd = new SensorDataChunkDTO
                {
                    ChunkId = key,
                    ChunkData = new List<SensorDataDTO>()
                };

                result.data.Add(itemToAdd);

                foreach (var sensorDetails in items.EnumerateArray())
                {
                    var sensorDetailsToAdd = new SensorDataDTO
                    {
                        Name = sensorDetails.GetProperty("Name").GetString(),
                        WebId = sensorDetails.GetProperty("WebId").GetString(),
                        DataPoints = new List<ValueDTO>()
                    };

                    var dataPointsDetails = sensorDetails.GetProperty("Items").EnumerateArray();

                    int index = 0;

                    foreach (var dataPointDetails in dataPointsDetails)
                    {
                        if (index == 0)
                        {
                            sensorDetailsToAdd.UoM = dataPointDetails.GetProperty("UnitsAbbreviation").ToString();
                        }

                        var dataPoint = dataPointDetails.ToValue();
                        sensorDetailsToAdd.DataPoints.Add(dataPoint);

                        index++;
                    }

                    itemToAdd.ChunkData.Add(sensorDetailsToAdd);
                }
            }
            catch (Exception ex)
            {
                result.errors.Add(new DatasourceError { Error = $"Failed to process template attributes: {ex.Message}" });
            }

            return result;
        }
        private (List<SensorSummaryDataChunkDTO>, List<DatasourceError> errors, List<DatasourceWarning> warnings) ProcessSummaryDataChunk(JsonElement content, string key)
        {
            var result = (
                data: new List<SensorSummaryDataChunkDTO>(),
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

                var itemToAdd = new SensorSummaryDataChunkDTO
                {
                    ChunkId = key,
                    ChunkData = new List<SensorSummaryDataDTO>()
                };

                result.data.Add(itemToAdd);

                foreach (var sensorDetails in items.EnumerateArray())
                {
                    var sensorDetailsToAdd = new SensorSummaryDataDTO
                    {
                        Name = sensorDetails.GetProperty("Name").GetString(),
                        WebId = sensorDetails.GetProperty("WebId").GetString(),
                        DataPoints = new List<SensorStatisticValueDTO>()
                    };


                    var dataPointsDetails = sensorDetails.GetProperty("Items").EnumerateArray();

                    int index = 0;

                    foreach (var dataPointDetails in dataPointsDetails)
                    {
                        if (index == 0)
                        {
                            sensorDetailsToAdd.UoM = dataPointDetails.GetProperty("Value").GetProperty("UnitsAbbreviation").ToString();
                        }

                        sensorDetailsToAdd.DataPoints.Add(dataPointDetails.ToSummaryValue());

                        index++;
                    }

                    itemToAdd.ChunkData.Add(sensorDetailsToAdd);
                }
            }
            catch (Exception ex)
            {
                result.errors.Add(new DatasourceError { Error = $"Failed to process template attributes: {ex.Message}" });
            }

            return result;
        }
    }
}
