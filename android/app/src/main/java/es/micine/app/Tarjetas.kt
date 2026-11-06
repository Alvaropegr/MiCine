package es.micine.app

import android.annotation.SuppressLint
import android.content.res.ColorStateList
import android.graphics.Color
import android.os.Parcelable
import android.view.LayoutInflater
import android.view.View
import android.view.ViewGroup
import android.widget.ImageView
import android.widget.TextView
import androidx.core.content.ContextCompat
import androidx.core.view.isVisible
import androidx.recyclerview.widget.LinearLayoutManager
import androidx.recyclerview.widget.RecyclerView
import com.google.android.material.card.MaterialCardView
import com.google.android.material.progressindicator.CircularProgressIndicator
import com.google.android.material.progressindicator.LinearProgressIndicator

/** Lo que hace la pantalla con las tarjetas: tocar, mantener pulsado, descargar… */
interface ManejoTarjetas {
    fun tocar(tarjeta: Tarjeta)

    /** Mantener pulsado (o el botón OK del mando un rato): la ficha con la sinopsis y las versiones. */
    fun mantener(tarjeta: Tarjeta): Boolean

    fun descargar(tarjeta: Tarjeta)

    fun infoDescarga(tarjeta: Tarjeta): InfoDescarga?

    fun urlImagen(ruta: String): String
}

/** Identificador fijo de cada elemento de las listas: así el mando no pierde su sitio al recargar. */
class IdsEstables {
    private val ids = HashMap<String, Long>()

    fun de(clave: String): Long = ids.getOrPut(clave) { ids.size.toLong() }
}

/** Carátula elegida con el mando: borde blanco y un poco más grande. */
fun resaltarTarjeta(tarjeta: MaterialCardView, foco: Boolean, animar: Boolean) {
    val contexto = tarjeta.context
    tarjeta.setStrokeColor(ColorStateList.valueOf(if (foco) Color.WHITE else ContextCompat.getColor(contexto, R.color.borde)))
    tarjeta.strokeWidth = contexto.dp(if (foco) 3 else 1)
    val escala = if (foco) 1.06f else 1f
    tarjeta.animate().cancel()
    if (animar) {
        tarjeta.animate().scaleX(escala).scaleY(escala).setDuration(150).start()
    } else {
        tarjeta.scaleX = escala
        tarjeta.scaleY = escala
    }
}

/** Una carátula (item_pelicula): en la cuadrícula o en una fila. */
class CeldaTarjeta(vista: View, private val manejo: ManejoTarjetas) : RecyclerView.ViewHolder(vista) {
    val tarjeta: MaterialCardView = vista.findViewById(R.id.tarjeta)
    private val poster: ImageView = vista.findViewById(R.id.poster)
    private val sinPoster: View = vista.findViewById(R.id.sinPoster)
    private val inicial: TextView = vista.findViewById(R.id.inicial)
    private val nombre: TextView = vista.findViewById(R.id.nombre)
    private val chapaVista: TextView = vista.findViewById(R.id.vista)
    private val progreso: LinearProgressIndicator = vista.findViewById(R.id.progreso)
    private val botonDescarga: View = vista.findViewById(R.id.botonDescarga)
    private val iconoDescarga: ImageView = vista.findViewById(R.id.iconoDescarga)
    private val progresoDescarga: CircularProgressIndicator = vista.findViewById(R.id.progresoDescarga)
    private val titulo: TextView = vista.findViewById(R.id.titulo)
    private val meta: TextView = vista.findViewById(R.id.meta)

    init {
        tarjeta.setOnFocusChangeListener { _, foco -> resaltarTarjeta(tarjeta, foco, animar = true) }
    }

    fun pintar(t: Tarjeta) {
        titulo.text = t.titulo
        meta.text = t.detalle
        sinPoster.background = Imagenes.degradado(t.titulo)
        inicial.text = t.titulo.trim().take(1).uppercase()
        nombre.text = t.titulo
        Imagenes.cargar(poster, t.poster?.let { manejo.urlImagen(it) })
        chapaVista.isVisible = t.vista
        progreso.isVisible = t.avance != null
        progreso.progress = ((t.avance ?: 0f) * 1000).toInt()
        tarjeta.setOnClickListener { manejo.tocar(t) }
        tarjeta.setOnLongClickListener { manejo.mantener(t) }
        resaltarTarjeta(tarjeta, tarjeta.isFocused, animar = false)
        tarjeta.contentDescription = if (t.video != null) "Ver ${t.titulo}" else "Abrir ${t.titulo}"
        botonDescarga.setOnClickListener { manejo.descargar(t) }
        pintarDescarga(t)
    }

