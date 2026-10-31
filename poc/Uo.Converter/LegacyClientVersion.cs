using System.Buffers.Binary;
using ClassicUO.Utility;

namespace Uo.Converter;

/// <summary>
/// Client version for the converter, in ModernUO's order (Projects/Server/Client/UOClient.cs): the configured value
/// (<c>--client-version</c>, ModernUO's <c>clientData.clientVersion</c>), a ClassicUO settings.json <c>clientversion</c>
/// (<c>--cuo-settings</c>, ModernUO's <c>DetectCUOClient</c>), then client.exe (<c>DetectClassicClient</c>): the
/// VS_VERSION_INFO resource, else the version format call of pre-resource clients. The two exe readers are a port of
/// ModernUO PR #2735 at 61d0651af ("fix: detect pre-resource classic client versions"); keep them identical to it.
/// Versions below 4.0.11c are not supported (owner, 2026-10-31).
/// </summary>
public static class LegacyClientVersion
{
    public const string FlagHelp = "pass --client-version <x.y.z.w> (or x.y.za, e.g. 4.0.11c) or --cuo-settings <ClassicUO settings.json with \"clientversion\">";

    /// <summary>The oldest supported client, as ClassicUO's parser reads "4.0.11c" (4.0.11.2: it counts letters from
    /// 'a' = 0, so the CV_4011C enum, whose low byte is the letter's ASCII code, is not comparable).</summary>
    public static ClientVersion MinSupported { get; } = ClientVersionHelper.IsClientVersionValid("4.0.11c", out var v) ? v : 0;

    /// <summary>Resolves the version text and value. <paramref name="source"/> says where it came from (empty when
    /// nothing was found).</summary>
    public static bool TryResolve(string uoPath, string? overrideText, string? cuoSettings, out string versionText, out ClientVersion version, out string source)
    {
        version = 0;
        versionText = "";
        source = "";
        if (overrideText != null)
        {
            versionText = overrideText;
            source = "--client-version";
            return ClientVersionHelper.IsClientVersionValid(overrideText, out version);
        }

        if (cuoSettings != null)
        {
            source = $"--cuo-settings {cuoSettings}";
            versionText = ReadCuoClientVersion(cuoSettings) ?? "";
            return ClientVersionHelper.IsClientVersionValid(versionText, out version);
        }

        var exe = Path.Combine(uoPath, "client.exe");
        if (!File.Exists(exe)) return false;
        var bytes = File.ReadAllBytes(exe);
        if (DetectFromVersionInfo(bytes) is { } info)
        {
            (versionText, source) = (info, "client.exe VS_VERSION_INFO");
            return ClientVersionHelper.IsClientVersionValid(versionText, out version);
        }

        if (DetectFromVersionFormat(bytes) is { } format)
        {
            (versionText, source) = (format, "client.exe version format call");
            return ClientVersionHelper.IsClientVersionValid(versionText, out version);
        }

        return false;
    }

    /// <summary>The <c>clientversion</c> string of a ClassicUO settings.json, or null.</summary>
    public static string? ReadCuoClientVersion(string settingsPath)
    {
        if (!File.Exists(settingsPath)) return null;
        using var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllBytes(settingsPath),
            new System.Text.Json.JsonDocumentOptions { CommentHandling = System.Text.Json.JsonCommentHandling.Skip, AllowTrailingCommas = true });
        foreach (var p in doc.RootElement.EnumerateObject())
        {
            if (p.Name.Equals("clientversion", StringComparison.OrdinalIgnoreCase) && p.Value.ValueKind == System.Text.Json.JsonValueKind.String)
                return p.Value.GetString();
        }

