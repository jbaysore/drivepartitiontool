using System.Diagnostics;
using FlaUI.Core;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Conditions;
using FlaUI.Core.Input;
using FlaUI.Core.Tools;
using FlaUI.Core.WindowsAPI;
using FlaUI.UIA3;

namespace Dpt.App.UITests;

/// <summary>
/// Launches DrivePartitionTool against a throwaway data folder and a throwaway browse folder, and closes it afterwards.
/// </summary>
public sealed class AppSession : IDisposable
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(15);
    private readonly Process _process;

    public AppSession(string? startFolder = null, bool createSample = true, bool openFolder = true, string? arguments = null, Action<string>? prepare = null, bool measurePerformance = false)
    {
        Root = Path.Combine(Path.GetTempPath(), "dpt-ui-tests", Guid.NewGuid().ToString("N"));
        DataDir = Path.Combine(Root, "data");
        Browse = startFolder ?? Path.Combine(Root, "browse");
        Directory.CreateDirectory(DataDir);
        Directory.CreateDirectory(Browse);
        if (createSample)
        {
            Directory.CreateDirectory(Path.Combine(Browse, "Alpha"));
            Directory.CreateDirectory(Path.Combine(Browse, "Beta"));
            File.WriteAllText(Path.Combine(Browse, "Alpha", "inside.txt"), "inside");
            File.WriteAllText(Path.Combine(Browse, "file1.txt"), "one");
        }

        prepare?.Invoke(Browse);
        var psi = new ProcessStartInfo(ExePath, arguments ?? (openFolder ? $"\"{Browse}\"" : "")) { UseShellExecute = false };
        psi.Environment["DPT_DATA_DIR"] = DataDir;
        psi.Environment["DPT_SAFETY"] = "virtual-only";
        psi.Environment["DPT_MEASURE_PERF"] = measurePerformance ? "1" : "0";
        if (Environment.GetEnvironmentVariable("DPT_TEST_APP_EXE") is { Length: > 0 })
        {
            psi.Environment["DOTNET_ROOT"] = Path.Combine(Root, "no-external-runtime");
            psi.Environment["DOTNET_MULTILEVEL_LOOKUP"] = "0";
        }
        // Catalog only the test folder (never scan the owner's real drives from tests).
        psi.Environment["DPT_CATALOG_ROOTS"] = Browse;
        // Retain the process we started, including its exit handle; reopening by PID loses the exit code after shutdown.
        _process = Process.Start(psi) ?? throw new InvalidOperationException("The test app did not start.");
        App = Application.Attach(_process);
        Automation = new UIA3Automation();
        try
        {
            // Match the actual app window and its ready content, rather than retaining a transient startup window.
            Window = Retry.WhileNull(() => App.GetAllTopLevelWindows(Automation)
                .FirstOrDefault(w => w.AutomationId == "MainWindow" && w.FindFirstDescendant(Automation.ConditionFactory.ByAutomationId("TabStrip")) is not null),
                TimeSpan.FromSeconds(30), TimeSpan.FromMilliseconds(150), throwOnTimeout: true, ignoreException: true).Result!;
            if (Environment.GetEnvironmentVariable("DPT_TEST_APP_EXE") is { Length: > 0 })
            {
                using var process = Process.GetProcessById(App.ProcessId);
                var runtime = process.Modules.Cast<ProcessModule>().Single(m => m.ModuleName.Equals("coreclr.dll", StringComparison.OrdinalIgnoreCase));
                Assert.Equal(Path.Combine(Path.GetDirectoryName(ExePath)!, "coreclr.dll"), runtime.FileName, ignoreCase: true);
            }
        }
        catch (Exception ex)
        {
            var diagnostic = "";
            try
            {
                var logs = Path.Combine(DataDir, "logs");
                diagnostic = Directory.Exists(logs) ? string.Join("\n", Directory.EnumerateFiles(logs, "*.log").Select(File.ReadAllText)) : "No app log.";
                diagnostic += "\nWindows: " + string.Join(" | ", App.GetAllTopLevelWindows(Automation).Select(w => w.Name + " (" + w.AutomationId + ")"));
            }
            catch (Exception) { }
            // Never leave a test copy of the app running (it would also hold the test runner's output pipe open).
            try { App.Kill(); } catch (Exception) { }
            Automation.Dispose();
            App.Dispose();
            _process.Dispose();
            throw new InvalidOperationException($"The test app did not become ready. Data: {Root}\n{diagnostic}", ex);
        }
    }

    public string Root { get; }
    public string DataDir { get; }
    public string Browse { get; }
    public Application App { get; }
    public UIA3Automation Automation { get; }
    public Window Window { get; }

    public static string ExePath
    {
        get
        {
            if (Environment.GetEnvironmentVariable("DPT_TEST_APP_EXE") is { Length: > 0 } package)
            {
                if (!Path.IsPathFullyQualified(package) || !File.Exists(package)) throw new InvalidOperationException("DPT_TEST_APP_EXE must name an existing, absolute packaged executable.");
                return package;
            }
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "DrivePartitionTool.sln"))) dir = dir.Parent;
            if (dir is null) throw new InvalidOperationException("Repository root not found.");
#if DEBUG
            const string config = "Debug";
#else
            const string config = "Release";
