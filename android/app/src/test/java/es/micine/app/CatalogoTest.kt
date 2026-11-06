package es.micine.app

import org.json.JSONObject
import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertNull
import org.junit.Assert.assertTrue
import org.junit.Test

/** Pruebas de la lógica del catálogo (sin Android): `gradle testReleaseUnitTest`. */
class CatalogoTest {

    // ---------- ayudas ----------

    private fun video(
        ruta: String,
        obra: String? = null,
        t: Int? = null,
        e: Int? = null,
        version: String? = null,
        tamano: Long = 1000,
        progreso: Progreso? = null,
        fecha: Long = 0,
        poster: String? = null,
        nombre: String? = null,
        seccion: String? = null,
        titulo: String = ruta,
    ) = Pelicula(
        ruta = ruta, titulo = titulo, carpeta = "", tamano = tamano, url = "/media/$ruta", poster = poster,
        subtitulos = emptyList(), progreso = progreso, fecha = fecha, seccion = seccion, obra = obra,
        temporada = t, episodio = e, nombreEpisodio = nombre, version = version, grupo = null,
    )

    private fun visto(fecha: Long) = Progreso(100.0, 100.0, fecha)
    private fun aMedias(fecha: Long) = Progreso(50.0, 100.0, fecha)

    private fun obra(id: String, tipo: String, titulo: String = id, seccion: String = if (tipo == "serie") "series" else "peliculas", poster: String? = null, anio: Int? = null) =
        Obra(id, tipo, titulo, anio, null, false, seccion, true, poster)

    private fun catalogo(videos: List<Pelicula>, vararg obras: Obra) = Catalogo(
        videos,
        obras.associateBy { it.id },
        listOf(Seccion("peliculas", "Películas"), Seccion("series", "Series"), Seccion("anime", "Anime"), Seccion("micine", "MiCine")),
    )

    private val nuevoServidor = """
        {
          "peliculas": [
            { "ruta": "Series/Breaking Bad/T1/bb102.mkv", "titulo": "Breaking Bad · T1·E2 · El gato está en la bolsa", "carpeta": "E:\\SERIES",
              "tamano": 123, "fecha": 1762390000, "compat": "si", "subtitulos": [ { "etiqueta": "Español", "url": "/sub/1?u=1" } ],
              "poster": "/caratula/abc.jpg?u=1&e=2&f=3&v=99", "url": "/media/bb102.mkv?u=1", "progreso": { "t": 30, "d": 2800, "fecha": 1762390500 },
              "seccion": "series", "obra": "tv:1396", "temporada": 1, "episodio": 2, "nombre_episodio": "El gato está en la bolsa",
              "version": "1080p", "grupo": "serie|breaking bad" },
            { "ruta": "Pelis/Matrix.mkv", "titulo": "Matrix (1999)", "tamano": 456, "fecha": 1762300000, "subtitulos": [],
              "poster": null, "url": "/media/matrix.mkv?u=1", "progreso": null,
              "seccion": "peliculas", "obra": "movie:603", "temporada": null, "episodio": null, "nombre_episodio": null,
              "version": null, "grupo": "pelicula|matrix|1999" }
          ],
          "obras": {
            "tv:1396": { "id": "tv:1396", "tipo": "serie", "titulo": "Breaking Bad", "anio": 2008, "sinopsis": "Un profesor de química…",
                         "anime": false, "seccion": "series", "identificada": true, "poster": "/caratula/abc.jpg?u=1&e=2&f=3&v=99" },
            "movie:603": { "id": "movie:603", "tipo": "pelicula", "titulo": "Matrix", "anio": 1999, "sinopsis": null,
                           "anime": false, "seccion": "peliculas", "identificada": true, "poster": null }
          },
          "secciones": [ { "id": "peliculas", "titulo": "Películas" }, { "id": "series", "titulo": "Series" } ],
          "identificacion": { "tmdb": true, "pendientes": 0, "estado": null },
          "caratulas": { "pendientes": 0 },
          "conversion": true, "red": null
        }
    """.trimIndent()

