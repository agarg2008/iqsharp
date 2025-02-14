﻿// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License.

using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.Quantum.IQSharp.Common;

using NuGet.Configuration;
using NuGet.Frameworks;
using NuGet.Packaging;
using NuGet.Packaging.Core;
using NuGet.Packaging.Signing;
using NuGet.Protocol;
using NuGet.Protocol.Core.Types;
using NuGet.Resolver;
using NuGet.Versioning;

namespace Microsoft.Quantum.IQSharp
{
    /// <summary>
    /// This class takes care of managing Nuget packages: it provides support
    /// for adding packages by finding all its dependencies and downloading them if necessary.
    /// </summary>
    public class NugetPackages
    {
        // The string we use to delimit the version from the package id.
        public static readonly string PACKAGE_VERSION_DELIMITER = "::";

        // The list of settings for this class. It extends Workspace.Settings so it can get things
        // list root and cache folder. 
        public class Settings : Workspace.Settings
        {
            public string[] DefaultPackageVersions { get; set; }
        }

        // The framework used to find packages.
        // We only use netcoreapp2.1, as IQSharp is built against this moniker.
        public static NuGetFramework NETCOREAPP2_1 = NuGetFramework.ParseFolder("netcoreapp2.1");

        // Nuget's logger.
        public NuGetLogger Logger { get; }

        // List of Packages already installed.
        public IEnumerable<PackageIdentity> Items { get; private set; }

        // List of Assemblies from current Packages..
        public IEnumerable<AssemblyInfo> Assemblies { get; private set; }

        // List of Nuget repositories. This is populated from NugetSettings.
        // It can't be cached otherwise we can't detect changes to repositores.
        public IEnumerable<SourceRepository> Repositories 
        {
            get
            {
                // global packages (i.e. ~/.nuget/packages)
                var globalPackagesFolder = SettingsUtility.GetGlobalPackagesFolder(NugetSettings);
                Logger?.LogDebug($"Using nuget global packages cache from: {globalPackagesFolder}");
                yield return Repository.CreateSource(Repository.Provider.GetCoreV3(), globalPackagesFolder);

                // fallback folders (i.e. /dotnet/sdk/NugetFallbackFolder)
                foreach (var folder in SettingsUtility.GetFallbackPackageFolders(NugetSettings))
                {
                    Logger?.LogDebug($"Using nuget fallback feed at: {folder}");
                    yield return Repository.CreateSource(Repository.Provider.GetCoreV3(), folder);
                }

                // Other sources as defined in Nuget.config:
                foreach(var source in new SourceRepositoryProvider(NugetSettings, Repository.Provider.GetCoreV3()).GetRepositories())
                {
                    Logger?.LogDebug($"Using nuget feed: {source.PackageSource.Name}");
                    yield return source;
                }
            }
        }

        // Nuget's way to find path of local packages
        public FindLocalPackagesResource LocalPackagesFinder =>
            this.Repositories.First().GetResource<FindLocalPackagesResource>();

        // Nuget's global settings.
        public ISettings NugetSettings { get; }

        // Keeps track of what package version to use for certain packages specified in the packageVersion.json.
        // This way we can better control what the correct version of Microsoft.Quantum
        // packages to use, since all of them need to be in-sync.
        public IReadOnlyDictionary<string, NuGetVersion> DefaultVersions { get; }

        public NugetPackages(IOptions<Settings> config, Microsoft.Extensions.Logging.ILogger logger)
        {
            this.Logger = new NuGetLogger(logger);

            this.NugetSettings = NuGet.Configuration.Settings.LoadDefaultSettings(root: config?.Value.Workspace);
            this.DefaultVersions = InitDefaultVersions(config?.Value.DefaultPackageVersions);
            this.Items = Enumerable.Empty<PackageIdentity>();
            this.Assemblies = Enumerable.Empty<AssemblyInfo>();
        }

