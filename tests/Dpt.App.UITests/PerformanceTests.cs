using System.Globalization;
using System.Text.Json;
using FlaUI.Core.AutomationElements;
using Xunit.Abstractions;

namespace Dpt.App.UITests;

[Trait("Category", "UI")]
[Trait("Category", "Perf")]
public class PerformanceTests(ITestOutputHelper output)
{
    private static JsonElement? Timing(AppSession session, string kind)
    {
        var path = Path.Combine(session.DataDir, "logs", "performance.jsonl");
        if (!File.Exists(path)) return null;
        try
        {
            return File.ReadAllLines(path).Select(line =>
            {
                using var document = JsonDocument.Parse(line);
                return (JsonElement?)document.RootElement.Clone();
            }).FirstOrDefault(entry => entry?.GetProperty("kind").GetString() == kind);
        }
        catch (IOException) { return null; }
        catch (JsonException) { return null; } // the producer may still be completing its line
    }

    [Fact]
    public void Fresh_process_to_first_render_is_under_1500ms()
    {
        var times = new List<double>();
        for (var i = 0; i < 5; i++)
        {
            using var session = new AppSession(createSample: false, openFolder: false, measurePerformance: true);
            session.WaitUntil(() => Timing(session, "startup") is not null, "first-render timing");
            times.Add(Timing(session, "startup")!.Value.GetProperty("milliseconds").GetDouble());
        }
        output.WriteLine("Fresh process, empty data, full Home render (ms): " + string.Join(", ", times.Select(t => t.ToString("F1", CultureInfo.InvariantCulture))));
        output.WriteLine("Includes CLR startup. OS/runtime disk caches were not flushed; this is not proof of cold-cache startup.");
        Assert.All(times, time => Assert.True(time < 1500, $"First render took {time:F1} ms (target <1500 ms)."));
    }

    [Fact]
    public void Fifty_thousand_items_are_loaded_sorted_bound_and_rendered_under_300ms()
    {
        var root = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "dpt-perf-" + Guid.NewGuid().ToString("N")));
        Directory.CreateDirectory(root);
        try
        {
            for (var i = 0; i < 50_000; i++)
                using (File.Create(Path.Combine(root, $"document{(i * 7919) % 50_000}.txt"))) { }
            var times = new List<double>();
            for (var i = 0; i < 5; i++)
            {
                using var session = new AppSession(startFolder: root, createSample: false, openFolder: false, measurePerformance: true);
                session.WaitUntil(() => Timing(session, "startup") is not null, "Home first render before folder navigation");
                session.Get("EditAddressButton").AsButton().Invoke();
                session.Get("AddressBox").AsTextBox().Text = root;
                session.Get("GoToAddressButton").AsButton().Invoke();
                session.WaitUntil(() => Timing(session, "folder") is not null, "complete 50k folder timing");
                var timing = Timing(session, "folder")!.Value;
                Assert.Equal(50_000, timing.GetProperty("count").GetInt32());
                var time = timing.GetProperty("milliseconds").GetDouble(); times.Add(time);
                output.WriteLine($"50k run {i + 1}: total={time:F1} ms, enumeration={timing.GetProperty("enumerationMs").GetDouble():F1}, application={timing.GetProperty("applicationMs").GetDouble():F1}");
                Assert.NotNull(session.Row("document0.txt"));
            }
            output.WriteLine("Navigation after Home first render: full enumeration, row creation, natural sort, binding and first folder render. Fresh processes, newly created local NTFS files; OS cache not flushed; normal catalog activity enabled.");
            Assert.All(times, time => Assert.True(time < 300, $"The 50k-item folder took {time:F1} ms (target <300 ms)."));
        }
        finally
        {
            var parent = Path.GetFullPath(Path.GetTempPath()).TrimEnd('\\') + "\\";
            if (!root.StartsWith(parent, StringComparison.OrdinalIgnoreCase) || !Path.GetFileName(root).StartsWith("dpt-perf-", StringComparison.Ordinal))
                throw new InvalidOperationException("Performance cleanup escaped its temporary test folder.");
            Directory.Delete(root, recursive: true);
        }
    }
}
