// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License.

using Microsoft.PowerShell.PowerShellGet.UtilClasses;
using NuGet.Versioning;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Management.Automation;

using Dbg = System.Diagnostics.Debug;

namespace Microsoft.PowerShell.PowerShellGet.Cmdlets
{
    /// <summary>
    /// The Save-PSResource cmdlet saves a resource to a machine.
    /// It returns nothing.
    /// </summary>
    [Cmdlet(VerbsData.Save, "PSResource", DefaultParameterSetName = "IncludeXmlParameterSet", SupportsShouldProcess = true, ConfirmImpact = ConfirmImpact.Low)]
    public sealed class SavePSResource : PSCmdlet
    {
        #region Members

        private const string InputObjectParameterSet = "InputObjectParameterSet";
        private const string AsNupkgParameterSet = "AsNupkgParameterSet";
        private const string IncludeXmlParameterSet = "IncludeXmlParameterSet";
        VersionRange _versionRange;
        InstallHelper _installHelper;

        #endregion

        #region Parameters

        /// <summary>
        /// Specifies the exact names of resources to save from a repository.
        /// A comma-separated list of module names is accepted. The resource name must match the resource name in the repository.
        /// </summary>
        [Parameter(Mandatory = true, Position = 0, ValueFromPipeline = true, ParameterSetName = AsNupkgParameterSet)]
        [Parameter(Mandatory = true, Position = 0, ValueFromPipeline = true, ParameterSetName = IncludeXmlParameterSet)]
        [ValidateNotNullOrEmpty]
        public string[] Name { get; set; }

        /// <summary>
        /// Specifies the version or version range of the package to be saved
        /// </summary>
        [Parameter(ParameterSetName = AsNupkgParameterSet)]
        [Parameter(ParameterSetName = IncludeXmlParameterSet)]
        [ValidateNotNullOrEmpty]
        public string Version { get; set; }

        /// <summary>
        /// Specifies to allow saving of prerelease versions
        /// </summary>
        [Parameter(ParameterSetName = AsNupkgParameterSet)]
        [Parameter(ParameterSetName = IncludeXmlParameterSet)]
        public SwitchParameter Prerelease { get; set; }

        /// <summary>
        /// Specifies the specific repositories to search within.
        /// </summary>
        [SupportsWildcards]
        [Parameter(ParameterSetName = AsNupkgParameterSet)]
        [Parameter(ParameterSetName = IncludeXmlParameterSet)]
        [ArgumentCompleter(typeof(RepositoryNameCompleter))]
        [ValidateNotNullOrEmpty]
        public string[] Repository { get; set; }

        /// <summary>
        /// Specifies a user account that has rights to save a resource from a specific repository.
        /// </summary>
        [Parameter]
        public PSCredential Credential { get; set; }

        /// <summary>
        /// Saves the resource as a .nupkg
        /// </summary>
        [Parameter(ParameterSetName = AsNupkgParameterSet)]
        public SwitchParameter AsNupkg { get; set; }

        /// <summary>
        /// Saves the metadata XML file with the resource
        /// </summary>
        [Parameter(ParameterSetName = IncludeXmlParameterSet)]
        public SwitchParameter IncludeXml { get; set; }

        /// <summary>
        /// The destination where the resource is to be installed. Works for all resource types.
        /// </summary>
        [Parameter(Mandatory = true)]
        [ValidateNotNullOrEmpty]
        public string Path
        {
            get
            { return _path; }

            set
            {
                if (WildcardPattern.ContainsWildcardCharacters(value))
                {
                    throw new PSArgumentException("Wildcard characters are not allowed in the path.");
                }

                // This will throw if path cannot be resolved
                _path = SessionState.Path.GetResolvedPSPathFromPSPath(value).First().Path;
            }
        }
        private string _path;

        /// <summary>
        /// The destination where the resource is to be temporarily saved to.

        /// </summary>
        [Parameter]
        [ValidateNotNullOrEmpty]
        public string TemporaryPath
        {
            get
            { return _tmpPath; }

            set
            {
                if (WildcardPattern.ContainsWildcardCharacters(value))
                {
                    throw new PSArgumentException("Wildcard characters are not allowed in the temporary path.");
                }

                // This will throw if path cannot be resolved
                _tmpPath = SessionState.Path.GetResolvedPSPathFromPSPath(value).First().Path;
            }
        }
        private string _tmpPath;

        /// <summary>
        /// Suppresses being prompted for untrusted sources.
        /// </summary>
        [Parameter]
        public SwitchParameter TrustRepository { get; set; }

        /// <summary>
        /// Passes the resource saved to the console.
        /// </summary>
        [Parameter]
        public SwitchParameter PassThru { get; set; }

        /// <summary>
        /// Used for pipeline input.
        /// </summary>
        [Parameter(Mandatory = true, Position = 0, ValueFromPipeline = true, ParameterSetName = InputObjectParameterSet)]
        [ValidateNotNullOrEmpty]
        public PSResourceInfo InputObject { get; set; }

