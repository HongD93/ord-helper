using System.ComponentModel;
using System.Text;
using System.Text.Json;

static class GameProgressionTests
{
    public static void Run()
    {
        (string Name, Action Test)[] tests = [
            ("named native globals and current-round exclusion", Known),
            ("victory remains terminal even while the player stays active", Victory),
            ("defeat is terminal when the active player flag clears", Defeat),
            ("known zero never becomes missing or active", KnownZero),
            ("Storm short-name hash vectors", HashVectors),
            ("unsupported version map player never reads", Unsupported),
            ("all real session boundaries reject changes", SessionChanges),
            ("enemy array changes preserve the local session", WorldChurn),
            ("rebased native instruction evidence is required", Signatures),
            ("a changing round cannot become a mixed sample", Changing),
            ("global table identity changes reject equal values", ChangingTable),
            ("exact case-sensitive names survive hash collisions", ExactNames),
            ("global scalar and array types must match", Types),
            ("native scalar initialization and assignment value tags stay exact", ScalarValueTags),
            ("native slot state is independent of map progression failures", PlayerSlotStates),
            ("type mismatch diagnostics preserve the exact observed variable without publishing progress", TypeDiagnostics),
            ("invalid native pointers and masks fail closed", InvalidStructures),
            ("cyclic collision chains and missing variables are unknown", CollisionFailure),
            ("array length and boolean value are checked", Arrays),
            ("local-player identity is separately read", PlayerIdentity),
            ("map-supported values stay bounded", ValueBounds),
            ("short failed and exceptional reads publish no partial fields", ReadFailures),
            ("separate captures players and states do not reuse data", NoCache),
            ("unknown serialization does not fabricate zero rewards", Serialization)
        ];
        foreach (var (name, test) in tests)
            try { test(); }
            catch (Exception error) { throw new Exception("Progression test failed: " + name, error); }
        Console.WriteLine($"PASS: read-only named JASS progression {tests.Length}/{tests.Length} cases; no game input or writes.");
        GameRandomStateTests.Run();
    }

