using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using ICSharpCode.SharpZipLib.Zip;
using Newtonsoft.Json;
using Nuke.Common;
using Nuke.Common.CI.GitHubActions;
using Nuke.Common.IO;
using Octokit;
using VRC.PackageManagement.Core.Types.Packages;
using ProductHeaderValue = Octokit.ProductHeaderValue;
using ListingSource = VRC.PackageManagement.Automation.Multi.ListingSource;

namespace VRC.PackageManagement.Automation
{
    [GitHubActions(
        "GHTest",
        GitHubActionsImage.UbuntuLatest,
        On = new[] { GitHubActionsTrigger.WorkflowDispatch, GitHubActionsTrigger.Push },
        EnableGitHubToken = true,
        AutoGenerate = false,
        InvokedTargets = new[] { nameof(BuildRepoListing) })]
    partial class Build : NukeBuild
    {
        public static int Main() => Execute<Build>(x => x.BuildRepoListing);

        GitHubActions GitHubActions => GitHubActions.Instance;

        const string PackageManifestFilename = "package.json";
        const string WebPageIndexFilename = "index.html";
        const string VRCAgent = "VCCBootstrap/1.0";
        const string PackageListingPublishFilename = "index.json";
        const string WebPageAppFilename = "app.js";

        [Parameter("Directory to save index into")] 
        AbsolutePath ListPublishDirectory = RootDirectory / "docs";

        [Parameter("PackageName")]
        string CurrentPackageName = "com.vrchat.demo-template";

        [Parameter("Filename of source json")]
        string PackageListingSourceFilename = "source.json";
        
        // assumes that "template-package-listings" repo is checked out in sibling dir for local testing, can be overriden
        [Parameter("Path to Target Listing Root")] 
        AbsolutePath PackageListingSourceFolder = IsServerBuild
            ? RootDirectory.Parent
            : RootDirectory.Parent / "template-package-listing";

        [Parameter("Path to existing index.json file, typically https://{owner}.github.io/{repo}/index.json")]
        string CurrentListingUrl =>
            $"https://{GitHubActions.RepositoryOwner}.github.io/{GitHubActions.Repository.Split('/')[1]}/{PackageListingPublishFilename}";
        
        // assumes that "template-package" repo is checked out in sibling dir to this repo, can be overridden
        [Parameter("Path to Target Package")] 
        AbsolutePath LocalTestPackagesPath => RootDirectory.Parent / "template-package"  / "Packages";
        
        AbsolutePath PackageListingSourcePath => PackageListingSourceFolder / PackageListingSourceFilename;
        AbsolutePath WebPageSourcePath => PackageListingSourceFolder / "Website";

        #region Methods wrapped for GitHub / Local Parity

        string GetRepoName()
        {
            return IsServerBuild
                ? GitHubActions.Repository.Replace($"{GitHubActions.RepositoryOwner}/", "")
                : CurrentPackageName;
        }

        string GetRepoOwner()
        {
            return IsServerBuild ? GitHubActions.RepositoryOwner : "LocalTestOwner";
        }

        #endregion

        ListingSource MakeListingSourceFromManifest(VRCPackageManifest manifest)
        {
            var result = new ListingSource()
            {
                name = $"{manifest.displayName} Listing",
                id = $"{manifest.name}.listing",
                author = new VRC.PackageManagement.Automation.Multi.Author()
                {
                    name = manifest.author.name ?? "",
                    url = manifest.author.url ?? "",
                    email = manifest.author.email ?? ""
                },
                url = CurrentListingUrl,
                description = $"Listing for {manifest.displayName}",
                bannerUrl = "banner.png",
                githubRepos = new List<string>()
                {
                    GitHubActions.Repository
                }
            };
            return result;
        }
        
