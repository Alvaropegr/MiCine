package es.micine.app

import android.content.Context
import android.content.Intent
import android.net.Uri
import androidx.appcompat.app.AppCompatActivity
import com.google.android.material.dialog.MaterialAlertDialogBuilder
import java.io.File

/** Abre el reproductor con [pelicula] (por la red) o con su descarga [local] (sin red), donde se dejó. */
fun intentReproductor(context: Context, pelicula: Pelicula?, local: Descarga?): Intent {
    require(pelicula != null || local != null)
    val ruta = pelicula?.ruta ?: local!!.ruta
    val titulo = pelicula?.titulo ?: local!!.titulo
    val progreso = listOfNotNull(pelicula?.progreso, ProgresoLocal(context).leer(ruta)).maxByOrNull { it.fecha }
    val inicioMs = progreso?.takeIf { it.enCurso }?.let { (it.segundos * 1000).toLong() } ?: 0L
    val api = Sesion(context).api()
    val origen = local?.archivo ?: api.absoluta(pelicula!!.url)
    val subtitulos = local?.subtitulos?.filter { File(it.archivo).exists() }?.map { Uri.fromFile(File(it.archivo)).toString() }
        ?: pelicula!!.subtitulos.map { api.absoluta(it.url) + "&original=1" }
    return Intent(context, ReproductorActivity::class.java)
        .putExtra(ReproductorActivity.EXTRA_TITULO, titulo)
        .putExtra(ReproductorActivity.EXTRA_RUTA, ruta)
        .putExtra(ReproductorActivity.EXTRA_ORIGEN, origen)
        .putStringArrayListExtra(ReproductorActivity.EXTRA_SUBTITULOS, ArrayList(subtitulos))
        .putExtra(ReproductorActivity.EXTRA_INICIO_MS, inicioMs)
}

/** El episodio que va después del que se está viendo: lo que enseña y abre el reproductor al acabar. */
class Siguiente(val detalle: String, val imagen: String?, val intent: Intent)

/**
 * Busca el siguiente episodio de [ruta]: en el catálogo si se cargó (prefiere la copia descargada)
 * y, sin catálogo (sin conexión), entre las descargas terminadas.
 */
fun buscarSiguiente(context: Context, ruta: String): Siguiente? {
    val descargas = Descargas(context)
    val completas = descargas.todas().filter { descargas.completa(it) }
    val catalogo = CatalogoActual.catalogo
    if (catalogo != null && catalogo.fichaDe(ruta) != null) {
        val siguiente = catalogo.siguienteEpisodio(ruta, completas.map { it.ruta }.toSet()) ?: return null
        val local = completas.firstOrNull { it.ruta == siguiente.video.ruta }
        val imagen = local?.poster?.takeIf { File(it).exists() }
            ?: siguiente.episodio?.poster?.let { Sesion(context).api().absoluta(it) }
        return Siguiente(siguiente.episodio?.titulo ?: siguiente.video.titulo, imagen, intentReproductor(context, siguiente.video, local))
    }
    val descarga = siguienteDescarga(completas, ruta) ?: return null
    return Siguiente(descarga.detalleEpisodio ?: descarga.titulo, descarga.poster?.takeIf { File(it).exists() }, intentReproductor(context, null, descarga))
}

/**
 * Reproducir y descargar: igual desde el catálogo que desde la ficha de una serie.
 * [avisar] enseña un mensaje abajo (con un botón opcional), [verDescargas] lleva a la pestaña Descargas
 * y [alCambiar] repinta la pantalla tras empezar o borrar una descarga.
 */
