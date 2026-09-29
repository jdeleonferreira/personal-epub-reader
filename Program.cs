// Atril for the browser: dotnet run, then open http://localhost:5080.
// All server logic lives in AtrilServer.cs (shared with the Windows app).

using Atril;

AtrilServer.Create(new ServerOptions { Args = args }).Run();

public partial class Program;
