package es.micine.app

import android.app.UiModeManager
import android.content.Context
import android.content.pm.PackageManager
import android.content.res.Configuration
import androidx.core.content.edit
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.SupervisorJob
import kotlinx.coroutines.launch
import kotlinx.coroutines.withContext
import org.json.JSONArray
import org.json.JSONObject
import java.io.IOException
import java.net.HttpURLConnection
import java.net.URL
import java.security.MessageDigest
import java.text.Normalizer
import java.util.Locale
import kotlin.math.roundToInt

/** Trabajos que deben terminar aunque se cierre la pantalla (guardar el progreso, bajar subtítulos…). */
val ambitoApp = CoroutineScope(SupervisorJob() + Dispatchers.IO)

data class Subtitulo(val etiqueta: String, val url: String)

data class Progreso(val segundos: Double, val duracion: Double, val fecha: Long) {
    val fraccion: Float get() = if (duracion > 0) (segundos / duracion).toFloat().coerceIn(0f, 1f) else 0f
    val vista: Boolean get() = fraccion >= 0.95f

    /** A medias (como en la web): más de 15 segundos y sin terminar. */
    val enCurso: Boolean get() = segundos > 15 && !vista

    /** Se empezó de verdad o se terminó: cuenta para «Seguir viendo». */
    val tocado: Boolean get() = vista || segundos > 15
}

/**
 * Un vídeo de MiCine. Los servidores nuevos (1.3) dicen además a qué obra pertenece (película o serie),
 * su sección y, si es un episodio, la temporada y el número; los antiguos no, y entonces todo es una lista plana.
 */
data class Pelicula(
    val ruta: String,
    val titulo: String,
    val carpeta: String,
    val tamano: Long,
    val url: String,
    val poster: String?,
    val subtitulos: List<Subtitulo>,
    val progreso: Progreso?,
    val fecha: Long = 0,
    val seccion: String? = null,
    val obra: String? = null,
    val temporada: Int? = null,
    val episodio: Int? = null,
    val nombreEpisodio: String? = null,
    /** «4K», «1080p», «720p» o null: distingue las copias de la misma película o episodio. */
    val version: String? = null,
    val grupo: String? = null,
) {
    val extension: String get() = ruta.substringAfterLast('.', "").lowercase(Locale.ROOT)

    val esEpisodio: Boolean get() = episodio != null

    companion object {
        fun desdeJson(o: JSONObject) = Pelicula(
            ruta = o.getString("ruta"),
            titulo = o.getString("titulo"),
            carpeta = o.textoONulo("carpeta").orEmpty(),
            tamano = o.optLong("tamano"),
            url = o.getString("url"),
            poster = o.textoONulo("poster"),
            subtitulos = o.optJSONArray("subtitulos").objetos().map { Subtitulo(it.getString("etiqueta"), it.getString("url")) },
            progreso = o.optJSONObject("progreso")?.let { Progreso(it.optDouble("t"), it.optDouble("d"), it.optLong("fecha")) },
            fecha = o.optLong("fecha"),
            seccion = o.textoONulo("seccion"),
            obra = o.textoONulo("obra"),
            temporada = o.enteroONulo("temporada"),
            episodio = o.enteroONulo("episodio"),
            nombreEpisodio = o.textoONulo("nombre_episodio")?.takeIf { it.isNotBlank() },
            version = o.textoONulo("version")?.takeIf { it.isNotBlank() },
            grupo = o.textoONulo("grupo"),
        )
    }
}

fun JSONArray?.objetos(): List<JSONObject> = if (this == null) emptyList() else (0 until length()).map { getJSONObject(it) }

fun JSONObject.textoONulo(clave: String): String? = if (isNull(clave)) null else optString(clave)

fun JSONObject.enteroONulo(clave: String): Int? = if (isNull(clave)) null else when (val valor = opt(clave)) {
    is Number -> valor.toInt()
    is String -> valor.trim().toIntOrNull()
    else -> null
}

