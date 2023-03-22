﻿using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using LegendaryExplorerCore.GameFilesystem;
using LegendaryExplorerCore.Gammtek.Extensions;
using LegendaryExplorerCore.Helpers;
using LegendaryExplorerCore.ME1.Unreal.UnhoodBytecode;
using LegendaryExplorerCore.Packages;
using ME3TweaksCore.Diagnostics;
using ME3TweaksCore.Helpers;
using ME3TweaksCore.Localization;
using ME3TweaksCore.Misc;
using ME3TweaksCore.Services.Backup;
using ME3TweaksCore.Targets;
using PropertyChanged;
using RoboSharp;

namespace ME3TweaksCore.Services.Restore
{
    #region RESTORE

    /// <summary>
    /// Object that contains the logic for performing the restoration of a game.
    /// </summary>
    [AddINotifyPropertyChangedInterface]
    public class GameRestore
    {
        public MEGame Game { get; }

        public long ProgressValue { get; set; }
        public long ProgressMax { get; set; }
        //public bool ProgressIndeterminate { get; set; }
        /// <summary>
        /// Callback for when there is a blocking error and the restore cannot be performed. The first parameter is the title, the second is the message
        /// </summary>
        public Action<string, string> BlockingErrorCallback { get; set; }
        /// <summary>
        /// Callback for when there is an error during the restore. This may mean you need to keep UI target available still so user can try again without losing the target
        /// </summary>
        public Action<string, string> RestoreErrorCallback { get; set; }
        /// <summary>
        /// Callback to select a directory for custom restore location
        /// </summary>
        public Func<string, string, string> SelectDestinationDirectoryCallback { get; set; }
        /// <summary>
        /// Callback to confirm restoration over existing game
        /// </summary>
        public Func<string, string, bool> ConfirmationCallback { get; set; }
        /// <summary>
        /// Callback when the status string on the UI should be updated
        /// </summary>
        public Action<string> UpdateStatusCallback { get; set; }
        /// <summary>
        /// Callback when there is a progress update for the UI
        /// </summary>
        public Action<long, long> UpdateProgressCallback { get; set; }
        /// <summary>
        /// Callback when the progressbar should change indeterminate states
        /// </summary>
        public Action<bool> SetProgressIndeterminateCallback { get; set; }
        /// <summary>
        /// Value indicating if a restore operation is currently in progress
        /// </summary>
        public bool RestoreInProgress { get; private set; }

        /// <summary>
        /// The function that retreives a string for the restore-everything prompt, to allow tool-specific text
        /// </summary>
        public Func<MEGame, string> GetRestoreEverythingString { get; set; } = RestoreEverythingDefault;

        /// <summary>
        /// If optimized texture restore method should be used. If false, it's skipped
        /// </summary>
        public Func<bool> UseOptimizedTextureRestore { get; set; } = UseOptimizedTextureRestoreDefault;

        /// <summary>
        /// If each file about to be copied should be logged. This is a debugging feature
        /// </summary>
        public Func<bool> ShouldLogEveryCopiedFile { get; set; } = ShouldLogEveryCopiedFileDefault;

        /// <summary>
        /// If the restore should use the legacy full copy implementation (not recommended)
        /// </summary>
        public Func<bool> UseLegacyFullCopy { get; set; } = UseLegacyFullCopyDefault;

        #region Delegate defaults
        /// <summary>
        /// If texture modded, scan for marker and strip it and reset datestamp instead of copy
        /// </summary>
        /// <returns></returns>
        private static bool UseOptimizedTextureRestoreDefault()
        {
            return true;
        }

        /// <summary>
        /// Do not log all files by default
        /// </summary>
        /// <returns></returns>
        private static bool ShouldLogEveryCopiedFileDefault()
        {
            return false;
        }


        /// <summary>
        /// The default text for when everything is being restored via a backup.
        /// </summary>
        /// <param name="game"></param>
        /// <returns></returns>
        private static string RestoreEverythingDefault(MEGame game)
        {
            return LC.GetString(LC.string_interp_restoringWillDeleteEverythingMessage);
        }

