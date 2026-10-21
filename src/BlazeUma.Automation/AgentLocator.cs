namespace BlazeUma.Automation;

/// <summary>
/// Finds the agent DLL: next to the exe (a published build), else agent/build/Release/BlazeUma.Agent.dll
/// searched upwards from the working directory and the exe.
/// </summary>
public static class AgentLocator
{
    public static string? Find()
    {
        string beside = Path.Combine(AppContext.BaseDirectory, Injector.AgentFileName);
        if (File.Exists(beside)) return beside;
        string relative = Path.Combine("agent", "build", "Release", Injector.AgentFileName);
        foreach (string start in new[] { Environment.CurrentDirectory, AppContext.BaseDirectory })
        {
            for (var dir = new DirectoryInfo(start); dir is not null; dir = dir.Parent)
            {
                string candidate = Path.Combine(dir.FullName, relative);
                if (File.Exists(candidate)) return candidate;
            }
        }
        return null;
    }
}
