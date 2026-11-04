package es.micine.app

import android.app.DownloadManager
import android.content.Context
import android.net.Uri
import android.os.Environment
import androidx.core.content.ContextCompat
import kotlinx.coroutines.launch
import org.json.JSONArray
import org.json.JSONObject
import java.io.File
import java.net.HttpURLConnection
import java.net.URL

data class SubtituloLocal(val etiqueta: String, val archivo: String)

data class Descarga(
    val ruta: String,
    val titulo: String,
    val idSistema: Long,
    val archivo: String,
    val poster: String?,
    val subtitulos: List<SubtituloLocal>,
    val tamano: Long,
) {
    fun aJson(): JSONObject = JSONObject()
        .put("ruta", ruta).put("titulo", titulo).put("id", idSistema).put("archivo", archivo)
        .put("poster", poster ?: JSONObject.NULL).put("tamano", tamano)
        .put("subtitulos", JSONArray(subtitulos.map { JSONObject().put("etiqueta", it.etiqueta).put("archivo", it.archivo) }))

    companion object {
        fun desdeJson(o: JSONObject) = Descarga(
            ruta = o.getString("ruta"),
            titulo = o.getString("titulo"),
            idSistema = o.getLong("id"),
            archivo = o.getString("archivo"),
            poster = o.textoONulo("poster"),
            subtitulos = o.optJSONArray("subtitulos").objetos().map { SubtituloLocal(it.getString("etiqueta"), it.getString("archivo")) },
            tamano = o.optLong("tamano"),
        )
    }
}

enum class EstadoDescarga { EN_COLA, DESCARGANDO, PAUSADA, COMPLETA, FALLIDA }

data class InfoDescarga(val estado: EstadoDescarga, val bajado: Long, val total: Long, val motivo: String? = null) {
    val activa: Boolean get() = estado == EstadoDescarga.EN_COLA || estado == EstadoDescarga.DESCARGANDO || estado == EstadoDescarga.PAUSADA
    val fraccion: Float get() = if (total > 0) (bajado.toFloat() / total).coerceIn(0f, 1f) else 0f
}

/**
 * Películas guardadas en el móvil para verlas sin conexión, como en Netflix.
 * El vídeo lo baja el gestor de descargas de Android (sigue aunque cierres la app,
 * reintenta si se corta y avisa al terminar); la carátula y los subtítulos los baja la app.
 * Todo se guarda en la carpeta privada de la app: se borra al desinstalarla.
 */
class Descargas(context: Context) {
    private val contexto = context.applicationContext
    private val gestor: DownloadManager? = ContextCompat.getSystemService(contexto, DownloadManager::class.java)
    private val indice = File(contexto.filesDir, "descargas.json")

    fun todas(): List<Descarga> = runCatching {
        JSONArray(indice.readText()).let { lista -> (0 until lista.length()).map { Descarga.desdeJson(lista.getJSONObject(it)) } }
    }.getOrDefault(emptyList())

    fun de(ruta: String): Descarga? = todas().firstOrNull { it.ruta == ruta }

    private fun guardar(lista: List<Descarga>) {
        val temporal = File(indice.path + ".tmp")
        temporal.writeText(JSONArray(lista.map { it.aJson() }).toString())
        temporal.renameTo(indice)
    }

    private fun carpetaExtras(ruta: String) = File(contexto.filesDir, "descargas/${huella(ruta)}")

    fun espacioLibre(): Long = contexto.getExternalFilesDir(Environment.DIRECTORY_MOVIES)?.usableSpace ?: 0L

    /** Empieza a bajar la película. Si ya había una descarga fallida, la sustituye. */
    fun iniciar(api: Api, pelicula: Pelicula): Descarga {
        val sistema = gestor ?: throw ErrorApi("Este aparato no permite descargar películas")
        de(pelicula.ruta)?.let { borrar(it) }
        val nombre = "${huella(pelicula.ruta)}.${pelicula.extension.ifEmpty { "mp4" }}"
        val carpeta = contexto.getExternalFilesDir(Environment.DIRECTORY_MOVIES)
            ?: throw ErrorApi("El almacenamiento del móvil no está disponible")
        File(carpeta, nombre).delete()
        val peticion = DownloadManager.Request(Uri.parse(api.absoluta(pelicula.url)))
            .setTitle(pelicula.titulo)
            .setDescription("MiCine")
            .setNotificationVisibility(DownloadManager.Request.VISIBILITY_VISIBLE_NOTIFY_COMPLETED)
            .setDestinationInExternalFilesDir(contexto, Environment.DIRECTORY_MOVIES, nombre)
        val id = sistema.enqueue(peticion)

        val extras = carpetaExtras(pelicula.ruta).apply { mkdirs() }
        val subtitulos = pelicula.subtitulos.mapIndexed { i, s ->
            val ext = s.url.substringBefore('?').substringAfterLast('.', "srt")
            SubtituloLocal(s.etiqueta, File(extras, "subtitulo$i.$ext").path)
        }
        val descarga = Descarga(
            ruta = pelicula.ruta,
            titulo = pelicula.titulo,
            idSistema = id,
            archivo = File(carpeta, nombre).path,
            poster = pelicula.poster?.let { File(extras, "poster").path },
            subtitulos = subtitulos,
            tamano = pelicula.tamano,
        )
        guardar(todas() + descarga)

        // Carátula y subtítulos: archivos pequeños, se bajan directamente.
        ambitoApp.launch {
            pelicula.poster?.let { url ->
                val enCache = Imagenes.archivoEnCache(contexto, api.absoluta(url))
                if (enCache.exists()) enCache.copyTo(File(descarga.poster!!), overwrite = true)
                else bajar(api.absoluta(url), File(descarga.poster!!))
            }
            pelicula.subtitulos.forEachIndexed { i, s ->
                bajar(api.absoluta(s.url) + "&original=1", File(subtitulos[i].archivo))
            }
        }
        return descarga
    }

