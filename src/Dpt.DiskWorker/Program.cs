using System.Security.Principal;
using System.Text.Json;
using Dpt.DiskWorker;
using Dpt.Ipc;

// DrivePartitionTool disk worker. Started elevated (one UAC prompt) for a single, previewed disk plan:
//   DrivePartitionTool.DiskWorker.exe --plan <plan.json>
// It re-checks everything before writing and refuses rather than guess (see AGENTS.md safety rules).

if (args.Length != 2 || args[0] != "--plan" || !File.Exists(args[1]))
{
    Console.Error.WriteLine("Usage: DrivePartitionTool.DiskWorker --plan <plan.json>");
    return DiskWorkerExit.BadArguments;
}

DiskPlan plan;
try
{
    plan = JsonSerializer.Deserialize<DiskPlan>(File.ReadAllText(args[1]), PipeProtocol.Json) ?? throw new InvalidDataException("Empty plan.");
}
catch (Exception ex) when (ex is JsonException or InvalidDataException or IOException)
{
    Console.Error.WriteLine("Invalid plan: " + ex.Message);
    return DiskWorkerExit.BadArguments;
}

using var status = new StatusWriter(plan.StatusFile);
using (var id = WindowsIdentity.GetCurrent())
{
    if (!new WindowsPrincipal(id).IsInRole(WindowsBuiltInRole.Administrator))
    {
        status.Write(DiskProgressState.Refused, "Administrator rights are needed for disk changes.");
        return DiskWorkerExit.NotElevated;
    }
}

Dpt.DiskWorker.Clone.CloneEngine.Register();
Dpt.DiskWorker.Windows.WindowsCopyEngine.Register();
return new PlanRunner(plan, status, SafetyMode.Current).Run();
