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

    public AppSession(string? startFolder = null, bool createSample = true, bool openFolder = true, string? arguments = null, Action<string>? prepare = null)
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
        // Catalog only the test folder (never scan the owner's real drives from tests).
        psi.Environment["DPT_CATALOG_ROOTS"] = Browse;
        App = Application.Launch(psi);
        Automation = new UIA3Automation();
        try
        {
            Window = Retry.WhileNull(() => App.GetMainWindow(Automation, TimeSpan.FromSeconds(1)), TimeSpan.FromSeconds(30), throwOnTimeout: true).Result!;
            WaitFor(() => Find("TabStrip"), TimeSpan.FromSeconds(30));
        }
        catch
        {
            // Never leave a test copy of the app running (it would also hold the test runner's output pipe open).
            try { App.Kill(); } catch (Exception) { }
            Automation.Dispose();
            throw;
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
        try
        {
            App.Close();
            if (!App.HasExited) App.Kill();
        }
        catch (Exception)
        {
            // already gone
        }

        Automation.Dispose();
        App.Dispose();
        if (Environment.GetEnvironmentVariable("DPT_KEEP_UI") == "1") return;
        try { Directory.Delete(Root, true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}


