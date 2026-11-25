using OpenDCP.Framework.Configuration.Model.Constants.Datasource;
using OpenDCP.Framework.Configuration.Model.Extensions;
using OpenDCP.Framework.Datasource.AVEVAPIwebAPI.Constants;
using OpenDCP.Framework.Datasource.AVEVAPIwebAPI.JsonSerializerContexts;
using OpenDCP.Framework.Datasource.Base.Interfaces;
using OpenDCP.Framework.Exceptions;
using OpenDCP.Framework.FormBuilder.Constants;
using OpenDCP.Framework.FormBuilder.Models;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Tasks;

namespace OpenDCP.Framework.Datasource.AVEVAPIwebAPI.Datasource
{
    public partial class AVEVAPIwebAPIDatasource : IDatasource
    {
        public override List<DataType> GetImplementedDataTypes()
        {
            return new List<DataType> {
                DataType.EquipmentData,
                DataType.EventframeContext,
                DataType.AssetHierarchy,
                DataType.SensorTelemetry,
                DataType.Alarms,
                DataType.BatchGenealogy
            };
        }
        [Obsolete]
        protected override ConnectionType Type { get; } = Configuration.Model.Constants.Datasource.ConnectionType.OSISoftPI;
        public static new string ConnectionType { get; } = "OSISoftPI";
        protected override List<InputField> ConnectionStringFields { get; } = new List<InputField>
        {
            new InputField
            {
                FieldType = (byte)InputFieldType.String,
                Label = "PI Server URL",
                Name = "BaseURL",
                FieldValidations = new List<FieldValidationDTO>
                {
                    new FieldValidationDTO
                    {
                        Type = InputFieldValidationType.Required.ToName()
                    }
                }
            },
            new InputField
             {
                FieldType = (byte)InputFieldType.String,
                Label = "Assetdatabase WebId",
                Name = "WebId",
                FieldValidations = new List<FieldValidationDTO>
                {
                    new FieldValidationDTO
                    {
                        Type = InputFieldValidationType.Required.ToName()
                    }
                }
             },
            new InputField
            {
                FieldType = (byte)InputFieldType.String,
                Label = "PI User",
                Name = "User",
                FieldValidations = new List<FieldValidationDTO>
                {
                    new FieldValidationDTO
                    {
                        Type = InputFieldValidationType.Required.ToName()
                    }
                }
            },
             new InputField
            {
                FieldType = (byte)InputFieldType.Secret,
                Label = "PI Password",
                Name = "Password",
                FieldValidations = new List<FieldValidationDTO>
                {
                    new FieldValidationDTO
                    {
                        Type = InputFieldValidationType.Required.ToName()
                    }
                }
            },
            new InputField
            {
                FieldType = (byte)InputFieldType.Checkbox,
                Label = "Include Name",
                Name = "IncludeName",
                FieldValidations = new List<FieldValidationDTO>
                {
                },
                 FieldSettings = new FieldSettings
                {
                    DefaultValue = false
                },
            },
            new InputField
            {
                FieldType = (byte)InputFieldType.Dropdown,
                Label = "Bulk Request Strategy",
                Name = "BulkRequestStrategy",
                FieldOptions = new List<InputFieldOption>
                {
                    new InputFieldOption
                    {
                        Label = "Parallel",
                        Value = "Parallel"
                    },
                    new InputFieldOption
                    {
                        Label = "Bulk",
                        Value = "Bulk"
                    },
                    new InputFieldOption
                    {
                        Label = "Mixed",
                        Value = "Mixed"
                    }
                },
                FieldSettings = new FieldSettings
                {
                    DefaultValue = "Parallel"
                },
                FieldValidations = new List<FieldValidationDTO>
                {
                    new FieldValidationDTO
                    {
                        Type = InputFieldValidationType.Required.ToName()
                    }
                }
            },
            new InputField
            {
                FieldType = (byte)InputFieldType.String,
                Label = "Global Filter Root",
                Name = "GlobalFilterRoot"
            },

             new InputField
             {
                FieldType = (byte)InputFieldType.MultiStringInput,
                Label = "Hidden Categories",
                Name = "HiddenCategories"
             },
             new InputField
             {
                FieldType = (byte)InputFieldType.String,
                Label = "Identifier Key",
                Name = "IdentifierKey",
                FieldSettings  = new FieldSettings
                {
                    DefaultValue = "BatchID"
                },
                FieldValidations = new List<FieldValidationDTO>
                {
                    new FieldValidationDTO
                    {
                        Type = InputFieldValidationType.Required.ToName()
                    }
                }
             },
             new InputField
             {
                FieldType = (byte)InputFieldType.Dropdown,
                Label = "Sensor Filter Type",
                Name = "SensorFilterType",
                FieldOptions = new List<InputFieldOption>
                {
                    new InputFieldOption
                    {
                        Label = "Attribute Filter",
                        Value = "AttributeFilter"
                    },
                    new InputFieldOption
                    {
                        Label = "No Sensor Filter",
                        Value = "NoSensorFilter"
                    },
                    new InputFieldOption
                    {
                        Label = "Element Filter",
                        Value = "ElementFilter"
                    }
                },
                FieldSettings = new FieldSettings
                {
                    DefaultValue = "NoSensorFilter"
                },
                FieldValidations = new List<FieldValidationDTO>
                {
                    new FieldValidationDTO
                    {
                        Type = InputFieldValidationType.Required.ToName()
                    }
                }
             },
             new InputField
             {
                FieldType = (byte)InputFieldType.String,
                Label = "Sensor Filter Condition",
                Name = "SensorFilterCondition",
                ConditionGroups = new List<FieldConditionGroup>
                {
                    new FieldConditionGroup
                    {
                        Operator = LogicalOperator.And,
                        Action = FieldConditionAction.Show,
                        LoadUrl = null,
                        LoadOnce = true,
                        Conditions = new List<FieldCondition>
                        {
                            new FieldCondition
                            {
                                Operator = FieldConditionOperator.In,
                                DependentOn = "SensorFilterType",
                                Values = new List<object> { "AttributeFilter", "ElementFilter" }
                            }
                        }
                    }
                },
                FieldValidations = new List<FieldValidationDTO>
                {
                    new FieldValidationDTO
                    {
                        Type = InputFieldValidationType.Required.ToName()
                    }
                }
             },
        };

