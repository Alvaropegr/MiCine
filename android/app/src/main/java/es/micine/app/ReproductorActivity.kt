package es.micine.app

import android.annotation.SuppressLint
import android.net.Uri
import android.os.Bundle
import android.os.Handler
import android.os.Looper
import android.os.SystemClock
import android.view.GestureDetector
import android.view.KeyEvent
import android.view.MotionEvent
import android.view.View
import android.view.WindowManager
import android.widget.ImageButton
import android.widget.ProgressBar
import android.widget.SeekBar
import android.widget.TextView
import androidx.activity.OnBackPressedCallback
import androidx.appcompat.app.AppCompatActivity
import androidx.core.view.ViewCompat
import androidx.core.view.WindowCompat
import androidx.core.view.WindowInsetsCompat
import androidx.core.view.WindowInsetsControllerCompat
import androidx.core.view.isVisible
import androidx.core.view.updatePadding
import com.google.android.material.dialog.MaterialAlertDialogBuilder
import kotlinx.coroutines.launch
import org.videolan.libvlc.LibVLC
import org.videolan.libvlc.Media
import org.videolan.libvlc.MediaPlayer
import org.videolan.libvlc.interfaces.IMedia
import org.videolan.libvlc.util.VLCVideoLayout
import kotlin.math.abs

/**
 * Reproductor a pantalla completa con el motor de VLC: abre prácticamente cualquier formato
 * (MKV, AVI, WMV, MOV, audio AC3/DTS…), tanto por la red como desde una descarga.
 * Se maneja con el dedo en el móvil y con el mando en la tele (Fire TV).
 */
class ReproductorActivity : AppCompatActivity() {

    private lateinit var libVlc: LibVLC
    private lateinit var reproductor: MediaPlayer
    private lateinit var videoLayout: VLCVideoLayout
    private lateinit var controles: View
    private lateinit var cargando: ProgressBar
    private lateinit var aviso: TextView
    private lateinit var botonReproducir: ImageButton
    private lateinit var posicion: SeekBar
    private lateinit var textoTiempo: TextView
    private lateinit var textoDuracion: TextView
    private lateinit var textoBusqueda: TextView

    private lateinit var ruta: String
    private var duracionMs = 0L
    private var arrastrando = false
    private var ultimoGuardado = 0L
    private var seguirAlVolver = true
    private var escala = 0

    // Avance y retroceso con el mando, como en Netflix: las pulsaciones seguidas se suman
    // (y aceleran) y el salto se hace al dejar de pulsar.
    private var destinoBusqueda = -1L
    private var origenBusqueda = 0L
    private var sentidoBusqueda = 0
    private var pulsaciones = 0
    private var ultimaBusqueda = 0L

    private val manejador = Handler(Looper.getMainLooper())
    private val ocultarControles = Runnable { mostrarControles(false) }
    private val ocultarAviso = Runnable { aviso.isVisible = false }
    private val confirmarBusqueda = Runnable { aplicarBusqueda() }

    /** Atrás con el mando dentro de los controles: primero se ocultan; la siguiente vez se sale. */
    private val atrasOcultaControles = object : OnBackPressedCallback(false) {
        override fun handleOnBackPressed() {
            if (destinoBusqueda >= 0) cancelarBusqueda() else mostrarControles(false)
        }
    }

    private val escalas = listOf(
        MediaPlayer.ScaleType.SURFACE_BEST_FIT to "Ajustar a la pantalla",
        MediaPlayer.ScaleType.SURFACE_FILL to "Rellenar la pantalla",
        MediaPlayer.ScaleType.SURFACE_16_9 to "16:9",
        MediaPlayer.ScaleType.SURFACE_4_3 to "4:3",
        MediaPlayer.ScaleType.SURFACE_ORIGINAL to "Tamaño original",
    )

    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        setContentView(R.layout.activity_reproductor)
        window.addFlags(WindowManager.LayoutParams.FLAG_KEEP_SCREEN_ON)
        pantallaCompleta()

        ruta = intent.getStringExtra(EXTRA_RUTA).orEmpty()
        val origen = intent.getStringExtra(EXTRA_ORIGEN).orEmpty()
        val inicioMs = intent.getLongExtra(EXTRA_INICIO_MS, 0L)

