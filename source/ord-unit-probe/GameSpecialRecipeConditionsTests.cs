using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Logging;

static class GameSpecialRecipeConditionsTests
{
    internal static async Task RunAsync()
    {
        var passed = 0;
        void Check(bool ok, string name) { if (!ok) throw new InvalidOperationException("Special recipe: " + name); passed++; }
        var f = new MemoryFixture(); var known = f.Capture();
        Check(known.Status == "confirmed" && known.DistortionWoodSurcharge == 0 && known.DistortionWoodIncrement == 5 &&
            known.TransformationCount == 2 && known.SkillPoints == 7 && known.Round == 40, "named globals and native state5 retain exact zeros and scale1");
        Check(known.NikaEnhancement is { Status: "confirmed", Stage: 0, LocalPlayer: 0 }, "NG zero is a confirmed unenhanced stage");
        foreach (var stage in new uint[] { 1, 2 }) { f.Write(f.Data["NG"], stage); Check(f.Capture().NikaEnhancement.Stage == stage, "NG retains exact enhanced/completed stage"); }
        f.Write(f.Data["NG"], 3); Check(f.Capture().NikaEnhancement.Status == "unknown" && f.Capture().Status == "confirmed", "out-of-range NG cannot discard the other known conditions");
        f = new(); f.Write(f.Nodes["NG"] + 0x20, 0); Check(f.Capture().NikaEnhancement.Stage == 0, "unallocated NG uses signature-verified native zero default");
        f = new(); f.Write(f.Nodes["NG"] + 0x1C, 4); Check(f.Capture().NikaEnhancement.Status == "unknown" && f.Capture().TransformationCount == 2, "NG type failure is isolated");
        f = new(); var ngReads = 0; var ngChanged = f.Capture((address, count) => { var bytes = f.Read(address, count); if (address == f.Data["NG"] && ++ngReads == 1) f.Write(address, 1); return bytes; });
        Check(ngChanged.NikaEnhancement.Status == "unknown" && ngChanged.Status == "confirmed", "changing NG does not become a confirmed enhancement");
        f = new(); f.Write(MemoryFixture.Base + 0x8D17B0, 0); Check(f.Capture().NikaEnhancement.Status == "unknown" && f.Capture().Status == "confirmed", "NG native layout mismatch is isolated");
        f = new();
        f.Write(f.Nodes["X3"] + 0x18, 0); Check(f.Capture().Round is null, "a nonzero scalar cannot retain the constructor's empty value tag");
        f.Write(f.Nodes["X3"] + 0x20, 0); Check(f.Capture().Round == 0, "constructor zero is distinct from an unknown round");
        f = new(); f.Write(f.Nodes["X3"] + 0x18, 5); Check(f.Capture().Round is null, "an integer round cannot take a real current-value type");
        f = new();
        f.Write(MemoryFixture.ValueObject + 0x18, 8);
        var partial = f.Capture(); Check(partial.Status == "partial" && partial.SkillPoints is null && partial.TransformationCount == 2, "bad skill handle generation is isolated");
        f = new(); f.Write(f.Nodes["hW"] + 0x1C, 4); partial = f.Capture();
        Check(partial.DistortionWoodSurcharge is null && partial.TransformationCount == 2 && partial.SkillPoints == 7, "wrong global type does not zero or discard independent facts");
        f = new(); var reads = 0;
        partial = f.Capture((address, count) => { var bytes = f.Read(address, count); if (address == f.Data["hW"] && ++reads == 1) f.Write(address, 5); return bytes; });
        Check(partial.DistortionWoodSurcharge is null && partial.TransformationCount == 2, "changing surcharge is unknown");
        f = new(); f.Write(f.Data["hW"], uint.MaxValue); partial = f.Capture();
        Check(partial.DistortionWoodSurcharge is null && partial.TransformationCount == 2, "negative surcharge does not become zero");
        f = new(); f.Write(f.Nodes["X3"] + 0x20, 66); partial = f.Capture();
        Check(partial.Round is null && partial.DistortionWoodSurcharge == 0, "invalid round does not invalidate independent costs");
        f = new(); f.Write(f.Data["LW"], 0); partial = f.Capture();
        Check(partial.DistortionWoodIncrement is null && partial.DistortionWoodSurcharge is null, "uninitialized increment is not a free distortion");
        f = new(); f.Write(f.Data["LO"], 0); f.Write(MemoryFixture.ValueObject + 0x78, 0); partial = f.Capture();
        Check(partial.TransformationCount == 0 && partial.SkillPoints == 0, "confirmed zero consumables remain zero");
        f = new(); f.Write(MemoryFixture.ValueObject + 0x78, uint.MaxValue); partial = f.Capture();
        Check(partial.SkillPoints is null && partial.DistortionWoodSurcharge == 0, "negative skill points do not corrupt wood facts");
        f = new(); var rootsRead = 0;
        Check(f.Capture(readRoots: () => ++rootsRead >= 2 ? f.Roots with { LocalPlayer = 1 } : f.Roots).Status == "unknown", "session change revokes all facts");
        byte[] Forbidden(uint address, int count) => throw new Exception("Unsupported profiles must not read.");
        Check(GameSpecialRecipeConditions.CaptureForTests(Forbidden, () => f.Roots, f.Roots, MemoryFixture.Base, "other", GameProgression.SupportedMapHash).Status == "unknown", "unsupported version does not read");
        Check(GameSpecialRecipeConditions.CaptureForTests(Forbidden, () => f.Roots, f.Roots, MemoryFixture.Base, GameProgression.SupportedVersion, "wrong").Status == "unknown", "unsupported map does not read");
        var state = Conditions(Special(0, 2, 8, 40));
        Check(CraftConditions.TryProject(state, [new("IFCT", "IFCT", 3), new("IFCT", "IFCT", 3)], out var next, out var wood, out _, out _) && wood == 11 && next?.Special?.DistortionWoodSurcharge == 10,
            "two distortion costs are3+8 and jointly advance surcharge");
        Check(!CraftConditions.Available(state, [new("CHCT", "CHCT", 2), new("CHCT", "CHCT", 1)], out _, out _), "shared transformation stock cannot be spent twice");
        Check(!CraftConditions.Available(state, [new("SKPT", "SKPT", 4), new("SKPT", "SKPT", 5)], out _, out _), "shared skill points cannot be spent twice");
        Check(CraftConditions.Available(state, [new("RMAX", "h05G", 40)], out _, out _) && !CraftConditions.Available(Conditions(Special(0, 2, 8, 41)), [new("RMAX", "h05G", 40)], out _, out _), "round40 allowed and41 blocked");
        Check(state.Special?.SkillPoints == 8 && state.KingStatus == "unknown" && !CraftConditions.Available(state, [new("KING", "h05G", 1)], out _, out _), "outer unknown preserves special facts without allowing KING");
        var malformedBans = JsonSerializer.SerializeToElement(new { localPlayer = 0, recipeConditions = new { status = "confirmed", localPlayer = 0,
            source = new { readOnly = true }, unitBans = new { bad = "invalid" }, special = Special(0, 2, 8, 40) } });
        Check(CraftConditions.Read(malformedBans)?.Special?.TransformationCount == 2 && !CraftConditions.Available(CraftConditions.Read(malformedBans), [new("UBAN", "h05G", 1)], out _, out _),
            "malformed unrelated bans revoke only bans and preserve independently verified special facts");
        var independent = Conditions(Special(0, 2, 8, 40) with { SkillPoints = null });
        Check(CraftConditions.Available(independent, [new("IFCT", "IFCT", 3)], out _, out _) && !CraftConditions.Available(independent, [new("SKPT", "SKPT", 4)], out _, out _),
            "unrelated missing skill points do not lock distortion while skill requirements stay closed");
        var bad = Special(0, 2, 8, 40) with { Source = Special(0, 2, 8, 40).Source with { SampleCount = 1 } };
        Check(Conditions(bad).Special is null, "one observation cannot grant special conditions");
        bad = Special(0, 2, 8, 40) with { LocalPlayer = 1 };
        Check(Conditions(bad).Special is null, "foreign owner does not grant special conditions");
        bad = Special(0, 2, 8, 40) with { Source = Special(0, 2, 8, 40).Source with { MapSha256 = "wrong" } };
        Check(Conditions(bad).Special is null, "wrong map does not grant special conditions");
        var before = Frame(Special(0, 2, 8, 40)); var after = Frame(Special(5, 1, 4, 40));
        CraftRecipeCondition[] consumed = [new("IFCT", "IFCT", 3), new("CHCT", "CHCT", 1), new("SKPT", "SKPT", 4)];
        Check(GameSpecialRecipeConditions.ConsumptionMatches(before, after, consumed), "all special consumptions require matching post-state");
        Check(!GameSpecialRecipeConditions.ConsumptionMatches(before, Frame(Special(0, 1, 4, 40)), consumed), "result units alone cannot prove distortion consumption");
        Check(GameSpecialRecipeConditions.ConsumptionMatches(before, Frame(Special(5, 20, 20, 41)), [new("IFCT", "IFCT", 3)]),
            "unrelated story rewards or a new round do not invalidate the required distortion counter proof");
        var commands = new[] { Command("r001", "m001", "b001", "CHCT", 1), Command("r002", "g001", "m001", "CHCT", 1) };
        Check(!CraftPlanner.Plan("r002", commands, new Dictionary<string,int>{{"b001",1}}, recipeConditions:Conditions(Special(0,1,8,40))).Executable,
            "recursive plan sums all transformation uses");
        Check(!CraftPlanner.ValidateExpected("r002", commands, new Dictionary<string,int>{{"b001",1}}, [new("craft","r001"),new("craft","r002")],recipeConditions:Conditions(Special(0,1,8,40))).Executable,
            "exact plan sums all transformation uses");
        passed += await ServiceTests();
        Console.WriteLine($"PASS: special recipe conditions {passed}/{passed} memory, projection and service fixture cases; no game input.");
    }
    private static CraftCommand Command(string id,string target,string material,string type,int count)=>new(id,target,1,[new(material,1)],"검사조합",null,[material],Conditions:[new(type,type,count)]);
    private static GameSpecialRecipeConditions.State Special(int surcharge,int changes,int skills,int round)=>new("confirmed",0,surcharge,5,changes,skills,round,
        new(true,GameProgression.SupportedMapHash,2,"fixture","two_matching_fixture_reads"));
    private static JsonElement Frame(GameSpecialRecipeConditions.State special)=>JsonSerializer.SerializeToElement(new{localPlayer=0,recipeConditions=new{status="unknown",special}});
    private static CraftConditions.State Conditions(GameSpecialRecipeConditions.State special)=>CraftConditions.Read(Frame(special))??new(new(),"unknown","");

