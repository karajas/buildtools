// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System;
using System.Threading.Tasks;
using MSBuild = Microsoft.Build.Utilities;
using Microsoft.Build.Framework;
using System.Collections.Generic;
using System.Diagnostics;

namespace Microsoft.DotNet.Build.Tasks.Feed
{

    public class FeedActionTask : MSBuild.Task
    {

        [Required]
        public string Task { get; set; }
        
        public string Type { get; set; }
        
        public string FeedAccountName { get; set; }
        
        public string FeedAccountKey { get; set; }

        public bool Debug { get; set; }

        public ITaskItem[] ItemsToPublish { get; set; }

        public string RelativePath { get; set; }

        public bool PublishFlatContainer { get; set; }

        public bool Overwrite { get; set; }

        public ITaskItem[] EnableFeeds { get; set; }

        public ITaskItem[] DisableFeeds { get; set; }
        public string RepoRoot { get; set; }

        public override bool Execute()
        {
            return ExecuteAsync().GetAwaiter().GetResult();
        }

        public async Task<bool> ExecuteAsync()
        {
            if(Debug)
                Debugger.Launch();
            try
            {
                switch (Task)
                {
                    case "Push":
                        Log.LogMessage(MessageImportance.High, "Performing feed push...");
                        if (ItemsToPublish == null)
                        {
                            Log.LogError($"No items to push. Please check ItemGroup ItemsToPublish.");
                        }
                        if (string.IsNullOrEmpty(RelativePath))
                        {
                            Log.LogWarning($"No relative path. Items are pushed to root of container.");
                        }
                        if (Type == "Blob") {
                            //FeedAccountName is of the form - https://account.blob.core.windows.net/container/
                            string removeHeader = FeedAccountName.Substring(FeedAccountName.IndexOf("https://")+8);
                            string accountName = removeHeader.Split('.')[0];
                            string containerName = removeHeader.Split('/')[1];
                            BlobFeedAction blobFeedAction = new BlobFeedAction(accountName, FeedAccountKey, containerName, Log);
                            if (!PublishFlatContainer)
                            {
                                blobFeedAction.feed.GenerateIndexes(ConvertToStringLists(ItemsToPublish), RelativePath);
                                if (!await blobFeedAction.feed.CheckIfFeedExists())
                                {
                                    if (await blobFeedAction.feed.CheckFeedStatus())
                                    {
                                        await blobFeedAction.feed.CreateFeedContainer();
                                        await blobFeedAction.PushToFeed(ConvertToStringLists(ItemsToPublish), RelativePath);
                                    }
                                }
                                else
                                {
                                    if (await blobFeedAction.feed.CheckFeedStatus())
                                    {
                                        //Push to feed after index merging
                                        await blobFeedAction.PushToFeed(ConvertToStringLists(ItemsToPublish), RelativePath, true);
                                    }
                                }
                            }
                            else
                            {
                                if (await blobFeedAction.feed.CheckFeedStatus())
                                {
                                    if (!await blobFeedAction.feed.CheckIfFeedExists())
                                    {
                                        await blobFeedAction.feed.CreateFeedContainer();
                                    }
                                    await blobFeedAction.PushToFeedFlat(ConvertToStringLists(ItemsToPublish), RelativePath, Overwrite);
                                }
                            }
                        }
                        else
                        {
                            Log.LogError($"Feed Type not supported.");
                        }
                        break;
                    case "ConfigureInput":
                        if (EnableFeeds == null)
                        {
                            Log.LogError($"No feeds to enable. Please check ItemGroup EnableFeeds.");
                        }
                        Log.LogMessage(MessageImportance.High, "Performing feed configure input...");
                        ConfigureInputFeed cif = new ConfigureInputFeed();
                        cif.GenerateNugetConfig(EnableFeeds, DisableFeeds, RepoRoot, Log);
                        break;
                }
            }
            catch (Exception e)
            {
                Log.LogErrorFromException(e, true);
            }
            return !Log.HasLoggedErrors;
        }

        private IEnumerable<string> ConvertToStringLists(ITaskItem[] taskItems)
        {
            List<string> stringList = new List<string>();
            foreach(var item in taskItems)
            {
                stringList.Add(item.ItemSpec);
            }
            return stringList;
        }
    }
}
