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
        readonly string clave, baseUrl;

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
            var generos = d.Obtener("genres", null) as List<object>;
            if (generos != null)
                foreach (object g in generos) { var go = g as Objeto; if (go != null && Py.EsNumero(go["id"]) && Py.ANumero(go["id"]) == 16) animacion = true; }
            var ids = d.Obtener("genre_ids", null) as List<object>; // en los resultados de búsqueda
            if (ids != null) foreach (object g in ids) if (Py.EsNumero(g) && Py.ANumero(g) == 16) animacion = true;
            bool anime = animacion && Py.Str(d.Obtener("original_language", "") ?? "") == "ja";
            return new Objeto
            {
                { "tipo", serie ? "serie" : "pelicula" }, { "titulo", titulo }, { "anio", anio },
                { "sinopsis", sinopsis != "" ? (object)sinopsis : null }, { "anime", anime },
            };
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
