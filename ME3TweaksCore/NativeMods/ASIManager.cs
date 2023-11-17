﻿using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Xml.Linq;
using LegendaryExplorerCore.Gammtek.Extensions;
using LegendaryExplorerCore.Helpers;
using LegendaryExplorerCore.Packages;
using ME3TweaksCore.Diagnostics;
using ME3TweaksCore.GameFilesystem;
using ME3TweaksCore.Helpers;
using ME3TweaksCore.Localization;
using ME3TweaksCore.NativeMods.Interfaces;
using ME3TweaksCore.Services;
using ME3TweaksCore.Targets;
using Newtonsoft.Json.Linq;
using PropertyChanged;
using Serilog;

namespace ME3TweaksCore.NativeMods
{
    /// <summary>
    /// Backend for ASI Management
    /// </summary>
    [AddINotifyPropertyChangedInterface]
    public class ASIManager
    {
        public static readonly string CachedASIsFolder = Directory.CreateDirectory(Path.Combine(MCoreFilesystem.GetAppDataFolder(), @"CachedASIs")).FullName;

        public static readonly string ManifestLocation = Path.Combine(CachedASIsFolder, @"manifest.xml");
        public static readonly string StagedManifestLocation = Path.Combine(CachedASIsFolder, @"manifest_staged.xml");

        public static readonly List<ASIMod> MasterME1ASIUpdateGroups = new List<ASIMod>();
        public static readonly List<ASIMod> MasterME2ASIUpdateGroups = new List<ASIMod>();
        public static readonly List<ASIMod> MasterME3ASIUpdateGroups = new List<ASIMod>();

        public static readonly List<ASIMod> MasterLE1ASIUpdateGroups = new List<ASIMod>();
        public static readonly List<ASIMod> MasterLE2ASIUpdateGroups = new List<ASIMod>();
        public static readonly List<ASIMod> MasterLE3ASIUpdateGroups = new List<ASIMod>();

        /// <summary>
        /// Returns an enumeration of all master update groups for all games
        /// </summary>
        public static IEnumerable<ASIMod> AllASIMods => MasterLE1ASIUpdateGroups.Concat(MasterLE2ASIUpdateGroups).Concat(MasterLE3ASIUpdateGroups).Concat(MasterME1ASIUpdateGroups).Concat(MasterME2ASIUpdateGroups).Concat(MasterME3ASIUpdateGroups);

        /// <summary>
        /// If ASI Manager is using Beta ASI mods
        /// </summary>
        public static bool UsingBeta { get; private set; }

        /// <summary>
        /// Use to set if ASI mods should use beta
        /// </summary>
        /// <param name="usingBeta"></param>
        public static void SetUsingBeta(bool usingBeta)
        {
            UsingBeta = usingBeta;

            // Other logic here
        }

        /// <summary>
        /// Loads the ASI manifest. This should only be done at startup or when the online manifest is refreshed. ForceLocal only works if there is local ASI manifest present
        /// </summary>
        /// <param name="forceLocal">If the manifest should be force-loaded from the local cache</param>
        /// <param name="overrideThrottling">If the manifest should be updated regardless of content check throttle</param>
        /// <param name="preloadedManifestData">Preloaded data if it was loaded from something else, such as a combined manifest</param>
        public static void LoadManifest(bool forceLocal = false, bool overrideThrottling = false, string preloadedManifestData = null)
        {
            MLog.Information($@"Loading ASI manifest. Using beta ASIs: {UsingBeta}");
            try
            {
                internalLoadManifest(forceLocal, overrideThrottling, preloadedManifestData);
            }
            catch (Exception e)
            {
                MLog.Error($@"Error loading ASI manifest: {e.Message}");
            }
        }

