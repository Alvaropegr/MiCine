using System;
using System.Collections.Generic;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace MiCine
{
    /// <summary>
    /// Qué es cada archivo por su nombre: película («Alien (1979).mkv», «A.Bugs.Life.1998.2160p…») o
    /// episodio («Serie/Season 01/Serie - S01E02.mkv», «Brothers [HDTV 1080p][Cap.103]»), con título limpio,
    /// año, temporada, episodio y calidad. Tiene que dar exactamente lo mismo que analizar() de servidor.py.
    /// </summary>
    public sealed class Analisis
    {
        public string Tipo;          // "pelicula" o "episodio"
        public string Titulo;
        public int? Anio, Temporada, Episodio;
        public string Calidad;       // "4K", "1080p", "720p" o null

        public bool EsEpisodio { get { return Tipo == "episodio"; } }
    }

    public static class Nombres
    {
        const RegexOptions I = RegexOptions.IgnoreCase | RegexOptions.CultureInvariant;
        const RegexOptions C = RegexOptions.CultureInvariant;

        // Carpetas de temporada: no dan el nombre de la serie, solo el número de temporada.
        static readonly Regex CarpetaTemporada = new Regex(@"^(?:season|temporada|temp\.?|series|serie|t)\s*[._-]?\s*(\d{1,2})$|^specials?$|^especiales$", I);
        // Episodios: S01E02 · 1x02 · Cap.103 · Temporada 1 Capitulo 2 · «Serie - 05 [1080p]» (anime)
        static readonly Regex SxxEyy = new Regex(@"(?<![a-z0-9])s(\d{1,2})[ ._-]?e(\d{1,3})(?![0-9])", I);
        static readonly Regex NxNN = new Regex(@"(?<![a-z0-9])(\d{1,2})x(\d{2,3})(?![0-9a-z])", I);
        static readonly Regex Cap = new Regex(@"cap(?:itulo|ítulo)?[ ._]*(\d{3,4})(?![0-9_])", I);
        static readonly Regex Temp = new Regex(@"(?:temporada|season|temp\.?)\s*[._-]?\s*(\d{1,2})(?![0-9])", I);
        static readonly Regex Episodio = new Regex(@"(?:episodio|episode|cap(?:itulo|ítulo)?|ep|e)[ ._-]*(\d{1,3})(?![0-9])", I);
        static readonly Regex Absoluto = new Regex(@"\s-\s(\d{1,4})(?:v\d)?\s*(?:[\[(]|$)", C);
        static readonly Regex InicioNum = new Regex(@"^(\d{1,3})(?:\s*[-._]\s*|\s+)(?=\D)", C);
        // Lo que viene detrás del título en los nombres de descargas: calidad, origen, audio, idioma…
        const string CORTE = @"(?:2160p?|1080p?|720p?|480p?|576p?|4k|uhd|hdr10?|hdr|dv|hevc|x\.?26[45]|h\.?26[45]|xvid|divx|av1|"
            + @"blu-?ray|bd-?rip|br-?rip|bdremux|remux|dvd-?rip|dvd|dvdscr|hd-?rip|web-?dl|web-?rip|web|hdtv|tvrip|microhd|"
            + @"ac3|eac3|dts(?:-hd)?|truehd|atmos|aac|mp3|dd5\.?1|5\.1|7\.1|dual|multi|castellano|español|espanol|spanish|"
            + @"latino|esp|vose|subs?|subtitulado|proper|repack|internal|limited|extended|unrated|remastered|"
            + @"cap\.?\s*\d{3}|temp\.?\s*\d|temporada|season|completa)";
        static readonly Regex Corte = new Regex(@"(?<![a-z0-9])" + CORTE + @"(?![a-z0-9áéíóúñ])|www\.", I);
        // «DumboM1080», «Panda1BD1080», «Canta 4K2160», «el rey leon 3D1080»: la calidad pegada al título
        static readonly Regex CalidadPegada = new Regex(@"(?:m|bd|bdr|4k|4krip|4kremux|uhdremux|3d)\s?(?:2160|1080|720)p?(?=$|[\s.\[\]_()-])", I);
        static readonly Regex AnioSuelto = new Regex(@"(?<![0-9])(19[2-9]\d|20[0-4]\d)(?![0-9])", C);
        static readonly Regex AnioEntero = new Regex(@"\A\s*(19[2-9]\d|20[0-4]\d)\s*\z", C);
        static readonly Regex Corchetes = new Regex(@"\[[^\]]*\]|\{[^}]*\}", C);
        static readonly Regex SinCerrar = new Regex(@"[\[({][^\])}]*$", C);
        static readonly Regex Parentesis = new Regex(@"\(([^)]*)\)", C);
        static readonly Regex Dominio = new Regex(@"(?:www\.)?[a-z0-9-]+\.(?:com|net|org|link|bz|to|tv|me|info|es)\b", I);
        static readonly Regex Prefijo = new Regex(@"^(?:series?|pel[ií]culas?|peliculas?|saga|duolog[ií]a|trilog[ií]a)\s*[-:]\s*", I);
        static readonly Regex Pegado = new Regex(@"(?<=[a-záéíóúñ])(?=[0-9])|(?<=[0-9])(?=[a-záéíóúñ])", I);
        static readonly Regex Separadores = new Regex(@"[._]+", C);
        static readonly Regex Espacios = new Regex(@"\s+", C);
        static readonly Regex Coma = new Regex(@",(?=\S)", C);
        static readonly Regex Camel = new Regex(@"[a-zñ][A-ZÑ]", C);
        static readonly Regex CamelSerie = new Regex(@"[a-zñ][A-ZÑ].*\d|\d.*[a-zñ][A-ZÑ]", C);
        static readonly Regex CorteTemporada = new Regex(@"\s-\s(?:temp|temporada|season)", I);
        static readonly Regex Q4K = new Regex(@"2160|(?<![a-z0-9])4k|uhd", C);
        static readonly Regex NoAlfanumerico = new Regex(@"[^a-z0-9]+", C);
        static readonly HashSet<string> Ignoradas = new HashSet<string>(StringComparer.Ordinal) { "plex versions" };

        /// <summary>Carpetas cuyo contenido no sale en MiCine (las copias que hace Plex).</summary>
        public static bool CarpetaIgnorada(string nombre) { return Ignoradas.Contains(nombre.ToLowerInvariant()); }

        /// <summary>int() de Python: también con cifras que no son ASCII.</summary>
        static int Num(string s)
        {
            int n = 0;
            foreach (char c in s) n = n * 10 + (int)char.GetNumericValue(c);
            return n;
        }

        static string Calidad(string texto)
        {
            string t = texto.ToLowerInvariant();
            if (Q4K.IsMatch(t)) return "4K";
            if (t.Contains("1080")) return "1080p";
            if (t.Contains("720")) return "720p";
            return null;
        }

        /// <summary>Título limpio y año (si lo hay) de un nombre de archivo o carpeta de descargas.</summary>
        static string Limpiar(string texto, out int? anio)
        {
            string t = Dominio.Replace(texto, " ");
            t = CalidadPegada.Replace(t, " ");
            t = Corchetes.Replace(t, " ");
            t = SinCerrar.Replace(t, " "); // corchete sin cerrar: «Brothers [4k 2160p][» + Cap.101
            anio = null;
            foreach (Match m in Parentesis.Matches(t))
            {
                Match a = AnioEntero.Match(m.Groups[1].Value);
                if (a.Success) anio = Num(a.Groups[1].Value);
            }
            t = Parentesis.Replace(t, " ");
            // Separadores: puntos y guiones bajos cuando hacen de espacio (A.Bugs.Life.1998)
            if ((Contar(t, '.') >= 2 && !t.Trim().Contains(" ")) || t.Contains("_"))
                t = Separadores.Replace(t, " ");
            Match corte = Corte.Match(t);
            if (corte.Success) t = t.Substring(0, corte.Index);
            if (anio == null)
            {
                MatchCollection anios = AnioSuelto.Matches(t);
                if (anios.Count > 0 && anios[anios.Count - 1].Index > 0)
                {
                    Match ultimo = anios[anios.Count - 1];
                    anio = Num(ultimo.Groups[1].Value);
                    t = t.Substring(0, ultimo.Index);
                }
            }
            t = Prefijo.Replace(t.Trim(), "");
            t = Espacios.Replace(t, " ").Trim(' ', '-', '–', '_', '.', ',', ':');
            t = Coma.Replace(t, ", ");
            return t;
        }

        static int Contar(string s, char c)
        {
            int n = 0;
            foreach (char x in s) if (x == c) n++;
            return n;
        }

        /// <summary>Nombres de archivo que no dicen nada (ComoentrenaratuDragon2, libroselva): mejor el de la carpeta.</summary>
        static bool Basura(string titulo)
        {
            if (string.IsNullOrEmpty(titulo) || titulo.Length < 2) return true;
            return !titulo.Contains(" ") && (titulo.Length >= 9 || Camel.IsMatch(titulo));
        }

        /// <summary>Para series vale una sola palabra (Succession, Daredevil); solo se descarta lo ilegible.</summary>
        static bool BasuraSerie(string titulo)
        {
            if (string.IsNullOrEmpty(titulo) || titulo.Length < 2) return true;
            return !titulo.Contains(" ") && (titulo.Length >= 20 || CamelSerie.IsMatch(titulo));
        }

        /// <summary>Temporada (puede ser null), episodio y posición donde empieza la marca de episodio.</summary>
        static bool NumeroEpisodio(string nombre, int? temporadaCarpeta, out int? temporada, out int episodio, out int pos)
        {
            temporada = null; episodio = 0; pos = 0;
            foreach (Regex rx in new[] { SxxEyy, NxNN })
            {
                Match m = rx.Match(nombre);
                if (m.Success) { temporada = Num(m.Groups[1].Value); episodio = Num(m.Groups[2].Value); pos = m.Index; return true; }
            }
            Match c = Cap.Match(nombre);
            if (c.Success)
            {
                string n = c.Groups[1].Value;
                temporada = Num(n.Substring(0, n.Length - 2)); episodio = Num(n.Substring(n.Length - 2)); pos = c.Index;
                return true;
            }
            if (temporadaCarpeta != null)
            {
                Match m = Episodio.Match(nombre);
                if (!m.Success) m = InicioNum.Match(nombre);
                if (m.Success) { temporada = temporadaCarpeta; episodio = Num(m.Groups[1].Value); pos = m.Index; return true; }
            }
            Match abs = Absoluto.Match(nombre);
            if (abs.Success) { episodio = Num(abs.Groups[1].Value); pos = abs.Index; return true; }
            return false;
        }

        /// <summary>
        /// ruta: «carpeta/sub/archivo.mkv», relativa a su carpeta de MiCine y sin el prefijo de carpeta extra
        /// (.xxxxxxxx/). Devuelve null si el archivo está en una carpeta que se ignora.
        /// </summary>
        public static Analisis Analizar(string ruta)
        {
            string[] partes = ruta.Split('/');
            string archivo = partes[partes.Length - 1];
            var carpetas = new List<string>(partes);
            carpetas.RemoveAt(carpetas.Count - 1);
            foreach (string c in carpetas) if (CarpetaIgnorada(c)) return null;
            int punto = archivo.LastIndexOf('.');
            string nombreBase = punto >= 0 ? archivo.Substring(0, punto) : archivo;

            int? temporadaCarpeta = null;
            for (int i = carpetas.Count - 1; i >= 0; i--)
            {
                string c = carpetas[i];
                Match m = CarpetaTemporada.Match(c.Trim());
                if (!m.Success) m = Temp.Match(c);
                if (m.Success && m.Groups[1].Success && m.Groups[1].Value != "") { temporadaCarpeta = Num(m.Groups[1].Value); break; }
            }
            string q = Calidad(ruta);

            int? temporada; int episodio, pos;
            bool esEpisodio = NumeroEpisodio(nombreBase, temporadaCarpeta, out temporada, out episodio, out pos);
            if (!esEpisodio && carpetas.Count > 0)
            {
                // Torrents de capítulos: la carpeta lleva el «Cap.103» y el archivo no
                string ultima = carpetas[carpetas.Count - 1];
                int? t2; int e2, p2;
                if (NumeroEpisodio(ultima, temporadaCarpeta, out t2, out e2, out p2) && Cap.IsMatch(ultima))
                {
                    esEpisodio = true; temporada = t2; episodio = e2; pos = nombreBase.Length;
                }
            }
            if (esEpisodio)
            {
                if (temporada == null && temporadaCarpeta != null) temporada = temporadaCarpeta;
                int? anio;
                string titulo = Limpiar(nombreBase.Substring(0, pos), out anio);
                if (BasuraSerie(titulo))
                {
                    titulo = "";
                    int? anio2 = null;
                    // Carpeta más cercana que no sea «Season 01»
                    for (int i = carpetas.Count - 1; i >= 0; i--)
                    {
                        string c = carpetas[i];
                        if (CarpetaTemporada.IsMatch(c.Trim())) continue;
                        int? a;
                        string t = Limpiar(CorteTemporada.Split(c)[0], out a);
                        if (!BasuraSerie(t)) { titulo = t; anio2 = a; break; }
                    }
                    if (anio == null) anio = anio2;
                }
                if (titulo != "")
                    return new Analisis
                    {
                        Tipo = "episodio", Titulo = titulo, Anio = anio,
                        Temporada = temporada == null || temporada == 0 ? 1 : temporada, Episodio = episodio, Calidad = q,
                    };
            }
            // Película: el nombre del archivo o, si no dice nada, el de su carpeta
            int? anioPeli;
            string tituloPeli = Limpiar(nombreBase, out anioPeli);
            if (Basura(tituloPeli) && carpetas.Count > 0)
            {
                int? a;
                string t = Limpiar(carpetas[carpetas.Count - 1], out a);
                if (!Basura(t)) { tituloPeli = t; if (anioPeli == null) anioPeli = a; }
            }
            if (tituloPeli != "" && !tituloPeli.Contains(" "))
                tituloPeli = Espacios.Replace(Pegado.Replace(tituloPeli, " "), " ").Trim();
            return new Analisis
            {
                Tipo = "pelicula", Titulo = tituloPeli != "" ? tituloPeli : nombreBase, Anio = anioPeli, Calidad = q,
            };
        }

        /// <summary>Minúsculas, sin tildes y solo letras y números separados por un espacio.</summary>
        public static string Normalizar(string texto)
        {
            string t = texto.ToLowerInvariant().Normalize(NormalizationForm.FormD);
            var sb = new StringBuilder(t.Length);
            foreach (char c in t)
                if (CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark) sb.Append(c);
            return NoAlfanumerico.Replace(sb.ToString(), " ").Trim();
        }

        /// <summary>Lo que hace que dos archivos sean «la misma obra» por su nombre.</summary>
        public static string Grupo(Analisis a)
        {
            if (a.EsEpisodio) return "serie|" + Normalizar(a.Titulo);
            return "pelicula|" + Normalizar(a.Titulo) + "|" + (a.Anio != null ? a.Anio.Value.ToString(CultureInfo.InvariantCulture) : "");
        }

        public static string Sha1Hex(string texto)
        {
            using (var sha = SHA1.Create())
            {
                byte[] h = sha.ComputeHash(Encoding.UTF8.GetBytes(texto));
                var sb = new StringBuilder(h.Length * 2);
                foreach (byte b in h) sb.Append(b.ToString("x2"));
                return sb.ToString();
            }
        }

        /// <summary>Id de una obra que (aún) no está identificada en TMDB.</summary>
        public static string IdLocal(string grupo) { return "local:" + Sha1Hex(grupo).Substring(0, 12); }
    }
}
