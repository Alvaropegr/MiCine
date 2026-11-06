package es.micine.app

import android.annotation.SuppressLint
import android.content.ClipData
import android.content.ClipboardManager
import android.content.Intent
import android.os.Bundle
import android.os.Handler
import android.os.Looper
import android.view.LayoutInflater
import android.view.Menu
import android.view.MenuItem
import android.view.View
import android.view.ViewGroup
import android.widget.ImageButton
import android.widget.ImageView
import android.widget.TextView
import androidx.activity.OnBackPressedCallback
import androidx.appcompat.app.AppCompatActivity
import androidx.appcompat.widget.SearchView
import androidx.core.content.ContextCompat
import androidx.core.view.children
import androidx.core.view.isVisible
import androidx.lifecycle.lifecycleScope
import androidx.recyclerview.widget.GridLayoutManager
import androidx.recyclerview.widget.LinearLayoutManager
import androidx.recyclerview.widget.RecyclerView
import com.google.android.material.appbar.MaterialToolbar
import com.google.android.material.bottomnavigation.BottomNavigationView
import com.google.android.material.chip.Chip
import com.google.android.material.chip.ChipGroup
import com.google.android.material.dialog.MaterialAlertDialogBuilder
import com.google.android.material.progressindicator.LinearProgressIndicator
import com.google.android.material.snackbar.Snackbar
import com.google.android.material.tabs.TabLayout
import kotlinx.coroutines.Job
import kotlinx.coroutines.launch
import java.io.File
import kotlin.math.roundToInt

class MainActivity : AppCompatActivity() {

    private lateinit var sesion: Sesion
    private lateinit var descargas: Descargas
    private lateinit var progresoLocal: ProgresoLocal
    private lateinit var acciones: Acciones
    private lateinit var listaPeliculas: RecyclerView
    private lateinit var listaDescargas: RecyclerView
    private lateinit var vacio: TextView
    private lateinit var cargando: View
    private lateinit var navegacion: BottomNavigationView
    private lateinit var pestanas: TabLayout
    private lateinit var barraSecciones: View
    private lateinit var chipsSecciones: ChipGroup

    private lateinit var adaptadorCatalogo: AdaptadorCatalogo
    private val adaptadorDescargas = AdaptadorDescargas()
    private var catalogo: Catalogo? = null
    private var infos: Map<String, InfoDescarga> = emptyMap()
    private var filtro = ""
    private var buscando = false
    private var pestana = R.id.nav_peliculas

    /** Sección elegida (Películas, Series…); null = Inicio. */
    private var seccion: String? = null
    private var seccionesPintadas: List<Seccion>? = null
    private var cambiandoSecciones = false
    private var ultimoError: String? = null
    private var cargaEnCurso: Job? = null

    /** Sin sesión pero con películas descargadas: solo se enseñan las descargas. */
    private var soloDescargas = false

    /** En la tele (Fire TV) todo se maneja con el mando. */
    private var tele = false
    private var enfoqueInicial = false

    private val ids = IdsEstables()

    private val reloj = Handler(Looper.getMainLooper())
    private val tictac = object : Runnable {
        override fun run() {
            refrescarDescargas()
            reloj.postDelayed(this, 1000)
        }
    }

    /** Atrás: de una sección se vuelve a Inicio y de Descargas al catálogo; desde Inicio se sale. */
    private val atras = object : OnBackPressedCallback(false) {
        override fun handleOnBackPressed() {
            if (pestana == R.id.nav_descargas) {
                elegirPestana(R.id.nav_peliculas)
            } else {
                elegirSeccion(null)
            }
        }
    }

