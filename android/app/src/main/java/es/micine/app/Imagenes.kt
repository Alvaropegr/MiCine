package es.micine.app

import android.content.Context
import android.graphics.Bitmap
import android.graphics.BitmapFactory
import android.graphics.drawable.GradientDrawable
import android.util.AttributeSet
import android.util.LruCache
import android.widget.ImageView
import androidx.appcompat.widget.AppCompatImageView
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.Job
import kotlinx.coroutines.SupervisorJob
import kotlinx.coroutines.launch
import kotlinx.coroutines.withContext
import java.io.File
import java.net.HttpURLConnection
import java.net.URL

/** ImageView con la proporción de un cartel de cine (2:3). */
class ImagenPoster(context: Context, attrs: AttributeSet?) : AppCompatImageView(context, attrs) {
    override fun onMeasure(widthMeasureSpec: Int, heightMeasureSpec: Int) {
        val ancho = MeasureSpec.getSize(widthMeasureSpec)
        setMeasuredDimension(ancho, ancho * 3 / 2)
    }
}

/** Carga de carátulas con caché en memoria y en disco (así se ven también sin conexión). */
object Imagenes {
    private val memoria = object : LruCache<String, Bitmap>(32 * 1024 * 1024) {
        override fun sizeOf(clave: String, valor: Bitmap) = valor.byteCount
    }
    private val ambito = CoroutineScope(SupervisorJob() + Dispatchers.Main)

    /** [origen] es una URL del servidor o la ruta de un archivo del móvil. */
    fun cargar(vista: ImageView, origen: String?, anchoMaximo: Int = 360) {
        (vista.getTag(R.id.trabajo_imagen) as? Job)?.cancel()
        vista.setImageDrawable(null)
        if (origen == null) return
        // Las URL firmadas cambian cada día: la caché va por la ruta, sin la firma.
        val clave = origen.substringBefore('?')
        memoria.get(clave)?.let {
            vista.setImageBitmap(it)
            return
        }
        val contexto = vista.context.applicationContext
        val trabajo = ambito.launch {
            val imagen = withContext(Dispatchers.IO) { runCatching { decodificar(contexto, origen, clave, anchoMaximo) }.getOrNull() }
            if (imagen != null) {
                memoria.put(clave, imagen)
                vista.setImageBitmap(imagen)
            }
        }
        vista.setTag(R.id.trabajo_imagen, trabajo)
    }

    /** Copia de la carátula ya bajada (para guardarla junto a una descarga). */
    fun archivoEnCache(context: Context, url: String): File = File(context.cacheDir, "caratulas/${huella(url.substringBefore('?'))}")

    private fun decodificar(context: Context, origen: String, clave: String, anchoMaximo: Int): Bitmap? {
        val bytes = if (origen.startsWith("http")) leerConCache(context, origen) else File(origen).readBytes()
        val limites = BitmapFactory.Options().apply { inJustDecodeBounds = true }
        BitmapFactory.decodeByteArray(bytes, 0, bytes.size, limites)
        var muestra = 1
        while (limites.outWidth / (muestra * 2) >= anchoMaximo) muestra *= 2
        return BitmapFactory.decodeByteArray(bytes, 0, bytes.size, BitmapFactory.Options().apply { inSampleSize = muestra })
    }

    private fun leerConCache(context: Context, url: String): ByteArray {
        val archivo = archivoEnCache(context, url)
        if (archivo.exists()) return archivo.readBytes()
        val conexion = URL(url).openConnection() as HttpURLConnection
        val bytes = try {
            conexion.connectTimeout = 6000
            conexion.readTimeout = 15000
            conexion.inputStream.use { it.readBytes() }
        } finally {
            conexion.disconnect()
        }
        archivo.parentFile?.mkdirs()
        archivo.writeBytes(bytes)
        return bytes
    }

    /** Fondo de color estable para las películas sin carátula. */
    fun degradado(titulo: String): GradientDrawable {
        val tono = (titulo.hashCode() and 0x7fffffff) % 360
        val claro = android.graphics.Color.HSVToColor(floatArrayOf(tono.toFloat(), 0.55f, 0.42f))
        val oscuro = android.graphics.Color.HSVToColor(floatArrayOf(((tono + 40) % 360).toFloat(), 0.65f, 0.16f))
        return GradientDrawable(GradientDrawable.Orientation.TL_BR, intArrayOf(claro, oscuro))
    }
}