    // ---------- lectura del JSON ----------

    @Test
    fun leeLosCamposNuevosDelServidor() {
        val c = Catalogo.desdeJson(JSONObject(nuevoServidor))
        assertTrue(c.agrupado)
        assertEquals(listOf("peliculas", "series"), c.secciones.map { it.id })
        assertEquals("Películas", c.secciones[0].titulo)

        val bb = c.peliculas[0]
        assertEquals("series", bb.seccion)
        assertEquals("tv:1396", bb.obra)
        assertEquals(1, bb.temporada)
        assertEquals(2, bb.episodio)
        assertEquals("El gato está en la bolsa", bb.nombreEpisodio)
        assertEquals("1080p", bb.version)
        assertEquals("serie|breaking bad", bb.grupo)
        assertEquals(1762390000L, bb.fecha)
        assertEquals("E:\\SERIES", bb.carpeta)
        assertEquals(30.0, bb.progreso!!.segundos, 0.0)
        assertTrue(bb.esEpisodio)

        val matrix = c.peliculas[1]
        assertNull(matrix.temporada)
        assertNull(matrix.episodio)
        assertNull(matrix.version)
        assertNull(matrix.nombreEpisodio)
        assertNull(matrix.poster)
        assertFalse(matrix.esEpisodio)

        val serie = c.obras.getValue("tv:1396")
        assertEquals("serie", serie.tipo)
        assertEquals("Breaking Bad", serie.titulo)
        assertEquals(2008, serie.anio)
        assertEquals("Un profesor de química…", serie.sinopsis)
        assertTrue(serie.esSerie)
        assertTrue(serie.identificada)
        assertNull(c.obras.getValue("movie:603").sinopsis)

        assertEquals(2, c.fichas.size)
        assertEquals("Breaking Bad", c.fichaDe("Series/Breaking Bad/T1/bb102.mkv")!!.obra.titulo)
        assertEquals("T1·E2 · El gato está en la bolsa", c.ficha("tv:1396")!!.episodios.single().titulo)
    }

    @Test
    fun servidorAntiguoSigueComoListaPlana() {
        val json = """
            { "peliculas": [
                { "ruta": "a.mkv", "titulo": "Alien", "carpeta": "", "tamano": 10, "url": "/media/a", "poster": null, "subtitulos": [], "progreso": null },
                { "ruta": "b.mkv", "titulo": "Blade Runner", "tamano": 20, "url": "/media/b", "subtitulos": [] }
              ], "libre": 1, "limite": 2 }
        """.trimIndent()
        val c = Catalogo.desdeJson(JSONObject(json))
        assertFalse(c.agrupado)
        assertTrue(c.obras.isEmpty())
        assertTrue(c.secciones.isEmpty())
        assertEquals(2, c.peliculas.size)
        assertNull(c.peliculas[0].obra)
        assertNull(c.peliculas[0].seccion)
        assertEquals(0L, c.peliculas[1].fecha)
        // Sin obra ni grupo cada vídeo va por su cuenta (nada se junta por error).
        assertEquals(2, c.fichas.size)
        val t = tarjetaPlana(c.peliculas[1])
        assertEquals("Blade Runner", t.titulo)
        assertEquals(c.peliculas[1], t.video)
        assertNull(t.obra)
    }

    @Test
    fun aguantaNumerosEnTextoYVaciosEnElJson() {
        val o = JSONObject("""{ "ruta": "x", "titulo": "X", "url": "/u", "temporada": "3", "episodio": 7.0, "version": "", "nombre_episodio": "  " }""")
        val p = Pelicula.desdeJson(o)
        assertEquals(3, p.temporada)
        assertEquals(7, p.episodio)
        assertNull(p.version)
        assertNull(p.nombreEpisodio)
        assertEquals("", p.carpeta)
    }