    private val manejo = object : ManejoTarjetas {
        override fun tocar(tarjeta: Tarjeta) {
            val video = tarjeta.video
            if (video != null) acciones.reproducir(video) else tarjeta.obra?.let { abrirFicha(it) }
        }

        override fun mantener(tarjeta: Tarjeta): Boolean {
            val obra = tarjeta.obra ?: return false
            abrirFicha(obra)
            return true
        }

        override fun descargar(tarjeta: Tarjeta) = acciones.pulsarDescarga(tarjeta.versiones)

        override fun infoDescarga(tarjeta: Tarjeta): InfoDescarga? =
            tarjeta.versiones.firstNotNullOfOrNull { infos[it.ruta] }

        override fun urlImagen(ruta: String): String = sesion.api().absoluta(ruta)
    }

    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        sesion = Sesion(this)
        descargas = Descargas(this)
        progresoLocal = ProgresoLocal(this)
        soloDescargas = !sesion.iniciada
        if (soloDescargas && !intent.getBooleanExtra(EXTRA_SIN_CONEXION, false)) {
            irALogin(null)
            return
        }

        setContentView(R.layout.activity_main)
        setSupportActionBar(findViewById<MaterialToolbar>(R.id.barra))
        supportActionBar?.subtitle = if (soloDescargas) "Sin conexión" else sesion.usuario
        listaPeliculas = findViewById(R.id.listaPeliculas)
        listaDescargas = findViewById(R.id.listaDescargas)
        vacio = findViewById(R.id.vacio)
        cargando = findViewById(R.id.cargando)
        navegacion = findViewById(R.id.navegacion)
        pestanas = findViewById(R.id.pestanas)
        barraSecciones = findViewById(R.id.barraSecciones)
        chipsSecciones = findViewById(R.id.chipsSecciones)
        tele = esTele(this)
        acciones = Acciones(
            this, sesion, descargas,
            avisar = ::aviso,
            verDescargas = { elegirPestana(R.id.nav_descargas) },
            alCambiar = {
                refrescarDescargas()
                pintar()
            },
        )

        val anchoDp = resources.displayMetrics.widthPixels / resources.displayMetrics.density
        val columnas: Int
        val anchoFilaDp: Float
        if (tele) {
            adaptarATele()
            columnas = ((anchoDp - 96) / 120).toInt().coerceAtLeast(4)
            anchoFilaDp = (anchoDp - 96 - 16) / 6.5f
        } else {
            columnas = (anchoDp / 130).toInt().coerceAtLeast(2)
            anchoFilaDp = ((anchoDp - 16) / 3.3f).coerceAtMost(170f)
        }
        adaptadorCatalogo = AdaptadorCatalogo(manejo, (anchoFilaDp * resources.displayMetrics.density).roundToInt())
        listaPeliculas.layoutManager = GridLayoutManager(this, columnas).apply {
            spanSizeLookup = object : GridLayoutManager.SpanSizeLookup() {
                override fun getSpanSize(posicion: Int) = if (adaptadorCatalogo.ocupaTodoElAncho(posicion)) columnas else 1
            }
        }
        listaPeliculas.adapter = adaptadorCatalogo
        listaDescargas.layoutManager = LinearLayoutManager(this)
        listaDescargas.adapter = adaptadorDescargas

        navegacion.setOnItemSelectedListener {
            pestana = it.itemId
            mostrarPestana()
            true
        }
        // Tocar «Inicio» estando ya en el catálogo: vuelve a Inicio (sin sección) y arriba del todo.
        navegacion.setOnItemReselectedListener { if (it.itemId == R.id.nav_peliculas) elegirSeccion(null) }
        chipsSecciones.setOnCheckedStateChangeListener { grupo, elegidos ->
            if (cambiandoSecciones) return@setOnCheckedStateChangeListener
            seccion = elegidos.firstOrNull()?.let { grupo.findViewById<View>(it)?.tag as? String }
            alCambiarSeccion()
        }
        navegacion.isVisible = !soloDescargas && !tele
        pestanas.isVisible = !soloDescargas && tele
        onBackPressedDispatcher.addCallback(this, atras)

