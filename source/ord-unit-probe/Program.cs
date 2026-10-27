using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Unicode;

// 게임 프로세스에는 조회 및 메모리 읽기 권한만 요청한다.
const uint ReadOnlyAccess = 0x0400 | 0x0010;
// 버전별 주소와 구조 필드의 공개 참고 근거:
// https://github.com/UnrealKaraulov/iCCupUnrealMaapHaack/blob/main/iCCupUnrealMapHack/iCCupUnrealMapHack/Minimaphack.cs
// 표시·게임 변경 기능은 구현하지 않으며 자료구조 오프셋만 참고한다.
const uint UiRva = 0xD326F0;
const uint PlayerRva = 0xD305E0;
var jsonOptions = new JsonSerializerOptions { WriteIndented = true, Encoder = JavaScriptEncoder.Create(UnicodeRanges.All) };
var lineOptions = new JsonSerializerOptions(jsonOptions) { WriteIndented = false };
var sampleCount = GetInt("--samples", 1, 1, 600);
var interval = GetInt("--interval-ms", args.Contains("--serve") ? 250 : 1000, 100, 60000);
var output = GetString("--output");
var catalogPath = GetString("--catalog");
var names = new Dictionary<string, string>(StringComparer.Ordinal);
var characterIds = new HashSet<string>(StringComparer.Ordinal);
string? catalogHash = null;
if (catalogPath is not null)
{
    using var catalog = JsonDocument.Parse(File.ReadAllText(catalogPath));
    catalogHash = catalog.RootElement.GetProperty("source_sha256").GetString();
    foreach (var unit in catalog.RootElement.GetProperty("units").EnumerateArray())
    {
        var name = unit.GetProperty("propernames").GetString();
        if (string.IsNullOrWhiteSpace(name)) name = unit.GetProperty("name").GetString();
        names.Add(unit.GetProperty("rawcode").GetString()!, name ?? "");
        var sourceName = unit.GetProperty("name").GetString() ?? "";
        if (UnitRules.IsCharacterLabel(sourceName) || UnitRules.IsCharacterLabel(name ?? ""))
            characterIds.Add(unit.GetProperty("rawcode").GetString()!);
    }
}
var mapHashes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
var inventoryIds = new HashSet<string>(characterIds, StringComparer.Ordinal) { "e018", "e0IX", "e01A", "e017", "e016", "e019", "e015" };
var playableCatalogPath = Path.Combine(AppContext.BaseDirectory, "wwwroot", "data", "map-catalog.json");
if (catalogHash is not null && File.Exists(playableCatalogPath))
{
    using var playableCatalog = JsonDocument.Parse(File.ReadAllText(playableCatalogPath));
    if (playableCatalog.RootElement.GetProperty("map").GetProperty("sha256").GetString() == catalogHash)
        foreach (var unit in playableCatalog.RootElement.GetProperty("units").EnumerateArray())
            if (unit.TryGetProperty("playable", out var playable) && playable.ValueKind == JsonValueKind.True)
                inventoryIds.Add(unit.GetProperty("rawcode").GetString()!);
}
if (args.Contains("--fixture"))
{
    ProbeSelfTest.Fixture();
    return;
}
if (GetString("--verify-selection") is { } verifySelection)
{
    Environment.ExitCode = await SelectionVerification.RunAsync(verifySelection,
        GetString("--snapshot") ?? throw new ArgumentException("Missing --snapshot."),
        GetString("--verification-output") ?? throw new ArgumentException("Missing --verification-output."),
        tryNormalFocus: args.Contains("--try-normal-focus"), forceSelection: args.Contains("--force-selection"));
    return;
}
if (args.Contains("--verify-korean-chat"))
{
    Environment.ExitCode = await ChatVerification.RunAsync(
        GetString("--snapshot") ?? throw new ArgumentException("Missing --snapshot."),
        GetString("--verification-output") ?? throw new ArgumentException("Missing --verification-output."),
        tryNormalFocus: args.Contains("--try-normal-focus"));
    return;
}
if (args.Contains("--self-test-special-conditions"))
{
    await GameSpecialRecipeConditionsTests.RunAsync();
    await NikaMaterialChoiceTests.RunAsync();
    return;
}
if (args.Contains("--self-test-craft-speed"))
{
    ChoiceWispWaitPolicyTests.Run();
    ChoiceWispBatchPlanTests.Run();
    ChoiceWispBatchSelectionTests.Run();
    ChoiceWispBatchRecoveryTests.Run();
    ChoiceWispRecoveryCleanupTests.Run();
    ChoiceWispPortraitTimingTests.Run();
    await CraftSnapshotTimingTests.RunAsync();
    ChoiceWispCameraSessionTests.Run();
    GameSelectionGeometryTests.Run();
    GameMinimapProjectionTests.Run();
    SelectionAreaPlanTests.Run();
    GameInputTests.Run();
    KoreanKeyboardPlanTests.Run();
    await GameChatSubmissionTests.RunAsync();
    CraftPlannerTests.Run();
    await GameCraftSequenceTests.RunAsync();
    await GameCraftItemSequenceTests.RunAsync();
    await GameCraftConditionSequenceTests.RunAsync();
    await GameSpecialRecipeConditionsTests.RunAsync();
    await NikaMaterialChoiceTests.RunAsync();
    return;
}
if (args.Contains("--self-test-run-recording"))
{
    GameProgressionTests.Run();
    GameStartingEvolutionTests.Run();
    FilePublicationTests.Run();
    GameRunRecorderTests.Run();
    return;
}
if (args.Contains("--self-test"))
{
    ProbeSelfTest.Run();
    GameResourcesTests.Run();
    GameItemsTests.Run();
    GameProgressionTests.Run();
    GameStartingEvolutionTests.Run();
    CraftItemTests.Run();
    GameRecipeConditionsTests.Run();
    CraftConditionTests.Run();
    SnapshotStabilityTests.Run();
    ChoiceWispWaitPolicyTests.Run();
    ChoiceWispBatchPlanTests.Run();
    ChoiceWispBatchSelectionTests.Run();
    ChoiceWispBatchRecoveryTests.Run();
    ChoiceWispRecoveryCleanupTests.Run();
    ChoiceWispPortraitTimingTests.Run();
    await CraftSnapshotTimingTests.RunAsync();
    ChoiceWispCameraSessionTests.Run();
    ElderDetector.RunSelfTests();
    GameSelectionGeometryTests.Run();
    GameMinimapProjectionTests.Run();
    SelectionAreaPlanTests.Run();
    GameInputTests.Run();
    KoreanKeyboardPlanTests.Run();
    await GameChatSubmissionTests.RunAsync();
    FilePublicationTests.Run();
    GameRunRecorderTests.Run();
    CraftPlannerTests.Run();
    await GameCraftSequenceTests.RunAsync();
    await GameCraftItemSequenceTests.RunAsync();
    await GameCraftConditionSequenceTests.RunAsync();
    await GameSpecialRecipeConditionsTests.RunAsync();
    await NikaMaterialChoiceTests.RunAsync();
    return;
}
if (args.Contains("--serve"))
{
    await LiveCounterServer.RunAsync(CaptureSafely, jsonOptions, lineOptions, GetInt("--port", 8765, 1024, 65535), interval, output);
    return;
}
for (var sample = 0; sample < sampleCount; sample++)
{
    var result = CaptureSafely();
    var json = JsonSerializer.Serialize(result, jsonOptions);
    Console.WriteLine(json);
    if (output is not null)
    {
        var path = Path.GetFullPath(output);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, json + Environment.NewLine, Encoding.UTF8);
        if (sampleCount > 1) File.AppendAllText(path + ".jsonl", JsonSerializer.Serialize(result, lineOptions) + "\n", Encoding.UTF8);
    }
    if (sample + 1 < sampleCount) Thread.Sleep(interval);
}

