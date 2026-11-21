// SPDX-License-Identifier: BSD-2-Clause

using System;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using Uo.Buffers;

namespace ClassicUO.Utility.Logging
{
    public sealed class LogFile : IDisposable
    {
        private readonly FileStream logStream;

        public LogFile(string directory, string file)
        {
            logStream = new FileStream
            (
                $"{directory}/{DateTime.Now:yyyy-MM-dd_hh-mm-ss}_{Environment.ProcessId}_{file}",
                FileMode.Append,
                FileAccess.Write,
                FileShare.ReadWrite,
                4096,
                true
            );
        }

        public void Dispose()
        {
            logStream.Close();
        }


        public void Write(string message)
        {
            int maxByteCount = Encoding.UTF8.GetMaxByteCount(message.Length);
            byte[] buffer = BufferPool.Rent<byte>(maxByteCount);

            try
            {
                int byteCount = Encoding.UTF8.GetBytes
                (
                    message,
                    0,
                    message.Length,
                    buffer,
                    0
                );

                logStream.Write(buffer, 0, byteCount);
                logStream.WriteByte((byte) '\n');
                logStream.Flush();
            }
            finally
            {
                BufferPool.Return(buffer);
            }
        }

        public async Task WriteAsync(string message)
        {
            int maxByteCount = Encoding.UTF8.GetMaxByteCount(message.Length);
            // The await may resume on another thread: the shared family, not the per-thread pool.
            byte[] buffer = BufferPool.RentShared<byte>(maxByteCount);

            try
            {
                int byteCount = Encoding.UTF8.GetBytes
                (
                    message,
                    0,
                    message.Length,
                    buffer,
                    0
                );

                await logStream.WriteAsync(buffer, 0, byteCount);
                logStream.WriteByte((byte) '\n');
                await logStream.FlushAsync();
            }
            finally
            {
                BufferPool.ReturnShared(buffer);
            }
        }


        public override string ToString()
        {
            return logStream.Name;
        }
    }
}