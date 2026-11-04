using System.Reflection;
using System.Runtime.Versioning;

// Sin esto, .NET trata el .exe como un programa antiguo y usa el manejo de rutas
// heredado: nada de rutas de más de 260 caracteres (Python sí las admite).
[assembly: TargetFramework(".NETFramework,Version=v4.8", FrameworkDisplayName = ".NET Framework 4.8")]

// Lo que enseña Windows en Propiedades → Detalles, y el nombre que sale en el
// aviso del cortafuegos la primera vez que MiCine escucha en la red.
[assembly: AssemblyTitle("MiCine")]
[assembly: AssemblyDescription("MiCine: tu propio servidor de películas")]
[assembly: AssemblyProduct("MiCine")]
[assembly: AssemblyCopyright("MiCine")]
[assembly: AssemblyVersion("1.0.0.0")]
[assembly: AssemblyFileVersion("1.0.0.0")]
