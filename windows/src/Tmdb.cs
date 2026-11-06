using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Text;

namespace MiCine
{
    /// <summary>Respuesta de error de TMDB (401: clave no válida; 404: no existe; 0: sin conexión).</summary>
    public sealed class ErrorTmdb : Exception
    {
        public readonly int Codigo;
        public ErrorTmdb(int codigo, string mensaje) : base(mensaje) { Codigo = codigo; }
    }

    /// <summary>
    /// La base de datos de películas y series de themoviedb.org, con la clave del usuario (gratis).
    /// Solo se le envían títulos y años sacados de los nombres de archivo, siempre en español.
    /// </summary>
    public sealed class Tmdb
    {
        public const string IMAGENES = "https://image.tmdb.org/t/p/w185";
        /// <summary>Los carteles que se guardan para las tarjetas y las fichas (500 px de ancho).</summary>
        public const string CARTELES = "https://image.tmdb.org/t/p/w500";
        readonly string clave, baseUrl;

        /// <summary>
        /// Géneros por su número de TMDB, siempre en español (en español, TMDB deja en inglés algunos de
        /// series, como «Action &amp; Adventure»). Los que no estén aquí se toman con el nombre que dé TMDB.
        /// </summary>
        static readonly Dictionary<long, string> Generos = new Dictionary<long, string>
        {
            { 28, "Acción" }, { 12, "Aventura" }, { 16, "Animación" }, { 35, "Comedia" }, { 80, "Crimen" },
            { 99, "Documental" }, { 18, "Drama" }, { 10751, "Familia" }, { 14, "Fantasía" }, { 36, "Historia" },
            { 27, "Terror" }, { 10402, "Música" }, { 9648, "Misterio" }, { 10749, "Romance" }, { 878, "Ciencia ficción" },
            { 10770, "Película de TV" }, { 53, "Suspense" }, { 10752, "Bélica" }, { 37, "Western" },
            { 10759, "Acción y aventura" }, { 10762, "Infantil" }, { 10763, "Noticias" }, { 10764, "Reality" },
            { 10765, "Ciencia ficción y fantasía" }, { 10766, "Telenovela" }, { 10767, "Entrevistas" }, { 10768, "Bélica y política" },
        };

        static Tmdb()
        {
            // .NET 4.8 no siempre ofrece TLS 1.2 por defecto, y TMDB lo exige.
            try { ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12; } catch (Exception) { }
        }

        public Tmdb(string clave)
        {
            this.clave = clave.Trim();
            baseUrl = (Environment.GetEnvironmentVariable("MICINE_TMDB_URL") ?? "https://api.themoviedb.org/3").TrimEnd('/');
        }

        /// <summary>El «token de lectura» (v4) va en la cabecera; la clave corta (v3), en la dirección.</summary>
        bool EsToken { get { return clave.Contains(".") && clave.Length > 100; } }

        public Objeto Pedir(string ruta, params string[] parametros)
        {
            var url = new StringBuilder(baseUrl).Append(ruta).Append("?language=es-ES");
            for (int i = 0; i + 1 < parametros.Length; i += 2)
                url.Append('&').Append(parametros[i]).Append('=').Append(Uri.EscapeDataString(parametros[i + 1]));
            if (!EsToken) url.Append("&api_key=").Append(Uri.EscapeDataString(clave));
            var peticion = (HttpWebRequest)WebRequest.Create(url.ToString());
            peticion.Timeout = 15000;
            peticion.ReadWriteTimeout = 15000;
            peticion.Accept = "application/json";
            peticion.UserAgent = "MiCine/1.3";
            if (EsToken) peticion.Headers["Authorization"] = "Bearer " + clave;
            try
            {
                using (var respuesta = (HttpWebResponse)peticion.GetResponse())
                using (var flujo = respuesta.GetResponseStream())
                using (var lector = new StreamReader(flujo, Encoding.UTF8))
                {
                    var o = Json.Leer(lector.ReadToEnd()) as Objeto;
                    if (o == null) throw new ErrorTmdb(0, "Respuesta de TMDB no válida");
                    return o;
                }
            }
            catch (WebException e)
            {
                var r = e.Response as HttpWebResponse;
                if (r != null)
                {
                    int codigo = (int)r.StatusCode;
                    r.Close();
                    throw new ErrorTmdb(codigo, codigo == 401 ? "La clave de TMDB no es válida" : "TMDB respondió " + codigo);
                }
                throw new ErrorTmdb(0, "Sin conexión con TMDB");
            }
            catch (ErrorJson)
            {
                throw new ErrorTmdb(0, "Respuesta de TMDB no válida");
            }
        }

        /// <summary>Resultados de búsqueda (película o serie); con año y, si no sale nada, sin él.</summary>
        public List<Objeto> Buscar(bool serie, string titulo, int? anio)
        {
            string ruta = serie ? "/search/tv" : "/search/movie";
            List<Objeto> r = null;
            if (anio != null)
                r = Resultados(Pedir(ruta, "query", titulo, "include_adult", "false", serie ? "first_air_date_year" : "year", anio.Value.ToString(Py.Inv)));
            if (r == null || r.Count == 0) r = Resultados(Pedir(ruta, "query", titulo, "include_adult", "false"));
            return r;
        }

