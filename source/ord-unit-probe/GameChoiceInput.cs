using System.ComponentModel;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;

// 선택 위습도 읽기 전용 메모리와 정상 선택·이동 입력만 사용한다.
static class GameChoiceInput
{
    public static bool Supported => true;
    internal static readonly string[] RegionResults = ["h003", "h002", "h001", "h007", "h004", "h008", "h005", "h009", "h006"];
    // 지원 맵 2.323[C]의 war3map.w3e에서 확인한 9개 선택 구역의 평평한 지형 높이.
    internal const float RegionGroundZ = 9.5f;

    internal sealed record ExecutionResult(string? Error, bool ConsumedWispVerified);
    internal sealed record WispSample(uint Address, string Identity, string? Rawcode, uint Owner, uint Flags20, uint Flags5c)
    {
        internal bool Active => Rawcode is not null && UnitRules.IsActive(new(Address, Rawcode, Flags20, Flags5c));
        internal string? Header { get; init; }
    }

    internal static string WispIdentity(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length < 0x20) throw new ArgumentException("A complete unit identity header is required.", nameof(bytes));
        Span<byte> identity = stackalloc byte[12];
        bytes[..4].CopyTo(identity);
        // 로컬 RVA40E7CA/40E7CD ->07E090: +0C index/+10 generation으로 유효 객체를 조회한다.
        // +04 참조 횟수와 나머지 linked/cache 필드는 카메라·선택 갱신 중 변할 수 있다.
        bytes.Slice(0x0C, 8).CopyTo(identity[4..]);
        return Convert.ToHexString(identity);
    }

    internal static WispSample ReadWispSample(MemoryReader memory, uint address)
    {
        var bytes = memory.Read(address, 0x60);
        return new(address, WispIdentity(bytes), MemoryReader.DecodeRawcode(bytes.AsSpan(0x30, 4)),
            BitConverter.ToUInt32(bytes, 0x58), BitConverter.ToUInt32(bytes, 0x20), BitConverter.ToUInt32(bytes, 0x5C))
            { Header = Convert.ToHexString(bytes.AsSpan(0, 0x20)) };
    }

    internal static bool SameWisp(WispSample before, WispSample after, int localPlayer) =>
        before.Address >= 0x10000 && before.Address == after.Address && before.Identity.Length == 24 && before.Identity == after.Identity &&
        before.Rawcode == "e018" && after.Rawcode == "e018" && before.Owner == localPlayer && after.Owner == localPlayer;

    internal static void LogWispMismatch(string stage, WispSample before, WispSample after)
    {
        var difference = "unknown";
        if (before.Header is { Length: 64 } first && after.Header is { Length: 64 } second)
        {
            difference = "none";
            for (var index = 0; index < 0x20; index++)
                if (!first.AsSpan(index * 2, 2).SequenceEqual(second.AsSpan(index * 2, 2)))
                { difference = $"0x{index:X2}"; break; }
        }
        Console.WriteLine($"Choice source mismatch: stage={stage}, source=0x{before.Address:X8}, firstHeaderDifference={difference}, identityEqual={before.Identity == after.Identity}, rawEqual={before.Rawcode == after.Rawcode}, ownerEqual={before.Owner == after.Owner}, active={after.Active}.");
    }

    internal static bool VerifyConsumption(WispSample before, WispSample first, WispSample second, int localPlayer) =>
        before.Active && SameWisp(before, first, localPlayer) && SameWisp(before, second, localPlayer) && !first.Active && !second.Active;

    internal static bool VerifyBatchConsumption(IReadOnlyList<WispSample> before, IReadOnlyList<WispSample> first,
        IReadOnlyList<WispSample> second, int localPlayer) => ChoiceWispBatchSelectionPolicy.ValidCount(before.Count) &&
        first.Count == before.Count && second.Count == before.Count && before.Select(unit => unit.Address).Distinct().Count() == before.Count &&
        Enumerable.Range(0, before.Count).All(index => VerifyConsumption(before[index], first[index], second[index], localPlayer));

    public static async Task<string?> ExecuteAsync(string resultRawcode, JsonElement baseline, CancellationToken cancel)
        => (await ExecuteWithProofAsync(resultRawcode, baseline, cancel)).Error;

    internal static async Task<ExecutionResult> ExecuteWithProofAsync(string resultRawcode, JsonElement baseline, CancellationToken cancel, int count = 1)
    {
        try
        {
            if (!RegionResults.Contains(resultRawcode, StringComparer.Ordinal))
                return new("선택 위습으로 뽑을 수 없는 유닛입니다.", false);
            if (!ChoiceWispBatchSelectionPolicy.ValidCount(count)) return new("선택 위습은 한 번에 1개부터 12개까지 사용할 수 있습니다.", false);
            using var process = Process.GetProcessById(baseline.GetProperty("processId").GetInt32());
            var game = process.Modules.Cast<ProcessModule>().SingleOrDefault(module => module.ModuleName.Equals("Game.dll", StringComparison.OrdinalIgnoreCase));
            var storm = process.Modules.Cast<ProcessModule>().SingleOrDefault(module => module.ModuleName.Equals("Storm.dll", StringComparison.OrdinalIgnoreCase));
            if (game?.FileVersionInfo.FileVersion != "1.28.5.7680" || storm is null) return new("지원하는 게임 버전을 확인하지 못했습니다.", false);
            var moduleBase = checked((uint)game.BaseAddress.ToInt64());
            using var memory = new MemoryReader(process.Id, 0x0410);
            var map = LoadedMapReader.Read(memory, checked((uint)storm.BaseAddress.ToInt64()));
            if (map is null || !string.Equals(map, baseline.GetProperty("loadedMap").GetString(), StringComparison.OrdinalIgnoreCase))
                return new("실행 중인 맵이 바뀌어 위습을 사용하지 않았습니다.", false);
            using (var file = File.OpenRead(map))
                if (!string.Equals(Convert.ToHexStringLower(SHA256.HashData(file)), baseline.GetProperty("loadedMapHash").GetString(), StringComparison.OrdinalIgnoreCase))
                    return new("실행 중인 맵을 확인하지 못했습니다.", false);
            var roots = memory.ReadRoots(moduleBase + 0xD326F0, moduleBase + 0xD305E0);
            if (roots is null || roots.LocalPlayer != baseline.GetProperty("localPlayer").GetInt32() ||
                baseline.GetProperty("sessionIdentity").GetString() != $"{roots.Ui:X8}:{roots.Players:X8}:{roots.Table:X8}")
                return new("게임이나 플레이어가 바뀌어 위습을 사용하지 않았습니다.", false);
            var dependency = new CraftCommand("WISP", resultRawcode, count, [new("e018", count)], null, null, ["e018"]);
            if (!GameInput.TryReadCommandInventory(dependency, roots, memory.ReadOwnedUnits,
                () => memory.ReadRoots(moduleBase + 0xD326F0, moduleBase + 0xD305E0), out var owned))
                return new("유닛 정보가 바뀌어 위습을 사용하지 않았습니다.", false);
            var window = process.MainWindowHandle;
            if (window == IntPtr.Zero) return new("워크래프트 창을 열어 주세요.", false);
            var consumed = false;
            var error = await GameSelection.UseChoiceWispAsync(memory, moduleBase, roots, window, resultRawcode,
                owned.Units.Where(UnitRules.IsActive).ToArray(), cancel, () => consumed = true, count);
            return new(error, consumed);
        }
        catch (Exception error) when (error is Win32Exception or IOException or InvalidOperationException or ArgumentException or OverflowException)
        {
            return new("선택 위습의 입력 결과를 확인하지 못했습니다. 게임에서 결과를 확인해 주세요.", false);
        }
    }
}