#endif
            return Path.Combine(dir.FullName, "src", "Dpt.App", "bin", config, "net10.0-windows", "DrivePartitionTool.exe");
        }
    }

    public ConditionFactory Cf => Automation.ConditionFactory;

    public AutomationElement? Find(string automationId) => Window.FindFirstDescendant(Cf.ByAutomationId(automationId));

    public AutomationElement Get(string automationId) => WaitFor(() => Find(automationId));

    public T WaitFor<T>(Func<T?> probe, TimeSpan? timeout = null) where T : class =>
        Retry.WhileNull(probe, timeout ?? Timeout, TimeSpan.FromMilliseconds(150), throwOnTimeout: true, timeoutMessage: "Timed out waiting for UI").Result!;

    public void WaitUntil(Func<bool> condition, string what, TimeSpan? timeout = null)
    {
        var ok = Retry.WhileFalse(condition, timeout ?? Timeout, TimeSpan.FromMilliseconds(150)).Result;
        if (!ok) Assert.Fail($"Timed out waiting for: {what}. Screenshot: {Screenshot()}");
    }

    public string Screenshot()
    {
        var path = Path.Combine(Path.GetTempPath(), $"dpt-ui-fail-{DateTime.Now:HHmmss}-{Guid.NewGuid():N}.png");
        try { FlaUI.Core.Capturing.Capture.Element(Window).ToFile(path); }
        catch (Exception) { return "(capture failed)"; }
        return path;
    }

    public string Heading => Find("FolderHeading")?.Name ?? "";

    public AutomationElement Row(string name) =>
        WaitFor(() => Find("FileList")?.FindFirstDescendant(
            Cf.ByControlType(FlaUI.Core.Definitions.ControlType.ListItem).Or(Cf.ByControlType(FlaUI.Core.Definitions.ControlType.DataItem)).And(Cf.ByName(name))));

    /// <summary>Selects and focuses a row through UI Automation (no mouse movement).</summary>
    public AutomationElement Select(string name)
    {
        var row = Row(name);
        row.Patterns.SelectionItem.Pattern.Select();
        row.Focus();
        Thread.Sleep(100);
        return row;
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    /// <summary>
    /// Real keyboard/mouse input goes to whatever window is in front. Refuse to send any unless the app under test is the
    /// foreground window, so a test can never type into the owner's other apps.
    /// </summary>
    public void EnsureForeground()
    {
        var hwnd = Window.Properties.NativeWindowHandle.Value;
        for (var i = 0; i < 10; i++)
        {
            if (GetForegroundWindow() == hwnd) return;
            Window.SetForeground();
            Thread.Sleep(150);
        }

        Assert.Fail("The app under test is not the foreground window; refusing to send keyboard/mouse input.");
    }

    public void Press(params VirtualKeyShort[] keys)
    {
        EnsureForeground();
        Keyboard.TypeSimultaneously(keys);
        Thread.Sleep(150);
    }

    public void Type(string text)
    {
        EnsureForeground();
        Keyboard.Type(text);
        Thread.Sleep(100);
    }

    public void MiddleClick(AutomationElement element)
    {
        EnsureForeground();
        Mouse.Click(element.GetClickablePoint(), MouseButton.Middle);
        Thread.Sleep(150);
    }

    public void Dispose()
    {
        Exception? closeError = null;
        try
        {
            if (!_process.HasExited)
            {
                WaitUntil(() => Window.Properties.ItemStatus.ValueOrDefault == "Ready", "file/disk jobs finish before the test app exits");
                // Invoke the app's actual close control. WindowPattern.Close can be dropped during a modal provider's teardown.
                try { Get("CloseAppButton").AsButton().Invoke(); }
                catch (Exception) when (_process.HasExited && _process.ExitCode == 0) { }
                if (!_process.WaitForExit(5000)) throw new TimeoutException("The test app did not exit after its file/disk jobs finished.");
            }
            if (_process.ExitCode != 0) throw new InvalidOperationException($"The test app exited with code {_process.ExitCode}.");
        }
        catch (Exception ex)
        {
            var diagnostic = "";
            try
            {
                diagnostic = string.Join("\n", Directory.EnumerateFiles(Path.Combine(DataDir, "logs"), "*.log").Select(File.ReadAllText));
                if (!_process.HasExited) diagnostic += "\nWindows: " + string.Join(" | ", App.GetAllTopLevelWindows(Automation).Select(w => $"{w.Name} ({w.AutomationId}), enabled={w.IsEnabled}"));
            }
            catch (Exception) { }
            closeError = new InvalidOperationException($"Data: {Root}\n{diagnostic}", ex);
        }
        finally
        {
            try { if (!_process.HasExited) App.Kill(); } catch (Exception) { }
            Automation.Dispose(); App.Dispose(); _process.Dispose();
            if (Environment.GetEnvironmentVariable("DPT_KEEP_UI") != "1")
                try
                {
                    var parent = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "dpt-ui-tests")).TrimEnd('\\') + "\\";
                    var resolved = Path.GetFullPath(Root);
                    if (!resolved.StartsWith(parent, StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("Test cleanup escaped its temporary directory.");
                    Directory.Delete(resolved, true);
                }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
        }
        if (closeError is not null) throw new InvalidOperationException("The test app failed its shutdown check.", closeError);
    }
}