        static List<Objeto> Resultados(Objeto o)
        {
            var lista = new List<Objeto>();
            var r = o.Obtener("results", null) as List<object>;
            if (r != null) foreach (object x in r) { var y = x as Objeto; if (y != null) lista.Add(y); }
            return lista;
        }

        /// <summary>Lo que MiCine guarda de una obra: título y año en español, sinopsis y si es anime.</summary>
        public Objeto Detalle(string obra)
        {
            bool serie = obra.StartsWith("tv:", StringComparison.Ordinal);
            string id = obra.Substring(obra.IndexOf(':') + 1);
            Objeto d = Pedir((serie ? "/tv/" : "/movie/") + id);
            return Resumen(d, serie);
        }

        public static Objeto Resumen(Objeto d, bool serie)
        {
            string titulo = Py.Str(d.Obtener(serie ? "name" : "title", "") ?? "");
            string fecha = Py.Str(d.Obtener(serie ? "first_air_date" : "release_date", "") ?? "");
            object anio = null;
            long a;
            if (fecha.Length >= 4 && Py.Entero(fecha.Substring(0, 4), out a)) anio = a;
            string sinopsis = Py.Str(d.Obtener("overview", "") ?? "");
            bool animacion = false;
            var nombres = new List<object>();
            var generos = d.Obtener("genres", null) as List<object>;
            if (generos != null)
                foreach (object g in generos)
                {
                    var go = g as Objeto;
                    if (go == null || !Py.EsNumero(go["id"])) continue;
                    long n = (long)Py.ANumero(go["id"]);
                    if (n == 16) animacion = true;
                    AnadirGenero(nombres, n, go["name"] as string);
                }
            var ids = d.Obtener("genre_ids", null) as List<object>; // en los resultados de búsqueda
            if (ids != null)
                foreach (object g in ids)
                {
                    if (!Py.EsNumero(g)) continue;
                    long n = (long)Py.ANumero(g);
                    if (n == 16) animacion = true;
                    AnadirGenero(nombres, n, null);
                }
            bool anime = animacion && Py.Str(d.Obtener("original_language", "") ?? "") == "ja";
            string cartel = d.Obtener("poster_path", null) as string;
            if (cartel != null && !System.Text.RegularExpressions.Regex.IsMatch(cartel, @"\A/[\w.-]{1,100}\z")) cartel = null;
            return new Objeto
            {
                { "tipo", serie ? "serie" : "pelicula" }, { "titulo", titulo }, { "anio", anio },
                { "sinopsis", sinopsis != "" ? (object)sinopsis : null }, { "anime", anime },
                { "generos", nombres }, { "cartel", cartel },
            };
        }

        static void AnadirGenero(List<object> lista, long id, string nombreTmdb)
        {
            string nombre;
            if (!Generos.TryGetValue(id, out nombre)) nombre = nombreTmdb != null ? Py.Strip(nombreTmdb) : null;
            if (!string.IsNullOrEmpty(nombre) && !lista.Contains(nombre)) lista.Add(nombre);
        }

        /// <summary>Descarga el cartel de una obra (ruta tipo «/abc.jpg»). Las imágenes de TMDB no piden clave.</summary>
        public static byte[] DescargarCartel(string ruta)
        {
            string url = (Environment.GetEnvironmentVariable("MICINE_TMDB_IMAGENES") ?? CARTELES).TrimEnd('/') + ruta;
            var peticion = (HttpWebRequest)WebRequest.Create(url);
            peticion.Timeout = 20000;
            peticion.ReadWriteTimeout = 20000;
            peticion.UserAgent = "MiCine/1.3";
            try
            {
                using (var respuesta = (HttpWebResponse)peticion.GetResponse())
                using (var flujo = respuesta.GetResponseStream())
                using (var memoria = new MemoryStream())
                {
                    flujo.CopyTo(memoria);
                    byte[] datos = memoria.ToArray();
                    // Solo JPEG (empieza por FF D8), y sin pasarse de tamaño
                    if (datos.Length < 4 || datos.Length > 5 * 1024 * 1024 || datos[0] != 0xFF || datos[1] != 0xD8)
                        throw new ErrorTmdb(415, "El cartel de TMDB no es una imagen JPEG");
                    return datos;
                }
            }
            catch (WebException e)
            {
                var r = e.Response as HttpWebResponse;
                if (r != null) { int codigo = (int)r.StatusCode; r.Close(); throw new ErrorTmdb(codigo, "TMDB respondió " + codigo); }
                throw new ErrorTmdb(0, "Sin conexión con TMDB");
            }
        }

        /// <summary>Número de episodio → nombre, de una temporada.</summary>
        public Objeto Temporada(string obra, int temporada)
        {
            string id = obra.Substring(obra.IndexOf(':') + 1);
            Objeto d = Pedir("/tv/" + id + "/season/" + temporada.ToString(Py.Inv));
            var nombres = new Objeto();
            var eps = d.Obtener("episodes", null) as List<object>;
            if (eps != null)
                foreach (object e in eps)
                {
                    var eo = e as Objeto;
                    if (eo == null || !Py.EsNumero(eo["episode_number"])) continue;
                    string nombre = Py.Strip(Py.Str(eo.Obtener("name", "") ?? ""));
                    if (nombre != "") nombres[((long)Py.ANumero(eo["episode_number"])).ToString(Py.Inv)] = nombre;
                }
            return nombres;
        }
    }
}
