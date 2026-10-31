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

        // The renderer's SubmitFrame on the fakes: a fence when NeedsFence, none otherwise; a failed submit passes zero.
        private static long SubmitFrame(CopyRetirement retire, FakeGpu gpu, bool fails, out IntPtr fence)
        {
            fence = IntPtr.Zero;
            if (!retire.NeedsFence)
            {
                return retire.Submit();
            }
            if (!fails) fence = gpu.NewFence();
            long serial = retire.Submit();
            retire.Track(serial, fence);
            return serial;
        }

        [Fact]
        public void A_failed_submit_retires_nothing_and_a_later_fence_resolves_it()
        {
            var gpu = new FakeGpu();
            var retire = gpu.Tracker();
            var slots = new StagingSlots(2) { Retire = retire };

            slots.Copied(0);
            SubmitFrame(retire, gpu, fails: true, out _); // no fence: the GPU may still run it
            retire.Poll();
            slots.Reusable(0).Should().BeFalse("a failed submit is no proof the copy never runs");
            retire.Retired.Should().Be(0);
            retire.FailedSubmits.Should().Be(1);
            retire.Unresolved.Should().BeTrue();
            retire.NeedsFence.Should().BeTrue("the next frame takes a fence even without copies");

            SubmitFrame(retire, gpu, fails: false, out var f2); // frame 2 carries no copies but is fenced
            retire.Staged.Should().BeFalse();
            retire.Poll();
            slots.Reusable(0).Should().BeFalse("frame 2 has not finished");

            gpu.Signalled.Add(f2);
            retire.Poll();
            slots.Reusable(0).Should().BeTrue("frame 2 finished, so frame 1 did too if it ran at all");
            retire.Unresolved.Should().BeFalse();
            retire.NeedsFence.Should().BeFalse("no more fences once resolved");
            gpu.Released.Should().Equal(f2);
        }

        [Fact]
        public void A_failed_submit_never_retires_an_earlier_unsignalled_frame()
        {
            var gpu = new FakeGpu();
            var retire = gpu.Tracker();
            var slots = new StagingSlots(2) { Retire = retire };

            slots.Copied(0); // slot A in frame 1, still running
            SubmitFrame(retire, gpu, fails: false, out var f1);
            slots.Copied(1); // slot B in frame 2, whose submit fails
            SubmitFrame(retire, gpu, fails: true, out _);
            retire.Poll();
            slots.Reusable(0).Should().BeFalse("frame 1's fence has not signalled");
            slots.Reusable(1).Should().BeFalse();

            SubmitFrame(retire, gpu, fails: false, out var f3); // fenced for the unresolved frame 2
            gpu.Signalled.Add(f3); // seen out of order: still wait for f1 first
            retire.Poll();
            slots.Reusable(0).Should().BeFalse();
            slots.Reusable(1).Should().BeFalse();

            gpu.Signalled.Add(f1);
            retire.Poll();
            slots.Reusable(0).Should().BeTrue();
            slots.Reusable(1).Should().BeTrue();
            gpu.Released.Should().Equal(f1, f3);
        }

        // D3D12 executes the command list before it acquires the fence, and Vulkan can queue it and then fail at present:
        // the copy really runs. On the fake GPU slot 0 stays busy until a later fenced frame, and nothing may map it before.
        [Fact]
        public void A_failure_after_execution_started_keeps_the_slot_until_proven_done()
        {
            var gpu = new FakeGpu();
            var retire = gpu.Tracker();
            var slots = new StagingSlots(1) { Retire = retire };

            slots.Copied(0);
            SubmitFrame(retire, gpu, fails: true, out _); // ran on the GPU, but returned no fence
            for (int frame = 0; frame < 5; frame++)
            {
                retire.Poll();
                slots.Reusable(0).Should().BeFalse("the GPU may still read the slot");
                SubmitFrame(retire, gpu, fails: true, out _); // the device keeps failing: stay blocked
            }
            retire.FailedSubmits.Should().Be(6);

            SubmitFrame(retire, gpu, fails: false, out var ok);
            retire.Poll();
            slots.Reusable(0).Should().BeFalse("the fenced frame has not finished yet");
            gpu.Signalled.Add(ok); // the queue finished frame 1 before this later frame
            retire.Poll();
            slots.Reusable(0).Should().BeTrue();
            retire.Unresolved.Should().BeFalse();
        }

        [Fact]
        public void A_gpu_idle_wait_resolves_a_failed_submit_and_releases_every_fence()
        {
            var gpu = new FakeGpu();
            var retire = gpu.Tracker();
            var slots = new StagingSlots(2) { Retire = retire };

            slots.Copied(0);
            SubmitFrame(retire, gpu, fails: false, out var f1);
            slots.Copied(1);
            SubmitFrame(retire, gpu, fails: true, out _);

            // Renderer.DrainInFlight after SDL_WaitForGPUIdle succeeded: everything submitted is done
            retire.MarkRetired(retire.Serial - 1);
            retire.Poll();
            slots.Reusable(0).Should().BeTrue();
            slots.Reusable(1).Should().BeTrue();
            retire.Unresolved.Should().BeFalse();
            retire.PendingFences.Should().Be(0);
            gpu.Released.Should().Equal(new[] { f1 }, "released without a query: the idle wait proved it signalled");
        }

        // The offscreen throttle holds its own 2 fences and marks a frame retired after waiting on it; GPU samples and
        // frame screenshots wait on the frame they submit; DrainInFlight waits for the whole GPU. Ordinary frames go
        // through the tracker's own fences. All of them share one serial line and one watermark.
        [Fact]
        public void Throttle_screenshot_and_drain_waits_retire_in_one_line()
        {
            var gpu = new FakeGpu();
            var retire = gpu.Tracker();
            var slots = new StagingSlots(4) { Retire = retire };

            // throttle frames 1 and 2 (fences held by the throttle, not the tracker); frame 2's submit fails
            slots.Copied(0);
            long t1 = retire.Submit();
            slots.Copied(1);
            long t2 = retire.Submit();
            retire.Failed(t2);
            // frame 3: throttle again; its submit waits on the oldest throttle fence (frame 1)
            long t3 = retire.Submit();
            retire.MarkRetired(t1);
            slots.Reusable(0).Should().BeTrue();
            slots.Reusable(1).Should().BeFalse("frame 2 failed: only a later wait resolves it");

            // frame 4: a frame screenshot copies slot 2 and waits on its own fence, covering the failed frame 2
            slots.Copied(2);
            long s4 = retire.Submit();
            retire.MarkRetired(s4);
            slots.Reusable(1).Should().BeTrue();
            slots.Reusable(2).Should().BeTrue();
            retire.Unresolved.Should().BeFalse();

            // frame 5: an ordinary tracked frame, then an earlier throttle wait arrives late: never goes back
            slots.Copied(3);
            SubmitFrame(retire, gpu, fails: false, out _);
            retire.MarkRetired(t3);
            retire.Retired.Should().Be(s4, "retirement never goes back");
            slots.Reusable(3).Should().BeFalse();

            // drain: GPU idle covers frame 5 and releases its fence unqueried
            retire.MarkRetired(retire.Serial - 1);
            retire.Poll();
            slots.Reusable(3).Should().BeTrue();
            retire.PendingFences.Should().Be(0);
            gpu.Released.Should().HaveCount(1);
        }

        // Art and anim PageResidency each own a StagingSlots, and the renderer hands both the one CopyRetirement
        // (RenderFrame sets f.Pages.Retire and f.AnimPages.Retire). PageResidency itself needs a GPU device, so this
        // models it with its StagingSlots: one fence per frame covers both, and each slot set keeps its own serials.
        [Fact]
        public void Art_and_anim_slots_share_one_tracker()
        {
            var gpu = new FakeGpu();
            var retire = gpu.Tracker();
            var art = new StagingSlots(4) { Retire = retire };
            var anim = new StagingSlots(2) { Retire = retire };

            art.Copied(0);
            anim.Copied(0);
            SubmitFrame(retire, gpu, fails: false, out var f1);
            retire.FencesTaken.Should().Be(1, "one fence per frame for both residencies");

            anim.Copied(1); // frame 2: only anim copies
            SubmitFrame(retire, gpu, fails: false, out var f2);
            retire.FencesTaken.Should().Be(2);

            art.Reusable(1).Should().BeTrue("art slot 1 was never copied; anim's slot 1 is a different buffer");
            gpu.Signalled.Add(f1);
            retire.Poll();
            art.Reusable(0).Should().BeTrue();
            anim.Reusable(0).Should().BeTrue();
            anim.Reusable(1).Should().BeFalse("its frame 2 copy is still in flight");

            art.Copied(0); // frame 3: art only, its submit fails
            SubmitFrame(retire, gpu, fails: true, out _);
            gpu.Signalled.Add(f2);
            retire.Poll();
            anim.Reusable(1).Should().BeTrue();
            art.Reusable(0).Should().BeFalse("anim's earlier fence says nothing about art's failed frame 3");
            retire.NeedsFence.Should().BeTrue();

            SubmitFrame(retire, gpu, fails: false, out var f4);
            gpu.Signalled.Add(f4);
            retire.Poll();
            art.Reusable(0).Should().BeTrue();
        }

        // WorldRenderer.Dispose runs drain, workers, pages, anim pages and the owned renderer through Teardown.Run. The
        // real steps need a GPU device; here the drain throws as after device loss, and the renderer step releases the
        // tracker's fences as Renderer.Dispose does (_copies.Dispose after its own failed drain).
        [Fact]
        public void A_failed_world_disposal_still_releases_the_fences()
        {
            var gpu = new FakeGpu();
            var retire = gpu.Tracker();
            var slots = new StagingSlots(2) { Retire = retire };
            slots.Copied(0);
            SubmitFrame(retire, gpu, fails: false, out var f1);
            slots.Copied(1);
            SubmitFrame(retire, gpu, fails: false, out var f2);

            var ran = new List<string>();
            var drainFailure = new GpuFenceException("drain in-flight frames: GPU wait failed: device lost");
            Action act = () => Teardown.Run("world renderer dispose",
                () => { ran.Add("drain"); throw drainFailure; },
                () => ran.Add("workers"),
                () => { ran.Add("pages"); throw new InvalidOperationException("pages"); },
                null, // no anim residency
                () => { ran.Add("renderer"); retire.Dispose(); });

            act.Should().Throw<GpuFenceException>().Which.Should().BeSameAs(drainFailure, "the first failure is reported");
            ran.Should().Equal("drain", "workers", "pages", "renderer");
            gpu.Released.Should().Equal(f1, f2);
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
                bool fenced = retire.NeedsFence;
                long serial = retire.Submit();
                if (fenced)
                {
                    var fence = gpu.NewFence();
                    retire.Track(serial, fence);
                    inFlight[fence] = (frame, copied);
                }
            }

            uploaded.Should().Be(pages);

            // the final submissions: let the fake GPU finish them, then poll until every fence retired
            for (int drain = 0; retire.PendingFences > 0 && drain < latency + 2; drain++, frame++)
            {
                foreach (var (fence, (f, s)) in inFlight)
                {
                    if (f + latency > frame || !gpu.Signalled.Add(fence)) continue;
                    foreach (int x in s) busy.Remove(x);
                }
                retire.Poll();
            }
            retire.PendingFences.Should().Be(0, "every final submission drains");
            busy.Should().BeEmpty();
            gpu.Released.Should().HaveCount((int)retire.FencesTaken).And.OnlyHaveUniqueItems();
            for (int s = 0; s < SlotCount; s++) slots.Reusable(s).Should().BeTrue();
            retire.Retired.Should().Be(retire.Serial - 1);
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
