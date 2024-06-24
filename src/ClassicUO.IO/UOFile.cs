﻿#region license

// Copyright (c) 2024, andreakarasho
// All rights reserved.
// 
// Redistribution and use in source and binary forms, with or without
// modification, are permitted provided that the following conditions are met:
// 1. Redistributions of source code must retain the above copyright
//    notice, this list of conditions and the following disclaimer.
// 2. Redistributions in binary form must reproduce the above copyright
//    notice, this list of conditions and the following disclaimer in the
//    documentation and/or other materials provided with the distribution.
// 3. All advertising materials mentioning features or use of this software
//    must display the following acknowledgement:
//    This product includes software developed by andreakarasho - https://github.com/andreakarasho
// 4. Neither the name of the copyright holder nor the
//    names of its contributors may be used to endorse or promote products
//    derived from this software without specific prior written permission.
// 
// THIS SOFTWARE IS PROVIDED BY THE COPYRIGHT HOLDERS ''AS IS'' AND ANY
// EXPRESS OR IMPLIED WARRANTIES, INCLUDING, BUT NOT LIMITED TO, THE IMPLIED
// WARRANTIES OF MERCHANTABILITY AND FITNESS FOR A PARTICULAR PURPOSE ARE
// DISCLAIMED. IN NO EVENT SHALL THE COPYRIGHT HOLDER BE LIABLE FOR ANY
// DIRECT, INDIRECT, INCIDENTAL, SPECIAL, EXEMPLARY, OR CONSEQUENTIAL DAMAGES
// (INCLUDING, BUT NOT LIMITED TO, PROCUREMENT OF SUBSTITUTE GOODS OR SERVICES;
// LOSS OF USE, DATA, OR PROFITS; OR BUSINESS INTERRUPTION) HOWEVER CAUSED AND
// ON ANY THEORY OF LIABILITY, WHETHER IN CONTRACT, STRICT LIABILITY, OR TORT
// (INCLUDING NEGLIGENCE OR OTHERWISE) ARISING IN ANY WAY OUT OF THE USE OF THIS
// SOFTWARE, EVEN IF ADVISED OF THE POSSIBILITY OF SUCH DAMAGE.

#endregion

using ClassicUO.Utility.Logging;
using System;
using System.IO;
using System.IO.MemoryMappedFiles;

namespace ClassicUO.IO
{
    public class UOFile : PinnedBuffer
    {
        public UOFile(string filepath)
        {
            FilePath = filepath;
            Load();
        }

        public string FilePath { get; }
        protected MemoryMappedViewAccessor _accessor;
        protected MemoryMappedFile _file;

        private unsafe void Load()
        {
            Log.Trace($"Loading file:\t\t{FilePath}");

            FileInfo fileInfo = new FileInfo(FilePath);

            if (!fileInfo.Exists)
            {
                Log.Error($"{FilePath}  not exists.");

                return;
            }

            long size = fileInfo.Length;

            if (size > 0)
            {
                _file = MemoryMappedFile.CreateFromFile
                (
                    File.Open(fileInfo.FullName, FileMode.Open, FileAccess.Read, FileShare.ReadWrite),
                    null,
                    0,
                    MemoryMappedFileAccess.Read,
                    HandleInheritability.None,
                    false
                );

                _accessor = _file.CreateViewAccessor(0, size, MemoryMappedFileAccess.Read);

                byte* ptr = null;

                try
                {
                    _accessor.SafeMemoryMappedViewHandle.AcquirePointer(ref ptr);
                    SetData(ptr, (long) _accessor.SafeMemoryMappedViewHandle.ByteLength);
                }
                catch
                {
                    _accessor.SafeMemoryMappedViewHandle.ReleasePointer();

                    throw new Exception("Something goes wrong...");
                }
            }
            else
            {
                Log.Error($"{FilePath}  size must be > 0");
            }
        }

        protected override void Dispose(bool disposing)
        {
            bool hasAcquiredPointer = HasData;

            base.Dispose(disposing);

            if (hasAcquiredPointer) {
                _accessor.SafeMemoryMappedViewHandle.ReleasePointer();
            }

            if (disposing)
            {
                if (_accessor != null)
                {
                    _accessor.Dispose();
                    _accessor = null;
                }

                if (_file != null)
                {
                    _file.Dispose();
                    _file = null;
                }
                Log.Trace($"Unloaded:\t\t{FilePath}");
            }
        }
    }
}
