using System;
using System.Collections.Generic;
using System.Text;

namespace UpdateSupportedPackageVersions
{
    public class Package
    {
        public string Id { get; set; }
        public string LtsVersion { get; set; }
        public string CurrentVersion { get; set; }
    }
}