fun huella(texto: String): String =
    MessageDigest.getInstance("SHA-1").digest(texto.toByteArray()).joinToString("") { "%02x".format(it) }.take(16)

fun normalizar(texto: String): String =
    Normalizer.normalize(texto, Normalizer.Form.NFD).replace(Regex("\\p{M}+"), "").lowercase(Locale.ROOT)

fun formatoTamano(bytes: Long): String {
    val unidades = listOf("B", "KB", "MB", "GB", "TB")
    var valor = bytes.toDouble()
    var i = 0
    while (valor >= 1024 && i < unidades.lastIndex) {
        valor /= 1024
        i++
    }
    return String.format(Locale.forLanguageTag("es-ES"), if (i >= 3) "%.1f %s" else "%.0f %s", valor, unidades[i])
}

fun formatoTiempo(milisegundos: Long): String {
    val total = (milisegundos / 1000).coerceAtLeast(0)
    val horas = total / 3600
    val minutos = (total % 3600) / 60
    val segundos = total % 60
    return if (horas > 0) "%d:%02d:%02d".format(horas, minutos, segundos) else "%d:%02d".format(minutos, segundos)
}

/** Fire TV y demás teles: se manejan con el mando a distancia, sin pantalla táctil. */
fun esTele(context: Context): Boolean {
    val modo = (context.getSystemService(Context.UI_MODE_SERVICE) as? UiModeManager)?.currentModeType
    return modo == Configuration.UI_MODE_TYPE_TELEVISION ||
        !context.packageManager.hasSystemFeature(PackageManager.FEATURE_TOUCHSCREEN)
}

fun Context.dp(valor: Int): Int = (valor * resources.displayMetrics.density).roundToInt()

/** La app para Fire TV, siempre en su última versión (se publica en GitHub). */
const val DESCARGA_FIRE_TV = "https://github.com/Alvaropegr/MiCine/releases/latest/download/MiCine-firetv.apk"

/** Usuario, servidor y token guardados en el móvil. */
class Sesion(context: Context) {
    private val prefs = context.getSharedPreferences("sesion", Context.MODE_PRIVATE)

    var servidor: String?
        get() = prefs.getString("servidor", null)
        set(valor) = prefs.edit { putString("servidor", valor) }
    var usuario: String?
        get() = prefs.getString("usuario", null)
        set(valor) = prefs.edit { putString("usuario", valor) }
    var token: String?
        get() = prefs.getString("token", null)
        set(valor) = prefs.edit { putString("token", valor) }

    val iniciada: Boolean get() = servidor != null && token != null

    fun api() = Api(servidor ?: "", token)

    /** Se olvida el token; el servidor y el usuario se quedan para rellenar el formulario. */
    fun cerrar() = prefs.edit { remove("token") }
}

/** Por dónde va cada película en este móvil (sirve también sin conexión). */
class ProgresoLocal(context: Context) {
    private val prefs = context.getSharedPreferences("progreso", Context.MODE_PRIVATE)

    fun leer(ruta: String): Progreso? = prefs.getString(ruta, null)?.let {
        runCatching { JSONObject(it) }.getOrNull()?.let { o -> Progreso(o.optDouble("t"), o.optDouble("d"), o.optLong("fecha")) }
    }

    fun guardar(ruta: String, segundos: Double, duracion: Double) = prefs.edit {
        putString(ruta, JSONObject().put("t", segundos).put("d", duracion).put("fecha", System.currentTimeMillis() / 1000).toString())
    }

    /** Lo visto sin conexión manda si es más reciente; de paso se lo cuenta al servidor. */
    fun mezclar(api: Api, pelicula: Pelicula): Pelicula {
        val local = leer(pelicula.ruta) ?: return pelicula
        val remoto = pelicula.progreso
        if (remoto != null && remoto.fecha >= local.fecha) return pelicula
        ambitoApp.launch { runCatching { api.progreso(pelicula.ruta, local.segundos, local.duracion) } }
        return pelicula.copy(progreso = local)
    }
}

