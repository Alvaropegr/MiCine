using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace MiCine
{
    /// <summary>Tipos de archivo: los mismos que servidor.py.</summary>
    public static class Tipos
    {
        // Tipo MIME con el que se envía cada vídeo. Las .mkv se anuncian como WebM (un subconjunto
        // de Matroska) y las .mov como MP4: así Chrome y Edge las reproducen si sus códecs lo permiten.
        public static readonly Dictionary<string, string> Videos = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            { ".mp4", "video/mp4" }, { ".m4v", "video/mp4" }, { ".webm", "video/webm" }, { ".ogv", "video/ogg" },
            { ".mkv", "video/webm" }, { ".mov", "video/mp4" },
            { ".avi", "video/x-msvideo" }, { ".wmv", "video/x-ms-wmv" }, { ".flv", "video/x-flv" },
            { ".mpg", "video/mpeg" }, { ".mpeg", "video/mpeg" }, { ".m2ts", "video/mp2t" }, { ".3gp", "video/3gpp" },
        };
        public static readonly HashSet<string> Compatibles = new HashSet<string> { ".mp4", ".m4v", ".webm", ".ogv" }; // el navegador las reproduce casi siempre
        public static readonly HashSet<string> Depende = new HashSet<string> { ".mkv", ".mov" };                     // depende de los códecs con que se grabaron
        public static readonly HashSet<string> Subtitulos = new HashSet<string> { ".srt", ".vtt" };
        public static readonly Dictionary<string, string> Imagenes = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            { ".jpg", "image/jpeg" }, { ".jpeg", "image/jpeg" }, { ".png", "image/png" }, { ".webp", "image/webp" },
        };
        public static readonly string[] PostersCarpeta = { "poster", "cover", "folder" };
        public static readonly Dictionary<string, string> Idiomas = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            { "es", "Español" }, { "spa", "Español" }, { "esp", "Español" }, { "en", "Inglés" }, { "eng", "Inglés" },
            { "fr", "Francés" }, { "fre", "Francés" }, { "fra", "Francés" }, { "it", "Italiano" }, { "ita", "Italiano" },
            { "de", "Alemán" }, { "ger", "Alemán" }, { "deu", "Alemán" }, { "pt", "Portugués" }, { "por", "Portugués" },
            { "ca", "Catalán" }, { "cat", "Catalán" }, { "forced", "forzados" },
        };
        public static readonly HashSet<string> ReservadosWindows = new HashSet<string>(StringComparer.Ordinal);

        static Tipos()
        {
            foreach (string r in new[] { "CON", "PRN", "AUX", "NUL" }) ReservadosWindows.Add(r);
            for (int i = 0; i < 10; i++) { ReservadosWindows.Add("COM" + i); ReservadosWindows.Add("LPT" + i); }
        }

        public static bool EsAcompanante(string ext) { return Subtitulos.Contains(ext) || Imagenes.ContainsKey(ext); }
    }

    /// <summary>Una de las carpetas de películas: la principal (sin prefijo) o una añadida.</summary>
    public sealed class Raiz
    {
        public readonly string Ruta, Prefijo, Nombre;
        public Raiz(string ruta, string prefijo, string nombre) { Ruta = ruta; Prefijo = prefijo; Nombre = nombre; }
        public string ConBarra { get { return Ruta.EndsWith("\\") ? Ruta : Ruta + "\\"; } }
    }

    /// <summary>
    /// Las carpetas de películas: la principal (donde se guardan las subidas) y las que se añadan, en
    /// otros discos por ejemplo. Juntas forman un solo catálogo.
    /// </summary>
    public sealed class Biblioteca
    {
        public readonly string Carpeta;
        public readonly object Cerrojo = new object();
        public readonly List<Raiz> Raices = new List<Raiz>();

        // Las películas de las carpetas añadidas van como «.1a2b3c4d/…»: un nombre que empieza por punto
        // nunca es de la carpeta principal (se tratan como ocultos), así que no se pueden confundir.
        static readonly Regex OtraCarpeta = new Regex(@"^\.([0-9a-f]{8})/(.+)\z", RegexOptions.Singleline);

        public Biblioteca(string carpeta) : this(carpeta, null) { }

        public Biblioteca(string carpeta, List<string> otras)
        {
            Directory.CreateDirectory(carpeta);
            Carpeta = Normal(Nativo.RutaReal(Path.GetFullPath(carpeta)));
            Raices.Add(new Raiz(Carpeta, "", ""));
            foreach (string otra in otras ?? new List<string>())
            {
                string real;
                try { real = Normal(Nativo.RutaReal(Path.GetFullPath(otra))); }
                catch (Exception) { Registro.Anotar("Se ignora la carpeta «" + otra + "»: no es una ruta válida"); continue; }
                if (Raices.Any(r => Solapan(r.Ruta, real)))
                {
                    Registro.Anotar("Se ignora la carpeta «" + otra + "»: está dentro de otra carpeta de MiCine o la contiene");
                    continue;
                }
                Raices.Add(new Raiz(real, "." + IdCarpeta(otra) + "/", NombreCarpeta(otra)));
            }
        }

        static string Normal(string ruta)
        {
            ruta = ruta.TrimEnd('\\');
            return ruta.EndsWith(":") ? ruta + "\\" : ruta;
        }

        /// <summary>Si una carpeta está dentro de la otra (o son la misma).</summary>
        public static bool Solapan(string a, string b)
        {
            string x = a.TrimEnd('\\') + "\\", y = b.TrimEnd('\\') + "\\";
            return x.StartsWith(y, StringComparison.OrdinalIgnoreCase) || y.StartsWith(x, StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>id_carpeta() de Python: el mismo identificador fijo para la misma carpeta en los dos servidores.</summary>
        public static string IdCarpeta(string configurada)
        {
            string normal = Path.GetFullPath(configurada).Replace('/', '\\').ToLowerInvariant().TrimEnd('\\');
            return Cripto.Sha256Hex(normal).Substring(0, 8);
        }

        /// <summary>Con qué nombre se agrupan sus películas: «Series», o «Disco E» si es un disco entero.</summary>
        public static string NombreCarpeta(string configurada)
        {
            string completa = Path.GetFullPath(configurada).Replace('/', '\\').TrimEnd('\\');
            string raiz = (Path.GetPathRoot(completa + "\\") ?? "").TrimEnd('\\');
            if (string.Equals(completa, raiz, StringComparison.OrdinalIgnoreCase)) return "Disco " + raiz.TrimEnd(':');
            return Path.GetFileName(completa);
        }

        Raiz RaizDe(string ruta)
        {
            // Las carpetas no están unas dentro de otras: solo puede ser una.
            return Raices.First(r => ruta.StartsWith(r.ConBarra, StringComparison.OrdinalIgnoreCase)
                || string.Equals(ruta.TrimEnd('\\'), r.Ruta.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase));
        }

        public string Relativa(string ruta)
        {
            var r = RaizDe(ruta);
            return r.Prefijo + ruta.Substring(r.ConBarra.Length).Replace('\\', '/');
        }

        /// <summary>Convierte una ruta recibida de fuera en un archivo de la biblioteca, sin salirse de ella.</summary>
        public string Resolver(string relativa)
        {
            if (string.IsNullOrEmpty(relativa) || relativa.IndexOf('\0') >= 0) return null;
            Raiz raiz = Raices[0];
            Match otra = OtraCarpeta.Match(relativa);
            if (otra.Success)
            {
                raiz = Raices.Skip(1).FirstOrDefault(r => r.Prefijo == "." + otra.Groups[1].Value + "/");
                if (raiz == null) return null;
                relativa = otra.Groups[2].Value;
            }
            string ruta;
            try
            {
                ruta = Path.GetFullPath(Path.Combine(raiz.Ruta, relativa));
                ruta = Nativo.RutaReal(ruta);
            }
            catch (Exception) { return null; }
            if (string.Equals(ruta.TrimEnd('\\'), raiz.Ruta.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase)) return null;
            if (!ruta.StartsWith(raiz.ConBarra, StringComparison.OrdinalIgnoreCase)) return null;
            foreach (string parte in ruta.Substring(raiz.ConBarra.Length).Split('\\'))
                if (parte.StartsWith(".")) return null;
            return File.Exists(ruta) ? ruta : null;
        }

        // ── listado ──────────────────────────────────────────────────────

        public List<Objeto> Listar()
        {
            var peliculas = new List<Objeto>();
            foreach (var r in Raices)
                if (Directory.Exists(r.Ruta)) Recorrer(r, r.Ruta, peliculas); // un disco desconectado no se cuenta
            // sort() de Python es estable: OrderBy también.
            return peliculas.OrderBy(p => Py.Casefold((string)p["titulo"]), Comparer<string>.Create(Py.OrdenPython)).ToList();
        }

        void Recorrer(Raiz r, string raiz, List<Objeto> peliculas)
        {
            var carpetas = new List<string>();
            var archivos = new List<string>();
            var info = new Dictionary<string, FileSystemInfo>(StringComparer.Ordinal);
            try
            {
                foreach (var e in new DirectoryInfo(raiz).EnumerateFileSystemInfos())
                {
                    if ((e.Attributes & FileAttributes.Directory) != 0)
                    {
                        // Enlaces y uniones de carpeta no se recorren: lo de dentro no se podría reproducir
                        // (está fuera de la carpeta) o saldría repetido.
                        if ((e.Attributes & FileAttributes.ReparsePoint) == 0) carpetas.Add(e.Name);
                    }
                    else
                    {
                        archivos.Add(e.Name);
                        info[e.Name] = e;
                    }
                }
            }
            catch (Exception) { return; }

            var ordenados = Py.Ordenados(archivos);
            string posterCarpeta = null;
            foreach (string a in ordenados)
            {
                string b, x;
                Py.SplitExt(a, out b, out x);
                if (Array.IndexOf(Tipos.PostersCarpeta, Py.Lower(b)) >= 0 && Tipos.Imagenes.ContainsKey(Py.Lower(x))) { posterCarpeta = a; break; }
            }
            var videos = archivos.Where(a => !a.StartsWith(".") && Tipos.Videos.ContainsKey(Py.Lower(Py.Ext(a)))).ToList();
            foreach (string nombre in videos)
            {
                long tamano, fecha;
                try
                {
                    var fi = (FileInfo)info[nombre];
                    fi.Refresh();
                    tamano = fi.Length;
                    fecha = Py.Segundos(fi.LastWriteTimeUtc);
                }
                catch (Exception) { continue; }
                string ext = Py.Lower(Py.Ext(nombre));
                string nombreBase = nombre.Substring(0, nombre.Length - ext.Length);
                var subtitulos = new List<object>();
                string poster = null;
                string prefijo = Py.Lower(nombreBase) + ".";
                foreach (string otro in ordenados)
                {
                    string otroExt = Py.Lower(Py.Ext(otro));
                    if (!Tipos.EsAcompanante(otroExt) || !Py.Lower(otro).StartsWith(prefijo, StringComparison.Ordinal)) continue;
                    int desde = nombreBase.Length + 1, hasta = otro.Length - otroExt.Length;
                    string medio = hasta > desde ? otro.Substring(desde, hasta - desde) : "";
                    if (Tipos.Subtitulos.Contains(otroExt))
                    {
                        string idioma;
                        string etiqueta = EtiquetaSubtitulo(medio, out idioma);
                        subtitulos.Add(new Objeto { { "ruta", Relativa(Path.Combine(raiz, otro)) }, { "etiqueta", etiqueta }, { "idioma", idioma } });
                    }
                    else if (poster == null) poster = Relativa(Path.Combine(raiz, otro));
                }
                if (poster == null && posterCarpeta != null && videos.Count == 1) poster = Relativa(Path.Combine(raiz, posterCarpeta));
                string sub = string.Equals(raiz.TrimEnd('\\'), r.Ruta.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase)
                    ? "" : raiz.Substring(r.ConBarra.Length).Replace('\\', '/');
                string carpeta = string.Join("/", new[] { r.Nombre, sub }.Where(x => x != ""));
                peliculas.Add(new Objeto
                {
                    { "ruta", Relativa(Path.Combine(raiz, nombre)) },
                    { "titulo", TituloLegible(nombreBase) },
                    { "carpeta", carpeta },
                    { "tamano", tamano },
                    { "fecha", fecha },
                    { "compat", Tipos.Compatibles.Contains(ext) ? "si" : Tipos.Depende.Contains(ext) ? "quizas" : "no" },
                    { "subtitulos", subtitulos },
                    { "poster", poster },
                });
            }
            foreach (string c in Py.Ordenados(carpetas))
                if (!c.StartsWith(".")) Recorrer(r, Path.Combine(raiz, c), peliculas);
        }

        // ── subidas ──────────────────────────────────────────────────────

        static readonly Regex ProhibidosEnNombre = new Regex("[<>:\"|?*\\x00-\\x1f]");

        public static string NombreSeguro(string nombre)
        {
            string[] trozos = nombre.Replace("\\", "/").Split('/');
            nombre = trozos[trozos.Length - 1];
            nombre = Py.Strip(Py.Strip(Py.Strip(ProhibidosEnNombre.Replace(nombre, "")), "."));
            string nombreBase, ext;
            Py.SplitExt(nombre, out nombreBase, out ext);
            string extMin = Py.Lower(ext);
            if (nombreBase.Length == 0 || !(Tipos.Videos.ContainsKey(extMin) || Tipos.EsAcompanante(extMin)))
                throw new ErrorPeticion("Solo se pueden subir vídeos, subtítulos (.srt, .vtt) o carátulas (.jpg, .png, .webp)");
            if (Tipos.ReservadosWindows.Contains(Py.Upper(nombreBase.Split('.')[0]))) nombreBase = "_" + nombreBase;
            return Py.Recortar(nombreBase, 180) + extMin;
        }

        static bool ExisteRuta(string ruta) { return File.Exists(ruta) || Directory.Exists(ruta); }

        public string DestinoLibre(string nombre)
        {
            string nombreBase, ext;
            Py.SplitExt(nombre, out nombreBase, out ext);
            for (int numero = 1; ; numero++)
            {
                string candidato = Path.Combine(Carpeta, numero == 1 ? nombre : nombreBase + " (" + numero + ")" + ext);
                if (!ExisteRuta(candidato) && !ExisteRuta(candidato + ".subiendo")) return candidato;
            }
        }

        // ── papelera ─────────────────────────────────────────────────────

        /// <summary>Mueve la película (y sus subtítulos y carátula) a .papelera. No borra nada de verdad.</summary>
        public int APapelera(string relativa)
        {
            string ruta = Resolver(relativa);
            string nombre = ruta == null ? "" : Path.GetFileName(ruta);
            string sufijo = Py.Sufijo(nombre);
            if (ruta == null || !Tipos.Videos.ContainsKey(Py.Lower(sufijo))) throw new ErrorPeticion("Esa película ya no existe", 404);
            string prefijo = Py.Lower(nombre.Substring(0, nombre.Length - sufijo.Length)) + ".";
            var archivos = new List<string> { ruta };
            foreach (string otro in Directory.EnumerateFileSystemEntries(Path.GetDirectoryName(ruta)))
            {
                string n = Path.GetFileName(otro);
                if (File.Exists(otro) && Tipos.EsAcompanante(Py.Lower(Py.Sufijo(n))) && Py.Lower(n).StartsWith(prefijo, StringComparison.Ordinal))
                    archivos.Add(otro);
            }
            string papelera = Path.Combine(RaizDe(ruta).Ruta, ".papelera"); // en su mismo disco: moverla es instantáneo
            Directory.CreateDirectory(papelera);
            string marca = DateTime.Now.ToString("yyyyMMdd-HHmmss", Py.Inv);
            try
            {
                foreach (string archivo in archivos)
                    Nativo.Reemplazar(archivo, Path.Combine(papelera, marca + " " + Path.GetFileName(archivo)));
            }
            catch (ErrorMover e)
            {
                if (e.EsPermiso) throw new ErrorPeticion("Alguien está viendo esa película ahora mismo. Inténtalo cuando termine.", 409);
                throw;
            }
            return archivos.Count;
        }

        // ── textos ───────────────────────────────────────────────────────

        // \s de Python incluye los separadores \x1c-\x1f, que .NET no cuenta como espacio.
        static readonly Regex Espacios = new Regex(@"[\s\x1c-\x1f]+");
        static readonly Regex Anio = new Regex(@"^(.+)[\s\x1c-\x1f][(\[]?((?:19|20)\d{2})[)\]]?(?:[\s\x1c-\x1f].*)?$", RegexOptions.Singleline);

        /// <summary>'The.Matrix.1999.1080p.BluRay' -> 'The Matrix (1999)'.</summary>
        public static string TituloLegible(string nombre)
        {
            string texto = nombre.Replace("_", " ");
            if (texto.IndexOf(' ') < 0) texto = texto.Replace(".", " ");
            texto = Py.Strip(Espacios.Replace(texto, " "));
            var m = Anio.Match(texto);
            if (m.Success)
            {
                string titulo = Py.Strip(m.Groups[1].Value, " -([");
                if (titulo.Length > 0) return titulo + " (" + m.Groups[2].Value + ")";
            }
            return texto;
        }

        /// <summary>'es.forced' -> 'Español (forzados)', idioma 'es'.</summary>
        public static string EtiquetaSubtitulo(string medio, out string idioma)
        {
            var partes = medio.Split('.').Where(p => p.Length > 0).ToList();
            idioma = "";
            if (partes.Count == 0) return "Subtítulos";
            var nombres = partes.Select(p => { string n; return Tipos.Idiomas.TryGetValue(Py.Lower(p), out n) ? n : p; }).ToList();
            string primero = Py.Lower(partes[0]);
            string codigo = Tipos.Idiomas.ContainsKey(primero) && primero != "forced" ? primero : "";
            idioma = Py.Recortar(codigo, 2);
            return nombres[0] + (nombres.Count > 1 ? " (" + string.Join(", ", nombres.Skip(1)) + ")" : "");
        }

        // cp1252: los bytes 0x80-0x9F. Los cinco sin definir (0x81, 0x8D, 0x8F, 0x90, 0x9D)
        // hacen fallar la descodificación en Python, que entonces prueba latin-1.
        static readonly string Cp1252Alto =
            "€￿‚ƒ„…†‡ˆ‰Š‹Œ￿Ž￿" +
            "￿‘’“”•–—˜™š›œ￿žŸ";

        static readonly Regex TiempoSrt = new Regex(@"(\d+):(\d{2}:\d{2}),(\d{3})");

        /// <summary>Los navegadores solo entienden WebVTT: las .srt se convierten al vuelo.</summary>
        public static byte[] SubtituloVtt(string ruta)
        {
            byte[] crudo = File.ReadAllBytes(ruta);
            string texto = null;
            if (crudo.Length >= 2 && ((crudo[0] == 0xFF && crudo[1] == 0xFE) || (crudo[0] == 0xFE && crudo[1] == 0xFF)))
            {
                var utf16 = new UnicodeEncoding(crudo[0] == 0xFE, false, true);
                texto = utf16.GetString(crudo, 2, crudo.Length - 2);
            }
            else
            {
                try
                {
                    int salto = (crudo.Length >= 3 && crudo[0] == 0xEF && crudo[1] == 0xBB && crudo[2] == 0xBF) ? 3 : 0;
                    texto = Py.Utf8Estricto.GetString(crudo, salto, crudo.Length - salto);
                }
                catch (ArgumentException) { }
                if (texto == null)
                {
                    var sb = new StringBuilder(crudo.Length);
                    bool valido = true;
                    foreach (byte b in crudo)
                    {
                        char c = (b >= 0x80 && b <= 0x9F) ? Cp1252Alto[b - 0x80] : (char)b;
                        if (c == '￿') { valido = false; break; }
                        sb.Append(c);
                    }
                    texto = valido ? sb.ToString() : Py.Latin1.GetString(crudo);
                }
            }
            texto = texto.Replace("\r\n", "\n").Replace("\r", "\n");
            if (Py.Lower(Py.Sufijo(Path.GetFileName(ruta))) == ".srt")
            {
                texto = TiempoSrt.Replace(texto, m =>
                {
                    string horas = Py.DigitosAscii(m.Groups[1].Value);
                    if (horas.Length < 2) horas = "0" + horas;
                    return horas + ":" + m.Groups[2].Value + "." + m.Groups[3].Value;
                });
                texto = "WEBVTT\n\n" + Py.LStrip(texto);
            }
            return Py.Utf8.GetBytes(texto);
        }
    }
}