    fun pintarDescarga(t: Tarjeta) {
        // Las series se descargan episodio a episodio, desde su ficha.
        botonDescarga.isVisible = t.versiones.isNotEmpty()
        if (t.versiones.isEmpty()) return
        val info = manejo.infoDescarga(t)
        val completa = info?.estado == EstadoDescarga.COMPLETA
        iconoDescarga.isVisible = info?.activa != true
        iconoDescarga.setImageResource(if (completa) R.drawable.ic_descargada else R.drawable.ic_descargar)
        iconoDescarga.imageTintList = ColorStateList.valueOf(
            ContextCompat.getColor(itemView.context, if (completa) R.color.exito else android.R.color.white),
        )
        progresoDescarga.isVisible = info?.activa == true
        if (info?.activa == true) progresoDescarga.setProgressCompat((info.fraccion * 100).toInt(), true)
        botonDescarga.contentDescription = when {
            completa -> "Descargada"
            info?.activa == true -> "Descargando; toca para cancelar"
            else -> "Descargar para ver sin conexión"
        }
    }
}

/** Las carátulas de una fila horizontal. */
class AdaptadorFila(private val manejo: ManejoTarjetas, private val ancho: Int, private val ids: IdsEstables) :
    RecyclerView.Adapter<CeldaTarjeta>() {
    private var lista: List<Tarjeta> = emptyList()

    init {
        setHasStableIds(true)
    }

    @SuppressLint("NotifyDataSetChanged")
    fun actualizar(nueva: List<Tarjeta>) {
        if (nueva == lista) return
        lista = nueva
        notifyDataSetChanged()
    }

    override fun getItemCount() = lista.size

    override fun getItemId(posicion: Int) = ids.de(lista[posicion].clave)

    override fun onCreateViewHolder(padre: ViewGroup, tipo: Int): CeldaTarjeta {
        val vista = LayoutInflater.from(padre.context).inflate(R.layout.item_pelicula, padre, false)
        vista.layoutParams = RecyclerView.LayoutParams(ancho, ViewGroup.LayoutParams.WRAP_CONTENT)
        return CeldaTarjeta(vista, manejo)
    }

    override fun onBindViewHolder(celda: CeldaTarjeta, posicion: Int, cambios: MutableList<Any>) {
        if (cambios.isNotEmpty() && cambios.all { it == CAMBIO_DESCARGA }) celda.pintarDescarga(lista[posicion])
        else onBindViewHolder(celda, posicion)
    }

    override fun onBindViewHolder(celda: CeldaTarjeta, posicion: Int) = celda.pintar(lista[posicion])

    companion object {
        const val CAMBIO_DESCARGA = "descarga"
    }
}

/** Un elemento de la lista principal: una fila de carátulas, un título o una carátula de la cuadrícula. */
sealed interface Elemento {
    val clave: String
}

data class ElementoFila(val fila: Fila) : Elemento {
    override val clave: String get() = "fila:${fila.clave}"
}

data class ElementoCabecera(val texto: String) : Elemento {
    override val clave: String get() = "cabecera:$texto"
}

data class ElementoTarjeta(val tarjeta: Tarjeta) : Elemento {
    override val clave: String get() = tarjeta.clave
}

/**
 * La lista principal (una cuadrícula): filas horizontales como en Netflix, títulos y carátulas sueltas.
 * Las filas y los títulos ocupan todo el ancho.
 */
