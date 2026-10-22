using FlaUI.Core.AutomationElements;
using FlaUI.Core.Input;
using FlaUI.Core.WindowsAPI;
using Dpt.Shell;

[assembly: CollectionBehavior(DisableTestParallelization = true)]

namespace Dpt.App.UITests;

/// <summary>M1 exit criteria: navigating, tabs, rename, new folder, and paste of a file copied in Explorer's clipboard format.</summary>
[Trait("Category", "UI")]
public class ExplorerShellTests
{
    [Fact]
    public void Navigate_into_folder_and_back()
    {
        using var s = new AppSession();
        Assert.Equal(Path.GetFileName(s.Browse), s.Heading);
        s.Select("Alpha");
        s.Press(VirtualKeyShort.RETURN);
        s.WaitUntil(() => s.Heading == "Alpha", "Alpha opened");
        s.Row("inside.txt");
        s.Press(VirtualKeyShort.ALT, VirtualKeyShort.LEFT);
        s.WaitUntil(() => s.Heading == Path.GetFileName(s.Browse), "back to root");
        s.Press(VirtualKeyShort.ALT, VirtualKeyShort.RIGHT);
        s.WaitUntil(() => s.Heading == "Alpha", "forward to Alpha");
        s.Get("UpButton").AsButton().Invoke();
        s.WaitUntil(() => s.Heading == Path.GetFileName(s.Browse), "up to root");
    }

    [Fact]
    public void Enter_opens_selected_folder_and_backspace_goes_up()
    {
        using var s = new AppSession();
        s.Select("Beta");
        s.Press(VirtualKeyShort.RETURN);
        s.WaitUntil(() => s.Heading == "Beta", "Beta opened");
        s.Get("FileList").Focus();
        s.Press(VirtualKeyShort.BACK);
        s.WaitUntil(() => s.Heading == Path.GetFileName(s.Browse), "up via Backspace");
    }

    [Fact]
    public void Tabs_open_switch_and_close()
    {
        using var s = new AppSession();
        var strip = s.Get("TabStrip");
        Assert.Single(strip.FindAllChildren());
        s.Get("NewTabButton").AsButton().Invoke();
        s.WaitUntil(() => strip.FindAllChildren().Length == 2, "second tab");
        s.Get("HomeView");
        s.Press(VirtualKeyShort.CONTROL, VirtualKeyShort.KEY_T);
        s.WaitUntil(() => strip.FindAllChildren().Length == 3, "third tab via Ctrl+T");
        s.Press(VirtualKeyShort.CONTROL, VirtualKeyShort.KEY_W);
        s.WaitUntil(() => strip.FindAllChildren().Length == 2, "closed via Ctrl+W");
        strip.FindAllChildren()[0].Patterns.SelectionItem.Pattern.Select();
        s.WaitUntil(() => s.Heading == Path.GetFileName(s.Browse), "first tab shows folder");
    }

    [Fact]
    public void Middle_click_folder_opens_new_tab()
    {
        using var s = new AppSession();
        var strip = s.Get("TabStrip");
        var row = s.Row("Alpha");
        s.MiddleClick(row);
        s.WaitUntil(() => strip.FindAllChildren().Length == 2, "new tab for Alpha");
        Assert.Equal(Path.GetFileName(s.Browse), s.Heading); // current tab stays put
    }

    [Fact]
    public void Rename_with_F2()
    {
        using var s = new AppSession();
        s.Select("file1.txt");
        s.Press(VirtualKeyShort.F2);
        s.Get("RenameBox");
        s.Press(VirtualKeyShort.CONTROL, VirtualKeyShort.KEY_A);
        s.Type("renamed.txt");
        s.Press(VirtualKeyShort.RETURN);
        s.WaitUntil(() => File.Exists(Path.Combine(s.Browse, "renamed.txt")), "renamed on disk");
        Assert.False(File.Exists(Path.Combine(s.Browse, "file1.txt")));
        s.Row("renamed.txt");
    }

    [Fact]
    public void New_folder_starts_rename()
    {
        using var s = new AppSession();
        s.Get("NewFolderButton").AsButton().Invoke();
        s.WaitUntil(() => Directory.Exists(Path.Combine(s.Browse, "New folder")), "New folder created");
        s.Get("RenameBox");
        s.Type("Made by test");
        s.Press(VirtualKeyShort.RETURN);
        s.WaitUntil(() => Directory.Exists(Path.Combine(s.Browse, "Made by test")), "renamed new folder");
    }

    [Fact]
    public void Paste_file_copied_in_explorer_format()
    {
        using var s = new AppSession();
        var outside = Path.Combine(s.Root, "from-explorer.txt");
        File.WriteAllText(outside, "pasted");
        RunSta(() => FileClipboard.SetFiles([outside], ClipboardFileAction.Copy));
        s.Select("file1.txt");
        s.Press(VirtualKeyShort.CONTROL, VirtualKeyShort.KEY_V);
        s.WaitUntil(() => File.Exists(Path.Combine(s.Browse, "from-explorer.txt")), "pasted file");
        Assert.True(File.Exists(outside));
        s.Row("from-explorer.txt");
    }

    [Fact]
    public void Cut_paste_moves_file()
    {
        using var s = new AppSession();
        var outside = Path.Combine(s.Root, "move-me.txt");
        File.WriteAllText(outside, "moved");
        RunSta(() => FileClipboard.SetFiles([outside], ClipboardFileAction.Cut));
        s.Select("file1.txt");
        s.Press(VirtualKeyShort.CONTROL, VirtualKeyShort.KEY_V);
        s.WaitUntil(() => File.Exists(Path.Combine(s.Browse, "move-me.txt")) && !File.Exists(outside), "moved file");
    }

    [Fact]
    public void Copy_puts_explorer_format_on_clipboard()
    {
        using var s = new AppSession();
        s.Select("file1.txt");
        s.Press(VirtualKeyShort.CONTROL, VirtualKeyShort.KEY_C);
        (IReadOnlyList<string> Paths, ClipboardFileAction Action) got = ([], ClipboardFileAction.None);
        s.WaitUntil(() =>
        {
            RunSta(() => got = FileClipboard.GetFiles());
            return got.Paths.Count == 1;
        }, "clipboard has the file");
        Assert.Equal(Path.Combine(s.Browse, "file1.txt"), got.Paths[0], ignoreCase: true);
        Assert.Equal(ClipboardFileAction.Copy, got.Action);

        s.Press(VirtualKeyShort.CONTROL, VirtualKeyShort.KEY_X);
        s.WaitUntil(() =>
        {
            RunSta(() => got = FileClipboard.GetFiles());
            return got.Action == ClipboardFileAction.Cut;
        }, "clipboard marked as cut");
    }

    [Fact]
    public void Address_bar_accepts_typed_path()
    {
        using var s = new AppSession();
        s.Press(VirtualKeyShort.CONTROL, VirtualKeyShort.KEY_L);
        s.Get("AddressBox");
        s.Press(VirtualKeyShort.CONTROL, VirtualKeyShort.KEY_A);
        s.Type(Path.Combine(s.Browse, "Beta"));
        s.Press(VirtualKeyShort.RETURN);
        s.WaitUntil(() => s.Heading == "Beta", "navigated by typed path");
    }

    private static void RunSta(Action action)
    {
        Exception? error = null;
        var t = new Thread(() =>
        {
            try { action(); }
            catch (Exception ex) { error = ex; }
        });
        t.SetApartmentState(ApartmentState.STA);
        t.Start();
        t.Join();
        if (error is not null) throw error;
    }
}