    @Test
    fun obraQueNoVieneEnObrasSeAgrupaIgual() {
        val c = Catalogo(
            listOf(
                video("s1", obra = "local:abc", t = 1, e = 1, titulo = "Brothers · T1·E1", seccion = "series"),
                video("s2", obra = "local:abc", t = 1, e = 2, titulo = "Brothers · T1·E2", seccion = "series"),
            ),
            emptyMap(),
            listOf(Seccion("series", "Series")),
        )
        val f = c.fichas.single()
        assertTrue(f.esSerie)
        assertEquals("Brothers", f.obra.titulo)
        assertEquals("series", f.obra.seccion)
        assertEquals(2, f.episodios.size)
    }

    @Test
    fun conPeliculasConservaObrasYSecciones() {
        val c = Catalogo.desdeJson(JSONObject(nuevoServidor))
        val otro = c.conPeliculas { it.copy(progreso = visto(5)) }
        assertEquals(c.obras, otro.obras)
        assertEquals(c.secciones, otro.secciones)
        assertTrue(otro.peliculas.all { it.progreso!!.vista })
    }

    // ---------- series: temporadas, episodios y versiones ----------

    @Test
    fun agrupaPorTemporadaYEpisodioEnOrden() {
        val serie = obra("tv:1", "serie", poster = null)
        val c = catalogo(
            listOf(
                video("t2e1", "tv:1", 2, 1, poster = "/p/t2e1"),
                video("t1e3", "tv:1", 1, 3),
                video("t1e1-720", "tv:1", 1, 1, "720p"),
                video("t1e1-4k", "tv:1", 1, 1, "4K", poster = "/p/t1e1"),
                video("t1e2", "tv:1", 1, 2),
                video("especial", "tv:1", 0, 1),
            ),
            serie,
        )
        val f = c.ficha("tv:1")!!
        assertEquals(listOf(1, 2, 0), f.temporadas.map { it.numero }) // los especiales, al final
        assertEquals("Especiales", f.temporadas.last().nombre)
        assertEquals("Temporada 1", f.temporadas.first().nombre)
        assertEquals(listOf("T1·E1", "T1·E2", "T1·E3", "T2·E1", "T0·E1"), f.episodios.map { it.etiqueta })
        val e1 = f.episodios.first()
        assertEquals(listOf("t1e1-4k", "t1e1-720"), e1.versiones.map { it.ruta }) // la mejor primero
        // Sin carátula de la obra: la del episodio más bajo que tenga.
        assertEquals("/p/t1e1", f.poster)
        assertEquals(6, c.peliculas.size)
        assertEquals(f, c.fichaDe("t1e1-720"))
    }

    @Test
    fun laCaratulaDeLaObraMandaSobreLaDeLosEpisodios() {
        val c = catalogo(listOf(video("a", "tv:1", 1, 1, poster = "/p/ep")), obra("tv:1", "serie", poster = "/p/serie"))
        assertEquals("/p/serie", c.ficha("tv:1")!!.poster)
    }

    @Test
    fun episodioSinNumeroNoSeJuntaConOtros() {
        val c = catalogo(
            listOf(video("x1", "tv:1", titulo = "Extra uno"), video("x2", "tv:1", titulo = "Extra dos"), video("e1", "tv:1", 1, 1)),
            obra("tv:1", "serie"),
        )
        val f = c.ficha("tv:1")!!
        assertEquals(3, f.episodios.size)
        assertEquals("T1·E1", f.episodios.first().etiqueta)
        assertEquals("Especial", f.episodios.last().etiqueta)
    }

