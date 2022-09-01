using System;
using System.IO;
using Newtonsoft.Json.Linq;
using Nuke.Common;
using Nuke.Common.CI.GitHubActions;
using Nuke.Common.IO;

[GitHubActions(
    "GHTest",
    GitHubActionsImage.UbuntuLatest,
    On = new[] { GitHubActionsTrigger.WorkflowDispatch },
    InvokedTargets = new[] { nameof(ConfigurePackageVersion) })]
class Build : NukeBuild
{
    public static int Main () => Execute<Build>(x => x.ConfigurePackageVersion);

    [Parameter("Configuration to build - Default is 'Debug' (local) or 'Release' (server)")]
    readonly Configuration Configuration = IsLocalBuild ? Configuration.Debug : Configuration.Release;

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
}
