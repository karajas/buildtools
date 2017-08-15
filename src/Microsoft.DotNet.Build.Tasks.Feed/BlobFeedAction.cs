// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System;
using System.Collections.Generic;
using MSBuild = Microsoft.Build.Utilities;
using System.Linq;
using System.Threading.Tasks;
using System.Threading;
using System.IO;
using Microsoft.DotNet.Build.CloudTestTasks;
using System.Net.Http;
using Microsoft.Build.Framework;
using Newtonsoft.Json;
using NuGet.Versioning;
using System.Xml;
using System.Net;

namespace Microsoft.DotNet.Build.Tasks.Feed
{
    sealed class BlobFeedAction: IFeedAction<string>
    {
        private MSBuild.TaskLoggingHelper Log;
        private static readonly CancellationTokenSource TokenSource = new CancellationTokenSource();
        private static readonly CancellationToken CancellationToken = TokenSource.Token;
        private static readonly string DownloadDirectory = Path.Combine(Directory.GetCurrentDirectory(), "download");
        private static readonly string IndexDirectory = Path.Combine(Directory.GetCurrentDirectory(), "tmp");

        public BlobFeed feed;
        public int MaxClients { get; set; } = 8;

        public BlobFeedAction(string accountName, string accountKey, string containerName, MSBuild.TaskLoggingHelper Log)
        {
            this.feed = new BlobFeed(accountName, accountKey, containerName, Log);
            this.Log = Log;
        }

        public Task<bool> DeleteFromFeed(string filterString = "")
        {
            throw new NotImplementedException();
        }

        public Task<IEnumerable<string>> ListPackagesOnFeed(string filterString = "")
        {
            throw new NotImplementedException();
        }

        public Task<IEnumerable<string>> ListVersionsOfPackage(string packageName)
        {
            throw new NotImplementedException();
        }

        public Task<bool> PullFromFeed(string filterString = "")
        {
            throw new NotImplementedException();
        }

        public async Task<bool> PushToFeed(IEnumerable<string> items, string relativePath = "", bool containerExists = false)
        {            
            if (containerExists)
            {
                if (Directory.Exists(DownloadDirectory))
                {
                    Directory.Delete(DownloadDirectory, true);
                }
                Directory.CreateDirectory(DownloadDirectory);
                using (var clientThrottle = new SemaphoreSlim(this.MaxClients, this.MaxClients))
                {
                    List<string> listAzureBlobs = await ListAzureBlobs(feed.AccountName, feed.AccountKey, feed.ContainerName);
                    List<string> indexJsonBlobs = listAzureBlobs.FindAll(blob => blob.Contains("index.json"));

                    // Merge index.json involves downloading the existing feed's index.jsons
                    await Task.WhenAll(items.Select(item => MergeIndexes(CancellationToken, item, relativePath, indexJsonBlobs)));
                }
            }
            if (CancellationToken.IsCancellationRequested)
            {
                Log.LogError("Task UploadToAzure cancelled");
                CancellationToken.ThrowIfCancellationRequested();
            }

            List<string> indexList = new List<string>(Directory.EnumerateFiles(Path.Combine(IndexDirectory, relativePath), "index.json", SearchOption.AllDirectories));
            using (var clientThrottle = new SemaphoreSlim(this.MaxClients, this.MaxClients))
            {
                Func<string, string> calculatePath = (string item) =>
                {
                    Tuple<string, NuGetVersion> blobPath = feed.CalculateBlobPath(item, relativePath);
                    string relativeBlobPath = Path.Combine(relativePath, blobPath.Item1, blobPath.Item2.ToFullString());
                    return Path.Combine(relativeBlobPath, Path.GetFileName(item)).ToLowerInvariant();
                };
                //upload all items
                await Task.WhenAll(items.Select(item => UploadAsync(CancellationToken, item, calculatePath(item), clientThrottle)));
                
                //upload equivalent index.jsons
                Func<string, string> calculateIndexPath = (string item) =>
                {
                    return item.Substring(IndexDirectory.Length + 1).ToLowerInvariant();
                };
                await Task.WhenAll(indexList.Select(item => UploadAsync(CancellationToken, item, calculateIndexPath(item), clientThrottle)));
            }

            return !Log.HasLoggedErrors;
        }

