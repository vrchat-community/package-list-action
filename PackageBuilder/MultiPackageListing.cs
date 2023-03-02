using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using ICSharpCode.SharpZipLib.Zip;
using Newtonsoft.Json;
using Nuke.Common;
using Nuke.Common.IO;
using Octokit;
using VRC.PackageManagement.Automation.Multi;
using VRC.PackageManagement.Core.Types.Packages;

namespace VRC.PackageManagement.Automation
{
    partial class Build
    {
        private const string PackageListingPublishFilename = "index.json";
        [Parameter("Filename of source json")]
        private const string PackageListingSourceFilename = "source.json";
        private const string WebPageAppFilename = "app.js";

        [Parameter("Path to existing index.json file, typically https://{owner}.github.io/{repo}/index.json")]
        string CurrentListingUrl =>
            $"https://{GitHubActions.RepositoryOwner}.github.io/{GitHubActions.Repository.Split('/')[1]}/{PackageListingPublishFilename}";

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
        
        // assumes that "template-package-listings" repo is checked out in sibling dir for local testing, can be overriden
        [Parameter("Path to Target Listing Root")] 
        static AbsolutePath PackageListingSourceFolder = IsServerBuild
            ? RootDirectory.Parent
            : RootDirectory.Parent / "template-package-listing";
        
        static AbsolutePath PackageListingSourcePath = PackageListingSourceFolder / PackageListingSourceFilename;

        static readonly AbsolutePath WebPageSourcePath = PackageListingSourceFolder / "Website";

        private async Task<List<IVRCPackage>> GetPackagesFromGitHubRepo(string ownerSlashName)
        {
            // Split string into owner and repo, or skip if invalid.
            var parts = ownerSlashName.Split('/');
            if (parts.Length != 2)
            {
                Serilog.Log.Fatal($"Could not get owner and repository from included repo info {parts}.");
                return null;
            }
            string owner = parts[0];
            string name = parts[1];
            
            GitHubClient client = new(new ProductHeaderValue("VRChat-Package-Manager-Automation"));
            if (IsServerBuild)
            {
                client.Credentials = new Credentials(GitHubActions.Token);
            }
            
            var targetRepo = await client.Repository.Get(owner, name);
            if (targetRepo == null)
            {
                Assert.Fail($"Could not get remote repo {owner}/{name}.");
                return null;
            }
            
            // Go through each release
            var releases = await client.Repository.Release.GetAll(owner, name);
            if (releases.Count == 0)
            {
                Serilog.Log.Information($"Found no releases for {owner}/{name}");
                return null;
            }

            var packages = new List<IVRCPackage>();
            
            foreach (Octokit.Release release in releases)
            {
                Serilog.Log.Information($"Looking at {owner}/{name} release {release.Name}.");

                // Check if zipUrl exists and is valid
                Serilog.Log.Information($"Looking for Release Zip for {release.Name}.");

                var zipAssets = release.Assets.Where(asset => asset.Name.EndsWith(".zip")).ToList();
                Serilog.Log.Information($"Found {zipAssets.Count}");

                // Check each zipAsset for a valid release
                foreach (var zipAsset in zipAssets)
                {
                    var manifest = await HashZipAndReturnManifest(zipAsset.BrowserDownloadUrl);
                    if (manifest == null)
                    {
                        Assert.Fail($"Could not create updated manifest from zip file {zipAsset.BrowserDownloadUrl}");
                    }
                
                    Serilog.Log.Information($"Found Release Zip {zipAsset.Name}: {zipAsset.Id}.");

                    // set contents of version object from retrieved manifest
                    packages.Add(manifest);
                }
            }

            return packages;
        }