class Acciones(
    private val actividad: AppCompatActivity,
    private val sesion: Sesion,
    private val descargas: Descargas,
    private val avisar: (texto: String, boton: String?, accion: (() -> Unit)?) -> Unit,
    private val verDescargas: () -> Unit,
    private val alCambiar: () -> Unit,
) {
    fun reproducir(pelicula: Pelicula) {
        val local = descargas.de(pelicula.ruta)?.takeIf { descargas.completa(it) }
        actividad.startActivity(intentReproductor(actividad, pelicula, local))
    }

    /** Lista para elegir entre varias versiones (4K, 1080p…) de la misma película o episodio. */
    fun elegirVersion(titulo: String, versiones: List<Pelicula>, alElegir: (Pelicula) -> Unit) {
        val completas = descargas.rutasCompletas()
        val nombres = versiones.map { v ->
            buildList {
                add(nombreVersion(v.version))
                add(formatoTamano(v.tamano))
                if (versiones.count { it.version == v.version } > 1) add(v.ruta.substringAfterLast('/'))
                v.progreso?.let { p ->
                    if (p.vista) add("vista") else if (p.enCurso) add("vas por ${formatoTiempo((p.segundos * 1000).toLong())}")
                }
                if (v.ruta in completas) add("en el móvil")
            }.joinToString(" · ")
        }
        MaterialAlertDialogBuilder(actividad)
            .setTitle(titulo)
            .setItems(nombres.toTypedArray()) { _, i -> alElegir(versiones[i]) }
            .setNegativeButton("Cancelar", null)
            .show()
    }

    /** Botón de descarga de una película o episodio con varias versiones: se elige cuál (la más ligera ocupa menos). */
    fun pulsarDescarga(versiones: List<Pelicula>) {
        val existente = versiones.firstOrNull { descargas.de(it.ruta) != null }
        when {
            versiones.isEmpty() -> Unit
            existente != null -> pulsarDescarga(existente)
            versiones.size == 1 -> pulsarDescarga(versiones.first())
            else -> elegirVersion("¿Qué versión descargas?", versiones) { pulsarDescarga(it) }
        }
    }

    fun pulsarDescarga(pelicula: Pelicula) {
        val descarga = descargas.de(pelicula.ruta)
        val info = descarga?.let { descargas.info(it) }
        when {
            info?.estado == EstadoDescarga.COMPLETA ->
                avisar("«${pelicula.titulo}» ya está en el móvil", "Ver descargas", verDescargas)
            info?.activa == true ->
                MaterialAlertDialogBuilder(actividad)
                    .setTitle("¿Cancelar la descarga?")
                    .setMessage(pelicula.titulo)
                    .setPositiveButton("Cancelar descarga") { _, _ ->
                        descargas.borrar(descarga)
                        alCambiar()
                    }
                    .setNegativeButton("Seguir descargando", null)
                    .show()
            else -> {
                val libre = descargas.espacioLibre()
                if (pelicula.tamano > libre - 200L * 1024 * 1024) {
                    MaterialAlertDialogBuilder(actividad)
                        .setTitle("No hay espacio suficiente")
                        .setMessage("«${pelicula.titulo}» ocupa ${formatoTamano(pelicula.tamano)} y en el móvil quedan ${formatoTamano(libre)} libres.")
                        .setPositiveButton("Entendido", null)
                        .show()
                    return
                }
                val que = if (pelicula.esEpisodio) "episodio" else "película"
                MaterialAlertDialogBuilder(actividad)
                    .setTitle("Descargar $que")
                    .setMessage("«${pelicula.titulo}» ocupa ${formatoTamano(pelicula.tamano)}. Podrás verlo sin conexión desde la pestaña Descargas.\n\nLibre en el móvil: ${formatoTamano(libre)}")
                    .setPositiveButton("Descargar") { _, _ -> iniciarDescarga(pelicula) }
                    .setNegativeButton("Cancelar", null)
                    .show()
            }
        }
    }

    fun iniciarDescarga(pelicula: Pelicula) {
        try {
            descargas.iniciar(sesion.api(), pelicula, CatalogoActual.catalogo?.fichaDe(pelicula.ruta)?.obra)
            avisar("Descargando «${pelicula.titulo}». Puedes seguir usando el móvil.", null, null)
        } catch (e: Exception) {
            avisar("No se pudo empezar la descarga: ${e.message}", null, null)
        }
        alCambiar()
    }

    /** Toque en la pestaña Descargas: se ve si terminó; si falló, se reintenta; si va a medias, se ofrece verla por la red. */
    fun reproducirDescarga(descarga: Descarga) {
        val info = descargas.info(descarga)
        val pelicula = CatalogoActual.catalogo?.pelicula(descarga.ruta)
        when {
            info.estado == EstadoDescarga.COMPLETA && File(descarga.archivo).exists() ->
                actividad.startActivity(intentReproductor(actividad, pelicula, descarga).putExtra(ReproductorActivity.EXTRA_TITULO, descarga.titulo))
            info.estado == EstadoDescarga.FALLIDA ->
                if (pelicula != null) iniciarDescarga(pelicula) else avisar("Conéctate a MiCine para reintentar la descarga", null, null)
            pelicula != null ->
                MaterialAlertDialogBuilder(actividad)
                    .setTitle(descarga.titulo)
                    .setMessage("Todavía se está descargando (${(info.fraccion * 100).toInt()} %). Mientras tanto puedes verlo por internet desde MiCine.")
                    .setPositiveButton("Ver ahora") { _, _ -> actividad.startActivity(intentReproductor(actividad, pelicula, null)) }
                    .setNegativeButton("Esperar", null)
                    .show()
            else -> avisar("Todavía se está descargando (${(info.fraccion * 100).toInt()} %)", null, null)
        }
    }

    fun confirmarBorrar(descarga: Descarga) {
        MaterialAlertDialogBuilder(actividad)
            .setTitle("¿Borrar la descarga?")
            .setMessage("«${descarga.titulo}» se borrará del móvil. Seguirá disponible en MiCine.")
            .setPositiveButton("Borrar") { _, _ ->
                descargas.borrar(descarga)
                alCambiar()
            }
            .setNegativeButton("Cancelar", null)
            .show()
    }
}