    private fun bajar(url: String, destino: File) {
        runCatching {
            val conexion = URL(url).openConnection() as HttpURLConnection
            try {
                conexion.connectTimeout = 6000
                conexion.readTimeout = 20000
                if (conexion.responseCode == 200) conexion.inputStream.use { entrada -> destino.outputStream().use { entrada.copyTo(it) } }
            } finally {
                conexion.disconnect()
            }
        }
    }

    fun info(descarga: Descarga): InfoDescarga {
        gestor?.query(DownloadManager.Query().setFilterById(descarga.idSistema))?.use { c ->
            if (c.moveToFirst()) {
                val estado = c.getInt(c.getColumnIndexOrThrow(DownloadManager.COLUMN_STATUS))
                val motivo = c.getInt(c.getColumnIndexOrThrow(DownloadManager.COLUMN_REASON))
                val bajado = c.getLong(c.getColumnIndexOrThrow(DownloadManager.COLUMN_BYTES_DOWNLOADED_SO_FAR))
                val total = c.getLong(c.getColumnIndexOrThrow(DownloadManager.COLUMN_TOTAL_SIZE_BYTES)).takeIf { it > 0 } ?: descarga.tamano
                return when (estado) {
                    DownloadManager.STATUS_SUCCESSFUL -> InfoDescarga(EstadoDescarga.COMPLETA, total, total)
                    DownloadManager.STATUS_RUNNING -> InfoDescarga(EstadoDescarga.DESCARGANDO, bajado, total)
                    DownloadManager.STATUS_PENDING -> InfoDescarga(EstadoDescarga.EN_COLA, bajado, total, "En cola")
                    DownloadManager.STATUS_PAUSED -> InfoDescarga(EstadoDescarga.PAUSADA, bajado, total, motivoPausa(motivo))
                    else -> InfoDescarga(EstadoDescarga.FALLIDA, bajado, total, motivoFallo(motivo))
                }
            }
        }
        // El sistema ya no la recuerda (se limpió su lista): vale si el archivo sigue ahí.
        return if (File(descarga.archivo).exists()) InfoDescarga(EstadoDescarga.COMPLETA, descarga.tamano, descarga.tamano)
        else InfoDescarga(EstadoDescarga.FALLIDA, 0, descarga.tamano, "El archivo ya no está en el móvil")
    }

    fun completa(descarga: Descarga): Boolean = info(descarga).estado == EstadoDescarga.COMPLETA && File(descarga.archivo).exists()

    /** Quita la descarga (o la cancela si aún no había terminado) y libera el espacio. */
    fun borrar(descarga: Descarga) {
        runCatching { gestor?.remove(descarga.idSistema) }
        File(descarga.archivo).delete()
        carpetaExtras(descarga.ruta).deleteRecursively()
        guardar(todas().filter { it.ruta != descarga.ruta })
    }

    private fun motivoPausa(motivo: Int) = when (motivo) {
        DownloadManager.PAUSED_WAITING_FOR_NETWORK -> "Esperando conexión con MiCine"
        DownloadManager.PAUSED_QUEUED_FOR_WIFI -> "Esperando Wi-Fi"
        DownloadManager.PAUSED_WAITING_TO_RETRY -> "Se cortó; reintentando…"
        else -> "En pausa"
    }

    private fun motivoFallo(motivo: Int) = when (motivo) {
        DownloadManager.ERROR_INSUFFICIENT_SPACE -> "No queda espacio en el móvil"
        DownloadManager.ERROR_CANNOT_RESUME, DownloadManager.ERROR_HTTP_DATA_ERROR -> "Se cortó la conexión. Toca para reintentar."
        401, 403 -> "El enlace caducó. Toca para reintentar."
        404 -> "La película ya no está en MiCine"
        else -> "No se pudo descargar. Toca para reintentar."
    }
}
