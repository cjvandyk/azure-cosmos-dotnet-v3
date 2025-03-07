﻿//------------------------------------------------------------
// Copyright (c) Microsoft Corporation.  All rights reserved.
//------------------------------------------------------------

namespace Microsoft.Azure.Cosmos.Telemetry
{
    using System;
    using System.Collections.Generic;
    using System.Diagnostics;
    using global::Azure.Core;
    using Microsoft.Azure.Cosmos.Telemetry.Diagnostics;

    /// <summary>
    /// This class is used to add information in an Activity tags ref. https://github.com/Azure/azure-cosmos-dotnet-v3/issues/3058
    /// </summary>
    internal struct OpenTelemetryCoreRecorder : IDisposable
    {
        private const string CosmosDb = "cosmosdb";

        private readonly DiagnosticScope scope = default;
        private readonly CosmosThresholdOptions config = null;
        private readonly Activity activity = null;

        private readonly Documents.OperationType operationType = Documents.OperationType.Invalid;
        private OpenTelemetryAttributes response = null;

        internal static IDictionary<Type, Action<Exception, DiagnosticScope>> OTelCompatibleExceptions = new Dictionary<Type, Action<Exception, DiagnosticScope>>()
        {
            { typeof(CosmosNullReferenceException), (exception, scope) => CosmosNullReferenceException.RecordOtelAttributes((CosmosNullReferenceException)exception, scope)},
            { typeof(CosmosObjectDisposedException), (exception, scope) => CosmosObjectDisposedException.RecordOtelAttributes((CosmosObjectDisposedException)exception, scope)},
            { typeof(CosmosOperationCanceledException), (exception, scope) => CosmosOperationCanceledException.RecordOtelAttributes((CosmosOperationCanceledException)exception, scope)},
            { typeof(CosmosException), (exception, scope) => CosmosException.RecordOtelAttributes((CosmosException)exception, scope)},
            { typeof(ChangeFeedProcessorUserException), (exception, scope) => ChangeFeedProcessorUserException.RecordOtelAttributes((ChangeFeedProcessorUserException)exception, scope)}
        };

        private OpenTelemetryCoreRecorder(DiagnosticScope scope)
        {
            this.scope = scope;
            this.scope.Start();
        }

        private OpenTelemetryCoreRecorder(string operationName)
        {
            this.activity = new Activity(operationName);
            this.activity.Start();
        }

        private OpenTelemetryCoreRecorder(
            DiagnosticScope scope,
            string operationName,
            string containerName,
            string databaseName,
            Documents.OperationType operationType, 
            CosmosClientContext clientContext, CosmosThresholdOptions config)
        {
            this.scope = scope;
            this.config = config;
            this.operationType = operationType;
            
            if (scope.IsEnabled)
            {
                this.scope.Start();

                this.Record(
                        operationName: operationName,
                        containerName: containerName,
                        databaseName: databaseName,
                        clientContext: clientContext);
            }
        }

        /// <summary>
        /// Used for creating parent activity in scenario where there are no listeners at operation level 
        /// but they are present at network level
        /// </summary>
        public static OpenTelemetryCoreRecorder CreateNetworkLevelParentActivity(DiagnosticScope networkScope)
        {
            return new OpenTelemetryCoreRecorder(networkScope);
        }

        /// <summary>
        /// Used for creating parent activity in scenario where there are no listeners at operation level and network level
        /// </summary>
        public static OpenTelemetryCoreRecorder CreateParentActivity(string operationName)
        {
            return new OpenTelemetryCoreRecorder(operationName);
        }

        /// <summary>
        /// Used for creating parent activity in scenario where there are listeners at operation level 
        /// </summary>
        public static OpenTelemetryCoreRecorder CreateOperationLevelParentActivity(
            DiagnosticScope operationScope,
            string operationName,
            string containerName,
            string databaseName,
            Documents.OperationType operationType,
            CosmosClientContext clientContext,
            CosmosThresholdOptions config)
        {
            return new OpenTelemetryCoreRecorder(
                        operationScope,
                        operationName,
                        containerName,
                        databaseName,
                        operationType,
                        clientContext,
                        config);
        }

        public bool IsEnabled => this.scope.IsEnabled;

        public void Record(string key, string value)
        {
            if (this.IsEnabled)
            {
                this.scope.AddAttribute(key, value);
            }
        }
        
