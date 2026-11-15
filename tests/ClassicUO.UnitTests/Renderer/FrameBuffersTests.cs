using System;
using System.Buffers;
using ClassicUO.Renderer;
using ClassicUO.Renderer.Animations;
using FluentAssertions;
using Xunit;

namespace ClassicUO.UnitTests.Renderer
{
    // FrameBuffers is the decode loop and the completion loop Animations runs for a direction; these drive it the way
    // DirectionJob.Execute and Animations.Complete do (decode, publish, upload, release), with a counting pool and the
    // real DecodeBudget/DecodeQueue, so every rental must come back and the budget must return to 0.
    public class FrameBuffersTests
    {
        private sealed class CountingPool : ArrayPool<uint>
        {
            public int Rented, Returned;
            public override uint[] Rent(int minimumLength)
            {
                Rented++;

                return new uint[Math.Max(16, minimumLength)];
            }

            public override void Return(uint[] array, bool clearArray = false) => Returned++;
        }

        private sealed class Job : DecodeBudget.Item
        {
            public FrameBuffers Buffers;
            public override void Execute() { }
        }

        private const int Frames = 4, Pixels = 100;
        private static int Size(int i) => Pixels;

        private static (DecodeBudget budget, DecodeQueue<Job> q, Job job, CountingPool pool) Setup()
        {
            Action<System.Threading.IThreadPoolWorkItem> noStart = _ => { };
            var budget = new DecodeBudget(1 << 20, noStart);
            var pool = new CountingPool();
            var q = new DecodeQueue<Job>(budget, j => j.Buffers.ReturnAll());
            var job = new Job { Buffers = new FrameBuffers(Frames, pool) };
            q.Submit(job, Frames * DecodeBudget.PooledBytes(Pixels), counted: true);
            budget.InFlightBytes.Should().BeGreaterThan(0);

            return (budget, q, job, pool);
        }

        [Fact]
        public void Decode_Throw_After_A_Successful_Frame_Returns_The_Partial_Rentals()
        {
            var (budget, q, job, pool) = Setup();

            // the worker (Execute): a failure is logged and the job is still published for the main-thread fallback
            try
            {
                job.Buffers.Decode(Size, (i, buf) =>
                {
                    if (i == 2)
                    {
                        throw new InvalidOperationException("bad frame");
                    }
                });
            }
            catch (InvalidOperationException)
            {
            }

            pool.Rented.Should().Be(3, "frames 0, 1 and the failing 2 were rented");
            pool.Returned.Should().Be(3);
            job.Buffers.Slots.Should().OnlyContain(s => s == null);
            q.Publish(job);

            // a later teardown must not return anything twice
            q.Cancel(_ => { });
            pool.Returned.Should().Be(3);
            budget.InFlightBytes.Should().Be(0);
        }

        [Fact]
        public void Failed_Worker_Falls_Back_To_Main_Thread_Decode_And_Releases_The_Budget()
        {
            var (budget, q, job, pool) = Setup();

            try
            {
                job.Buffers.Decode(Size, (i, buf) => throw new InvalidOperationException());
            }
            catch (InvalidOperationException)
            {
            }

            q.Publish(job);
            q.TryDequeue(out var got).Should().BeTrue();
            int uploads = 0;

            try
            {
                got.Buffers.Upload(Size, i => new uint[Pixels], (i, px) => uploads++);
            }
            finally
            {
                q.Release(got);
            }

            uploads.Should().Be(Frames);
            pool.Returned.Should().Be(pool.Rented);
            budget.InFlightBytes.Should().Be(0);
        }

        [Fact]
        public void Upload_Throw_On_A_Middle_Frame_Returns_Every_Buffer_Once_And_Releases_The_Budget()
        {
            var (budget, q, job, pool) = Setup();
            job.Buffers.Decode(Size, (i, buf) => { });
            q.Publish(job);
            pool.Rented.Should().Be(Frames);

            q.TryDequeue(out var got).Should().BeTrue();
            int uploaded = 0;

            // Animations.Complete: the upload loop, then Release in the finally
            var act = () =>
            {
                try
                {
                    got.Buffers.Upload(Size, i => null, (i, px) =>
                    {
                        if (i == 1)
                        {
                            throw new InvalidOperationException("upload failed");
                        }

                        uploaded++;
                    });
                }
                finally
                {
                    q.Release(got);
                }
            };

            act.Should().Throw<InvalidOperationException>();
            uploaded.Should().Be(1);
            pool.Returned.Should().Be(Frames, "frame 0 after its upload, frames 1..3 in the finally; none twice");
            got.Buffers.Slots.Should().OnlyContain(s => s == null);
            budget.InFlightBytes.Should().Be(0);

            q.Cancel(_ => { });
            pool.Returned.Should().Be(Frames);
        }

        [Fact]
        public void Normal_Completion_Returns_Each_Buffer_Once()
        {
            var (budget, q, job, pool) = Setup();
            job.Buffers.Decode(Size, (i, buf) => buf[0] = (uint)i + 1);
            q.Publish(job);
            q.TryDequeue(out var got).Should().BeTrue();

            try
            {
                got.Buffers.Upload(Size, i => null, (i, px) =>
                {
                    px.Length.Should().Be(Pixels);
                    px[0].Should().Be((uint)i + 1);
                });
            }
            finally
            {
                q.Release(got);
            }

            pool.Returned.Should().Be(Frames);
            budget.InFlightBytes.Should().Be(0);
        }
    }
}
