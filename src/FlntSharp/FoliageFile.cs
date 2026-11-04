using System.Buffers.Binary;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;

namespace FlntSharp;

public enum FoliageFileKind
{
    Unknown,
    Tree,
    Undergrowth,
}

public static class FoliagePlacement
{
    public static IReadOnlyList<string> TreeCategories { get; } = ["LowTree", "Ornament"];

    public static IReadOnlyList<string> UndergrowthLayers { get; } =
        ["Undergrowth4", "Undergrowth8", "Moss", "Turf", "Stone", "Forest"];

    // Foliage/ImpostorInfo/*.flnt come back Unknown: neither parser here is what reads them in the game.
    public static FoliageFileKind GetKind(string path)
    {
        string name = path[(path.LastIndexOfAny(['/', '\\']) + 1)..];
        if (name.EndsWith(".zs", StringComparison.Ordinal))
            name = name[..^3];
        if (!name.EndsWith(".flnt", StringComparison.Ordinal))
            return FoliageFileKind.Unknown;

        string[] parts = name[..^5].Split('.');
        if (parts.Length >= 2 && TreeCategories.Contains(parts[^1]))
            return FoliageFileKind.Tree;
        if (parts.Length >= 3 && UndergrowthLayers.Contains(parts[^1]))
            return FoliageFileKind.Undergrowth;
        return FoliageFileKind.Unknown;
    }
}

public sealed record FoliageModelEntry(byte Unknown0, string ModelName);

// The game copies these 64 bytes to the GPU without reading them, so their layout lives in the shaders.
[InlineArray(WordCount)]
public struct FoliageInstance
{
    public const int WordCount = 16;

    private uint _word;

    public readonly float GetSingle(int index) => BitConverter.UInt32BitsToSingle(this[index]);
}

public sealed class FoliageTreeFile
{
    public const int MaxModels = 512;
    public const int ModelEntrySize = 0x100;
    public const int InstanceEntrySize = 0x40;

    public uint Version { get; init; }
    public IReadOnlyList<FoliageModelEntry> Models { get; init; } = [];
    public FoliageInstance[] Instances { get; init; } = [];
    public uint[] InstanceMetadata { get; init; } = [];
    public uint[] ExtraTable1 { get; init; } = [];
    public uint ExtraCount0 { get; init; }
    public uint[] ExtraTable0Packed { get; init; } = [];

    public static FoliageTreeFile Load(ReadOnlySpan<byte> data)
    {
        int offset = 0;
        ReadOnlySpan<byte> header = FlntReader.Take(data, ref offset, 0x10);
        uint rawModelCount = BinaryPrimitives.ReadUInt32LittleEndian(header);
        uint instanceCount = BinaryPrimitives.ReadUInt32LittleEndian(header[0x04..]);
        uint version = BinaryPrimitives.ReadUInt32LittleEndian(header[0x08..]);

        uint extraCount0 = 0;
        uint extraCount1 = 0;
        if (version >= 2)
        {
            ReadOnlySpan<byte> extended = FlntReader.Take(data, ref offset, 0x10);
            extraCount0 = BinaryPrimitives.ReadUInt32LittleEndian(extended);
            extraCount1 = BinaryPrimitives.ReadUInt32LittleEndian(extended[0x04..]);
        }

        // The game stops at 512 entries and takes the instances from right after them, whatever the count says.
        int modelCount = (int)Math.Min(rawModelCount, MaxModels);
        ReadOnlySpan<byte> modelTable = FlntReader.Take(data, ref offset, (long)modelCount * ModelEntrySize);
        var models = new FoliageModelEntry[modelCount];
        for (int i = 0; i < modelCount; i++)
        {
            ReadOnlySpan<byte> entry = modelTable.Slice(i * ModelEntrySize, ModelEntrySize);
            ReadOnlySpan<byte> name = entry[1..];
            int nul = name.IndexOf((byte)0);
            if (nul >= 0)
                name = name[..nul];
            models[i] = new FoliageModelEntry(entry[0], Encoding.UTF8.GetString(name));
        }

        FoliageInstance[] instances = FlntReader.TakeArray<FoliageInstance>(data, ref offset, instanceCount);

        uint[] instanceMetadata = [];
        uint[] extraTable1 = [];
        uint[] extraTable0 = [];
        if (version >= 2)
        {
            instanceMetadata = FlntReader.TakeArray<uint>(data, ref offset, instanceCount);
            extraTable1 = FlntReader.TakeArray<uint>(data, ref offset, extraCount1);
            // Four values to a dword; a count of zero has no table at all.
            if (extraCount0 != 0)
                extraTable0 = FlntReader.TakeArray<uint>(data, ref offset, ((extraCount0 - 1) >> 2) + 1);
        }

        return new FoliageTreeFile
        {
            Version = version,
            Models = models,
            Instances = instances,
            InstanceMetadata = instanceMetadata,
            ExtraTable1 = extraTable1,
            ExtraCount0 = extraCount0,
            ExtraTable0Packed = extraTable0,
        };
    }
}