        /// <summary>
        /// Recording information
        /// </summary>
        /// <param name="operationName"></param>
        /// <param name="containerName"></param>
        /// <param name="databaseName"></param>
        /// <param name="clientContext"></param>
        public void Record(
            string operationName,
            string containerName,
            string databaseName,
            CosmosClientContext clientContext)
        {
            if (this.IsEnabled)
            {
                this.scope.AddAttribute(OpenTelemetryAttributeKeys.DbOperation, operationName);
                this.scope.AddAttribute(OpenTelemetryAttributeKeys.DbName, databaseName);
                this.scope.AddAttribute(OpenTelemetryAttributeKeys.ContainerName, containerName);
                
                // Other information
                this.scope.AddAttribute(OpenTelemetryAttributeKeys.DbSystemName, OpenTelemetryCoreRecorder.CosmosDb);
                this.scope.AddAttribute(OpenTelemetryAttributeKeys.MachineId, VmMetadataApiHandler.GetMachineId());
                this.scope.AddAttribute(OpenTelemetryAttributeKeys.NetPeerName, clientContext.Client?.Endpoint?.Host);

                // Client Information
                this.scope.AddAttribute(OpenTelemetryAttributeKeys.ClientId, clientContext?.Client?.Id);
                this.scope.AddAttribute(OpenTelemetryAttributeKeys.UserAgent, clientContext.UserAgent);
                this.scope.AddAttribute(OpenTelemetryAttributeKeys.ConnectionMode, clientContext.ClientOptions.ConnectionMode);
            }
        }

        /// <summary>
        /// Record attributes from response
        /// </summary>
        /// <param name="response"></param>
        public void Record(OpenTelemetryAttributes response)
        {
            if (this.IsEnabled)
            {
                this.response = response;
            }
        }

        /// <summary>
        /// Record attributes during exception
        /// </summary>
        /// <param name="exception"></param>
        public void MarkFailed(Exception exception)
        {
            if (this.IsEnabled)
            {
                this.scope.AddAttribute(OpenTelemetryAttributeKeys.ExceptionStacktrace, exception.StackTrace);
                this.scope.AddAttribute(OpenTelemetryAttributeKeys.ExceptionType, exception.GetType());

                // If Exception is not registered with open Telemetry
                if (!OpenTelemetryCoreRecorder.IsExceptionRegistered(exception, this.scope))
                {
                    this.scope.AddAttribute(OpenTelemetryAttributeKeys.ExceptionMessage, exception.Message);
                }

                if (exception is not CosmosException || (exception is CosmosException cosmosException
                            && !DiagnosticsFilterHelper
                                    .IsSuccessfulResponse(cosmosException.StatusCode, cosmosException.SubStatusCode)))
                {
                    this.scope.Failed(exception);
                }
            }
        }

        /// <summary>
        /// Checking if passed exception is registered with Open telemetry or Not
        /// </summary>
        /// <param name="exception"></param>
        /// <param name="scope"></param>
        /// <returns>tru/false</returns>
        internal static bool IsExceptionRegistered(Exception exception, DiagnosticScope scope)
        {
            foreach (KeyValuePair<Type, Action<Exception, DiagnosticScope>> registeredExceptionHandlers in OpenTelemetryCoreRecorder.OTelCompatibleExceptions)
            {
                Type exceptionType = exception.GetType();
                if (registeredExceptionHandlers.Key.IsAssignableFrom(exceptionType))
                {
                    registeredExceptionHandlers.Value(exception, scope);

                    return true;
                }
            }

            return false;
        }

        public void Dispose()
        {
            if (this.IsEnabled)
            {
                Documents.OperationType operationType 
                    = (this.response == null || this.response?.OperationType == Documents.OperationType.Invalid) ? this.operationType : this.response.OperationType;

                this.scope.AddAttribute(OpenTelemetryAttributeKeys.OperationType, operationType);

                if (this.response != null)
                {
                    this.scope.AddAttribute(OpenTelemetryAttributeKeys.RequestContentLength, this.response.RequestContentLength);
                    this.scope.AddAttribute(OpenTelemetryAttributeKeys.ResponseContentLength, this.response.ResponseContentLength);
                    this.scope.AddAttribute(OpenTelemetryAttributeKeys.StatusCode, (int)this.response.StatusCode);
                    this.scope.AddAttribute(OpenTelemetryAttributeKeys.SubStatusCode, this.response.SubStatusCode);
                    this.scope.AddAttribute(OpenTelemetryAttributeKeys.RequestCharge, this.response.RequestCharge);
                    this.scope.AddAttribute(OpenTelemetryAttributeKeys.ItemCount, this.response.ItemCount);
                    this.scope.AddAttribute(OpenTelemetryAttributeKeys.ActivityId, this.response.ActivityId);
                    this.scope.AddAttribute(OpenTelemetryAttributeKeys.CorrelatedActivityId, this.response.CorrelatedActivityId);

                    if (this.response.Diagnostics != null)
                    {
                        this.scope.AddAttribute(OpenTelemetryAttributeKeys.Region, ClientTelemetryHelper.GetContactedRegions(this.response.Diagnostics.GetContactedRegions()));
                        CosmosDbEventSource.RecordDiagnosticsForRequests(this.config, operationType, this.response);
                    }

                    if (!DiagnosticsFilterHelper.IsSuccessfulResponse(this.response.StatusCode, this.response.SubStatusCode))
                    {
                        this.scope.Failed();
                    }
                }

                this.scope.Dispose();
            }
            else
            {
                this.activity?.Stop();
            }
        }
    }
}
