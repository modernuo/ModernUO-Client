using System.Collections.Generic;
using TinyEcs;
using TinyEcs.Bevy;
using Xunit;

namespace TinyEcs.Tests
{
	using Stage = TinyEcs.Bevy.Stage;

	// Repro for BevyApp.TryAddToBatch only checking explicit Before/After edges against
	// systems already in the current batch, not against skipped systems still pending.
	public class SchedulerBatchOrderingRepro
	{
		sealed class ResA { public int V; }
		sealed class ResC { public int V; }

		[Fact]
		public void TransitiveAfterIsRespectedInMultiThreadedMode()
		{
			using var world = new World();
			var app = new App(world, ThreadingMode.Multi);
			app.AddResource(new ResA());
			app.AddResource(new ResC());
			var executed = new List<string>();
			var gate = new object();

			// A and B conflict (both write ResA). C only writes ResC, so it doesn't conflict with A.
			app.AddSystem((ResMut<ResA> r) => { lock (gate) executed.Add("A"); })
				.InStage(Stage.Update).Label("A").Build();
			app.AddSystem((ResMut<ResA> r) => { lock (gate) executed.Add("B"); })
				.InStage(Stage.Update).Label("B").After("A").Build();
			app.AddSystem((ResMut<ResC> r) => { lock (gate) executed.Add("C"); })
				.InStage(Stage.Update).Label("C").After("B").Build();

			app.Run();

			Assert.Equal(new[] { "A", "B", "C" }, executed);
		}
	}
}