public readonly record struct FoliageCell(uint Word0, uint Word1);

public readonly record struct FoliagePatch(uint Word0, uint Word1, uint Word2);

public sealed class FoliageUndergrowthFile
{
    public const uint MinVersion = 3;

    public uint Version { get; init; }
    public uint[] HeaderParams { get; init; } = new uint[6];
    public FoliageCell[] Cells { get; init; } = [];
    public FoliagePatch[] Patches { get; init; } = [];

    // A sniffing heuristic: the game never compares the file's size, it trusts the name it asked for.
    public static bool IsUndergrowthBuffer(ReadOnlySpan<byte> data)
    {
        if (data.Length < 0x24)
            return false;
        uint version = BinaryPrimitives.ReadUInt32LittleEndian(data);
        if (version < MinVersion)
            return false;

        uint cellCount = BinaryPrimitives.ReadUInt32LittleEndian(data[0x04..]);
        long patchCountOffset = 0x20L + cellCount * 8L;
        if (patchCountOffset > data.Length - 4)
            return false;

        uint patchCount = BinaryPrimitives.ReadUInt32LittleEndian(data[(int)patchCountOffset..]);
        return (9L + cellCount * 2L + patchCount * 3L) * 4L == data.Length;
    }

    public static FoliageUndergrowthFile Load(ReadOnlySpan<byte> data)
    {
        int offset = 0;
        ReadOnlySpan<byte> header = FlntReader.Take(data, ref offset, 8);
        uint version = BinaryPrimitives.ReadUInt32LittleEndian(header);
        if (version < MinVersion)
            throw new InvalidDataException($"Unsupported FLNT undergrowth version {version} (expected >= {MinVersion}).");
        uint cellCount = BinaryPrimitives.ReadUInt32LittleEndian(header[0x04..]);

        uint[] headerParams = FlntReader.TakeArray<uint>(data, ref offset, 6);
        FoliageCell[] cells = FlntReader.TakeArray<FoliageCell>(data, ref offset, cellCount);
        uint patchCount = BinaryPrimitives.ReadUInt32LittleEndian(FlntReader.Take(data, ref offset, 4));
        FoliagePatch[] patches = FlntReader.TakeArray<FoliagePatch>(data, ref offset, patchCount);

        return new FoliageUndergrowthFile
        {
            Version = version,
            HeaderParams = headerParams,
            Cells = cells,
            Patches = patches,
        };
    }
}

internal static class FlntReader
{
    public static ReadOnlySpan<byte> Take(ReadOnlySpan<byte> data, ref int offset, long size)
    {
        if (size > data.Length - offset)
            throw new InvalidDataException($"FLNT file truncated: {size} bytes needed at 0x{offset:X}, file is {data.Length} bytes.");
        ReadOnlySpan<byte> slice = data.Slice(offset, (int)size);
        offset += (int)size;
        return slice;
    }

    public static T[] TakeArray<T>(ReadOnlySpan<byte> data, ref int offset, uint count) where T : unmanaged
    {
        ReadOnlySpan<byte> slice = Take(data, ref offset, count * (long)Unsafe.SizeOf<T>());
        return MemoryMarshal.Cast<byte, T>(slice).ToArray();
    }
}