        private static void internalLoadManifest(bool forceLocal = false, bool overrideThrottling = false, string preloadedManifestData = null)
        {
            if (File.Exists(ManifestLocation) && (forceLocal || (!MOnlineContent.CanFetchContentThrottleCheck() && !overrideThrottling))) //Force local, or we can't online check and cannot override throttle
            {
                LoadManifestFromDisk(ManifestLocation);
                MLog.Information(@"Loaded cached ASI manifest");
                logManifestInfo();
                return;
            }

            var shouldNotFetch = forceLocal || (!overrideThrottling && !MOnlineContent.CanFetchContentThrottleCheck()) && File.Exists(ManifestLocation);
            if (!shouldNotFetch) //this cannot be triggered if forceLocal is true
            {
                string onlineManifest = null;
                if (preloadedManifestData == null)
                {
                    MLog.Error(@"Fetching ASI manifest failed: As of 11/16/2023, data must come from combined services. This is a bug.");
                    Debugger.Break();
                }
                else
                {
                    MLog.Information(@"Using ASI manifest from online source");
                    onlineManifest = preloadedManifestData;
                }

                if (onlineManifest == null)
                {
                    MLog.Warning(@"Cannot load ASI manifest: Could not fetch online manifest and no local manifest exists");
                    LoadEmbeddedManifest();
                    logManifestInfo();
                    return;
                }

                onlineManifest = onlineManifest.Trim();
                try
                {
                    File.WriteAllText(StagedManifestLocation, onlineManifest);
                }
                catch (Exception e)
                {
                    MLog.Error(@"Error writing cached ASI manifest to disk: " + e.Message);
                }

                try
                {
                    ParseManifest(onlineManifest, true);
                    logManifestInfo();
                }
                catch (Exception e)
                {
                    MLog.Error(@"Error parsing online ASI manifest: " + e.Message);
                    internalLoadManifest(true); //force local load instead
                }

                return;
            }

            if (File.Exists(ManifestLocation))
            {
                MLog.Information(@"Loading local ASI manifest");
                try
                {
                    LoadManifestFromDisk(ManifestLocation, false);
                    MLog.Information(@"Loaded local ASI manifest");
                    logManifestInfo();
                }
                catch (Exception e)
                {
                    MLog.Exception(e, @"Error loading cached manifest: ");
                    //can't use local manifest - use the embedded one
                    LoadEmbeddedManifest();
                    logManifestInfo();
                }

                return;
            }


            //can't get manifest or local manifest.
            LoadEmbeddedManifest();
            logManifestInfo();
        }


        private static void LoadEmbeddedManifest()
        {
            MLog.Warning(@"Loading embedded ASI manifest as no on-disk or network based version could be used");
            var resource = MUtilities.ExtractInternalFileToStream(@"ME3TweaksCore.NativeMods.CachedASI.asimanifest.xml");
            var embeddedManifest = new StreamReader(resource).ReadToEnd();
            ParseManifest(embeddedManifest, false);

        }

        private static void logManifestInfo()
        {
            MLog.Information(@"Loaded ASI information:");
            var masterGroups = new[]
            {
                MasterME1ASIUpdateGroups, MasterME2ASIUpdateGroups, MasterME3ASIUpdateGroups, MasterLE1ASIUpdateGroups,
                MasterLE2ASIUpdateGroups, MasterLE3ASIUpdateGroups
            };

            for (int i = 0; i < masterGroups.Length; i++)
            {
                MLog.Information($@"{GameNumConversion.FromGameNum(i + 1).ToGameName(true)} has ASI groups {string.Join(',', masterGroups[i].Select(x => x.UpdateGroupId))} available");
                foreach (var asiversions in masterGroups[i])
                {
                    MLog.Debug($@"ASI Update Group {asiversions.UpdateGroupId} IsHidden: {asiversions.IsHidden} ---------------");
                    foreach (var asi in asiversions.Versions)
                    {
                        MLog.Debug($@"   {asi.Name} v{asi.Version} IsBetaOnly: {asi.IsBeta}");
                    }
                }

            }
        }

        /// <summary>
        /// Loads the data using a ServiceLoader system. This is treated like an online fetch.
        /// </summary>
        /// <param name="data"></param>
        /// <returns></returns>
        public static bool LoadService(JToken data)
        {
            // The data is just the xml string.
            LoadManifest(overrideThrottling: data != null, preloadedManifestData: data?.ToObject<string>());
            return true; // ??
        }

        ///// <summary>
        ///// Extracts the default ASI assets from this assembly so there is a default set of cached assets that are alway required for proper program functionality
        ///// </summary>
        //public static void ExtractDefaultASIResources()
        //{
        //    string[] defaultResources = { @"BalanceChangesReplacer-v3.0.asi", @"ME1-DLC-ModEnabler-v1.0.asi", @"ME3Logger_truncating-v1.0.asi", @"AutoTOC_LE-v2.0.asi", @"LE1AutoloadEnabler-v1.0.asi", @"manifest.xml" };
        //    foreach (var file in defaultResources)
        //    {
        //        var outfile = Path.Combine(CachedASIsFolder, file);
        //        if (!File.Exists(outfile))
        //        {
        //            MUtilities.ExtractInternalFile(@"ME3TweaksModManager.modmanager.asi." + file, outfile, true);
        //        }
        //    }
        //}