        /// <summary>
        /// Inits the list of default packages versions from the configuration.
        /// Each element on the config array is expected to be a package; all packages are expected to have a version
        /// thus the name is checked for package delimiter (::)
        /// </summary>
        public IReadOnlyDictionary<string, NuGetVersion> InitDefaultVersions(string[] config)
        {
            var versions = new Dictionary<string, NuGetVersion>(StringComparer.OrdinalIgnoreCase);

            if (config != null)
            {
                foreach (var info in config)
                {
                    var index = info.IndexOf(PACKAGE_VERSION_DELIMITER);
                    Debug.Assert(index > 0);

                    if (index > 0)
                    {
                        var pkg = ParsePackageId(info).Result;
                        versions[pkg.Id] = pkg.Version;
                    }
                    else
                    {
                        Logger.LogWarning($"Invalid package version '{info}'. Expecting package in format 'pkgId::pkgVersion'");
                    }
                }
            }

            return versions;
        }

        /// <summary>
        /// Given a package name, generates a PackageId.
        /// The package name can include the version delimited by double colon (::). If not,
        /// it will call GetLatestVersion to get the latest version found across all sources.
        /// </summary>
        /// <exception cref="System.ArgumentNullException">
        ///     If <paramref name="package" /> is <c>null</c>.
        /// </exception>
        public async Task<PackageIdentity> ParsePackageId(string package)
        {
            package = package?.Trim() ?? throw new ArgumentNullException(nameof(package));

            var index = package.IndexOf(PACKAGE_VERSION_DELIMITER);

            if (index > 0)
            {
                var name = package.Substring(0, index);
                var version = package.Substring(index + PACKAGE_VERSION_DELIMITER.Length);
                return new PackageIdentity(name, NuGetVersion.Parse(version));
            }
            else
            {
                return new PackageIdentity(package, await GetLatestVersion(package));
            }
        }

        /// <summary>
        /// Adds a new package given the name and version as strings.
        /// </summary>
        public async Task<PackageIdentity> Add(string package)
        {
            if (string.IsNullOrWhiteSpace(package))
            {
                throw new InvalidOperationException("Please provide a name of a package.");
            }

            var pkgId = await ParsePackageId(package);
            await Add(pkgId);

            return pkgId;
        }

        /// <summary>
        /// Adds the given package.
        /// </summary>
        public async Task Add(PackageIdentity pkgId)
        {
            // Already added:
            if (Items.Contains(pkgId)) return;

            using (var sourceCacheContext = new SourceCacheContext())
            {
                var packages = await GetPackageDependencies(pkgId, sourceCacheContext);

                await DownloadPackages(sourceCacheContext, packages);

                this.Items = Items.Union(new PackageIdentity[] { pkgId }).ToArray();
                this.Assemblies = Assemblies.Union(packages.Reverse().SelectMany(GetAssemblies)).ToArray();
            }
        }

        /// <summary>
        /// Returns true if the package exists in the GlobalPackages folder.
        /// </summary>
        public bool IsInstalled(PackageIdentity pkg) => LocalPackagesFinder.Exists(pkg, Logger, CancellationToken.None);

        /// <summary>
        /// Identifies system packages, i.e., those that should be installed as part of .net.
        /// These packages will not be downloaded nor will we try to get their list of assemblies.
        /// </summary>
        public static bool IsSystemPackage(PackageIdentity pkg) =>
            pkg.Id.StartsWith("System", StringComparison.InvariantCultureIgnoreCase) || 
            pkg.Id.StartsWith("Microsoft.NET", StringComparison.InvariantCultureIgnoreCase) || 
            pkg.Id.StartsWith("NETStandard", StringComparison.InvariantCultureIgnoreCase) || 
            pkg.Id.StartsWith("Microsoft.Win32", StringComparison.InvariantCultureIgnoreCase);