        public async Task<bool> PushToFeedFlat(IEnumerable<string> items, string relativePath = "", bool overwrite = false)
        {
            if (overwrite)
            {
                using (var clientThrottle = new SemaphoreSlim(this.MaxClients, this.MaxClients))
                {
                    Func<string, string> calculatePath = (string item) =>
                    {
                        return item.Substring(item.IndexOf(relativePath)).Replace("\\", "/");
                    };
                    //upload all items
                    await Task.WhenAll(items.Select(item => UploadAsync(CancellationToken, item, calculatePath(item), clientThrottle)));
                }
            }
            else
            {
                throw new Exception("Push failed to due overwrite being false.");
            }
            return !Log.HasLoggedErrors;
        }

        public async Task<List<string>> ListAzureBlobs(string AccountName, string AccountKey, string ContainerName, string FilterBlobNames = "")
        {
            List<string> blobsNames = new List<string>();
            string urlListBlobs = string.Format("https://{0}.blob.core.windows.net/{1}?restype=container&comp=list", AccountName, ContainerName);
            if (!string.IsNullOrWhiteSpace(FilterBlobNames))
            {
                urlListBlobs += $"&prefix={FilterBlobNames}";
            }
            Log.LogMessage(MessageImportance.Low, "Sending request to list blobsNames for container '{0}'.", ContainerName);

            using (HttpClient client = new HttpClient())
            {
                try
                {
                    var createRequest = AzureHelper.RequestMessage("GET", urlListBlobs, AccountName, AccountKey);

                    XmlDocument responseFile;
                    string nextMarker = string.Empty;
                    using (HttpResponseMessage response = await AzureHelper.RequestWithRetry(Log, client, createRequest))
                    {
                        responseFile = new XmlDocument();
                        responseFile.LoadXml(await response.Content.ReadAsStringAsync());
                        XmlNodeList elemList = responseFile.GetElementsByTagName("Name");

                        blobsNames.AddRange(elemList.Cast<XmlNode>()
                                                    .Select(x => x.InnerText)
                                                    .ToList());

                        nextMarker = responseFile.GetElementsByTagName("NextMarker").Cast<XmlNode>().FirstOrDefault()?.InnerText;
                    }
                    while (!string.IsNullOrEmpty(nextMarker))
                    {
                        urlListBlobs = string.Format($"https://{AccountName}.blob.core.windows.net/{ContainerName}?restype=container&comp=list&marker={nextMarker}");
                        var nextRequest = AzureHelper.RequestMessage("GET", urlListBlobs, AccountName, AccountKey);
                        using (HttpResponseMessage nextResponse = AzureHelper.RequestWithRetry(Log, client, nextRequest).GetAwaiter().GetResult())
                        {
                            responseFile = new XmlDocument();
                            responseFile.LoadXml(await nextResponse.Content.ReadAsStringAsync());
                            XmlNodeList elemList = responseFile.GetElementsByTagName("Name");

                            blobsNames.AddRange(elemList.Cast<XmlNode>()
                                                        .Select(x => x.InnerText)
                                                        .ToList());

                            nextMarker = responseFile.GetElementsByTagName("NextMarker").Cast<XmlNode>().FirstOrDefault()?.InnerText;
                        }
                    }
                }
                catch (Exception e)
                {
                    Log.LogErrorFromException(e, true);
                }
                return blobsNames;
            }
        }

