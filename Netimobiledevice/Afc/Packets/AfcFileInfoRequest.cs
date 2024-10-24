﻿using Netimobiledevice.Extentions;
using System.Text;

namespace Netimobiledevice.Afc.Packets
{
    internal class AfcFileInfoRequest(string filename) : AfcPacket
    {
        public string Filename { get; set; } = filename;

        public override int DataSize => Filename.Length;

        public override byte[] GetBytes()
        {
            return [.. Header.GetBytes(), .. Filename.AsCString().GetBytes(Encoding.UTF8)];
        }
    }
}
