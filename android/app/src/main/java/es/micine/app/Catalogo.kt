package es.micine.app

import org.json.JSONObject
import java.util.Locale

// Todo lo de este archivo es lógica pura (sin Android): se prueba con JUnit en app/src/test.

/** Una sección del catálogo («Películas», «Series», «Anime», «MiCine»), en el orden en que la manda el servidor. */
data class Seccion(val id: String, val titulo: String)

/** Lo que MiCine sabe de una película o de una serie (identificada con TMDB o solo por el nombre del archivo). */
data class Obra(
    val id: String,
    val tipo: String,
    val titulo: String,
    val anio: Int?,
    val sinopsis: String?,
    val anime: Boolean,
    val seccion: String,
    val identificada: Boolean,
    val poster: String?,
) {
    val esSerie: Boolean get() = tipo == "serie"

    companion object {
        fun desdeJson(id: String, o: JSONObject) = Obra(
            id = o.textoONulo("id") ?: id,
            tipo = o.textoONulo("tipo") ?: "pelicula",
            titulo = o.textoONulo("titulo").orEmpty(),
            anio = o.enteroONulo("anio"),
            sinopsis = o.textoONulo("sinopsis")?.takeIf { it.isNotBlank() },
            anime = o.optBoolean("anime"),
            seccion = o.textoONulo("seccion") ?: "peliculas",
            identificada = o.optBoolean("identificada"),
            poster = o.textoONulo("poster"),
        )
    }
}

// ---------- versiones ----------

/**
 * Calidad de una versión para elegir la mejor: 4K > 1080p > 720p. Sin calidad en el nombre cuenta como
 * 1080p (así suelen llamarse las series bien ordenadas, p. ej. las de Sonarr).
 */
fun rangoCalidad(version: String?): Int = when (version?.trim()?.lowercase(Locale.ROOT)) {
    "4k", "2160p" -> 3
    "1080p", null, "" -> 2
    "720p" -> 1
    else -> 0
}

/** La mejor versión primero; a igual calidad, el archivo más grande (más calidad de imagen). */
val ordenVersiones: Comparator<Pelicula> =
    compareByDescending<Pelicula> { rangoCalidad(it.version) }.thenByDescending { it.tamano }.thenBy { it.ruta }

fun mejorVersion(versiones: List<Pelicula>): Pelicula? = versiones.minWithOrNull(ordenVersiones)

/**
 * La versión que se pone al tocar: la que se está viendo (para seguir donde se dejó); si no, una que ya
 * esté descargada en el aparato (va sin red); y si no, la de mejor calidad.
 */
fun versionPorDefecto(versiones: List<Pelicula>, descargadas: Set<String> = emptySet()): Pelicula? {
    versiones.filter { it.progreso?.enCurso == true }.maxByOrNull { it.progreso!!.fecha }?.let { return it }
    versiones.filter { it.ruta in descargadas }.minWithOrNull(ordenVersiones)?.let { return it }
    return mejorVersion(versiones)
}

/** «4K», «1080p»… o «Estándar» si el archivo no dice la calidad. */
fun nombreVersion(version: String?): String = version ?: "Estándar"

fun etiquetaEpisodio(temporada: Int, episodio: Int): String = "T$temporada·E$episodio"

// ---------- series ----------

/** Un episodio con todas sus versiones (la mejor primero). [numero] 0 = el archivo no dice qué episodio es. */
data class Episodio(val temporada: Int, val numero: Int, val versiones: List<Pelicula>) {
    val nombre: String? get() = versiones.firstNotNullOfOrNull { it.nombreEpisodio }

    /** Lo último que se vio de cualquiera de sus versiones. */
    val progreso: Progreso? get() = versiones.mapNotNull { it.progreso }.maxByOrNull { it.fecha }

    val poster: String? get() = versiones.firstNotNullOfOrNull { it.poster }

    /** «T1·E3» (o «Especial» si no tiene número). */
    val etiqueta: String get() = if (numero > 0) etiquetaEpisodio(temporada, numero) else "Especial"

    /** «T1·E3 · El gato está en la bolsa» */
    val titulo: String
        get() = listOfNotNull(etiqueta, nombre ?: versiones.first().titulo.takeIf { numero <= 0 }).joinToString(" · ")
}

