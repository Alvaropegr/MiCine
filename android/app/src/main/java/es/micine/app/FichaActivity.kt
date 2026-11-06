package es.micine.app

import android.annotation.SuppressLint
import android.content.Intent
import android.content.res.ColorStateList
import android.os.Bundle
import android.os.Handler
import android.os.Looper
import android.view.LayoutInflater
import android.view.View
import android.view.ViewGroup
import android.widget.ImageView
import android.widget.TextView
import androidx.appcompat.app.AppCompatActivity
import androidx.core.content.ContextCompat
import androidx.core.view.isVisible
import androidx.lifecycle.lifecycleScope
import androidx.recyclerview.widget.LinearLayoutManager
import androidx.recyclerview.widget.RecyclerView
import com.google.android.material.appbar.MaterialToolbar
import com.google.android.material.button.MaterialButton
import com.google.android.material.progressindicator.CircularProgressIndicator
import com.google.android.material.progressindicator.LinearProgressIndicator
import com.google.android.material.snackbar.Snackbar
import com.google.android.material.tabs.TabLayout
import kotlinx.coroutines.Job
import kotlinx.coroutines.launch
import kotlin.math.ceil

/**
 * Ficha de una serie, como en Netflix: carátula, sinopsis, «Reproducir T1·E3» / «Seguir T1·E3»,
 * temporadas y la lista de episodios (con su fotograma, progreso, versiones y descarga).
 * Sirve también para una película: entonces la lista son sus versiones (4K, 1080p…).
 */
class FichaActivity : AppCompatActivity() {

    private lateinit var sesion: Sesion
    private lateinit var descargas: Descargas
    private lateinit var progresoLocal: ProgresoLocal
    private lateinit var acciones: Acciones
    private lateinit var lista: RecyclerView
    private lateinit var vacio: TextView
    private lateinit var cargando: View
    private val adaptador = AdaptadorFicha()
    private val ids = IdsEstables()
    private var tele = false

    private var idObra = ""
    private var ficha: Ficha? = null

    /** Número de la temporada elegida. */
    private var temporada: Int? = null

    /** Si la obra cambia de id (al identificarla con TMDB), se vuelve a encontrar por sus vídeos. */
    private var rutas: Set<String> = emptySet()
    private var descargadas: Set<String> = emptySet()
    private var infos: Map<String, InfoDescarga> = emptyMap()
    private var cargaEnCurso: Job? = null
    private var enfocada = false

    private val reloj = Handler(Looper.getMainLooper())
    private val tictac = object : Runnable {
        override fun run() {
            refrescarDescargas()
            reloj.postDelayed(this, 1000)
        }
    }