object CaptureSafely()
{
    try { return Capture(); }
    catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or IOException or OverflowException)
    {
        return new { status = "read_failed", timestamp = DateTimeOffset.UtcNow, error = ex.Message };
    }
}

object Capture()
{
    var candidates = Process.GetProcesses().Where(p =>
        p.ProcessName.Equals("War3", StringComparison.OrdinalIgnoreCase) ||
        p.ProcessName.Equals("Warcraft III", StringComparison.OrdinalIgnoreCase)).ToArray();
    try
    {
        if (candidates.Length != 1)
            return new { status = candidates.Length == 0 ? "game_not_running" : "multiple_games", timestamp = DateTimeOffset.UtcNow, processCount = candidates.Length };
        var process = candidates[0];
        ProcessModule? game = null;
        ProcessModule? storm = null;
        var relatedModules = new List<string>();
        foreach (ProcessModule module in process.Modules)
        {
            if (module.ModuleName.Equals("Game.dll", StringComparison.OrdinalIgnoreCase)) game = module;
            if (module.ModuleName.Equals("Storm.dll", StringComparison.OrdinalIgnoreCase)) storm = module;
            if (module.ModuleName.Contains("TMO", StringComparison.OrdinalIgnoreCase)) relatedModules.Add(module.ModuleName);
        }
        if (game is null) return new { status = "game_module_not_ready", timestamp = DateTimeOffset.UtcNow, processId = process.Id };
        var version = game.FileVersionInfo.FileVersion;
        if (version != "1.28.5.7680")
            return new { status = "unsupported_game_version", timestamp = DateTimeOffset.UtcNow, processId = process.Id, gameVersion = version };
        using var memory = new MemoryReader(process.Id, ReadOnlyAccess);
        var moduleBase = checked((uint)game.BaseAddress.ToInt64());
        var loadedMap = storm is null ? null : LoadedMapReader.Read(memory, checked((uint)storm.BaseAddress.ToInt64()));
        string? loadedMapHash = null;
        if (loadedMap is not null)
        {
            if (!mapHashes.TryGetValue(loadedMap, out loadedMapHash))
            {
                using var file = File.OpenRead(loadedMap);
                loadedMapHash = Convert.ToHexStringLower(SHA256.HashData(file));
                mapHashes.Add(loadedMap, loadedMapHash);
            }
        }
        var gameState1 = memory.U32(moduleBase + 0xD328D0);
        var gameState2 = memory.U32(moduleBase + 0xD30110);
        var roots = memory.ReadRoots(moduleBase + UiRva, moduleBase + PlayerRva);
        if (roots is null) return new { status = "world_not_ready", timestamp = DateTimeOffset.UtcNow, processId = process.Id, gameVersion = version };
        var data = memory.ReadOwnedUnits(roots);
        var middle = memory.ReadRoots(moduleBase + UiRva, moduleBase + PlayerRva);
        var second = SnapshotStability.SameSession(middle, roots) ? memory.ReadOwnedUnits(middle!) : null;
        var after = memory.ReadRoots(moduleBase + UiRva, moduleBase + PlayerRva);
        var stable = SnapshotStability.SameSession(middle, roots) && SnapshotStability.SameSession(after, roots);
        var stableUnits = SnapshotStability.SameInventory(data, second, inventoryIds);
        var readErrors = data.ReadErrors + (second?.ReadErrors ?? 0);
        var elder = stable ? ElderDetector.Capture(memory, roots, moduleBase, loadedMapHash)
                           : ElderDetector.Unknown("session_changed");
        var resources = stable ? GameResources.Capture(memory, roots, moduleBase, version)
                               : GameResources.Unknown("session_changed");
        var items = stable ? GameItems.Capture(memory, roots, moduleBase, version, loadedMapHash)
                           : GameItems.Unknown("session_changed");
        var recipeConditions = stable ? GameRecipeConditions.Capture(memory, roots, moduleBase, version, loadedMapHash)
                                      : GameRecipeConditions.Unknown("session_changed");
        var progression = stable ? GameProgression.Capture(memory, roots, moduleBase, version, loadedMapHash)
                                 : GameProgression.Unknown("session_changed");
        var startingEvolution = stable && progression.Status == "confirmed" && progression.ActivePlayer == true && progression.GameEnded == false
            ? GameStartingEvolution.Capture(memory, roots, moduleBase, version, loadedMapHash)
            : GameStartingEvolution.Unknown(stable ? "progression_not_active" : "session_changed");
        return new
        {
            status = !stable ? "world_changed" : readErrors > 0 ? "partial_read" : !stableUnits ? "units_changed" : "snapshot_read",
            timestamp = DateTimeOffset.UtcNow,
            processId = process.Id,
            gameVersion = version,
            loadedMap,
            loadedMapHash,
            catalogMatchesLoadedArchive = catalogHash is not null && loadedMapHash == catalogHash,
            gameState1,
            gameState2,
            localPlayer = roots.LocalPlayer,
            sessionIdentity = $"{roots.Ui:X8}:{roots.Players:X8}:{roots.Table:X8}",
            totalWorldEntries = roots.Count,
            ownObjectCount = data.Units.Count,
            readErrors,
            stableRoots = stable,
            stableUnits,
            elder,
            resources,
            items,
            recipeConditions,
            progression,
            startingEvolution,
            relatedModules,
            // 위습·건물·더미가 포함될 수 있으므로 확정한 캐릭터 보유량과 구분한다.
            counts = data.Units.Where(UnitRules.IsActive).GroupBy(u => u.Rawcode).OrderBy(g => g.Key).Select(g => new
            {
                rawcode = g.Key,
                name = names.GetValueOrDefault(g.Key),
                count = g.Count(),
                isCharacter = characterIds.Contains(g.Key),
                flagVariants = g.GroupBy(u => (u.Flags20, u.Flags5c)).Select(v => new
                { flags20 = $"0x{v.Key.Flags20:X8}", flags5c = $"0x{v.Key.Flags5c:X8}", count = v.Count() })
            }),
            excludedDeadOrHidden = data.Units.Count(u => !UnitRules.IsActive(u)),
            validation = "Character labels use this map's catalog. Special evolution/cosmetic cases require separate validation."
        };
    }
    finally { foreach (var candidate in candidates) candidate.Dispose(); }
}