        Target BuildRepoListing => _ => _
            .Executes(async () =>
            {
                ListingSource listSource;
                
                if (!FileSystemTasks.FileExists(PackageListingSourcePath))
                {
                    AbsolutePath packagePath = RootDirectory.Parent / "Packages"  / CurrentPackageName  / PackageManifestFilename;
                    if (!FileSystemTasks.FileExists(packagePath))
                    {
                        Serilog.Log.Error($"Could not find Listing Source at {PackageListingSourcePath} or Package Manifest at {packagePath}, you need at least one of them.");
                        return;
                    }
                    
                    // Deserialize manifest from packagePath
                    var manifest = JsonConvert.DeserializeObject<VRCPackageManifest>(File.ReadAllText(packagePath), JsonReadOptions);
                    listSource = MakeListingSourceFromManifest(manifest);
                    if (listSource == null)
                    {
                        Serilog.Log.Error($"Could not create listing source from manifest.");
                        return;
                    }
                }
                else
                {
                    // Get listing source
                    var listSourceString = File.ReadAllText(PackageListingSourcePath);
                    listSource = JsonConvert.DeserializeObject<ListingSource>(listSourceString, JsonReadOptions);
                }

                if (string.IsNullOrWhiteSpace(listSource.id))
                {
                    listSource.id = $"io.github.{GetRepoOwner()}.{GetRepoName()}";
                    Serilog.Log.Warning($"Your listing needs an id. We've autogenerated one for you: {listSource.id}. If you want to change it, edit {PackageListingSourcePath}.");
                }
                
                // Get existing RepoList URLs or create empty one, so we can skip existing packages
                var currentRepoListString = IsServerBuild ? await GetAuthenticatedString(CurrentListingUrl) : null;
                var currentPackageUrls = currentRepoListString == null
                    ? new List<string>()
                    : JsonConvert.DeserializeObject<VRCRepoList>(currentRepoListString, JsonReadOptions).GetAll()
                        .Select(package => package.Url).ToList();

                // Make collection for constructed packages
                var packages = new List<VRCPackageManifest>();
                var possibleReleaseUrls = new List<string>();
                
                // Add packages from listing source if included
                if (listSource.packages != null)
                {
                    possibleReleaseUrls.AddRange(
                        listSource.packages?.SelectMany(info => info.releases)
                    );
                }

                // Add GitHub repos if included
                if (listSource.githubRepos != null && listSource.githubRepos.Count > 0)
                {
                    foreach (string ownerSlashName in listSource.githubRepos)
                    {
                        possibleReleaseUrls.AddRange(await GetReleaseZipUrlsFromGitHubRepo(ownerSlashName));
                    }
                }

                // Add each release url to the packages collection if it's not already in the listing, and its zip is valid
                foreach (string url in possibleReleaseUrls)
                {
                    Serilog.Log.Information($"Looking at {url}");
                    if (currentPackageUrls.Contains(url))
                    {
                        Serilog.Log.Information($"Current listing already contains {url}, skipping");
                        continue;
                    }
                    
                    var manifest = await HashZipAndReturnManifest(url);
                    if (manifest == null)
                    {
                        Serilog.Log.Information($"Could not find manifest in zip file {url}, skipping.");
                        continue;
                    }
                    
                    // Add package with updated manifest to collection
                    Serilog.Log.Information($"Found {manifest.Id} ({manifest.name}) {manifest.Version}, adding to listing.");
                    packages.Add(manifest);
                }

                // Copy listing-source.json to new Json Object
                Serilog.Log.Information($"All packages prepared, generating Listing.");
                var repoList = new VRCRepoList(packages)
                {
                    name = listSource.name,
                    id = listSource.id,
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
                
                Serilog.Log.Information($"Made listingInfo {JsonConvert.SerializeObject(listingInfo, JsonWriteOptions)}");

                var latestPackages = packages.OrderByDescending(p => p.Version).DistinctBy(p => p.Id).ToList();
                Serilog.Log.Information($"LatestPackages: {JsonConvert.SerializeObject(latestPackages, JsonWriteOptions)}");
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
                
                Serilog.Log.Information($"formatted packages: {JsonConvert.SerializeObject(formattedPackages, JsonWriteOptions)}");

                var rendered = Scriban.Template.Parse(indexTemplateContent).Render(
                    new { listingInfo, packages = formattedPackages }, member => member.Name
                );
                
                Serilog.Log.Information($"rendered: {rendered}");

                File.WriteAllText(indexWritePath, rendered);

                var appJsRendered = Scriban.Template.Parse(File.ReadAllText(appReadPath)).Render(
                    new { listingInfo, packages = formattedPackages }, member => member.Name
                );
                Serilog.Log.Information($"appJsRendered: {appJsRendered}");
                File.WriteAllText(indexAppWritePath, appJsRendered);

                if (!IsServerBuild) {
                    FileSystemTasks.CopyDirectoryRecursively(WebPageSourcePath, ListPublishDirectory, DirectoryExistsPolicy.Merge, FileExistsPolicy.Skip);
                }
                
                Serilog.Log.Information($"Saved Listing to {savePath}.");
            });

        GitHubClient _client;
        GitHubClient Client
        {
            get
            {
                if (_client == null)
                {
                    _client = new(new ProductHeaderValue("VRChat-Package-Manager-Automation"));
                    if (IsServerBuild)
                    {
                        _client.Credentials = new Credentials(GitHubActions.Token);
                    }
                }

                return _client;
            }
        }
        
        async Task<List<string>> GetReleaseZipUrlsFromGitHubRepo(string ownerSlashName)
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

            var targetRepo = await Client.Repository.Get(owner, name);
            if (targetRepo == null)
            {
                Assert.Fail($"Could not get remote repo {owner}/{name}.");
                return null;
            }
            
            // Go through each release
            var releases = await Client.Repository.Release.GetAll(owner, name);
            if (releases.Count == 0)
            {
                Serilog.Log.Information($"Found no releases for {owner}/{name}");
                return null;
            }

            var result = new List<string>();
            
            foreach (Octokit.Release release in releases)
            {
                result.AddRange(release.Assets.Where(asset => asset.Name.EndsWith(".zip")).Select(asset => asset.BrowserDownloadUrl));
            }

            return result;
        }