        /// <summary>
        /// Calls ParseManifest() on the given file path.
        /// </summary>
        /// <param name="manifestPath"></param>
        /// <param name="games"></param>
        /// <param name="isStaged">If file is staged for copying the cached location</param>
        /// <param name="selectionStateUpdateCallback"></param>
        private static void LoadManifestFromDisk(string manifestPath, bool isStaged = false)
        {
            MLog.Information($@"Using ASI manifest from disk: {manifestPath}");
            ParseManifest(File.ReadAllText(manifestPath), isStaged);
        }

        /// <summary>
        /// Converts integer to MEGame
        /// </summary>
        /// <param name="i"></param>
        /// <returns></returns>
        private static MEGame intToGame(int i)
        {
            switch (i)
            {
                case 1:
                    return MEGame.ME1;
                case 2:
                    return MEGame.ME2;
                case 3:
                    return MEGame.ME3;
                case 4:
                    return MEGame.LE1;
                case 5:
                    return MEGame.LE2;
                case 6:
                    return MEGame.LE3;
                case 7:
                    // Not used currently but this is how ME3Tweaks Server identifies LELauncher
                    return MEGame.LELauncher;
                default:
                    throw new Exception(LC.GetString(LC.string_interp_unsupportedGameIdInAsiManifest, i));
            }
        }

        /// <summary>
        /// Fetches the specified ASI by it's hash for the specified game
        /// </summary>
        /// <param name="hash">The MD5 to lookup</param>
        /// <param name="game">The game the ASI belongs to</param>
        /// <returns>The versioned ASI mod if found, null otherwise</returns>
        public static ASIModVersion GetASIVersionByHash(string hash, MEGame game)
        {
            List<ASIMod> relevantGroups = null;
            switch (game)
            {
                case MEGame.ME1:
                    relevantGroups = MasterME1ASIUpdateGroups;
                    break;
                case MEGame.ME2:
                    relevantGroups = MasterME2ASIUpdateGroups;
                    break;
                case MEGame.ME3:
                    relevantGroups = MasterME3ASIUpdateGroups;
                    break;
                case MEGame.LE1:
                    relevantGroups = MasterLE1ASIUpdateGroups;
                    break;
                case MEGame.LE2:
                    relevantGroups = MasterLE2ASIUpdateGroups;
                    break;
                case MEGame.LE3:
                    relevantGroups = MasterLE3ASIUpdateGroups;
                    break;
                default:
                    return null;
            }

            if (relevantGroups.Any())
            {
                return relevantGroups.FirstOrDefault(x => x.HasMatchingHash(hash))?.Versions.First(x => x.Hash == hash);
            }

            return null;
        }


        /// <summary>
        /// Fetches the specific ASI version by game.
        /// </summary>
        /// <param name="updateGroup">The update group of the ASI.</param>
        /// <param name="version">The version to fetch of the ASI</param>
        /// <param name="game">The game the ASI belongs to</param>
        /// <returns>The versioned ASI mod object, or null if not found.</returns>
        public static ASIModVersion GetASIVersion(int updateGroup, int version, MEGame game)
        {
            List<ASIMod> relevantGroups = null;
            switch (game)
            {
                case MEGame.ME1:
                    relevantGroups = MasterME1ASIUpdateGroups;
                    break;
                case MEGame.ME2:
                    relevantGroups = MasterME2ASIUpdateGroups;
                    break;
                case MEGame.ME3:
                    relevantGroups = MasterME3ASIUpdateGroups;
                    break;
                case MEGame.LE1:
                    relevantGroups = MasterLE1ASIUpdateGroups;
                    break;
                case MEGame.LE2:
                    relevantGroups = MasterLE2ASIUpdateGroups;
                    break;
                case MEGame.LE3:
                    relevantGroups = MasterLE3ASIUpdateGroups;
                    break;
                default:
                    return null;
            }

            if (relevantGroups.Any())
            {
                var group = relevantGroups.FirstOrDefault(x => x.UpdateGroupId == updateGroup);
                if (group != null)
                {
                    return group.Versions.FirstOrDefault(x => x.Version == version);
                }
            }

            return null;
        }

