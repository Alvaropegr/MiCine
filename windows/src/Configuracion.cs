using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace MiCine
{
    /// <summary>
    /// config.json: el MISMO archivo que usa servidor.py, con las mismas
    /// claves y valores por defecto. Si no existe, se crea igual que lo crea
    /// Python.
    /// </summary>
    public sealed class Configuracion
    {
        public readonly Objeto Valores;
        public readonly string Ruta;

        Configuracion(string ruta, Objeto valores) { Ruta = ruta; Valores = valores; }

        public static Objeto PorDefecto()
        {
            return new Objeto
            {
                { "puerto", 8080L },
                { "direccion", "0.0.0.0" },
                { "carpeta_peliculas", "peliculas" },
                { "dias_sesion", 30L },
                { "limite_subida_gb", 100L },
                { "abrir_navegador", true },
            };
        }

        /// <summary>cargar_config(): los valores por defecto, actualizados con lo que haya en el archivo.</summary>
        public static Configuracion Cargar(string ruta)
        {
            var config = PorDefecto();
            if (File.Exists(ruta))
            {
                var leido = Json.LeerArchivo(ruta, new Objeto()) as Objeto;
                if (leido == null) throw new ErrorJson("config.json tiene que ser un objeto JSON: { … }");
                foreach (var kv in leido) config[kv.Key] = kv.Value;
            }
            else
            {
                Json.EscribirArchivo(ruta, PorDefecto());
            }
            return new Configuracion(ruta, config);
        }

        /// <summary>
        /// Cambia una clave en config.json sin tocar las demás (ni su orden).
        /// Python nunca reescribe el archivo; esto solo lo usa la ventana.
        /// </summary>
        public static void Guardar(string ruta, string clave, object valor)
        {
            Objeto datos = File.Exists(ruta) ? Json.LeerArchivo(ruta, new Objeto()) as Objeto : PorDefecto();
            if (datos == null) throw new ErrorJson("config.json tiene que ser un objeto JSON: { … }");
            datos[clave] = valor;
            Json.EscribirArchivo(ruta, datos);
        }

        // ── valores ──────────────────────────────────────────────────────

        public int Puerto
        {
            get
            {
                object v = Valores["puerto"];
                if (v is long && (long)v > 0 && (long)v < 65536) return (int)(long)v;
                throw new FormatException("El puerto de config.json no es válido: " + Py.ReprValor(v));
            }
        }

        public string Direccion
        {
            get
            {
                var s = Valores["direccion"] as string;
                if (s == null) throw new FormatException("La dirección de config.json no es válida: " + Py.ReprValor(Valores["direccion"]));
                return s;
            }
        }

        /// <summary>dias_sesion · 86400 (entero si el valor es entero, como en Python).</summary>
        public object DuracionSesion
        {
            get
            {
                object v = Valores["dias_sesion"];
                if (v is long) return (long)v * 86400;
                if (v is double) return (double)v * 86400;
                if (v is bool) return (bool)v ? 86400L : 0L;
                throw new FormatException("dias_sesion de config.json no es un número: " + Py.ReprValor(v));
            }
        }

        /// <summary>limite_subida_gb tal cual (long o double).</summary>
        public object LimiteSubidaGb
        {
            get
            {
                object v = Valores["limite_subida_gb"];
                if (v is long || v is double) return v;
                if (v is bool) return (bool)v ? 1L : 0L;
                throw new FormatException("limite_subida_gb de config.json no es un número: " + Py.ReprValor(v));
            }
        }

        /// <summary>int(limite_subida_gb * 1024 ** 3)</summary>
        public long LimiteBytes
        {
            get
            {
                object v = LimiteSubidaGb;
                if (v is long) return (long)v * 1073741824L;
                double d = (double)v * 1073741824.0;
                if (d >= long.MaxValue) return long.MaxValue;
                if (d <= long.MinValue) return long.MinValue;
                return (long)d;
            }
        }

        public bool AbrirNavegador { get { return Py.Verdad(Valores["abrir_navegador"]); } }

        /// <summary>Anunciarse a las teles de casa por DLNA (solo MiCine.exe; servidor.py ignora la clave).</summary>
        public bool Dlna { get { return Py.Verdad(Valores.Obtener("dlna", true)); } }

        public string CarpetaPeliculasTexto
        {
            get
            {
                var s = Valores["carpeta_peliculas"] as string;
                if (s == null) throw new FormatException("carpeta_peliculas de config.json no es un texto");
                return s;
            }
        }

        /// <summary>
        /// La carpeta de películas como la calcula Python:
        /// expandvars(expanduser(...)) y, si no es absoluta, dentro de la carpeta de MiCine.
        /// </summary>
        public string CarpetaPeliculas(string baseDir) { return RutaDeConfig(CarpetaPeliculasTexto, baseDir); }

        /// <summary>«otras_carpetas»: más carpetas de películas (otros discos…), tal como están escritas.</summary>
        public List<string> OtrasCarpetasTexto
        {
            get
            {
                object v = Valores.Obtener("otras_carpetas", null);
                var lista = new List<string>();
                if (v == null) return lista;
                var crudo = v as List<object>;
                if (crudo == null || crudo.Any(x => !(x is string)))
                    throw new FormatException("«otras_carpetas» de config.json tiene que ser una lista de carpetas: [\"D:\\\\Películas\", …]");
                foreach (object x in crudo) lista.Add((string)x);
                return lista;
            }
        }

        public List<string> OtrasCarpetas(string baseDir) { return OtrasCarpetasTexto.Select(t => RutaDeConfig(t, baseDir)).ToList(); }

        /// <summary>ruta_de_config() de Python: expandvars(expanduser(...)) y, si no es absoluta, dentro de la carpeta de MiCine.</summary>
        public static string RutaDeConfig(string texto, string baseDir)
        {
            string c = ExpandirVariables(ExpandirUsuario(texto));
            if (EsAbsoluta(c)) return c;
            if (c.StartsWith("\\") || c.StartsWith("/"))
            {
                // Raíz sin unidad: pathlib la pone en la unidad de la carpeta base.
                return Path.GetPathRoot(baseDir).TrimEnd('\\') + c;
            }
            return Path.Combine(baseDir, c);
        }

        /// <summary>PureWindowsPath.is_absolute(): unidad y raíz, o ruta UNC.</summary>
        public static bool EsAbsoluta(string p)
        {
            if (p.Length >= 3 && char.IsLetter(p[0]) && p[1] == ':' && (p[2] == '\\' || p[2] == '/')) return true;
            if (p.Length >= 2 && (p[0] == '\\' || p[0] == '/') && (p[1] == '\\' || p[1] == '/')) return true;
            return false;
        }

        /// <summary>ntpath.expanduser</summary>
        public static string ExpandirUsuario(string ruta)
        {
            if (!ruta.StartsWith("~")) return ruta;
            int i = 1;
            while (i < ruta.Length && ruta[i] != '\\' && ruta[i] != '/') i++;
            string casa = Environment.GetEnvironmentVariable("USERPROFILE");
            if (string.IsNullOrEmpty(casa))
            {
                string unidad = Environment.GetEnvironmentVariable("HOMEDRIVE") ?? "";
                string ruta2 = Environment.GetEnvironmentVariable("HOMEPATH");
                if (ruta2 == null) return ruta;
                casa = unidad + ruta2;
            }
            if (i != 1)
            {
                string otro = ruta.Substring(1, i - 1);
                if (!string.Equals(otro, Path.GetFileName(casa.TrimEnd('\\', '/')), StringComparison.Ordinal))
                    casa = Path.Combine(Path.GetDirectoryName(casa.TrimEnd('\\', '/')) ?? "", otro);
            }
            return casa + ruta.Substring(i);
        }

        /// <summary>ntpath.expandvars: %VAR%, $VAR, ${VAR}, '…' sin expandir.</summary>
        public static string ExpandirVariables(string ruta)
        {
            if (ruta.IndexOf('$') < 0 && ruta.IndexOf('%') < 0) return ruta;
            const string letras = "abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789_-";
            var res = new StringBuilder();
            string p = ruta;
            int index = 0;
            int largo = p.Length;
            while (index < largo)
            {
                char c = p[index];
                if (c == '\'')
                {
                    p = p.Substring(index + 1);
                    largo = p.Length;
                    int k = p.IndexOf('\'');
                    if (k >= 0) { res.Append(c).Append(p.Substring(0, k + 1)); index = k; }
                    else { res.Append(c).Append(p); index = largo - 1; }
                }
                else if (c == '%')
                {
                    if (index + 1 < largo && p[index + 1] == '%') { res.Append(c); index++; }
                    else
                    {
                        p = p.Substring(index + 1);
                        largo = p.Length;
                        int k = p.IndexOf('%');
                        if (k < 0) { res.Append('%').Append(p); index = largo - 1; }
                        else
                        {
                            string nombre = p.Substring(0, k);
                            string valor = nombre.Length > 0 ? Environment.GetEnvironmentVariable(nombre) : null;
                            res.Append(valor ?? ("%" + nombre + "%"));
                            index = k;
                        }
                    }
                }
                else if (c == '$')
                {
                    if (index + 1 < largo && p[index + 1] == '$') { res.Append(c); index++; }
                    else if (index + 1 < largo && p[index + 1] == '{')
                    {
                        p = p.Substring(index + 2);
                        largo = p.Length;
                        int k = p.IndexOf('}');
                        if (k < 0) { res.Append("${").Append(p); index = largo - 1; }
                        else
                        {
                            string nombre = p.Substring(0, k);
                            string valor = nombre.Length > 0 ? Environment.GetEnvironmentVariable(nombre) : null;
                            res.Append(valor ?? ("${" + nombre + "}"));
                            index = k;
                        }
                    }
                    else
                    {
                        var nombre = new StringBuilder();
                        index++;
                        while (index < largo && letras.IndexOf(p[index]) >= 0) { nombre.Append(p[index]); index++; }
                        string valor = nombre.Length > 0 ? Environment.GetEnvironmentVariable(nombre.ToString()) : null;
                        res.Append(valor ?? ("$" + nombre));
                        if (index < largo) index--;
                    }
                }
                else res.Append(c);
                index++;
            }
            return res.ToString();
        }
    }
}
