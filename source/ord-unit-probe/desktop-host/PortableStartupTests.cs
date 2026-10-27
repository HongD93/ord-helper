using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;

namespace OrdDesktopHost;

internal static class PortableStartupTests
{
    internal static int Run(string output)
    {
        if (!Path.IsPathFullyQualified(output)) return 2;
        var tests = new List<object>(); var failed = 0;
        void Test(string name, Action action)
        {
            try { action(); tests.Add(new { name, passed = true }); }
            catch (Exception error) { failed++; tests.Add(new { name, passed = false, error = error.ToString() }); }
        }
        var fixture = Path.Combine(Path.GetDirectoryName(output)!, "portable-test-fixture-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(fixture);
        var server = Path.Combine(fixture, "app", "server", "OrdUnitProbe.exe");
        var catalog = Path.Combine(fixture, "data", "unit_catalog_2.323C.json");
        Directory.CreateDirectory(Path.GetDirectoryName(server)!); Directory.CreateDirectory(Path.GetDirectoryName(catalog)!);
        Directory.CreateDirectory(Path.Combine(Path.GetDirectoryName(server)!, "wwwroot"));
        File.WriteAllText(server, "fixture"); File.WriteAllText(catalog, "{}");
        File.WriteAllText(Path.Combine(Path.GetDirectoryName(server)!, "wwwroot", "index.html"), "fixture");
        var layout = new PortableLayout(fixture, server, catalog, Path.Combine(fixture, "logs"));
        var manifest = Path.Combine(fixture, "portable-layout.json");
        Test("manifest 없는 개발 위치는 포터블 모드로 오인하지 않음", () => Require(PortableLayout.Find(fixture) is null));
        Test("포터블 경로는 실행 파일 폴더 기준", () =>
        {
            File.WriteAllText(manifest, "{\"formatVersion\":1,\"serverRelativePath\":\"app/server/OrdUnitProbe.exe\",\"catalogRelativePath\":\"data/unit_catalog_2.323C.json\"}");
            var found = PortableLayout.Find(fixture)!; Require(found.Server == server && found.Catalog == catalog && found.Logs == layout.Logs);
            Require(found.Arguments.Contains(catalog) && found.Arguments.Contains(Path.Combine(layout.Logs, "live-counter.json")));
        });
        Test("상위 폴더나 다른 서버 경로를 설정으로 허용하지 않음", () =>
        {
            File.WriteAllText(manifest, "{\"formatVersion\":1,\"serverRelativePath\":\"../outside.exe\"}");
            Throws(() => PortableLayout.Find(fixture));
        });
        Test("미지원 설정 버전 거부", () =>
        {
            File.WriteAllText(manifest, "{\"formatVersion\":2}"); Throws(() => PortableLayout.Find(fixture));
        });
        Test("카탈로그 누락은 서버 시작 전에 중단", () =>
        {
            var fake = new FakePlatform(); Throws(() => PortableStartup.EnsureServerAsync(layout with { Catalog = catalog + ".absent" }, fake).GetAwaiter().GetResult()); Require(fake.Starts == 0);
        });
        Test("자기 패키지 서버 재사용은 새 서버를 만들지 않음", () =>
        {
            var fake = new FakePlatform(new ServerProbe(ProbeKind.Owned, 700));
            var result = PortableStartup.EnsureServerAsync(layout, fake).GetAwaiter().GetResult();
            Require(result is null && fake.Starts == 0 && fake.Identities.SequenceEqual([700]));
        });
        Test("외부 점유 서버는 재사용하거나 종료하지 않음", () =>
        {
            var fake = new FakePlatform(new ServerProbe(ProbeKind.Foreign, 700)); Throws(() => PortableStartup.EnsureServerAsync(layout, fake).GetAwaiter().GetResult()); Require(fake.Starts == 0 && fake.Handle.Stops == 0);
        });
        Test("접속 상태 미확인은 점유 오류와 구분하고 서버를 시작하지 않음", () =>
        {
            var fake = new FakePlatform(new ServerProbe(ProbeKind.Unavailable));
            try { PortableStartup.EnsureServerAsync(layout, fake).GetAwaiter().GetResult(); throw new InvalidOperationException("Expected failure."); }
            catch (PortableStartupException error) { Require(!error.Message.Contains("다른 프로그램") && fake.Starts == 0 && fake.Handle.Stops == 0); }
        });
        Test("새 서버는 동일 PID의 준비 응답 뒤에만 성공", () =>
        {
            var fake = new FakePlatform(new ServerProbe(ProbeKind.NoListener), new(ProbeKind.NoListener), new(ProbeKind.Owned, 500));
            using var result = PortableStartup.EnsureServerAsync(layout, fake).GetAwaiter().GetResult();
            Require(result?.ProcessId == 500 && fake.Starts == 1 && fake.Identities.SequenceEqual([500]) && fake.Handle.Stops == 0);
        });
        Test("시작 도중 다른 PID 응답이면 새로 만든 프로세스만 정리", () =>
        {
            var fake = new FakePlatform(new ServerProbe(ProbeKind.NoListener), new(ProbeKind.Owned, 501));
            Throws(() => PortableStartup.EnsureServerAsync(layout, fake).GetAwaiter().GetResult()); Require(fake.Handle.Stops == 1 && fake.Handle.Disposals == 1 && fake.Identities.Count == 0);
        });
        Test("시작 도중 외부 점유로 바뀌면 자기 프로세스만 정리", () =>
        {
            var fake = new FakePlatform(new ServerProbe(ProbeKind.NoListener), new(ProbeKind.Foreign, 501));
            Throws(() => PortableStartup.EnsureServerAsync(layout, fake).GetAwaiter().GetResult()); Require(fake.Handle.Stops == 1 && fake.Handle.Disposals == 1);
        });
        Test("준비 시간초과는 새 서버만 정리", () =>
        {
            var fake = new FakePlatform(new ServerProbe(ProbeKind.NoListener)); Throws(() => PortableStartup.EnsureServerAsync(layout, fake, TimeSpan.FromSeconds(2)).GetAwaiter().GetResult());
            Require(fake.Handle.Stops == 1 && fake.Handle.Disposals == 1 && fake.Identities.Count == 0);
        });
        Test("즉시 종료된 새 서버는 준비 성공으로 처리하지 않음", () =>
        {
            var fake = new FakePlatform(new ServerProbe(ProbeKind.NoListener)) { Handle = new FakeHandle { Exited = true } };
            Throws(() => PortableStartup.EnsureServerAsync(layout, fake).GetAwaiter().GetResult()); Require(fake.Handle.Disposals == 1 && fake.Identities.Count == 0);
        });
        Test("실행 기록 저장 실패도 자기 프로세스만 정리", () =>
        {
            var fake = new FakePlatform(new ServerProbe(ProbeKind.NoListener), new(ProbeKind.Owned, 500)) { RecordFailure = true };
            Throws(() => PortableStartup.EnsureServerAsync(layout, fake).GetAwaiter().GetResult()); Require(fake.Handle.Stops == 1 && fake.Handle.Disposals == 1);
        });
        Test("서버 핸들을 닫아도 정상 실행 서버를 중지하지 않음", () =>
        {
            var fake = new FakePlatform(new ServerProbe(ProbeKind.NoListener), new(ProbeKind.Owned, 500));
            PortableStartup.EnsureServerAsync(layout, fake).GetAwaiter().GetResult()!.Dispose(); Require(fake.Handle.Disposals == 1 && fake.Handle.Stops == 0);
        });
        Test("경로 일치는 파일 이름만이 아닌 전체 경로 비교", () =>
        {
            Require(WindowsStartupPlatform.SamePath(server.ToUpperInvariant(), server));
            Require(!WindowsStartupPlatform.SamePath(Path.Combine(fixture, "other", "OrdUnitProbe.exe"), server));
        });
        Test("접속 거절만 미실행이고 시간초과는 점유 불명", () =>
        {
            Require(WindowsStartupPlatform.IsConnectionRefused(new HttpRequestException("fixture", new SocketException((int)SocketError.ConnectionRefused))));
            Require(!WindowsStartupPlatform.IsConnectionRefused(new TaskCanceledException()));
            Require(WindowsStartupPlatform.ClassifyProbeFailure(new TaskCanceledException()) == ProbeKind.Unavailable);
            Require(WindowsStartupPlatform.ClassifyProbeFailure(new HttpRequestException("fixture", new SocketException((int)SocketError.AccessDenied))) == ProbeKind.Unavailable);
            Require(WindowsStartupPlatform.ClassifyProbeFailure(new HttpRequestException("fixture", new SocketException((int)SocketError.ConnectionRefused))) == ProbeKind.NoListener);
        });
        Test("공백 한글 따옴표 끝 역슬래시 인자는 Windows 파서로 복원", () =>
        {
            var values = new[] { "", "E:\\원랜디 도우미\\file.json", "a b", "a\"b", "C:\\ends\\", "a\\\"b", "$x;`text`" };
            foreach (var value in values)
            {
                var memory = CommandLineToArgvW("program.exe " + NativeServerHandle.QuoteArgument(value), out var count);
                if (memory == IntPtr.Zero) throw new InvalidOperationException("Command line parse failed.");
                try { Require(count == 2 && Marshal.PtrToStringUni(Marshal.ReadIntPtr(memory, IntPtr.Size)) == value); }
                finally { _ = LocalFree(memory); }
            }
        });
        Directory.CreateDirectory(Path.GetDirectoryName(output)!);
        File.WriteAllText(output, JsonSerializer.Serialize(new { passed = tests.Count - failed, total = tests.Count, failed, tests,
            scope = "Mocked startup lifecycle and native argument parsing only. No game input, existing process termination, UI or localhost access." },
            new JsonSerializerOptions { WriteIndented = true }), Encoding.UTF8);
        return failed == 0 ? 0 : 1;
    }

    private static void Require(bool condition) { if (!condition) throw new InvalidOperationException("Assertion failed."); }
    private static void Throws(Action action)
    {
        try { action(); } catch (PortableStartupException) { return; } catch (IOException) { return; }
        throw new InvalidOperationException("Expected startup failure.");
    }
    private sealed class FakeHandle : PortableServerHandle
    {
        internal int Stops, Disposals; internal bool Exited;
        internal override int ProcessId => 500;
        internal override bool HasExited => Exited;
        internal override void Stop() => Stops++;
        public override void Dispose() => Disposals++;
    }
    private sealed class FakePlatform(params ServerProbe[] probes) : IStartupPlatform
    {
        private int index;
        public DateTime UtcNow { get; private set; } = new(2026, 10, 25, 0, 0, 0, DateTimeKind.Utc);
        internal int Starts; internal FakeHandle Handle = new(); internal bool RecordFailure;
        internal List<int> Identities = [];
        public Task<ServerProbe> ProbeAsync(PortableLayout layout) => Task.FromResult(probes.Length == 0 ? new(ProbeKind.NoListener) : probes[Math.Min(index++, probes.Length - 1)]);
        public PortableServerHandle Start(PortableLayout layout) { Starts++; return Handle; }
        public Task DelayAsync() { UtcNow = UtcNow.AddSeconds(1); return Task.CompletedTask; }
        public void RecordIdentity(PortableLayout layout, int processId) { if (RecordFailure) throw new IOException("fixture"); Identities.Add(processId); }
    }
    [DllImport("shell32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern IntPtr CommandLineToArgvW(string commandLine, out int count);
    [DllImport("kernel32.dll")] private static extern IntPtr LocalFree(IntPtr memory);
}