    private static async Task<int> ServiceTests()
    {
        var folder=Path.Combine(Path.GetTempPath(),"ord-special-fixture-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(folder);
        var catalog=Path.Combine(folder,"catalog.json");var aliases=Path.Combine(folder,"aliases.json");
        var builder=WebApplication.CreateBuilder(new WebApplicationOptions{Args=[]});builder.Logging.ClearProviders();await using var app=builder.Build();
        var passed=0;
        try
        {
            foreach(var type in new[]{"IFCT","CHCT","SKPT","RMAX"})
            {
                var amount=type=="IFCT"?3:type=="SKPT"?4:type=="RMAX"?40:1;
                object Recipe(string id,string target,string material)=>new{id,resultUnitRawcode=target,resultCount=1,hotkey=(string?)null,casterUnitRawcodes=new[]{material},
                    ingredients=new[]{new{type="UNIT",rawcode=material,count=1},new{type,rawcode=type,count=amount}}};
                File.WriteAllText(catalog,JsonSerializer.Serialize(new{map=new{sha256=GameProgression.SupportedMapHash},units=new[]{"b001","m001","g001"}.Select(rawcode=>new{rawcode,playable=true}),recipes=new[]{Recipe("r001","m001","b001"),Recipe("r002","g001","m001")}}));
                File.WriteAllText(aliases,JsonSerializer.Serialize(new{recipes=new[]{"r001","r002"}.Select(recipeId=>new{recipeId,command="검사조합"})}));
                async Task<JsonElement> Run(ServiceFixture f)=>await new GameCraftService(f.Snapshot,catalog,aliases,f.Execute).RunFixtureAsync("r002",1,CancellationToken.None,app,adaptive:true);
                var normal=new ServiceFixture(type);var result=await Run(normal);
                if(result.GetProperty("status").GetString()!="succeeded"||normal.Calls!=2)throw new InvalidOperationException("Special normal service "+type+" "+result);
                passed++;
                var shortage=new ServiceFixture(type);if(type=="IFCT")shortage.Wood=10;else if(type=="CHCT")shortage.Changes=1;else if(type=="SKPT")shortage.Skills=7;else shortage.Round=41;
                result=await Run(shortage);if(result.GetProperty("status").GetString()!="failed"||shortage.Calls!=0)throw new InvalidOperationException("Special shortage must not consume "+type);passed++;
                if(type=="RMAX")
                {
                    var transition=new ServiceFixture(type){PassRoundAfterFirst=true};result=await Run(transition);
                    if(result.GetProperty("status").GetString()!="failed"||transition.Calls!=1||transition.Units.GetValueOrDefault("m001")!=1)throw new InvalidOperationException("RMAX transition must stop before next input");passed++;
                }
            }
        }
        finally{File.Delete(catalog);File.Delete(aliases);Directory.Delete(folder);}
        return passed;
    }
    private sealed class ServiceFixture(string type)
    {
        internal Dictionary<string,int> Units=new(){{"b001",1}};internal int Calls,Wood=11,Changes=2,Skills=8,Round=40,Surcharge;internal bool PassRoundAfterFirst;private long sequence;
        internal string Snapshot()=>JsonSerializer.Serialize(new{status="snapshot_read",timestamp=DateTimeOffset.UtcNow,processId=1234,sequence=++sequence,gameVersion="1.28.5.7680",localPlayer=0,
            sessionIdentity="00010000:00020000:00030000",loadedMap=Path.GetFullPath("fixture.w3x"),loadedMapHash=GameProgression.SupportedMapHash,catalogMatchesLoadedArchive=true,stableRoots=true,stableUnits=true,readErrors=0,
            counts=Units.Where(x=>x.Value>0).Select(x=>new{rawcode=x.Key,count=x.Value,isCharacter=true}),resources=new{status="confirmed",localPlayer=0,wood=Wood,gold=1000},
            recipeConditions=new{status="unknown",special=Special(Surcharge,Changes,Skills,Round)}});
        internal Task<string?> Execute(PlannedCraftStep step,JsonElement baseline,CancellationToken cancel)
        {
            Calls++;foreach(var i in step.Ingredients)Units[i.Rawcode]-=i.Count;Units[step.ResultRawcode]=Units.GetValueOrDefault(step.ResultRawcode)+step.ResultCount;
            if(type=="IFCT"){Wood-=3+Surcharge;Surcharge+=5;}else if(type=="CHCT")Changes--;else if(type=="SKPT")Skills-=4;
            if(PassRoundAfterFirst&&Calls==1)Round=41;return Task.FromResult<string?>(null);
        }
    }
    private sealed class MemoryFixture
    {
        internal const uint Base=0x60000000,Player=0x26000000,ValueObject=0x37000000;
        internal Roots Roots=new(0x31000000,0x32000000,0x33000000,0x34000000,1,0);
        internal Dictionary<uint,byte> Bytes=new();internal Dictionary<string,uint> Nodes=new(),Data=new();private uint nextNode=0x30000000,nextArray=0x27000000;
        internal MemoryFixture()
        {
            foreach(var(rva,bytes)in GameProgression.NativeSignatures(Base).Concat(GameResources.SkillPointSignatures(Base)).Concat(GameStartingEvolution.NativeSignatures().Where(signature => signature.Rva is 0x8D17B0 or 0x8CB9D0)))Put(Base+rva,bytes);
            Write(Base+0xD0DEF8,0x20000000);Write(0x20000014,0x21000000);Write(0x21000090,0x22000000);Write(0x22000004,0x23000000);Write(0x2300285C,0x24000000);
            Write(0x2400001C,0x25000000);Write(0x24000024,63);Fill(0x25000000,64*12);for(uint i=0;i<64;i++)Write(0x25000000+i*12,4);
            Write(Roots.Players+0x58,Player);Put(Player+0x30,[0]);
            Add("hW",0);Add("LW",5);Add("LO",2);Add("X3",40);Add("NG",0);
            Write(Base+0xD30448,0x35000000);Write(0x3500001C,2);Write(0x3500000C,0x36000000);Write(0x36000008,0xFFFFFFFE);Write(0x3600000C,ValueObject);
            Write(Player+0x98,1);Write(Player+0x9C,7);Write(ValueObject+0x18,7);Write(ValueObject+0x78,7);
        }
        private void Add(string name,uint value)
        {
            var node=nextNode;nextNode+=0x100;var key=node+0x50;Fill(node,0x24);Put(key,Encoding.ASCII.GetBytes(name+"\0"));var hash=GameProgression.NameHash(name);var bucket=0x25000000+(hash&63)*12;
            Write(node,hash);Write(node+8,BitConverter.ToUInt32(Read(bucket+8,4)));Write(node+0x14,key);Write(bucket+8,node);Nodes[name]=node;
            if(name=="X3"){Write(node+0x18,4);Write(node+0x1C,4);Write(node+0x20,value);}else{var header=nextArray;nextArray+=0x1000;Write(node+0x18,9);Write(node+0x1C,9);Write(node+0x20,header);Write(header+8,4);Write(header+12,header+0x100);Write(header+0x100,value);Data[name]=header+0x100;}
        }
        internal GameSpecialRecipeConditions.State Capture(Func<uint,int,byte[]>? read=null,Func<Roots?>? readRoots=null)=>GameSpecialRecipeConditions.CaptureForTests(read??Read,readRoots??(()=>Roots),Roots,Base,GameProgression.SupportedVersion,GameProgression.SupportedMapHash);
        internal void Write(uint p,uint value)=>Put(p,BitConverter.GetBytes(value));
        internal void Fill(uint p,int n){for(var i=0;i<n;i++)Bytes[p+(uint)i]=0;}
        internal void Put(uint p,byte[] data){for(var i=0;i<data.Length;i++)Bytes[p+(uint)i]=data[i];}
        internal byte[] Read(uint p,int n){var data=new byte[n];for(var i=0;i<n;i++)if(!Bytes.TryGetValue(p+(uint)i,out data[i]))throw new IOException("Missing special fixture memory");return data;}
    }
}