        /// <summary>
        /// Parses a string (xml) into an ASI manifest.
        /// </summary>
        private static void ParseManifest(string xmlText, bool isStaged = false)
        {
            bool reloadOnError = true;
            try
            {
                MasterME1ASIUpdateGroups.Clear();
                MasterME2ASIUpdateGroups.Clear();
                MasterME3ASIUpdateGroups.Clear();

                MasterLE1ASIUpdateGroups.Clear();
                MasterLE2ASIUpdateGroups.Clear();
                MasterLE3ASIUpdateGroups.Clear();
                XElement rootElement = XElement.Parse(xmlText.Trim());
                Debug.WriteLine(rootElement.ToString());


                //I Love Linq
                var updateGroups = (from ugroup in rootElement.Elements(@"updategroup")
                                    select new ASIMod
                                    {
                                        UpdateGroupId = (int)ugroup.Attribute(@"groupid"),
                                        Game = intToGame((int)ugroup.Attribute(@"game")),
                                        Versions = ugroup.Elements(@"asimod").Select(version => new ASIModVersion
                                        {
                                            Name = (string)version.Element(@"name"),
                                            InstalledPrefix = (string)version.Element(@"installedname"),
                                            Author = (string)version.Element(@"author"),
                                            Version = TryConvert.ToInt32(version.Element(@"version")?.Value, 1), // This could hide errors pretty easily if something is wrong with ASI manifest!
                                            Description = (string)version.Element(@"description"),
                                            Hash = (string)version.Element(@"hash"),
                                            SourceCodeLink = (string)version.Element(@"sourcecode"),
                                            DownloadLink = (string)version.Element(@"downloadlink"),
                                            IsBeta = TryConvert.ToBoolFromInt(version.Element(@"beta")?.Value),
                                            Hidden = TryConvert.ToBoolFromInt(version.Element(@"hidden")?.Value),
                                            _otherGroupsToDeleteOnInstallInternal = version.Element(@"autoremovegroups")?.Value,

                                            Game = intToGame((int)ugroup.Attribute(@"game")), // use ugroup element to pull from outer group
                                        }).OrderBy(x => x.Version).ToList()
                                    }).ToList();
                foreach (var v in updateGroups)
                {
                    if (v.LatestVersionIncludingHidden == null)
                        continue; // Group has no available ASIs - maybe new beta asi
#if DEBUG
                    Debug.WriteLine($@"Read {v.Game} ASI group {v.UpdateGroupId}: {v.LatestVersionIncludingHidden}. Beta: {v.LatestVersionIncludingHidden.IsBeta}");
#endif
                    // If all ASIs are hidden mark entire group as hidden.
                    if (v.Versions.All(x => x.Hidden))
                    {
                        v.IsHidden = true;
                    }

                    switch (v.Game)
                    {
                        case MEGame.ME1:
                            MasterME1ASIUpdateGroups.Add(v);
                            break;
                        case MEGame.ME2:
                            MasterME2ASIUpdateGroups.Add(v);
                            break;
                        case MEGame.ME3:
                            MasterME3ASIUpdateGroups.Add(v);
                            break;
                        case MEGame.LE1:
                            MasterLE1ASIUpdateGroups.Add(v);
                            break;
                        case MEGame.LE2:
                            MasterLE2ASIUpdateGroups.Add(v);
                            break;
                        case MEGame.LE3:
                            MasterLE3ASIUpdateGroups.Add(v);
                            break;
                    }

                    // Linq (get it?) versions to parents
                    foreach (var m in v.Versions)
                    {
                        m.OwningMod = v;
                        m.OtherGroupsToDeleteOnInstall.Remove(v.UpdateGroupId); // Ensure we don't delete ourself on install
                    }
                }

                reloadOnError = false;
                if (isStaged)
                {
                    File.Copy(StagedManifestLocation, ManifestLocation, true); //this will make sure cached manifest is parsable.
                }
            }
            catch (Exception e)
            {
                if (isStaged && File.Exists(ManifestLocation) && reloadOnError)
                {
                    //try cached instead
                    LoadManifestFromDisk(ManifestLocation, false);
                    return;
                }
                if (!reloadOnError)
                {
                    return; //Don't rethrow exception as we did load the manifest still
                }

                throw new Exception(@"Error parsing the ASI Manifest: " + e.Message);
            }

        }

