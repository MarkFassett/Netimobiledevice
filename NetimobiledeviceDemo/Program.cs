﻿using Microsoft.Extensions.Logging;
using Netimobiledevice;
using Netimobiledevice.Afc;
using Netimobiledevice.Backup;
using Netimobiledevice.Diagnostics;
using Netimobiledevice.Exceptions;
using Netimobiledevice.Heartbeat;
using Netimobiledevice.InstallationProxy;
using Netimobiledevice.Lockdown;
using Netimobiledevice.Lockdown.Pairing;
using Netimobiledevice.Misagent;
using Netimobiledevice.Plist;
using Netimobiledevice.Remoted;
using Netimobiledevice.Remoted.Tunnel;
using Netimobiledevice.SpringBoardServices;
using Netimobiledevice.Usbmuxd;

namespace NetimobiledeviceDemo;

public class Program
{
    private static readonly CancellationTokenSource tokenSource = new CancellationTokenSource();

    internal static async Task Main()
    {
        using ILoggerFactory factory = LoggerFactory.Create(builder => builder.SetMinimumLevel(LogLevel.Debug).AddConsole());
        ILogger logger = factory.CreateLogger("NetimobiledeviceDemo");
        logger.LogInformation("Hello World! Logging is {Description}.", "fun");

        TaskScheduler.UnobservedTaskException += (sender, args) => {
            Console.WriteLine($"UnobservedTaskException error: {args.Exception}");
        };

        Console.CancelKeyPress += (sender, eventArgs) => {
            Console.WriteLine("Cancellation requested...");
            tokenSource.Cancel();
            // Prevent the process from terminating immediately
            eventArgs.Cancel = true;
        };
        Console.WriteLine("Press Ctrl+C to cancel the operation.");

        List<UsbmuxdDevice> devices = Usbmux.GetDeviceList();

        if (devices.Count == 0) {
            logger.LogError("No device is connected to the system.");
            return;
        }

        logger.LogDebug("There's {deviceCount} devices connected", devices.Count);
        foreach (UsbmuxdDevice device in devices) {
            Console.WriteLine($"Device found: {device.DeviceId} - {device.Serial}");
        }

        // Connect via usbmuxd
        using (UsbmuxLockdownClient lockdown = MobileDevice.CreateUsingUsbmux(logger: logger)) {
            using (CrashReportsService crs = new CrashReportsService(lockdown)) {
                if (Directory.Exists("CrashDir")) {
                    Directory.Delete("CrashDir", true);
                }

                List<string> crashList = await crs.GetCrashReportsList();
                await crs.GetCrashReport("CrashDir");
            }
        }

        using (LockdownClient lockdown = MobileDevice.CreateUsingUsbmux(logger: logger)) {
            Progress<PairingState> progress = new();
            progress.ProgressChanged += Progress_ProgressChanged;
            if (!lockdown.IsPaired) {
                await lockdown.PairAsync(progress);
            }
        }

        using (LockdownClient lockdown = MobileDevice.CreateUsingUsbmux(logger: logger)) {
            using (HeartbeatService heartbeatService = new HeartbeatService(lockdown)) {
                heartbeatService.Start();
                await Task.Delay(10000);
            }

            using (OsTraceService osTrace = new OsTraceService(lockdown)) {
                int counter = 0;
                foreach (SyslogEntry entry in osTrace.WatchSyslog()) {
                    Console.WriteLine($"[{entry.Level}] {entry.Timestamp} {entry.Label?.Subsystem} - {entry.Message}");
                    if (counter >= 100) {
                        break;
                    }
                    counter++;
                }
            }

            await Task.Delay(1000);

            using (DiagnosticsService diagnosticsService = new DiagnosticsService(lockdown)) {
                try {
                    Dictionary<string, ulong> storageInfo = diagnosticsService.GetStorageDetails();
                    ulong totalDiskValue = 0;
                    storageInfo?.TryGetValue("TotalDiskCapacity", out totalDiskValue);
                    logger.LogInformation("Total disk capacity in bytes: {totalDiskValue} bytes", totalDiskValue);
                }
                catch (DeprecatedException) {
                    logger.LogError("This functionality has been deprecated as of iOS 17.4 (beta)");
                }
            }

            using (DiagnosticsService diagnosticsService = new DiagnosticsService(lockdown)) {
                try {
                    Dictionary<string, object> batteryInfo = diagnosticsService.GetBatteryDetails();
                    ulong batteryPercentage = 0;
                    if (batteryInfo != null && batteryInfo.TryGetValue("BatteryCurrentCapacity", out object? batteryCurrentCapacity)) {
                        if (batteryCurrentCapacity is ulong capacity) {
                            batteryPercentage = capacity;
                        }
                        else if (batteryCurrentCapacity is int capacityInt) {
                            batteryPercentage = (ulong) capacityInt;
                        }
                        else if (batteryCurrentCapacity is uint capacityUInt) {
                            batteryPercentage = capacityUInt;
                        }
                    }
                    logger.LogInformation("Current battery percentage: {percent}", batteryPercentage);

                    bool isMobileCharging = false;
                    if (batteryInfo != null && batteryInfo.TryGetValue("BatteryIsCharging", out object? chargingStatus) && chargingStatus is bool charging) {
                        isMobileCharging = charging;
                    }
                    logger.LogInformation("Battery is charging: {isCharging}", isMobileCharging);

                    bool isFullyCharged = false;
                    if (batteryInfo != null && batteryInfo.TryGetValue("BatteryIsFullyCharged", out object? fullyChargedStatus) && fullyChargedStatus is bool fullyCharged) {
                        isFullyCharged = fullyCharged;
                    }
                    logger.LogInformation("Battery is fully charged: {isFullyCharged}", isFullyCharged);

                    string batterySerialNumber = string.Empty;
                    if (batteryInfo != null && batteryInfo.TryGetValue("BatterySerialNumber", out object? serialNumber) && serialNumber is string serial) {
                        batterySerialNumber = serial;
                    }
                    logger.LogInformation("Battery serial number: {serialNumber}", batterySerialNumber);
                }
                catch (Exception ex) {
                    logger.LogError(ex, "Error in getting battery details");
                }
            }
        }

        using (LockdownClient lockdown = MobileDevice.CreateUsingUsbmux(logger: logger)) {
            string product = lockdown.ProductType;
            string productName = lockdown.ProductFriendlyName;
            logger.LogInformation("Connected device is a {productName} ({product})", productName, product);
        }

        Usbmux.Subscribe(SubscriptionCallback, SubscriptionErrorCallback);
        Usbmux.Unsubscribe();

        using (LockdownClient lockdown = MobileDevice.CreateUsingUsbmux(logger: logger)) {
            using (Mobilebackup2Service mb2 = new Mobilebackup2Service(lockdown)) {
                await mb2.Backup(true, "backups", tokenSource.Token);
            }
            Console.WriteLine($"Backup done!");
        }

        using (LockdownClient lockdown = MobileDevice.CreateUsingUsbmux(logger: logger)) {
            using (MisagentService misagentService = new MisagentService(lockdown)) {
                await misagentService.GetInstalledProvisioningProfiles();
            }

            using (InstallationProxyService installationProxyService = new InstallationProxyService(lockdown)) {
                ArrayNode apps = await installationProxyService.Browse();
            }

            using (SpringBoardServicesService springBoard = new SpringBoardServicesService(lockdown)) {
                PropertyNode png = springBoard.GetIconPNGData("net.whatsapp.WhatsApp");
            }

            using (DiagnosticsService diagnosticsService = new DiagnosticsService(lockdown)) {
                DictionaryNode info = diagnosticsService.GetBattery();
            }

            using (SyslogService syslog = new SyslogService(lockdown)) {
                int counter = 0;
                foreach (string line in syslog.Watch()) {
                    logger.LogDebug("{line}", line);
                    if (counter >= 100) {
                        break;
                    }
                    counter++;
                }
            }

            // Get the list of directories in the Connected iOS device.
            using (AfcService afcService = new AfcService(lockdown)) {
                List<string> pathList = await afcService.GetDirectoryList(CancellationToken.None);
                logger.LogInformation("Path's available in the connected iOS device are as below.\n");
                logger.LogInformation("{pathList}", string.Join(", " + Environment.NewLine, pathList));
            }
        }

        // Connect via usbmuxd
        using (UsbmuxLockdownClient lockdown = MobileDevice.CreateUsingUsbmux(logger: logger)) {
            int count = 0;
            foreach (string line in new SyslogService(lockdown).Watch()) {
                if (count > 100) {
                    break;
                }
                count++;

                // Print all syslog lines as is
                Console.WriteLine(line);
            }
        }
    }

    private static void Progress_ProgressChanged(object? sender, PairingState e)
    {
        Console.WriteLine($"Pair Progress Changed: {e}");
    }

    private static void SubscriptionCallback(UsbmuxdDevice device, UsbmuxdConnectionEventType connectionEvent)
    {
        Console.WriteLine("NewCallbackExecuted");
        Console.WriteLine($"Connection event: {connectionEvent}");
        Console.WriteLine($"Device: {device.DeviceId} - {device.Serial}");
    }

    private static void SubscriptionErrorCallback(Exception ex)
    {
        Console.WriteLine("NewErrorCallbackExecuted");
        Console.WriteLine(ex.Message);
    }

    private static async Task Remoted()
    {
        Tunneld tunneld = Remote.StartTunneld();
        await Task.Delay(5 * 1000);
        RemoteServiceDiscoveryService rsd = await tunneld.GetDevice() ?? throw new Exception("No device found");
        using (Mobilebackup2Service mb2 = new Mobilebackup2Service(rsd)) {
            await mb2.Backup(true, "backups", tokenSource.Token);
        }
        await Task.Delay(5 * 1000);
        tunneld.Stop();
    }
}
