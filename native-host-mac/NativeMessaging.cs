using System;
using System.IO;
using System.Text;

namespace AwsLoginHelperHost
{
    /// <summary>
    /// Implements Chrome's native messaging stdio framing: each message is a 4-byte
    /// little-endian length prefix followed by that many bytes of UTF-8 JSON.
    /// See: https://developer.chrome.com/docs/extensions/develop/concepts/native-messaging
    /// </summary>
    internal static class NativeMessaging
    {
        // Chrome refuses to send/receive single messages larger than 1MB (host->extension)
        // or 4GB (extension->host in newer versions); 1MB is a safe practical ceiling here.
        private const int MaxMessageBytes = 1024 * 1024;

        // Now that one process handles many requests over its lifetime (see Program.cs's
        // persistent connectNative loop), more than one of those requests can be in flight
        // and finish at roughly the same time, each wanting to write its own response to the
        // SAME stdout stream. Interleaving two writes' bytes would corrupt the framing for
        // both. A single lock around the actual write serializes them - cheap, since each
        // write is tiny and infrequent compared to the seconds-long work that produces it.
        private static readonly object WriteLock = new object();

        public static string ReadMessage(Stream input)
        {
            byte[] lengthBytes = ReadExactly(input, 4);
            if (lengthBytes == null)
            {
                return null; // stdin closed before a message arrived
            }

            int length = BitConverter.ToInt32(lengthBytes, 0);
            if (length <= 0 || length > MaxMessageBytes)
            {
                throw new InvalidDataException($"Native message length out of range: {length}");
            }

            byte[] payload = ReadExactly(input, length);
            if (payload == null)
            {
                throw new EndOfStreamException("stdin closed mid-message");
            }

            return Encoding.UTF8.GetString(payload);
        }

        public static void WriteMessage(Stream output, string json)
        {
            byte[] payload = Encoding.UTF8.GetBytes(json);
            byte[] lengthPrefix = BitConverter.GetBytes(payload.Length);
            lock (WriteLock)
            {
                output.Write(lengthPrefix, 0, 4);
                output.Write(payload, 0, payload.Length);
                output.Flush();
            }
        }

        private static byte[] ReadExactly(Stream input, int count)
        {
            byte[] buffer = new byte[count];
            int offset = 0;
            while (offset < count)
            {
                int read = input.Read(buffer, offset, count - offset);
                if (read <= 0)
                {
                    return offset == 0 ? null : throw new EndOfStreamException();
                }
                offset += read;
            }
            return buffer;
        }
    }
}
