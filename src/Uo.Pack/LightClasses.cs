using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;

namespace Uo.Pack;

/// <summary>How a light shape's glow behaves when its emitter's sprite fades (light_alpha_fade).</summary>
public enum LightClass : byte
{
    /// <summary>Not in the table: the client decides by the emitter's tiledata Wall/Window flags.</summary>
    Unlisted = 0,

    /// <summary>A glow around its source (lamps, torches, sconces, braziers, candles, fires): full light whatever happens to
    /// its sprite.</summary>
    Radial = 1,

    /// <summary>A glow on a surface (a lit window's beam on the floor): it fades with its emitter's drawn alpha.</summary>
    Surface = 2
}

/// <summary>
/// lightclass.bin (optional, magic LCLS): <c>FileHeader</c>, count i32, then one <see cref="LightClass"/> byte per light id
/// (the tiledata light id, index into lights.bin). The converter writes <see cref="Seed"/> or a shard's JSON
/// (<c>--light-classes</c>). A pack without the file lists no shape: every light is <see cref="LightClass.Unlisted"/>.
/// </summary>
public static class LightClassFile
{
    public const uint Magic = 0x534C434C; // "LCLS"
    public const string FileName = "lightclass.bin";

    /// <summary>Light ids are a tiledata byte.</summary>
    public const int MaxCount = 256;

    public static void Write(string path, ReadOnlySpan<byte> classes)
    {
        Validate(classes, path);
        TableFile.Write(path, Magic, classes);
    }

    public static byte[] Read(string path)
    {
        var bytes = BoundedRead.ReadBounded(path, Magic, 4, out int hdr, (head, length) =>
        {
            int count = BinaryPrimitives.ReadInt32LittleEndian(head);
            return count >= 0 && count <= MaxCount && 4L + count == length;
        });
        var classes = bytes.AsSpan(hdr + 4).ToArray();
        Validate(classes, path);
        return classes;
    }

    private static void Validate(ReadOnlySpan<byte> classes, string path)
    {
        if (classes.Length > MaxCount) throw new InvalidDataException($"'{path}': {classes.Length} light classes; at most {MaxCount}");
        for (int i = 0; i < classes.Length; i++)
            if (classes[i] > (byte)LightClass.Surface) throw new InvalidDataException($"'{path}': light {i}: class {classes[i]} unknown");
    }

    /// <summary>
    /// The classes of the standard light.mul shapes (every client from 2.0 to 7.0 ships the same 0..55): the beams a lit
    /// window throws on the floor and the window panes' own glow are surface glows; the round, wedge and directional glows of
    /// lamps, torches, wall torches, sconces, fireplaces, ovens, braziers and moongates are radial. Shape 0 is a beam but its
    /// tiledata users are wall sconces, fire columns, fire pits and runes (light id 0), so it is radial. Ids without a
    /// standard shape stay unlisted. A shard with other shapes writes its own table (<c>--light-classes</c>).
    /// </summary>
    public static byte[] Seed()
    {
        var classes = new byte[MaxCount];
        foreach (int id in SeedSurface) classes[id] = (byte)LightClass.Surface;
        foreach (int id in SeedRadial) classes[id] = (byte)LightClass.Radial;
        return classes;
    }

    public static readonly int[] SeedSurface = { 3, 4, 22, 23, 24, 25, 26, 27, 28, 37, 38, 39, 40, 41, 42, 44, 45, 46 };

    public static readonly int[] SeedRadial =
    {
        0, 1, 2, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15, 16, 17, 18, 19, 20, 21, 29, 30, 31, 32, 33, 34, 35, 36, 47, 48, 49, 50, 51,
        52, 53, 54, 55
    };

    /// <summary>A table from id lists (JSON input): ids 0..255, each in at most one list.</summary>
    public static byte[] FromLists(IEnumerable<int> surface, IEnumerable<int> radial)
    {
        var classes = new byte[MaxCount];
        void Put(IEnumerable<int> ids, LightClass c)
        {
            foreach (int id in ids)
            {
                if ((uint)id >= MaxCount) throw new ArgumentException($"light id {id} outside 0..{MaxCount - 1}");
                if (classes[id] != 0) throw new ArgumentException($"light id {id} listed twice");
                classes[id] = (byte)c;
            }
        }
        Put(surface, LightClass.Surface);
        Put(radial, LightClass.Radial);
        return classes;
    }

    /// <summary>The id lists of a table (JSON output).</summary>
    public static (List<int> Surface, List<int> Radial) ToLists(ReadOnlySpan<byte> classes)
    {
        var s = new List<int>();
        var r = new List<int>();
        for (int i = 0; i < classes.Length; i++)
        {
            if (classes[i] == (byte)LightClass.Surface) s.Add(i);
            else if (classes[i] == (byte)LightClass.Radial) r.Add(i);
        }
        return (s, r);
    }
}
