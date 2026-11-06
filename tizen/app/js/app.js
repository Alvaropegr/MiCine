/* Arranque, cambio de pantalla, diálogos, avisos y reparto de las teclas del mando. */

var contenedorPantallas, contenedorCapas;

MC.pantalla = null;
MC.modal = null;

MC.mostrar = function (pantalla) {
  if (!pantalla) return;
  if (MC.pantalla && MC.pantalla !== pantalla && MC.pantalla.salirPantalla) MC.pantalla.salirPantalla();
  if (MC.escribiendo()) document.activeElement.blur();
  MC.vaciar(contenedorPantallas);
  MC.pantalla = pantalla;
  contenedorPantallas.appendChild(pantalla.el);
  if (pantalla.entrarPantalla) pantalla.entrarPantalla();
};

MC.irInicio = function () {
  MC.mostrar(new PantallaInicio());
};

MC.cerrarSesion = function (mensaje) {
  if (!mensaje && MC.api.token) MC.api.pedir('POST', '/api/logout', {}, null, null);
  var sesion = MC.leer('sesion', null) || {};
  MC.guardar('sesion', { servidor: sesion.servidor, usuario: sesion.usuario });
  MC.api.token = '';
  MC.mostrar(new PantallaConectar(mensaje));
};

MC.dialogo = function (titulo, texto, botones) {
  var fila = MC.h('div', { class: 'dialogo-botones' });
  var modal = {
    i: 0,
    cerrar: function () {
      contenedorCapas.removeChild(modal.el);
      MC.modal = null;
    },
    pintar: function () {
      for (var k = 0; k < botones.length; k++) MC.clase(botones[k].el, 'foco', k === modal.i);
    },
    tecla: function (t) {
      if (t === 'izq' && modal.i > 0) modal.i--;
      else if (t === 'der' && modal.i < botones.length - 1) modal.i++;
      else if (t === 'atras') return modal.cerrar();
      else if (t === 'ok') {
        modal.cerrar();
        if (botones[modal.i].accion) botones[modal.i].accion();
        return;
      }
      modal.pintar();
    }
  };
  for (var k = 0; k < botones.length; k++) {
    botones[k].el = MC.h('div', { class: 'boton' }, botones[k].texto);
    fila.appendChild(botones[k].el);
    (function (indice) {
      botones[indice].el.addEventListener('click', function () {
        modal.i = indice;
        modal.tecla('ok');
      });
    })(k);
  }
  modal.el = MC.h('div', { class: 'dialogo-fondo' },
    MC.h('div', { class: 'dialogo' }, MC.h('h2', null, titulo), texto ? MC.h('p', null, texto) : null, fila));
  contenedorCapas.appendChild(modal.el);
  MC.modal = modal;
  modal.pintar();
};

/** Diálogo con una lista vertical de opciones (por ejemplo, las versiones de una película).
    actual: la opción marcada; alElegir(n) se llama con la elegida. VOLVER cierra sin elegir. */
MC.elegir = function (titulo, opciones, actual, alElegir) {
  var lista = MC.h('div', { class: 'eleccion-lista' }), els = [];
  var modal = {
    i: actual || 0,
    cerrar: function () {
      contenedorCapas.removeChild(modal.el);
      MC.modal = null;
    },
    pintar: function () {
      for (var k = 0; k < els.length; k++) MC.clase(els[k], 'foco', k === modal.i);
    },
    tecla: function (t) {
      if (t === 'arr' && modal.i > 0) modal.i--;
      else if (t === 'aba' && modal.i < els.length - 1) modal.i++;
      else if (t === 'atras') return modal.cerrar();
      else if (t === 'ok') {
        modal.cerrar();
        return alElegir(modal.i);
      }
      modal.pintar();
    }
  };
  for (var k = 0; k < opciones.length; k++) {
    els.push(MC.h('div', { class: 'eleccion-item' + (k === actual ? ' activo' : '') },
      MC.h('span', { class: 'panel-marca' }, k === actual ? MC.icono('marca') : null), opciones[k]));
    lista.appendChild(els[k]);
    (function (indice) {
      els[indice].addEventListener('click', function () {
        modal.i = indice;
        modal.tecla('ok');
      });
    })(k);
  }
  modal.el = MC.h('div', { class: 'dialogo-fondo' },
    MC.h('div', { class: 'dialogo eleccion' }, MC.h('h2', null, titulo), lista,
      MC.h('p', { class: 'eleccion-ayuda' }, 'Pulsa VOLVER para cancelar')));
  contenedorCapas.appendChild(modal.el);
  MC.modal = modal;
  modal.pintar();
};

MC.preguntarSalir = function () {
  MC.dialogo('¿Salir de MiCine?', '', [{ texto: 'Salir', accion: MC.cerrarApp }, { texto: 'Cancelar' }]);
};

MC.aviso = function (texto) {
  var el = MC.h('div', { class: 'aviso' }, texto);
  contenedorCapas.appendChild(el);
  setTimeout(function () { MC.clase(el, 'visible', true); }, 20);
  setTimeout(function () { MC.clase(el, 'visible', false); }, 3000);
  setTimeout(function () { if (el.parentNode) el.parentNode.removeChild(el); }, 3500);
};

/** La app está pensada para 1920×1080; en otras pantallas (o en el navegador) se escala. */
function ajustarEscala() {
  var raiz = document.getElementById('raiz');
  var escala = Math.min(window.innerWidth / 1920, window.innerHeight / 1080);
  var t = Math.abs(escala - 1) < 0.01 ? '' : 'scale(' + escala + ')';
  raiz.style.webkitTransform = t;
  raiz.style.transform = t;
}

document.addEventListener('keydown', function (e) {
  var t = MC.tecla(e);
  if (MC.escribiendo()) {
    // Mientras se escribe, solo se atienden las teclas que cierran el teclado o sacan del campo.
    if (t === 'ok' || t === 'hecho' || t === 'cancelar' || t === 'atras' || t === 'arr' || t === 'aba') {
      e.preventDefault();
      if (MC.pantalla && MC.pantalla.teclaCampo) MC.pantalla.teclaCampo(t);
      else document.activeElement.blur();
    }
    return;
  }
  if (!t) return;
  e.preventDefault();
  if (MC.modal) MC.modal.tecla(t);
  else if (MC.pantalla) MC.pantalla.tecla(t);
});

(function arrancar() {
  var raiz = document.getElementById('raiz');
  contenedorPantallas = MC.h('div', { id: 'pantallas' });
  contenedorCapas = MC.h('div', { id: 'capas' });
  raiz.appendChild(contenedorPantallas);
  raiz.appendChild(contenedorCapas);
  ajustarEscala();
  window.addEventListener('resize', ajustarEscala);
  MC.registrarTeclas();

  var sesion = MC.leer('sesion', null);
  if (sesion && sesion.token && sesion.servidor) {
    MC.api.servidor = sesion.servidor;
    MC.api.token = sesion.token;
    MC.irInicio();
  } else {
    MC.mostrar(new PantallaConectar());
  }
})();
