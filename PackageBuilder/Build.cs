using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Threading.Tasks;
using Nuke.Common;
using Nuke.Common.CI.GitHubActions;
using Nuke.Common.IO;
using Octokit;
using VRC.PackageManagement.Core.Types.Packages;
using ProductHeaderValue = Octokit.ProductHeaderValue;

[GitHubActions(
    "GHTest",
    GitHubActionsImage.UbuntuLatest,
    On = new[] { GitHubActionsTrigger.WorkflowDispatch, GitHubActionsTrigger.Push },
    EnableGitHubToken = true,
    AutoGenerate = false,
    InvokedTargets = new[] { nameof(BuildRepoListing) })]
class Build : NukeBuild
{
    public static int Main () => Execute<Build>(x => x.BuildRepoListing);

    [Parameter("Configuration to build - Default is 'Debug' (local) or 'Release' (server)")]
    readonly Configuration Configuration = IsLocalBuild ? Configuration.Debug : Configuration.Release;
    
    GitHubActions GitHubActions => GitHubActions.Instance;

    const string PackageManifestFilename = "package.json";
    string CurrentPackageVersion;
    const string VRCAgent = "VCCBootstrap/1.0";

    [Parameter("Directory to save index into")]
    AbsolutePath ListPublishDirectory = RootDirectory / "docs";
    
    [Parameter("PackageName")]
    private string CurrentPackageName = "com.vrchat.demo-template";

    [Parameter("Path to Target Package")] private string TargetPackagePath => RootDirectory / "Packages"  / CurrentPackageName;
    
    protected GitHubClient Client
    {
        get
        {
            if (_client == null)
            {
                _client = new GitHubClient(new ProductHeaderValue("VCC-Nuke"),
                    new Octokit.Internal.InMemoryCredentialStore(new Credentials(GitHubActions.Token)));
            }
            return _client;
        }
    }
    private GitHubClient _client;

    // Assumes single package in this type of listing, make a different one for multi-package sets
    Target BuildRepoListing => _ => _
        .Executes(async () =>
        {
            var packages = new List<IVRCPackage>();
            var repoName = GitHubActions.Repository.Replace($"{GitHubActions.RepositoryOwner}/", "");
            var releases = await Client.Repository.Release.GetAll(GitHubActions.RepositoryOwner, repoName);
            foreach (var release in releases)
            {
                // Release must have package.json and .zip file, or else it will throw an exception here
                var manifest = await GetManifestFromRelease(release);
                if (manifest == null)
                {
                    Serilog.Log.Error($"Could not get manifest from {release.Name}");
                    return;
                }
                
                ReleaseAsset zipAsset = release.Assets.First(asset => asset.Name.EndsWith(".zip"));

                // Set url to .zip asset, add latest package version
                manifest.url = zipAsset.BrowserDownloadUrl;
                packages.Add(manifest);
            }
            
            var latestRelease = await Client.Repository.Release.GetLatest(GitHubActions.RepositoryOwner, repoName);
            
            // Assumes we're publishing both zip and unitypackage
            var latestManifest = await GetManifestFromRelease(latestRelease);
            if (latestManifest == null)
            {
                throw new Exception($"Could not get Manifest for release {latestRelease.Name}");
            }

            var repoList = new VRCRepoList(packages)
            {
                author = latestManifest.author?.name ?? GitHubActions.RepositoryOwner,
                name = $"{latestManifest.name} Releases",
                url = $"https://{GitHubActions.RepositoryOwner}.github.io/{repoName}/index.json"
            };

            string savePath = ListPublishDirectory / "index.json";

            FileSystemTasks.EnsureExistingParentDirectory(savePath);
            repoList.Save(savePath);
        })
        .Triggers(RebuildHomePage);

    async Task<HttpResponseMessage> GetAuthenticatedResponse(string url)
    {
        using (var requestMessage =
            new HttpRequestMessage(HttpMethod.Get, url))
        {
            requestMessage.Headers.Accept.ParseAdd("application/octet-stream");
            requestMessage.Headers.Authorization =
                new AuthenticationHeaderValue("Bearer", GitHubActions.Token);

           return await Http.SendAsync(requestMessage);
        }
    }

    async Task<VRCPackageManifest> GetManifestFromRelease(Release release)
    {
        // Release must have package.json and .zip file, or else it will throw an exception here
        ReleaseAsset manifestAsset =
            release.Assets.First(asset => asset.Name.CompareTo(PackageManifestFilename) == 0);

        var result = await GetAuthenticatedResponse(manifestAsset.Url);
        if (result.IsSuccessStatusCode)
        {
            return VRCPackageManifest.FromJson(await result.Content.ReadAsStringAsync());
        }
        else
        {
            Serilog.Log.Error($"Could not download manifest from {manifestAsset.Url}");
            return null;
        }
    }

    Target RebuildHomePage => _ => _
        .Executes(async () =>
        {
            var repoName = GitHubActions.Repository.Replace($"{GitHubActions.RepositoryOwner}/", "");
            var release = await Client.Repository.Release.GetLatest(GitHubActions.RepositoryOwner, repoName);
            
            // Assumes we're publishing both zip and unitypackage
            var zipUrl = release.Assets.First(asset => asset.Name.EndsWith(".zip")).BrowserDownloadUrl;
            var unityPackageUrl = release.Assets.First(asset => asset.Name.EndsWith(".unitypackage")).BrowserDownloadUrl;
            var manifest = await GetManifestFromRelease(release);
            if (manifest == null)
            {
                throw new Exception($"Could not get Manifest for release {release.Name}");
            }
            var indexPath = ListPublishDirectory / "index.html";
            string indexTemplateContent = File.ReadAllText(indexPath);
            
            if (manifest.author == null) {
                manifest.author = new VRC.PackageManagement.Core.Types.Packages.Author{
                    name = GitHubActions.RepositoryOwner,
                    url = $"https://github.com/{GitHubActions.RepositoryOwner}"
                };
            }

            var rendered = Scriban.Template.Parse(indexTemplateContent).Render(new {manifest, assets=new{zip=zipUrl, unityPackage=unityPackageUrl}}, member => member.Name);
            File.WriteAllText(indexPath, rendered);
            Serilog.Log.Information($"Updated index page at {indexPath}");
        });

    static HttpClient _http;

    public static HttpClient Http
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

    public static async Task<string> GetRemoteString(string url)
    {
        return await Http.GetStringAsync(url);
    }
}
