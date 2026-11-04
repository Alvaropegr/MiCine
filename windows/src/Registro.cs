using System;
using System.IO;
using System.Text;

namespace MiCine
{
    /// <summary>
    /// registrar() de servidor.py: lo que allí sale por la consola (accesos,
    /// fallos, subidas, borrados, cambios de usuarios) aquí va a la lista de
    /// actividad de la ventana y a datos\micine.log.
    /// </summary>
    public static class Registro
    {
        public delegate void Manejador(DateTime cuando, string mensaje);
        public static event Manejador Nuevo;

        static readonly object cerrojo = new object();
        static string archivo;
        const long TAMANO_MAXIMO = 2 * 1024 * 1024;

        /// <summary>Archivo donde se guarda el registro (null = ninguno).</summary>
        public static void Configurar(string rutaArchivo)
        {
            lock (cerrojo) archivo = rutaArchivo;
        }

        public const int MAXIMO_RECIENTES = 500;
        static readonly System.Collections.Generic.List<System.Collections.Generic.KeyValuePair<DateTime, string>> recientes =
            new System.Collections.Generic.List<System.Collections.Generic.KeyValuePair<DateTime, string>>();

        /// <summary>Lo último que se ha anotado (para la ventana que se abre después), de más antiguo a más nuevo.</summary>
        public static System.Collections.Generic.List<System.Collections.Generic.KeyValuePair<DateTime, string>> Recientes()
        {
            lock (cerrojo) return new System.Collections.Generic.List<System.Collections.Generic.KeyValuePair<DateTime, string>>(recientes);
        }

        public static void Anotar(string mensaje)
        {
            DateTime ahora = DateTime.Now;
            lock (cerrojo)
            {
                recientes.Add(new System.Collections.Generic.KeyValuePair<DateTime, string>(ahora, mensaje));
                if (recientes.Count > MAXIMO_RECIENTES) recientes.RemoveAt(0);
                try { Console.Out.WriteLine("[" + ahora.ToString("HH:mm:ss", Py.Inv) + "] " + mensaje); Console.Out.Flush(); }
                catch (Exception) { }
                if (archivo != null)
                {
                    try
                    {
                        var info = new FileInfo(archivo);
                        if (info.Exists && info.Length > TAMANO_MAXIMO)
                        {
                            string viejo = archivo + ".1";
                            if (File.Exists(viejo)) File.Delete(viejo);
                            File.Move(archivo, viejo);
                        }
                        File.AppendAllText(archivo, "[" + ahora.ToString("yyyy-MM-dd HH:mm:ss", Py.Inv) + "] " + mensaje + "\r\n", Py.Utf8);
                    }
                    catch (Exception) { }
                }
            }
            var aviso = Nuevo;
            if (aviso != null)
            {
                try { aviso(ahora, mensaje); } catch (Exception) { }
            }
        }

        /// <summary>Lo que en Python es traceback.print_exc(): un error inesperado.</summary>
        public static void Error(string contexto, Exception e)
        {
            Anotar("Error interno (" + contexto + "): " + e.GetType().Name + ": " + e.Message);
            lock (cerrojo)
            {
                if (archivo == null) return;
                // Pila recortada: un JSON con miles de niveles daría miles de líneas.
                string[] lineas = e.ToString().Split('\n');
                string texto = lineas.Length <= 30 ? e.ToString()
                    : string.Join("\n", lineas, 0, 30) + "\n   … (" + (lineas.Length - 30) + " líneas más)";
                try { File.AppendAllText(archivo, texto.TrimEnd() + "\r\n", Py.Utf8); } catch (Exception) { }
            }
        }
    }
}