        Target BuildMultiPackageListing => _ => _
            .Executes(async () =>
            {
                // Get listing source
                var listSourceString = File.ReadAllText(PackageListingSourcePath);
                var listSource = JsonConvert.DeserializeObject<ListingSource>(listSourceString, JsonReadOptions);
                
                // Get existing RepoList or create empty one, so we can skip existing packages
                var currentRepoListString = await GetAuthenticatedString(CurrentListingUrl);
                var currentPackages = (currentRepoListString == null)
                    ? new List<IVRCPackage>()
                    : JsonConvert.DeserializeObject<VRCRepoList>(currentRepoListString, JsonReadOptions).GetAll(); 

                // Make collection for constructed packages
                var packages = new List<VRCPackageManifest>();
                
                // Add GitHub repos if included
                if (listSource.githubRepos != null && listSource.githubRepos.Count > 0)
                {
                    foreach (string ownerSlashName in listSource.githubRepos)
                    {
                        var discoveredPackages = await GetPackagesFromGitHubRepo(ownerSlashName);
                        if (discoveredPackages != null && discoveredPackages.Count > 0)
                        {
                            packages.AddRange(
                                discoveredPackages
                                    .Where(p=>
                                        currentPackages.Exists(m=>m.Id == p.Id && m.Version == p.Version))
                                    .ToList()
                                    .ConvertAll(p => (VRCPackageManifest) p));
                        }
                    }
                }

                // Go through each package 
                if (listSource.packages != null)
                {
                    foreach (var info in listSource.packages)
                    {
                        Serilog.Log.Information($"Looking at {info.id} with {info.releases.Count} releases.");
                    
                        // Just used in logging
                        int releaseIndex = 0;
                    
                        // Go through each release in each package
                        foreach (var release in info.releases)
                        {
                            releaseIndex++;
                        
                            // Skip packages already in listing
                            if (currentPackages.Exists(m => m.Id == info.id && m.Version == release.version))
                            {
                                Serilog.Log.Information($"Listing already contains {info.id} {release.version}, skipping.");
                                continue;
                            }
                            
                            Serilog.Log.Information($"Looking at {info.id} {release.version}.");

                            // Check if zipUrl exists and is valid
                            Serilog.Log.Information($"Checking Zip URL {release.url}.");

                            var manifest = await HashZipAndReturnManifest(release.url);
                            if (manifest == null)
                            {
                                Assert.Fail($"Could not create updated manifest from zip file {release}");
                            }

                            // Ensure the Id and Version from the extracted package match the info supplied in the source 
                            if (manifest.Id != info.id)
                                Assert.Fail($"The manifest id in the zip is {manifest.Id}, which does not match the supplied id {info.id}, cannot publish this package.");
                            
                            if (manifest.Version != release.version)
                                Assert.Fail($"The manifest version in the zip is {manifest.Version}, which does not match the supplied id {release.version}, cannot publish this package.");

                            // set contents of version object from retrieved manifest
                            Serilog.Log.Information($"Zip file exists. Adding package and moving on...");
                            packages.Add(manifest);
                        }
                    }
                }

                // Copy listing-source.json to new Json Object
                Serilog.Log.Information($"All packages prepared, generating Listing.");
                var repoList = new VRCRepoList(packages)
                {
                    name = listSource.name,
                    author = listSource.author.name,
                    url = listSource.url
                };

                // Server builds write into the source directory itself
                // So we dont need to clear it out
                if (!IsServerBuild) {
                    FileSystemTasks.EnsureCleanDirectory(ListPublishDirectory);
                }

                string savePath = ListPublishDirectory / PackageListingPublishFilename;
                repoList.Save(savePath);

                var indexReadPath = WebPageSourcePath / WebPageIndexFilename;
                var appReadPath = WebPageSourcePath / WebPageAppFilename;
                var indexWritePath = ListPublishDirectory / WebPageIndexFilename;
                var indexAppWritePath = ListPublishDirectory / WebPageAppFilename;

                string indexTemplateContent = File.ReadAllText(indexReadPath);

                var listingInfo = new {
                    Name = listSource.name,
                    Url = listSource.url,
                    Description = listSource.description,
                    InfoLink = new {
                        Text = listSource.infoLink?.text,
                        Url = listSource.infoLink?.url,
                    },
                    Author = new {
                        Name = listSource.author.name,
                        Url = listSource.author.url,
                        Email = listSource.author.email
                    },
                    BannerImage = !string.IsNullOrEmpty(listSource.bannerUrl),
                    BannerImageUrl = listSource.bannerUrl,
                };

                var latestPackages = packages.OrderByDescending(p => p.Version).DistinctBy(p => p.Id).ToList();
                var formattedPackages = latestPackages.ConvertAll(p => new {
                    Name = p.Id,
                    Author = new {
                        Name = p.author?.name,
                        Url = p.author?.url,
                    },
                    ZipUrl = p.url,
                    License = p.license,
                    LicenseUrl = p.licensesUrl,
                    Keywords = p.keywords,
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

                if (!IsServerBuild) {
                    FileSystemTasks.CopyDirectoryRecursively(WebPageSourcePath, ListPublishDirectory, DirectoryExistsPolicy.Merge, FileExistsPolicy.Skip);
                }
                
                Serilog.Log.Information($"Saved Listing to {savePath}.");
            });

        string GetPackageType(IVRCPackage p)
        {
            string result = "Any";
            var manifest = p as VRCPackageManifest;
            if (manifest == null) return result;
            
            if (manifest.ContainsAvatarDependencies()) result = "Avatar";
            else if (manifest.ContainsWorldDependencies()) result = "World";
            
            return result;
        }

        async Task<VRCPackageManifest> HashZipAndReturnManifest(string url)
        {
            using (var response = await Http.GetAsync(url))
            {
                if (!response.IsSuccessStatusCode)
                {
                    Assert.Fail($"Could not find valid zip file at {url}");
                }

                var bytes = await response.Content.ReadAsByteArrayAsync();
                var manifestBytes = GetFileFromZip(bytes, PackageManifestFilename);
                var manifestString = Encoding.UTF8.GetString(manifestBytes);
                var manifest = VRCPackageManifest.FromJson(manifestString);
                var hash = GetHashForBytes(bytes);
                manifest.vrchatVersion = hash; // putting the hash in here for now
                // Point manifest towards release
                manifest.url = url;
                return manifest;
            }
        }
        
        public static byte[] GetFileFromZip(byte[] bytes, string fileName)
        {
            byte[] ret = null;
            var stream = new MemoryStream(bytes);
            ZipFile zf = new ZipFile(stream);
            ZipEntry ze = zf.GetEntry(fileName);

            if (ze != null)
            {
                Stream s = zf.GetInputStream(ze);
                ret = new byte[ze.Size];
                s.Read(ret, 0, ret.Length);
            }

            return ret;
        }

        static string GetHashForBytes(byte[] bytes)
        {
            using (var hash = SHA256.Create())
            {
                return string.Concat(hash
                    .ComputeHash(bytes)
                    .Select(item => item.ToString("x2")));
            }
        }
    }
}