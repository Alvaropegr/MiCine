using System;
using System.IO;
using System.Threading;
using System.Windows.Forms;

namespace MiCine
{
    /// <summary>
    /// MiCine.exe: el servidor de películas de servidor.py, como programa de
    /// Windows con ventana e icono junto al reloj. No necesita Python ni nada
    /// instalado: .NET Framework 4.8 viene con Windows 10 y 11.
    ///
    ///     MiCine.exe                     ventana + icono en la bandeja
    ///     MiCine.exe --minimizado        arranca escondido en la bandeja (inicio con Windows)
    ///     MiCine.exe --puerto 9000       otro puerto (también se puede fijar en config.json)
    ///     MiCine.exe --direccion IP      0.0.0.0 = toda la red de casa; 127.0.0.1 = solo este ordenador
    ///     MiCine.exe --sin-ventana       solo el servidor, sin ventana; registra en datos\micine.log
    ///     MiCine.exe --sin-navegador     no abre el navegador al arrancar
    /// </summary>
    public static class Programa
    {
        public static string Base { get; private set; }
        public static bool Minimizado { get; private set; }
        public static bool SinNavegador { get; private set; }

        [STAThread]
        public static int Main(string[] args)
        {
            Base = Path.GetDirectoryName(Path.GetFullPath(Application.ExecutablePath));
            var nucleo = new Nucleo(Base);
            bool sinVentana = false;
            for (int i = 0; i < args.Length; i++)
            {
                string a = args[i];
                int n;
                if (a == "--puerto" && i + 1 < args.Length && int.TryParse(args[i + 1], out n)) { nucleo.PuertoForzado = n; i++; }
                else if (a == "--direccion" && i + 1 < args.Length) { nucleo.DireccionForzada = args[i + 1]; i++; }
                else if (a == "--sin-ventana") sinVentana = true;
                else if (a == "--minimizado") Minimizado = true;
                else if (a == "--sin-navegador") SinNavegador = true;
            }

            try { Directory.CreateDirectory(nucleo.Datos); } catch (Exception) { }
            Registro.Configurar(Path.Combine(nucleo.Datos, "micine.log"));
            AppDomain.CurrentDomain.UnhandledException += delegate(object o, UnhandledExceptionEventArgs e)
            {
                var ex = e.ExceptionObject as Exception;
                if (ex != null) Registro.Error("sin controlar", ex);
            };

            if (sinVentana) return SinVentana(nucleo);
            return ConVentana(nucleo);
        }

        /// <summary>Solo el servidor: para pruebas y automatización. Se para cuando se mata el proceso.</summary>
        static int SinVentana(Nucleo nucleo)
        {
            string error = nucleo.Iniciar();
            if (error != null) return 1;
            new ManualResetEvent(false).WaitOne();
            return 0;
        }

        static int ConVentana(Nucleo nucleo)
        {
            Nativo.DpiConsciente();
            // Una sola ventana por carpeta: dos MiCine sobre los mismos datos\ se pisarían.
            string id = "MiCine-" + Cripto.Sha256Hex(Base.ToLowerInvariant()).Substring(0, 16);
            bool nuevo;
            var unico = new Mutex(true, "Local\\" + id, out nuevo);
            if (!nuevo)
            {
                try { EventWaitHandle.OpenExisting("Local\\" + id + "-mostrar").Set(); } catch (Exception) { }
                return 0;
            }
            var mostrar = new EventWaitHandle(false, EventResetMode.AutoReset, "Local\\" + id + "-mostrar");

            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.ThreadException += delegate(object o, ThreadExceptionEventArgs e)
            {
                Registro.Error("ventana", e.Exception);
                MessageBox.Show(e.Exception.Message, "MiCine", MessageBoxButtons.OK, MessageBoxIcon.Error);
            };

            var ventana = new FormPrincipal(nucleo, Minimizado);
            var oyente = new Thread(delegate()
            {
                while (mostrar.WaitOne())
                {
                    try { ventana.BeginInvoke((MethodInvoker)ventana.MostrarVentana); } catch (Exception) { }
                }
            });
            oyente.IsBackground = true;
            oyente.Start();

            Application.Run(ventana);
            nucleo.Detener();
            GC.KeepAlive(unico);
            return 0;
        }
    }
}
