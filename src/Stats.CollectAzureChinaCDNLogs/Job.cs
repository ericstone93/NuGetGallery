// Copyright (c) .NET Foundation. All rights reserved.
// Licensed under the Apache License, Version 2.0. See License.txt in the project root for license information.

using System;
using System.Collections.Generic;
using System.ComponentModel.Design;
using System.Threading;
using System.Threading.Tasks;
using Autofac;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
//using Microsoft.WindowsAzure.Storage;
using NuGet.Jobs;
using Azure.Storage.Blobs;
using Stats.AzureCdnLogs.Common;
using Stats.AzureCdnLogs.Common.Collect;

namespace Stats.CollectAzureChinaCDNLogs
{
    public class Job : JsonConfigurationJob
    {
        private const int DefaultExecutionTimeoutInSeconds = 14400; // 4 hours
        private const int MaxFilesToProcess = 4;
        
        private CollectAzureChinaCdnLogsConfiguration _configuration;
        private int _executionTimeoutInSeconds;
        private Collector _chinaCollector;

        public override void Init(IServiceContainer serviceContainer, IDictionary<string, string> jobArgsDictionary)
        {
            base.Init(serviceContainer, jobArgsDictionary);

            InitializeJobConfiguration(_serviceProvider);
        }

        public void InitializeJobConfiguration(IServiceProvider serviceProvider)
        {
            _configuration = serviceProvider.GetRequiredService<IOptionsSnapshot<CollectAzureChinaCdnLogsConfiguration>>().Value;
            _executionTimeoutInSeconds = _configuration.ExecutionTimeoutInSeconds ?? DefaultExecutionTimeoutInSeconds;

            var superstring = _configuration.AzureAccountConnectionStringSource;


            if (string.IsNullOrEmpty(_configuration.AzureAccountConnectionStringSource))
            {
                throw new ArgumentException(nameof(superstring));
            }


            if (string.IsNullOrEmpty(_configuration.AzureAccountConnectionStringDestination))
            {
                throw new ArgumentException(nameof(superstring));
            }

            superstring = superstring.Replace("SharedAccessSignature=?", "SharedAccessSignature=");

            var blobLeaseManager = new AzureBlobLeaseManager(
                serviceProvider.GetRequiredService<ILogger<AzureBlobLeaseManager>>(),
                ValidateAzureBlobServiceClient(superstring),
                _configuration.AzureContainerNameSource,
                "");


            var source = new AzureStatsLogSource(
                ValidateAzureBlobServiceClient(superstring),
                _configuration.AzureContainerNameSource,
                _executionTimeoutInSeconds / MaxFilesToProcess,
                blobLeaseManager,
                serviceProvider.GetRequiredService<ILogger<AzureStatsLogSource>>());

            superstring = _configuration.AzureAccountConnectionStringDestination;
            superstring = superstring.Replace("SharedAccessSignature=?", "SharedAccessSignature=");

            var dest = new AzureStatsLogDestination(
                ValidateAzureBlobServiceClient(superstring),
                _configuration.AzureContainerNameDestination,
                serviceProvider.GetRequiredService<ILogger<AzureStatsLogDestination>>());

            _chinaCollector = new ChinaStatsCollector(
                source,
                dest,
                serviceProvider.GetRequiredService<ILogger<ChinaStatsCollector>>(),
                _configuration.WriteOutputHeader,
                _configuration.AddSourceFilenameColumn);
        }

        public override async Task Run()
        {
            // StreamReader and StreamWriter used in Collector class to process logs
            // don't have ReadLineAsync/WriteLineAsync overloads that accept CancellationToken,
            // so we can't reliably terminate those if they get stuck or very slow. If migrated
            // to .NET 6+ then we can properly propagate the token and this hack would no longer
            // be needed.
            
            // Instead we'll wait a bit extra time after firing main CancellationTokenSource to
            // let it gracefully stop if it is indeed just slow, then stop the process and let it
            // retry processing on restart.
            var forceStopCts = new CancellationTokenSource();
            forceStopCts.CancelAfter(TimeSpan.FromSeconds(_executionTimeoutInSeconds + 60));

            var cts = new CancellationTokenSource();
            cts.CancelAfter(TimeSpan.FromSeconds(_executionTimeoutInSeconds));
            Task<AggregateException> logProcessTask = _chinaCollector
                .TryProcessAsync(maxFileCount: MaxFilesToProcess,
                     fileNameTransform: s => $"{_configuration.DestinationFilePrefix}_{s}",
                     sourceContentType: ContentType.GZip,
                     destinationContentType: ContentType.GZip,
                     token: cts.Token)
                .WithCancellation(forceStopCts.Token);

            AggregateException aggregateExceptions = null;

            try
            {
                aggregateExceptions = await logProcessTask;
            }
            catch (TaskCanceledException)
            {
                Logger.LogWarning("Got TaskCancelledException, terminating");
                return;
            }

            if (aggregateExceptions != null)
            {
                foreach(var ex in aggregateExceptions.InnerExceptions)
                {
                    Logger.LogError(LogEvents.JobRunFailed, ex, ex.Message);
                }
            }

            if(cts.IsCancellationRequested)
            {
                Logger.LogInformation("Execution exceeded the timeout of {ExecutionTimeoutInSeconds} seconds and it was cancelled.", _executionTimeoutInSeconds);
            }
        }

        private static BlobServiceClient ValidateAzureBlobServiceClient(string blobServiceClient)
        {
            if (string.IsNullOrEmpty(blobServiceClient))
            {
                throw new ArgumentException("Job parameter for Azure CDN Blob Service Client is not defined.");
            }

            try
            {
                return new BlobServiceClient(blobServiceClient);
            }
            catch (Exception ex)
            {
                throw new ArgumentException("Job parameter for Azure CDN Blob Service Client is invalid.", ex);
            }
        }

        protected override void ConfigureAutofacServices(ContainerBuilder containerBuilder, IConfigurationRoot configurationRoot)
        {
        }

        protected override void ConfigureJobServices(IServiceCollection services, IConfigurationRoot configurationRoot)
        {
            ConfigureInitializationSection<CollectAzureChinaCdnLogsConfiguration>(services, configurationRoot);
        }
    }
}
