using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;

namespace MiCine
{
    /// <summary>
    /// Qué es cada vídeo: película, serie, anime o «sin identificar» (sección MiCine). Por el nombre del
    /// archivo sabe si es episodio y de qué serie; con la clave de TMDB busca la obra (en segundo plano,
    /// de una en una) y lo guarda en datos\tmdb.json, que comparte con servidor.py.
    /// </summary>
    public sealed class Identificacion
    {
        public const string PELICULAS = "peliculas", SERIES = "series", ANIME = "anime", MICINE = "micine";
        public static readonly string[][] Secciones =
        {
            new[] { PELICULAS, "Películas" }, new[] { SERIES, "Series" }, new[] { ANIME, "Anime" }, new[] { MICINE, "MiCine" },
        };
        const int CADA_CUANTO = 10 * 60 * 1000;

        sealed class Busqueda { public string Grupo, Titulo; public bool Serie; public int? Anio; }

        readonly string archivo;
        readonly Func<List<Objeto>> listar;
        readonly object cerrojo = new object();
        readonly AutoResetEvent despertar = new AutoResetEvent(false);
        readonly Dictionary<string, Analisis> analisis = new Dictionary<string, Analisis>(StringComparer.Ordinal);
        Objeto datos;
        Tmdb tmdb;
        Dictionary<string, Busqueda> busquedasPendientes = new Dictionary<string, Busqueda>(StringComparer.Ordinal);
        HashSet<string> detallesPendientes = new HashSet<string>(StringComparer.Ordinal);
        HashSet<string> temporadasPendientes = new HashSet<string>(StringComparer.Ordinal);
        readonly HashSet<string> detallesFallidos = new HashSet<string>(StringComparer.Ordinal); // 404: hasta reiniciar o reintentar
        DateTime ultimoGuardado = DateTime.MinValue;
        volatile bool activo, sinClaveValida, cambios;
        volatile string estado;

        /// <summary>Recuento de la última clasificación, para la ventana.</summary>
        public volatile int Total, Identificados, EnMiCine;

        public Identificacion(string carpetaDatos, Func<List<Objeto>> listar, string clave)
        {
            archivo = Path.Combine(carpetaDatos, "tmdb.json");
            this.listar = listar;
            try { datos = Json.LeerArchivo(archivo, null) as Objeto; }
            catch (Exception e) { Registro.Anotar("datos\\tmdb.json está dañado y se empieza de cero: " + e.Message); }
            if (datos == null) datos = new Objeto { { "version", 1L } };
            foreach (string k in new[] { "busquedas", "obras", "manual" })
                if (datos.ObjetoDe(k) == null) datos[k] = new Objeto();
            CambiarClave(clave);
        }

        public bool ConClave { get { return tmdb != null; } }

        /// <summary>El texto de estado que va en /api/peliculas (el mismo que da servidor.py).</summary>
        public string EstadoParaWeb
        {
            get
            {
                if (tmdb == null) return "Sin clave: clasificado por el nombre del archivo";
                string e = estado;
                if (e != null && e != "Identificando con TMDB…") return e;
                int n = Pendientes;
                if (n > 0) return "Identificando con TMDB: quedan " + n;
                return "Identificadas " + Identificados + " de " + Total + (EnMiCine > 0 ? " · " + EnMiCine + " en MiCine" : "");
            }
        }
        public string Estado { get { return estado; } }
        public int Pendientes { get { lock (cerrojo) return busquedasPendientes.Count + detallesPendientes.Count + temporadasPendientes.Count; } }

        public void CambiarClave(string clave)
        {
            lock (cerrojo) tmdb = string.IsNullOrWhiteSpace(clave) ? null : new Tmdb(clave);
            sinClaveValida = false;
            estado = null;
            despertar.Set();
        }

        public void Arrancar()
        {
            activo = true;
            new Thread(Trabajar) { IsBackground = true, Name = "MiCine: identificar" }.Start();
        }

        public void Parar()
        {
            activo = false;
            despertar.Set();
        }

        Objeto Busquedas { get { return datos.ObjetoDe("busquedas"); } }
        Objeto Obras { get { return datos.ObjetoDe("obras"); } }
        Objeto Manual { get { return datos.ObjetoDe("manual"); } }

