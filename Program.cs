// Atril para navegador: dotnet run y abre http://localhost:5080.
// Toda la lógica del servidor está en AtrilServidor.cs (la comparte la app de Windows).

using Atril;

AtrilServidor.Crear(new OpcionesServidor { Args = args }).Run();

public partial class Program;
