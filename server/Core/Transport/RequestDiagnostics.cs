using System.Text.Json;

namespace Brs.Local.Transport;

public sealed class RequestDiagnostics
{
    private readonly TextWriter writer;
    private readonly string route;
    private readonly string requestId = Guid.NewGuid().ToString("N");
    private string stage = "entry";

    public RequestDiagnostics(string route, TextWriter? writer = null)
    {
        this.route = route;
        this.writer = writer ?? Console.Out;
    }

    public void Mark(string value)
    {
        stage = value;
        Write("request-stage", null, null, null);
    }

    public void Fault(Exception exception) =>
        Write("request-exception", 500, exception.GetType().FullName, exception.StackTrace);

    public void Complete(int status) => Write("request-complete", status, null, null);

    private void Write(string kind, int? status, string? exceptionType, string? serverStack) =>
        writer.WriteLine(JsonSerializer.Serialize(new
        {
            time = DateTimeOffset.UtcNow, kind, request_id = requestId, route, stage,
            status, exception_type = exceptionType, server_stack = serverStack
        }));
}
