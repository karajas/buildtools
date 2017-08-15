// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System.Collections.Generic;
using System.Threading.Tasks;

namespace Microsoft.DotNet.Build.Tasks.Feed
{
    interface IFeedAction<T>
    {
        Task<bool> PushToFeed(IEnumerable<T> items, string relativePath, bool containerExists);

        Task<bool> PullFromFeed(string filterString);

        Task<bool> DeleteFromFeed(string filterString);
        
        Task<IEnumerable<string>> ListPackagesOnFeed(string filterString);

        Task<IEnumerable<string>> ListVersionsOfPackage(string packageName);
    }
}