        /// <summary>Ruta sin el prefijo de las carpetas extra (.xxxxxxxx/): así la ve el analizador.</summary>
        static string SinPrefijo(string ruta)
        {
            if (ruta.Length > 10 && ruta[0] == '.' && ruta[9] == '/') return ruta.Substring(10);
            return ruta;
        }

        Analisis Analizar(string ruta)
        {
            lock (cerrojo)
            {
                Analisis a;
                if (analisis.TryGetValue(ruta, out a)) return a;
                a = Nombres.Analizar(SinPrefijo(ruta));
                analisis[ruta] = a;
                return a;
            }
        }

        /// <summary>El año más repetido entre los episodios de una serie (empate: el menor).</summary>
        static int? AnioMasRepetido(IEnumerable<int?> anios)
        {
            var cuenta = new SortedDictionary<int, int>();
            foreach (int? a in anios) if (a != null) cuenta[a.Value] = (cuenta.ContainsKey(a.Value) ? cuenta[a.Value] : 0) + 1;
            int? mejor = null; int veces = 0;
            foreach (var kv in cuenta) if (kv.Value > veces) { mejor = kv.Key; veces = kv.Value; }
            return mejor;
        }

        /// <summary>Sin clave de TMDB se clasifica solo por el nombre del archivo (no se usa lo guardado).</summary>
        string ObraDe(string grupo)
        {
            if (tmdb == null) return null;
            string m = Manual[grupo] as string;
            if (m != null) return m;
            var b = Busquedas.ObjetoDe(grupo);
            return b != null ? b["obra"] as string : null;
        }

