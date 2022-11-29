using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Linq;
using Newtonsoft.Json;
using Nuke.Common;
using Nuke.Common.IO;
using VRC.PackageManagement.Automation.Multi;
using VRC.PackageManagement.Core.Types.Packages;

namespace VRC.PackageManagement.Automation
{
    partial class Build
    {
        private const string PackageListingPublishFilename = "index.json";
        private const string PackageListingSourceFilename = "source.json";
        private const string WebPageAppFilename = "app.js";
        private const string WebPageStylesFilename = "styles.css";

        // https://www.newtonsoft.com/json/help/html/T_Newtonsoft_Json_JsonSerializerSettings.htm
        public static JsonSerializerSettings JsonWriteOptions = new()
        {
            NullValueHandling = NullValueHandling.Ignore,
            Formatting = Formatting.Indented,
            Converters = new List<JsonConverter>()
            {
                new PackageConverter(),
                new VersionListConverter()
            },
        };
        
        public static JsonSerializerSettings JsonReadOptions = new()
        {
            NullValueHandling = NullValueHandling.Ignore,
            Converters = new List<JsonConverter>()
            {
                new PackageConverter(),
                new VersionListConverter()
            },
        };
        
        // assumes that "template-package" repo is checked out in sibling dir to this repo, can be overridden
        [Parameter("Path to Target Listing")] 
        private static AbsolutePath PackageListingSourcePath => IsServerBuild
        ? RootDirectory.Parent / PackageListingSourceFilename
        : RootDirectory.Parent / "template-package-listing" / PackageListingSourceFilename;

        private static  readonly AbsolutePath WebPageSourcePath = PackageListingSourcePath.Parent / "Website";

        Target BuildMultiPackageListing => _ => _
            .Executes(async () =>
            {
                // Get listing source
                var listSourceString = File.ReadAllText(PackageListingSourcePath);
                var listSource = JsonConvert.DeserializeObject<ListingSource>(listSourceString, JsonReadOptions);
                
                // Make collection for constructed packages
                var packages = new List<IVRCPackage>();

                // Go through each package 
                foreach (var info in listSource.packages)
                {
                    Serilog.Log.Information($"Looking at {info.name} with {info.releases.Count} releases.");
                    
                    // Just used in logging
                    int releaseIndex = 0;
                    
                    // Go through each release in each package
                    foreach (var release in info.releases)
                    {
                        releaseIndex++;
                        
                        Serilog.Log.Information($"Looking at {info.name} release {releaseIndex}.");
                        
                        // Retrieve manifest
                        Serilog.Log.Information($"Fetching manifest from {release.manifestUrl}.");
                        var manifest = VRCPackageManifest.FromJson(await GetRemoteString(release.manifestUrl));
                        Serilog.Log.Information($"Manifest fetched and deserialized.");
                        
                        // Check if zipUrl exists and is valid
                        Serilog.Log.Information($"Fetching zip headers from {release.zipUrl}.");
                        using (var headerResponse = await Http.GetAsync(release.zipUrl, HttpCompletionOption.ResponseHeadersRead))
                        {
                            if (!headerResponse.IsSuccessStatusCode)
                            {
                                Serilog.Log.Fatal($"Could not find valid zip file at {release.zipUrl}");
                                return;
                            }
                        }
                        // set contents of version object from retrieved manifest
                        Serilog.Log.Information($"Zip file exists. Adding package and moving on...");
                        packages.Add(manifest);
                    }
                }

                // Copy listing-source.json to new Json Object
                Serilog.Log.Information($"All packages prepared, generating Listing.");
                var repoList = new VRCRepoList(packages)
                {
                    name = listSource.name,
                    author = listSource.author,
                    url = listSource.url
                };
                string savePath = ListPublishDirectory / PackageListingPublishFilename;
                repoList.Save(savePath);

                var indexReadPath = WebPageSourcePath / WebPageIndexFilename;
                var appReadPath = WebPageSourcePath / WebPageAppFilename;
                var stylesReadPath = WebPageSourcePath / WebPageStylesFilename;

                var indexWritePath = ListPublishDirectory / WebPageIndexFilename;
                var indexAppWritePath = ListPublishDirectory / WebPageAppFilename;
                var indexStylesWritePath = ListPublishDirectory / WebPageStylesFilename;

                string indexTemplateContent = File.ReadAllText(indexReadPath);

                var listingInfo = new {
                    Name = listSource.name,
                    Url = listSource.url,
                    ListingRepoUrl = "" // this should probably point to the github repository
                };
                
                var formattedPackages = packages.ConvertAll(p => new {
                    Name = p.Id,
                    Author = new {
                        Name = listSource.author,
                        Url = "" // this should probably point at the github/some other url down the line
                    },
                    ZipUrl = listSource.packages.Find(release => release.name == p.Id).releases[0].zipUrl,
                    Type = GetPackageType(p),
                    p.Description,
                    DisplayName = p.Title,
                    p.Version,
                    Dependencies = p.VPMDependencies.Select(dep => new {
                            Name = dep.Key,
                            Version = dep.Value
                        }
                    ).ToList(),
                });

                var rendered = Scriban.Template.Parse(indexTemplateContent).Render(
                    new { listingInfo, packages = formattedPackages }, member => member.Name
                );
                File.WriteAllText(indexWritePath, rendered);

                var appJsRendered = Scriban.Template.Parse(File.ReadAllText(appReadPath)).Render(
                    new { listingInfo, packages = formattedPackages }, member => member.Name
                );
                File.WriteAllText(indexAppWritePath, appJsRendered);
                // Styles are copied over without modifications
                File.WriteAllText(indexStylesWritePath, File.ReadAllText(stylesReadPath));
                
                Serilog.Log.Information($"Saved Listing to {savePath}.");
            });

        string GetPackageType(IVRCPackage p)
        {
            string result = "Any";
            var manifest = p as VRCPackageManifest;
            if (manifest == null) return result;
            
            if (manifest.ContainsAvatarDependencies()) result = "World";
            else if (manifest.ContainsWorldDependencies()) result = "Avatar";
            
            return result;
        }
    }
}