    /** Una fila de la lista: un episodio (con sus versiones) o, en una película, una de sus versiones. */
    private data class FilaFicha(
        val clave: String,
        val titulo: String,
        val detalle: String,
        val numero: String,
        val imagen: String?,
        val avance: Float?,
        val vista: Boolean,
        val versiones: List<Pelicula>,
    )

    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        sesion = Sesion(this)
        descargas = Descargas(this)
        progresoLocal = ProgresoLocal(this)
        setContentView(R.layout.activity_ficha)
        val barra = findViewById<MaterialToolbar>(R.id.barra)
        lista = findViewById(R.id.lista)
        vacio = findViewById(R.id.vacio)
        cargando = findViewById(R.id.cargando)
        tele = esTele(this)
        barra.setNavigationOnClickListener { finish() }
        if (tele) {
            // Con el mando se vuelve con Atrás; margen de seguridad en los bordes (algunas teles los recortan).
            barra.isVisible = false
            lista.setPadding(dp(40), dp(20), dp(40), dp(24))
        }
        acciones = Acciones(
            this, sesion, descargas,
            avisar = ::aviso,
            verDescargas = ::verDescargas,
            alCambiar = {
                refrescarDescargas()
                pintar()
            },
        )
        idObra = intent.getStringExtra(EXTRA_OBRA).orEmpty()
        temporada = savedInstanceState?.getInt("temporada", -1)?.takeIf { it >= 0 }
        lista.layoutManager = LinearLayoutManager(this)
        lista.adapter = adaptador
        // Se enseña enseguida lo que ya había en el catálogo; al volver de MiCine se actualiza.
        CatalogoActual.catalogo?.takeIf { it.ficha(idObra) != null }?.let { mostrar(it) }
        cargando.isVisible = ficha == null
    }

    override fun onResume() {
        super.onResume()
        cargar() // también recoge el progreso al volver del reproductor
        reloj.post(tictac)
    }

    override fun onPause() {
        super.onPause()
        reloj.removeCallbacks(tictac)
    }

    override fun onSaveInstanceState(outState: Bundle) {
        super.onSaveInstanceState(outState)
        temporada?.let { outState.putInt("temporada", it) }
    }

    private fun cargar() {
        if (!sesion.iniciada || cargaEnCurso?.isActive == true) return
        cargaEnCurso = lifecycleScope.launch {
            try {
                mostrar(cargarCatalogo(sesion, progresoLocal))
            } catch (e: ErrorApi) {
                if (e.codigo == 401) {
                    sesion.cerrar()
                    CatalogoActual.catalogo = null
                    startActivity(
                        Intent(this@FichaActivity, LoginActivity::class.java)
                            .putExtra(LoginActivity.EXTRA_MENSAJE, "Tu sesión ha terminado. Vuelve a entrar.")
                            .addFlags(Intent.FLAG_ACTIVITY_NEW_TASK or Intent.FLAG_ACTIVITY_CLEAR_TASK),
                    )
                    finish()
                    return@launch
                }
                // Con la ficha ya en pantalla (sin conexión) se sigue enseñando lo que había.
                if (ficha == null) {
                    vacio.text = e.message
                    vacio.isVisible = true
                }
            } finally {
                cargando.isVisible = false
            }
        }
    }

    private fun mostrar(catalogo: Catalogo) {
        val nueva = catalogo.ficha(idObra) ?: rutas.firstNotNullOfOrNull { catalogo.fichaDe(it) }
        if (nueva == null) {
            if (ficha == null) {
                vacio.text = "Esto ya no está en MiCine."
                vacio.isVisible = true
            }
            return
        }
        ficha = nueva
        idObra = nueva.obra.id
        rutas = nueva.videos.map { it.ruta }.toSet()
        if (nueva.esSerie && nueva.temporadas.none { it.numero == temporada }) {
            temporada = nueva.episodioPrincipal()?.temporada ?: nueva.temporadas.firstOrNull()?.numero
        }
        vacio.isVisible = false
        pintar()
    }

    private fun pintar() {
        val f = ficha ?: return
        descargadas = descargas.rutasCompletas()
        val filas = if (f.esSerie) {
            val episodios = f.temporadas.firstOrNull { it.numero == temporada }?.episodios ?: f.episodios
            episodios.map(::filaEpisodio)
        } else {
            f.videos.map(::filaVersion)
        }
        adaptador.actualizar(f, filas)
        enfocar(10)
    }

    private fun elegirTemporada(numero: Int) {
        if (numero == temporada) return
        temporada = numero
        pintar()
    }

    private fun filaEpisodio(e: Episodio): FilaFicha {
        val video = versionPorDefecto(e.versiones, descargadas) ?: e.versiones.first()
        val p = e.progreso
        val detalle = listOfNotNull(
            e.versiones.singleOrNull()?.version,
            formatoTamano(video.tamano),
            textoProgreso(p, "Visto"),
        ).joinToString(" · ")
        val titulo = if (e.numero > 0) "${e.numero}. ${e.nombre ?: "Episodio ${e.numero}"}" else e.titulo
        return FilaFicha(
            clave = "ep:${e.temporada}:${e.numero}:${e.versiones.first().ruta}",
            titulo = titulo,
            detalle = detalle,
            numero = if (e.numero > 0) e.numero.toString() else "★",
            imagen = e.poster,
            avance = p?.takeIf { it.enCurso }?.fraccion,
            vista = p?.vista == true,
            versiones = e.versiones,
        )
    }

    private fun filaVersion(v: Pelicula): FilaFicha {
        val p = v.progreso
        return FilaFicha(
            clave = "v:${v.ruta}",
            titulo = nombreVersion(v.version),
            detalle = listOfNotNull(formatoTamano(v.tamano), v.ruta.substringAfterLast('/'), textoProgreso(p, "Vista")).joinToString(" · "),
            numero = nombreVersion(v.version).take(5),
            imagen = v.poster,
            avance = p?.takeIf { it.enCurso }?.fraccion,
            vista = p?.vista == true,
            versiones = listOf(v),
        )
    }

    /** [visto]: «Visto» (episodio) o «Vista» (película). */
    private fun textoProgreso(p: Progreso?, visto: String): String? = when {
        p == null -> null
        p.vista -> visto
        p.enCurso -> quedan(p)
        else -> null
    }

    private fun quedan(p: Progreso): String {
        if (p.duracion <= 0) return "Vas por ${formatoTiempo((p.segundos * 1000).toLong())}"
        val minutos = ceil((p.duracion - p.segundos) / 60).toInt().coerceAtLeast(1)
        return "Quedan $minutos min"
    }

    /** Lo que pone el botón grande: el episodio por el que se va (o el primero) o la película. */
    private fun principal(f: Ficha): Pair<Episodio?, List<Pelicula>> {
        if (!f.esSerie) return null to f.videos
        val episodio = f.episodioPrincipal()
        return episodio to episodio?.versiones.orEmpty()
    }

    private fun reproducirPrincipal() {
        val f = ficha ?: return
        val (_, versiones) = principal(f)
        versionPorDefecto(versiones, descargadas)?.let { acciones.reproducir(it) }
    }

    private fun reproducir(fila: FilaFicha) {
        versionPorDefecto(fila.versiones, descargadas)?.let { acciones.reproducir(it) }
    }

    /** En la tele, al abrir, el mando empieza en «Reproducir». */
    private fun enfocar(intentos: Int) {
        if (!tele || enfocada) return
        val boton = lista.findViewHolderForAdapterPosition(0)?.itemView?.findViewById<View>(R.id.reproducir)
        if (boton != null) {
            enfocada = true
            boton.requestFocus()
        } else if (intentos > 0) {
            lista.postDelayed({ enfocar(intentos - 1) }, 60)
        }
    }

    private fun refrescarDescargas() {
        val nuevas = descargas.todas().associate { it.ruta to descargas.info(it) }
        if (nuevas == infos) return
        val terminadas = nuevas.filterValues { it.estado == EstadoDescarga.COMPLETA }.keys != infos.filterValues { it.estado == EstadoDescarga.COMPLETA }.keys
        infos = nuevas
        // Si acaba alguna descarga cambia la versión que se pone por defecto: se repinta todo.
        if (terminadas) pintar() else adaptador.refrescarDescargas()
    }

    private fun verDescargas() {
        startActivity(
            Intent(this, MainActivity::class.java)
                .addFlags(Intent.FLAG_ACTIVITY_CLEAR_TOP or Intent.FLAG_ACTIVITY_SINGLE_TOP)
                .putExtra(MainActivity.EXTRA_VER_DESCARGAS, true),
        )
        finish()
    }

    private fun aviso(texto: String, boton: String? = null, accion: (() -> Unit)? = null) {
        val barra = Snackbar.make(lista, texto, Snackbar.LENGTH_LONG)
        if (boton != null && accion != null) barra.setAction(boton) { accion() }
        barra.show()
    }

    // ---------- lista ----------

    private inner class Cabecera(vista: View) : RecyclerView.ViewHolder(vista) {
        private val poster: ImageView = vista.findViewById(R.id.poster)
        private val sinPoster: View = vista.findViewById(R.id.sinPoster)
        private val inicial: TextView = vista.findViewById(R.id.inicial)
        private val titulo: TextView = vista.findViewById(R.id.titulo)
        private val meta: TextView = vista.findViewById(R.id.meta)
        private val estado: TextView = vista.findViewById(R.id.estado)
        private val sinopsis: TextView = vista.findViewById(R.id.sinopsis)
        private val botonReproducir: MaterialButton = vista.findViewById(R.id.reproducir)
        private val botonVersiones: MaterialButton = vista.findViewById(R.id.versiones)
        private val temporadas: TabLayout = vista.findViewById(R.id.temporadas)
        private val rotulo: TextView = vista.findViewById(R.id.rotulo)
        private var temporadasPintadas: List<Int>? = null
        private var pintando = false
        private val lineasSinopsis = sinopsis.maxLines

        init {
            temporadas.addOnTabSelectedListener(object : TabLayout.OnTabSelectedListener {
                override fun onTabSelected(tab: TabLayout.Tab) {
                    if (!pintando) (tab.tag as? Int)?.let { elegirTemporada(it) }
                }

                override fun onTabUnselected(tab: TabLayout.Tab) = Unit
                override fun onTabReselected(tab: TabLayout.Tab) = Unit
            })
            // En el móvil, tocar la sinopsis la abre entera (en la tele no se puede elegir: no estorba al mando).
            if (!tele) {
                sinopsis.setOnClickListener {
                    sinopsis.maxLines = if (sinopsis.maxLines == lineasSinopsis) Int.MAX_VALUE else lineasSinopsis
                }
            }
            botonReproducir.setOnClickListener { reproducirPrincipal() }
        }

        fun pintar(f: Ficha) {
            val obra = f.obra
            titulo.text = obra.titulo
            sinPoster.background = Imagenes.degradado(obra.titulo)
            inicial.text = obra.titulo.trim().take(1).uppercase()
            Imagenes.cargar(poster, f.poster?.let { sesion.api().absoluta(it) }, anchoMaximo = 400)
            sinopsis.text = obra.sinopsis
            sinopsis.isVisible = obra.sinopsis != null
            val tipo = when {
                obra.anime -> "Anime"
                f.esSerie -> "Serie"
                else -> "Película"
            }
            val numeros = f.temporadas.map { it.numero }
            meta.text = if (f.esSerie) {
                val reales = numeros.count { it > 0 }
                listOfNotNull(
                    obra.anio?.toString(), tipo,
                    if (reales > 1) "$reales temporadas" else if (f.episodios.size == 1) "1 episodio" else "${f.episodios.size} episodios",
                ).joinToString(" · ")
            } else {
                listOfNotNull(obra.anio?.toString(), tipo, if (f.videos.size > 1) "${f.videos.size} versiones" else f.videos.first().version)
                    .joinToString(" · ")
            }

            // Botón grande: «Seguir T1·E3» si está a medias, si no «Reproducir T1·E4» (el siguiente o el primero).
            val (episodio, versiones) = principal(f)
            val video = versionPorDefecto(versiones, descargadas)
            val enCurso = video?.progreso?.enCurso == true
            botonReproducir.text = when {
                episodio != null -> "${if (enCurso) "Seguir" else "Reproducir"} ${episodio.etiqueta}"
                enCurso -> "Seguir"
                else -> "Reproducir"
            }
            botonReproducir.isEnabled = video != null
            val textoEstado = when {
                episodio != null -> listOfNotNull(episodio.titulo, video?.progreso?.takeIf { enCurso }?.let { quedan(it) }).joinToString(" · ")
                enCurso -> listOfNotNull(video?.version, quedan(video!!.progreso!!)).joinToString(" · ")
                else -> null
            }
            estado.text = textoEstado
            estado.isVisible = textoEstado != null
            botonVersiones.isVisible = versiones.size > 1
            botonVersiones.setOnClickListener {
                acciones.elegirVersion(episodio?.let { "${obra.titulo} · ${it.etiqueta}" } ?: obra.titulo, versiones) { acciones.reproducir(it) }
            }

            pintando = true
            if (numeros != temporadasPintadas) {
                temporadasPintadas = numeros
                temporadas.removeAllTabs()
                // Como las pestañas de la pantalla principal: con el mando se cambia de temporada con OK.
                for (t in f.temporadas) temporadas.addTab(temporadas.newTab().setText(t.nombre).setTag(t.numero), false)
            }
            (0 until temporadas.tabCount).mapNotNull { temporadas.getTabAt(it) }
                .firstOrNull { it.tag == temporada }?.takeIf { !it.isSelected }?.select()
            pintando = false
            temporadas.isVisible = f.esSerie && numeros.size > 1
            rotulo.text = when {
                !f.esSerie -> if (f.videos.size > 1) "Versiones" else "Archivo"
                numeros.size > 1 -> ""
                else -> "Episodios"
            }
            rotulo.isVisible = rotulo.text.isNotEmpty()
        }
    }

    private inner class CeldaEpisodio(vista: View) : RecyclerView.ViewHolder(vista) {
        private val fondo: View = vista.findViewById(R.id.fondoMiniatura)
        private val numero: TextView = vista.findViewById(R.id.numero)
        private val imagen: ImageView = vista.findViewById(R.id.imagen)
        private val chapaVista: View = vista.findViewById(R.id.vista)
        private val progreso: LinearProgressIndicator = vista.findViewById(R.id.progreso)
        private val titulo: TextView = vista.findViewById(R.id.titulo)
        private val detalle: TextView = vista.findViewById(R.id.detalle)
        private val botonVersiones: MaterialButton = vista.findViewById(R.id.botonVersiones)
        private val botonDescarga: View = vista.findViewById(R.id.botonDescarga)
        private val iconoDescarga: ImageView = vista.findViewById(R.id.iconoDescarga)
        private val progresoDescarga: CircularProgressIndicator = vista.findViewById(R.id.progresoDescarga)

        init {
            if (tele) {
                // Desde el sofá: fotograma más grande.
                vista.findViewById<View>(R.id.miniatura).layoutParams.apply {
                    width = dp(176)
                    height = dp(99)
                }
            }
        }

        fun pintar(fila: FilaFicha) {
            titulo.text = fila.titulo
            detalle.text = fila.detalle
            fondo.background = Imagenes.degradado(fila.titulo)
            numero.text = fila.numero
            Imagenes.cargar(imagen, fila.imagen?.let { sesion.api().absoluta(it) }, anchoMaximo = 400)
            chapaVista.isVisible = fila.vista
            progreso.isVisible = fila.avance != null
            progreso.progress = ((fila.avance ?: 0f) * 1000).toInt()
            itemView.setOnClickListener { reproducir(fila) }
            itemView.contentDescription = "Ver ${fila.titulo}"
            val varias = fila.versiones.size > 1
            botonVersiones.isVisible = varias
            botonVersiones.text = "${fila.versiones.size} versiones"
            botonVersiones.setOnClickListener {
                acciones.elegirVersion(fila.titulo, fila.versiones) { acciones.reproducir(it) }
            }
            itemView.nextFocusRightId = if (varias) R.id.botonVersiones else R.id.botonDescarga
            botonDescarga.setOnClickListener { acciones.pulsarDescarga(fila.versiones) }
            pintarDescarga(fila)
        }

        fun pintarDescarga(fila: FilaFicha) {
            val info = fila.versiones.firstNotNullOfOrNull { infos[it.ruta] }
            val completa = info?.estado == EstadoDescarga.COMPLETA
            iconoDescarga.isVisible = info?.activa != true
            iconoDescarga.setImageResource(if (completa) R.drawable.ic_descargada else R.drawable.ic_descargar)
            iconoDescarga.imageTintList = ColorStateList.valueOf(
                ContextCompat.getColor(this@FichaActivity, if (completa) R.color.exito else android.R.color.white),
            )
            progresoDescarga.isVisible = info?.activa == true
            if (info?.activa == true) progresoDescarga.setProgressCompat((info.fraccion * 100).toInt(), true)
            botonDescarga.contentDescription = when {
                completa -> "Descargado"
                info?.activa == true -> "Descargando; toca para cancelar"
                else -> "Descargar para ver sin conexión"
            }
        }
    }

    /** La cabecera (posición 0) y debajo los episodios de la temporada elegida (o las versiones). */
    private inner class AdaptadorFicha : RecyclerView.Adapter<RecyclerView.ViewHolder>() {
        private var fichaPintada: Ficha? = null
        private var filas: List<FilaFicha> = emptyList()

        init {
            setHasStableIds(true)
        }

        @SuppressLint("NotifyDataSetChanged")
        fun actualizar(f: Ficha, nuevas: List<FilaFicha>) {
            fichaPintada = f
            filas = nuevas
            notifyDataSetChanged()
        }

        fun refrescarDescargas() = notifyItemRangeChanged(1, filas.size, CAMBIO_DESCARGA)

        override fun getItemCount() = if (fichaPintada == null) 0 else filas.size + 1

        override fun getItemId(posicion: Int) = ids.de(if (posicion == 0) "cabecera" else filas[posicion - 1].clave)

        override fun getItemViewType(posicion: Int) = if (posicion == 0) TIPO_CABECERA else TIPO_EPISODIO

        override fun onCreateViewHolder(padre: ViewGroup, tipo: Int): RecyclerView.ViewHolder {
            val inflador = LayoutInflater.from(padre.context)
            return if (tipo == TIPO_CABECERA) Cabecera(inflador.inflate(R.layout.item_cabecera_ficha, padre, false))
            else CeldaEpisodio(inflador.inflate(R.layout.item_episodio, padre, false))
        }

        override fun onBindViewHolder(celda: RecyclerView.ViewHolder, posicion: Int, cambios: MutableList<Any>) {
            if (celda is CeldaEpisodio && cambios.isNotEmpty() && cambios.all { it == CAMBIO_DESCARGA }) celda.pintarDescarga(filas[posicion - 1])
            else onBindViewHolder(celda, posicion)
        }

        override fun onBindViewHolder(celda: RecyclerView.ViewHolder, posicion: Int) {
            when (celda) {
                is Cabecera -> celda.pintar(fichaPintada ?: return)
                is CeldaEpisodio -> celda.pintar(filas[posicion - 1])
            }
        }
    }

    companion object {
        const val EXTRA_OBRA = "obra"
        private const val TIPO_CABECERA = 0
        private const val TIPO_EPISODIO = 1
        private const val CAMBIO_DESCARGA = "descarga"
    }
}
