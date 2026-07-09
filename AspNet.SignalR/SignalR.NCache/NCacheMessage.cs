using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using Microsoft.AspNet.SignalR.Messaging;
using Microsoft.AspNet.SignalR.Tracing;

namespace Alachisoft.NCache.AspNet.SignalR
{
    public class NCacheMessage
    {
        public ulong Id { get; private set; }
        public ScaleoutMessage ScaleoutMessage { get; private set; }

        public NCacheMessage(ulong id, ScaleoutMessage msg) 
        {
            this.Id = id;
            this.ScaleoutMessage = msg;
        }

        public static byte[] ToBytes(NCacheMessage ncacheMsg)
        {
            if (ncacheMsg == null || ncacheMsg.ScaleoutMessage == null || ncacheMsg.ScaleoutMessage.Messages == null)
            {
                throw new ArgumentNullException("messages");
            }

            using (var ms = new MemoryStream())
            {
                var binaryWriter = new BinaryWriter(ms);

                var buffer = ncacheMsg.ScaleoutMessage.ToBytes();
                binaryWriter.Write(ncacheMsg.Id);

                binaryWriter.Write(buffer.Length);
                binaryWriter.Write(buffer);

                return ms.ToArray();
            }
        }
        
        public static NCacheMessage FromBytes(byte[] data, TraceSource trace)
        {
            using (var stream = new MemoryStream(data))
            {
                var binaryReader = new BinaryReader(stream);                
                ulong id = binaryReader.ReadUInt64();


                int count = binaryReader.ReadInt32();
                byte[] buffer = binaryReader.ReadBytes(count);
                var scaleoutMessage = ScaleoutMessage.FromBytes(buffer);

                return new NCacheMessage(id, scaleoutMessage);
            }
        }
    }
}
