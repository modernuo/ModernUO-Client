using System;
using System.Collections.Generic;
using FluentAssertions;
using Uo.Render;
using Xunit;

namespace ClassicUO.UnitTests.Renderer
{
    // Memory round 3: page staging slots are reused only after the copy recorded from them last has retired, and are then
    // mapped without cycling, so SDL_GPU never grows a slot's transfer-buffer container. StagingSlots and CopyRetirement
    // hold that logic without a GPU device; fences here are fake handles whose signalled state the test controls.
    public sealed class StagingSlotsTests
    {
        private sealed class FakeGpu
        {
            public readonly HashSet<IntPtr> Signalled = new();
            public readonly List<IntPtr> Released = new();
            private int _next;

            public IntPtr NewFence() => (IntPtr)(++_next);

            public CopyRetirement Tracker() => new(f => Signalled.Contains(f), f => Released.Add(f));
        }

        [Fact]
        public void A_copied_slot_is_reused_only_after_its_fence_signals()
        {
            var gpu = new FakeGpu();
            var retire = gpu.Tracker();
            var slots = new StagingSlots(2) { Retire = retire };

            slots.Reusable(0).Should().BeTrue("never copied from");
            slots.CycleOnMap.Should().BeFalse();

            slots.Copied(0);
            retire.Staged.Should().BeTrue();
            var fence = gpu.NewFence();
            retire.Track(retire.Submit(), fence);
            retire.Staged.Should().BeFalse();

            retire.Poll();
            slots.Reusable(0).Should().BeFalse("its copy is still in flight");
            slots.Reusable(1).Should().BeTrue();
            gpu.Released.Should().BeEmpty();

            gpu.Signalled.Add(fence);
            retire.Poll();
            slots.Reusable(0).Should().BeTrue();
            gpu.Released.Should().Equal(fence);
            retire.PendingFences.Should().Be(0);
        }

        [Fact]
        public void Fences_retire_in_submit_order()
        {
            var gpu = new FakeGpu();
            var retire = gpu.Tracker();
            var slots = new StagingSlots(2) { Retire = retire };

            slots.Copied(0);
            var f1 = gpu.NewFence();
            retire.Track(retire.Submit(), f1);
            slots.Copied(1);
            var f2 = gpu.NewFence();
            retire.Track(retire.Submit(), f2);

            gpu.Signalled.Add(f2); // only the later one reports done: wait for the earlier
            retire.Poll();
            slots.Reusable(0).Should().BeFalse();
            slots.Reusable(1).Should().BeFalse();
            gpu.Released.Should().BeEmpty();

            gpu.Signalled.Add(f1);
            retire.Poll();
            slots.Reusable(0).Should().BeTrue();
            slots.Reusable(1).Should().BeTrue();
            gpu.Released.Should().Equal(f1, f2);
        }

        [Fact]
        public void A_frame_without_copies_takes_no_fence_and_a_recopied_slot_waits_again()
        {
            var gpu = new FakeGpu();
            var retire = gpu.Tracker();
            var slots = new StagingSlots(1) { Retire = retire };

            retire.Submit().Should().Be(1);
            retire.Staged.Should().BeFalse();
            retire.FencesTaken.Should().Be(0);

            slots.Copied(0);
            var f = gpu.NewFence();
            retire.Track(retire.Submit(), f);
            gpu.Signalled.Add(f);
            retire.Poll();
            slots.Reusable(0).Should().BeTrue();

            slots.Copied(0); // the slot is mapped, loaded and copied again
            slots.Reusable(0).Should().BeFalse("the new copy is recorded but not yet submitted");
            var g = gpu.NewFence();
            retire.Track(retire.Submit(), g);
            retire.Poll();
            slots.Reusable(0).Should().BeFalse();
            retire.FencesTaken.Should().Be(2);
        }

        [Fact]
        public void A_failed_submit_or_a_wait_elsewhere_retires_the_serial()
        {
            var gpu = new FakeGpu();
            var retire = gpu.Tracker();
            var slots = new StagingSlots(2) { Retire = retire };

            slots.Copied(0);
            retire.Track(retire.Submit(), IntPtr.Zero); // submit failed: the GPU never reads the slot
            slots.Reusable(0).Should().BeTrue();
            retire.PendingFences.Should().Be(0);

            slots.Copied(1);
            long serial = retire.Submit();
            slots.Reusable(1).Should().BeFalse();
            retire.MarkRetired(serial); // the caller waited (offscreen throttle, GPU idle, screenshot)
            slots.Reusable(1).Should().BeTrue();
            retire.MarkRetired(serial - 1);
            retire.Retired.Should().Be(serial, "retirement never goes back");
        }