data class Temporada(val numero: Int, val episodios: List<Episodio>) {
    val nombre: String get() = if (numero == 0) "Especiales" else "Temporada $numero"
}

/** Las temporadas en orden (los especiales, temporada 0, al final) y sus episodios en orden. */
fun agruparEpisodios(videos: List<Pelicula>): List<Temporada> {
    val grupos = LinkedHashMap<String, MutableList<Pelicula>>()
    for (v in videos) {
        val clave = if (v.episodio != null) "${temporadaDe(v)}|${v.episodio}" else "${temporadaDe(v)}|${v.ruta}"
        grupos.getOrPut(clave) { mutableListOf() }.add(v)
    }
    val episodios = grupos.values.map { lista ->
        val v = lista.first()
        Episodio(temporadaDe(v), v.episodio ?: 0, lista.sortedWith(ordenVersiones))
    }
    return episodios.groupBy { it.temporada }
        .toSortedMap(compareBy { ordenTemporada(it) })
        .map { (numero, lista) -> Temporada(numero, lista.sortedWith(compareBy<Episodio>({ it.numero }, { it.versiones.first().ruta }))) }
}

private fun temporadaDe(v: Pelicula): Int = v.temporada ?: if (v.episodio != null) 1 else 0

private fun ordenTemporada(numero: Int): Int = if (numero == 0) Int.MAX_VALUE else numero

/** Una película (con todas sus versiones) o una serie entera: lo que va en una tarjeta del catálogo. */
class Ficha(val obra: Obra, videos: List<Pelicula>) {
    /** Película: sus versiones, la mejor primero. Serie: todos sus vídeos. */
    val videos: List<Pelicula> = videos.sortedWith(ordenVersiones)

    val esSerie: Boolean get() = obra.esSerie

    val temporadas: List<Temporada> by lazy { agruparEpisodios(this.videos) }

    val episodios: List<Episodio> by lazy { temporadas.flatMap { it.episodios } }

    /** Cuándo se añadió lo más nuevo (para «Añadidas recientemente»). */
    val fecha: Long = videos.maxOfOrNull { it.fecha } ?: 0

    val poster: String?
        get() = obra.poster ?: if (esSerie) episodios.firstNotNullOfOrNull { it.poster } else videos.firstNotNullOfOrNull { it.poster }

    val tituloOrden: String by lazy { normalizar(obra.titulo).trim() }

    val textoBusqueda: String by lazy {
        normalizar(obra.titulo + " " + videos.joinToString(" ") { "${it.titulo} ${it.nombreEpisodio.orEmpty()} ${it.ruta}" })
    }
}

/** El episodio por el que seguir una serie: el que está a medias o, si el último que se vio se terminó, el siguiente sin ver. */
fun Ficha.episodioParaSeguir(): Episodio? {
    val lista = episodios
    val ultimo = lista.indices
        .filter { lista[it].progreso?.tocado == true }
        .maxWithOrNull(compareBy<Int>({ lista[it].progreso!!.fecha }, { it }))
        ?: return null
    if (lista[ultimo].progreso!!.enCurso) return lista[ultimo]
    return lista.drop(ultimo + 1).firstOrNull { it.progreso?.vista != true }
}

/** Lo que propone el botón grande de la ficha de una serie: seguir, el siguiente sin ver o el primero. */
fun Ficha.episodioPrincipal(): Episodio? = episodioParaSeguir() ?: episodios.firstOrNull()

/** Una serie con todos sus episodios vistos (o una película vista). */
val Ficha.vista: Boolean
    get() = if (esSerie) episodios.isNotEmpty() && episodios.all { it.progreso?.vista == true }
    else videos.any { it.progreso?.vista == true }

// ---------- catálogo ----------

/**
 * Todo lo que manda `GET /api/peliculas`. Los servidores antiguos solo mandan la lista de películas
 * ([agrupado] = false): entonces se enseña la lista plana de siempre.
 */