        /// <summary>
        /// Default: we don't use legacy full wipe method of restore
        /// </summary>
        /// <returns></returns>
        private static bool UseLegacyFullCopyDefault()
        {
            return false;
        }
        #endregion

        public GameRestore(MEGame game)
        {
            this.Game = game;
        }

        /// <summary>
        /// Restores the game to the specified directory (game location). Pass in null if you wish to restore to a custom location. Refreshes the target on completion. This call is blocking, it should be run on a background thread.
        /// </summary>
        /// <param name="destinationDirectory">Game directory that will be replaced with backup</param>
        /// <returns></returns>
        public bool PerformRestore(GameTarget restoreTarget, string destinationDirectory)
        {
            var useFullCopyMethod = UseLegacyFullCopy();

            if (MUtilities.IsGameRunning(Game))
            {
                BlockingErrorCallback?.Invoke(LC.GetString(LC.string_cannotRestoreGame), LC.GetString(LC.string_interp_cannotRestoreGameToGameWhileRunning, Game.ToGameName()));
                return false;
            }

            bool restore = destinationDirectory == null; // Restore to custom location
            if (!restore)
            {
                var confirmDeletion = ConfirmationCallback?.Invoke(LC.GetString(LC.string_interp_restoringWillDeleteEverythingTitle, Game.ToGameName()), GetRestoreEverythingString(Game));
                restore |= confirmDeletion.HasValue && confirmDeletion.Value;
            }

            var backupStatus = BackupService.GetBackupStatus(Game);

            if (restore)
            {
                RestoreInProgress = true;
                // We will set values on backupStatus.
                string backupPath = BackupService.GetGameBackupPath(Game);

                if (destinationDirectory == null)
                {
                    destinationDirectory = SelectDestinationDirectoryCallback?.Invoke(LC.GetString(LC.string_selectDestinationLocationForRestore), LC.GetString(LC.string_selectADirectoryToRestoreTheGameToThisDirectoryMustBeEmpty));
                    if (destinationDirectory != null)
                    {
                        //Check empty
                        if (Directory.Exists(destinationDirectory))
                        {
                            if (Directory.GetFiles(destinationDirectory).Length > 0 || Directory.GetDirectories(destinationDirectory).Length > 0)
                            {
                                //Directory not empty
                                BlockingErrorCallback?.Invoke(LC.GetString(LC.string_cannotRestoreGame), LC.GetString(LC.string_restoreDestinationNotEmpty));
                                return false;
                            }

                            //TODO: PREVENT RESTORING TO DOCUMENTS/BIOWARE

                        }

                        TelemetryInterposer.TrackEvent(@"Chose to restore game to custom location", new Dictionary<string, string>() { { @"Game", Game.ToString() } });

                    }
                    else
                    {
                        MLog.Warning(@"User declined to choose destination directory");
                        return false;
                    }
                }

                SetProgressIndeterminateCallback?.Invoke(true);

                if (useFullCopyMethod)
                {
                    // Leftover unused code for now
                    backupStatus.BackupStatus = LC.GetString(LC.string_deletingExistingGameInstallation);
                    if (Directory.Exists(destinationDirectory))
                    {
                        if (Directory.GetFiles(destinationDirectory).Any() || Directory.GetDirectories(destinationDirectory).Any())
                        {
                            MLog.Information(@"Deleting existing game directory: " + destinationDirectory);
                            try
                            {
                                bool deletedDirectory = MUtilities.DeleteFilesAndFoldersRecursively(destinationDirectory);
                                if (deletedDirectory != true)
                                {
                                    RestoreErrorCallback?.Invoke(LC.GetString(LC.string_couldNotDeleteGameDirectory), LC.GetString(LC.string_interp_couldNotFullyDeleteGameDirectory, Game.ToGameName()));
                                    //b.Result = RestoreResult.ERROR_COULD_NOT_DELETE_GAME_DIRECTORY;
                                    return false;
                                }
                            }
                            catch (Exception ex)
                            {
                                //todo: handle this better
                                MLog.Error($@"Exception deleting game directory: {destinationDirectory}: {ex.Message}");
                                RestoreErrorCallback?.Invoke(LC.GetString(LC.string_errorDeletingGameDirectory), LC.GetString(LC.string_interp_couldNotFullyDeleteGameDirectoryException, Game.ToGameName(), ex.Message));
                                //b.Result = RestoreResult.EXCEPTION_DELETING_GAME_DIRECTORY;
                                return false;
                            }
                        }
                    }
                    else
                    {
                        MLog.Error(@"Game directory not found! Was it removed while the app was running?");
                    }
                }

                backupStatus.BackupLocationStatus = LC.GetString(LC.string_preparingGameDirectory);
                var created = MUtilities.CreateDirectoryWithWritePermission(destinationDirectory);
                if (!created)
                {
                    RestoreErrorCallback?.Invoke(LC.GetString(LC.string_errorCreatingGameDirectory), LC.GetString(LC.string_interp_couldNotCreateGameDirectoryNoPermission, Game.ToGameName()));
                    //b.Result = RestoreResult.ERROR_COULD_NOT_CREATE_DIRECTORY;
                    return false;
                }

                if (useFullCopyMethod)
                {
                    RestoreUsingFullCopy(backupPath, destinationDirectory);
                }
                else
                {
                    RestoreUsingRoboCopy(backupPath, restoreTarget, backupStatus, destinationDirectory);
                }

                //Check for cmmvanilla file and remove it present

                string cmmVanilla = Path.Combine(destinationDirectory, @"cmm_vanilla");
                if (File.Exists(cmmVanilla))
                {
                    MLog.Information(@"Removing cmm_vanilla file");
                    File.Delete(cmmVanilla);
                }

                MLog.Information(@"Restore thread wrapping up");
                RestoreInProgress = false;

                BackupService.RefreshBackupStatus(game: Game);
                restoreTarget?.ReloadGameTarget(); // Reload target if we were passed in one.
                return true;
            }

            BackupService.RefreshBackupStatus(game: Game);
            RestoreInProgress = false;
            return false;
        }