        return null;
    }

    /// <summary>ModernUO <c>DetectFromVersionInfo</c>: "major.minor.build.private" from VS_VERSION_INFO, or null.</summary>
    public static string? DetectFromVersionInfo(ReadOnlySpan<byte> exe)
    {
        // VS_VERSION_INFO (unicode)
        var versionIndex = exe.IndexOf("V\0S\0_\0V\0E\0R\0S\0I\0O\0N\0_\0I\0N\0F\0O\0"u8);
        if (versionIndex < 0 || versionIndex + 50 > exe.Length) return null;

        var offset = versionIndex + 42; // 30 + 12
        var minorPart = BinaryPrimitives.ReadUInt16LittleEndian(exe[offset..]);
        var majorPart = BinaryPrimitives.ReadUInt16LittleEndian(exe[(offset + 2)..]);
        var privatePart = BinaryPrimitives.ReadUInt16LittleEndian(exe[(offset + 4)..]);
        var buildPart = BinaryPrimitives.ReadUInt16LittleEndian(exe[(offset + 6)..]);
        return $"{majorPart}.{minorPart}.{buildPart}.{privatePart}";
    }

    /// <summary>
    /// ModernUO <c>DetectFromVersionFormat</c>. Older clients (4.0.11c and later 4.x/5.0.x builds) have no version
    /// resource. They build the version string at startup with sprintf("%d.%d.%d%s", major, minor, revision, letter),
    /// so the parts are the operands of the pushes just before the format string's push:
    /// <c>68 &lt;letter VA&gt;   6A &lt;revision&gt;   6A &lt;minor&gt;   6A &lt;major&gt;   68 &lt;format VA&gt;</c>.
    /// The push of the format must be unique and the letter at most one lower-case character; otherwise null.
    /// </summary>
    public static string? DetectFromVersionFormat(ReadOnlySpan<byte> exe)
    {
        var formatOffset = exe.IndexOf("%d.%d.%d%s\0"u8);
        if (formatOffset < 0 || !TryOffsetToVirtualAddress(exe, formatOffset, out var formatVa)) return null;

        Span<byte> pushFormat = stackalloc byte[5];
        pushFormat[0] = 0x68;
        BinaryPrimitives.WriteUInt32LittleEndian(pushFormat[1..], formatVa);

        var pushOffset = exe.IndexOf(pushFormat);
        if (pushOffset < 11 || exe[(pushOffset + 1)..].IndexOf(pushFormat) >= 0) return null; // missing or ambiguous

        var pushes = exe.Slice(pushOffset - 11, 11);
        if (pushes[0] != 0x68 || pushes[5] != 0x6A || pushes[7] != 0x6A || pushes[9] != 0x6A) return null;

        var letterVa = BinaryPrimitives.ReadUInt32LittleEndian(pushes[1..]);
        if (!TryVirtualAddressToOffset(exe, letterVa, out var letterOffset)) return null;

        var letter = exe[letterOffset..];
        var letterLength = letter.IndexOf((byte)0);
        if (letterLength is < 0 or > 1 || letterLength == 1 && !char.IsAsciiLetterLower((char)letter[0])) return null;

        var suffix = letterLength == 1 ? ((char)letter[0]).ToString() : "";
        return $"{pushes[10]}.{pushes[8]}.{pushes[6]}{suffix}";
    }

    private static bool TryOffsetToVirtualAddress(ReadOnlySpan<byte> exe, int offset, out uint virtualAddress)
    {
        foreach (var (sectionVa, rawPointer, rawSize) in GetSections(exe, out var imageBase))
        {
            if (offset >= rawPointer && offset < rawPointer + rawSize)
            {
                virtualAddress = imageBase + sectionVa + (uint)(offset - rawPointer);
                return true;
            }
        }

        virtualAddress = 0;
        return false;
    }

    private static bool TryVirtualAddressToOffset(ReadOnlySpan<byte> exe, uint virtualAddress, out int offset)
    {
        foreach (var (sectionVa, rawPointer, rawSize) in GetSections(exe, out var imageBase))
        {
            var start = imageBase + sectionVa;
            if (virtualAddress >= start && virtualAddress < start + rawSize && rawPointer + (virtualAddress - start) < exe.Length)
            {
                offset = (int)(rawPointer + (virtualAddress - start));
                return true;
            }
        }

        offset = 0;
        return false;
    }

    // PE32 section headers that fit in the file; empty if not a PE32 image.
    private static (uint VirtualAddress, uint RawPointer, uint RawSize)[] GetSections(ReadOnlySpan<byte> exe, out uint imageBase)
    {
        imageBase = 0;
        if (exe.Length < 0x40) return [];

        var pe = BinaryPrimitives.ReadInt32LittleEndian(exe[0x3C..]);
        if (pe < 0 || pe + 24 + 32 > exe.Length || BinaryPrimitives.ReadUInt32LittleEndian(exe[pe..]) != 0x00004550 ||
            BinaryPrimitives.ReadUInt16LittleEndian(exe[(pe + 24)..]) != 0x10B) // "PE\0\0", PE32 optional header
        {
            return [];
        }

        var count = BinaryPrimitives.ReadUInt16LittleEndian(exe[(pe + 6)..]);
        var optionalSize = BinaryPrimitives.ReadUInt16LittleEndian(exe[(pe + 20)..]);
        var table = pe + 24 + optionalSize;
        if (table >= exe.Length) return [];

        imageBase = BinaryPrimitives.ReadUInt32LittleEndian(exe[(pe + 24 + 28)..]);

        var sections = new (uint, uint, uint)[Math.Min(count, (exe.Length - table) / 40)];
        for (var i = 0; i < sections.Length; i++)
        {
            var section = exe.Slice(table + i * 40, 40);
            sections[i] = (BinaryPrimitives.ReadUInt32LittleEndian(section[12..]), BinaryPrimitives.ReadUInt32LittleEndian(section[20..]),
                           BinaryPrimitives.ReadUInt32LittleEndian(section[16..]));
        }

        return sections;
    }
}
