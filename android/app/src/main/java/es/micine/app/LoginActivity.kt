package es.micine.app

import android.content.Intent
import android.os.Bundle
import android.view.Gravity
import android.view.KeyEvent
import android.view.View
import android.view.inputmethod.EditorInfo
import android.widget.FrameLayout
import android.widget.TextView
import androidx.appcompat.app.AppCompatActivity
import androidx.core.view.isVisible
import androidx.lifecycle.lifecycleScope
import com.google.android.material.button.MaterialButton
import com.google.android.material.textfield.TextInputEditText
import kotlinx.coroutines.launch

class LoginActivity : AppCompatActivity() {

    private lateinit var sesion: Sesion
    private lateinit var servidor: TextInputEditText
    private lateinit var usuario: TextInputEditText
    private lateinit var contrasena: TextInputEditText
    private lateinit var error: TextView
    private lateinit var entrar: MaterialButton

    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        setContentView(R.layout.activity_login)
        sesion = Sesion(this)
        servidor = findViewById(R.id.servidor)
        usuario = findViewById(R.id.usuario)
        contrasena = findViewById(R.id.contrasena)
        error = findViewById(R.id.error)
        entrar = findViewById(R.id.entrar)

        servidor.setText(sesion.servidor?.removePrefix("http://").orEmpty())
        usuario.setText(sesion.usuario.orEmpty())
        intent.getStringExtra(EXTRA_MENSAJE)?.let { mostrarError(it) }

        // En pantallas anchas (la tele, tabletas) el formulario no ocupa todo el ancho.
        val anchoDp = resources.displayMetrics.widthPixels / resources.displayMetrics.density
        if (anchoDp >= 720) {
            findViewById<View>(R.id.formulario).layoutParams = FrameLayout.LayoutParams(dp(520), FrameLayout.LayoutParams.WRAP_CONTENT, Gravity.CENTER)
        }

        entrar.setOnClickListener { entrar() }
        // «Hecho» en el teclado de la pantalla o Intro en un teclado de verdad: se entra directamente.
        contrasena.setOnEditorActionListener { _, accion, evento ->
            val intro = accion == EditorInfo.IME_NULL && evento?.keyCode == KeyEvent.KEYCODE_ENTER
            if (accion == EditorInfo.IME_ACTION_DONE || accion == EditorInfo.IME_ACTION_GO || (intro && evento?.action == KeyEvent.ACTION_DOWN)) entrar()
            accion == EditorInfo.IME_ACTION_DONE || accion == EditorInfo.IME_ACTION_GO || intro
        }

        findViewById<MaterialButton>(R.id.verDescargas).apply {
            isVisible = Descargas(this@LoginActivity).todas().isNotEmpty()
            setOnClickListener {
                startActivity(Intent(this@LoginActivity, MainActivity::class.java).putExtra(MainActivity.EXTRA_SIN_CONEXION, true))
                finish()
            }
        }

        when {
            servidor.text.isNullOrEmpty() -> servidor.requestFocus()
            usuario.text.isNullOrEmpty() -> usuario.requestFocus()
            else -> contrasena.requestFocus()
        }
    }

    private fun entrar() {
        val direccion = servidor.text?.toString().orEmpty()
        val nombre = usuario.text?.toString().orEmpty().trim()
        val clave = contrasena.text?.toString().orEmpty()
        if (direccion.isBlank() || nombre.isEmpty() || clave.isEmpty()) {
            mostrarError("Rellena la dirección, el usuario y la contraseña")
            return
        }
        error.isVisible = false
        entrar.isEnabled = false
        entrar.text = "Entrando…"
        lifecycleScope.launch {
            try {
                val api = Api(direccion)
                val (token, nombreReal) = api.login(nombre, clave)
                sesion.servidor = api.base
                sesion.usuario = nombreReal
                sesion.token = token
                CatalogoActual.catalogo = null // puede ser otro usuario: nada de lo visto antes
                startActivity(Intent(this@LoginActivity, MainActivity::class.java))
                finish()
            } catch (e: ErrorApi) {
                mostrarError(e.message ?: "No se pudo entrar")
            } finally {
                entrar.isEnabled = true
                entrar.text = "Entrar"
            }
        }
    }

    private fun mostrarError(texto: String) {
        error.text = texto
        error.isVisible = true
    }

    companion object {
        const val EXTRA_MENSAJE = "mensaje"
    }
}
