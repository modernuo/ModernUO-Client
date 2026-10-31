using System;
using System.Buffers;
using System.Diagnostics;
using ClassicUO.Assets;
using Microsoft.Xna.Framework.Graphics;

namespace ClassicUO.Renderer.Texmaps
{
    public sealed class Texmap
    {
        private readonly TextureAtlas _atlas;
        private readonly SpriteInfo[] _spriteInfos;
        private readonly PixelPicker _picker = new PixelPicker();
        private readonly TexmapsLoader _texmapsLoader;
        private readonly FirstSightDecoder _decoder;

        public Texmap(TexmapsLoader texmapsLoader, GraphicsDevice device)
        {
            _texmapsLoader = texmapsLoader;
            _atlas = new TextureAtlas(device, 2048, 2048, SurfaceFormat.Color);
            _spriteInfos = new SpriteInfo[texmapsLoader.Count];
            _decoder = new FirstSightDecoder("texmap", _spriteInfos.Length, DecodeOnWorker, UploadDecoded,
                idx => _texmapsLoader.Exists((int)idx) ? _texmapsLoader.PackSpriteEntries[idx].OrigW * _texmapsLoader.PackSpriteEntries[idx].OrigH * 4L : 0L);
        }

        /// <summary>Worker decodes requested and not uploaded yet.</summary>
        public int PendingDecodes => _decoder.Pending;

        /// <summary>Worker decodes uploaded so far.</summary>
        public int CompletedDecodes => _decoder.Completed;

        public ref readonly SpriteInfo GetTexmap(uint idx)
        {
            if (idx >= _spriteInfos.Length)
                return ref SpriteInfo.Empty;

            ref var spriteInfo = ref _spriteInfos[idx];

            if (spriteInfo.Texture == null)
            {
                long t0 = Stopwatch.GetTimestamp();
                Arts.Art.FrameSyncLoads++;

                var texmapInfo = _texmapsLoader.GetTexmap(idx);
                if (!texmapInfo.Pixels.IsEmpty)
                {
                    spriteInfo.Texture = _atlas.AddSprite(
                        texmapInfo.Pixels,
                        texmapInfo.Width,
                        texmapInfo.Height,
                        out spriteInfo.UV
                    );

                    _picker.Set(idx, texmapInfo.Width, texmapInfo.Height, texmapInfo.Pixels);
                }

                Arts.Art.FrameSyncTicks += Stopwatch.GetTimestamp() - t0;
            }

            return ref spriteInfo;
        }

        /// <summary>
        /// True when texmap <paramref name="idx"/> is in the atlas, or has no image; otherwise queues its worker decode,
        /// once, and returns false (see <see cref="Arts.Art.AsyncFirstSight"/>). Main thread.
        /// </summary>
        public bool Request(uint idx)
        {
            if (idx >= _spriteInfos.Length || _spriteInfos[idx].Texture != null || !Arts.Art.AsyncFirstSight || !_texmapsLoader.Exists((int)idx)
                || _decoder.IsFailed(idx))
            {
                return true;
            }

            _decoder.Request(idx);

            return false;
        }

        /// <summary>Scene teardown: drops finished worker decodes and returns their buffers (see FirstSightDecoder.Cancel).</summary>
        public void CancelPending() => _decoder.Cancel();

        /// <summary>Uploads finished worker decodes until <paramref name="deadline"/> (Stopwatch ticks), at least one. Main thread.</summary>
        public void DrainReady(long deadline)
        {
            if (_decoder.HasReady)
            {
                long t0 = Stopwatch.GetTimestamp();
                _decoder.Drain(deadline);
                Arts.Art.FrameUploadTicks += Stopwatch.GetTimestamp() - t0;
            }
        }

        // worker thread: the loader's sprite table (immutable after Load), the pack pages (thread-safe), ArrayPool
        private bool DecodeOnWorker(FirstSightDecoder.Job job)
        {
            if (!_texmapsLoader.Exists((int)job.Index))
            {
                return false;
            }

            ref readonly var sprite = ref _texmapsLoader.PackSpriteEntries[job.Index];
            int count = sprite.OrigW * sprite.OrigH;
            job.Pixels = ArrayPool<uint>.Shared.Rent(count);
            PackSprites.DecodeColor(_texmapsLoader.FileManager.Pack.Tex, in sprite, job.Pixels);
            job.Width = sprite.OrigW;
            job.Height = sprite.OrigH;

            return true;
        }

        // main thread: what GetTexmap does after its decode
        private void UploadDecoded(FirstSightDecoder.Job job)
        {
            ref var spriteInfo = ref _spriteInfos[job.Index];

            if (spriteInfo.Texture != null)
            {
                return;
            }

            var pixels = job.Pixels.AsSpan(0, job.Width * job.Height);
            spriteInfo.Texture = _atlas.AddSprite(pixels, job.Width, job.Height, out spriteInfo.UV);
            _picker.Set(job.Index, job.Width, job.Height, pixels);
            Arts.Art.FrameAsyncUploads++;
        }
    }
}