        /// <summary>
        /// Installs the specific version of an ASI to the specified target
        /// </summary>
        /// <param name="target"></param>
        /// <param name="forceSource">Null to let application choose the source, true to force online, false to force local cache. This parameter is used for testing</param>
        /// <returns></returns>
        public static bool InstallASIToTarget(ASIModVersion asi, GameTarget target, bool? forceSource = null)
        {
            if (asi.Game != target.Game) throw new Exception($@"ASI {asi.Name} cannot be installed to game {target.Game}");
            MLog.Information($@"Processing ASI installation request: {asi.Name} v{asi.Version} -> {target.TargetPath}");
            string destinationFilename = $@"{asi.InstalledPrefix}-v{asi.Version}.asi";
            string cachedPath = Path.Combine(CachedASIsFolder, destinationFilename);
            string destinationDirectory = M3Directories.GetASIPath(target);
            if (!Directory.Exists(destinationDirectory))
            {
                MLog.Information(@"Creating ASI directory in game: " + destinationDirectory);
                Directory.CreateDirectory(destinationDirectory);
            }
            string finalPath = Path.Combine(destinationDirectory, destinationFilename);

            var installedASIs = target.GetInstalledASIs();
            // Delete existing ASIs from the same group to ensure we don't install the same mod
            var existingSameGroupMods = installedASIs.OfType<IKnownInstalledASIMod>().Where(x => x.AssociatedManifestItem.OwningMod == asi.OwningMod).ToList();
            bool hasExistingVersionOfModInstalled = false;
            if (existingSameGroupMods.Any())
            {
                foreach (var v in existingSameGroupMods)
                {
                    if (v.Hash == asi.Hash && !forceSource.HasValue && !hasExistingVersionOfModInstalled) //If we are forcing a source, we should always install. Delete duplicates past the first one
                    {
                        MLog.Information($@"{v.AssociatedManifestItem.Name} is already installed. We will not remove the existing correct installed ASI for this install request");
                        hasExistingVersionOfModInstalled = true;
                        continue; //Don't delete this one. We are already installed. There is no reason to install it again.
                    }
                    MLog.Information($@"Deleting existing ASI from same group: {v.InstalledPath}");
                    v.Uninstall();
                    installedASIs.Remove(v);
                }
            }

            // Remove any conflicting ASIs
            foreach (var v in installedASIs.OfType<IKnownInstalledASIMod>())
            {
                if (asi.OtherGroupsToDeleteOnInstall.Contains(v.AssociatedManifestItem.OwningMod.UpdateGroupId))
                {
                    // Delete tis other ASI
                    v.Uninstall();
                }
            }

            if (hasExistingVersionOfModInstalled)
            {
                return true; // The ASI was already installed. There is nothing left to do
            }

            // Install the ASI
            //if (forceSource == null || forceSource.Value == false)
            //{
            //    Debug.WriteLine("Hit me");
            //}
            string md5;
            bool useLocal = forceSource == false;
            if (!useLocal && !forceSource.HasValue)
            {
                // Do not use local was set, but no preference of where source was set, so we will see if it exists in cache
                useLocal = File.Exists(cachedPath);
            }
            if (useLocal)
            {
                //Check hash first
                md5 = MUtilities.CalculateHash(cachedPath);
                if (md5 == asi.Hash)
                {
                    MLog.Information($@"Copying ASI from cached library to destination: {cachedPath} -> {finalPath}");

                    File.Copy(cachedPath, finalPath, true);
                    MLog.Information($@"Installed ASI to {finalPath}");
                    TelemetryInterposer.TrackEvent(@"Installed ASI", new Dictionary<string, string>() {
                                { @"Filename", Path.GetFileNameWithoutExtension(finalPath)}
                            });
                    return true;
                }
            }

            if (!forceSource.HasValue || forceSource.Value)
            {
                var usingEmbedded = false;
                var downloadStream = new MemoryStream();


                // Is this a locally embedded ASI?
                var fname = MUtilities.GetFileNameFromUrl(asi.DownloadLink);
                if (fname != null)
                {
                    var embeddedAssetPath = $@"ME3TweaksCore.NativeMods.CachedASI.{asi.Game}.{destinationFilename}";

                    if (asi.OwningMod.UpdateGroupId is 29 or 30 or 31) // AutoTOC for LE is the same across all games 
                    {
                        embeddedAssetPath = $@"ME3TweaksCore.NativeMods.CachedASI.{destinationFilename}";
                    }

                    var existsEmbedded = MUtilities.DoesEmbeddedAssetExist(embeddedAssetPath);
                    if (existsEmbedded)
                    {
                        downloadStream = MUtilities.ExtractInternalFileToStream(embeddedAssetPath);

                        if (MUtilities.CalculateHash(downloadStream) != asi.Hash)
                        {
                            MLog.Error(@"Embedded ASI hash does not match manifest, we will discard the embedded ASI data");
                            downloadStream = new MemoryStream();
                        }
                        else
                        {
                            usingEmbedded = true;
                        }
                    }
                }

                try
                {
                    if (!usingEmbedded)
                    {
                        // Online download
                        MLog.Information(@"Fetching remote ASI from server");
                        var request = WebRequest.Create(asi.DownloadLink);

                        using WebResponse response = request.GetResponse();
                        response.GetResponseStream().CopyTo(downloadStream);
                    }

                    // Online download security check
                    if (!usingEmbedded)
                    {
                        md5 = MUtilities.CalculateHash(downloadStream);
                        if (md5 != asi.Hash)
                        {
                            //ERROR!
                            MLog.Error(@"Downloaded ASI did not match the manifest! It has the wrong hash.");
                            return false;
                        }

                        MLog.Information(@"Fetched remote ASI from server.");
                    }

                    MLog.Information($@"Installing ASI to {finalPath}");

                    downloadStream.WriteToFile(finalPath);
                    MLog.Information(@"ASI successfully installed.");
                    TelemetryInterposer.TrackEvent(@"Installed ASI", new Dictionary<string, string>()
                    {
                        {@"Filename", Path.GetFileNameWithoutExtension(finalPath)}
                    });

                    //Cache ASI
                    if (!Directory.Exists(CachedASIsFolder))
                    {
                        MLog.Information(@"Creating cached ASIs folder");
                        Directory.CreateDirectory(CachedASIsFolder);
                    }

                    MLog.Information(@"Caching ASI to local ASI library: " + cachedPath);
                    downloadStream.WriteToFile(cachedPath);
                    return true;
                }
                catch (Exception e)
                {
                    MLog.Error($@"Error downloading ASI from {asi.DownloadLink}: {e.Message}");
                }
            }

            // We could not install the ASI
            return false;
        }

