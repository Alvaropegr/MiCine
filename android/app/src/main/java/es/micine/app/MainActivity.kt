package es.micine.app

import android.annotation.SuppressLint
import android.content.ClipData
import android.content.ClipboardManager
import android.content.Intent
import android.content.res.ColorStateList
import android.graphics.Color
import android.net.Uri
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
import androidx.appcompat.app.AppCompatActivity
import androidx.appcompat.widget.SearchView
import androidx.core.content.ContextCompat
import androidx.core.view.isVisible
import androidx.lifecycle.lifecycleScope
import androidx.recyclerview.widget.GridLayoutManager
import androidx.recyclerview.widget.LinearLayoutManager
import androidx.recyclerview.widget.RecyclerView
import com.google.android.material.appbar.MaterialToolbar
import com.google.android.material.bottomnavigation.BottomNavigationView
import com.google.android.material.card.MaterialCardView
import com.google.android.material.dialog.MaterialAlertDialogBuilder
import com.google.android.material.progressindicator.CircularProgressIndicator
import com.google.android.material.progressindicator.LinearProgressIndicator
import com.google.android.material.snackbar.Snackbar
import com.google.android.material.tabs.TabLayout
import kotlinx.coroutines.Job
import kotlinx.coroutines.launch
import java.io.File

class MainActivity : AppCompatActivity() {

    private lateinit var sesion: Sesion
    private lateinit var descargas: Descargas
    private lateinit var progresoLocal: ProgresoLocal
    private lateinit var listaPeliculas: RecyclerView
    private lateinit var listaDescargas: RecyclerView
    private lateinit var vacio: TextView
    private lateinit var cargando: View
    private lateinit var navegacion: BottomNavigationView
    private lateinit var pestanas: TabLayout

    private val adaptadorPeliculas = AdaptadorPeliculas()
    private val adaptadorDescargas = AdaptadorDescargas()
    private var peliculas: List<Pelicula> = emptyList()
    private var filtro = ""
    private var pestana = R.id.nav_peliculas
    private var ultimoError: String? = null
    private var cargaEnCurso: Job? = null

    /** Sin sesión pero con películas descargadas: solo se enseñan las descargas. */
    private var soloDescargas = false

    /** En la tele (Fire TV) todo se maneja con el mando. */
    private var tele = false
    private var enfoqueInicial = false

    /** Identificador fijo de cada película en las listas: así el mando no pierde su sitio al recargar. */
    private val ids = HashMap<String, Long>()

    private val reloj = Handler(Looper.getMainLooper())
    private val tictac = object : Runnable {
        override fun run() {
            refrescarDescargas()
            reloj.postDelayed(this, 1000)
        }
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
        tele = esTele(this)

        val anchoDp = resources.displayMetrics.widthPixels / resources.displayMetrics.density
        val columnas = if (tele) {
            adaptarATele()
            ((anchoDp - 96) / 120).toInt().coerceAtLeast(4)
        } else {
            (anchoDp / 130).toInt().coerceAtLeast(2)
        }
        listaPeliculas.layoutManager = GridLayoutManager(this, columnas)
        listaPeliculas.adapter = adaptadorPeliculas
        listaDescargas.layoutManager = LinearLayoutManager(this)
        listaDescargas.adapter = adaptadorDescargas

        navegacion.setOnItemSelectedListener {
            mostrarPestana(it.itemId)
            true
        }
        navegacion.isVisible = !soloDescargas && !tele
        pestanas.isVisible = !soloDescargas && tele
        if (soloDescargas) {
            mostrarPestana(R.id.nav_descargas)
        } else {
            val inicial = savedInstanceState?.getInt("pestana") ?: R.id.nav_peliculas
            elegirPestana(inicial)
            mostrarPestana(inicial)
        }
    }

