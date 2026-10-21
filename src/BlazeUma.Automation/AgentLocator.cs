namespace BlazeUma.Automation;

/// <summary>Finds the built agent DLL: agent/build/Release/BlazeUma.Agent.dll, searched upwards from the working directory and the exe.</summary>
public static class AgentLocator
{
    public static string? Find()
    {
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