class Catalogo(
    val peliculas: List<Pelicula>,
    val obras: Map<String, Obra> = emptyMap(),
    val secciones: List<Seccion> = emptyList(),
) {
    val agrupado: Boolean get() = secciones.isNotEmpty()

    val fichas: List<Ficha> by lazy {
        val grupos = LinkedHashMap<String, MutableList<Pelicula>>()
        for (p in peliculas) grupos.getOrPut(idObra(p)) { mutableListOf() }.add(p)
        grupos.map { (id, videos) -> Ficha(obras[id] ?: obraSuelta(id, videos), videos) }
    }

    private val fichaPorId: Map<String, Ficha> by lazy { fichas.associateBy { it.obra.id } }
    private val fichaPorRuta: Map<String, Ficha> by lazy { fichas.flatMap { f -> f.videos.map { it.ruta to f } }.toMap() }
    private val peliculaPorRuta: Map<String, Pelicula> by lazy { peliculas.associateBy { it.ruta } }

    fun ficha(idObra: String): Ficha? = fichaPorId[idObra]

    fun fichaDe(ruta: String): Ficha? = fichaPorRuta[ruta]

    fun pelicula(ruta: String): Pelicula? = peliculaPorRuta[ruta]

    /** [seccion] null: todas. */
    fun fichasDe(seccion: String?): List<Ficha> = if (seccion == null) fichas else fichas.filter { it.obra.seccion == seccion }

    fun conPeliculas(transformar: (Pelicula) -> Pelicula) = Catalogo(peliculas.map(transformar), obras, secciones)

    /** Si el servidor no dijo la obra (o no la mandó), cada vídeo va con los de su grupo o él solo. */
    private fun idObra(p: Pelicula): String = p.obra ?: p.grupo?.let { "grupo:$it" } ?: "ruta:${p.ruta}"

    private fun obraSuelta(id: String, videos: List<Pelicula>): Obra {
        val v = videos.first()
        val serie = videos.any { it.esEpisodio }
        return Obra(
            id = id,
            tipo = if (serie) "serie" else "pelicula",
            titulo = if (serie) v.titulo.substringBefore(" · ") else v.titulo,
            anio = null,
            sinopsis = null,
            anime = false,
            seccion = v.seccion ?: if (serie) "series" else "peliculas",
            identificada = false,
            poster = null,
        )
    }

    companion object {
        fun desdeJson(o: JSONObject): Catalogo {
            val peliculas = o.optJSONArray("peliculas").objetos().map(Pelicula::desdeJson)
            val obras = o.optJSONObject("obras")?.let { json ->
                json.keys().asSequence().mapNotNull { id -> json.optJSONObject(id)?.let { id to Obra.desdeJson(id, it) } }.toMap()
            }.orEmpty()
            val secciones = o.optJSONArray("secciones").objetos().mapNotNull { s ->
                s.textoONulo("id")?.let { Seccion(it, s.textoONulo("titulo") ?: it) }
            }
            return Catalogo(peliculas, obras, secciones)
        }
    }
}

/** Una tarjeta de «Seguir viendo»: el vídeo exacto que se pone (y el episodio, si es una serie). */
data class Continuacion(val ficha: Ficha, val video: Pelicula, val episodio: Episodio?, val fecha: Long)

/** Películas a medias y una tarjeta por serie (con el episodio a medias o el siguiente), lo último primero. */
fun Catalogo.seguirViendo(descargadas: Set<String> = emptySet()): List<Continuacion> =
    fichas.mapNotNull { f ->
        if (f.esSerie) {
            val episodio = f.episodioParaSeguir() ?: return@mapNotNull null
            val video = versionPorDefecto(episodio.versiones, descargadas) ?: return@mapNotNull null
            val fecha = f.episodios.mapNotNull { e -> e.progreso?.takeIf { it.tocado }?.fecha }.maxOrNull() ?: 0
            Continuacion(f, video, episodio, fecha)
        } else {
            val video = f.videos.filter { it.progreso?.enCurso == true }.maxByOrNull { it.progreso!!.fecha }
                ?: return@mapNotNull null
            Continuacion(f, video, null, video.progreso!!.fecha)
        }
    }.sortedByDescending { it.fecha }

