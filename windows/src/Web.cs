using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;

namespace MiCine
{
    /// <summary>
    /// La web (index.html, app.js, estilos.css, icono.svg) viaja DENTRO del
    /// .exe: compilar.ps1 mete como recurso todo lo que hay en la carpeta web\
    /// del proyecto, que sigue siendo la única copia que se edita.
    /// </summary>
    public static class Web
    {
        public const string PREFIJO = "web/";
        static readonly Dictionary<string, KeyValuePair<string, byte[]>> archivos =
            new Dictionary<string, KeyValuePair<string, byte[]>>(StringComparer.OrdinalIgnoreCase);

        static Web()
        {
            var ensamblado = Assembly.GetExecutingAssembly();
            foreach (string nombre in ensamblado.GetManifestResourceNames())
            {
                if (!nombre.StartsWith(PREFIJO, StringComparison.Ordinal)) continue;
                using (var s = ensamblado.GetManifestResourceStream(nombre))
                using (var m = new MemoryStream())
                {
                    s.CopyTo(m);
                    string relativa = nombre.Substring(PREFIJO.Length);
                    archivos[relativa] = new KeyValuePair<string, byte[]>(relativa, m.ToArray());
                }
            }
        }

        public static int Cuantos { get { return archivos.Count; } }

        /// <summary>
        /// Busca un archivo como lo haría Python con (WEB / nombre).resolve():
        /// se normaliza la ruta con «..», «.» y separadores, y se rechaza lo que
        /// queda fuera de web\. Las mayúsculas no importan (Windows), pero el
        /// nombre que se devuelve es el real, y con él se decide el tipo.
        /// </summary>
        public static bool Buscar(string baseDir, string nombre, out string nombreReal, out byte[] contenido)
        {
            nombreReal = null;
            contenido = null;
            if (nombre == null || nombre.IndexOf('\0') >= 0) return false;
            string carpeta = Path.Combine(baseDir, "web");
            string completa;
            try { completa = Path.GetFullPath(Path.Combine(carpeta, nombre)); }
            catch (Exception) { return false; }
            string prefijo = carpeta.TrimEnd('\\') + "\\";
            if (!completa.StartsWith(prefijo, StringComparison.OrdinalIgnoreCase)) return false;
            string relativa = completa.Substring(prefijo.Length).Replace('\\', '/');
            KeyValuePair<string, byte[]> archivo;
            if (!archivos.TryGetValue(relativa, out archivo)) return false;
            nombreReal = archivo.Key;
            contenido = archivo.Value;
            return true;
        }
    }
}
