using System;
using System.IO;
using System.Linq;
using Newtonsoft.Json.Linq;
using Nuke.Common;
using Nuke.Common.CI.GitHubActions;
using Nuke.Common.IO;
using Octokit;

[GitHubActions(
    "GHTest",
    GitHubActionsImage.UbuntuLatest,
    On = new[] { GitHubActionsTrigger.WorkflowDispatch },
    EnableGitHubToken = true,
    InvokedTargets = new[] { nameof(ConfigurePackageVersion) })]
class Build : NukeBuild
{
    public static int Main () => Execute<Build>(x => x.BuildRepoListing);

    [Parameter("Configuration to build - Default is 'Debug' (local) or 'Release' (server)")]
    readonly Configuration Configuration = IsLocalBuild ? Configuration.Debug : Configuration.Release;
    
    GitHubActions GitHubActions => GitHubActions.Instance;

    private const string PackageManifestFilename = "package.json";
    private const string PackageVersionProperty = "version";
    private string CurrentPackageVersion;

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

    Target BuildRepoListing => _ => _
        .Executes( async () =>
        {
            var repoName = GitHubActions.Repository.Replace($"{GitHubActions.RepositoryOwner}/", "");
            var result = await Client.Repository.Release.GetAll(GitHubActions.RepositoryOwner, repoName);
            foreach (var release in result)
            {
                var manifests = release.Assets.Where(asset => asset.Name.CompareTo(PackageManifestFilename) == 0)
                    .Select(asset => new { asset.Name, asset.BrowserDownloadUrl });
               
                foreach (var manifest in manifests)
                {
                    Serilog.Log.Information($"Found manifest name: {manifest.Name} url: {manifest.BrowserDownloadUrl}");
                }
            }
        });
}
