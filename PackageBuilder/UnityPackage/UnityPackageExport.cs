using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using GlobExpressions;
// using Microsoft.Extensions.FileSystemGlobbing;
using Nuke.Common;
using Nuke.Common.IO;
using Serilog;
using UnityPackageExporter.Dependency;
using UnityPackageExporter.Package;

namespace VRC.PackageManagement.Automation;

// Most of this code is adapted from https://github.com/Lachee/Unity-Package-Exporter, which is licensed under the MIT license.
partial class Build
{

    [Parameter("Unity Project Directory")]
    AbsolutePath unityPackageExportSource;
    
    [Parameter("Output .unitypackage file")]
    AbsolutePath unityPackageExportOutput;
    
    [Parameter("Adds an asset to the pack. Supports glob matching.", Separator = " ")]
    string[] assetPattern = new string[]{"**.*"};

    [Parameter("Adds an asset to the pack. Supports glob matching.", Separator = " ")]
    string[] excludePattern = new string[] { "Library/**.*"};
    
    [Parameter("Skips dependency analysis. Disabling this feature may result in missing assets in your packages.")]
    bool skipDep = false;
    
    [Parameter("Sets the root directory for the assets. Used in dependency analysis to only check files that could be potentially included.")]
    string assetRoot = "Assets";
    
    static void PrintDirectoryTree(string directoryPath, string indent = "")
    {
        DirectoryInfo directoryInfo = new DirectoryInfo(directoryPath);

        if (!directoryInfo.Exists)
        {
            Log.Information($"Directory \"{directoryPath}\" not found.");
            return;
        }

        Log.Information($"{indent}+ {directoryInfo.Name}");

        foreach (DirectoryInfo subDirectory in directoryInfo.GetDirectories())
        {
            PrintDirectoryTree(subDirectory.FullName, indent + "  ");
        }

        foreach (FileInfo file in directoryInfo.GetFiles())
        {
            Log.Information($"{indent}  - {file.Name}");
        }
    }

    Target BuildUnityPackage => _ => _
        .Requires(() => unityPackageExportSource)
        .Requires(() => unityPackageExportOutput)
        .Executes( async () =>
        {
            Log.Information($"Packing {unityPackageExportSource}");
            
            PrintDirectoryTree(unityPackageExportSource/"Packages"/"com.vrchat.ClientSim");
            
            // Make the output file (touch it) so we can exclude
            await File.WriteAllBytesAsync(unityPackageExportOutput, new byte[0]);

            Stopwatch timer = Stopwatch.StartNew();
            using DependencyAnalyser analyser = !skipDep ? await DependencyAnalyser.CreateAsync(unityPackageExportSource / assetRoot, excludePattern) : null;
            using Packer packer = new Packer(unityPackageExportSource, unityPackageExportOutput);

            // Use Nuke's globbing to match the assets
            
            // Get all files in the directory and its subdirectories
            var rootPath = unityPackageExportSource;
            var allFiles = Directory.GetFiles(rootPath, "*.*", SearchOption.AllDirectories);

            List<string> matchedAssets = new List<string>();
            // Iterate through the files and apply the include and exclude patterns
            Log.Information("Matched files:");
            foreach (var filePath in allFiles)
            {
                // Get the relative path to the root directory
                string relativePath = Path.GetRelativePath(rootPath, filePath);

                // Check if the file matches any of the include patterns and does not match any of the exclude patterns
                bool isIncluded = AnyPatternMatches(relativePath, assetPattern);
                bool isExcluded = AnyPatternMatches(relativePath, excludePattern);

                if (isIncluded && !isExcluded)
                {
                    Log.Information($"Matched file: {relativePath}");
                    matchedAssets.Add(relativePath);
                }
            }
            
            // Match all the assets we need
            // Matcher assetMatcher = new Matcher();
            // assetMatcher.AddIncludePatterns(assetPattern);
            // foreach (string s in assetPattern)
            // {
            //     Log.Information($"Added includePattern {s}");
            // }
            // // assetMatcher.AddExcludePatterns(excludePattern);
            // // foreach (string s in excludePattern)
            // // {
            // //     Log.Information($"Added excludePattern {s}");
            // // }
            // assetMatcher.AddExclude(unityPackageExportOutput);
            // Log.Information($"Added exclude {unityPackageExportOutput}");
            //
            // var matchedAssets = assetMatcher.GetResultsInFullPath(unityPackageExportSource);
            //
            Assert.True(matchedAssets.Count() > 0, "No assets matched the pattern. Please check your pattern and try again.");
            
            Log.Information($"Found {matchedAssets.Count()} matched assets");
            
            
            await packer.AddAssetsAsync(matchedAssets);

            if (!skipDep)
            {
                var results = await analyser.FindDependenciesAsync(matchedAssets);
                await packer.AddAssetsAsync(results);
            }

            // Finally flush and tell them we done
            Log.Information($"Finished Packing in {timer.ElapsedMilliseconds}ms to {packer.OutputPath}");
            await packer.FlushAsync();
        });
    
    // Helper method to check if any pattern in the given array matches the provided path
    static bool AnyPatternMatches(string path, string[] patterns)
    {
        foreach (var pattern in patterns)
        {
            if (Glob.IsMatch(path, pattern))
            {
                return true;
            }
        }
        return false;
    }
}