class AdaptadorCatalogo(private val manejo: ManejoTarjetas, private val anchoFila: Int) :
    RecyclerView.Adapter<RecyclerView.ViewHolder>() {
    private var lista: List<Elemento> = emptyList()
    private val ids = IdsEstables()
    private val idsTarjetas = IdsEstables()
    private val piscina = RecyclerView.RecycledViewPool()

    /** Por dónde iba cada fila al salir de la pantalla, para dejarla igual al volver. */
    private val posicionesFilas = HashMap<String, Parcelable?>()

    init {
        setHasStableIds(true)
    }

    private inner class CeldaFila(vista: View) : RecyclerView.ViewHolder(vista) {
        val titulo: TextView = vista.findViewById(R.id.filaTitulo)
        val lista: RecyclerView = vista.findViewById(R.id.filaLista)
        val adaptador = AdaptadorFila(manejo, anchoFila, idsTarjetas)
        var clave: String? = null

        init {
            lista.layoutManager = LinearLayoutManager(vista.context, LinearLayoutManager.HORIZONTAL, false)
            lista.adapter = adaptador
            lista.setRecycledViewPool(piscina)
            // Sin esto, al recibir datos la fila se queda con el foco y la lista salta hasta ella.
            lista.isFocusable = false
            lista.isFocusableInTouchMode = false
        }
    }

    private class CeldaCabecera(vista: View) : RecyclerView.ViewHolder(vista) {
        val texto: TextView = vista.findViewById(R.id.cabeceraTexto)
    }

    @SuppressLint("NotifyDataSetChanged")
    fun actualizar(nueva: List<Elemento>) {
        if (nueva == lista) return
        lista = nueva
        notifyDataSetChanged()
    }

    /** Se ha bajado algo más de una descarga: solo se repinta el botón. */
    fun refrescarDescargas() = notifyItemRangeChanged(0, itemCount, AdaptadorFila.CAMBIO_DESCARGA)

    fun ocupaTodoElAncho(posicion: Int): Boolean = lista.getOrNull(posicion) !is ElementoTarjeta

    override fun getItemCount() = lista.size

    override fun getItemId(posicion: Int) = ids.de(lista[posicion].clave)

    override fun getItemViewType(posicion: Int) = when (lista[posicion]) {
        is ElementoFila -> TIPO_FILA
        is ElementoCabecera -> TIPO_CABECERA
        is ElementoTarjeta -> TIPO_TARJETA
    }

    override fun onCreateViewHolder(padre: ViewGroup, tipo: Int): RecyclerView.ViewHolder {
        val inflador = LayoutInflater.from(padre.context)
        return when (tipo) {
            TIPO_FILA -> CeldaFila(inflador.inflate(R.layout.item_fila, padre, false))
            TIPO_CABECERA -> CeldaCabecera(inflador.inflate(R.layout.item_cabecera, padre, false))
            else -> CeldaTarjeta(inflador.inflate(R.layout.item_pelicula, padre, false), manejo)
        }
    }

    override fun onBindViewHolder(celda: RecyclerView.ViewHolder, posicion: Int, cambios: MutableList<Any>) {
        if (cambios.isEmpty() || cambios.any { it != AdaptadorFila.CAMBIO_DESCARGA }) {
            onBindViewHolder(celda, posicion)
            return
        }
        when (celda) {
            is CeldaFila -> celda.adaptador.notifyItemRangeChanged(0, celda.adaptador.itemCount, AdaptadorFila.CAMBIO_DESCARGA)
            is CeldaTarjeta -> (lista[posicion] as? ElementoTarjeta)?.let { celda.pintarDescarga(it.tarjeta) }
        }
    }

    override fun onBindViewHolder(celda: RecyclerView.ViewHolder, posicion: Int) {
        when (val elemento = lista[posicion]) {
            is ElementoFila -> {
                celda as CeldaFila
                celda.titulo.text = elemento.fila.titulo
                val otra = celda.clave != elemento.fila.clave
                if (otra) celda.clave?.let { posicionesFilas[it] = celda.lista.layoutManager?.onSaveInstanceState() }
                celda.adaptador.actualizar(elemento.fila.tarjetas)
                if (otra) {
                    celda.clave = elemento.fila.clave
                    val guardada = posicionesFilas[elemento.fila.clave]
                    if (guardada != null) celda.lista.layoutManager?.onRestoreInstanceState(guardada)
                    else celda.lista.scrollToPosition(0)
                }
            }
            is ElementoCabecera -> (celda as CeldaCabecera).texto.text = elemento.texto
            is ElementoTarjeta -> (celda as CeldaTarjeta).pintar(elemento.tarjeta)
        }
    }

    override fun onViewRecycled(celda: RecyclerView.ViewHolder) {
        if (celda is CeldaFila) celda.clave?.let { posicionesFilas[it] = celda.lista.layoutManager?.onSaveInstanceState() }
    }

    companion object {
        private const val TIPO_FILA = 0
        private const val TIPO_CABECERA = 1
        private const val TIPO_TARJETA = 2
    }
}