        /// <summary>
        /// Añade a cada vídeo su sección, obra, temporada, episodio, versión y grupo, y le pone un título
        /// legible; devuelve las obras (sin carátula: la pone quien conoce los enlaces) y las secciones.
        /// También anota lo que falta por buscar en TMDB para el trabajador.
        /// </summary>
        public Objeto Clasificar(List<Objeto> peliculas, out List<object> secciones)
        {
            var obras = new Objeto();
            var aniosSerie = new Dictionary<string, List<int?>>(StringComparer.Ordinal);
            var porGrupo = new Dictionary<string, Busqueda>(StringComparer.Ordinal);
            var analizadas = new List<KeyValuePair<Objeto, Analisis>>();
            foreach (var p in peliculas)
            {
                Analisis a = Analizar((string)p["ruta"]);
                if (a == null) continue;
                analizadas.Add(new KeyValuePair<Objeto, Analisis>(p, a));
                string g = Nombres.Grupo(a);
                if (!porGrupo.ContainsKey(g)) porGrupo[g] = new Busqueda { Grupo = g, Titulo = a.Titulo, Serie = a.EsEpisodio, Anio = a.Anio };
                if (a.EsEpisodio)
                {
                    List<int?> l;
                    if (!aniosSerie.TryGetValue(g, out l)) aniosSerie[g] = l = new List<int?>();
                    l.Add(a.Anio);
                }
            }
            foreach (var kv in aniosSerie) porGrupo[kv.Key].Anio = AnioMasRepetido(kv.Value);

            var buscar = new Dictionary<string, Busqueda>(StringComparer.Ordinal);
            var detalles = new HashSet<string>(StringComparer.Ordinal);
            var temporadas = new HashSet<string>(StringComparer.Ordinal);
            var presentes = new HashSet<string>(StringComparer.Ordinal);
            int identificados = 0, enMiCine = 0;
            bool conClave;
            lock (cerrojo)
            {
                conClave = tmdb != null;
                foreach (var par in analizadas)
                {
                    Objeto p = par.Key; Analisis a = par.Value;
                    string g = Nombres.Grupo(a);
                    string id = ObraDe(g);
                    Objeto info = id != null ? Obras.ObjetoDe(id) : null;
                    if (conClave && id == null && Manual[g] == null && Busquedas[g] == null) buscar[g] = porGrupo[g];
                    if (id != null && info == null && !detallesFallidos.Contains(id)) detalles.Add(id);
                    string seccion;
                    bool identificada = info != null;
                    if (identificada)
                    {
                        seccion = Py.Verdad(info["anime"]) ? ANIME : id.StartsWith("tv:", StringComparison.Ordinal) ? SERIES : PELICULAS;
                        identificados++;
                    }
                    else
                    {
                        id = Nombres.IdLocal(g);
                        seccion = conClave ? MICINE : a.EsEpisodio ? SERIES : PELICULAS;
                        if (conClave) enMiCine++;
                    }
                    string nombreEp = null;
                    if (a.EsEpisodio && identificada && id.StartsWith("tv:", StringComparison.Ordinal))
                    {
                        string t = a.Temporada.Value.ToString(Py.Inv);
                        var temps = info.ObjetoDe("temporadas");
                        var nombres = temps != null ? temps.ObjetoDe(t) : null;
                        if (nombres == null) temporadas.Add(id + "/" + t);
                        else nombreEp = nombres[a.Episodio.Value.ToString(Py.Inv)] as string;
                    }

                    // La obra: con los datos de TMDB o, si no, con los del nombre del archivo
                    if (!obras.Contiene(id))
                    {
                        Busqueda b = porGrupo[g];
                        obras[id] = new Objeto
                        {
                            { "id", id }, { "tipo", a.EsEpisodio || id.StartsWith("tv:", StringComparison.Ordinal) ? "serie" : "pelicula" },
                            { "titulo", identificada ? info["titulo"] : b.Titulo },
                            { "anio", identificada ? info["anio"] : (b.Anio != null ? (object)(long)b.Anio.Value : null) },
                            { "sinopsis", identificada ? info.Obtener("sinopsis", null) : null },
                            { "anime", identificada && Py.Verdad(info["anime"]) },
                            { "seccion", seccion }, { "identificada", identificada },
                            { "poster", null },
                        };
                        presentes.Add(seccion);
                    }
                    var obra = obras.ObjetoDe(id);
                    string tituloObra = Py.Str(obra["titulo"]);
                    p["seccion"] = seccion;
                    p["obra"] = id;
                    p["grupo"] = g;
                    p["version"] = a.Calidad;
                    if (a.EsEpisodio)
                    {
                        p["temporada"] = (long)a.Temporada.Value;
                        p["episodio"] = (long)a.Episodio.Value;
                        p["nombre_episodio"] = nombreEp;
                        p["titulo"] = tituloObra + " · T" + a.Temporada.Value + "·E" + a.Episodio.Value + (nombreEp != null ? " · " + nombreEp : "");
                    }
                    else
                    {
                        p["temporada"] = null;
                        p["episodio"] = null;
                        p["nombre_episodio"] = null;
                        p["titulo"] = tituloObra + (obra["anio"] != null ? " (" + Py.Str(obra["anio"]) + ")" : "");
                    }
                }
                if (conClave)
                {
                    busquedasPendientes = buscar;
                    detallesPendientes = detalles;
                    temporadasPendientes = temporadas;
                }
            }
            Total = analizadas.Count;
            Identificados = identificados;
            EnMiCine = enMiCine;
            if (conClave && (buscar.Count > 0 || detalles.Count > 0 || temporadas.Count > 0)) despertar.Set();

            secciones = new List<object>();
            foreach (var s in Secciones)
                if (presentes.Contains(s[0])) secciones.Add(new Objeto { { "id", s[0] }, { "titulo", s[1] } });
            return obras;
        }

        /// <summary>Orden de la lista: título de la obra (sin mayúsculas), año, id, temporada, episodio y ruta.</summary>
        public static void Ordenar(List<Objeto> peliculas, Objeto obras)
        {
            Func<Objeto, Objeto> obraDe = p => obras.ObjetoDe((string)p["obra"]) ?? new Objeto();
            var ordenadas = peliculas
                .OrderBy(p => Py.Casefold(Py.Str(obraDe(p)["titulo"] ?? "")), Comparer<string>.Create(Py.Comparar))
                .ThenBy(p => obraDe(p)["anio"] == null ? -1 : Py.ANumero(obraDe(p)["anio"]))
                .ThenBy(p => (string)p["obra"], Comparer<string>.Create(Py.Comparar))
                .ThenBy(p => p["temporada"] == null ? -1 : Py.ANumero(p["temporada"]))
                .ThenBy(p => p["episodio"] == null ? -1 : Py.ANumero(p["episodio"]))
                .ThenBy(p => (string)p["ruta"], Comparer<string>.Create(Py.Comparar))
                .ToList();
            peliculas.Clear();
            peliculas.AddRange(ordenadas);
        }