    @Test
    fun laMejorVersionEs4KLuego1080pLuego720p() {
        val versiones = listOf(
            video("nada", version = null, tamano = 9000),
            video("720", version = "720p"),
            video("4k", version = "4K"),
            video("1080", version = "1080p"),
        )
        assertEquals("4k", mejorVersion(versiones)!!.ruta)
        // Sin calidad en el nombre cuenta como 1080p; a igual calidad gana el archivo más grande
        assertEquals("nada", mejorVersion(versiones.filter { it.ruta != "4k" })!!.ruta)
        assertEquals("nada", mejorVersion(versiones.filter { it.ruta == "720" || it.ruta == "nada" })!!.ruta)
        assertEquals(listOf("4k", "nada", "1080", "720"), versiones.sortedWith(ordenVersiones).map { it.ruta })
        assertNull(mejorVersion(emptyList()))
        assertEquals(3, rangoCalidad("2160p"))
        assertEquals(3, rangoCalidad("4k"))
        assertEquals(2, rangoCalidad(null))
        assertEquals(1, rangoCalidad("720p"))
    }

    @Test
    fun aIgualCalidadGanaElArchivoMasGrande() {
        val versiones = listOf(video("chica", version = "1080p", tamano = 1), video("grande", version = "1080p", tamano = 2))
        assertEquals("grande", mejorVersion(versiones)!!.ruta)
    }

    @Test
    fun porDefectoSeSigueLaQueEstaAMediasYLuegoLaDescargada() {
        val k4 = video("4k", version = "4K")
        val hd = video("hd", version = "1080p")
        val sd = video("sd", version = "720p")
        assertEquals("4k", versionPorDefecto(listOf(sd, hd, k4))!!.ruta)
        assertEquals("sd", versionPorDefecto(listOf(sd, hd, k4), descargadas = setOf("sd"))!!.ruta)
        val hdAMedias = hd.copy(progreso = aMedias(10))
        assertEquals("hd", versionPorDefecto(listOf(sd, hdAMedias, k4), descargadas = setOf("sd"))!!.ruta)
        // Una vista entera no cuenta como «a medias».
        assertEquals("4k", versionPorDefecto(listOf(sd, hd.copy(progreso = visto(10)), k4))!!.ruta)
    }

    // ---------- seguir viendo ----------

    private fun serieDeCinco(vararg progresos: Pair<Int, Progreso>): Catalogo {
        val p = progresos.toMap()
        val videos = (1..3).map { video("t1e$it", "tv:1", 1, it, progreso = p[it]) } +
            (1..2).map { video("t2e$it", "tv:1", 2, it, progreso = p[10 + it]) }
        return catalogo(videos, obra("tv:1", "serie", "Serie"))
    }

    @Test
    fun sinVerNadaNoHayQueSeguirYSeEmpiezaPorElPrimero() {
        val f = serieDeCinco().ficha("tv:1")!!
        assertNull(f.episodioParaSeguir())
        assertEquals("T1·E1", f.episodioPrincipal()!!.etiqueta)
        assertTrue(serieDeCinco().seguirViendo().isEmpty())
    }

    @Test
    fun seSigueElEpisodioAMedias() {
        val f = serieDeCinco(1 to visto(100), 2 to aMedias(200)).ficha("tv:1")!!
        assertEquals("T1·E2", f.episodioParaSeguir()!!.etiqueta)
    }

    @Test
    fun siElUltimoSeTerminoTocaElSiguienteAunqueSeaDeOtraTemporada() {
        assertEquals("T1·E3", serieDeCinco(1 to visto(100), 2 to visto(200)).ficha("tv:1")!!.episodioParaSeguir()!!.etiqueta)
        assertEquals("T2·E1", serieDeCinco(3 to visto(300)).ficha("tv:1")!!.episodioParaSeguir()!!.etiqueta)
    }

    @Test
    fun cuentaElUltimoQueSeTocoNoElMasAlto() {
        // Se vio el 3 y luego se volvió a empezar el 1: se sigue por el 1.
        val f = serieDeCinco(3 to visto(100), 1 to aMedias(200)).ficha("tv:1")!!
        assertEquals("T1·E1", f.episodioParaSeguir()!!.etiqueta)
    }

