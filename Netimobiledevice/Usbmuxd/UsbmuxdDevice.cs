using Microsoft.Extensions.Logging;
using Netimobiledevice.Plist;
using System;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;

namespace Netimobiledevice.Usbmuxd
{
    /// <summary>
    /// Usbmuxd Device information.
    /// </summary>
    public class UsbmuxdDevice
    {
        public UsbmuxdConnectionType ConnectionType { get; private set; }
        public ulong DeviceId { get; private set; }
        public string Serial { get; private set; }

        public byte[]? NetworkAddress { get; private set; }

        public int? InterfaceIndex { get; private set; } = null;

        public UsbmuxdDevice(IntegerNode deviceId, DictionaryNode propertiesDict, ILogger? logger = null)
        {
            DeviceId = deviceId.Value;
            Serial = propertiesDict["SerialNumber"].AsStringNode().Value;

            string connectionTypeString = propertiesDict["ConnectionType"].AsStringNode().Value;
            if (connectionTypeString == "USB") {
                ConnectionType = UsbmuxdConnectionType.Usb;
            }
            else if (connectionTypeString == "Network") {
                ConnectionType = UsbmuxdConnectionType.Network;
                var netAddressNode = propertiesDict["NetworkAddress"].AsDataNode();
                var netInterfaceIndexNode = propertiesDict["InterfaceIndex"].AsIntegerNode();
                if (netInterfaceIndexNode != null) {
                    InterfaceIndex = (int)netInterfaceIndexNode.Value;
                }

                var addressValue = netAddressNode.Value[1];

                if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows)) { // This may not catch all issues, but I don't have an arm based PC here to test on
                    addressValue = netAddressNode.Value[0];
                }

                if (addressValue == 2) { // AF_INET
                    var ipAddress = new IPAddress(netAddressNode.Value.AsSpan(4, 4));
                    NetworkAddress = new[] {
                        netAddressNode.Value[4],
                        netAddressNode.Value[5],
                        netAddressNode.Value[6],
                        netAddressNode.Value[7]
                    };
                }
                else if (addressValue == 0x1e || addressValue == (int)AddressFamily.InterNetworkV6) { // IPV6
                    var ipAddress = new IPAddress(netAddressNode.Value.AsSpan(8, 16));
                    var addrFam = ipAddress.AddressFamily;
                    NetworkAddress = ipAddress.GetAddressBytes();
                }
                else {
                    logger?.LogError(1, $"Network address is not supported. NetAddress Node Array [ {BitConverter.ToString(netAddressNode.Value).Replace("-", ", ")} ]");
                }
            }
            else {
                throw new NotImplementedException($"Unknown connection type: {connectionTypeString}");
            }
        }

        public UsbmuxdDevice(uint deviceId, string serialNumber, UsbmuxdConnectionType connectionType)
        {
            DeviceId = deviceId;
            Serial = serialNumber;
            ConnectionType = connectionType;
        }

        public Socket Connect(ushort port, string usbmuxAddress = "", ILogger? logger = null)
        {
            UsbmuxConnection muxConnection = UsbmuxConnection.Create(usbmuxAddress, logger);
            try {
                return muxConnection.Connect(this, port);
            }
            catch (Exception ex) {
                logger?.LogWarning($"Couldn't connect to port {port}: {ex}");
                muxConnection.Close();
                throw;
            }
        }
    }
}