        /// <summary>
        /// Downloads and extracts a package into the GlobalPackages folder.
        /// </summary>
        public async Task DownloadPackages(SourceCacheContext context, IEnumerable<SourcePackageDependencyInfo> packagesToInstall)
        {
            foreach (var pkg in packagesToInstall)
            {
                // Ignore all SDK packages:
                if (IsSystemPackage(pkg)) continue;

                if (!IsInstalled(pkg))
                {
                    var downloadResource = await pkg.Source.GetResourceAsync<DownloadResource>(CancellationToken.None);
                    var downloadResult = await downloadResource.GetDownloadResourceResultAsync(
                        pkg,
                        new PackageDownloadContext(context),
                        SettingsUtility.GetGlobalPackagesFolder(NugetSettings),
                        Logger, CancellationToken.None);

                    await GlobalPackagesFolderUtility.AddPackageAsync(
                        source: null,
                        packageIdentity: pkg,
                        packageStream: downloadResult.PackageStream,
                        globalPackagesFolder: LocalPackagesFinder.Root,
                        clientPolicyContext: ClientPolicyContext.GetClientPolicy(NugetSettings, Logger),
                        logger: Logger,
                        parentId: Guid.Empty,
                        token: CancellationToken.None);
                }
            }
        }

        /// <summary>
        /// Find the list of netstandard Assemblies (if any) for the given package.
        /// Certain System and .NET packages are ignored as the assemblies of these should
        /// be automatically included without package references.
        /// </summary>
        public IEnumerable<AssemblyInfo> GetAssemblies(PackageIdentity pkg)
        {
            // Ignore all SDK packages:
            if (pkg == null || IsSystemPackage(pkg)) return Enumerable.Empty<AssemblyInfo>();

            var pkgInfo = LocalPackagesFinder.GetPackage(pkg, Logger, CancellationToken.None);
            var packageReader = pkgInfo?.GetReader();
            var libs = packageReader?.GetLibItems();

            // If package contains no dlls:
            if (libs == null) 
            {
                Logger.LogWarning($"Could not find any dll for {pkg}");
                return Enumerable.Empty<AssemblyInfo>();
            }

            var root = Path.GetDirectoryName(pkgInfo.Path);
            string[] CheckOnFramework(NuGetFramework framework)
            {
                var frameworkReducer = new FrameworkReducer();
                var nearest = frameworkReducer.GetNearest(framework, libs.Select(x => x.TargetFramework));

                if (nearest == null) return new string[0];

                var files = libs
                    .Where(x => x.TargetFramework.Equals(nearest))
                    .SelectMany(x => x.Items)
                    .Where(n => n.EndsWith(".dll"))
                    .Select(p => Path.Combine(root, p));

                return files.ToArray();
            }

            var names = CheckOnFramework(NETCOREAPP2_1);

            Assembly LoadAssembly(string path)
            {
                try
                {
                    return Assembly.LoadFile(path);
                }
                catch (Exception e)
                {
                    Logger.LogWarning($"Unable to load assembly '{path}' ({e.Message})");
                    return null;
                }
            }
            return names
                .Select(LoadAssembly)
                .Select(AssemblyInfo.Create)
                .Where(a => a != null);
        }


        /// <summary>
        /// Finds the latest version of the package with the given id. 
        /// The id must match. It returns null if the package is not found.
        /// </summary>
        public async Task<NuGetVersion> GetLatestVersion(string package)
        {
            package = package?.Trim() ?? throw new ArgumentNullException(nameof(package));

            NuGetVersion found = null;

            if (DefaultVersions.TryGetValue(package, out found))
            {
                return found;
            }

            foreach (var repo in this.Repositories)
            {
                try
                {
                    var feed = await repo.GetResourceAsync<ListResource>();
                    if (feed == null) continue;

                    var metadatas = await feed.ListAsync(package, prerelease: false, allVersions: false, includeDelisted: false, log: Logger, token: CancellationToken.None);
                    if (metadatas == null) continue;

                    var e = metadatas.GetEnumeratorAsync();
                    while (await e.MoveNextAsync())
                    {
                        if (string.Equals(e.Current.Identity.Id, package, StringComparison.InvariantCultureIgnoreCase) && e.Current.Identity.HasVersion)
                        {
                            var current = e.Current.Identity.Version;
                            if (found == null || found < current)
                            {
                                found = current;
                            }
                        }
                    }
                }
                catch (NuGetProtocolException e)
                {
                    Logger?.LogWarning($"Repository throw exception: {e.Message}");
                }
            }

            return found;
        }