    @Test
    fun saltaLosSiguientesQueYaSeVieron() {
        val f = serieDeCinco(2 to visto(300), 3 to visto(100)).ficha("tv:1")!!
        assertEquals("T2·E1", f.episodioParaSeguir()!!.etiqueta)
    }

    @Test
    fun serieTerminadaNoSaleEnSeguirViendo() {
        val c = serieDeCinco(1 to visto(1), 2 to visto(2), 3 to visto(3), 11 to visto(4), 12 to visto(5))
        assertNull(c.ficha("tv:1")!!.episodioParaSeguir())
        assertEquals("T1·E1", c.ficha("tv:1")!!.episodioPrincipal()!!.etiqueta)
        assertTrue(c.seguirViendo().isEmpty())
        assertTrue(c.ficha("tv:1")!!.vista)
    }

    @Test
    fun unosSegundosNoCuentanComoEmpezado() {
        val f = serieDeCinco(1 to Progreso(10.0, 100.0, 50)).ficha("tv:1")!!
        assertNull(f.episodioParaSeguir())
    }

    @Test
    fun seguirViendoJuntaPeliculasYSeriesLoUltimoPrimero() {
        val c = catalogo(
            listOf(
                video("matrix", "movie:1", progreso = aMedias(300)),
                video("alien", "movie:2", progreso = visto(400)),
                video("blade", "movie:3", progreso = Progreso(5.0, 100.0, 500)),
                video("bb1", "tv:1", 1, 1, progreso = visto(350)),
                video("bb2-4k", "tv:1", 1, 2, "4K"),
                video("bb2-hd", "tv:1", 1, 2, "1080p"),
                video("got1", "tv:2", 1, 1, progreso = aMedias(100)),
            ),
            obra("movie:1", "pelicula", "Matrix"), obra("movie:2", "pelicula", "Alien"), obra("movie:3", "pelicula", "Blade Runner"),
            obra("tv:1", "serie", "Breaking Bad"), obra("tv:2", "serie", "Juego de tronos"),
        )
        val seguir = c.seguirViendo(descargadas = setOf("bb2-hd"))
        assertEquals(listOf("tv:1", "movie:1", "tv:2"), seguir.map { it.ficha.obra.id })
        val bb = seguir.first()
        assertEquals("T1·E2", bb.episodio!!.etiqueta)
        assertEquals("bb2-hd", bb.video.ruta) // la descargada antes que la 4K por la red
        val tarjeta = tarjetaDe(bb)
        assertEquals("Breaking Bad", tarjeta.titulo)
        assertEquals("T1·E2", tarjeta.detalle)
        assertEquals("seguir:tv:1", tarjeta.clave)
        assertEquals(2, tarjeta.versiones.size)
        assertEquals(0.5f, tarjetaDe(seguir[1]).avance!!, 0.001f)
    }

    // ---------- siguiente episodio ----------

    @Test
    fun siguienteEpisodioConLaMismaCalidad() {
        val c = catalogo(
            listOf(
                video("e1-4k", "tv:1", 1, 1, "4K"), video("e1-hd", "tv:1", 1, 1, "1080p"), video("e1-sd", "tv:1", 1, 1, "720p"),
                video("e2-4k", "tv:1", 1, 2, "4K"), video("e2-hd", "tv:1", 1, 2, "1080p"),
                video("t2e1", "tv:1", 2, 1, nombre = "Nueva temporada"),
                video("especial", "tv:1", 0, 1),
            ),
            obra("tv:1", "serie"),
        )
        assertEquals("e2-4k", c.siguienteEpisodio("e1-4k")!!.video.ruta)
        assertEquals("e2-hd", c.siguienteEpisodio("e1-hd")!!.video.ruta)
        // No hay 720p del siguiente: la mejor, salvo que haya una descargada.
        assertEquals("e2-4k", c.siguienteEpisodio("e1-sd")!!.video.ruta)
        assertEquals("e2-hd", c.siguienteEpisodio("e1-sd", descargadas = setOf("e2-hd"))!!.video.ruta)
        val cambio = c.siguienteEpisodio("e2-hd")!!
        assertEquals("T2·E1", cambio.episodio!!.etiqueta)
        assertEquals("T2·E1 · Nueva temporada", cambio.episodio!!.titulo)
        // Después del último no se ofrecen los especiales.
        assertNull(c.siguienteEpisodio("t2e1"))
        assertNull(c.siguienteEpisodio("no-existe"))
    }

