﻿using Microsoft.Extensions.Logging;
using Netimobiledevice.Afc;
using Netimobiledevice.Diagnostics;
using Netimobiledevice.EndianBitConversion;
using Netimobiledevice.Exceptions;
using Netimobiledevice.InstallationProxy;
using Netimobiledevice.Lockdown;
using Netimobiledevice.NotificationProxy;
using Netimobiledevice.Plist;
using Netimobiledevice.SpringBoardServices;
using Netimobiledevice.Usbmuxd;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Netimobiledevice.Backup
{
    public class DeviceBackup : IDisposable
    {
        /// <summary>
        /// iTunes files to be inserted into the Info.plist file.
        /// </summary>
        private static readonly string[] iTunesFiles = new string[] {
            "ApertureAlbumPrefs",
            "IC-Info.sidb",
            "IC-Info.sidv",
            "PhotosFolderAlbums",
            "PhotosFolderName",
            "PhotosFolderPrefs",
            "VoiceMemos.plist",
            "iPhotoAlbumPrefs",
            "iTunesApplicationIDs",
            "iTunesPrefs",
            "iTunesPrefs.plist"
        };

        private CancellationTokenSource? internalCancellationTokenSource;

        /// <summary>
        /// The AFC service.
        /// </summary>
        private readonly AfcService afcService;
        /// <summary>
        /// The last backup status received.
        /// </summary>
        private BackupStatus? lastStatus;
        /// <summary>
        /// The Notification service.
        /// </summary>
        private NotificationProxyService? notificationProxyService;
        /// <summary>
        /// The current snapshot state for the backup.
        /// </summary>
        private SnapshotState snapshotState = SnapshotState.Uninitialized;
        /// <summary>
        /// The sync lock identifier.
        /// </summary>
        private ulong syncLock;
        /// <summary>
        /// Indicates whether the device was disconnected during the backup process.
        /// </summary>
        protected bool deviceDisconnected;
        /// <summary>
        /// The backup service.
        /// </summary>
        protected Mobilebackup2Service? mobilebackup2Service;
        /// <summary>
        /// The exception that caused the backup to fail.
        /// </summary>
        protected Exception? terminatingException;
        /// <summary>
        /// Indicates whether the user cancelled the backup process.
        /// </summary>
        protected bool userCancelled;
        /// <summary>
        /// A list of the files whose transfer failed due to a device error.
        /// </summary>
        protected readonly List<BackupFile> failedFiles = new List<BackupFile>();

        protected bool IsFinished { get; set; }
        /// <summary>
        /// The Lockdown client.
        /// </summary>
        private LockdownClient LockdownClient { get; }
        /// <summary>
        /// The flag for cancelling the backup process.
        /// </summary>
        protected bool IsCancelling { get; set; }
        /// <summary>
        /// Indicates whether the backup is encrypted.
        /// </summary>
        public bool IsEncrypted { get; protected set; }
        public bool IsStopping => IsCancelling || IsFinished;
        /// <summary>
        /// The path to the backup folder, without the device UDID.
        /// </summary>
        public string BackupDirectory { get; }
        /// <summary>
        /// The path to the backup folder, including the device UDID.
        /// </summary>
        public string DeviceBackupPath { get; }
        /// <summary>
        /// Indicates whether the backup is currently in progress.
        /// </summary>
        public bool InProgress { get; protected set; }
        /// <summary>
        /// Indicates the backup progress, in a 0 to 100,000 range (in order to obtain a smoother integer progress).
        /// </summary>
        public double ProgressPercentage { get; protected set; }
        /// <summary>
        /// The time at which the backup started.
        /// </summary>
        public DateTime StartTime { get; protected set; }

        /// <summary>
        /// Event raised when a file is about to be transferred from the device.
        /// </summary>
        public event EventHandler<BackupFileEventArgs>? BeforeReceivingFile;
        /// <summary>
        /// Event raised when the backup finishes.
        /// </summary>
        public event EventHandler<BackupResultEventArgs>? Completed;
        /// <summary>
        /// Event raised when there is some error during the backup.
        /// </summary>
        public event EventHandler<ErrorEventArgs>? Error;
        /// <summary>
        /// Event raised when a file is received from the device.
        /// </summary>
        public event EventHandler<BackupFileEventArgs>? FileReceived;
        /// <summary>
        /// Event raised when a part of a file has been received from the device.
        /// </summary>
        public event EventHandler<BackupFileEventArgs>? FileReceiving;
        /// <summary>
        /// Event raised when a file transfer has failed due an internal device error.
        /// </summary>
        public event EventHandler<BackupFileErrorEventArgs>? FileTransferError;
        /// <summary>
        /// Event raised when the device requires a passcode to start the backup
        /// </summary>
        public event EventHandler? PasscodeRequiredForBackup;
        /// <summary>
        /// Event raised for signaling the backup progress.
        /// </summary>
        public event ProgressChangedEventHandler? Progress;
        /// <summary>
        /// Event raised when the backup started.
        /// </summary>
        public event EventHandler? Started;
        /// <summary>
        /// Event raised for signaling different kinds of the backup status.
        /// </summary>
        public event EventHandler<StatusEventArgs>? Status;

        /// <summary>
        /// Creates an instance of a BackupJob class.
        /// </summary>
        /// <param name="lockdown">The lockdown client for the device that will be backed-up.</param>
        /// <param name="backupFolder">The folder to store the backup data. Without the device UDID.</param>
        /// <param name="logger">The logger to handle and log messages output</param>
        public DeviceBackup(LockdownClient lockdown, string backupFolder)
        {
            LockdownClient = lockdown;
            BackupDirectory = backupFolder;
            DeviceBackupPath = Path.Combine(BackupDirectory, lockdown.Udid);

            afcService = new AfcService(LockdownClient);
        }

        /// <summary>
        /// Destructor of the BackupJob class.
        /// </summary>
        ~DeviceBackup()
        {
            Dispose();
        }

        private async Task AquireBackupLock(CancellationToken cancellationToken)
        {
            notificationProxyService?.Post(SendableNotificaton.SyncWillStart);
            syncLock = await afcService.FileOpen("/com.apple.itunes.lock_sync", cancellationToken, AfcFileOpenMode.ReadWrite).ConfigureAwait(false);

            if (syncLock != 0) {
                notificationProxyService?.Post(SendableNotificaton.SyncLockRequest);
                for (int i = 0; i < 50; i++) {
                    bool lockAquired = false;
                    try {
                        await afcService.Lock(syncLock, AfcLockModes.ExclusiveLock, cancellationToken).ConfigureAwait(false);
                        lockAquired = true;
                    }
                    catch (AfcException e) {
                        if (e.AfcError == AfcError.OpWouldBlock) {
                            await Task.Delay(200, cancellationToken).ConfigureAwait(false);
                        }
                        else {
                            await afcService.FileClose(syncLock, cancellationToken).ConfigureAwait(false);
                            throw;
                        }
                    }
                    catch (Exception) {
                        throw;
                    }

                    if (lockAquired) {
                        notificationProxyService?.Post(SendableNotificaton.SyncDidStart);
                        break;
                    }
                }
            }
            else {
                // Lock failed
                await afcService.FileClose(syncLock, cancellationToken).ConfigureAwait(false);
                throw new AfcException("Failed to lock iTunes backup sync file");
            }
        }

        /// <summary>
        /// Cleans the used resources.
        /// </summary>
        private async Task CleanResources(CancellationToken cancellationToken)
        {
            if (InProgress) {
                IsCancelling = true;
            }

            try {
                await Unlock(cancellationToken).ConfigureAwait(false);
            }
            catch (ObjectDisposedException) {
                LockdownClient.Logger.LogDebug("Object already disposed so I assume we can just continue");
            }
            catch (IOException) {
                LockdownClient.Logger.LogDebug("Had an IO exception but I assume we can just continue");
            }

            notificationProxyService?.Dispose();
            mobilebackup2Service?.Dispose();
            afcService.Dispose();
            InProgress = false;
        }

        /// <summary>
        /// Backup creation task entry point.
        /// </summary>
        private async Task CreateBackup(CancellationToken cancellationToken)
        {
            LockdownClient.Logger.LogInformation("Starting backup of device {productVersion} v{osVersion}", LockdownClient.GetValue("ProductType")?.AsStringNode().Value, LockdownClient.OsVersion);

            // Reset everything in case we have called this more than once.
            lastStatus = null;
            InProgress = true;
            IsCancelling = false;
            IsFinished = false;
            userCancelled = false;
            deviceDisconnected = false;
            StartTime = DateTime.Now;
            ProgressPercentage = 0.0;
            terminatingException = null;
            snapshotState = SnapshotState.Uninitialized;

            LockdownClient.Logger.LogDebug("Saving at {DeviceBackupPath}", DeviceBackupPath);

            IsEncrypted = LockdownClient.GetValue("com.apple.mobile.backup", "WillEncrypt")?.AsBooleanNode().Value ?? false;
            LockdownClient.Logger.LogInformation("The backup will{encyption} be encrypted.", IsEncrypted ? string.Empty : " not");

            try {
                mobilebackup2Service = await Mobilebackup2Service.CreateAsync(LockdownClient, cancellationToken);
                notificationProxyService = new NotificationProxyService(LockdownClient);

                await AquireBackupLock(cancellationToken).ConfigureAwait(false);

                OnStatus("Initializing backup ...");
                DictionaryNode options = CreateBackupOptions();
                mobilebackup2Service.SendRequest("Backup", LockdownClient.Udid, LockdownClient.Udid, options);

                if (IsPasscodeRequiredBeforeBackup()) {
                    PasscodeRequiredForBackup?.Invoke(this, EventArgs.Empty);
                }

                await MessageLoop(cancellationToken);
            }
            catch (Exception ex) {
                OnError(ex);
                return;
            }
        }

        /// <summary>
        /// Creates the Info.plist dictionary.
        /// </summary>
        /// <returns>The created Info.plist as a DictionaryNode.</returns>
        private async Task<DictionaryNode> CreateInfoPlist(CancellationToken cancellationToken)
        {
            DictionaryNode info = new DictionaryNode();

            (DictionaryNode appDict, ArrayNode installedApps) = await CreateInstalledAppList();
            info.Add("Applications", appDict);

            DictionaryNode? rootNode = LockdownClient.GetValue()?.AsDictionaryNode();
            if (rootNode != null) {
                info.Add("Build Version", rootNode["BuildVersion"]);
                info.Add("Device Name", rootNode["DeviceName"]);
                info.Add("Display Name", rootNode["DeviceName"]);
                info.Add("GUID", new StringNode(Guid.NewGuid().ToString()));

                if (rootNode.ContainsKey("IntegratedCircuitCardIdentity")) {
                    info.Add("ICCID", rootNode["IntegratedCircuitCardIdentity"]);
                }
                if (rootNode.ContainsKey("InternationalMobileEquipmentIdentity")) {
                    info.Add("IMEI", rootNode["InternationalMobileEquipmentIdentity"]);
                }

                info.Add("Installed Applications", installedApps);
                info.Add("Last Backup Date", new DateNode(StartTime));

                if (rootNode.ContainsKey("MobileEquipmentIdentifier")) {
                    info.Add("MEID", rootNode["MobileEquipmentIdentifier"]);
                }
                if (rootNode.ContainsKey("PhoneNumber")) {
                    info.Add("Phone Number", rootNode["PhoneNumber"]);
                }

                info.Add("Product Type", rootNode["ProductType"]);
                info.Add("Product Version", rootNode["ProductVersion"]);
                info.Add("Serial Number", rootNode["SerialNumber"]);

                info.Add("Target Identifier", new StringNode(LockdownClient.Udid.ToUpperInvariant()));
                info.Add("Target Type", new StringNode("Device"));
                info.Add("Unique Identifier", new StringNode(LockdownClient.Udid.ToUpperInvariant()));
            }

            try {
                byte[] dataBuffer = await afcService.GetFileContents("/Books/iBooksData2.plist", cancellationToken).ConfigureAwait(false) ?? Array.Empty<byte>();
                info.Add("iBooks Data 2", new DataNode(dataBuffer));
            }
            catch (AfcException ex) {
                if (ex.AfcError != AfcError.ObjectNotFound) {
                    throw;
                }
            }

            DictionaryNode files = new DictionaryNode();
            foreach (string iTuneFile in iTunesFiles) {
                try {
                    string filePath = Path.Combine("/iTunes_Control/iTunes", iTuneFile);
                    byte[] dataBuffer = await afcService.GetFileContents(filePath, cancellationToken).ConfigureAwait(false) ?? Array.Empty<byte>();
                    files.Add(iTuneFile, new DataNode(dataBuffer));
                }
                catch (AfcException ex) {
                    if (ex.AfcError == AfcError.ObjectNotFound) {
                        continue;
                    }
                    else {
                        throw;
                    }
                }
            }
            info.Add("iTunes Files", files);

            PropertyNode? itunesSettings = LockdownClient.GetValue("com.apple.iTunes", null);
            info.Add("iTunes Settings", itunesSettings ?? new DictionaryNode());

            // If we don't have iTunes, then let's get the minimum required iTunes version from the device
            PropertyNode? minItunesVersion = LockdownClient.GetValue("com.apple.mobile.iTunes", "MinITunesVersion");
            info.Add("iTunes Version", minItunesVersion ?? new StringNode("10.0.1"));

            return info;
        }

        /// <summary>
        /// Creates the application array and dictionary for the Info.plist file.
        /// </summary>
        /// <returns>The application dictionary and array of applications bundle ids.</returns>
        private async Task<(DictionaryNode, ArrayNode)> CreateInstalledAppList()
        {
            DictionaryNode appDict = new DictionaryNode();
            ArrayNode installedApps = new ArrayNode();

            using (InstallationProxyService installationProxyService = new InstallationProxyService(LockdownClient)) {
                using (SpringBoardServicesService springBoardServicesService = new SpringBoardServicesService(LockdownClient)) {
                    try {
                        ArrayNode apps = await installationProxyService.Browse(
                        new DictionaryNode() { { "ApplicationType", new StringNode("User") } },
                        new ArrayNode() { new StringNode("CFBundleIdentifier"), new StringNode("ApplicationSINF"), new StringNode("iTunesMetadata") });
                        foreach (DictionaryNode app in apps.Cast<DictionaryNode>()) {
                            if (app.ContainsKey("CFBundleIdentifier")) {
                                StringNode bundleId = app["CFBundleIdentifier"].AsStringNode();
                                installedApps.Add(bundleId);
                                if (app.ContainsKey("iTunesMetadata") && app.ContainsKey("ApplicationSINF")) {
                                    appDict.Add(bundleId.Value, new DictionaryNode() {
                                        { "ApplicationSINF", app["ApplicationSINF"] },
                                        { "iTunesMetadata", app["iTunesMetadata"] },
                                        { "PlaceholderIcon", springBoardServicesService.GetIconPNGData(bundleId.Value) },
                                    });
                                }
                            }
                        }
                    }
                    catch (Exception ex) {
                        LockdownClient.Logger.LogWarning(ex, "Failed to create application list for Info.plist");
                    }
                }
            }
            return (appDict, installedApps);
        }

        private bool IsPasscodeRequiredBeforeBackup()
        {
            // iOS versions 15.7.1 and anything 16.1 or newer will require you to input a passcode before
            // it can start a backup so we make sure to notify the user about this.
            if ((LockdownClient.OsVersion >= new Version(15, 7, 1) && LockdownClient.OsVersion < new Version(16, 0)) ||
                LockdownClient.OsVersion >= new Version(16, 1)) {
                using (DiagnosticsService diagnosticsService = new DiagnosticsService(LockdownClient)) {
                    string queryString = "PasswordConfigured";
                    try {
                        DictionaryNode queryResponse = diagnosticsService.MobileGestalt(new List<string>() { queryString });
                        if (queryResponse.TryGetValue(queryString, out PropertyNode? passcodeSetNode)) {
                            bool passcodeSet = passcodeSetNode.AsBooleanNode().Value;
                            if (passcodeSet) {
                                return true;
                            }
                        }
                    }
                    catch (DeprecatedException) {
                        // Assume that the passcode is set for now
                        // TODO Try and find a new way to tell if the devices passcode is set 
                        return true;
                    }
                }
            }
            return false;
        }

        /// <summary>
        /// The main loop for processing messages from the device.
        /// </summary>
        private async Task MessageLoop(CancellationToken cancellationToken)
        {
            bool isFirstMessage = true;

            LockdownClient.Logger.LogDebug("Starting the backup message loop.");
            while (!IsStopping) {
                try {
                    if (mobilebackup2Service != null) {
                        ArrayNode msg = await mobilebackup2Service.ReceiveMessage(cancellationToken);
                        if (msg != null) {
                            // Reset waiting state
                            if (snapshotState == SnapshotState.Waiting) {
                                OnSnapshotStateChanged(snapshotState, snapshotState = lastStatus?.SnapshotState ?? SnapshotState.Waiting);
                            }

                            // If it's the first message that isn't null report that the backup is started
                            if (isFirstMessage) {
                                OnBackupStarted();
                                await SaveInfoPropertyList(cancellationToken);
                                isFirstMessage = false;
                            }

                            try {
                                await mobilebackup2Service.OnDeviceLinkMessageReceived(msg, msg[0].AsStringNode().Value, BackupDirectory, cancellationToken);
                            }
                            catch (Exception ex) {
                                OnError(ex);
                            }
                        }
                        else if (!Usbmux.IsDeviceConnected(LockdownClient.Udid)) {
                            throw new DeviceDisconnectedException();
                        }
                    }
                }
                catch (TimeoutException) {
                    OnSnapshotStateChanged(snapshotState, SnapshotState.Waiting);
                    OnStatus("Waiting for device to be ready ...");
                    await Task.Delay(100, cancellationToken);
                }
                catch (Exception ex) {
                    LockdownClient.Logger.LogError($"Issue receiving message: {ex.Message}");
                    OnError(ex);
                    break;
                }
            }

            // Check if the execution arrived here due to a device disconnection, but skip if the scan has finished
            if (!IsFinished) {
                if (terminatingException == null || !Usbmux.IsDeviceConnected(LockdownClient.Udid)) {
                    throw new DeviceDisconnectedException();
                }
            }

            LockdownClient.Logger.LogInformation($"Finished message loop. Cancelling = {IsCancelling}, Finished = {IsFinished}, Errored = {terminatingException != null}");
            OnBackupCompleted();
        }

        private void OnProcessMessage(ArrayNode msg)
        {
            int resultCode = ProcessMessage(msg);
            switch (resultCode) {
                case 0: {
                    IsFinished = true;
                    break;
                }
                case -38: {
                    OnError(new Exception("Backing up the phone is denied by managing organisation"));
                    break;
                }
                case -207: {
                    OnError(new Exception("No backup encryption password set but is required by managing organisation"));
                    break;
                }
                case -208: {
                    // Device locked which most commonly happens when requesting a backup but the user either
                    // hit cancel or the screen turned off again locking the phone and cancelling the backup.
                    OnError(new Exception($"Device locked - {msg[1].AsDictionaryNode()["ErrorDescription"].AsStringNode().Value}"));
                    break;
                }
                default: {
                    LockdownClient.Logger.LogError($"Issue with OnProcessMessage: {resultCode}");
                    DictionaryNode msgDict = msg[1].AsDictionaryNode();
                    if (msgDict.TryGetValue("ErrorDescription", out PropertyNode? errDescription)) {
                        throw new Exception($"Error {resultCode}: {errDescription.AsStringNode().Value}");
                    }
                    else {
                        throw new Exception($"Error {resultCode}");
                    }
                }
            }
        }

        /// <summary>
        /// Processes a message response received from the backup service.
        /// </summary>
        /// <param name="msg">The message received.</param>
        /// <returns>The result status code from the message.</returns>
        private int ProcessMessage(ArrayNode msg)
        {
            DictionaryNode tmp = msg[1].AsDictionaryNode();
            int errorCode = (int) tmp["ErrorCode"].AsIntegerNode().Value;
            string errorDescription = tmp["ErrorDescription"].AsStringNode().Value;
            if (errorCode != 0) {
                LockdownClient.Logger.LogError($"ProcessMessage Code: {errorCode} {errorDescription}");
            }
            return -errorCode;
        }

        /// <summary>
        /// Reads the information of the next file that the backup service will send.
        /// </summary>
        /// <returns>Returns the file information of the next file to download, or null if there are no more files to download.</returns>
        private BackupFile? ReceiveBackupFile()
        {
            int len = ReceiveFilename(out string devicePath);
            if (len == 0) {
                return null;
            }
            len = ReceiveFilename(out string backupPath);
            if (len <= 0) {
                LockdownClient.Logger.LogWarning("Error reading backup file path.");
            }
            return new BackupFile(devicePath, backupPath, BackupDirectory);
        }

        /// <summary>
        /// Reads a filename from the backup service stream.
        /// </summary>
        /// <param name="filename">The filename read from the backup stream, or NULL if there are no more files.</param>
        /// <returns>The length of the filename read.</returns>
        private int ReceiveFilename(out string filename)
        {
            filename = string.Empty;
            int len = ReadInt32();

            // A zero length means no more files to receive.
            if (len != 0) {
                byte[] buffer = mobilebackup2Service?.ReceiveRaw(len) ?? Array.Empty<byte>();
                filename = Encoding.UTF8.GetString(buffer);
            }
            return len;
        }

        private ResultCode ReadCode()
        {
            byte[] buffer = mobilebackup2Service?.ReceiveRaw(1) ?? Array.Empty<byte>();

            byte code = buffer[0];
            if (!Enum.IsDefined(typeof(ResultCode), code)) {
                LockdownClient.Logger.LogWarning($"New backup code found: {code}");
            }
            ResultCode result = (ResultCode) code;

            return result;
        }

        /// <summary>
        /// Reads an Int32 value from the backup service.
        /// </summary>
        /// <returns>The Int32 value read.</returns>
        private int ReadInt32()
        {
            byte[] buffer = mobilebackup2Service?.ReceiveRaw(4) ?? Array.Empty<byte>();
            if (buffer.Length > 0) {
                return EndianBitConverter.BigEndian.ToInt32(buffer, 0);
            }
            return -1;
        }

        /// <summary>
        /// Unlocks the sync file.
        /// </summary>
        private async Task Unlock(CancellationToken cancellationToken)
        {
            if (syncLock != 0) {
                await afcService.Lock(syncLock, AfcLockModes.Unlock, cancellationToken).ConfigureAwait(false);
                syncLock = 0;
            }
        }

        /// <summary>
        /// Creates a dictionary with the options for the backup.
        /// </summary>
        /// <returns>A PropertyDict containing the backup options.</returns>
        protected virtual DictionaryNode CreateBackupOptions()
        {
            DictionaryNode options = new DictionaryNode {
                { "ForceFullBackup", new BooleanNode(true) }
            };
            return options;
        }

        /// <summary>
        /// Invoke the FileReceived event
        /// </summary>
        /// <param name="eventArgs">The BackupFileEventArgs for the file receiving event</param>
        protected void InvokeOnFileReceived(BackupFileEventArgs eventArgs)
        {
            FileReceived?.Invoke(this, eventArgs);
        }

        /// <summary>
        /// Invoke the FileReceiving event
        /// </summary>
        /// <param name="eventArgs">The BackupFileEventArgs for the file receiving event</param>
        protected void InvokeOnFileReceiving(BackupFileEventArgs eventArgs)
        {
            FileReceiving?.Invoke(this, eventArgs);
        }

        /// <summary>
        /// Event handler called when the backup is completed.
        /// </summary>
        protected virtual void OnBackupCompleted()
        {
            LockdownClient.Logger.LogInformation("Device Backup Completed");
            Completed?.Invoke(this, new BackupResultEventArgs(failedFiles, userCancelled, deviceDisconnected));
        }

        /// <summary>
        /// Event handler called to report progress.
        /// </summary>
        /// <param name="filename">The filename related to the progress.</param>
        protected virtual void OnBackupProgress()
        {
            Progress?.Invoke(this, new ProgressChangedEventArgs((int) ProgressPercentage, null));
        }

        /// <summary>
        /// Event handler called when the backup has actually started.
        /// </summary>
        protected virtual void OnBackupStarted()
        {
            notificationProxyService?.Post(SendableNotificaton.SyncDidStart);
            Started?.Invoke(this, EventArgs.Empty);
        }

        /// <summary>
        /// Event handler called before a file is to be received from the device.
        /// </summary>
        /// <param name="file">The file to be received.</param>
        protected virtual void OnBeforeReceivingFile(BackupFile file)
        {
            BeforeReceivingFile?.Invoke(this, new BackupFileEventArgs(file));
        }

        /// <summary>
        /// Manages the CopyItem device message.
        /// </summary>
        /// <param name="msg">The message received from the device.</param>
        /// <returns>The errno result of the operation.</returns>
        protected virtual void OnCopyItem(ArrayNode msg)
        {
            int errorCode = 0;
            string errorDesc = string.Empty;
            string srcPath = Path.Combine(BackupDirectory, msg[1].AsStringNode().Value);
            string dstPath = Path.Combine(BackupDirectory, msg[2].AsStringNode().Value);

            FileInfo source = new FileInfo(srcPath);
            if (source.Attributes.HasFlag(FileAttributes.Directory)) {
                LockdownClient.Logger.LogError($"Trying to coppy a whole directory rather than an individual file");
            }
            else {
                File.Copy(source.FullName, new FileInfo(dstPath).FullName);
            }
            // TODO mobilebackup2Service?.SendStatusReport(errorCode, errorDesc);
        }

        /// <summary>
        /// Event handler called when a terminating error happens during the backup.
        /// </summary>
        /// <param name="ex"></param>
        protected virtual void OnError(Exception ex)
        {
            LockdownClient.Logger.LogError(ex, "Error in backup job");

            IsCancelling = true;
            internalCancellationTokenSource?.Cancel();

            deviceDisconnected = Usbmux.IsDeviceConnected(LockdownClient.Udid);
            terminatingException = deviceDisconnected ? ex : new DeviceDisconnectedException();
            Error?.Invoke(this, new ErrorEventArgs(terminatingException));
        }

        /// <summary>
        /// Event handler called after a file has been received from the device.
        /// </summary>
        /// <param name="file">The file received.</param>
        protected virtual void OnFileReceived(BackupFile file)
        {
            FileReceived?.Invoke(this, new BackupFileEventArgs(file));
            if (string.Equals("Status.plist", Path.GetFileName(file.LocalPath), StringComparison.OrdinalIgnoreCase)) {
                using (FileStream fs = File.OpenRead(file.LocalPath)) {
                    DictionaryNode statusPlist = PropertyList.Load(fs).AsDictionaryNode();
                    OnStatusReceived(new BackupStatus(statusPlist, LockdownClient.Logger));
                }
            }
        }

        /// <summary>
        /// Event handler called after a part (or all of) a file has been sent from the device from the device.
        /// </summary>
        /// <param name="file">The file received.</param>
        /// <param name="fileData">The file contents received</param>
        protected virtual void OnFileReceiving(BackupFile file, byte[] fileData)
        {
            InvokeOnFileReceiving(new BackupFileEventArgs(file, fileData));

            // Ensure the directory requested exists before writing to it.
            string? pathDir = Path.GetDirectoryName(file.LocalPath);
            if (!string.IsNullOrWhiteSpace(pathDir) && !Directory.Exists(file.LocalPath)) {
                Directory.CreateDirectory(pathDir);
            }

            using (FileStream stream = File.OpenWrite(file.LocalPath)) {
                stream.Seek(0, SeekOrigin.End);
                stream.Write(fileData, 0, fileData.Length);
            }
        }

        /// <summary>
        /// Event handler called after a file transfer failed due to a device error.
        /// </summary>
        /// <param name="file">The file whose tranfer failed.</param>
        protected virtual void OnFileTransferError(BackupFile file)
        {
            failedFiles.Add(file);
            if (FileTransferError != null) {
                BackupFileErrorEventArgs e = new BackupFileErrorEventArgs(file);
                FileTransferError.Invoke(this, e);
                IsCancelling = e.Cancel;
            }
        }

        /// <summary>
        /// Manages the RemoveItems device message.
        /// </summary>
        /// <param name="msg">The message received from the device.</param>
        /// <returns>The number of items removed.</returns>
        protected virtual void OnRemoveItems(ArrayNode msg)
        {
            UpdateProgressForMessage(msg, 3);

            int errorCode = 0;
            string errorDesc = string.Empty;
            ArrayNode removes = msg[1].AsArrayNode();
            foreach (StringNode filename in removes.Cast<StringNode>()) {
                if (IsStopping) {
                    break;
                }

                if (string.IsNullOrEmpty(filename.Value)) {
                    LockdownClient.Logger.LogWarning("Empty file to remove.");
                }
                else {
                    FileInfo file = new FileInfo(Path.Combine(BackupDirectory, filename.Value));
                    if (file.Exists) {
                        if (file.Attributes.HasFlag(FileAttributes.Directory)) {
                            Directory.Delete(file.FullName, true);
                        }
                        else {
                            file.Delete();
                        }
                    }
                }
            }

            if (!IsStopping) {
                // TODO mobilebackup2Service?.SendStatusReport(errorCode, errorDesc);
            }
        }

        /// <summary>
        /// Event handler called when the snapshot state of the backup changes.
        /// </summary>
        /// <param name="oldSnapshotState">The previous snapshot state.</param>
        /// <param name="newSnapshotState">The new snapshot state.</param>
        protected virtual void OnSnapshotStateChanged(SnapshotState oldSnapshotState, SnapshotState newSnapshotState)
        {
            LockdownClient.Logger.LogDebug("Snapshot state changed: {newSnapshotState}", newSnapshotState);
            OnStatus($"{newSnapshotState} ...");
            if (newSnapshotState == SnapshotState.Finished) {
                IsFinished = true;
            }
        }

        /// <summary>
        /// Event handler called to report a status messages.
        /// </summary>
        /// <param name="message">The status message to report.</param>
        protected virtual void OnStatus(string message)
        {
            Status?.Invoke(this, new StatusEventArgs(message));
            LockdownClient.Logger.LogDebug("OnStatus: {message}", message);
        }

        /// <summary>
        /// Event handler called each time the backup service sends a status report.
        /// </summary>
        /// <param name="status">The status report sent from the backup service.</param>
        protected virtual void OnStatusReceived(BackupStatus status)
        {
            lastStatus = status;
            if (snapshotState != status.SnapshotState) {
                OnSnapshotStateChanged(snapshotState, snapshotState = status.SnapshotState);
            }
        }

        /// <summary>
        /// Receives a single file from the device.
        /// </summary>
        /// <param name="file">The BackupFile to receive.</param>
        /// <param name="totalSize">The total size indicated in the device message.</param>
        /// <param name="realSize">The actual bytes transferred.</param>
        /// <param name="skip">Indicates whether to skip or save the file.</param>
        /// <returns>The result code of the transfer.</returns>
        protected virtual ResultCode ReceiveFile(BackupFile file, long totalSize, ref long realSize, bool skip = false)
        {
            const int bufferLen = 32 * 1024;
            ResultCode lastCode = ResultCode.Success;
            if (File.Exists(file.LocalPath)) {
                File.Delete(file.LocalPath);
            }
            while (!IsStopping) {
                // Size is the number of bytes left to read
                int size = ReadInt32();
                if (size <= 0) {
                    break;
                }

                ResultCode code = ReadCode();
                int blockSize = size - sizeof(ResultCode);
                if (code != ResultCode.FileData) {
                    if (code == ResultCode.Success) {
                        return code;
                    }

                    string msg = string.Empty;
                    if (blockSize > 0) {
                        byte[] msgBuffer = mobilebackup2Service?.ReceiveRaw(blockSize) ?? Array.Empty<byte>();
                        msg = Encoding.UTF8.GetString(msgBuffer);
                    }

                    // iOS 17 beta devices seem to give RemoteError for a fair number of file now?
                    LockdownClient.Logger.LogWarning("Failed to fully upload {localPath}. Device file name {devicePath}. Reason: {msg}", file.LocalPath, file.DevicePath, msg);

                    OnFileTransferError(file);
                    return code;
                }
                lastCode = code;

                int done = 0;
                while (done < blockSize) {
                    int toRead = Math.Min(blockSize - done, bufferLen);
                    byte[] buffer = mobilebackup2Service?.ReceiveRaw(toRead) ?? Array.Empty<byte>();
                    if (!skip) {
                        OnFileReceiving(file, buffer);
                    }
                    done += buffer.Length;
                }
                if (done == blockSize) {
                    realSize += blockSize;
                }
            }

            return lastCode;
        }

        /// <summary>
        /// Generates and saves the backup Info.plist file.
        /// </summary>
        protected virtual async Task SaveInfoPropertyList(CancellationToken cancellationToken)
        {
            OnStatus("Creating Info.plist");
            BackupFile backupFile = new BackupFile(string.Empty, $"Info.plist", DeviceBackupPath);

            DateTime startTime = DateTime.Now;

            PropertyNode infoPlist = await CreateInfoPlist(cancellationToken).ConfigureAwait(false);
            byte[] infoPlistData = PropertyList.SaveAsByteArray(infoPlist, PlistFormat.Xml);
            OnFileReceiving(backupFile, infoPlistData);

            TimeSpan elapsed = DateTime.Now - startTime;
            LockdownClient.Logger.LogDebug("Creating Info.plist took {elapsed}", elapsed);

            OnFileReceived(backupFile);
        }

        /// <summary>
        /// Updates the backup progress as signaled by the backup service.
        /// </summary>
        /// <param name="msg">The message received containing the progress information.</param>
        /// <param name="index">The index of the element in the array that contains the progress value.</param>
        protected void UpdateProgressForMessage(ArrayNode msg, int index)
        {
            double progress = msg[index].AsRealNode().Value;
            if (progress > 0.0) {
                ProgressPercentage = progress;
                OnBackupProgress();
            }
        }

        /// <summary>
        /// Disposes the used resources.
        /// </summary>
        public void Dispose()
        {
            CleanResources(CancellationToken.None).GetAwaiter().GetResult();
            GC.SuppressFinalize(this);
        }

        /// <summary>
        /// Starts the backup process.
        /// </summary>
        public async Task Start(CancellationToken cancellationToken = default)
        {
            internalCancellationTokenSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            if (!InProgress) {
                await CreateBackup(internalCancellationTokenSource.Token);
            }
        }

        /// <summary>
        /// Stops the backup process.
        /// </summary>
        public void Stop()
        {
            if (InProgress && !IsStopping) {
                IsCancelling = true;
                userCancelled = true;
                internalCancellationTokenSource?.Cancel();
            }
        }
    }
}