/**
 * El episodio que va después de [ruta] en su serie, con la versión que conviene (la misma calidad
 * que se estaba viendo, si existe). Null si es el último (los especiales no cuentan como siguiente).
 */
fun Catalogo.siguienteEpisodio(ruta: String, descargadas: Set<String> = emptySet()): Continuacion? {
    val ficha = fichaDe(ruta)?.takeIf { it.esSerie } ?: return null
    val lista = ficha.episodios
    val i = lista.indexOfFirst { e -> e.versiones.any { it.ruta == ruta } }
    if (i < 0) return null
    val siguiente = lista.getOrNull(i + 1) ?: return null
    if (siguiente.temporada == 0 && lista[i].temporada != 0) return null
    val actual = lista[i].versiones.first { it.ruta == ruta }
    val mismaCalidad = siguiente.versiones.filter { it.version == actual.version }
    val video = versionPorDefecto(mismaCalidad.ifEmpty { siguiente.versiones }, descargadas) ?: return null
    return Continuacion(ficha, video, siguiente, 0)
}

/** Sin catálogo (sin conexión): el siguiente episodio de la misma serie entre las descargas que se le pasan. */
fun siguienteDescarga(descargas: List<Descarga>, ruta: String): Descarga? {
    val actual = descargas.firstOrNull { it.ruta == ruta } ?: return null
    val obra = actual.obra ?: return null
    val t = actual.temporada ?: return null
    val e = actual.episodio ?: return null
    val despues = descargas.filter { d ->
        val dt = d.temporada
        val de = d.episodio
        d.obra == obra && dt != null && de != null && (dt != 0 || t == 0) &&
            (ordenTemporada(dt) > ordenTemporada(t) || (dt == t && de > e))
    }
    val primera = despues.minWithOrNull(compareBy<Descarga>({ ordenTemporada(it.temporada!!) }, { it.episodio!! })) ?: return null
    val mismas = despues.filter { it.temporada == primera.temporada && it.episodio == primera.episodio }
    return mismas.firstOrNull { it.version == actual.version } ?: mismas.maxBy { rangoCalidad(it.version) }
}

// ---------- tarjetas y filas de la pantalla principal ----------

/** Lo que se ve en una carátula de las filas o de la cuadrícula. */
data class Tarjeta(
    /** Fija para cada tarjeta: así el mando no pierde su sitio al recargar. */
    val clave: String,
    val titulo: String,
    val detalle: String,
    val poster: String?,
    /** Barra de progreso (0..1) de lo que se pone al tocarla; null sin barra. */
    val avance: Float?,
    val vista: Boolean,
    /** Lo que se reproduce al tocarla; null en las series (se abre su ficha). */
    val video: Pelicula?,
    /** Entre qué versiones se elige al descargar (las de la película o del episodio de la tarjeta). */
    val versiones: List<Pelicula>,
    /** Obra a la que pertenece; null en la lista plana de los servidores antiguos. */
    val obra: String?,
)

data class Fila(val clave: String, val titulo: String, val tarjetas: List<Tarjeta>)

/** Una sección: filas arriba y debajo todo de la A a la Z. */
data class Pagina(val filas: List<Fila>, val tituloTodas: String, val todas: List<Tarjeta>)

private const val TOPE_FILA = 40

private fun avanceDe(p: Progreso?): Float? = p?.takeIf { !it.vista && it.fraccion > 0.01f }?.fraccion

private val recientes: Comparator<Ficha> = compareByDescending<Ficha> { it.fecha }.thenBy { it.tituloOrden }
private val alfabetico: Comparator<Ficha> = compareBy<Ficha> { it.tituloOrden }.thenBy { it.obra.anio ?: 0 }

/** Lista plana (servidores antiguos): una tarjeta por vídeo, como siempre. */
fun tarjetaPlana(p: Pelicula) = Tarjeta(
    clave = "ruta:${p.ruta}",
    titulo = p.titulo,
    detalle = listOf(p.carpeta, formatoTamano(p.tamano)).filter { it.isNotEmpty() }.joinToString(" · "),
    poster = p.poster,
    avance = avanceDe(p.progreso),
    vista = p.progreso?.vista == true,
    video = p,
    versiones = listOf(p),
    obra = null,
)