    @Test
    fun lasPeliculasNoTienenSiguiente() {
        val c = catalogo(listOf(video("a", "movie:1"), video("b", "movie:1", version = "4K")), obra("movie:1", "pelicula"))
        assertNull(c.siguienteEpisodio("a"))
    }

    private fun descarga(ruta: String, obra: String?, t: Int?, e: Int?, version: String? = null) =
        Descarga(ruta, "Serie · T$t·E$e", 1, "/f/$ruta", null, emptyList(), 1, obra, "Serie", t, e, null, version)

    @Test
    fun sinConexionElSiguienteSaleDeLasDescargas() {
        val descargas = listOf(
            descarga("t1e1", "tv:1", 1, 1, "1080p"),
            descarga("t1e3-4k", "tv:1", 1, 3, "4K"),
            descarga("t1e3-hd", "tv:1", 1, 3, "1080p"),
            descarga("t2e1", "tv:1", 2, 1),
            descarga("otra", "tv:2", 1, 2),
            descarga("especial", "tv:1", 0, 1),
            descarga("peli", null, null, null),
        )
        assertEquals("t1e3-hd", siguienteDescarga(descargas, "t1e1")!!.ruta) // falta el 2: el siguiente que haya
        assertEquals("t2e1", siguienteDescarga(descargas, "t1e3-4k")!!.ruta)
        assertNull(siguienteDescarga(descargas, "t2e1"))
        assertNull(siguienteDescarga(descargas, "peli"))
        assertNull(siguienteDescarga(descargas, "no-existe"))
        // Sin la misma calidad, la mejor.
        assertEquals("t1e3-4k", siguienteDescarga(descargas.filter { it.ruta != "t1e1" } + descarga("t1e2", "tv:1", 1, 2, "720p"), "t1e2")!!.ruta)
    }

    @Test
    fun laDescargaGuardaLaSerieYSeLeeSinElla() {
        val d = Descarga("r", "Breaking Bad · T1·E2 · El gato", 7, "/f", null, listOf(SubtituloLocal("es", "/s")), 9, "tv:1396", "Breaking Bad", 1, 2, "El gato", "4K")
        val leida = Descarga.desdeJson(JSONObject(d.aJson().toString()))
        assertEquals(d, leida)
        assertEquals("T1·E2 · El gato", leida.detalleEpisodio)
        // Las descargas guardadas por la versión anterior no tienen nada de esto.
        val antigua = Descarga.desdeJson(JSONObject("""{ "ruta": "r", "titulo": "Alien", "id": 3, "archivo": "/f", "poster": null, "tamano": 1, "subtitulos": [] }"""))
        assertNull(antigua.obra)
        assertNull(antigua.serie)
        assertNull(antigua.detalleEpisodio)
    }

    // ---------- pantalla principal ----------

