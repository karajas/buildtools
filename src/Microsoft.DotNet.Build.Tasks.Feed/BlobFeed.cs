// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using Microsoft.Build.Framework;
using MSBuild = Microsoft.Build.Utilities;
using Newtonsoft.Json;
using NuGet.Versioning;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Threading.Tasks;
using System.IO;
using Microsoft.DotNet.Build.CloudTestTasks;
using System;
using System.Net;
using NuGet.Packaging;
using System.Text.RegularExpressions;

namespace Microsoft.DotNet.Build.Tasks.Feed
{
    public sealed class BlobFeed : IFeed<string>
    {
        private MSBuild.TaskLoggingHelper log;
        public string AccountName { get; set; }
        public string AccountKey { get; set; }
        public string ContainerName { get; set; }


        public BlobFeed(string accountName, string accountKey, string containerName, MSBuild.TaskLoggingHelper loggingHelper)
        {
            AccountName = accountName;
            AccountKey = accountKey;
            ContainerName = containerName;
            log = loggingHelper;
        }

        public string GetFeedUrl
        {
            get
            {
                return $"https://{ AccountName }.blob.core.windows.net/";
            }
        }

        public async Task<bool> CheckFeedStatus()
        {
            // check if azure service is up
            string url = $"{ GetFeedUrl }?restype=service&comp=properties";
            using (HttpClient client = new HttpClient())
            {
                client.DefaultRequestHeaders.Clear();
                var request = AzureHelper.RequestMessage("GET", url, AccountName, AccountKey);
                using (HttpResponseMessage response = await AzureHelper.RequestWithRetry(log, client, request))
                {
                    if (response.IsSuccessStatusCode)
                    {
                        log.LogMessage(
                            MessageImportance.Low,
                            $"Service is live for {AccountName}: Status Code:{response.StatusCode} Status Desc: {await response.Content.ReadAsStringAsync()}");
                    }
                    else
                    {
                        log.LogError($"Service is unavailable for {AccountName}: Status Code:{response.StatusCode} Status Desc: {await response.Content.ReadAsStringAsync()}");
                    }
                    return response.IsSuccessStatusCode;
                }
            }
        }

        public async Task<bool> CheckIfFeedExists()
        {
            string url = $"{ GetFeedUrl }{ContainerName}?restype=container";
            using (HttpClient client = new HttpClient())
            {
                client.DefaultRequestHeaders.Clear();
                var request = AzureHelper.RequestMessage("GET", url, AccountName, AccountKey).Invoke();
                using (HttpResponseMessage response = await client.SendAsync(request))
                {
                    if (response.IsSuccessStatusCode)
                    {
                        log.LogMessage(
                            MessageImportance.Low,
                            $"Container {ContainerName} exists for {AccountName}: Status Code:{response.StatusCode} Status Desc: {await response.Content.ReadAsStringAsync()}");
                    }
                    else
                    {
                        log.LogMessage(
                            MessageImportance.Low, 
                            $"Container {ContainerName} does not exist for {AccountName}: Status Code:{response.StatusCode} Status Desc: {await response.Content.ReadAsStringAsync()}");
                    }
                    return response.IsSuccessStatusCode;
                }
            }
        }

        public string GenerateIndexes(IEnumerable<string> items, string relativePath)
        {
            log.LogMessage(MessageImportance.Low, $"START generating indexes for {relativePath}");
            // given a set of packages in a folder generate the root index.json
            // and the respective individual index.jsons
            GenerateRootServiceIndex(AccountName, relativePath);
            foreach (var package in items)
            {
                Tuple<string, NuGetVersion> blobPath = CalculateBlobPath(package);
                GeneratePackageServiceIndex(AccountName, Path.Combine(relativePath, blobPath.Item1), blobPath.Item2.ToFullString());
            }
            log.LogMessage(MessageImportance.Low, $"DONE generating indexes for {relativePath}");
            return Path.Combine(Directory.GetCurrentDirectory(), "tmp");
        }

        public Tuple<string, NuGetVersion> CalculateBlobPath(string package)
        {
            using (var reader = new PackageArchiveReader(package))
            using (var nuspecStream = reader.GetNuspec())
            {
                NuspecReader nuspec = new NuspecReader(nuspecStream);
                return new Tuple<string, NuGetVersion>(nuspec.GetId(), nuspec.GetVersion());
            }
        }

