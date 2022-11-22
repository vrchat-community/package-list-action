using System.Collections.Generic;

namespace VRC.PackageManagement.Automation.Multi
{
    public class ListingSource
    {
        public string name { get; set; }
        public string author { get; set; }
        public string url { get; set; }
        public List<PackageInfo> packages { get; set; }
    }
    
    public class PackageInfo
    {
        public string name { get; set; }
        public List<Release> releases { get; set; }
    }

    public class Release
    {
        public string manifestUrl { get; set; }
        public string zipUrl { get; set; }
    }
    
}