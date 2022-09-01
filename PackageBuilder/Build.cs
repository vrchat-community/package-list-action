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
    AutoGenerate = false,
    InvokedTargets = new[] { nameof(BuildRepoListing) })]
class Build : NukeBuild
{
    public static int Main () => Execute<Build>(x => x.BuildRepoListing);

    [Parameter("Configuration to build - Default is 'Debug' (local) or 'Release' (server)")]
    readonly Configuration Configuration = IsLocalBuild ? Configuration.Debug : Configuration.Release;
    
    GitHubActions GitHubActions => GitHubActions.Instance;

    const string PackageManifestFilename = "package.json";
    const string PackageVersionProperty = "version";
    string CurrentPackageVersion;
    const string VRCAgent = "VCCBootstrap/1.0";

    [Parameter("Name of Listing Author")]
    readonly string ListAuthorName = "VRChat";
    [Parameter("Name of Listing")]
    readonly string ListName = "Example Listing";
    [Parameter("URL to published Listing")] // Todo: generate this automatically from repo info
    readonly string ListURL = "https://momo-the-monster.github.io/package-list-action/index.json";
    [Parameter("Directory to save index into")]
    AbsolutePath ListPublishDirectory = RootDirectory / "docs";
    
    [Parameter("PackageName")]
    private string CurrentPackageName = "com.vrchat.demo-template";

    [Parameter("Path to Target Package")] private string TargetPackagePath => RootDirectory / "Packages"  / CurrentPackageName;
    
    Target ConfigurePackageVersion => _ => _
        .Executes(() =>
        {
            var jManifest = JObject.Parse(GetManifestContents());
            CurrentPackageVersion = jManifest.Value<string>(PackageVersionProperty);
            if (string.IsNullOrWhiteSpace(CurrentPackageVersion))
            {
                throw new Exception($"Could not find Package Version in manifest");
            }
            Serilog.Log.Information($"Found version {CurrentPackageVersion}");
        });

    private string GetManifestContents()
    {
        var manifestFile = (AbsolutePath)TargetPackagePath / PackageManifestFilename;
        return File.ReadAllText(manifestFile);
    }
    
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
                ReleaseAsset manifestAsset =
                    release.Assets.First(asset => asset.Name.CompareTo(PackageManifestFilename) == 0);
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
                author = ListAuthorName,
                name = ListName,
                url = ListURL
            };

            string savePath = ListPublishDirectory / "index.json";

            FileSystemTasks.EnsureExistingParentDirectory(savePath);
            repoList.Save(savePath);
        })
        .Triggers(RebuildHomePage);

    Target RebuildHomePage => _ => _
        .Executes(() =>
        {
            var indexPath = ListPublishDirectory / "index.html";
            string indexTemplateContent = File.ReadAllText(indexPath);
            var manifest = VRCPackageManifest.FromJson(GetManifestContents());
            var rendered = Scriban.Template.Parse(indexTemplateContent).Render(new {manifest}, member => member.Name);
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