        /// <summary>
        /// Skips the check for resource dependencies, so that only found resources are saved,
        /// and not any resources the found resource depends on.
        /// </summary>
        [Parameter]
        public SwitchParameter SkipDependencyCheck { get; set; }

        /// <summary>
        /// Check validation for signed and catalog files

        /// </summary>
        [Parameter]
        public SwitchParameter AuthenticodeCheck { get; set; }

        /// <summary>
        /// Suppresses progress information.
        /// </summary>
        public SwitchParameter Quiet { get; set; }

        #endregion

        #region Method overrides

        protected override void BeginProcessing()
        {
            // Create a repository story (the PSResourceRepository.xml file) if it does not already exist
            // This is to create a better experience for those who have just installed v3 and want to get up and running quickly
            RepositorySettings.CheckRepositoryStore();

            _installHelper = new InstallHelper(cmdletPassedIn: this);
        }

        protected override void ProcessRecord()
        {
            switch (ParameterSetName)
            {
                case AsNupkgParameterSet:
                case IncludeXmlParameterSet:
                    // validate that if a -Version param is passed in that it can be parsed into a NuGet version range.
                    // an exact version will be formatted into a version range.
                    if (Version == null)
                    {
                        _versionRange = VersionRange.All;
                    }
                    else if (!Utils.TryParseVersionOrVersionRange(Version, out _versionRange))
                    {
                        var exMessage = "Argument for -Version parameter is not in the proper format.";
                        var ex = new ArgumentException(exMessage);
                        var IncorrectVersionFormat = new ErrorRecord(ex, "IncorrectVersionFormat", ErrorCategory.InvalidArgument, null);
                        ThrowTerminatingError(IncorrectVersionFormat);
                    }

                    ProcessSaveHelper(
                        pkgNames: Name,
                        pkgPrerelease: Prerelease,
                        pkgRepository: Repository);
                    break;

                case InputObjectParameterSet:
                    string normalizedVersionString = Utils.GetNormalizedVersionString(InputObject.Version.ToString(), InputObject.Prerelease);
                    if (!Utils.TryParseVersionOrVersionRange(normalizedVersionString, out _versionRange))
                    {
                        var exMessage = String.Format("Version '{0}' for resource '{1}' cannot be parsed.", normalizedVersionString, InputObject.Name);
                        var ex = new ArgumentException(exMessage);
                        var IncorrectVersionFormat = new ErrorRecord(ex, "IncorrectVersionFormat", ErrorCategory.InvalidArgument, null);
                        ThrowTerminatingError(IncorrectVersionFormat);
                    }

                    ProcessSaveHelper(
                        pkgNames: new string[] { InputObject.Name },
                        pkgPrerelease: InputObject.IsPrerelease,
                        pkgRepository: new string[] { InputObject.Repository });

                    break;

                default:
                    Dbg.Assert(false, "Invalid parameter set");
                    break;
            }
        }

        #endregion

        #region Private methods

        private void ProcessSaveHelper(string[] pkgNames, bool pkgPrerelease, string[] pkgRepository)
        {
            var namesToSave = Utils.ProcessNameWildcards(pkgNames, out string[] errorMsgs, out bool nameContainsWildcard);
            if (nameContainsWildcard)
            {
                WriteError(new ErrorRecord(
                    new PSInvalidOperationException("Name with wildcards is not supported for Save-PSResource cmdlet"),
                    "NameContainsWildcard",
                    ErrorCategory.InvalidArgument,
                    this));
                return;
            }

            foreach (string error in errorMsgs)
            {
                WriteError(new ErrorRecord(
                    new PSInvalidOperationException(error),
                    "ErrorFilteringNamesForUnsupportedWildcards",
                    ErrorCategory.InvalidArgument,
                    this));
            }

            // this catches the case where Name wasn't passed in as null or empty,
            // but after filtering out unsupported wildcard names there are no elements left in namesToSave
            if (namesToSave.Length == 0)
            {
                return;
            }

            if (!ShouldProcess(string.Format("Resources to save: '{0}'", namesToSave)))
            {
                WriteVerbose(string.Format("Save operation cancelled by user for resources: {0}", namesToSave));
                return;
            }

            var installedPkgs = _installHelper.InstallPackages(
                names: namesToSave,
                versionRange: _versionRange,
                prerelease: pkgPrerelease,
                repository: pkgRepository,
                acceptLicense: true,
                quiet: Quiet,
                reinstall: true,
                force: false,
                trustRepository: TrustRepository,
                credential: Credential,
                noClobber: false,
                asNupkg: AsNupkg,
                includeXml: IncludeXml,
                skipDependencyCheck: SkipDependencyCheck,
                authenticodeCheck: AuthenticodeCheck,
                savePkg: true,
                pathsToInstallPkg: new List<string> { _path },
                scope: null,
                tmpPath: _tmpPath);

            if (PassThru)
            {
                foreach (PSResourceInfo pkg in installedPkgs)
                {
                    WriteObject(pkg);
                }
            }
        }

        #endregion
    }
}