string? GetString(string option)
{
    var index = Array.IndexOf(args, option);
    if (index < 0) return null;
    if (index + 1 >= args.Length) throw new ArgumentException($"Missing value for {option}");
    return args[index + 1];
}
int GetInt(string option, int fallback, int min, int max)
{
    var text = GetString(option);
    if (text is null) return fallback;
    if (!int.TryParse(text, out var value) || value < min || value > max)
        throw new ArgumentException($"{option} must be between {min} and {max}.");
    return value;
}

record Roots(uint Ui, uint Players, uint Table, uint Array, int Count, int LocalPlayer);
record UnitObject(uint Address, string Rawcode, uint Flags20, uint Flags5c);
record OwnedData(List<UnitObject> Units, int ReadErrors);

sealed class MemoryReader : IDisposable
{
    private readonly IntPtr handle;
    public MemoryReader(int processId, uint rights)
    {
        handle = Native.OpenProcess(rights, false, processId);
        if (handle == IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error());
    }
    public byte[] Read(uint address, int size)
    {
        if (address < 0x10000 || size < 0 || size > 4_000_000) throw new IOException("Invalid read range.");
        var data = new byte[size];
        if (!Native.ReadProcessMemory(handle, (IntPtr)(long)address, data, (nuint)size, out var actual) || actual != (nuint)size)
            throw new Win32Exception(Marshal.GetLastWin32Error(), $"Read failed at 0x{address:X8} ({size} bytes).");
        return data;
    }
    public uint U32(uint address) => BitConverter.ToUInt32(Read(address, 4));
    public Roots? ReadRoots(uint uiAddress, uint playersAddress)
    {
        var ui = U32(uiAddress);
        var players = U32(playersAddress);
        if (ui < 0x10000 || players < 0x10000) return null;
        var localPlayer = BitConverter.ToUInt16(Read(players + 0x28, 2));
        if (localPlayer > 31) throw new IOException("Invalid local player ID.");
        var table = U32(ui + 0x3BC);
        if (table < 0x10000) return null;
        var header = Read(table + 0x604, 8);
        var count = BitConverter.ToInt32(header, 0);
        var array = BitConverter.ToUInt32(header, 4);
        if (count < 0 || count > 100_000) throw new IOException("Invalid unit-list length.");
        if (count > 0 && array < 0x10000) throw new IOException("Invalid unit-list pointer.");
        return new Roots(ui, players, table, array, count, localPlayer);
    }
    public OwnedData ReadOwnedUnits(Roots roots)
    {
        var units = new List<UnitObject>();
        var seen = new HashSet<uint>();
        var errors = 0;
        var pointers = roots.Count == 0 ? [] : Read(roots.Array, roots.Count * 4);
        for (var i = 0; i < roots.Count; i++)
        {
            var pointer = BitConverter.ToUInt32(pointers, i * 4);
            if (pointer < 0x10000 || !seen.Add(pointer)) continue;
            try
            {
                // 타 플레이어의 유닛 정보는 출력하지 않도록 소유자부터 확인한다.
                if (U32(pointer + 0x58) != roots.LocalPlayer) continue;
                var info = Read(pointer + 0x20, 0x40);
                // 객체 재사용·소유권 변경 중 읽은 자료는 유효한 보유량에 포함하지 않는다.
                if (BitConverter.ToUInt32(info, 0x38) != roots.LocalPlayer ||
                    U32(pointer + 0x58) != roots.LocalPlayer ||
                    U32(pointer + 0x30) != BitConverter.ToUInt32(info, 0x10))
                { errors++; continue; }
                var rawcode = DecodeRawcode(info.AsSpan(0x10, 4));
                if (rawcode is null) { errors++; continue; }
                units.Add(new UnitObject(pointer, rawcode, BitConverter.ToUInt32(info, 0), BitConverter.ToUInt32(info, 0x3C)));
            }
            catch (Exception ex) when (ex is Win32Exception or IOException) { errors++; }
        }
        return new OwnedData(units, errors);
    }
    public static string? DecodeRawcode(ReadOnlySpan<byte> source)
    {
        if (source.Length != 4) return null;
        var chars = new char[4];
        for (var i = 0; i < 4; i++)
        {
            var value = source[3 - i];
            if (value < 0x20 || value > 0x7E) return null;
            chars[i] = (char)value;
        }
        return new string(chars);
    }
    public void Dispose() { Native.CloseHandle(handle); }
}

static class Native
{
    [DllImport("kernel32.dll", SetLastError = true)]
    internal static extern IntPtr OpenProcess(uint rights, bool inherit, int processId);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool ReadProcessMemory(IntPtr process, IntPtr address, byte[] buffer, nuint size, out nuint actual);
    [DllImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool CloseHandle(IntPtr handle);
}

