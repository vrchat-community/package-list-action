using System.Collections.Generic;
using VRC.PackageManagement.Core.Types.Packages;

namespace VRC.PackageManagement.Automation.Multi
{
    public class ListingSource
    {
        public string name { get; set; }
        public string id { get; set; }
        public Author author { get; set; }
        public string url { get; set; }
        public string description { get; set;}
        public InfoLink infoLink { get; set; }
        public string bannerUrl { get; set; }
        public List<PackageInfo> packages { get; set; }
		public List<string> githubRepos { get; set; }
    }

    public class InfoLink {
        public string text { get; set; }
        public string url { get; set; }
    }

    public class Author {
        public string name { get; set;}
        public string url { get; set;}
        public string email {get; set;}
    }
    
    public class PackageInfo
    {
        public string id { get; set; }
        public List<string> releases { get; set; }
    }
}