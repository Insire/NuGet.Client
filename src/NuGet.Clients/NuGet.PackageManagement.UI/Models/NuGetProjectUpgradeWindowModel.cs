// Copyright (c) .NET Foundation. All rights reserved.
// Licensed under the Apache License, Version 2.0. See License.txt in the project root for license information.

using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using NuGet.Frameworks;
using NuGet.Packaging;
using NuGet.Packaging.Core;
using NuGet.ProjectManagement;
using NuGet.Versioning;

namespace NuGet.PackageManagement.UI
{
    public class NuGetProjectUpgradeWindowModel : INotifyPropertyChanged
    {
        private IEnumerable<NuGetProjectUpgradeDependencyItem> _dependencyPackages;
        private IEnumerable<NuGetProjectUpgradeDependencyItem> _includedCollapsedPackages;
        private IEnumerable<NuGetProjectUpgradeDependencyItem> _upgradeDependencyItems;
        private string _projectName;
        private IList<string> _warnings;
        private IList<string> _errors;
        private IList<PackageIdentity> _notFoundPackages;

        public NuGetProjectUpgradeWindowModel(NuGetProject project, IList<PackageDependencyInfo> packageDependencyInfos,
            bool collapseDependencies)
        {
            PackageDependencyInfos = packageDependencyInfos;
            Project = project;
        }

        public event PropertyChangedEventHandler PropertyChanged;

        public NuGetProject Project { get; }

        public string Title => string.Format(CultureInfo.CurrentCulture, Resources.WindowTitle_NuGetMigrator, ProjectName);

        private string ProjectName
        {
            get
            {
                if (string.IsNullOrEmpty(_projectName))
                {
                    _projectName = NuGetProject.GetUniqueNameOrName(Project);
                    return _projectName;
                }
                else
                {
                    return _projectName;
                }
            }
            set
            {
                _projectName = value;
            }
        }

        public IList<PackageDependencyInfo> PackageDependencyInfos { get; }

        public IEnumerable<string> Warnings
        {
            get
            {
                if (_warnings == null)
                {
                    InitPackageUpgradeIssues();
                }
                return _warnings;
            }
        }

        public IEnumerable<string> Errors
        {
            get
            {
                if (_errors == null)
                {
                    InitPackageUpgradeIssues();
                }
                return _errors;
            }
        }

        public IList<PackageIdentity> NotFoundPackages
        {
            get
            {
                if (_notFoundPackages == null)
                {
                    InitPackageUpgradeIssues();
                }
                return _notFoundPackages;
            }
        }

        public bool HasIssues => Errors.Any() || Warnings.Any();

        public IEnumerable<NuGetProjectUpgradeDependencyItem> UpgradeDependencyItems
            => _upgradeDependencyItems ?? (_upgradeDependencyItems = GetUpgradeDependencyItems());

        public IEnumerable<NuGetProjectUpgradeDependencyItem> DirectDependencies => IncludedCollapsedPackages;

        public IEnumerable<NuGetProjectUpgradeDependencyItem> TransitiveDependencies => DependencyPackages;

        private IEnumerable<NuGetProjectUpgradeDependencyItem> DependencyPackages => _dependencyPackages ?? (_dependencyPackages = GetDependencyPackages());

        private IEnumerable<NuGetProjectUpgradeDependencyItem> AllPackages => UpgradeDependencyItems;

        private IEnumerable<NuGetProjectUpgradeDependencyItem> IncludedCollapsedPackages => _includedCollapsedPackages ?? (_includedCollapsedPackages = GetIncludedCollapsedPackages());

        private IEnumerable<NuGetProjectUpgradeDependencyItem> GetDependencyPackages()
        {
            return UpgradeDependencyItems.Where(d => d.DependingPackages.Any());
        }

        private IEnumerable<NuGetProjectUpgradeDependencyItem> GetIncludedCollapsedPackages()
        {
            return UpgradeDependencyItems
                .Where(upgradeDependencyItem => !upgradeDependencyItem.DependingPackages.Any());
        }

        private void InitPackageUpgradeIssues()
        {
            _warnings = new List<string>();
            _errors = new List<string>();
            _notFoundPackages = new List<PackageIdentity>();

            var msBuildNuGetProject = (MSBuildNuGetProject)Project;
            var framework = msBuildNuGetProject.ProjectSystem.TargetFramework;
            var folderNuGetProject = msBuildNuGetProject.FolderNuGetProject;

            foreach (var package in PackageDependencyInfos)
            {
                // We create a new PackageIdentity here, otherwise we would be passing in a PackageDependencyInfo
                // which includes dependencies in its ToString().
                InitPackageUpgradeIssues(folderNuGetProject, new PackageIdentity(package.Id, package.Version), framework);
            }
        }