        videoLayout = findViewById(R.id.video)
        controles = findViewById(R.id.controles)
        cargando = findViewById(R.id.cargando)
        aviso = findViewById(R.id.aviso)
        botonReproducir = findViewById(R.id.reproducir)
        posicion = findViewById(R.id.posicion)
        textoTiempo = findViewById(R.id.tiempo)
        textoDuracion = findViewById(R.id.duracion)
        textoBusqueda = findViewById(R.id.busqueda)
        findViewById<TextView>(R.id.titulo).text = intent.getStringExtra(EXTRA_TITULO)

        // Que los botones no queden debajo de la cámara frontal ni de los bordes curvos.
        ViewCompat.setOnApplyWindowInsetsListener(controles) { vista, insets ->
            val borde = insets.getInsets(WindowInsetsCompat.Type.displayCutout() or WindowInsetsCompat.Type.systemGestures())
            vista.updatePadding(left = borde.left, right = borde.right)
            insets
        }
        // En la tele, margen de seguridad: algunas recortan los bordes de la imagen.
        if (esTele(this)) {
            findViewById<View>(R.id.barraSuperior).setPaddingRelative(dp(40), dp(20), dp(40), 0)
            findViewById<View>(R.id.barraInferior).setPaddingRelative(dp(48), 0, dp(48), dp(24))
        }

        libVlc = LibVLC(this, arrayListOf("--audio-time-stretch", "--http-reconnect", "--no-stats"))
        reproductor = MediaPlayer(libVlc)
        val media = if (origen.startsWith("http")) Media(libVlc, Uri.parse(origen)) else Media(libVlc, origen)
        media.setHWDecoderEnabled(true, false)
        media.addOption(":network-caching=2500")
        if (inicioMs > 0) {
            media.addOption(":start-time=${inicioMs / 1000.0}")
            mostrarAviso("Continuando desde ${formatoTiempo(inicioMs)}", 3000)
        }
        intent.getStringArrayListExtra(EXTRA_SUBTITULOS).orEmpty().forEach {
            media.addSlave(IMedia.Slave(IMedia.Slave.Type.Subtitle, 4, it))
        }
        reproductor.media = media
        media.release()
        reproductor.setEventListener(::alEvento)