        /// <summary>Lo que no está en el catálogo (carpetas de Plex) se quita de la lista.</summary>
        public List<Objeto> SinIgnorados(List<Objeto> peliculas)
        {
            return peliculas.Where(p => Analizar((string)p["ruta"]) != null).ToList();
        }

        // ── trabajador ───────────────────────────────────────────────────

        void Trabajar()
        {
            DateTime ultimoListado = DateTime.MinValue;
            while (activo)
            {
                try
                {
                    if ((DateTime.UtcNow - ultimoListado).TotalMilliseconds >= CADA_CUANTO)
                    {
                        ultimoListado = DateTime.UtcNow;
                        List<object> s;
                        try { Clasificar(listar(), out s); } catch (Exception) { }
                    }
                    Tmdb cliente;
                    lock (cerrojo) cliente = tmdb;
                    if (cliente == null || sinClaveValida || Pendientes == 0)
                    {
                        Guardar();
                        despertar.WaitOne(CADA_CUANTO);
                        continue;
                    }
                    estado = "Identificando con TMDB…";
                    Paso(cliente);
                    if (Pendientes == 0) estado = null;
                }
                catch (ErrorTmdb e)
                {
                    Guardar();
                    estado = e.Message;
                    if (e.Codigo == 401) { sinClaveValida = true; Registro.Anotar(e.Message); }
                    else despertar.WaitOne(CADA_CUANTO);
                }
                catch (Exception e)
                {
                    Registro.Error("Identificar con TMDB", e);
                    despertar.WaitOne(CADA_CUANTO);
                }
            }
            Guardar();
        }

        /// <summary>Una petición a TMDB: primero las búsquedas, luego los detalles y al final las temporadas.</summary>
        void Paso(Tmdb cliente)
        {
            // Primero lo que completa obras ya encontradas: así se van viendo identificadas poco a poco
            Busqueda b = null; string detalle = null, temporada = null;
            lock (cerrojo)
            {
                if (detallesPendientes.Count > 0) detalle = detallesPendientes.First();
                else if (temporadasPendientes.Count > 0) temporada = temporadasPendientes.First();
                else if (busquedasPendientes.Count > 0) b = busquedasPendientes.Values.First();
            }
            if (b != null)
            {
                List<Objeto> r;
                try { r = cliente.Buscar(b.Serie, b.Titulo, b.Anio); }
                catch (ErrorTmdb e)
                {
                    // Un 4xx que no sea clave ni límite: se da por no encontrado
                    if (e.Codigo < 400 || e.Codigo >= 500 || e.Codigo == 401 || e.Codigo == 429) throw;
                    r = new List<Objeto>();
                }
                string obra = null;
                if (r.Count > 0 && Py.EsNumero(r[0]["id"]))
                    obra = (b.Serie ? "tv:" : "movie:") + ((long)Py.ANumero(r[0]["id"])).ToString(Py.Inv);
                lock (cerrojo)
                {
                    Busquedas[b.Grupo] = new Objeto { { "obra", obra }, { "fecha", Py.AhoraEntero() } };
                    busquedasPendientes.Remove(b.Grupo);
                    if (obra != null && Obras.ObjetoDe(obra) == null) detallesPendientes.Add(obra);
                }
            }
            else if (detalle != null)
            {
                Objeto d;
                try { d = cliente.Detalle(detalle); }
                catch (ErrorTmdb e)
                {
                    if (e.Codigo != 404) throw;
                    lock (cerrojo) { detallesFallidos.Add(detalle); detallesPendientes.Remove(detalle); }
                    return;
                }
                lock (cerrojo) { Obras[detalle] = d; detallesPendientes.Remove(detalle); }
            }
            else if (temporada != null)
            {
                int barra = temporada.LastIndexOf('/');
                string obra = temporada.Substring(0, barra);
                int n = int.Parse(temporada.Substring(barra + 1), Py.Inv);
                Objeto nombres;
                try { nombres = cliente.Temporada(obra, n); }
                catch (ErrorTmdb e) { if (e.Codigo != 404) throw; nombres = new Objeto(); }
                lock (cerrojo)
                {
                    var o = Obras.ObjetoDe(obra);
                    if (o != null)
                    {
                        var temps = o.ObjetoDe("temporadas");
                        if (temps == null) o["temporadas"] = temps = new Objeto();
                        temps[n.ToString(Py.Inv)] = nombres;
                    }
                    temporadasPendientes.Remove(temporada);
                }
            }
            cambios = true;
            Thread.Sleep(150);
            if (Pendientes == 0 || (DateTime.UtcNow - ultimoGuardado).TotalSeconds >= 5) Guardar();
        }

