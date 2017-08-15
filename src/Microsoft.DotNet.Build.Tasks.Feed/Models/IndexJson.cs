using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using NuGet.Versioning;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Microsoft.DotNet.Build.Tasks.Feed
{
    public class IndexJson
    {
        public List<string> versions { get; set; }

        public List<NuGetVersion> ConvertToVersion
        {
            get
            {
                List<NuGetVersion> nugetVersionList = new List<NuGetVersion>();
                foreach (var version in versions)
                {
                    nugetVersionList.Add(NuGetVersion.Parse(version));
                }
                return nugetVersionList;
            }
        }
    }
}