        /// <summary>
        /// External setter for RestoreInProgress - used when errors may occur that stall this variable from being reset
        /// </summary>
        /// <param name="inProgress"></param>
        public void SetRestoreInProgress(bool inProgress)
        {
            RestoreInProgress = inProgress;
        }
        private void RestoreUsingRoboCopy(string backupPath, GameTarget destTarget, GameBackupStatus backupStatus, string destinationPathOverride = null)
        {
            var useTextureOptimized = UseOptimizedTextureRestore();
            var logEachFileCopied = ShouldLogEveryCopiedFile();
            if (destTarget != null && useTextureOptimized && destTarget.TextureModded)
            {
                MLog.Information(@"Using texture-modded restore method");
                backupStatus.BackupLocationStatus = LC.GetString(LC.string_analyzingGameFiles);
                SetProgressIndeterminateCallback?.Invoke(true);

                // Game is texture modded.
                var packagesToCheck = new List<string>();

                void addNonVanillaFile(string failedItem)
                {
                    if (failedItem.RepresentsPackageFilePath())
                        packagesToCheck.Add(failedItem);
                }

                VanillaDatabaseService.ValidateTargetAgainstVanilla(destTarget, addNonVanillaFile, false);

                // For each package that failed validation, we should check the size.
                backupStatus.BackupLocationStatus = LC.GetString(LC.string_checkingTexturetaggedPackages);
                UpdateStatusCallback?.Invoke(backupStatus.BackupLocationStatus);
                int numOnlyTexTagged = 0;
                SetProgressIndeterminateCallback?.Invoke(false);
                ProgressValue = 0;
                ProgressMax = packagesToCheck.Count;
                foreach (var fullPath in packagesToCheck)
                {
                    var relativePath = fullPath.Substring(destTarget.TargetPath.Length + 1);
                    var fi = new FileInfo(fullPath);
                    bool resetDate = false;
                    var vanillaInfos = VanillaDatabaseService.GetVanillaFileInfo(destTarget, relativePath);
                    if (vanillaInfos != null && vanillaInfos.Any(x => x.size == fi.Length - 24))
                    {
                        // Might just be MEMI tagged
                        using var fileStream = File.Open(fullPath, FileMode.Open);
                        fileStream.SeekEnd();
                        fileStream.Seek(-24, SeekOrigin.Current);
                        var tag = fileStream.ReadStringASCII(24);
                        if (tag == @"ThisIsMEMEndOfFileMarker")
                        {
                            fileStream.SetLength(fileStream.Length - 24); // Truncate
                            fileStream.Dispose();
                            fileStream.Close();
                            resetDate = true;
                        }
                    }

                    // This is done outside of the previous block to make the filestream be closed so it doesn't interfere with our operation
                    if (resetDate)
                    {
                        // Copy data over from backup so robocopy doesn't copy it.
                        MUtilities.CopyTimestamps(Path.Combine(backupPath, relativePath), fullPath);
                        numOnlyTexTagged++;
                    }
                    ProgressValue++;
                    UpdateProgressCallback?.Invoke(ProgressValue, ProgressMax);
                }
                MLog.Information(@"Texture-modded pre-restore has completed");

                Debug.WriteLine($@"Files only texture tagged: {numOnlyTexTagged}");
            }


            backupStatus.BackupStatus = LC.GetString(LC.string_restoringFromBackup);
            UpdateStatusCallback?.Invoke(backupStatus.BackupStatus);

            string currentRoboCopyFile = null;
            RoboCommand rc = new RoboCommand();
            rc.CopyOptions.Destination = destinationPathOverride ?? destTarget.TargetPath;
            rc.CopyOptions.Source = backupPath;
            rc.CopyOptions.Mirror = true;
            rc.CopyOptions.MultiThreadedCopiesCount = 2;
            rc.OnCopyProgressChanged += (sender, args) =>
            {
                SetProgressIndeterminateCallback?.Invoke(false);
                ProgressValue = (int)args.CurrentFileProgress;
                ProgressMax = 100;
                UpdateProgressCallback?.Invoke(ProgressValue, ProgressMax);
            };
            rc.OnFileProcessed += (sender, args) =>
            {
                if (args.ProcessedFile.Name.StartsWith(backupPath) && args.ProcessedFile.Name.Length > backupPath.Length)
                {
                    currentRoboCopyFile = args.ProcessedFile.Name.Substring(backupPath.Length + 1);
                    if (logEachFileCopied)
                    {
                        MLog.Debug($@"Robocopying {currentRoboCopyFile}");
                    }
                    backupStatus.BackupLocationStatus = LC.GetString(LC.string_interp_copyingX, currentRoboCopyFile);
                    UpdateStatusCallback?.Invoke(backupStatus.BackupLocationStatus);

                }
            };
            MLog.Information($@"Beginning robocopy restore: {backupPath} -> {rc.CopyOptions.Destination}");
            rc.Start().Wait();
            MLog.Information(@"Robocopy restore has completed");

        }