    @Test
    fun filasDeInicioSeguirViendoYUnaPorSeccion() {
        val c = catalogo(
            listOf(
                video("vieja", "movie:1", fecha = 100), video("nueva", "movie:2", fecha = 300, progreso = aMedias(1)),
                video("bb1", "tv:1", 1, 1, fecha = 200), video("bb2", "tv:1", 1, 2, fecha = 250),
                video("naruto", "tv:9", 1, 1, fecha = 50, seccion = "anime"),
            ),
            obra("movie:1", "pelicula", "Vieja"), obra("movie:2", "pelicula", "Nueva"),
            obra("tv:1", "serie", "Breaking Bad"), obra("tv:9", "serie", "Naruto", seccion = "anime"),
        )
        val filas = c.filasInicio()
        assertEquals(listOf("Seguir viendo", "Películas", "Series", "Anime"), filas.map { it.titulo }) // MiCine vacía: no sale
        assertEquals(listOf("Nueva", "Vieja"), filas[1].tarjetas.map { it.titulo }) // lo más nuevo primero
        val serie = filas[2].tarjetas.single()
        assertNull(serie.video) // una serie abre su ficha
        assertTrue(serie.versiones.isEmpty())
        assertEquals("tv:1", serie.obra)
        assertEquals("2 episodios", serie.detalle)
    }

    @Test
    fun paginaDeSeccionDeLaAALaZ() {
        val c = catalogo(
            listOf(video("z", "tv:1", 1, 1), video("a", "tv:2", 1, 1), video("m", "tv:3", 1, 1, progreso = aMedias(5))),
            obra("tv:1", "serie", "Zorro"), obra("tv:2", "serie", "Águila Roja"), obra("tv:3", "serie", "Mentes"),
        )
        val pagina = c.paginaSeccion(Seccion("series", "Series"))
        assertEquals("Series de la A a la Z", pagina.tituloTodas)
        assertEquals(listOf("Águila Roja", "Mentes", "Zorro"), pagina.todas.map { it.titulo }) // sin tildes al ordenar
        assertEquals(listOf("Seguir viendo"), pagina.filas.map { it.titulo })
    }

    @Test
    fun tarjetaDePeliculaConVariasVersiones() {
        val c = catalogo(
            listOf(video("hd", "movie:1", version = "1080p", tamano = 2048), video("k4", "movie:1", version = "4K", tamano = 4096)),
            obra("movie:1", "pelicula", "Matrix", anio = 1999),
        )
        val t = tarjetaDe(c.ficha("movie:1")!!)
        assertEquals("Matrix", t.titulo)
        assertEquals("k4", t.video!!.ruta)
        assertTrue(t.detalle.startsWith("1999 · 2 versiones · "))
        assertEquals(2, t.versiones.size)
        assertEquals("hd", tarjetaDe(c.ficha("movie:1")!!, descargadas = setOf("hd")).video!!.ruta)
    }

    @Test
    fun buscarPorTituloNombreDeEpisodioYSinTildes() {
        val c = catalogo(
            listOf(video("bb", "tv:1", 1, 2, nombre = "El gato está en la bolsa"), video("p", "movie:1")),
            obra("tv:1", "serie", "Breaking Bad"), obra("movie:1", "pelicula", "Película rara"),
        )
        assertEquals(listOf("Breaking Bad"), c.buscar(normalizar("breaking"), null).map { it.titulo })
        assertEquals(listOf("Breaking Bad"), c.buscar(normalizar("esta en la bolsa"), null).map { it.titulo })
        assertEquals(listOf("Película rara"), c.buscar(normalizar("pelicula"), null).map { it.titulo })
        assertTrue(c.buscar(normalizar("breaking"), "peliculas").isEmpty())
    }

    @Test
    fun laClaveDeLaImagenQuitaLaFirmaPeroNoLaVersion() {
        assertEquals("/media/x.jpg", claveImagen("/media/x.jpg?u=1&e=2&f=3"))
        assertEquals("/caratula/abc.jpg?v=99", claveImagen("/caratula/abc.jpg?u=1&e=2&f=3&v=99"))
        assertEquals("/caratula/abc.jpg?v=7", claveImagen("http://1.2.3.4:8080/caratula/abc.jpg?v=7&u=1").substringAfter("8080"))
        assertEquals("/sin/firma.jpg", claveImagen("/sin/firma.jpg"))
    }
}