        [Fact]
        public void Without_a_tracker_every_slot_is_reusable_and_maps_with_cycling()
        {
            var slots = new StagingSlots(4);
            slots.CycleOnMap.Should().BeTrue();
            slots.Copied(2);
            slots.Reusable(2).Should().BeTrue();
        }

        [Fact]
        public void Dispose_releases_the_fences_still_pending()
        {
            var gpu = new FakeGpu();
            var retire = gpu.Tracker();
            var a = gpu.NewFence();
            var b = gpu.NewFence();
            retire.Track(retire.Submit(), a);
            retire.Track(retire.Submit(), b);

            retire.Dispose();
            gpu.Released.Should().Equal(a, b);
        }

        [Fact]
        public void Slot_waits_count_episodes_frames_and_time()
        {
            var slots = new StagingSlots(4);
            slots.Track(false, 0);
            slots.Track(true, 100);
            slots.Track(true, 150);
            slots.Track(false, 400); // episode 1: 2 frames, 300 ticks
            slots.Track(false, 500);
            slots.Track(true, 600);
            slots.Track(false, 700); // episode 2: 1 frame, 100 ticks

            slots.Stalls.Should().Be(2);
            slots.StallFrames.Should().Be(3);
            slots.StallTicks.Should().Be(400);
            slots.MaxStallTicks.Should().Be(300);
        }

        // A login burst on the model of the client: 4 slots, page loads that finish within a frame, every frame's copies
        // retiring 2 frames after their submit (SDL keeps up to 3 frames in flight). No slot is ever rewritten while a
        // copy from it is in flight, every page loads, and the loads that had to wait show up as slot waits.
        [Theory]
        [InlineData(40, 2)]
        [InlineData(13, 3)]
        [InlineData(4, 1)]
        public void A_burst_never_rewrites_a_slot_in_flight_and_loads_every_page(int pages, int latency)
        {
            const int SlotCount = 4;
            var gpu = new FakeGpu();
            var retire = gpu.Tracker();
            var slots = new StagingSlots(SlotCount) { Retire = retire };
            var inFlight = new Dictionary<IntPtr, (long Frame, List<int> Slots)>();
            var busy = new HashSet<int>(); // slots whose copy the fake GPU has not finished
            var loaded = new int?[SlotCount];
            int queued = pages, uploaded = 0;
            long frame = 0;

            for (; uploaded < pages && frame < 1000; frame++)
            {
                // the GPU finishes the frames submitted `latency` frames ago
                foreach (var (fence, (f, s)) in inFlight)
                {
                    if (f + latency > frame || !gpu.Signalled.Add(fence)) continue;
                    foreach (int x in s) busy.Remove(x);
                }

                // StartLoads: map free slots whose last copy retired; the rest of the queue waits
                retire.Poll();
                bool blocked = false;
                for (int s = 0; s < SlotCount && queued > 0; s++)
                {
                    if (loaded[s] != null) continue;
                    if (!slots.Reusable(s)) { blocked = true; continue; }
                    busy.Should().NotContain(s, "a slot is rewritten only after its copy finished");
                    loaded[s] = pages - queued--;
                }
                slots.Track(blocked && queued > 0, frame);

                // Upload: copy every loaded slot into this frame's command buffer, submit with a fence
                var copied = new List<int>();
                for (int s = 0; s < SlotCount; s++)
                {
                    if (loaded[s] == null) continue;
                    slots.Copied(s);
                    busy.Add(s);
                    copied.Add(s);
                    loaded[s] = null;
                    uploaded++;
                }
                bool staged = retire.Staged;
                long serial = retire.Submit();
                if (staged)
                {
                    var fence = gpu.NewFence();
                    retire.Track(serial, fence);
                    inFlight[fence] = (frame, copied);
                }
            }

            uploaded.Should().Be(pages);
            retire.FencesTaken.Should().Be((pages + SlotCount - 1) / SlotCount, "one fence per frame that carried copies");
            if (pages > SlotCount)
            {
                slots.Stalls.Should().BeGreaterThan(0);
                slots.StallFrames.Should().BeGreaterThan(0);
            }
            else
            {
                slots.Stalls.Should().Be(0, "a burst that fits the slots never waits");
            }
        }
    }
}
