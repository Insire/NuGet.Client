// Copyright (c) .NET Foundation. All rights reserved.
// Licensed under the Apache License, Version 2.0. See License.txt in the project root for license information.

using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using NuGet.Common;
using NuGet.Frameworks;
using NuGet.Packaging;
using NuGet.Packaging.Core;
using NuGet.Packaging.Rules;
using NuGet.ProjectManagement;
using NuGet.Versioning;

namespace NuGet.PackageManagement.UI
{
    public class NuGetProjectUpgradeWindowModel : INotifyPropertyChanged
    {
        private IEnumerable<NuGetProjectUpgradeDependencyItem> _dependencyPackages;
        private IEnumerable<NuGetProjectUpgradeDependencyItem> _includedCollapsedPackages;
        private ObservableCollection<NuGetProjectUpgradeDependencyItem> _upgradeDependencyItems;
        private HashSet<PackageIdentity> _notFoundPackages;
        private string _projectName;

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

        public IEnumerable<PackageIdentity> NotFoundPackages
        {
            get
            {
                if(_notFoundPackages == null)
                {
                    GetUpgradeDependencyItems();
                }
                return _notFoundPackages;
            }
        }

        internal ObservableCollection<NuGetProjectUpgradeDependencyItem> UpgradeDependencyItems
            => _upgradeDependencyItems ?? (_upgradeDependencyItems = GetUpgradeDependencyItems());

        public IEnumerable<NuGetProjectUpgradeDependencyItem> DirectDependencies => IncludedCollapsedPackages;

        public IEnumerable<NuGetProjectUpgradeDependencyItem> TransitiveDependencies => DependencyPackages;

        private IEnumerable<NuGetProjectUpgradeDependencyItem> DependencyPackages => _dependencyPackages ?? (_dependencyPackages = GetDependencyPackages());

        public IEnumerable<NuGetProjectUpgradeDependencyItem> AllPackages => UpgradeDependencyItems;

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

        private void InitPackageUpgradeIssues(FolderNuGetProject folderNuGetProject, NuGetProjectUpgradeDependencyItem package, NuGetFramework framework)
        {
            _notFoundPackages = new HashSet<PackageIdentity>();
            var packageIdentity = new PackageIdentity(package.Id, NuGetVersion.Parse(package.Version));
            // Confirm package exists
            var packagePath = folderNuGetProject.GetInstalledPackageFilePath(packageIdentity);
            if (string.IsNullOrEmpty(packagePath))
            {
                _notFoundPackages.Add(packageIdentity);
                package.Issues.Add(PackLogMessage.CreateWarning(
                    string.Format(CultureInfo.CurrentCulture, Resources.Upgrader_PackageNotFound, packageIdentity.Id),
                    NuGetLogCode.NU5500));
            }
            else
            {
                var reader = new PackageArchiveReader(packagePath);

                // TODO: Create the right set of rules here.
                var packageRules = PackageCreationRuleSet.Rules;
                var issues = package.Issues;

                foreach (var rule in packageRules)
                {
                    issues.AddRange(rule.Validate(reader).OrderBy(p => p.Code.ToString(), StringComparer.CurrentCulture));
                }
            }
        }

        private ObservableCollection<NuGetProjectUpgradeDependencyItem> GetUpgradeDependencyItems()
        {
            var upgradeDependencyItems = new ObservableCollection<NuGetProjectUpgradeDependencyItem>(PackageDependencyInfos
                .Select(p => new NuGetProjectUpgradeDependencyItem(new PackageIdentity(p.Id, p.Version))).ToList());

            foreach (var packageDependencyInfo in PackageDependencyInfos)
            {
                foreach (var dependency in packageDependencyInfo.Dependencies)
                {
                    var matchingDependencyItem = upgradeDependencyItems
                        .FirstOrDefault(d => (d.Package.Id == dependency.Id) && (d.Package.Version == dependency.VersionRange.MinVersion));
                    matchingDependencyItem?.DependingPackages.Add(new PackageIdentity(packageDependencyInfo.Id, packageDependencyInfo.Version));
                }
            }

            var msBuildNuGetProject = (MSBuildNuGetProject)Project;
            var framework = msBuildNuGetProject.ProjectSystem.TargetFramework;
            var folderNuGetProject = msBuildNuGetProject.FolderNuGetProject;

            foreach (var package in upgradeDependencyItems)
            {
                InitPackageUpgradeIssues(folderNuGetProject, package, framework);
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
        }

        private static readonly PackageIdentity PackageOne = new PackageIdentity("Test.Package.One", new NuGetVersion("1.2.3"));
        private static readonly PackageIdentity PackageTwo = new PackageIdentity("Test.Package.Two", new NuGetVersion("4.5.6"));
        private static readonly PackageIdentity PackageThree = new PackageIdentity("Test.Package.Three", new NuGetVersion("7.8.9"));

        private static readonly ObservableCollection<NuGetProjectUpgradeDependencyItem> DesignTimeUpgradeDependencyItems = new ObservableCollection<NuGetProjectUpgradeDependencyItem>(new List<NuGetProjectUpgradeDependencyItem>
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

        });
#endif
    }
}