        private async Task UploadAsync(CancellationToken ct, string item, string uploadPath, SemaphoreSlim clientThrottle)
        {
            if (!File.Exists(item))
                throw new Exception(string.Format("The file '{0}' does not exist.", item));

            // TODO: Renew lease in case of long uploads

            await clientThrottle.WaitAsync();
            string leaseId = string.Empty;
            bool isLeaseRequired = false;
            using (HttpClient client = new HttpClient())
            {
                try
                {
                    // take a lease on blob, retry if unsuccesful
                    string leaseUrl = AzureHelper.GetBlobRestUrl(feed.AccountName, feed.ContainerName, uploadPath) + "?comp=lease";
                    Tuple<string, string> headerLeaseAction = new Tuple<string, string>("x-ms-lease-action", "acquire");
                    Tuple<string, string> headerLeaseActionDuration = new Tuple<string, string>("x-ms-lease-duration", "60");
                    List<Tuple<string, string>> additionalHeaders = new List<Tuple<string, string>>() { headerLeaseAction, headerLeaseActionDuration };
                    var getLeaseRequest = AzureHelper.RequestMessage("PUT", leaseUrl, feed.AccountName, feed.AccountKey, additionalHeaders);
                    Func<HttpResponseMessage, bool> validate = (HttpResponseMessage response) =>
                    {
                        return response.IsSuccessStatusCode || response.StatusCode == HttpStatusCode.NotFound;
                    };
                    using (HttpResponseMessage response = await AzureHelper.RequestWithRetry(Log, client, getLeaseRequest, validate))
                    {
                        if(response.StatusCode == HttpStatusCode.NotFound)
                        {
                            Log.LogMessage($"Lease not required for {uploadPath}", MessageImportance.Low);
                        }
                        if (response.IsSuccessStatusCode)
                        {
                            leaseId = response.Headers.GetValues("x-ms-lease-id").FirstOrDefault();
                            isLeaseRequired = true;
                            if (string.IsNullOrWhiteSpace(leaseId))
                            {
                                throw new Exception($"Error while reading lease id from response for {uploadPath}");
                            }
                        }
                    }
                }
                catch(Exception e)
                {
                    //Failed after retry 
                    Log.LogErrorFromException(e);
                }
            }

            try
            {
                Log.LogMessage($"Uploading {item} to {uploadPath}.");
                UploadClient uploadClient = new UploadClient(Log);
                await
                    uploadClient.UploadBlockBlobAsync(
                        ct,
                        feed.AccountName,
                        feed.AccountKey,
                        feed.ContainerName,
                        item,
                        uploadPath,
                        leaseId);
            }
            finally
            {
                clientThrottle.Release();
            }
            if (isLeaseRequired) {
                using (HttpClient client = new HttpClient())
                {
                    try
                    {
                        // release a lease on blob, retry if unsuccesful
                        string leaseUrl = AzureHelper.GetBlobRestUrl(feed.AccountName, feed.ContainerName, uploadPath) + "?comp=lease";
                        Tuple<string, string> headerLeaseAction = new Tuple<string, string>("x-ms-lease-action", "release");
                        Tuple<string, string> headerLeaseId = new Tuple<string, string>("x-ms-lease-id", leaseId);
                        List<Tuple<string, string>> additionalHeaders = new List<Tuple<string, string>>() { headerLeaseAction, headerLeaseId };
                        var getLeaseRequest = AzureHelper.RequestMessage("PUT", leaseUrl, feed.AccountName, feed.AccountKey, additionalHeaders);
                        using (HttpResponseMessage response = await AzureHelper.RequestWithRetry(Log, client, getLeaseRequest))
                        {
                            if (response.IsSuccessStatusCode)
                            {
                                Log.LogMessage($"Release lease on {uploadPath}", MessageImportance.Low);
                            }
                        }
                    }
                    catch (Exception e)
                    {
                        //Failed after retry 
                        Log.LogErrorFromException(e);
                    }
                }
            }
        }