/** El último catálogo recibido: lo comparten el catálogo, la ficha de una serie y el reproductor (para el siguiente episodio). */
object CatalogoActual {
    @Volatile
    var catalogo: Catalogo? = null
}

/** Pide el catálogo a MiCine, le añade lo visto sin conexión y lo deja a mano de las demás pantallas. */
suspend fun cargarCatalogo(sesion: Sesion, progresoLocal: ProgresoLocal): Catalogo {
    val api = sesion.api()
    val catalogo = api.catalogo().conPeliculas { progresoLocal.mezclar(api, it) }
    CatalogoActual.catalogo = catalogo
    return catalogo
}

class ErrorApi(mensaje: String, val codigo: Int = 0) : Exception(mensaje)

/** Conversación con el servidor MiCine del ordenador. */
class Api(servidor: String, private val token: String? = null) {
    val base: String = normalizarServidor(servidor)

    fun absoluta(url: String): String = if (url.startsWith("http")) url else base + url

    suspend fun login(usuario: String, contrasena: String): Pair<String, String> {
        val cuerpo = JSONObject().put("usuario", usuario).put("contrasena", contrasena).put("app", true)
        val respuesta = peticion("POST", "/api/login", cuerpo)
        return respuesta.getString("token") to respuesta.getJSONObject("usuario").getString("nombre")
    }

    /** Las películas y, en los servidores nuevos, también las obras (series agrupadas) y las secciones. */
    suspend fun catalogo(): Catalogo = Catalogo.desdeJson(peticion("GET", "/api/peliculas"))

    suspend fun progreso(ruta: String, segundos: Double, duracion: Double) {
        peticion("POST", "/api/progreso", JSONObject().put("ruta", ruta).put("t", segundos).put("d", duracion))
    }

    suspend fun logout() {
        peticion("POST", "/api/logout", JSONObject())
    }

    private suspend fun peticion(metodo: String, ruta: String, cuerpo: JSONObject? = null): JSONObject =
        withContext(Dispatchers.IO) {
            val conexion = try {
                URL(base + ruta).openConnection() as HttpURLConnection
            } catch (e: IOException) {
                throw ErrorApi("La dirección de MiCine no es válida")
            }
            try {
                conexion.requestMethod = metodo
                conexion.connectTimeout = 6000
                conexion.readTimeout = 30000
                conexion.setRequestProperty("Accept", "application/json")
                token?.let { conexion.setRequestProperty("Authorization", "Bearer $it") }
                if (cuerpo != null) {
                    conexion.doOutput = true
                    conexion.setRequestProperty("Content-Type", "application/json")
                    conexion.setRequestProperty("X-MiCine", "1")
                    conexion.outputStream.use { it.write(cuerpo.toString().toByteArray()) }
                }
                val codigo = conexion.responseCode
                val flujo = if (codigo < 400) conexion.inputStream else conexion.errorStream
                val texto = flujo?.bufferedReader()?.use { it.readText() }.orEmpty()
                val json = runCatching { JSONObject(texto) }.getOrElse { JSONObject() }
                if (codigo >= 400) throw ErrorApi(json.optString("error").ifEmpty { "Error $codigo del servidor" }, codigo)
                json
            } catch (e: IOException) {
                throw ErrorApi("No se puede conectar con MiCine ($base). ¿Está encendido el ordenador y estás en la misma red?")
            } finally {
                conexion.disconnect()
            }
        }

    companion object {
        /** "192.168.1.50" → "http://192.168.1.50:8080" */
        fun normalizarServidor(texto: String): String {
            var s = texto.trim().trimEnd('/')
            if (s.isEmpty()) return s
            if (!s.startsWith("http://") && !s.startsWith("https://")) s = "http://$s"
            val url = runCatching { URL(s) }.getOrNull()
            if (url != null && url.port == -1 && url.protocol == "http" && url.path.isEmpty()) s = "$s:8080"
            return s
        }
    }
}