    /** Tele: margen de seguridad en los bordes (algunas recortan la imagen) y pestañas arriba. */
    private fun adaptarATele() {
        val lado = dp(40)
        val borde = dp(20)
        (findViewById<View>(R.id.barra).layoutParams as ViewGroup.MarginLayoutParams).setMargins(lado, borde, lado, 0)
        (pestanas.layoutParams as ViewGroup.MarginLayoutParams).setMargins(lado, 0, lado, dp(4))
        (findViewById<View>(R.id.contenido).layoutParams as ViewGroup.MarginLayoutParams).setMargins(lado, 0, lado, borde)
        pestanas.addTab(pestanas.newTab().setText("Películas").setId(R.id.nav_peliculas))
        pestanas.addTab(pestanas.newTab().setText("Descargas").setId(R.id.nav_descargas))
        pestanas.addOnTabSelectedListener(object : TabLayout.OnTabSelectedListener {
            override fun onTabSelected(tab: TabLayout.Tab) = mostrarPestana(tab.id)
            override fun onTabUnselected(tab: TabLayout.Tab) = Unit
            override fun onTabReselected(tab: TabLayout.Tab) = Unit
        })
    }

    /** Cambia de pestaña como si se hubiera pulsado (abajo en el móvil, arriba en la tele). */
    private fun elegirPestana(id: Int) {
        if (tele) {
            (0 until pestanas.tabCount).mapNotNull { pestanas.getTabAt(it) }.firstOrNull { it.id == id }?.select()
        } else {
            navegacion.selectedItemId = id
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
    }

    override fun onCreateOptionsMenu(menu: Menu): Boolean {
        menuInflater.inflate(R.menu.principal, menu)
        val buscador = menu.findItem(R.id.accion_buscar).actionView as SearchView
        buscador.queryHint = "Buscar película…"
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
        cargando.isVisible = peliculas.isEmpty() && pestana == R.id.nav_peliculas
        vacio.isVisible = false
        cargaEnCurso = lifecycleScope.launch {
            try {
                val api = sesion.api()
                peliculas = api.peliculas().map { conProgresoLocal(api, it) }
                ultimoError = null
            } catch (e: ErrorApi) {
                if (e.codigo == 401) {
                    sesion.cerrar()
                    irALogin("Tu sesión ha terminado. Vuelve a entrar.")
                    return@launch
                }
                ultimoError = e.message
                if (peliculas.isEmpty() && descargas.todas().isNotEmpty() && pestana != R.id.nav_descargas) {
                    elegirPestana(R.id.nav_descargas)
                    aviso("Sin conexión con MiCine. Aquí tienes tus descargas.")
                } else {
                    aviso(e.message ?: "No se pudo actualizar")
                }
            } finally {
                cargando.isVisible = false
                pintar()
            }
        }
    }

    /** Lo visto sin conexión manda si es más reciente; de paso se lo cuenta al servidor. */
    private fun conProgresoLocal(api: Api, pelicula: Pelicula): Pelicula {
        val local = progresoLocal.leer(pelicula.ruta) ?: return pelicula
        val remoto = pelicula.progreso
        if (remoto != null && remoto.fecha >= local.fecha) return pelicula
        ambitoApp.launch { runCatching { api.progreso(pelicula.ruta, local.segundos, local.duracion) } }
        return pelicula.copy(progreso = local)
    }

    private fun mostrarPestana(id: Int) {
        pestana = id
        listaPeliculas.isVisible = id == R.id.nav_peliculas
        listaDescargas.isVisible = id == R.id.nav_descargas
        cargando.isVisible = cargando.isVisible && id == R.id.nav_peliculas
        refrescarDescargas()
        pintar()
    }

    private fun pintar() {
        val consulta = normalizar(filtro.trim())
        val mensaje = if (pestana == R.id.nav_peliculas) {
            val lista = peliculas.filter { normalizar("${it.titulo} ${it.ruta}").contains(consulta) }
            adaptadorPeliculas.actualizar(lista)
            when {
                cargando.isVisible -> ""
                peliculas.isEmpty() && ultimoError != null -> ultimoError!!
                peliculas.isEmpty() -> "Todavía no hay películas en MiCine.\n\nSúbelas desde la web en el ordenador."
                lista.isEmpty() -> "Ninguna película coincide con «$filtro»."
                else -> ""
            }
        } else {
            val lista = descargas.todas().filter { normalizar(it.titulo).contains(consulta) }
            adaptadorDescargas.actualizar(lista)
            when {
                lista.isEmpty() && consulta.isNotEmpty() -> "Ninguna descarga coincide con «$filtro»."
                lista.isEmpty() -> "Aún no has descargado ninguna película.\n\nToca el botón de descarga de una película para guardarla en el móvil y verla sin conexión."
                else -> ""
            }
        }
        vacio.text = mensaje
        vacio.isVisible = mensaje.isNotEmpty()
        enfocarInicio()
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
        val fila = lista.findViewHolderForAdapterPosition(0)?.itemView
        val destino = fila?.findViewById<View>(R.id.tarjeta) ?: fila
        if (destino != null) {
            destino.requestFocus()
        } else if (intentos > 0) {
            lista.postDelayed({ enfocarPrimero(lista, intentos - 1) }, 60)
        }
    }

    private fun idDe(ruta: String): Long = ids.getOrPut(ruta) { ids.size.toLong() }

    /** Carátula elegida con el mando: borde blanco y un poco más grande. */
    private fun resaltar(tarjeta: MaterialCardView, foco: Boolean, animar: Boolean) {
        tarjeta.setStrokeColor(ColorStateList.valueOf(if (foco) Color.WHITE else ContextCompat.getColor(this, R.color.borde)))
        tarjeta.strokeWidth = dp(if (foco) 3 else 1)
        val escala = if (foco) 1.06f else 1f
        tarjeta.animate().cancel()
        if (animar) {
            tarjeta.animate().scaleX(escala).scaleY(escala).setDuration(150).start()
        } else {
            tarjeta.scaleX = escala
            tarjeta.scaleY = escala
        }
    }

    private fun refrescarDescargas() {
        if (!::listaPeliculas.isInitialized) return
        val infos = descargas.todas().associate { it.ruta to descargas.info(it) }
        adaptadorPeliculas.actualizarInfos(infos)
        adaptadorDescargas.actualizarInfos(infos)
    }

    // ---------- acciones ----------

    private fun reproducir(pelicula: Pelicula) {
        val local = descargas.de(pelicula.ruta)?.takeIf { descargas.completa(it) }
        abrirReproductor(pelicula.ruta, pelicula.titulo, pelicula.progreso, pelicula, local)
    }

    private fun reproducirDescarga(descarga: Descarga) {
        val info = descargas.info(descarga)
        val pelicula = peliculas.firstOrNull { it.ruta == descarga.ruta }
        when {
            info.estado == EstadoDescarga.COMPLETA && File(descarga.archivo).exists() ->
                abrirReproductor(descarga.ruta, descarga.titulo, pelicula?.progreso, pelicula, descarga)
            info.estado == EstadoDescarga.FALLIDA ->
                if (pelicula != null) iniciarDescarga(pelicula) else aviso("Conéctate a MiCine para reintentar la descarga")
            pelicula != null ->
                MaterialAlertDialogBuilder(this)
                    .setTitle(descarga.titulo)
                    .setMessage("Todavía se está descargando (${(info.fraccion * 100).toInt()} %). Mientras tanto puedes verla por internet desde MiCine.")
                    .setPositiveButton("Ver ahora") { _, _ -> abrirReproductor(pelicula.ruta, pelicula.titulo, pelicula.progreso, pelicula, null) }
                    .setNegativeButton("Esperar", null)
                    .show()
            else -> aviso("Todavía se está descargando (${(info.fraccion * 100).toInt()} %)")
        }
    }

    private fun abrirReproductor(ruta: String, titulo: String, remoto: Progreso?, pelicula: Pelicula?, local: Descarga?) {
        val progreso = listOfNotNull(remoto, progresoLocal.leer(ruta)).maxByOrNull { it.fecha }
        val inicioMs = progreso?.takeIf { !it.vista && it.segundos > 15 }?.let { (it.segundos * 1000).toLong() } ?: 0L
        val api = sesion.api()
        val origen = local?.archivo ?: api.absoluta(pelicula!!.url)
        val subtitulos = local?.subtitulos?.filter { File(it.archivo).exists() }?.map { Uri.fromFile(File(it.archivo)).toString() }
            ?: pelicula!!.subtitulos.map { api.absoluta(it.url) + "&original=1" }
        startActivity(
            Intent(this, ReproductorActivity::class.java)
                .putExtra(ReproductorActivity.EXTRA_TITULO, titulo)
                .putExtra(ReproductorActivity.EXTRA_RUTA, ruta)
                .putExtra(ReproductorActivity.EXTRA_ORIGEN, origen)
                .putStringArrayListExtra(ReproductorActivity.EXTRA_SUBTITULOS, ArrayList(subtitulos))
                .putExtra(ReproductorActivity.EXTRA_INICIO_MS, inicioMs),
        )
    }

    private fun pulsarDescarga(pelicula: Pelicula) {
        val descarga = descargas.de(pelicula.ruta)
        val info = descarga?.let { descargas.info(it) }
        when {
            info?.estado == EstadoDescarga.COMPLETA ->
                Snackbar.make(navegacion, "«${pelicula.titulo}» ya está en el móvil", Snackbar.LENGTH_SHORT)
                    .setAnchorView(navegacion.takeIf { it.isVisible })
                    .setAction("Ver descargas") { elegirPestana(R.id.nav_descargas) }
                    .show()
            info?.activa == true ->
                MaterialAlertDialogBuilder(this)
                    .setTitle("¿Cancelar la descarga?")
                    .setMessage(pelicula.titulo)
                    .setPositiveButton("Cancelar descarga") { _, _ ->
                        descargas.borrar(descarga)
                        refrescarDescargas()
                        pintar()
                    }
                    .setNegativeButton("Seguir descargando", null)
                    .show()
            else -> {
                val libre = descargas.espacioLibre()
                if (pelicula.tamano > libre - 200L * 1024 * 1024) {
                    MaterialAlertDialogBuilder(this)
                        .setTitle("No hay espacio suficiente")
                        .setMessage("«${pelicula.titulo}» ocupa ${formatoTamano(pelicula.tamano)} y en el móvil quedan ${formatoTamano(libre)} libres.")
                        .setPositiveButton("Entendido", null)
                        .show()
                    return
                }
                MaterialAlertDialogBuilder(this)
                    .setTitle("Descargar película")
                    .setMessage("«${pelicula.titulo}» ocupa ${formatoTamano(pelicula.tamano)}. Podrás verla sin conexión desde la pestaña Descargas.\n\nLibre en el móvil: ${formatoTamano(libre)}")
                    .setPositiveButton("Descargar") { _, _ -> iniciarDescarga(pelicula) }
                    .setNegativeButton("Cancelar", null)
                    .show()
            }
        }
    }

    private fun iniciarDescarga(pelicula: Pelicula) {
        try {
            descargas.iniciar(sesion.api(), pelicula)
            aviso("Descargando «${pelicula.titulo}». Puedes seguir usando el móvil.")
        } catch (e: Exception) {
            aviso("No se pudo empezar la descarga: ${e.message}")
        }
        refrescarDescargas()
        pintar()
    }

    private fun confirmarBorrar(descarga: Descarga) {
        MaterialAlertDialogBuilder(this)
            .setTitle("¿Borrar la descarga?")
            .setMessage("«${descarga.titulo}» se borrará del móvil. Seguirá disponible en MiCine.")
            .setPositiveButton("Borrar") { _, _ ->
                descargas.borrar(descarga)
                refrescarDescargas()
                pintar()
            }
            .setNegativeButton("Cancelar", null)
            .show()
    }

    private fun confirmarSalir() {
        MaterialAlertDialogBuilder(this)
            .setTitle("¿Cerrar sesión?")
            .setMessage("Tus películas descargadas seguirán en el móvil.")
            .setPositiveButton("Cerrar sesión") { _, _ ->
                val api = sesion.api()
                ambitoApp.launch { runCatching { api.logout() } }
                sesion.cerrar()
                irALogin(null)
            }
            .setNegativeButton("Cancelar", null)
            .show()
    }

    private fun irALogin(mensaje: String?) {
        startActivity(Intent(this, LoginActivity::class.java).putExtra(LoginActivity.EXTRA_MENSAJE, mensaje))
        finish()
    }

    private fun aviso(texto: String) {
        Snackbar.make(navegacion, texto, Snackbar.LENGTH_LONG).setAnchorView(navegacion.takeIf { it.isVisible }).show()
    }

    // ---------- listas ----------

    private inner class AdaptadorPeliculas : RecyclerView.Adapter<AdaptadorPeliculas.Celda>() {
        private var lista: List<Pelicula> = emptyList()
        private var infos: Map<String, InfoDescarga> = emptyMap()

        init {
            setHasStableIds(true)
        }

        inner class Celda(vista: View) : RecyclerView.ViewHolder(vista) {
            val tarjeta: MaterialCardView = vista.findViewById(R.id.tarjeta)
            val poster: ImageView = vista.findViewById(R.id.poster)
            val sinPoster: View = vista.findViewById(R.id.sinPoster)
            val inicial: TextView = vista.findViewById(R.id.inicial)
            val nombre: TextView = vista.findViewById(R.id.nombre)
            val chapaVista: TextView = vista.findViewById(R.id.vista)
            val progreso: LinearProgressIndicator = vista.findViewById(R.id.progreso)
            val botonDescarga: View = vista.findViewById(R.id.botonDescarga)
            val iconoDescarga: ImageView = vista.findViewById(R.id.iconoDescarga)
            val progresoDescarga: CircularProgressIndicator = vista.findViewById(R.id.progresoDescarga)
            val titulo: TextView = vista.findViewById(R.id.titulo)
            val meta: TextView = vista.findViewById(R.id.meta)
        }

        @SuppressLint("NotifyDataSetChanged")
        fun actualizar(nueva: List<Pelicula>) {
            lista = nueva
            notifyDataSetChanged()
        }

        fun actualizarInfos(nuevas: Map<String, InfoDescarga>) {
            if (nuevas == infos) return
            infos = nuevas
            notifyItemRangeChanged(0, itemCount, CAMBIO_DESCARGA)
        }

        override fun getItemCount() = lista.size

        override fun getItemId(posicion: Int) = idDe(lista[posicion].ruta)

        override fun onCreateViewHolder(padre: ViewGroup, tipo: Int): Celda {
            val celda = Celda(LayoutInflater.from(padre.context).inflate(R.layout.item_pelicula, padre, false))
            celda.tarjeta.setOnFocusChangeListener { _, foco -> resaltar(celda.tarjeta, foco, animar = true) }
            return celda
        }

        override fun onBindViewHolder(celda: Celda, posicion: Int, cambios: MutableList<Any>) {
            if (cambios.isNotEmpty() && cambios.all { it == CAMBIO_DESCARGA }) pintarDescarga(celda, lista[posicion])
            else onBindViewHolder(celda, posicion)
        }

        override fun onBindViewHolder(celda: Celda, posicion: Int) {
            val p = lista[posicion]
            celda.titulo.text = p.titulo
            celda.meta.text = listOf(p.carpeta, formatoTamano(p.tamano)).filter { it.isNotEmpty() }.joinToString(" · ")
            celda.sinPoster.background = Imagenes.degradado(p.titulo)
            celda.inicial.text = p.titulo.trim().take(1).uppercase()
            celda.nombre.text = p.titulo
            Imagenes.cargar(celda.poster, p.poster?.let { sesion.api().absoluta(it) })
            val progreso = p.progreso
            celda.chapaVista.isVisible = progreso?.vista == true
            celda.progreso.isVisible = progreso != null && progreso.fraccion > 0.01f && !progreso.vista
            celda.progreso.progress = ((progreso?.fraccion ?: 0f) * 1000).toInt()
            celda.tarjeta.setOnClickListener { reproducir(p) }
            resaltar(celda.tarjeta, celda.tarjeta.isFocused, animar = false)
            celda.tarjeta.contentDescription = "Ver ${p.titulo}"
            celda.botonDescarga.setOnClickListener { pulsarDescarga(p) }
            pintarDescarga(celda, p)
        }

        private fun pintarDescarga(celda: Celda, p: Pelicula) {
            val info = infos[p.ruta]
            val completa = info?.estado == EstadoDescarga.COMPLETA
            celda.iconoDescarga.isVisible = info?.activa != true
            celda.iconoDescarga.setImageResource(if (completa) R.drawable.ic_descargada else R.drawable.ic_descargar)
            celda.iconoDescarga.imageTintList = ColorStateList.valueOf(
                ContextCompat.getColor(this@MainActivity, if (completa) R.color.exito else android.R.color.white),
            )
            celda.progresoDescarga.isVisible = info?.activa == true
            if (info?.activa == true) celda.progresoDescarga.setProgressCompat((info.fraccion * 100).toInt(), true)
            celda.botonDescarga.contentDescription = when {
                completa -> "Descargada"
                info?.activa == true -> "Descargando; toca para cancelar"
                else -> "Descargar para ver sin conexión"
            }
        }
    }

    private inner class AdaptadorDescargas : RecyclerView.Adapter<AdaptadorDescargas.Fila>() {
        private var lista: List<Descarga> = emptyList()
        private var infos: Map<String, InfoDescarga> = emptyMap()

        init {
            setHasStableIds(true)
        }

        inner class Fila(vista: View) : RecyclerView.ViewHolder(vista) {
            val fondoPoster: View = vista.findViewById(R.id.fondoPoster)
            val inicial: TextView = vista.findViewById(R.id.inicial)
            val poster: ImageView = vista.findViewById(R.id.poster)
            val titulo: TextView = vista.findViewById(R.id.titulo)
            val estado: TextView = vista.findViewById(R.id.estado)
            val barra: LinearProgressIndicator = vista.findViewById(R.id.barra)
            val borrar: ImageButton = vista.findViewById(R.id.borrar)
        }

        @SuppressLint("NotifyDataSetChanged")
        fun actualizar(nueva: List<Descarga>) {
            lista = nueva
            notifyDataSetChanged()
        }

        fun actualizarInfos(nuevas: Map<String, InfoDescarga>) {
            if (nuevas == infos) return
            val habiaNuevas = nuevas.keys != infos.keys
            infos = nuevas
            if (habiaNuevas && pestana == R.id.nav_descargas) pintar()
            else notifyItemRangeChanged(0, itemCount, CAMBIO_DESCARGA)
        }

        override fun getItemCount() = lista.size

        override fun getItemId(posicion: Int) = idDe(lista[posicion].ruta)

        override fun onCreateViewHolder(padre: ViewGroup, tipo: Int) =
            Fila(LayoutInflater.from(padre.context).inflate(R.layout.item_descarga, padre, false))

        override fun onBindViewHolder(fila: Fila, posicion: Int, cambios: MutableList<Any>) {
            if (cambios.isNotEmpty() && cambios.all { it == CAMBIO_DESCARGA }) pintarEstado(fila, lista[posicion])
            else onBindViewHolder(fila, posicion)
        }

        override fun onBindViewHolder(fila: Fila, posicion: Int) {
            val d = lista[posicion]
            fila.titulo.text = d.titulo
            fila.fondoPoster.background = Imagenes.degradado(d.titulo)
            fila.inicial.text = d.titulo.trim().take(1).uppercase()
            Imagenes.cargar(fila.poster, d.poster?.takeIf { File(it).exists() }, anchoMaximo = 160)
            fila.itemView.setOnClickListener { reproducirDescarga(d) }
            fila.borrar.setOnClickListener { confirmarBorrar(d) }
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
        private const val CAMBIO_DESCARGA = "descarga"
    }
}
