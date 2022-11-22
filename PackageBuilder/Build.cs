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

        [Parameter("Configuration to build - Default is 'Debug' (local) or 'Release' (server)")]
        readonly Configuration Configuration = IsLocalBuild ? Configuration.Debug : Configuration.Release;

        GitHubActions GitHubActions => GitHubActions.Instance;

        const string PackageManifestFilename = "package.json";
        const string WebPageIndexFilename = "index.html";
        string CurrentPackageVersion;
        const string VRCAgent = "VCCBootstrap/1.0";

        [Parameter("Directory to save index into")] AbsolutePath ListPublishDirectory = RootDirectory / "docs";

        [Parameter("PackageName")] private string CurrentPackageName = "com.vrchat.demo-template";

        // assumes that "template-package" repo is checked out in sibling dir to this repo, can be overridden
        [Parameter("Path to Target Package")]
        private AbsolutePath LocalTestPackagesPath => RootDirectory.Parent / "template-package" / "Packages";

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

        #region Local Package Methods

        private IReadOnlyList<Release> GetLocalReleases(string repoName)
        {
            return new List<Release>() { GetLocalRelease(repoName) };
        }

        private Release GetLocalRelease(string packageName)
        {
            // Fills in most fields with the name of the field, we only need the URL for our purposes
            return new Release("url", "htmlUrl", "assetsUrl", "uploadUrl", 0,
                "nodeId", "tagName", "targetCommitish", "name", "body",
                false, false, DateTimeOffset.Now, DateTimeOffset.Now, new Octokit.Author(),
                "tarballUrl", "zipballUrl",
                new[]
                {
                    GetLocalReleaseAsset(LocalTestPackagesPath / packageName, "package.json"),
                    GetLocalReleaseAsset(LocalTestPackagesPath / packageName, "package.zip"),
                    GetLocalReleaseAsset(LocalTestPackagesPath / packageName, "package.unitypackage")
                });
        }

        private ReleaseAsset GetLocalReleaseAsset(string path, string filename)
        {
            // Fills in most fields with the name of the field, we only need the URL for our purposes
            return new ReleaseAsset(Path.Combine(path, filename), 0, "nodeId", filename, "label", "state",
                "contentType", 0, 0, DateTimeOffset.Now, DateTimeOffset.Now,
                $"https://local-test-wont-work/{filename}", new Octokit.Author());
        }

        #endregion

        #region Methods wrapped for GitHub / Local Parity

        private async Task<Release> GetLatestRelease(string repoName)
        {
            if (IsServerBuild)
            {
                return await Client.Repository.Release.GetLatest(GitHubActions.RepositoryOwner, repoName);
            }
            else
            {
                return GetLocalRelease(repoName); // assumes we just have a single release available locally, for now.
            }
        }

        private string GetRepoName()
        {
            return IsServerBuild
                ? GitHubActions.Repository.Replace($"{GitHubActions.RepositoryOwner}/", "")
                : CurrentPackageName;
        }

        private string GetRepoOwner()
        {
            return IsServerBuild ? GitHubActions.RepositoryOwner : "LocalTestOwner";
        }

        // On GitHub, we're running in the target package's repo. Locally, we run in the action dir.
        AbsolutePath ListSourceDirectory =>
            IsServerBuild ? ListPublishDirectory : LocalTestPackagesPath.Parent / "Website";

        #endregion

        // Assumes single package in this type of listing, make a different one for multi-package sets
        Target BuildRepoListing => _ => _
            .Executes(async () =>
            {
                var packages = new List<IVRCPackage>();

                var repoName = GetRepoName();
                var repoOwner = GetRepoOwner();

                var releases = IsServerBuild
                    ? await Client.Repository.Release.GetAll(GitHubActions.RepositoryOwner, repoName)
                    : GetLocalReleases(repoName);

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

                var latestRelease = await GetLatestRelease(repoName);

                // Assumes we're publishing both zip and unitypackage
                var latestManifest = await GetManifestFromRelease(latestRelease);
                if (latestManifest == null)
                {
                    throw new Exception($"Could not get Manifest for release {latestRelease.Name}");
                }

                var repoList = new VRCRepoList(packages)
                {
                    author = latestManifest.author?.name ?? repoOwner,
                    name = $"{latestManifest.name} Releases",
                    url = $"https://{repoOwner}.github.io/{repoName}/index.json"
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

        async Task<string> GetAuthenticatedString(string url)
        {
            if (IsServerBuild)
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
            else
            {
                // Treat like absolute path for local files
                return File.ReadAllText(url);
            }
        }

        async Task<VRCPackageManifest> GetManifestFromRelease(Release release)
        {
            // Release must have package.json or else it will throw an exception here
            ReleaseAsset manifestAsset =
                release.Assets.First(asset => asset.Name.CompareTo(PackageManifestFilename) == 0);

            // Will log an error if it fails, stopping the automation
            return VRCPackageManifest.FromJson(await GetAuthenticatedString(manifestAsset.Url));
        }

        Target RebuildHomePage => _ => _
            .Executes(async () =>
            {
                var repoName = GetRepoName();
                var repoOwner = GetRepoOwner();
                var release = await GetLatestRelease(repoName);

                // Assumes we're publishing both zip and unitypackage
                var zipUrl = release.Assets.First(asset => asset.Name.EndsWith(".zip")).BrowserDownloadUrl;
                var unityPackageUrl = release.Assets.First(asset => asset.Name.EndsWith(".unitypackage"))
                    .BrowserDownloadUrl;
                var manifest = await GetManifestFromRelease(release);
                if (manifest == null)
                {
                    throw new Exception($"Could not get Manifest for release {release.Name}");
                }

                var indexReadPath = ListSourceDirectory / WebPageIndexFilename;
                var indexWritePath = ListPublishDirectory / WebPageIndexFilename;
                string indexTemplateContent = File.ReadAllText(indexReadPath);

                if (manifest.author == null)
                {
                    manifest.author = new VRC.PackageManagement.Core.Types.Packages.Author
                    {
                        name = repoOwner,
                        url = $"https://github.com/{repoOwner}"
                    };
                }

                var rendered = Scriban.Template.Parse(indexTemplateContent).Render(
                    new { manifest, assets = new { zip = zipUrl, unityPackage = unityPackageUrl } },
                    member => member.Name);
                File.WriteAllText(indexWritePath, rendered);
                Serilog.Log.Information($"Updated index page at {indexWritePath}");
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
}