        /// <summary>
        /// Finds all the dependencies of the given package.
        /// </summary>
        public async Task<IEnumerable<SourcePackageDependencyInfo>> GetPackageDependencies(
            PackageIdentity package,
            SourceCacheContext context)
        {
            var all = new HashSet<SourcePackageDependencyInfo>(PackageIdentityComparer.Default);

            await FindDependencies(package, context, all);

            return ResolveDependencyGraph(package, all);
        }

        /// Flattens the list of dependency packages to a single list of packages to be installed.
        public IEnumerable<SourcePackageDependencyInfo> ResolveDependencyGraph(PackageIdentity pkgId, IEnumerable<SourcePackageDependencyInfo> dependencies)
        {
            // We used PackageResolver to flatten the dependency graph. This is the process Nuget uses 
            // when adding a package to a project. It takes:
            // - a list of targets, in this case the package we want to add
            // - a list of packages already installed, (i.e. the package that used to be defined in the packages.config)
            //      * in our case, the packages already added to this service
            // - a list of available packages (i.e. the list of packages in the nuget sources).
            //      * in our case, all the dependencies we already found via GetPackageDependencies
            // The resolver will then filter out the list such that only one version of each package
            //  gets installed.
            var resolverContext = new PackageResolverContext(
                dependencyBehavior: DependencyBehavior.Lowest,
                targetIds: new[] { pkgId.Id },
                requiredPackageIds: Enumerable.Empty<string>(),
                packagesConfig: Items.Select(p => new PackageReference(p, NETCOREAPP2_1, true)),
                preferredVersions: Enumerable.Empty<PackageIdentity>(),
                availablePackages: dependencies,
                packageSources: Repositories.Select(s => s.PackageSource),
                log: Logger);

            var resolver = new PackageResolver();

            return resolver.Resolve(resolverContext, CancellationToken.None)
                    .Select(p => dependencies.Single(x => PackageIdentityComparer.Default.Equals(x, p)));
        }

        /// <summary>
        /// Recursively finds all the dependencies of the given package and returns in the 
        /// dependencies.
        /// </summary>
        internal async Task FindDependencies(
            PackageIdentity package,
            SourceCacheContext context,
            ISet<SourcePackageDependencyInfo> dependencies)
        {
            if (dependencies.Contains(package)) return;

            foreach (var repo in this.Repositories)
            {
                try
                {
                    var dependencyInfoResource = await repo.GetResourceAsync<DependencyInfoResource>();
                    if (dependencyInfoResource == null) continue;

                    var dependencyInfo = await dependencyInfoResource.ResolvePackage(package, NETCOREAPP2_1, context, this.Logger, CancellationToken.None);
                    if (dependencyInfo == null) continue;

                    dependencies.Add(dependencyInfo);

                    foreach (var dependency in dependencyInfo.Dependencies)
                    {
                        await FindDependencies(
                            new PackageIdentity(dependency.Id, dependency.VersionRange.MinVersion),
                            context, dependencies);
                    }

                    // dependencies found, no need to look into next repo
                    break;
                }
                catch (NuGetProtocolException) { }
            }
        }

        /// <summary>
        ///  Helper method that creates a new instance with default dependencies.
        ///  Used mainly for testing.
        /// </summary>
        internal static NugetPackages Create()
        {
            var logger = new LoggerFactory().CreateLogger<NugetPackages>();
            return new NugetPackages(null, logger);
        }
    }
}