        onBackPressedDispatcher.addCallback(this, atrasOcultaControles)
        window.decorView.viewTreeObserver.addOnGlobalFocusChangeListener { _, _ -> actualizarAtras() }
        configurarControles()
        // Con el mando se empieza en Reproducir/Pausa (en modo táctil no hace nada).
        botonReproducir.requestFocus()
        actualizarAtras()
    }

    override fun onStart() {
        super.onStart()
        reproductor.attachViews(videoLayout, null, true, false)
        reproductor.videoScale = escalas[escala].first
        if (seguirAlVolver) reproductor.play()
    }

    override fun onStop() {
        super.onStop()
        seguirAlVolver = reproductor.isPlaying
        guardarProgreso()
        reproductor.pause()
        reproductor.detachViews()
    }

    override fun onDestroy() {
        super.onDestroy()
        manejador.removeCallbacksAndMessages(null)
        reproductor.setEventListener(null)
        reproductor.stop()
        reproductor.release()
        libVlc.release()
    }

    override fun onWindowFocusChanged(hasFocus: Boolean) {
        super.onWindowFocusChanged(hasFocus)
        if (hasFocus) pantallaCompleta()
    }

    override fun dispatchKeyEvent(evento: KeyEvent): Boolean {
        if (tecla(evento)) return true
        val atendida = super.dispatchKeyEvent(evento)
        // Mientras se usa el mando, los controles no se esconden.
        if (controles.isVisible) programarOcultar()
        return atendida
    }

    /** Mando de la tele, teclado o mando de juegos. Devuelve true si la tecla ya está atendida. */
    private fun tecla(evento: KeyEvent): Boolean {
        val pulsada = evento.action == KeyEvent.ACTION_DOWN
        val primera = pulsada && evento.repeatCount == 0
        val enControles = controles.isVisible && controles.hasFocus()
        when (evento.keyCode) {
            KeyEvent.KEYCODE_MEDIA_PLAY_PAUSE, KeyEvent.KEYCODE_HEADSETHOOK -> if (primera) {
                aplicarBusqueda()
                alternarPausa()
            }
            KeyEvent.KEYCODE_MEDIA_PLAY -> if (primera) {
                aplicarBusqueda()
                reproductor.play()
            }
            KeyEvent.KEYCODE_MEDIA_PAUSE -> if (primera) {
                aplicarBusqueda()
                reproductor.pause()
            }
            KeyEvent.KEYCODE_MEDIA_FAST_FORWARD -> if (pulsada) buscar(1, 30_000, evento, acelerar = false)
            KeyEvent.KEYCODE_MEDIA_REWIND -> if (pulsada) buscar(-1, 30_000, evento, acelerar = false)
            KeyEvent.KEYCODE_DPAD_LEFT, KeyEvent.KEYCODE_DPAD_RIGHT -> {
                // Entre los botones, las flechas pasan de uno a otro; en la barra de progreso
                // (o con los controles ocultos) avanzan o retroceden.
                if (enControles && !posicion.isFocused) return false
                if (pulsada) buscar(if (evento.keyCode == KeyEvent.KEYCODE_DPAD_RIGHT) 1 else -1, 10_000, evento, acelerar = true)
            }
            KeyEvent.KEYCODE_DPAD_UP, KeyEvent.KEYCODE_DPAD_DOWN -> {
                if (enControles) return false
                if (primera) mostrarControles(true, botonReproducir)
            }
            KeyEvent.KEYCODE_DPAD_CENTER, KeyEvent.KEYCODE_ENTER, KeyEvent.KEYCODE_NUMPAD_ENTER, KeyEvent.KEYCODE_BUTTON_A -> when {
                destinoBusqueda >= 0 -> if (primera) aplicarBusqueda()
                enControles -> return false
                // Con los controles ocultos, como en Netflix: aparecen y se pausa (o se sigue).
                primera -> {
                    mostrarControles(true, botonReproducir)
                    alternarPausa()
                }
            }
            else -> return false
        }
        return true
    }

    private fun pantallaCompleta() {
        WindowCompat.setDecorFitsSystemWindows(window, false)
        WindowInsetsControllerCompat(window, window.decorView).apply {
            hide(WindowInsetsCompat.Type.systemBars())
            systemBarsBehavior = WindowInsetsControllerCompat.BEHAVIOR_SHOW_TRANSIENT_BARS_BY_SWIPE
        }
    }

    @SuppressLint("ClickableViewAccessibility")
    private fun configurarControles() {
        findViewById<View>(R.id.atras).setOnClickListener { finish() }
        botonReproducir.setOnClickListener { alternarPausa() }
        findViewById<View>(R.id.retroceder).setOnClickListener { saltar(-10_000) }
        findViewById<View>(R.id.avanzar).setOnClickListener { saltar(10_000) }
        findViewById<View>(R.id.botonSubtitulos).setOnClickListener { elegirSubtitulos() }
        findViewById<View>(R.id.botonAudio).setOnClickListener { elegirAudio() }
        findViewById<View>(R.id.botonFormato).setOnClickListener {
            escala = (escala + 1) % escalas.size
            reproductor.videoScale = escalas[escala].first
            mostrarAviso(escalas[escala].second)
            programarOcultar()
        }

        posicion.setOnSeekBarChangeListener(object : SeekBar.OnSeekBarChangeListener {
            override fun onProgressChanged(barra: SeekBar, valor: Int, delUsuario: Boolean) {
                if (delUsuario) textoTiempo.text = formatoTiempo(valor * 1000L)
            }

            override fun onStartTrackingTouch(barra: SeekBar) {
                arrastrando = true
                manejador.removeCallbacks(ocultarControles)
            }

            override fun onStopTrackingTouch(barra: SeekBar) {
                arrastrando = false
                reproductor.time = barra.progress * 1000L
                programarOcultar()
            }
        })

        // Un toque muestra u oculta los controles; doble toque a un lado salta 10 segundos.
        val gestos = GestureDetector(this, object : GestureDetector.SimpleOnGestureListener() {
            // Sin aceptar el primer contacto, Android no manda el resto del toque y no se detecta nada.
            override fun onDown(e: MotionEvent) = true

            override fun onSingleTapConfirmed(e: MotionEvent): Boolean {
                mostrarControles(!controles.isVisible)
                return true
            }

            override fun onDoubleTap(e: MotionEvent): Boolean {
                val ancho = findViewById<View>(R.id.raiz).width
                when {
                    e.x < ancho / 3f -> saltar(-10_000)
                    e.x > ancho * 2 / 3f -> saltar(10_000)
                    else -> alternarPausa()
                }
                return true
            }
        })
        val tocar = View.OnTouchListener { _, evento -> gestos.onTouchEvent(evento) }
        findViewById<View>(R.id.raiz).setOnTouchListener(tocar)
        controles.setOnTouchListener(tocar)
        programarOcultar()
    }

    private fun alEvento(evento: MediaPlayer.Event) {
        when (evento.type) {
            MediaPlayer.Event.Buffering -> cargando.isVisible = evento.buffering < 100f
            MediaPlayer.Event.Playing -> {
                cargando.isVisible = false
                botonReproducir.setImageResource(R.drawable.ic_pause)
                programarOcultar()
            }
            MediaPlayer.Event.Paused -> {
                botonReproducir.setImageResource(R.drawable.ic_play)
                mostrarControles(true)
            }
            MediaPlayer.Event.LengthChanged -> {
                duracionMs = evento.lengthChanged
                posicion.max = (duracionMs / 1000).toInt()
                textoDuracion.text = formatoTiempo(duracionMs)
            }
            MediaPlayer.Event.TimeChanged -> {
                if (!arrastrando) {
                    posicion.progress = (evento.timeChanged / 1000).toInt()
                    textoTiempo.text = formatoTiempo(evento.timeChanged)
                }
                if (SystemClock.elapsedRealtime() - ultimoGuardado > 15_000) guardarProgreso()
            }
            MediaPlayer.Event.EndReached -> {
                guardarProgreso(terminada = true)
                finish()
            }
            MediaPlayer.Event.EncounteredError -> {
                cargando.isVisible = false
                mostrarControles(true)
                mostrarAviso("No se pudo reproducir. Comprueba que MiCine sigue encendido en el ordenador o que la descarga terminó bien.", 0)
            }
        }
    }

    private fun alternarPausa() {
        if (reproductor.isPlaying) reproductor.pause() else reproductor.play()
    }

    private fun saltar(milisegundos: Long) {
        val destino = (reproductor.time + milisegundos).coerceIn(0, if (duracionMs > 0) duracionMs - 1000 else Long.MAX_VALUE)
        reproductor.time = destino
        mostrarAviso(if (milisegundos < 0) "« 10 s" else "10 s »", 700)
        if (controles.isVisible) programarOcultar()
    }

    /** Avance o retroceso con el mando: cuantas más pulsaciones seguidas, más largo el salto. */
    private fun buscar(sentido: Int, paso: Long, evento: KeyEvent, acelerar: Boolean) {
        val ahora = SystemClock.uptimeMillis()
        if (evento.repeatCount > 0 && ahora - ultimaBusqueda < 100) return // tecla mantenida: diez saltos por segundo como mucho
        ultimaBusqueda = ahora
        if (destinoBusqueda < 0) {
            origenBusqueda = reproductor.time.coerceAtLeast(0)
            destinoBusqueda = origenBusqueda
            pulsaciones = 0
        }
        if (sentido != sentidoBusqueda) pulsaciones = 0
        sentidoBusqueda = sentido
        pulsaciones++
        val factor = when {
            !acelerar || pulsaciones <= 4 -> 1
            pulsaciones <= 10 -> 3
            pulsaciones <= 20 -> 6
            else -> 12
        }
        val maximo = if (duracionMs > 1000) duracionMs - 1000 else Long.MAX_VALUE
        destinoBusqueda = (destinoBusqueda + sentido * paso * factor).coerceIn(0, maximo)

        arrastrando = true
        if (!(controles.isVisible && controles.hasFocus())) mostrarControles(true, posicion)
        manejador.removeCallbacks(ocultarControles)
        posicion.progress = (destinoBusqueda / 1000).toInt()
        textoTiempo.text = formatoTiempo(destinoBusqueda)
        val salto = destinoBusqueda - origenBusqueda
        textoBusqueda.text = "${if (salto < 0) "«" else "»"} ${formatoTiempo(destinoBusqueda)}  ·  ${if (salto < 0) "−" else "+"}${formatoSalto(abs(salto))}"
        textoBusqueda.isVisible = true
        manejador.removeCallbacks(confirmarBusqueda)
        manejador.postDelayed(confirmarBusqueda, 800)
        actualizarAtras()
    }

    private fun aplicarBusqueda() {
        manejador.removeCallbacks(confirmarBusqueda)
        if (destinoBusqueda < 0) return
        reproductor.time = destinoBusqueda
        terminarBusqueda()
    }

    private fun cancelarBusqueda() {
        manejador.removeCallbacks(confirmarBusqueda)
        if (destinoBusqueda < 0) return
        val actual = reproductor.time.coerceAtLeast(0)
        posicion.progress = (actual / 1000).toInt()
        textoTiempo.text = formatoTiempo(actual)
        terminarBusqueda()
    }

    private fun terminarBusqueda() {
        destinoBusqueda = -1
        sentidoBusqueda = 0
        pulsaciones = 0
        arrastrando = false
        textoBusqueda.isVisible = false
        programarOcultar()
        actualizarAtras()
    }

    private fun formatoSalto(milisegundos: Long): String {
        val segundos = milisegundos / 1000
        return when {
            segundos < 60 -> "$segundos s"
            segundos % 60 == 0L -> "${segundos / 60} min"
            else -> "${segundos / 60} min ${segundos % 60} s"
        }
    }

    /** [enfocar]: botón que se elige con el mando al aparecer (si no, Reproducir/Pausa). */
    private fun mostrarControles(mostrar: Boolean, enfocar: View? = null) {
        val teniaFoco = controles.isVisible && controles.hasFocus()
        controles.isVisible = mostrar
        if (mostrar) {
            if (!controles.isInTouchMode && (enfocar != null || !teniaFoco)) (enfocar ?: botonReproducir).requestFocus()
            programarOcultar()
        } else {
            manejador.removeCallbacks(ocultarControles)
        }
        actualizarAtras()
    }

    private fun programarOcultar() {
        manejador.removeCallbacks(ocultarControles)
        if (reproductor.isPlaying && destinoBusqueda < 0) manejador.postDelayed(ocultarControles, 4000)
    }

    private fun actualizarAtras() {
        atrasOcultaControles.isEnabled = destinoBusqueda >= 0 || (controles.isVisible && controles.hasFocus())
    }

    private fun mostrarAviso(texto: String, duracion: Long = 1500) {
        aviso.text = texto
        aviso.isVisible = true
        manejador.removeCallbacks(ocultarAviso)
        if (duracion > 0) manejador.postDelayed(ocultarAviso, duracion)
    }

    private fun elegirSubtitulos() {
        val pistas = reproductor.spuTracks.orEmpty()
        if (pistas.size <= 1) {
            mostrarAviso("Esta película no tiene subtítulos")
            return
        }
        elegirPista("Subtítulos", pistas, reproductor.spuTrack) { reproductor.spuTrack = it }
    }

    private fun elegirAudio() {
        val pistas = reproductor.audioTracks.orEmpty()
        if (pistas.size <= 2) { // «Desactivar» y una pista: no hay nada que elegir
            mostrarAviso("Esta película solo tiene un idioma de audio")
            return
        }
        elegirPista("Audio", pistas, reproductor.audioTrack) { reproductor.audioTrack = it }
    }

    private fun elegirPista(titulo: String, pistas: Array<out MediaPlayer.TrackDescription>, actual: Int, elegir: (Int) -> Unit) {
        manejador.removeCallbacks(ocultarControles)
        val nombres = pistas.map { if (it.id == -1) "Desactivados" else it.name ?: "Pista ${it.id}" }.toTypedArray()
        MaterialAlertDialogBuilder(this)
            .setTitle(titulo)
            .setSingleChoiceItems(nombres, pistas.indexOfFirst { it.id == actual }) { dialogo, i ->
                elegir(pistas[i].id)
                dialogo.dismiss()
            }
            .setOnDismissListener { programarOcultar(); pantallaCompleta() }
            .show()
    }

    /** Se guarda en el móvil (vale sin conexión) y se intenta mandar a MiCine. */
    private fun guardarProgreso(terminada: Boolean = false) {
        if (duracionMs <= 0 || ruta.isEmpty()) return
        val tiempo = if (terminada) duracionMs else reproductor.time
        if (tiempo < 1000) return
        ultimoGuardado = SystemClock.elapsedRealtime()
        val segundos = tiempo / 1000.0
        val duracion = duracionMs / 1000.0
        ProgresoLocal(this).guardar(ruta, segundos, duracion)
        val sesion = Sesion(this)
        if (sesion.iniciada) {
            val api = sesion.api()
            ambitoApp.launch { runCatching { api.progreso(ruta, segundos, duracion) } }
        }
    }

    companion object {
        const val EXTRA_TITULO = "titulo"
        const val EXTRA_RUTA = "ruta"
        const val EXTRA_ORIGEN = "origen"
        const val EXTRA_SUBTITULOS = "subtitulos"
        const val EXTRA_INICIO_MS = "inicio_ms"
    }
}
