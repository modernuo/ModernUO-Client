using System;
using System.Buffers;

namespace ClassicUO.Renderer.Animations
{
    /// <summary>
    /// The pooled pixel buffers of one animation direction job, with the decode loop and the completion loop that use
    /// them (Animations calls both; tests drive them with a counting pool). The job owns the slots from construction:
    /// every rented array is reachable through <see cref="Slots"/> until it is returned, and a returned slot is cleared,
    /// so no buffer is returned twice or lost.
    /// </summary>
    public sealed class FrameBuffers
    {
        public delegate void FrameUpload(int frame, ReadOnlySpan<uint> pixels);

        private readonly ArrayPool<uint> _pool;
        public readonly uint[][] Slots;

        public FrameBuffers(int frameCount, ArrayPool<uint> pool)
        {
            _pool = pool;
            Slots = new uint[frameCount][];
        }

        /// <summary>
        /// Worker: rents and fills a buffer per frame whose pixel count (<paramref name="pixelCount"/>) is not 0. If a
        /// decode throws, every buffer rented so far goes back to the pool before the exception propagates.
        /// </summary>
        public void Decode(Func<int, int> pixelCount, Action<int, uint[]> decode)
        {
            try
            {
                for (int i = 0; i < Slots.Length; i++)
                {
                    int count = pixelCount(i);

                    if (count == 0)
                    {
                        continue;
                    }

                    Slots[i] = _pool.Rent(count);
                    decode(i, Slots[i]);
                }
            }
            catch
            {
                ReturnAll();

                throw;
            }
        }

        /// <summary>
        /// Main thread: uploads each frame from its worker buffer (or from <paramref name="fallback"/> when the worker
        /// failed and left none), returning each buffer after its upload. If an upload throws, the buffers of that frame
        /// and of the later ones go back to the pool in the finally, and the exception propagates.
        /// </summary>
        public void Upload(Func<int, int> pixelCount, Func<int, uint[]> fallback, FrameUpload upload)
        {
            try
            {
                for (int i = 0; i < Slots.Length; i++)
                {
                    int count = pixelCount(i);

                    if (count == 0)
                    {
                        continue;
                    }

                    var pixels = Slots[i] ?? fallback(i);
                    upload(i, pixels.AsSpan(0, count));
                    ReturnSlot(i);
                }
            }
            finally
            {
                ReturnAll();
            }
        }

        private void ReturnSlot(int i)
        {
            var pixels = Slots[i];

            if (pixels != null)
            {
                Slots[i] = null;
                _pool.Return(pixels);
            }
        }

        /// <summary>Returns every buffer still held (each slot is cleared as it is returned, so calling again does nothing).</summary>
        public void ReturnAll()
        {
            for (int i = 0; i < Slots.Length; i++)
            {
                ReturnSlot(i);
            }
        }
    }
}
