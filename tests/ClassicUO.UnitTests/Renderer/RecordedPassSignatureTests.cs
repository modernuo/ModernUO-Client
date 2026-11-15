#if CUO_DEVTOOLS
using System;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.CompilerServices;
using ClassicUO.Renderer;
using Microsoft.Xna.Framework.Graphics;
using Xunit;

namespace ClassicUO.UnitTests.Renderer;

// evloop-5: CUO_ANIM_DUE_CHECK signs every frame. A UI pass whose recording compares the same is dropped (the cached target
// is shown), so it must fold the signature of its last submission: otherwise a frame with an unchanged UI signs only the
// world and differs from the frame that submitted the UI (a false "skipped frame differs").
[Collection("ChunkStaticTileDataCollection")] // UltimaBatcher2D.SignatureOn is process-wide
public class RecordedPassSignatureTests
{
    private const BindingFlags Any = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;

    private static void Set(object target, string field, object value) => target.GetType().GetField(field, Any)!.SetValue(target, value);

    private static UltimaBatcher2D Batcher()
    {
        var batcher = (UltimaBatcher2D)RuntimeHelpers.GetUninitializedObject(typeof(UltimaBatcher2D));
        GC.SuppressFinalize(batcher);
        Set(batcher, "_recSegments", new List<RecordedSegment>());
        Set(batcher, "_vertexInfo", new UltimaBatcher2D.PositionNormalTextureColor4[16]);
        Set(batcher, "_textureInfo", new Texture2D[16]);
        return batcher;
    }

    private static void Fold(UltimaBatcher2D b, ulong signature, long sprites) =>
        typeof(UltimaBatcher2D).GetMethod("FoldSignature", Any)!.Invoke(b, new object[] { signature, sprites });

    [Fact]
    public void A_Dropped_Same_Recording_Signs_Like_Its_Submission()
    {
        bool saved = UltimaBatcher2D.SignatureOn;
        UltimaBatcher2D.SignatureOn = true;

        try
        {
            var pass = new RecordedPass();
            Set(pass, "Valid", true);
            Set(pass, "Signature", 0x1234_5678_9ABC_DEF0UL);
            Set(pass, "SignatureSprites", 42L);

            // the submitting frame: world draws, then the pass folded in (SubmitRecording)
            UltimaBatcher2D submitted = Batcher();
            submitted.FrameSignature = 777;
            Fold(submitted, 0x1234_5678_9ABC_DEF0UL, 42);

            // a later frame with the same world and the UI recording the same
            UltimaBatcher2D dropped = Batcher();
            dropped.FrameSignature = 777;
            dropped.DropRecording(pass);

            Assert.Equal(submitted.FrameSignature, dropped.FrameSignature);
            Assert.Equal(42, dropped.SignatureSprites);

            // a pass never submitted folds nothing
            UltimaBatcher2D invalid = Batcher();
            invalid.FrameSignature = 777;
            invalid.DropRecording(new RecordedPass());
            Assert.Equal(777UL, invalid.FrameSignature);
        }
        finally
        {
            UltimaBatcher2D.SignatureOn = saved;
        }
    }
}
#endif
