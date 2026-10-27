using System.ComponentModel;
using System.Text;

// 공개 OpenCirnix의 1.28.5.7680용 Storm 자료구조를 독립적으로 판독한다.
// https://github.com/BlacklightsC/OpenCirnix/blob/master/Cirnix.Memory/LoadedFiles.cs
// 열린 맵 아카이브의 경로이며, 현재 경기의 맵임을 단독으로 보증하지는 않는다.
internal static class LoadedMapReader
{
    private const uint ArchiveListRva = 0x5817C;
    private const uint PathTableOffset = 0xA0;
    private const uint PathSlotStride = 0x268;
    private const uint PathScanLimit = 0x1F64;
    private const int MaximumNodes = 128;
    private const int PathBufferLength = 0x100;
    private static readonly byte[] ArchiveSignature = [0x53, 0x8E, 0x3F, 0x7F, 0x53];
    private static readonly byte[] LoadedMarker = [0x68, 0x02, 0x04];
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    public static string? Read(MemoryReader memory, uint stormBase)
    {
        ArgumentNullException.ThrowIfNull(memory);
        if (stormBase < 0x10000) return null;

        try
        {
            var rootAddress = checked(stormBase + ArchiveListRva);
            var firstNode = memory.U32(rootAddress);
            var node = firstNode;
            var visited = new HashSet<uint>();

            for (var step = 0; step < MaximumNodes; step++)
            {
                // 잘못된 포인터, 순환 목록, 종료 포인터에서는 판독을 중단한다.
                if (node < 0x10000 || !visited.Add(node)) return null;
                var header = memory.Read(node, 4 + ArchiveSignature.Length);
                if (header.AsSpan(4).SequenceEqual(ArchiveSignature))
                {
                    var path = ReadPathTable(memory, checked(node + PathTableOffset));
                    if (path is null) return null;

                    // 판독 도중 목록이 교체되었으면 이전 경로를 확정하지 않는다.
                    if (memory.U32(rootAddress) != firstNode ||
                        !memory.Read(node, header.Length).AsSpan().SequenceEqual(header))
                        return null;
                    return path;
                }
                node = BitConverter.ToUInt32(header, 0);
            }
        }
        catch (Exception error) when (error is Win32Exception or IOException or OverflowException)
        {
            // 게임 종료, 로딩 전환, 미지원 Storm 빌드는 식별 실패로 처리한다.
        }
        return null;
    }

    private static string? ReadPathTable(MemoryReader memory, uint table)
    {
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (uint offset = 0; offset < PathScanLimit; offset += PathSlotStride)
        {
            var path = ReadExistingPath(memory, checked(table + offset));
            if (path is not null) paths.Add(path);
            // 여러 맵 아카이브가 열렸다면 임의로 하나를 선택하지 않는다.
            if (paths.Count > 1) return null;
        }
        return paths.Count == 1 ? paths.Single() : null;
    }

    private static string? ReadExistingPath(MemoryReader memory, uint address)
    {
        try
        {
            var markerAddress = checked(address - 0x10);
            if (!memory.Read(markerAddress, LoadedMarker.Length).AsSpan().SequenceEqual(LoadedMarker))
                return null;

            var buffer = memory.Read(address, PathBufferLength);
            var terminator = Array.IndexOf(buffer, (byte)0);
            if (terminator <= 0) return null;
            var path = StrictUtf8.GetString(buffer, 0, terminator);
            var extension = Path.GetExtension(path);
            if (!extension.Equals(".w3x", StringComparison.OrdinalIgnoreCase) &&
                !extension.Equals(".w3m", StringComparison.OrdinalIgnoreCase))
                return null;

            if (!File.Exists(path)) return null;
            var fullPath = Path.GetFullPath(path);

            // 경로 슬롯이 재사용되는 중간 상태를 반환하지 않는다.
            if (!memory.Read(markerAddress, LoadedMarker.Length).AsSpan().SequenceEqual(LoadedMarker) ||
                !memory.Read(address, PathBufferLength).AsSpan().SequenceEqual(buffer))
                return null;
            return fullPath;
        }
        catch (Exception error) when (error is Win32Exception or IOException or OverflowException or ArgumentException)
        {
            return null;
        }
    }
}