    private static void Known()
    {
        var f = new Fixture(); var state = f.Capture();
        Check(state.Status == "confirmed" && state.Round == 35 && state.StoryIndex == 14 && state.ActivePlayer == true &&
            state.Difficulty == "nightmare" && state.DifficultyValue == 6 && state.GameEnded == false && state.RoundTransitionDepth == 0 && state.LocalPlayer == 0,
            "the six exact named globals publish coherent round/story/player state");
        Check(state.Phase == "unknown" && state.RewardBoundary == "exclude_current_round" && state.ClaimedTechs.Count == 0 &&
            state.Source.ReadOnly && state.Source.SampleCount == 2 && !state.Source.PhaseRead && !state.Source.ClaimFlagsRead &&
            state.Source.MapSha256 == GameProgression.SupportedMapHash && state.Victory == false,
            "timer phase and claim flags are not guessed from a current round");
    }
    private static void Victory()
    {
        var f=new Fixture();f.Value("YU",1);var state=f.Capture();
        Check(state.Status=="confirmed"&&state.GameEnded==true&&state.ActivePlayer==true&&state.Victory==true,"a victory entry cannot count future rewards just because Ji remains true");
        f.Value("YU",2);Unknown(f.Capture());
        f=new Fixture();f.Write(f.Nodes["YU"]+0x1C,4);Unknown(f.Capture());
    }
    private static void Defeat()
    {
        var f=new Fixture();f.Write(Fixture.ArrayData,0);var state=f.Capture();
        Check(state.Status=="confirmed"&&state.GameEnded==true&&state.ActivePlayer==false&&state.Victory==false,"the per-player inactive path clears Ji without requiring the shared victory flag");
    }
    private static void KnownZero()
    {
        var f = new Fixture();f.Value("X3",0);f.Value("on",0);f.Value("NE",0);f.Write(Fixture.ArrayData,0);
        var state=f.Capture();Check(state.Status=="confirmed"&&state.Round==0&&state.StoryIndex==0&&state.ActivePlayer==false&&state.Difficulty=="unknown","preparation values are explicit known zero rather than absent values");
    }
    private static void HashVectors()
    {
        // 공개 YDWE/Storm Jenkins 원식을 별도 Python으로 계산한 고정 벡터다.
        Check(GameProgression.NameHash("X3")==0xD310CF7A && GameProgression.NameHash("on")==0x7FDD3CE2 &&
            GameProgression.NameHash("Ji")==0xE008DDBA, "independent short-name hash vectors");
        Check(GameProgression.NameHash("Ji")==GameProgression.NameHash("jI"),"only the hash is case normalized");
    }
    private static void Unsupported()
    {
        var f=new Fixture();byte[] Forbidden(uint _,int __)=>throw new Exception("Unexpected read");
        Unknown(GameProgression.CaptureForTests(Forbidden,()=>f.Roots,f.Roots,Fixture.Base,"1.26",GameProgression.SupportedMapHash));
        Unknown(GameProgression.CaptureForTests(Forbidden,()=>f.Roots,f.Roots,Fixture.Base,GameProgression.SupportedVersion,"other-map"));
        foreach(var player in new[]{-1,4})Unknown(GameProgression.CaptureForTests(Forbidden,()=>f.Roots,f.Roots with{LocalPlayer=player},Fixture.Base,GameProgression.SupportedVersion,GameProgression.SupportedMapHash));
    }
    private static void SessionChanges()
    {
        foreach(var boundary in new[]{1,2,3})foreach(var part in new[]{0,1,2,3})
        {
            var f=new Fixture();var count=0;var altered=part switch{0=>f.Roots with{Ui=f.Roots.Ui+4},1=>f.Roots with{Players=f.Roots.Players+4},2=>f.Roots with{Table=f.Roots.Table+4},_=>f.Roots with{LocalPlayer=1}};
            var state=f.Capture(roots:()=>++count>=boundary?altered:f.Roots);Unknown(state);Check(state.Source.Reason=="session_changed","all session components at all three boundaries");
        }
    }
    private static void WorldChurn(){var f=new Fixture();Check(f.Capture(roots:()=>f.Roots with{Count=999,Array=0x20100000}).Status=="confirmed","enemy count and array are not the session identity");}
    private static void Signatures(){var f=new Fixture();f.Bytes[Fixture.Base+0x8C1666]=0;var state=f.Capture();Unknown(state);Check(state.Source.Reason=="unsupported_native_layout","a modified or other build cannot use presumed structure offsets");}
    private static void Changing()
    {
        var f=new Fixture();var count=0;byte[] Read(uint address,int size){if(address==f.Nodes["X3"]&&++count==2)f.Value("X3",36);return f.Read(address,size);}
        var state=f.Capture(Read);Unknown(state);Check(state.Source.Reason=="progression_changing","round change between samples is not cached or rounded");
    }
    private static void ChangingTable()
    {
        var f=new Fixture();var count=0;byte[] Read(uint address,int size){if(address==Fixture.Vm+0x285C&&++count==2)f.Write(address,Fixture.Globals+0x1000);return f.Read(address,size);}
        f.Copy(Fixture.Globals,Fixture.Globals+0x1000,0x28);Unknown(f.Capture(Read));
    }
    private static void ExactNames()
    {
        var f=new Fixture();f.Add("x3",4,4);Check(f.Capture().Round==35,"same hash different-case key is skipped");
        var old=f.Nodes["X3"];f.Write(old+0x14,Fixture.InvalidKey);f.Text(Fixture.InvalidKey,"zz\0");Unknown(f.Capture());
        f=new Fixture();f.Add("X3",4,35);Unknown(f.Capture());
    }
    private static void Types()
    {
        foreach(var name in new[]{"X3","Ji"}){var f=new Fixture();f.Write(f.Nodes[name]+0x1C,5);Unknown(f.Capture());}
        var a=new Fixture();a.Write(a.Nodes["Ji"]+0x18,0);Unknown(a.Capture());
        var b=new Fixture();b.Write(b.Nodes["NE"]+0x18,5);Unknown(b.Capture());
    }
    private static void ScalarValueTags()
    {
        var f = new Fixture();
        foreach (var name in new[] { "X3", "on", "NE", "sJ", "YU" }) { f.Value(name, 0); f.Write(f.Nodes[name] + 0x18, 0); }
        Check(f.Capture().Status == "confirmed", "constructor value-type0 is valid only with its initial zero value");
        f.Value("X3", 1); Unknown(f.Capture());
        f.Write(f.Nodes["X3"] + 0x18, 4); Check(f.Capture().Round == 1, "native scalar assignment sets the current-value tag to its declared integer type");
        f = new Fixture(); f.Write(f.Nodes["YU"] + 0x18, 4); Unknown(f.Capture());
        f = new Fixture(); f.Write(f.Nodes["Ji"] + 0x18, 8); Unknown(f.Capture());
        f = new Fixture(); f.Bytes[Fixture.Base + 0x8CB01A] = 0; Unknown(f.Capture());
    }
    private static void PlayerSlotStates()
    {
        var f = new Fixture(); var state = f.Capture();
        Check(state.PlayerSlotState == 1 && state.Source.SlotState is { Status: "confirmed", ReadOnly: true, SampleCount: 2 }, "a native slot uses its own two matching reads and instruction evidence");
        f.Write(Fixture.Player + 0x270, 2); Check(f.Capture().PlayerSlotState == 2, "the native left state remains distinct from map inactive");
        f.Write(Fixture.Player + 0x270, 3); state = f.Capture();
        Check(state.Status == "confirmed" && state.Round == 35 && state.PlayerSlotState is null && state.Source.SlotState?.Status == "unknown", "unsupported slots do not erase separately verified progression");
        f = new Fixture(); f.Bytes[Fixture.Base + 0x234210] = 0; state = f.Capture();
        Check(state.Status == "confirmed" && state.PlayerSlotState is null, "slot native-layout mismatch remains isolated");
        f = new Fixture(); var reads = 0; byte[] Read(uint address, int size) { if (address == Fixture.Player + 0x270 && ++reads == 2) f.Write(address, 2); return f.Read(address, size); }
        state = f.Capture(Read);
        Check(state.Status == "confirmed" && state.PlayerSlotState is null && state.Source.SlotState?.Reason == "player_slot_state_changing", "a changing slot is not mislabeled as a stable defeat or leave");
    }
    private static void TypeDiagnostics()
    {
        var f = new Fixture(); f.Write(f.Nodes["X3"] + 0x1C, 5); var state = f.Capture(); Unknown(state);
        Check(state.Source.Reason == "unexpected_global_type" && state.Source.TypeMismatch is
            { Name: "X3", ExpectedType: 4, ActualType: 5, ActualValueTypeTag: 4 } && state.Source.TypeMismatch.AllowedValueTypeTags.SequenceEqual(new uint[] { 0, 4 }), "the observed scalar metadata is retained without accepting another type");
        f = new Fixture(); f.Write(f.Nodes["Ji"] + 0x18, 0); state = f.Capture(); Unknown(state);
        Check(state.Source.TypeMismatch is { Name: "Ji", ExpectedType: 13, ActualType: 13, ActualValueTypeTag: 0 } && state.Source.TypeMismatch.AllowedValueTypeTags.SequenceEqual(new uint[] { 13 }), "array tag mismatches are distinguished from element type mismatches");
        using var json = JsonDocument.Parse(JsonSerializer.Serialize(state));
        Check(json.RootElement.GetProperty("victory").ValueKind == JsonValueKind.Null &&
            json.RootElement.GetProperty("source").GetProperty("globalTypeMismatch").GetProperty("name").GetString() == "Ji", "diagnostic JSON does not fabricate a terminal flag");
        Check(new Fixture().Capture().Source.TypeMismatch is null, "a successful capture cannot inherit an earlier mismatch");
    }
    private static void InvalidStructures()
    {
        foreach(var address in new[]{Fixture.Base+0xD0DEF8,Fixture.Environment+0x14,Fixture.Instance+0x90,Fixture.Vms+4,Fixture.Vm+0x285C,Fixture.Globals+0x1C})
        {var f=new Fixture();f.Write(address,0);Unknown(f.Capture());}
        foreach(var mask in new uint[]{2,0xFFFFFFFF,0x1FFFF}){var f=new Fixture();f.Write(Fixture.Globals+0x24,mask);Unknown(f.Capture());}
        var a=new Fixture();a.Write(a.Bucket("X3"),3);Unknown(a.Capture());
    }
    private static void CollisionFailure()
    {
        var f=new Fixture();f.Write(f.Nodes["X3"]+8,f.Nodes["X3"]);Unknown(f.Capture());
        f=new Fixture();f.Write(f.Bucket("X3")+8,0);Unknown(f.Capture());
        f=new Fixture();for(var i=0;i<129;i++)f.Add("x3",4,0);Unknown(f.Capture());
    }
    private static void Arrays()
    {
        foreach(var size in new uint[]{0,8193}){var f=new Fixture();f.Write(Fixture.ArrayHeader+8,size);Unknown(f.Capture());}
        var a=new Fixture();a.Write(Fixture.ArrayHeader+12,0);Unknown(a.Capture());
        var b=new Fixture();b.Write(Fixture.ArrayData,2);Unknown(b.Capture());
    }
    private static void PlayerIdentity(){var f=new Fixture();f.Bytes[Fixture.Player+0x30]=1;Unknown(f.Capture());}
    private static void ValueBounds()
    {
        foreach(var (name,value) in new(string,uint)[]{("X3",66),("X3",uint.MaxValue),("on",15),("NE",7),("sJ",uint.MaxValue)})
        {var f=new Fixture();f.Value(name,value);Unknown(f.Capture());}
    }
    private static void ReadFailures()
    {
        foreach(var error in new Exception[]{new IOException(),new Win32Exception(5),new OverflowException()})
        {var f=new Fixture();Unknown(f.Capture((_,__)=>throw error));}
        var a=new Fixture();Unknown(a.Capture((_,__)=>[]));
    }
    private static void NoCache()
    {
        var f=new Fixture();Check(f.Capture().ActivePlayer==true,"baseline active");f.Value("NE",5);f.Value("sJ",1);f.Write(Fixture.ArrayData,0);
        var state=f.Capture();Check(state.Status=="confirmed"&&state.ActivePlayer==false&&state.Difficulty=="other"&&state.GameEnded==true&&state.RoundTransitionDepth==1,"new player-defeat state is independently read");
        f.Write(Fixture.ArrayData,1);state=f.Capture();Check(state.GameEnded==false&&state.RoundTransitionDepth==1,"a positive transition semaphore alone is not interpreted as a game-ended flag");
        var other=new Fixture(1);other.Write(Fixture.ArrayData+4,0);Check(other.Capture().ActivePlayer==false&&other.Capture().LocalPlayer==1,"local array index is not reused");
    }
    private static void Serialization()
    {
        using var json=JsonDocument.Parse(JsonSerializer.Serialize(GameProgression.Unknown("test")));
        var root=json.RootElement;Check(root.GetProperty("round").ValueKind==JsonValueKind.Null&&root.GetProperty("activePlayer").ValueKind==JsonValueKind.Null&&root.GetProperty("claimedTechs").EnumerateObject().Count()==0,"unknown is not round0 inactive or claimed=false");
    }
    private static void Unknown(GameProgression.State state){Check(state.Status=="unknown"&&state.Round is null&&state.StoryIndex is null&&state.ActivePlayer is null&&state.GameEnded is null&&state.Victory is null&&state.ClaimedTechs.Count==0,"failure publishes no partial entitlement or progress values");}
    private static void Check(bool ok,string message){if(!ok)throw new InvalidOperationException(message);}

