using System.ComponentModel;
using System.Text.Json.Serialization;

// 게임 메모리는 읽기만 한다. 지원 버전의 공개 고정 경로가 불안정하면 자원만 미확인으로 격리한다.
static class GameResources
{
    internal const string SupportedVersion = "1.28.5.7680";
    private const uint UiRva = 0xD326F0;
    private const uint PlayersRva = 0xD305E0;
    private const uint GameRva = 0xD30448;
    private const int Scale = 10;
    // 이 진단이 지원하는 보수적 범위다. 범위를 넘는 값은 보정하거나 잘라 쓰지 않는다.
    internal const int MaximumSupportedAmount = 1_000_000;

    // GetPlayerState(state5): CPlayer+(state+4)*16 -> native handle+generation -> value+0x78, integer divisor1.
    // 같은 버전 PE의2342CF/0805A0/07E090 경로를 검증하며 금화·목재의 /10값을 추정에 쓰지 않는다.
    internal static int? CaptureSkillPointsForTests(Func<uint,int,byte[]> read, Func<Roots?> readRoots, Roots roots, uint moduleBase, string? version)
    {
        if (version != SupportedVersion || roots.LocalPlayer is < 0 or > 11 || !Pointer(moduleBase)) return null;
        try
        {
            foreach (var (rva, bytes) in SkillPointSignatures(moduleBase))
                if (!Read(checked(moduleBase+rva),bytes.Length).AsSpan().SequenceEqual(bytes)) return null;
            if (!SnapshotStability.SameSession(readRoots(),roots)) return null;
            var first=Take();if (!SnapshotStability.SameSession(readRoots(),roots)) return null;
            var second=Take();if (!SnapshotStability.SameSession(readRoots(),roots)||first!=second) return null;
            return first.Value is >=0 and <=MaximumSupportedAmount ? first.Value : null;
            (uint Player,uint Handles,uint Index,uint Serial,uint ValueObject,int Value) Take()
            {
                var player=Ptr(U32(checked(roots.Players+0x58+(uint)roots.LocalPlayer*4)));
                if(Read(checked(player+0x30),1)[0]!=roots.LocalPlayer)throw new IOException("Skill-point player changed.");
                var index=U32(checked(player+0x98));var serial=U32(checked(player+0x9C));var handles=Ptr(U32(checked(moduleBase+GameRva)));
                var offset=(index&0x80000000)!=0?0x20u:0u;var slot=index&0x7FFFFFFF;
                var length=U32(checked(handles+0x1C+offset));if(length>2_000_000||slot>=length)throw new IOException("Invalid skill-point handle.");
                var array=Ptr(U32(checked(handles+0xC+offset)));var record=checked(array+slot*8);
                if(U32(record)!=0xFFFFFFFE)throw new IOException("Stale skill-point handle.");
                var value=Ptr(U32(checked(record+4)));if(U32(checked(value+0x18))!=serial)throw new IOException("Skill-point generation changed.");
                return(player,handles,index,serial,value,unchecked((int)U32(checked(value+0x78))));
            }
            uint Ptr(uint p)=>Pointer(p)?p:throw new IOException("Invalid skill-point pointer.");
            byte[] Read(uint p,int n){if(!Pointer(p))throw new IOException("Invalid skill-point read.");var bytes=read(p,n);if(bytes.Length!=n)throw new IOException("Short skill-point read.");return bytes;}
            uint U32(uint p)=>BitConverter.ToUInt32(Read(p,4));
        }
        catch(Exception error)when(error is IOException or Win32Exception or OverflowException){return null;}
    }
    internal static (uint Rva,byte[] Bytes)[] SkillPointSignatures(uint moduleBase)=>[
        (0x2342CF,[0x8D,0x4E,4,0xC1,0xE1,4,0x57,3,0xC8,0xE8,0xC3,0xC2,0xE4,0xFF]),
        (0x0805A0,[0x8B,0x51,0x0C,0x8B,0x49,8,0xE8,0xE5,0xDA,0xFF,0xFF,0x8B,0x40,0x78,0xC3]),
        (0x07E092,[0x8B,0x35,..BitConverter.GetBytes(checked(moduleBase+GameRva))]),
        (0x07E0DD,[0x8B,0x4C,0xC8,4,0x33,0xC0,0x39,0x59,0x18,0x5B,0x0F,0x44,0xC1])];
    private static readonly uint[] PlayerOffsets =
        [0x190, 0x1410, 0x26A0, 0x3920, 0x4BB0, 0x5E30, 0x70C0, 0x8350, 0x95D0, 0xA860, 0xBAE0, 0xCD70];
    private static readonly string[] PrimarySources =
    [
        "https://github.com/Still4/War3Trainer/blob/master/War3Trainer/GameContext.cs#L135-L138",
        "https://github.com/Still4/War3Trainer/blob/master/War3Trainer/War3Common.cs#L12-L39",
        "https://github.com/Still4/War3Trainer/blob/master/War3Trainer/GameTrainer.cs#L344-L373"
    ];

