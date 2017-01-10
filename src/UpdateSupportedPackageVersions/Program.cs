using System;
using System.Linq;
using System.IO;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading.Tasks;
using NuGet.Protocol.Core.Types;
using NuGet.Protocol;
using NuGet.Configuration;
using NuGet.Common;
using System.Threading;
using CsvHelper;
using NuGet.Versioning;
using MoreLinq;
using System.Text;
using Newtonsoft.Json.Linq;

namespace UpdateSupportedPackageVersions
{
    class Program
    {
        static void Main(string[] args)
        {
            MainAsync(args).GetAwaiter().GetResult();
        }

        static async Task MainAsync(string[] args)
        {
            var seedPackageIds = new List<string>()
            {
                "Microsoft.NETCore.App",
                "Microsoft.AspNetCore",
                "Microsoft.AspNetCore.Mvc",
                "Microsoft.AspNetCore.Identity.EntityFrameworkCore",
                "Microsoft.AspNetCore.Authentication.OpenIdConnect",
                "Microsoft.EntityFrameworkCore.SqlServer"
            };

            // Get the LTS and Current versions for the seed packages based on semantic versioning
            var packages = CreateSeedPackages(seedPackageIds);
            await GetVersionsByConventionAsync(packages);

            // Restore the package graphs for LTS and Current based on the seed packages
            GeneratePackageGraphs(packages);

            // Persist the package versions
            var outputPath = "dotnet_supported_package_versions.csv";
            SavePackages(packages.Values, outputPath);
        }

        static IDictionary<string, Package> CreateSeedPackages(IEnumerable<string> seedPackageIds)
        {
            var packages = new Dictionary<string, Package>();
            foreach (var seedPackageId in seedPackageIds)
            {
                packages[seedPackageId] = new Package() { Id = seedPackageId };
            }
            return packages;
        }

        static async Task GetVersionsByConventionAsync(IDictionary<string, Package> packages)
        {
            var repo = Repository.Factory.GetCoreV3(NuGetConstants.V3FeedUrl);
            var resource = await repo.GetResourceAsync<MetadataResource>();

            var versionRequests = packages.Values.Select(package =>
            {
                Console.WriteLine($"Get versions for {package.Id}");
                return resource.GetVersions(
                    packageId: package.Id,
                    includePrerelease: false,
                    includeUnlisted: false,
                    log: NullLogger.Instance,
                    token: CancellationToken.None)
                    .ContinueWith(task =>
                    {
                        if (task.IsFaulted)
                        {
                            Console.WriteLine($"ERROR: Failed to get versions for {package.Id}: {task.Exception.Message}");
                        }
                        else
                        {
                            if (task.Result.Count() > 0)
                            {
                                package.LtsVersion = GetLtsVersion(task.Result);
                                package.CurrentVersion = GetCurrentVersion(task.Result);
                                Console.WriteLine($"{package.Id} : (LTS) {package.LtsVersion}, (Current) {package.CurrentVersion}");
                            }
                            else
                            {
                                Console.WriteLine($"WARNING: {package.Id} has no stable versions");
                            }
                        }
                    });
            });

            foreach (var batch in versionRequests.Batch(20))
            {
                await Task.WhenAll(batch);
            }
        }

        private static string GetCurrentVersion(IEnumerable<NuGetVersion> versions)
        {
            return versions
                .OrderBy(version => version.Version.Major)
                .ThenBy(version => version.Version.Minor)
                .ThenBy(version => version.Version.Revision)
                .Last()
                .ToString();
        }

        static string GetLtsVersion(IEnumerable<NuGetVersion> versions)
        {
            var lts = versions
                .Where(version => version.Version.Minor == 0)
                .OrderBy(version => version.Version.Major)
                .ThenBy(version => version.Version.Revision)
                .LastOrDefault();
            return lts != null ? lts.ToString() : GetCurrentVersion(versions);
        }

        static void GeneratePackageGraphs(IDictionary<string, Package> seedPackages)
        {
            var ltsCsroj = GenerateLtsCsproj(seedPackages.Values);
            var currentCsproj = GenerateCurrentCsproj(seedPackages.Values);

            GeneratePackageGraph(seedPackages, ltsCsroj, (package, version) => package.LtsVersion = version);
            GeneratePackageGraph(seedPackages, currentCsproj, (package, version) => package.CurrentVersion = version);
        }

        static void GeneratePackageGraph(IDictionary<string, Package> packages, string csproj, Action<Package, string> versionSetter)
        {
            var tempPath = Path.GetTempPath();
            var graphPath = Path.Combine(tempPath, Guid.NewGuid().ToString());
            var graphDir = Directory.CreateDirectory(graphPath);
            var csprojPath = Path.Combine(graphPath, "temp.csproj");
            File.WriteAllText(csprojPath, csproj);
            Process restoreProcess = Process.Start("dotnet", $"restore {csprojPath}");
            restoreProcess.WaitForExit();
            var packageGraphPath = Path.Combine(graphPath, "obj", "project.assets.json");
            if (restoreProcess.ExitCode > 0 || !File.Exists(packageGraphPath))
            {
                throw new InvalidOperationException("Failed to generate package graph: NuGet restore failed.");
            }

            var packageGraphText = File.ReadAllText(packageGraphPath);

            Directory.Delete(graphPath, true);

            var packageGraphJson = JObject.Parse(packageGraphText);
            var libraries = packageGraphJson["libraries"].Children().Select(lib => ((JProperty)lib).Name);

            foreach (var lib in libraries)
            {
                var packageIdAndVersion = lib.Split('/');
                var packageId = packageIdAndVersion.First();
                var version = packageIdAndVersion.Last();
                Package package;
                if (packages.TryGetValue(packageId, out package))
                {
                    versionSetter(package, version);
                }
                else
                {
                    package = new Package() { Id = packageId };
                    versionSetter(package, version);
                    packages[packageId] = package;
                }
            }
        }

        static string GenerateLtsCsproj(IEnumerable<Package> packages)
        {
            return GenerateCsproj(packages, package => package.LtsVersion);
        }

        static string GenerateCurrentCsproj(IEnumerable<Package> packages)
        {
            return GenerateCsproj(packages, package => package.CurrentVersion);
        }

        static string GenerateCsproj(IEnumerable<Package> packages, Func<Package, string> versionSelector)
        {
            var targetFramework = GetTargetFramework(packages, versionSelector);

            var csprojBuilder = new StringBuilder();
            var projectStart =
$@"<Project Sdk=""Microsoft.NET.Sdk"">
  <PropertyGroup>
    <OutputType>Exe</OutputType>
    <TargetFramework>{targetFramework}</TargetFramework>
  </PropertyGroup>
  <ItemGroup>
";
            csprojBuilder.Append(projectStart);

            foreach(var package in packages)
            {
                csprojBuilder.Append($"    <PackageReference Include=\"{package.Id}\" Version=\"{versionSelector(package)}\" />\n");
            }

            var projectEnd =
@"  </ItemGroup>
</Project>
";
            csprojBuilder.Append(projectEnd);

            return csprojBuilder.ToString();
        }

        static string GetTargetFramework(IEnumerable<Package> packages, Func<Package, string> versionSelector)
        {
            var netCoreAppPackage = packages.First(package => package.Id == "Microsoft.NETCore.App");
            var version = Version.Parse(versionSelector(netCoreAppPackage));
            return $"netcoreapp{version.Major}.{version.Minor}";
        }

        static void SavePackages(IEnumerable<Package> packages, string path)
        {
            using (CsvWriter writer = new CsvWriter(File.CreateText(path)))
            {
                writer.WriteRecords(packages);
            }
        }
    }
}