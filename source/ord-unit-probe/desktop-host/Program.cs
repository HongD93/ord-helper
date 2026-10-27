namespace OrdDesktopHost;

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        ApplicationConfiguration.Initialize();
        if (args.Length == 2 && args[0] == "--self-test-portable")
        {
            Environment.ExitCode = PortableStartupTests.Run(args[1]);
            return;
        }
        if (args.Length == 4 && args[0] == "--self-test-probe" && args[2] == "--package-root")
        {
            Environment.ExitCode = PortableProbeDiagnostics.RunAsync(args[3], args[1]).GetAwaiter().GetResult();
            return;
        }
        PortableServerHandle? startedServer = null;
        PackageInstance? instance = null;
        PortableLayout? layout = null;
        try
        {
            var options = ParseOptions(args);
            var recipe = options.GetValueOrDefault("--verify-recipe");
            var output = options.GetValueOrDefault("--verify-output");
            if ((recipe is null) != (output is null) || recipe is not null && (recipe.Length != 4 || !recipe.All(char.IsAsciiLetterOrDigit)) ||
                output is not null && !Path.IsPathFullyQualified(output)) throw new ArgumentException("Invalid verification options.");
            layout = PortableLayout.Find(AppContext.BaseDirectory);
            if (layout is not null)
            {
                if (recipe is null)
                {
                    instance = PackageInstance.Acquire(layout.Root);
                    if (!instance.IsOwner) { PackageInstance.RestoreExisting(); return; }
                }
                startedServer = PortableStartup.EnsureServerAsync(layout, new WindowsStartupPlatform()).GetAwaiter().GetResult();
            }
            var profile = GetProfilePath(options.GetValueOrDefault("--user-data"));
            Directory.CreateDirectory(profile);
            using var window = new HelperWindow(profile, recipe, output);
            Application.Run(window);
            // 새로 시작한 서버만 화면 시작 실패 때 정리한다. 정상 창 종료는 진행 중인 조합에 영향을 주지 않는다.
            if (!window.StartupSucceeded) startedServer?.Stop();
        }
        catch (Exception error)
        {
            startedServer?.Stop();
            PortableStartup.WriteStartupFailure(layout?.Root ?? AppContext.BaseDirectory, error);
            MessageBox.Show(error is PortableStartupException ? error.Message : "도우미 창을 열지 못했습니다. 실행 경로를 확인해 주세요.",
                "원랜디 도우미", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        finally { startedServer?.Dispose(); instance?.Dispose(); }
    }

    private static Dictionary<string, string> ParseOptions(string[] args)
    {
        if (args.Length % 2 != 0) throw new ArgumentException("Missing option value.");
        var options = new Dictionary<string, string>(StringComparer.Ordinal);
        for (var index = 0; index < args.Length; index += 2)
        {
            if (args[index] is not ("--user-data" or "--verify-recipe" or "--verify-output") || !options.TryAdd(args[index], args[index + 1]))
                throw new ArgumentException("Unknown or duplicate option.");
        }
        return options;
    }

    private static string GetProfilePath(string? requested)
    {
        if (requested is not null)
        {
            if (!Path.IsPathFullyQualified(requested)) throw new ArgumentException("A fully qualified user-data path is required.");
            return Path.GetFullPath(requested);
        }
        return Path.Combine(AppContext.BaseDirectory, "state", "webview-profile");
    }
}
