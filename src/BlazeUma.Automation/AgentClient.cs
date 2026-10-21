using System.IO.Pipes;
using System.Text;

namespace BlazeUma.Automation;

/// <summary>Talks to the in-game agent over its local named pipe: one command, one reply.</summary>
public sealed class AgentClient : IDisposable
{
    private readonly NamedPipeClientStream _pipe;

    private AgentClient(NamedPipeClientStream pipe) => _pipe = pipe;

    public static string PipeName(int pid) => $"BlazeUma.Agent.{pid}";

    /// <summary>Connects to the agent in process <paramref name="pid"/>; throws TimeoutException if none answers.</summary>
    public static AgentClient Connect(int pid, int timeoutMs = 2000)
    {
        var pipe = new NamedPipeClientStream(".", PipeName(pid), PipeDirection.InOut);
        try
        {
            pipe.Connect(timeoutMs);
            pipe.ReadMode = PipeTransmissionMode.Message;
            return new AgentClient(pipe);
        }
        catch
        {
            pipe.Dispose();
            throw;
        }
    }

    public string Send(string command)
    {
        byte[] request = Encoding.UTF8.GetBytes(command);
        _pipe.Write(request);
        _pipe.Flush();

        var reply = new MemoryStream();
        var buffer = new byte[4096];
        do
        {
            int read = _pipe.Read(buffer);
            if (read == 0) throw new IOException("Agent closed the pipe.");
            reply.Write(buffer, 0, read);
        } while (!_pipe.IsMessageComplete);
        return Encoding.UTF8.GetString(reply.ToArray());
    }

    public void Dispose() => _pipe.Dispose();
}