    internal sealed record State(
        [property: JsonPropertyName("status")] string Status,
        [property: JsonPropertyName("gold")] int? Gold,
        [property: JsonPropertyName("wood")] int? Wood,
        [property: JsonPropertyName("localPlayer")] int? LocalPlayer,
        [property: JsonPropertyName("source")] Evidence Source);

    internal sealed record Evidence(
        [property: JsonPropertyName("method")] string Method,
        [property: JsonPropertyName("reason")] string Reason,
        [property: JsonPropertyName("readOnly")] bool ReadOnly,
        [property: JsonPropertyName("scale")] int Scale,
        [property: JsonPropertyName("sampleCount")] int SampleCount,
        [property: JsonPropertyName("rawGold")] int? RawGold,
        [property: JsonPropertyName("rawWood")] int? RawWood,
        [property: JsonPropertyName("valueValidation")] string ValueValidation,
        [property: JsonPropertyName("hudCrossCheckPerformed")] bool HudCrossCheckPerformed,
        [property: JsonPropertyName("primarySources")] string[] PrimarySources);

    private readonly record struct Sample(uint Game, uint Handles, uint First, uint Block, int Gold, int Wood);

    public static State Unknown(string reason) => new("unknown", null, null, null,
        Source(reason, 0, null, null));

    public static State Capture(MemoryReader memory, Roots roots, uint moduleBase, string? gameVersion) =>
        CaptureForTests(memory.Read,
            () => memory.ReadRoots(checked(moduleBase + UiRva), checked(moduleBase + PlayersRva)),
            roots, moduleBase, gameVersion);

    internal static State CaptureForTests(Func<uint, int, byte[]> read, Func<Roots?> readRoots,
        Roots roots, uint moduleBase, string? gameVersion)
    {
        if (!string.Equals(gameVersion, SupportedVersion, StringComparison.Ordinal)) return Unknown("unsupported_version");
        if (roots.LocalPlayer < 0 || roots.LocalPlayer >= PlayerOffsets.Length) return Unknown("unsupported_local_player");
        if (!Pointer(moduleBase) || !Pointer(roots.Ui) || !Pointer(roots.Players) || !Pointer(roots.Table))
            return Unknown("invalid_session_pointer");
        try
        {
            if (!SameSession(readRoots(), roots)) return Unknown("session_changed");
            var first = ReadSample();
            if (!SameSession(readRoots(), roots)) return Unknown("session_changed");
            var second = ReadSample();
            if (!SameSession(readRoots(), roots)) return Unknown("session_changed");
            if (first != second) return Unknown("resources_changing");
            if (!SupportedRaw(first.Gold) || !SupportedRaw(first.Wood)) return Unknown("resource_out_of_supported_range");
            // 음수가 없으므로 정수 나눗셈은 표시 가능한 완전한 자원 수량의 내림값이다.
            return new("confirmed", first.Gold / Scale, first.Wood / Scale, roots.LocalPlayer,
                Source("same_version_primary_path_and_two_matching_reads", 2, first.Gold, first.Wood));

            Sample ReadSample()
            {
                var game = U32(checked(moduleBase + GameRva));
                if (!Pointer(game)) throw new IOException("Invalid game-resource root.");
                var handles = U32(checked(game + 0xC));
                if (!Pointer(handles)) throw new IOException("Invalid game-resource handle array.");
                var firstHandle = U32(checked(handles + 1 * 8 + 4));
                if (!Pointer(firstHandle)) throw new IOException("Invalid indexed resource block pointer.");
                var block = firstHandle & 0xFFFF0000;
                if (!Pointer(block)) throw new IOException("Invalid resource block.");
                var goldAddress = checked(block + PlayerOffsets[roots.LocalPlayer]);
                var woodAddress = checked(goldAddress + 0x80);
                return new(game, handles, firstHandle, block, I32(goldAddress), I32(woodAddress));
            }
            uint U32(uint address)
            {
                var bytes = read(address, 4);
                if (bytes.Length != 4) throw new IOException("Short resource pointer read.");
                return BitConverter.ToUInt32(bytes);
            }
            int I32(uint address)
            {
                var bytes = read(address, 4);
                if (bytes.Length != 4) throw new IOException("Short resource value read.");
                return BitConverter.ToInt32(bytes);
            }
        }
        catch (Exception error) when (error is IOException or Win32Exception or OverflowException)
        {
            return Unknown("resource_read_failed");
        }
    }

    private static bool Pointer(uint value) => value >= 0x10000 && value <= 0xFFFF0000;
    private static bool SupportedRaw(int value) => value >= 0 && value <= MaximumSupportedAmount * Scale;
    private static bool SameSession(Roots? actual, Roots expected) => actual is not null &&
        actual.Ui == expected.Ui && actual.Players == expected.Players && actual.Table == expected.Table &&
        actual.LocalPlayer == expected.LocalPlayer;
    private static Evidence Source(string reason, int samples, int? rawGold, int? rawWood) => new(
        "read_only_1285_native_resource_block", reason, true, Scale, samples, rawGold, rawWood,
        "two matching addresses and raw values; nonnegative Int32/10 floored; supported 0..1000000; HUD not sampled",
        false, PrimarySources);
}