        private void InitPackageUpgradeIssues(FolderNuGetProject folderNuGetProject, PackageIdentity packageIdentity, NuGetFramework framework)
        {
            // Confirm package exists
            var packagePath = folderNuGetProject.GetInstalledPackageFilePath(packageIdentity);
            if (string.IsNullOrEmpty(packagePath))
            {
                _errors.Add(string.Format(CultureInfo.CurrentCulture, Resources.NuGetUpgradeError_CannotFindPackage, packageIdentity));
                _notFoundPackages.Add(packageIdentity);
            }
            else
            {
                var reader = new PackageArchiveReader(packagePath);

                // Check if it has content files
                var contentFilesGroup = MSBuildNuGetProjectSystemUtility.GetMostCompatibleGroup(framework,
                    reader.GetContentItems());
                if (MSBuildNuGetProjectSystemUtility.IsValid(contentFilesGroup) && contentFilesGroup.Items.Any())
                {
                    _warnings.Add(string.Format(CultureInfo.CurrentCulture, Resources.NuGetUpgradeWarning_HasContentFiles, packageIdentity));
                }

                // Check if it has an install.ps1 file
                var toolItemsGroup = MSBuildNuGetProjectSystemUtility.GetMostCompatibleGroup(framework,
                    reader.GetToolItems());
                toolItemsGroup = MSBuildNuGetProjectSystemUtility.Normalize(toolItemsGroup);
                var isValid = MSBuildNuGetProjectSystemUtility.IsValid(toolItemsGroup);
                var hasInstall = isValid && toolItemsGroup.Items.Any(p => p.EndsWith(Path.DirectorySeparatorChar + PowerShellScripts.Install, StringComparison.OrdinalIgnoreCase));
                if (hasInstall)
                {
                    _warnings.Add(string.Format(CultureInfo.CurrentCulture, Resources.NuGetUpgradeWarning_HasInstallScript, packageIdentity));
                }
            }
        }

        private IEnumerable<NuGetProjectUpgradeDependencyItem> GetUpgradeDependencyItems()
        {
            var upgradeDependencyItems = PackageDependencyInfos
                .Select(p => new NuGetProjectUpgradeDependencyItem(new PackageIdentity(p.Id, p.Version))).ToList();

            foreach (var packageDependencyInfo in PackageDependencyInfos)
            {
                foreach (var dependency in packageDependencyInfo.Dependencies)
                {
                    var matchingDependencyItem = upgradeDependencyItems
                        .FirstOrDefault(d => (d.Package.Id == dependency.Id) && (d.Package.Version == dependency.VersionRange.MinVersion));
                    matchingDependencyItem?.DependingPackages.Add(new PackageIdentity(packageDependencyInfo.Id, packageDependencyInfo.Version));
                }
            }

            return upgradeDependencyItems;
        }

        protected void OnPropertyChanged([CallerMemberName] string propertyName = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }

#if DEBUG
        public NuGetProjectUpgradeWindowModel()
        {
            _upgradeDependencyItems = DesignTimeUpgradeDependencyItems;
            _projectName = "TestProject";
            _errors = new List<string>
            {
                string.Format(CultureInfo.CurrentCulture, Resources.NuGetUpgradeError_CannotFindPackage, PackageTwo)
            };
            _warnings = new List<string>
            {
                string.Format(CultureInfo.CurrentCulture, Resources.NuGetUpgradeWarning_HasContentFiles, PackageOne),
                string.Format(CultureInfo.CurrentCulture, Resources.NuGetUpgradeWarning_HasInstallScript, PackageOne),
                string.Format(CultureInfo.CurrentCulture, Resources.NuGetUpgradeWarning_HasInstallScript, PackageThree)
            };
        }

        private static readonly PackageIdentity PackageOne = new PackageIdentity("Test.Package.One", new NuGetVersion("1.2.3"));
        private static readonly PackageIdentity PackageTwo = new PackageIdentity("Test.Package.Two", new NuGetVersion("4.5.6"));
        private static readonly PackageIdentity PackageThree = new PackageIdentity("Test.Package.Three", new NuGetVersion("7.8.9"));

        private static readonly IEnumerable<NuGetProjectUpgradeDependencyItem> DesignTimeUpgradeDependencyItems = new List<NuGetProjectUpgradeDependencyItem>
        {
            new NuGetProjectUpgradeDependencyItem(PackageOne),
            new NuGetProjectUpgradeDependencyItem(PackageTwo, new List<PackageIdentity> {PackageOne}),
            new NuGetProjectUpgradeDependencyItem(PackageThree, new List<PackageIdentity> {PackageOne, PackageTwo}),
            new NuGetProjectUpgradeDependencyItem(PackageThree, new List<PackageIdentity> {PackageOne, PackageTwo}),
            new NuGetProjectUpgradeDependencyItem(PackageThree, new List<PackageIdentity> {PackageOne, PackageTwo}),
            new NuGetProjectUpgradeDependencyItem(PackageThree, new List<PackageIdentity> {PackageOne, PackageTwo}),
            new NuGetProjectUpgradeDependencyItem(PackageThree, new List<PackageIdentity> {PackageOne, PackageTwo}),
            new NuGetProjectUpgradeDependencyItem(PackageThree, new List<PackageIdentity> {PackageOne, PackageTwo}),
            new NuGetProjectUpgradeDependencyItem(PackageThree, new List<PackageIdentity> {PackageOne, PackageTwo}),
            new NuGetProjectUpgradeDependencyItem(PackageThree, new List<PackageIdentity> {PackageOne, PackageTwo}),
            new NuGetProjectUpgradeDependencyItem(PackageThree, new List<PackageIdentity> {PackageOne, PackageTwo}),
            new NuGetProjectUpgradeDependencyItem(PackageThree, new List<PackageIdentity> {PackageOne, PackageTwo}),
            new NuGetProjectUpgradeDependencyItem(PackageThree, new List<PackageIdentity> {PackageOne, PackageTwo})

        };
#endif
    }
}