        void Guardar()
        {
            if (!cambios) return;
            try
            {
                string texto;
                lock (cerrojo) { texto = Json.EscribirSangrado(datos); cambios = false; }
                ultimoGuardado = DateTime.UtcNow;
                string temporal = archivo + ".tmp";
                File.WriteAllBytes(temporal, Py.Utf8.GetBytes(texto));
                Nativo.ReemplazarConReintentos(temporal, archivo);
            }
            catch (Exception e) { Registro.Error("Guardar datos\\tmdb.json", e); }
        }

        // ── a mano (administradores) ─────────────────────────────────────

        public List<object> Buscar(string texto, bool serie)
        {
            Tmdb cliente;
            lock (cerrojo) cliente = tmdb;
            if (cliente == null) throw new ErrorPeticion("Falta la clave de TMDB");
            List<Objeto> r;
            try { r = cliente.Buscar(serie, texto, null); }
            catch (ErrorTmdb e) { throw new ErrorPeticion(e.Message, 502); }
            var lista = new List<object>();
            foreach (var x in r.Take(10))
            {
                if (!Py.EsNumero(x["id"])) continue;
                Objeto res = Tmdb.Resumen(x, serie);
                string imagen = x["poster_path"] as string;
                lista.Add(new Objeto
                {
                    { "obra", (serie ? "tv:" : "movie:") + ((long)Py.ANumero(x["id"])).ToString(Py.Inv) },
                    { "titulo", res["titulo"] }, { "anio", res["anio"] }, { "sinopsis", res["sinopsis"] },
                    { "imagen", imagen != null ? Tmdb.IMAGENES + imagen : null },
                });
            }
            return lista;
        }

        /// <summary>Lo elegido a mano manda sobre la búsqueda automática (null lo quita).</summary>
        public void Asignar(string grupo, string obra)
        {
            if (string.IsNullOrEmpty(grupo)) throw new ErrorPeticion("Falta el grupo");
            if (obra != null && !System.Text.RegularExpressions.Regex.IsMatch(obra, @"\A(?:movie|tv):\d{1,10}\z"))
                throw new ErrorPeticion("Obra no válida");
            Tmdb cliente;
            lock (cerrojo) cliente = tmdb;
            if (obra != null)
            {
                bool falta;
                lock (cerrojo) falta = Obras.ObjetoDe(obra) == null;
                if (falta)
                {
                    if (cliente == null) throw new ErrorPeticion("Falta la clave de TMDB");
                    Objeto d;
                    try { d = cliente.Detalle(obra); }
                    catch (ErrorTmdb e) { throw new ErrorPeticion(e.Message, 502); }
                    lock (cerrojo) Obras[obra] = d;
                }
            }
            lock (cerrojo)
            {
                if (obra == null) Manual.Quitar(grupo);
                else Manual[grupo] = obra;
            }
            cambios = true;
            Guardar();
            despertar.Set();
        }

        /// <summary>«Volver a identificar»: se repiten las búsquedas que no encontraron nada.</summary>
        public void Reintentar()
        {
            lock (cerrojo)
            {
                foreach (string g in Busquedas.Claves)
                {
                    var b = Busquedas.ObjetoDe(g);
                    if (b == null || b["obra"] == null) Busquedas.Quitar(g);
                }
                detallesFallidos.Clear();
            }
            sinClaveValida = false;
            estado = null;
            cambios = true;
            Guardar();
            List<object> s;
            try { Clasificar(listar(), out s); } catch (Exception) { }
            despertar.Set();
        }
    }
}