    private sealed class Fixture
    {
        internal const uint Base=0x60000000,Environment=0x20000000,Instance=0x21000000,Vms=0x22000000,Vm=0x23000000,Globals=0x24000000,Buckets=0x25000000,
            Player=0x26000000,ArrayHeader=0x27000000,ArrayData=0x28000000,InvalidKey=0x29000000;
        internal Dictionary<uint,byte> Bytes=new();internal Dictionary<string,uint> Nodes=new(StringComparer.Ordinal);private uint nextNode=0x30000000;
        internal Roots Roots;
        internal Fixture(int local=0)
        {
            Roots=new(0x31000000,0x32000000,0x33000000,0x34000000,1,local);
            foreach(var(rva,bytes)in GameProgression.NativeSignatures(Base))Put(Base+rva,bytes);
            foreach(var(rva,bytes)in GameProgression.PlayerSlotSignatures())Put(Base+rva,bytes);
            Write(Base+0xD0DEF8,Environment);Write(Environment+0x14,Instance);Write(Instance+0x90,Vms);Write(Vms+4,Vm);Write(Vm+0x285C,Globals);
            Fill(Globals,0x28);Write(Globals+0x1C,Buckets);Write(Globals+0x24,63);Fill(Buckets,64*12);
            for(uint i=0;i<64;i++)Write(Buckets+i*12,4);
            Write(Roots.Players+0x58+(uint)local*4,Player);Bytes[Player+0x30]=(byte)local;Write(Player+0x270,1);
            Fill(ArrayHeader,16);Write(ArrayHeader+8,4);Write(ArrayHeader+12,ArrayData);for(uint i=0;i<4;i++)Write(ArrayData+i*4,1);
            Add("X3",4,35);Add("on",4,14);Add("NE",4,6);Add("sJ",4,0);Add("Ji",13,ArrayHeader);Add("YU",8,0);
        }
        internal uint Bucket(string name)=>Buckets+(GameProgression.NameHash(name)&63)*12;
        internal void Add(string name,uint type,uint value)
        {
            var node=nextNode;nextNode+=0x100;var key=node+0x50;Fill(node,0x24);Text(key,name+"\0");var bucket=Bucket(name);
            Write(node,GameProgression.NameHash(name));Write(node+8,BitConverter.ToUInt32(Read(bucket+8,4)));Write(node+0x14,key);
            Write(node+0x18,type);Write(node+0x1C,type);Write(node+0x20,value);Write(bucket+8,node);Nodes[name]=node;
        }
        internal void Value(string name,uint value)=>Write(Nodes[name]+0x20,value);
        internal void Fill(uint address,int size){for(var i=0;i<size;i++)Bytes[address+(uint)i]=0;}
        internal void Put(uint address,byte[] bytes){for(var i=0;i<bytes.Length;i++)Bytes[address+(uint)i]=bytes[i];}
        internal void Text(uint address,string text)=>Put(address,Encoding.ASCII.GetBytes(text));
        internal void Write(uint address,uint value)=>Put(address,BitConverter.GetBytes(value));
        internal byte[] Read(uint address,int size){var result=new byte[size];for(var i=0;i<size;i++)if(!Bytes.TryGetValue(address+(uint)i,out result[i]))throw new IOException("Unexpected fixture address");return result;}
        internal void Copy(uint source,uint destination,int count)=>Put(destination,Read(source,count));
        internal GameProgression.State Capture(Func<uint,int,byte[]>? read=null,Func<Roots?>? roots=null)=>GameProgression.CaptureForTests(read??Read,roots??(()=>Roots),Roots,Base,GameProgression.SupportedVersion,GameProgression.SupportedMapHash);
    }
}
