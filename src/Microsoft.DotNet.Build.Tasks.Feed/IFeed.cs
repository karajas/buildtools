// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System.Collections.Generic;
using System.Threading.Tasks;

namespace Microsoft.DotNet.Build.Tasks.Feed
{
    interface IFeed<T>
    {
        //check if feed exists, key works, and service status is running
        Task<bool> CheckFeedStatus();

        //check if feed exists
        Task<bool> CheckIfFeedExists();
        
        //create feed container
        Task<bool> CreateFeedContainer();

        //Generate indexes
        //multiple version of a package or single version of a package
        string GenerateIndexes(IEnumerable<T> items, string relativePath);

        //sanitize items to perform feed action on.
        // maybe multiple version of a package or single version of a package
        bool IsSanityChecked(IEnumerable<T> items);
    }
}