        private async Task MergeIndexes(CancellationToken ct, string item, string relativePath, List<string> indexJsonList)
        {
            //pull equivalent index.json
            Tuple<string, NuGetVersion> blobPath = feed.CalculateBlobPath(item, relativePath);
            string relativeBlobPath = Path.Combine(relativePath, blobPath.Item1).ToLowerInvariant().Replace("\\", "/");
            try
            {
                if (indexJsonList.Any(x => x.Contains(relativeBlobPath)))
                {
                    //download index.json to download folder
                    string blob = indexJsonList.Find(x => x.Contains(relativeBlobPath));
                    using (HttpClient client = new HttpClient())
                    {
                        int dirIndex = blob.LastIndexOf("/");
                        string blobDirectory = string.Empty;
                        string blobFilename = string.Empty;

                        if (dirIndex == -1)
                        {
                            blobFilename = blob;
                        }
                        else
                        {
                            blobDirectory = blob.Substring(0, dirIndex);
                            blobFilename = blob.Substring(dirIndex + 1);
                        }

                        string downloadBlobDirectory = Path.Combine(DownloadDirectory, blobDirectory);
                        if (!Directory.Exists(downloadBlobDirectory))
                        {
                            Directory.CreateDirectory(downloadBlobDirectory);
                        }
                        string filename = Path.Combine(downloadBlobDirectory, blobFilename);

                        Log.LogMessage(MessageImportance.Low, "Downloading BLOB - {0}", blob);
                        string urlGetBlob = AzureHelper.GetBlobRestUrl(feed.AccountName, feed.ContainerName, blob);
                        var createRequest = AzureHelper.RequestMessage("GET", urlGetBlob, feed.AccountName, feed.AccountKey);

                        using (HttpResponseMessage response = await AzureHelper.RequestWithRetry(Log, client, createRequest))
                        {
                            if (response.IsSuccessStatusCode)
                            {
                                // Blobs can be files but have the name of a directory.  We'll skip those and log something weird happened.
                                if (!string.IsNullOrEmpty(Path.GetFileName(filename)))
                                {
                                    Stream responseStream = await response.Content.ReadAsStreamAsync();

                                    using (FileStream sourceStream = File.Open(filename, FileMode.Create))
                                    {
                                        responseStream.CopyTo(sourceStream);
                                    }
                                }
                                else
                                {
                                    Log.LogWarning($"Unable to download blob '{blob}' as it has a directory-like name.  This may cause problems if it was needed.");
                                }
                            }
                            else
                            {
                                Log.LogError("Failed to retrieve blob {0}, the status code was {1}", blob, response.StatusCode);
                            }
                        }

                        string generatedIndex, downloadedIndex;
                        //read current index.json
                        using (StreamReader sr = new StreamReader(File.OpenRead(Path.Combine(IndexDirectory, relativeBlobPath, "index.json"))))
                        {
                            generatedIndex = sr.ReadToEnd();
                        }

                        //read downloaded index.json
                        using (StreamReader sr = new StreamReader(File.OpenRead(Path.Combine(DownloadDirectory, relativeBlobPath, "index.json"))))
                        {
                            downloadedIndex = sr.ReadToEnd();
                        }
                        //deserialize
                        List<NuGetVersion> generated = JsonConvert.DeserializeObject<IndexJson>(generatedIndex).ConvertToVersion;
                        List<NuGetVersion> downloaded = JsonConvert.DeserializeObject<IndexJson>(downloadedIndex).ConvertToVersion;

                        //merge
                        foreach(var version in generated)
                        {
                            if (!downloaded.Contains(version))
                            {
                                downloaded.Add(version);
                            }
                        }
                        downloaded.Sort();

                        JsonSerializer jsonSerializer = new JsonSerializer();
                        using (StreamWriter sw = new StreamWriter(File.OpenWrite(Path.Combine(IndexDirectory, relativeBlobPath, "index.json"))))
                        using (JsonWriter writer = new JsonTextWriter(sw))
                        {
                            writer.WriteStartObject();
                            writer.WritePropertyName("versions");
                            writer.WriteStartArray();
                            foreach (var downloadedVersion in downloaded)
                            {
                                writer.WriteRawValue($"\"{downloadedVersion.ToFullString()}\"");
                            }
                            writer.WriteEndArray();
                            writer.WriteEndObject();
                        }
                    }
                }
            }
            catch (Exception e)
            {
                Log.LogErrorFromException(e);
            }
        }
    }
}