        /// <summary>
        /// Installs an ASI to the specified target. If there is an existing version installed, it is updated. If no version is specified, the latest version is installed.
        /// </summary>
        /// <param name="mod">The ASI mod to install</param>
        /// <param name="target">The target to install to</param>
        /// <param name="version">The version to install. The default is 0, which means the latest</param>
        /// <param name="includeHiddenASIVersions">If hidden ASI mod versions should be included.</param>
        /// <returns></returns>
        public static bool InstallASIToTarget(ASIMod mod, GameTarget target, int version = 0, bool includeHiddenASIVersions = false)
        {
            ASIModVersion modV = null;
            if (version == 0)
            {
                modV = includeHiddenASIVersions ? mod.LatestVersionIncludingHidden : mod.LatestVersion;
            }
            else
            {
                modV = mod.Versions.FirstOrDefault(x => x.Version == version);
            }

            if (modV == null)
                return false; // Did not install

            return InstallASIToTarget(modV, target);
        }

        /// <summary>
        /// Installs the specified ASI (by update group ID) to the target. If a version is not specified, the latest version is installed.
        /// </summary>
        /// <param name="updateGroup"></param>
        /// <param name="nameForLogging"></param>
        /// <param name="gameTarget"></param>
        /// <param name="version">The versio nto install. The default is 0, which means the latest</param>
        public static bool InstallASIToTargetByGroupID(int updateGroup, string nameForLogging, GameTarget gameTarget, int version = 0, bool includeHiddenASIs = false)
        {
            var group = GetASIModsByGame(gameTarget.Game).FirstOrDefault(x => x.UpdateGroupId == updateGroup);
            if (group == null)
            {
                // Cannot find ASI!
                MLog.Error($@"Cannot find ASI ({nameForLogging}) with update group ID {updateGroup} for game {gameTarget.Game}");
                return false;
            }

            return InstallASIToTarget(group, gameTarget, version, includeHiddenASIs);
        }

        /// <summary>
        /// Gets the list of ASI mods by game from the manifest
        /// </summary>
        /// <param name="game"></param>
        /// <returns></returns>
        public static List<ASIMod> GetASIModsByGame(MEGame game)
        {
            switch (game)
            {
                case MEGame.ME1:
                    return MasterME1ASIUpdateGroups;
                case MEGame.ME2:
                    return MasterME2ASIUpdateGroups;
                case MEGame.ME3:
                    return MasterME3ASIUpdateGroups;
                case MEGame.LE1:
                    return MasterLE1ASIUpdateGroups;
                case MEGame.LE2:
                    return MasterLE2ASIUpdateGroups;
                case MEGame.LE3:
                    return MasterLE3ASIUpdateGroups;
                default:
                    return null;
            }
        }
    }
}