        private void RestoreUsingFullCopy(string backupPath, string destinationDirectory)
        {
            #region callbacks

            void fileCopiedCallback()
            {
                ProgressValue++;
                if (ProgressMax != 0)
                {
                    UpdateProgressCallback?.Invoke(ProgressValue, ProgressMax);
                }
            }

            string dlcFolderpath = MEDirectories.GetDLCPath(Game, backupPath) + Path.DirectorySeparatorChar; //\ at end makes sure we are restoring a subdir
            int dlcSubStringLen = dlcFolderpath.Length;
            //Debug.WriteLine(@"DLC Folder: " + dlcFolderpath);
            //Debug.Write(@"DLC Folder path len:" + dlcFolderpath);

            // Cached stuff to avoid hitting same codepath thousands of times
            var officialDLCNames = MEDirectories.OfficialDLCNames(Game);

            bool aboutToCopyCallback(string fileBeingCopied)
            {
                if (fileBeingCopied.Contains(@"\cmmbackup\")) return false; //do not copy cmmbackup files
                Debug.WriteLine(fileBeingCopied);
                if (fileBeingCopied.StartsWith(dlcFolderpath, StringComparison.InvariantCultureIgnoreCase))
                {
                    //It's a DLC!
                    string dlcname = fileBeingCopied.Substring(dlcSubStringLen);
                    int index = dlcname.IndexOf(Path.DirectorySeparatorChar);
                    if (index > 0) //Files directly in the DLC directory won't have path sep
                    {
                        try
                        {
                            dlcname = dlcname.Substring(0, index);
                            if (officialDLCNames.TryGetValue(dlcname, out var hrName))
                            {
                                UpdateStatusCallback?.Invoke(LC.GetString(LC.string_interp_restoringX, hrName));
                            }
                            else
                            {
                                UpdateStatusCallback?.Invoke(LC.GetString(LC.string_interp_restoringX, dlcname));
                            }
                        }
                        catch (Exception e)
                        {
                            TelemetryInterposer.TrackError(e, new Dictionary<string, string>()
                                    {
                                        {@"Source", @"Restore UI display callback"},
                                        {@"Value", fileBeingCopied},
                                        {@"DLC Folder path", dlcFolderpath}
                                    });
                        }
                    }
                }
                else
                {
                    //It's basegame
                    if (fileBeingCopied.EndsWith(@".bik"))
                    {
                        UpdateStatusCallback?.Invoke(LC.GetString(LC.string_restoringMovies));
                    }
                    else if (new FileInfo(fileBeingCopied).Length > 52428800)
                    {
                        UpdateStatusCallback?.Invoke(LC.GetString(LC.string_interp_restoringX, Path.GetFileName(fileBeingCopied)));
                    }
                    else
                    {
                        UpdateStatusCallback?.Invoke(LC.GetString(LC.string_restoringBasegame));
                    }
                }

                return true;
            }

            void totalFilesToCopyCallback(int total)
            {
                ProgressValue = 0;
                SetProgressIndeterminateCallback?.Invoke(false);
                ProgressMax = total;
            }

            void bigFileProgressCallback(string fileBeingCopied, long dataCopied, long totalDataToCopy)
            {
                if (fileBeingCopied.StartsWith(dlcFolderpath, StringComparison.InvariantCultureIgnoreCase))
                {
                    //It's a DLC!
                    string dlcname = fileBeingCopied.Substring(dlcSubStringLen);
                    int index = dlcname.IndexOf(Path.DirectorySeparatorChar);
                    try
                    {
                        string prefix = LC.GetString(LC.string_restoring) + @" ";
                        dlcname = dlcname.Substring(0, index);
                        if (officialDLCNames.TryGetValue(dlcname, out var hrName))
                        {
                            prefix += hrName;
                        }
                        else
                        {
                            prefix += dlcname;
                        }

                        UpdateStatusCallback?.Invoke($@"{prefix} {(int)(dataCopied * 100d / totalDataToCopy)}%");
                    }
                    catch
                    {

                    }
                }
                else
                {
                    UpdateStatusCallback?.Invoke(LC.GetString(LC.string_interp_restoringX, Path.GetFileName(fileBeingCopied), (int)(dataCopied * 100d / totalDataToCopy)));
                }
            }

            #endregion

            UpdateStatusCallback?.Invoke(LC.GetString(LC.string_calculatingHowManyFilesWillBeRestored));
            MLog.Information($@"Copying backup to game directory: {backupPath} -> {destinationDirectory}");
            CopyTools.CopyAll_ProgressBar(new DirectoryInfo(backupPath), new DirectoryInfo(destinationDirectory),
                totalItemsToCopyCallback: totalFilesToCopyCallback,
                aboutToCopyCallback: aboutToCopyCallback,
                fileCopiedCallback: fileCopiedCallback,
                ignoredExtensions: new[] { @"*.pdf", @"*.mp3", @"*.bak" },
                bigFileProgressCallback: bigFileProgressCallback);
            MLog.Information(@"Restore of game data has completed");
        }
    }

    #endregion
}