        // Keeping this for now to ensure existing listings are not broken
        Target BuildMultiPackageListing => _ => _
            .Triggers(BuildRepoListing);

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

                // Get manifest or return null
                var bytes = await response.Content.ReadAsByteArrayAsync();
                var manifestBytes = GetFileFromZip(bytes, PackageManifestFilename);
                if (manifestBytes == null) return null;
                
                var manifestString = Encoding.UTF8.GetString(manifestBytes);
                var manifest = VRCPackageManifest.FromJson(manifestString);
                var hash = GetHashForBytes(bytes);
                manifest.zipSHA256 = hash; // putting the hash in here for now
                // Point manifest towards release
                manifest.url = url;
                return manifest;
            }
        }
        
        static byte[] GetFileFromZip(byte[] bytes, string fileName)
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

        async Task<HttpResponseMessage> GetAuthenticatedResponse(string url)
        {
            using (var requestMessage =
                   new HttpRequestMessage(HttpMethod.Get, url))
            {
                requestMessage.Headers.Accept.ParseAdd("application/octet-stream");
                if (IsServerBuild)
                {
                    requestMessage.Headers.Authorization =
                        new AuthenticationHeaderValue("Bearer", GitHubActions.Token);
                }

                return await Http.SendAsync(requestMessage);
            }
        }

        async Task<string> GetAuthenticatedString(string url)
        {
            var result = await GetAuthenticatedResponse(url);
            if (result.IsSuccessStatusCode)
            {
                return await result.Content.ReadAsStringAsync();
            }
            else
            {
                Serilog.Log.Error($"Could not download manifest from {url}");
                return null;
            }
        }

        static HttpClient _http;

        static HttpClient Http
        {
            get
            {
                if (_http != null)
                {
                    return _http;
                }

                _http = new HttpClient();
                _http.DefaultRequestHeaders.UserAgent.ParseAdd(VRCAgent);
                return _http;
            }
        }
        
        // https://www.newtonsoft.com/json/help/html/T_Newtonsoft_Json_JsonSerializerSettings.htm
        static JsonSerializerSettings JsonWriteOptions = new()
        {
            NullValueHandling = NullValueHandling.Ignore,
            Formatting = Formatting.Indented,
            Converters = new List<JsonConverter>()
            {
                new PackageConverter(),
                new VersionListConverter()
            },
        };
        
        static JsonSerializerSettings JsonReadOptions = new()
        {
            NullValueHandling = NullValueHandling.Ignore,
            Converters = new List<JsonConverter>()
            {
                new PackageConverter(),
                new VersionListConverter()
            },
        };
    }
}