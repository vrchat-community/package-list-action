using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
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
    InvokedTargets = new[] { nameof(BuildRepoListing) })]
class Build : NukeBuild
{
    public static int Main () => Execute<Build>(x => x.BuildRepoListing);

    [Parameter("Configuration to build - Default is 'Debug' (local) or 'Release' (server)")]
    readonly Configuration Configuration = IsLocalBuild ? Configuration.Debug : Configuration.Release;
    
    GitHubActions GitHubActions => GitHubActions.Instance;

    private const string PackageManifestFilename = "package.json";
    private const string PackageVersionProperty = "version";
    private string CurrentPackageVersion;
    private const string VRCAgent = "VCCBootstrap/1.0";

    private AbsolutePath ListPublishDirectory = RootDirectory / "docs";
    
    [Parameter("PackageName")]
    private string CurrentPackageName = "com.vrchat.demo-template";

    [Parameter("Path to Target Package")] private string TargetPackagePath;
    
    Target ConfigurePackageVersion => _ => _
        .Executes(() =>
        {
            Serilog.Log.Information($"TargetPackagePath is {TargetPackagePath}");
            var manifestFile = (AbsolutePath)TargetPackagePath / PackageManifestFilename;
            var jManifest = JObject.Parse(File.ReadAllText(manifestFile));
            CurrentPackageVersion = jManifest.Value<string>(PackageVersionProperty);
            if (string.IsNullOrWhiteSpace(CurrentPackageVersion))
            {
                throw new Exception($"Could not find Package Version in {manifestFile.Name}");
            }
            Serilog.Log.Information($"Found version {CurrentPackageVersion} for {manifestFile}");
        });
    
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
        .Executes( async () =>
        {
            var packages = new List<IVRCPackage>();
            var repoName = GitHubActions.Repository.Replace($"{GitHubActions.RepositoryOwner}/", "");
            var releases = await Client.Repository.Release.GetAll(GitHubActions.RepositoryOwner, repoName);
            foreach (var release in releases)
            {
                // Release must have package.json and .zip file, or else it will throw an exception here
                ReleaseAsset manifestAsset = release.Assets.First(asset => asset.Name.CompareTo(PackageManifestFilename) == 0);
                ReleaseAsset zipAsset = release.Assets.First(asset => asset.Name.EndsWith(".zip"));

                using (var requestMessage =
                    new HttpRequestMessage(HttpMethod.Get, manifestAsset.Url))
                {
                    requestMessage.Headers.Accept.ParseAdd("application/octet-stream");
                    requestMessage.Headers.Authorization =
                        new AuthenticationHeaderValue("Bearer", GitHubActions.Token);
    
                    var result = await Http.SendAsync(requestMessage);
                    if (result.IsSuccessStatusCode)
                    {
                        // Set url to .zip asset, add latest package version
                        var item = VRCPackageManifest.FromJson(await result.Content.ReadAsStringAsync());
                        item.url = zipAsset.BrowserDownloadUrl;
                        packages.Add(item);
                    }
                    else
                    {
                        Serilog.Log.Error($"Could not download manifest from {manifestAsset.Url}");
                    }
                }
            }
            
            var repoList = new VRCRepoList(packages)
            {
                author = "VRChat",
                name = "Test List",
                url = "https://TBD"
            };

            repoList.Save(ListPublishDirectory/"repolist.json");
        })
        .Produces(ListPublishDirectory/"repolist.json")
    ;

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