fun detalleFicha(f: Ficha, video: Pelicula? = null): String = if (f.esSerie) {
    val temporadas = f.temporadas.count { it.numero > 0 }
    val episodios = f.episodios.size
    listOfNotNull(
        f.obra.anio?.toString(),
        if (temporadas > 1) "$temporadas temporadas" else if (episodios == 1) "1 episodio" else "$episodios episodios",
    ).joinToString(" · ")
} else {
    val v = video ?: f.videos.first()
    listOfNotNull(
        f.obra.anio?.toString(),
        if (f.videos.size > 1) "${f.videos.size} versiones" else v.version,
        formatoTamano(v.tamano),
    ).joinToString(" · ")
}

fun tarjetaDe(f: Ficha, descargadas: Set<String> = emptySet()): Tarjeta {
    if (f.esSerie) {
        return Tarjeta("obra:${f.obra.id}", f.obra.titulo, detalleFicha(f), f.poster, null, f.vista, null, emptyList(), f.obra.id)
    }
    val video = versionPorDefecto(f.videos, descargadas) ?: f.videos.first()
    return Tarjeta(
        clave = "obra:${f.obra.id}",
        titulo = f.obra.titulo,
        detalle = detalleFicha(f, video),
        poster = f.poster ?: video.poster,
        avance = avanceDe(video.progreso),
        vista = video.progreso?.vista == true,
        video = video,
        versiones = f.videos,
        obra = f.obra.id,
    )
}

fun tarjetaDe(c: Continuacion): Tarjeta = Tarjeta(
    clave = "seguir:${c.ficha.obra.id}",
    titulo = c.ficha.obra.titulo,
    detalle = c.episodio?.titulo ?: detalleFicha(c.ficha, c.video),
    poster = c.ficha.poster ?: c.video.poster,
    avance = avanceDe(c.video.progreso),
    vista = false,
    video = c.video,
    versiones = c.episodio?.versiones ?: c.ficha.videos,
    obra = c.ficha.obra.id,
)

private fun filaSeguir(clave: String, lista: List<Continuacion>): Fila? =
    if (lista.isEmpty()) null else Fila(clave, "Seguir viendo", lista.take(TOPE_FILA).map(::tarjetaDe))

/** Inicio: «Seguir viendo» y una fila por sección (lo más nuevo primero). */
fun Catalogo.filasInicio(descargadas: Set<String> = emptySet()): List<Fila> {
    val filas = mutableListOf<Fila>()
    filaSeguir("seguir", seguirViendo(descargadas))?.let { filas += it }
    for (s in secciones) {
        val fichas = fichasDe(s.id).sortedWith(recientes).take(TOPE_FILA)
        if (fichas.isNotEmpty()) filas += Fila("seccion:${s.id}", s.titulo, fichas.map { tarjetaDe(it, descargadas) })
    }
    return filas
}

/** Una sección: lo que se está viendo de ella, lo último que llegó y todo de la A a la Z. */
fun Catalogo.paginaSeccion(seccion: Seccion, descargadas: Set<String> = emptySet()): Pagina {
    val fichas = fichasDe(seccion.id)
    val filas = mutableListOf<Fila>()
    filaSeguir("seguir:${seccion.id}", seguirViendo(descargadas).filter { it.ficha.obra.seccion == seccion.id })?.let { filas += it }
    if (fichas.size > 12 && fichas.any { it.fecha > 0 }) {
        filas += Fila("recientes:${seccion.id}", "Añadidas recientemente", fichas.sortedWith(recientes).take(20).map { tarjetaDe(it, descargadas) })
    }
    return Pagina(filas, "${seccion.titulo} de la A a la Z", fichas.sortedWith(alfabetico).map { tarjetaDe(it, descargadas) })
}

/** Búsqueda en una sección (o en todo, con [seccion] null). [consulta] ya normalizada. */
fun Catalogo.buscar(consulta: String, seccion: String?, descargadas: Set<String> = emptySet()): List<Tarjeta> =
    fichasDe(seccion).filter { it.textoBusqueda.contains(consulta) }.sortedWith(alfabetico).map { tarjetaDe(it, descargadas) }