        private void GeneratePackageServiceIndex(string accountName, string redirectUrl, string value)
        {
            string pathToTempFolder = Path.Combine(Directory.GetCurrentDirectory(), "tmp", redirectUrl);
            if (Directory.Exists(pathToTempFolder))
            {
                Directory.Delete(pathToTempFolder, true);
            }
            Directory.CreateDirectory(pathToTempFolder);
            using (FileStream fs = File.OpenWrite(Path.Combine(pathToTempFolder, "index.json")))
            {
                using (var streamWriter = new StreamWriter(fs))
                {
                    using (JsonTextWriter writer = new JsonTextWriter(streamWriter))
                    {
                        writer.WriteStartObject();
                        writer.WritePropertyName("versions");
                        writer.WriteStartArray();
                        writer.WriteRawValue($"\"{value}\"");
                        writer.WriteEndArray();
                        writer.WriteEndObject();
                    }
                }
            }
        }

        private void GenerateRootServiceIndex(string accountName, string redirectUrl)
        {
            string pathToTempFolder = Path.Combine(Directory.GetCurrentDirectory(), "tmp", redirectUrl);
            if (Directory.Exists(pathToTempFolder))
            {
                Directory.Delete(pathToTempFolder, true);
            }
            Directory.CreateDirectory(pathToTempFolder);
            using (FileStream fs = File.OpenWrite(Path.Combine(pathToTempFolder, "index.json")))
            {
                using (var streamWriter = new StreamWriter(fs))
                {
                    using (JsonTextWriter writer = new JsonTextWriter(streamWriter))
                    {
                        writer.WriteStartObject();
                        writer.WritePropertyName("version");
                        writer.WriteRawValue("\"3.0.0\"");
                        writer.WritePropertyName("resources");
                        writer.WriteStartArray();
                        writer.WriteStartObject();
                        writer.WritePropertyName("@id");
                        writer.WriteRawValue($"\"{ GetFeedUrl }{ContainerName}/{redirectUrl}/\"");
                        writer.WritePropertyName("@type");
                        writer.WriteRawValue("\"PackageBaseAddress/3.0.0\"");
                        writer.WritePropertyName("comment");
                        writer.WriteRawValue("\"Base URL of Azure storage where NuGet package registration info for intermediaries is stored.\"");
                        writer.WriteEndObject();
                        writer.WriteEndArray();
                        writer.WritePropertyName("@context");
                        writer.WriteStartObject();
                        writer.WritePropertyName("@vocab");
                        writer.WriteRawValue("\"http://schema.nuget.org/schema#\"");
                        writer.WritePropertyName("comment");
                        writer.WriteRawValue("\"http://www.w3.org/2000/01/rdf-schema#comment\"");
                        writer.WriteEndObject();
                        writer.WriteEndObject();
                    }
                }
            }
        }

        public bool IsSanityChecked(IEnumerable<string> items)
        {
            log.LogMessage(MessageImportance.Low, $"START checking sanitized items for feed");
            //check for duplicates blob endpoints
            foreach (var item in items)
            {
                if (items.Any(s => Path.GetExtension(item) != "nupkg" || !Path.GetFileName(s).Equals(Path.GetFileName(item))))
                {
                    log.LogError($"{item} is not a nupkg or duplicated.");
                    return false;
                }
            }
            log.LogMessage(MessageImportance.Low, $"DONE checking for sanitized items for feed");
            return true;
        }

        public async Task<bool> CreateFeedContainer()
        {
            ValidateContainerName(ContainerName);
            string url = $"https://{AccountName}.blob.core.windows.net/{ContainerName}?restype=container";
            using (HttpClient client = new HttpClient())
            {
                Tuple<string, string> headerBlobType = new Tuple<string, string>("x-ms-blob-public-access", "blob");
                List<Tuple<string, string>> additionalHeaders = new List<Tuple<string, string>>() { headerBlobType };
                var createRequest = AzureHelper.RequestMessage("PUT", url, AccountName, AccountKey, additionalHeaders);

                using (HttpResponseMessage response = await AzureHelper.RequestWithRetry(log, client, createRequest))
                {
                    try
                    {
                        log.LogMessage(
                            MessageImportance.Low,
                            "Received response to create Container {0}: Status Code: {1} {2}",
                            ContainerName, response.StatusCode, response.Content.ToString());
                    }
                    catch (Exception e)
                    {
                        log.LogErrorFromException(e, true);
                    }
                }
            }
            return !log.HasLoggedErrors;
        }

        private void ValidateContainerName(string container)
        {
            if (container.Length < 3 || container.Length > 63 || !Regex.IsMatch(container, @"^[a-z0-9]+(-[a-z0-9]+)*$"))
                throw new Exception("Container Name is invalid");
        }
    }
}
