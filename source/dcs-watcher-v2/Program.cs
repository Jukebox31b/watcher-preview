using System.Runtime.InteropServices;

namespace DcsWatcherV2;

internal static class Program
{
    private static int _showingFatalError;
    private static int _fatalExitRequested;
    private static MainForm? _activeMainForm;

    [STAThread]
    private static int Main(string[] args)
    {
        InitializeWindowsFormsEnvironment();

        if (args.Length == 3 && args[0].Equals("--stage3-manual-pilot", StringComparison.OrdinalIgnoreCase))
        {
            var runtime = LoadActiveRuntime();
            if (runtime is null || runtime.TrustContext is null) return 1;
            var result = new Services.Stage3ManualPilotService().RunAsync(
                args[1], args[2], runtime.Config, runtime.TrustContext).GetAwaiter().GetResult();
            return result.Disposition.Equals("PASS_PENDING_CODEX_RECEIPT", StringComparison.Ordinal) ? 0 : 1;
        }
        if (args.Length == 2 && args[0].Equals("--stage3-finalize-pilot-pass", StringComparison.OrdinalIgnoreCase))
        {
            var runtime = LoadActiveRuntime();
            if (runtime is null) return 1;
            _ = Services.Stage3ManualPilotService.FinalizePass(args[1], runtime.Config);
            return 0;
        }
        if (args.Length == 1 && args[0].Equals("--release-ui-self-test", StringComparison.OrdinalIgnoreCase))
        {
            _ = new Services.WatcherSelfTestService().Run();
            return Services.UiReleaseSelfTest.Run().Passed ? 0 : 1;
        }
        if (args.Length is 1 or 2 && args[0].Equals("--release-test", StringComparison.OrdinalIgnoreCase))
        {
            _ = AttachConsole(AttachParentProcess);
            var result = Services.WatcherReleaseTestSuite.RunFullAsync().GetAwaiter().GetResult();
            var json = result.ToJson();
            Console.WriteLine(json);
            if (args.Length == 2)
            {
                try
                {
                    var outputPath = Path.GetFullPath(args[1]);
                    Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
                    var bytes = System.Text.Encoding.UTF8.GetBytes(json);
                    using var output = new FileStream(
                        outputPath,
                        FileMode.Create,
                        FileAccess.Write,
                        FileShare.None,
                        bufferSize: 4096,
                        FileOptions.WriteThrough);
                    output.Write(bytes);
                    output.Flush(flushToDisk: true);
                }
                catch (Exception ex)
                {
                    Console.Error.WriteLine($"Release-test output could not be written to '{args[1]}': {ex.Message}");
                    return 1;
                }
            }
            return result.Passed ? 0 : 1;
        }
        if (args.Length is 1 or 2 && args[0].Equals("--runtime-composition-self-test", StringComparison.OrdinalIgnoreCase))
        {
            var result = Services.RuntimeCompositionReleaseSelfTest.Run();
            foreach (var message in result.Messages) Console.WriteLine(message);
            Console.WriteLine($"Runtime composition: {result.Passed} passed, {result.Failed} failed.");
            if (args.Length == 2)
            {
                File.WriteAllLines(args[1], result.Messages.Append($"Runtime composition: {result.Passed} passed, {result.Failed} failed."));
            }
            return result.Failed == 0 ? 0 : 1;
        }

        var demoUi = args.Length == 1 && args[0].Equals("--demo-ui", StringComparison.OrdinalIgnoreCase);

        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
        Application.ThreadException += (_, args) => ShowFatalStartupError(args.Exception);
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
        {
            if (args.ExceptionObject is Exception ex)
            {
                ShowFatalStartupError(ex);
            }
        };
        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            ShowFatalStartupError(args.Exception);
            args.SetObserved();
        };

        try
        {
            _activeMainForm = new MainForm(demoUi);
            Application.Run(_activeMainForm);
            _activeMainForm = null;
            return Volatile.Read(ref _fatalExitRequested) == 0 ? 0 : 1;
        }
        catch (Exception ex)
        {
            ShowFatalStartupError(ex);
            return 1;
        }
    }

    private static void InitializeWindowsFormsEnvironment()
    {
        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
    }

    private static Services.RuntimeComposition? LoadActiveRuntime()
    {
        var configs = new Services.ConfigService();
        var installation = configs.Load();
        if (string.IsNullOrWhiteSpace(installation.ActiveProfileId)) return null;
        var profiles = new Services.ProfileService(configs.GetProfileDirectory(installation));
        var profile = profiles.Load(installation.ActiveProfileId);
        var result = Services.RuntimeComposition.TryCreate(configs, installation, profile);
        return result.Accepted ? result.Composition : null;
    }

    private static void ShowFatalStartupError(Exception exception)
    {
        if (Interlocked.Exchange(ref _showingFatalError, 1) != 0)
        {
            return;
        }

        var logPath = WriteFatalStartupLog(exception);
        var message =
            "DCS Watcher v2 failed to start." + Environment.NewLine +
            Environment.NewLine +
            exception.Message + Environment.NewLine +
            Environment.NewLine +
            "Details were written to:" + Environment.NewLine +
            logPath;

        try
        {
            MessageBox.Show(
                message,
                "DCS Watcher v2 startup error",
                MessageBoxButtons.OK,
                MessageBoxIcon.Error);
        }
        catch
        {
            // The startup log is the durable error path if a message box cannot be shown.
        }

        Environment.ExitCode = 1;
        Interlocked.Exchange(ref _fatalExitRequested, 1);
        TerminateMessageLoop();
    }

    private static void TerminateMessageLoop()
    {
        var form = _activeMainForm;
        if (form is null)
        {
            Application.Exit();
            return;
        }

        void StopAndExit()
        {
            form.StopForFatalError();
            form.Close();
            Application.ExitThread();
        }

        if (form.InvokeRequired)
        {
            try
            {
                form.BeginInvoke(StopAndExit);
            }
            catch
            {
                Application.Exit();
            }
            return;
        }

        StopAndExit();
    }

    private static string WriteFatalStartupLog(Exception exception)
    {
        var text =
            $"Timestamp: {DateTimeOffset.Now:O}{Environment.NewLine}" +
            $"Base directory: {AppContext.BaseDirectory}{Environment.NewLine}" +
            $"Current directory: {Environment.CurrentDirectory}{Environment.NewLine}" +
            Environment.NewLine +
            exception + Environment.NewLine;

        var fallbackPath = Path.Combine(AppContext.BaseDirectory, "DcsWatcherV2-startup-error.log");
        var writtenPath = TryWriteStartupLog(fallbackPath, text);
        if (!string.IsNullOrWhiteSpace(writtenPath))
        {
            return writtenPath;
        }

        var tempPath = Path.Combine(Path.GetTempPath(), "DcsWatcherV2-startup-error.log");
        return TryWriteStartupLog(tempPath, text) ?? tempPath;
    }

    private static string? TryWriteStartupLog(string path, string text)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.AppendAllText(path, text + Environment.NewLine);
            return path;
        }
        catch
        {
            return null;
        }
    }

    private const uint AttachParentProcess = 0xFFFFFFFF;

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool AttachConsole(uint processId);
}