        if (soloDescargas) {
            pestana = R.id.nav_descargas
            mostrarPestana()
        } else {
            seccion = savedInstanceState?.getString("seccion")
            catalogo = CatalogoActual.catalogo // al girar la pantalla se ve enseguida lo que ya había
            pintarSecciones()
            elegirPestana(savedInstanceState?.getInt("pestana") ?: R.id.nav_peliculas)
            mostrarPestana()
        }
    }

    override fun onNewIntent(intent: Intent) {
        super.onNewIntent(intent)
        if (intent.getBooleanExtra(EXTRA_VER_DESCARGAS, false)) elegirPestana(R.id.nav_descargas)
    }

    /** Tele: margen de seguridad en los bordes (algunas recortan la imagen) y pestañas arriba. */
    private fun adaptarATele() {
        val lado = dp(40)
        val borde = dp(20)
        (findViewById<View>(R.id.barra).layoutParams as ViewGroup.MarginLayoutParams).setMargins(lado, borde, lado, 0)
        (pestanas.layoutParams as ViewGroup.MarginLayoutParams).setMargins(lado, 0, lado, dp(4))
        (findViewById<View>(R.id.contenido).layoutParams as ViewGroup.MarginLayoutParams).setMargins(lado, 0, lado, borde)
        pestanas.addOnTabSelectedListener(object : TabLayout.OnTabSelectedListener {
            override fun onTabSelected(tab: TabLayout.Tab) {
                if (cambiandoSecciones) return
                val destino = tab.tag as? Destino ?: return
                pestana = destino.pestana
                if (destino.pestana == R.id.nav_peliculas) {
                    seccion = destino.seccion
                    alCambiarSeccion()
                } else {
                    mostrarPestana()
                }
            }

            override fun onTabUnselected(tab: TabLayout.Tab) = Unit
            override fun onTabReselected(tab: TabLayout.Tab) = Unit
        })
    }

    /** Cada pestaña de la tele: Inicio, una por sección y Descargas. */
    private data class Destino(val pestana: Int, val seccion: String?)

    /** Las secciones que se pueden elegir (con una sola no hace falta elegir: Inicio ya es esa sección). */
    private fun seccionesVisibles(): List<Seccion> =
        catalogo?.takeIf { it.agrupado }?.secciones?.takeIf { it.size > 1 }.orEmpty()

    /** Pestañas de la tele o chips del móvil según las secciones que haya. */
    private fun pintarSecciones() {
        val lista = seccionesVisibles()
        if (catalogo != null && seccion != null && lista.none { it.id == seccion }) seccion = null
        cambiandoSecciones = true
        if (tele) {
            if (lista != seccionesPintadas || pestanas.tabCount == 0) {
                pestanas.removeAllTabs()
                pestanas.addTab(pestanas.newTab().setText("Inicio").setTag(Destino(R.id.nav_peliculas, null)), false)
                lista.forEach { pestanas.addTab(pestanas.newTab().setText(it.titulo).setTag(Destino(R.id.nav_peliculas, it.id)), false) }
                pestanas.addTab(pestanas.newTab().setText("Descargas").setTag(Destino(R.id.nav_descargas, null)), false)
            }
            val actual = Destino(pestana, if (pestana == R.id.nav_peliculas) seccion else null)
            (0 until pestanas.tabCount).mapNotNull { pestanas.getTabAt(it) }.firstOrNull { it.tag == actual }
                ?.takeIf { !it.isSelected }?.select()
        } else {
            if (lista != seccionesPintadas) {
                chipsSecciones.removeAllViews()
                for (s in lista) {
                    val chip = layoutInflater.inflate(R.layout.item_chip, chipsSecciones, false) as Chip
                    chip.id = View.generateViewId()
                    chip.text = s.titulo
                    chip.tag = s.id
                    chipsSecciones.addView(chip)
                }
            }
            val elegido = chipsSecciones.children.firstOrNull { it.tag == seccion }
            if (elegido != null) chipsSecciones.check(elegido.id) else chipsSecciones.clearCheck()
            barraSecciones.isVisible = lista.isNotEmpty() && pestana == R.id.nav_peliculas
        }
        seccionesPintadas = lista
        cambiandoSecciones = false
        actualizarAtras()
    }

    private fun elegirSeccion(id: String?) {
        seccion = id
        pintarSecciones()
        alCambiarSeccion()
    }

    private fun alCambiarSeccion() {
        if (pestana != R.id.nav_peliculas) {
            pestana = R.id.nav_peliculas
            if (!tele) navegacion.selectedItemId = R.id.nav_peliculas
        }
        mostrarPestana()
        listaPeliculas.scrollToPosition(0)
    }

    /** Cambia de pestaña como si se hubiera pulsado (abajo en el móvil, arriba en la tele). */
    private fun elegirPestana(id: Int) {
        if (tele) {
            val destino = Destino(id, if (id == R.id.nav_peliculas) seccion else null)
            val tab = (0 until pestanas.tabCount).mapNotNull { pestanas.getTabAt(it) }.firstOrNull { it.tag == destino }
            if (tab != null && !tab.isSelected) tab.select() else if (pestana != id) {
                pestana = id
                mostrarPestana()
            }
        } else if (navegacion.selectedItemId != id) {
            navegacion.selectedItemId = id
        } else if (pestana != id) {
            // Ya marcada abajo: volver a marcarla contaría como tocarla otra vez (y volvería a Inicio).
            pestana = id
            mostrarPestana()
        }
    }

    override fun onResume() {
        super.onResume()
        if (!soloDescargas) cargar() // también recoge el progreso al volver del reproductor
        reloj.post(tictac)
    }

    override fun onPause() {
        super.onPause()
        reloj.removeCallbacks(tictac)
    }

    override fun onSaveInstanceState(outState: Bundle) {
        super.onSaveInstanceState(outState)
        outState.putInt("pestana", pestana)
        outState.putString("seccion", seccion)
    }

    override fun onCreateOptionsMenu(menu: Menu): Boolean {
        menuInflater.inflate(R.menu.principal, menu)
        val elementoBuscar = menu.findItem(R.id.accion_buscar)
        val buscador = elementoBuscar.actionView as SearchView
        buscador.queryHint = "Buscar película o serie…"
        buscador.setOnQueryTextListener(object : SearchView.OnQueryTextListener {
            override fun onQueryTextSubmit(texto: String?): Boolean {
                buscador.clearFocus()
                return true
            }

            override fun onQueryTextChange(texto: String?): Boolean {
                filtro = texto.orEmpty()
                pintar()
                return true
            }
        })
        // Con el buscador abierto, Atrás lo cierra (y no cambia de sección).
        elementoBuscar.setOnActionExpandListener(object : MenuItem.OnActionExpandListener {
            override fun onMenuItemActionExpand(item: MenuItem): Boolean {
                buscando = true
                actualizarAtras()
                return true
            }

            override fun onMenuItemActionCollapse(item: MenuItem): Boolean {
                buscando = false
                actualizarAtras()
                return true
            }
        })
        menu.findItem(R.id.accion_actualizar).isVisible = !soloDescargas
        menu.findItem(R.id.accion_salir).title = if (soloDescargas) "Entrar en MiCine" else "Cerrar sesión"
        return true
    }

    override fun onOptionsItemSelected(item: MenuItem): Boolean = when (item.itemId) {
        R.id.accion_actualizar -> {
            cargar()
            true
        }
        R.id.accion_salir -> {
            if (soloDescargas) irALogin(null) else confirmarSalir()
            true
        }
        R.id.accion_tele -> {
            instalarEnLaTele()
            true
        }
        else -> super.onOptionsItemSelected(item)
    }

    /** La dirección que hay que escribir en la app Downloader de la Fire TV. */
    private fun instalarEnLaTele() {
        // La corta («192.168.1.20:8080/tele») la da el propio MiCine y lleva a la última versión en GitHub.
        val corta = sesion.servidor?.let { "${Api(it).base}/tele" }
        val url = corta ?: DESCARGA_FIRE_TV
        val mensaje = buildString {
            append("En la Fire TV, instala «Downloader» (gratis, en la tienda de Amazon), ábrela y escribe esta dirección:\n\n")
            append(url)
            if (corta != null) append("\n\nSi la tele no está en tu casa, sirve esta otra:\n$DESCARGA_FIRE_TV")
        }
        MaterialAlertDialogBuilder(this)
            .setTitle("Instalar MiCine en la tele")
            .setMessage(mensaje)
            .setPositiveButton("Copiar dirección") { _, _ ->
                getSystemService(ClipboardManager::class.java)?.setPrimaryClip(ClipData.newPlainText("MiCine", url))
                aviso("Dirección copiada")
            }
            .setNegativeButton("Cerrar", null)
            .show()
    }

    // ---------- datos ----------

    private fun cargar() {
        if (cargaEnCurso?.isActive == true) return
        cargando.isVisible = catalogo == null && pestana == R.id.nav_peliculas
        vacio.isVisible = false
        cargaEnCurso = lifecycleScope.launch {
            try {
                catalogo = cargarCatalogo(sesion, progresoLocal)
                ultimoError = null
            } catch (e: ErrorApi) {
                if (e.codigo == 401) {
                    sesion.cerrar()
                    CatalogoActual.catalogo = null
                    irALogin("Tu sesión ha terminado. Vuelve a entrar.")
                    return@launch
                }
                ultimoError = e.message
                if (catalogo?.peliculas.isNullOrEmpty() && descargas.todas().isNotEmpty() && pestana != R.id.nav_descargas) {
                    elegirPestana(R.id.nav_descargas)
                    aviso("Sin conexión con MiCine. Aquí tienes tus descargas.")
                } else {
                    aviso(e.message ?: "No se pudo actualizar")
                }
            } finally {
                cargando.isVisible = false
                pintarSecciones()
                pintar()
            }
        }
    }

    private fun mostrarPestana() {
        listaPeliculas.isVisible = pestana == R.id.nav_peliculas
        listaDescargas.isVisible = pestana == R.id.nav_descargas
        barraSecciones.isVisible = !tele && pestana == R.id.nav_peliculas && seccionesVisibles().isNotEmpty()
        cargando.isVisible = cargando.isVisible && pestana == R.id.nav_peliculas
        refrescarDescargas()
        pintar()
        actualizarAtras()
    }

    private fun actualizarAtras() {
        atras.isEnabled = !soloDescargas && !buscando &&
            (pestana == R.id.nav_descargas || (seccion != null && seccionesVisibles().isNotEmpty()))
    }

    private fun pintar() {
        val consulta = normalizar(filtro.trim())
        val mensaje = if (pestana == R.id.nav_peliculas) pintarCatalogo(consulta) else pintarDescargas(consulta)
        vacio.text = mensaje
        vacio.isVisible = mensaje.isNotEmpty()
        enfocarInicio()
    }

    /** Devuelve el mensaje que se enseña si no hay nada que ver. */
    private fun pintarCatalogo(consulta: String): String {
        val catalogo = catalogo
        val elementos = mutableListOf<Elemento>()
        var mensaje = ""
        if (catalogo == null || !catalogo.agrupado) {
            // Servidor antiguo (o aún sin datos): la lista plana de siempre.
            val todas = catalogo?.peliculas.orEmpty()
            val lista = todas.filter { normalizar("${it.titulo} ${it.ruta}").contains(consulta) }
            lista.mapTo(elementos) { ElementoTarjeta(tarjetaPlana(it)) }
            mensaje = when {
                cargando.isVisible -> ""
                todas.isEmpty() && ultimoError != null -> ultimoError!!
                todas.isEmpty() -> "Todavía no hay películas en MiCine.\n\nCopia tus películas y series a las carpetas de MiCine en el ordenador."
                lista.isEmpty() -> "Ninguna película coincide con «$filtro»."
                else -> ""
            }
        } else {
            val descargadas = descargas.rutasCompletas()
            val elegida = seccionesVisibles().firstOrNull { it.id == seccion }
            when {
                consulta.isNotEmpty() -> {
                    val lista = catalogo.buscar(consulta, elegida?.id, descargadas)
                    lista.mapTo(elementos) { ElementoTarjeta(it) }
                    if (lista.isEmpty()) mensaje = "Nada coincide con «$filtro»" + (elegida?.let { " en ${it.titulo}" } ?: "") + "."
                }
                elegida != null || catalogo.secciones.size == 1 -> {
                    val pagina = catalogo.paginaSeccion(elegida ?: catalogo.secciones.first(), descargadas)
                    pagina.filas.mapTo(elementos) { ElementoFila(it) }
                    if (pagina.filas.isNotEmpty() && pagina.todas.isNotEmpty()) elementos += ElementoCabecera(pagina.tituloTodas)
                    pagina.todas.mapTo(elementos) { ElementoTarjeta(it) }
                }
                else -> catalogo.filasInicio(descargadas).mapTo(elementos) { ElementoFila(it) }
            }
        }
        adaptadorCatalogo.actualizar(elementos)
        return mensaje
    }

    private fun pintarDescargas(consulta: String): String {
        val lista = descargas.todas().filter { normalizar(it.titulo).contains(consulta) }
        adaptadorDescargas.actualizar(lista)
        return when {
            lista.isEmpty() && consulta.isNotEmpty() -> "Ninguna descarga coincide con «$filtro»."
            lista.isEmpty() -> "Aún no has descargado nada.\n\nToca el botón de descarga de una película o de un episodio para guardarlo en el móvil y verlo sin conexión."
            else -> ""
        }
    }

    /** En la tele, al abrir, el mando empieza en la primera carátula (o en la primera descarga). */
    private fun enfocarInicio() {
        if (!tele || enfoqueInicial || cargando.isVisible) return
        val lista = if (pestana == R.id.nav_peliculas) listaPeliculas else listaDescargas
        if ((lista.adapter?.itemCount ?: 0) == 0) return
        enfoqueInicial = true
        enfocarPrimero(lista, 10)
    }

    private fun enfocarPrimero(lista: RecyclerView, intentos: Int) {
        // En una fila, la primera carátula de la fila (es la primera «tarjeta» que se encuentra dentro).
        val fila = lista.findViewHolderForAdapterPosition(0)?.itemView
        val destino = fila?.findViewById<View>(R.id.tarjeta) ?: fila?.takeIf { it.isFocusable }
        if (destino != null) {
            destino.requestFocus()
        } else if (intentos > 0) {
            lista.postDelayed({ enfocarPrimero(lista, intentos - 1) }, 60)
        }
    }

    private fun refrescarDescargas() {
        if (!::listaPeliculas.isInitialized) return
        val nuevas = descargas.todas().associate { it.ruta to descargas.info(it) }
        if (nuevas == infos) return
        val otras = nuevas.keys != infos.keys
        infos = nuevas
        adaptadorCatalogo.refrescarDescargas()
        adaptadorDescargas.actualizarInfos(otras)
    }

    // ---------- acciones ----------

    private fun abrirFicha(obra: String) {
        startActivity(Intent(this, FichaActivity::class.java).putExtra(FichaActivity.EXTRA_OBRA, obra))
    }

    private fun confirmarSalir() {
        MaterialAlertDialogBuilder(this)
            .setTitle("¿Cerrar sesión?")
            .setMessage("Tus películas descargadas seguirán en el móvil.")
            .setPositiveButton("Cerrar sesión") { _, _ ->
                val api = sesion.api()
                ambitoApp.launch { runCatching { api.logout() } }
                sesion.cerrar()
                CatalogoActual.catalogo = null
                irALogin(null)
            }
            .setNegativeButton("Cancelar", null)
            .show()
    }

    private fun irALogin(mensaje: String?) {
        startActivity(Intent(this, LoginActivity::class.java).putExtra(LoginActivity.EXTRA_MENSAJE, mensaje))
        finish()
    }

    private fun aviso(texto: String, boton: String? = null, accion: (() -> Unit)? = null) {
        val barra = Snackbar.make(navegacion, texto, Snackbar.LENGTH_LONG).setAnchorView(navegacion.takeIf { it.isVisible })
        if (boton != null && accion != null) barra.setAction(boton) { accion() }
        barra.show()
    }

    // ---------- descargas ----------

    private inner class AdaptadorDescargas : RecyclerView.Adapter<AdaptadorDescargas.Fila>() {
        private var lista: List<Descarga> = emptyList()

        init {
            setHasStableIds(true)
        }

        inner class Fila(vista: View) : RecyclerView.ViewHolder(vista) {
            val fondoPoster: View = vista.findViewById(R.id.fondoPoster)
            val inicial: TextView = vista.findViewById(R.id.inicial)
            val poster: ImageView = vista.findViewById(R.id.poster)
            val titulo: TextView = vista.findViewById(R.id.titulo)
            val subtitulo: TextView = vista.findViewById(R.id.subtitulo)
            val estado: TextView = vista.findViewById(R.id.estado)
            val barra: LinearProgressIndicator = vista.findViewById(R.id.barra)
            val borrar: ImageButton = vista.findViewById(R.id.borrar)
        }

        @SuppressLint("NotifyDataSetChanged")
        fun actualizar(nueva: List<Descarga>) {
            lista = nueva
            notifyDataSetChanged()
        }

        /** [otras]: ha aparecido o desaparecido alguna descarga (hay que rehacer la lista). */
        fun actualizarInfos(otras: Boolean) {
            if (otras && pestana == R.id.nav_descargas) pintar()
            else notifyItemRangeChanged(0, itemCount, CAMBIO_DESCARGA)
        }

        override fun getItemCount() = lista.size

        override fun getItemId(posicion: Int) = ids.de(lista[posicion].ruta)

        override fun onCreateViewHolder(padre: ViewGroup, tipo: Int) =
            Fila(LayoutInflater.from(padre.context).inflate(R.layout.item_descarga, padre, false))

        override fun onBindViewHolder(fila: Fila, posicion: Int, cambios: MutableList<Any>) {
            if (cambios.isNotEmpty() && cambios.all { it == CAMBIO_DESCARGA }) pintarEstado(fila, lista[posicion])
            else onBindViewHolder(fila, posicion)
        }

        override fun onBindViewHolder(fila: Fila, posicion: Int) {
            val d = lista[posicion]
            // Un episodio enseña su serie y, debajo, «T1·E2 · Nombre» (también sin conexión).
            val nombre = d.serie ?: d.titulo
            fila.titulo.text = nombre
            fila.subtitulo.text = d.detalleEpisodio
            fila.subtitulo.isVisible = d.detalleEpisodio != null
            fila.fondoPoster.background = Imagenes.degradado(nombre)
            fila.inicial.text = nombre.trim().take(1).uppercase()
            Imagenes.cargar(fila.poster, d.poster?.takeIf { File(it).exists() }, anchoMaximo = 160)
            fila.itemView.setOnClickListener { acciones.reproducirDescarga(d) }
            fila.borrar.setOnClickListener { acciones.confirmarBorrar(d) }
            pintarEstado(fila, d)
        }

        private fun pintarEstado(fila: Fila, d: Descarga) {
            val info = infos[d.ruta] ?: descargas.info(d)
            val visto = progresoLocal.leer(d.ruta)
            fila.estado.text = when (info.estado) {
                EstadoDescarga.COMPLETA -> buildString {
                    append("En el móvil · ${formatoTamano(info.total)}")
                    if (visto?.vista == true) append(" · Vista")
                    else if (visto != null && visto.segundos > 15) append(" · Vas por ${formatoTiempo((visto.segundos * 1000).toLong())}")
                }
                EstadoDescarga.DESCARGANDO ->
                    "Descargando ${(info.fraccion * 100).toInt()} % · ${formatoTamano(info.bajado)} de ${formatoTamano(info.total)}"
                else -> info.motivo ?: ""
            }
            val color = when (info.estado) {
                EstadoDescarga.COMPLETA -> R.color.exito
                EstadoDescarga.FALLIDA -> R.color.error
                else -> R.color.tenue
            }
            fila.estado.setTextColor(ContextCompat.getColor(this@MainActivity, color))
            fila.barra.isVisible = info.activa
            if (info.activa) fila.barra.setProgressCompat((info.fraccion * 1000).toInt(), true)
        }
    }

    companion object {
        const val EXTRA_SIN_CONEXION = "sin_conexion"

        /** Desde la ficha de una serie: «Ver descargas». */
        const val EXTRA_VER_DESCARGAS = "ver_descargas"
        private const val CAMBIO_DESCARGA = "descarga"
    }
}