        protected override List<string> SecretFields { get; } = new List<string> { "password" };

        protected override List<string> SecretDistributionFields => new List<string> { "baseurl", "user", "password", "bulkrequeststrategy" };

        public async Task ValidateConnectionString(JsonObject connectionString)
        {
            var url = $"{connectionString["BaseURL"]!.GetValue<string>()}assetdatabases/{connectionString["WebId"]!.GetValue<string>()}";

            var filterType = connectionString["SensorFilterType"]!.GetValue<string>();
            var filterCondition = connectionString["SensorFilterCondition"]?.GetValue<string>();

            url = filterType switch
            {
                "AttributeFilter" =>
                    $"{url}/attributecategories?selectedFields=Items.Name&webIdType=IDOnly",

                "ElementFilter" =>
                    $"{url}/elements?selectedFields=Items.Name&searchFullHierarchy=true&nameFilter={filterCondition}&webIdType=IDOnly",

                "NoSensorFilter" =>
                    $"{url}/elements?selectedFields=Items.WebId&webIdType=IDOnly",

                _ => throw new DCPBadRequestException(AvevaErrorMessages.UnkownFilterType)
            };

            HttpResponseMessage response;

            try
            {
                response = await _httpClient.GetAsync(
                  url,
                 (h) =>
                 {
                     h.Authorization = GetAuthHeader(connectionString);
                 });
            }
            catch (Exception ex)
            {
                throw new DCPBadRequestException(AvevaErrorMessages.ConnectionFailed);
            }


            if (response == null || !response.IsSuccessStatusCode)
            {
                throw new DCPBadRequestException(AvevaErrorMessages.ConnectionFailed);
            }

            if (connectionString["SensorFilterType"]!.GetValue<string>() == "AttributeFilter")
            {
                var content = await response.Content.ReadAsStringAsync();
                JsonObject jsonObject = JsonSerializer.Deserialize<JsonObject>(content, DatasourceSerializerContext.Default.JsonObject) ?? throw new InvalidOperationException("Failed to deserialize content");

                if (!jsonObject["Items"]!.AsArray().Any(el => el!.AsObject().ContainsKey("Name") && el.AsObject()["Name"]!.ToString() == filterCondition))
                {
                    throw new DCPBadRequestException(AvevaErrorMessages.FilterConditionNotFound);
                }

            }
            else if (connectionString["SensorFilterType"]!.GetValue<string>() == "ElementFilter")
            {
                var content = await response.Content.ReadAsStringAsync();

                JsonObject jsonObject = JsonSerializer.Deserialize<JsonObject>(content, DatasourceSerializerContext.Default.JsonObject) ?? throw new InvalidOperationException("Failed to deserialize content");
                if (jsonObject["Items"]!.AsArray().Count == 0)
                {
                    throw new DCPBadRequestException(AvevaErrorMessages.FilterConditionNotFound);
                }
            }
        }
    }
}
