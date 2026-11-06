using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Threading;
using System.Windows.Forms;
using Microsoft.Win32;

namespace MiCine
{
    /// <summary>
    /// La ventana de MiCine: si está en marcha, en qué direcciones se entra,
    /// la carpeta de películas, los usuarios y lo que va pasando. Al cerrarla
    /// MiCine sigue funcionando en la bandeja, junto al reloj.
    /// </summary>
    public sealed class FormPrincipal : Form
    {
        const string CLAVE_INICIO = @"Software\Microsoft\Windows\CurrentVersion\Run";
        const string VALOR_INICIO = "MiCine";

        readonly Nucleo nucleo;
        bool permitirVisible;
        bool saliendo;
        bool avisadoBandeja;
        bool cargando;

        readonly NotifyIcon bandeja;
        readonly Label estado = new Label();
        readonly Label detalleError = new Label();
        readonly TextBox urlLocal, urlRed, carpeta;
        readonly Label etiquetaRed, pistaRed, cuantas, usuariosTexto;
        readonly Button abrirLocal, copiarLocal, abrirRed, copiarRed, abrirCarpeta, cambiarCarpeta, anadirCarpeta;
        readonly Label etiquetaOtras;
        readonly TextBox urlTele, urlMovil;
        readonly Button copiarTele, copiarMovil;
        readonly Button actualizarCaratulas, guardarTmdb, reidentificar;
        readonly TextBox claveTmdb;
        readonly Label tmdbTexto;
        readonly TableLayoutPanel listaCarpetas;
        string firmaCarpetas;
        readonly Button crearAdmin, gestionar, recuperar, aplicarPuerto, marcha;
        readonly CheckBox conWindows, dlnaActivo;
        readonly Label telesTexto;
        readonly TableLayoutPanel listaTeles;
        readonly NumericUpDown puerto;
        readonly ListBox actividad;
        readonly System.Windows.Forms.Timer reloj;
        int contando;
        int peliculas = -1;

        public FormPrincipal(Nucleo nucleo, bool minimizado)
        {
            this.nucleo = nucleo;
            permitirVisible = !minimizado;

            SuspendLayout();
            AutoScaleDimensions = new SizeF(96f, 96f);
            AutoScaleMode = AutoScaleMode.Dpi;
            Text = "MiCine";
            Icon = Tema.Icono(32);
            BackColor = Tema.Fondo;
            ForeColor = Tema.Texto;
            Font = Tema.Letra(9.75f);
            ClientSize = new Size(660, 860);
            MinimumSize = new Size(600, 700);
            StartPosition = FormStartPosition.CenterScreen;
            Padding = new Padding(16, 14, 16, 14);

            var raiz = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 1,
                BackColor = Tema.Fondo,
                Margin = new Padding(0),
                Padding = new Padding(0),
                AutoScroll = true, // en pantallas bajas (o con muchas carpetas y teles) se desplaza
            };
            raiz.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));

            // ── cabecera ──
            var cabecera = new TableLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, ColumnCount = 3, Margin = new Padding(0, 0, 0, 8) };
            cabecera.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            cabecera.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
            cabecera.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            var logo = new PictureBox { Image = Tema.Icono(48).ToBitmap(), SizeMode = PictureBoxSizeMode.Zoom, Size = new Size(36, 36), Margin = new Padding(0, 0, 10, 0) };
            var titulos = new FlowLayoutPanel { FlowDirection = FlowDirection.TopDown, AutoSize = true, WrapContents = false, Margin = new Padding(0), Anchor = AnchorStyles.Left };
            titulos.Controls.Add(Tema.Etiqueta("MiCine", Tema.Texto, Tema.Negrita(15f)));
            titulos.Controls.Add(Tema.Etiqueta("Tu servidor de películas", Tema.Tenue, Tema.Letra(9f)));
            estado.AutoSize = true;
            estado.Font = Tema.Negrita(10f);
            estado.Anchor = AnchorStyles.Right;
            estado.Margin = new Padding(8, 0, 0, 0);
            cabecera.Controls.Add(logo, 0, 0);
            cabecera.Controls.Add(titulos, 1, 0);
            cabecera.Controls.Add(estado, 2, 0);

            detalleError.AutoSize = true;
            detalleError.ForeColor = Tema.Acento;
            detalleError.Font = Tema.Letra(9.5f);
            detalleError.Margin = new Padding(0, 0, 0, 8);
            detalleError.Visible = false;

            // ── direcciones ──
            var direcciones = Tema.Tarjeta();
            Tema.Fila(direcciones, Tema.Titulo("Cómo entrar"), null);
            Tema.Fila(direcciones, Tema.Etiqueta("En este ordenador", Tema.Tenue, Tema.Letra(9f)), null);
            urlLocal = Tema.Campo(Tema.Negrita(12f));
            abrirLocal = Tema.Boton("Abrir", true);
            copiarLocal = Tema.Boton("Copiar", false);
            Tema.Fila(direcciones, urlLocal, Tema.Botonera(abrirLocal, copiarLocal));
            etiquetaRed = Tema.Etiqueta("En el móvil, la tablet, la tele o la app de Android", Tema.Tenue, Tema.Letra(9f));
            etiquetaRed.Margin = new Padding(0, 10, 0, 2);
            Tema.Fila(direcciones, etiquetaRed, null);
            urlRed = Tema.Campo(Tema.Negrita(12f));
            abrirRed = Tema.Boton("Abrir", false);
            copiarRed = Tema.Boton("Copiar", false);
            Tema.Fila(direcciones, urlRed, Tema.Botonera(abrirRed, copiarRed));
            pistaRed = Tema.Etiqueta("", Tema.Tenue, Tema.Letra(8.75f));
            pistaRed.Margin = new Padding(0, 6, 0, 0);
            Tema.Fila(direcciones, pistaRed, null);

            // ── películas ──
            var tarjetaPeliculas = Tema.Tarjeta();
            Tema.Fila(tarjetaPeliculas, Tema.Titulo("Películas"), null);
            Tema.Fila(tarjetaPeliculas, Tema.Etiqueta("Carpeta principal", Tema.Tenue, Tema.Letra(9f)), null);
            carpeta = Tema.Campo(Tema.Letra(9.75f));
            abrirCarpeta = Tema.Boton("Abrir carpeta", false);
            cambiarCarpeta = Tema.Boton("Cambiar…", false);
            Tema.Fila(tarjetaPeliculas, carpeta, Tema.Botonera(abrirCarpeta, cambiarCarpeta));
            etiquetaOtras = Tema.Etiqueta("También se leen estas carpetas", Tema.Tenue, Tema.Letra(9f));
            etiquetaOtras.Margin = new Padding(0, 8, 0, 2);
            Tema.Fila(tarjetaPeliculas, etiquetaOtras, null);
            listaCarpetas = new TableLayoutPanel
            {
                AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Dock = DockStyle.Fill, ColumnCount = 2,
                BackColor = Tema.Panel, Margin = new Padding(0), Padding = new Padding(0),
            };
            listaCarpetas.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
            listaCarpetas.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            Tema.Fila(tarjetaPeliculas, listaCarpetas, null);
            cuantas = Tema.Etiqueta("", Tema.Tenue, Tema.Letra(9f));
            cuantas.Anchor = AnchorStyles.Left;
            anadirCarpeta = Tema.Boton("Añadir carpeta…", false);
            actualizarCaratulas = Tema.Boton("Actualizar carátulas", false);
            Tema.Fila(tarjetaPeliculas, cuantas, Tema.Botonera(actualizarCaratulas, anadirCarpeta));

            // ── identificar con TMDB ──
            var tarjetaTmdb = Tema.Tarjeta();
            var conseguirClave = Tema.Boton("Conseguir una clave", false);
            conseguirClave.Margin = new Padding(0, 0, 0, 4);
            Tema.Fila(tarjetaTmdb, Tema.Titulo("Series, películas y anime (TMDB)"), conseguirClave);
            Tema.Fila(tarjetaTmdb, Tema.Etiqueta("Con una clave gratuita de themoviedb.org, MiCine reconoce cada película y serie (y el anime) y les pone su nombre en español.\n" +
                "Solo se envían los nombres de los archivos.", Tema.Tenue, Tema.Letra(9f)), null);
            claveTmdb = new TextBox
            {
                BorderStyle = BorderStyle.FixedSingle, BackColor = Tema.Fondo, ForeColor = Tema.Texto, Font = Tema.Letra(10f),
                UseSystemPasswordChar = true, Anchor = AnchorStyles.Left | AnchorStyles.Right, Margin = new Padding(0, 4, 8, 4),
            };
            guardarTmdb = Tema.Boton("Guardar clave", false);
            Tema.Fila(tarjetaTmdb, claveTmdb, Tema.Botonera(guardarTmdb));
            tmdbTexto = Tema.Etiqueta("", Tema.Tenue, Tema.Letra(9f));
            tmdbTexto.Anchor = AnchorStyles.Left;
            reidentificar = Tema.Boton("Volver a identificar", false);
            Tema.Fila(tarjetaTmdb, tmdbTexto, Tema.Botonera(reidentificar));
            conseguirClave.Click += delegate { AbrirUrl("https://www.themoviedb.org/settings/api"); };

            // ── usuarios ──
            var tarjetaUsuarios = Tema.Tarjeta();
            Tema.Fila(tarjetaUsuarios, Tema.Titulo("Usuarios"), null);
            usuariosTexto = Tema.Etiqueta("", Tema.Texto, Tema.Letra(9.75f));
            usuariosTexto.Anchor = AnchorStyles.Left;
            crearAdmin = Tema.Boton("Crear administrador", true);
            gestionar = Tema.Boton("Gestionar usuarios", false);
            recuperar = Tema.Boton("Recuperar administrador…", false);
            Tema.Fila(tarjetaUsuarios, usuariosTexto, Tema.Botonera(crearAdmin, gestionar, recuperar));

            // ── teles con DLNA ──
            var tarjetaTeles = Tema.Tarjeta();
            dlnaActivo = new CheckBox { Text = "Activado", AutoSize = true, ForeColor = Tema.Texto, Anchor = AnchorStyles.Right, Margin = new Padding(0, 0, 0, 4) };
            Tema.Fila(tarjetaTeles, Tema.Titulo("Teles con DLNA"), dlnaActivo);
            telesTexto = Tema.Etiqueta("", Tema.Tenue, Tema.Letra(9f));
            Tema.Fila(tarjetaTeles, telesTexto, null);
            listaTeles = new TableLayoutPanel
            {
                AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Dock = DockStyle.Fill, ColumnCount = 2,
                BackColor = Tema.Panel, Margin = new Padding(0, 4, 0, 0), Padding = new Padding(0),
            };
            listaTeles.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
            listaTeles.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            Tema.Fila(tarjetaTeles, listaTeles, null);

            // ── apps ──
            var tarjetaApps = Tema.Tarjeta();
            var verGithub = Tema.Boton("Ver en GitHub", false);
            verGithub.Margin = new Padding(0, 0, 0, 4);
            Tema.Fila(tarjetaApps, Tema.Titulo("Instalar las apps"), verGithub);
            Tema.Fila(tarjetaApps, Tema.Etiqueta("Fire TV con Fire OS: instala «Downloader» (tienda de Amazon) y escribe esta dirección. Las nuevas con Vega OS no admiten apps de fuera.", Tema.Tenue, Tema.Letra(9f)), null);
            urlTele = Tema.Campo(Tema.Negrita(12f));
            copiarTele = Tema.Boton("Copiar", false);
            Tema.Fila(tarjetaApps, urlTele, Tema.Botonera(copiarTele));
            var etiquetaMovil = Tema.Etiqueta("Móvil Android: abre esta dirección en su navegador", Tema.Tenue, Tema.Letra(9f));
            etiquetaMovil.Margin = new Padding(0, 8, 0, 2);
            Tema.Fila(tarjetaApps, etiquetaMovil, null);
            urlMovil = Tema.Campo(Tema.Negrita(12f));
            copiarMovil = Tema.Boton("Copiar", false);
            Tema.Fila(tarjetaApps, urlMovil, Tema.Botonera(copiarMovil));
            verGithub.Click += delegate { AbrirUrl(Apps.GITHUB); };
            copiarTele.Click += delegate { Copiar(urlTele.Text, copiarTele); };
            copiarMovil.Click += delegate { Copiar(urlMovil.Text, copiarMovil); };

            // ── opciones ──
            var opciones = new TableLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, ColumnCount = 5, Margin = new Padding(0, 0, 0, 10) };
            opciones.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            opciones.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
            opciones.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            opciones.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            opciones.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            conWindows = new CheckBox { Text = "Iniciar con Windows", AutoSize = true, ForeColor = Tema.Texto, Anchor = AnchorStyles.Left, Margin = new Padding(0, 4, 12, 4) };
            var etiquetaPuerto = Tema.Etiqueta("Puerto", Tema.Tenue, Tema.Letra(9.5f));
            etiquetaPuerto.Anchor = AnchorStyles.Right;
            puerto = new NumericUpDown
            {
                Minimum = 1, Maximum = 65535, Width = 76, BackColor = Tema.Panel2, ForeColor = Tema.Texto,
                BorderStyle = BorderStyle.FixedSingle, Anchor = AnchorStyles.Right, Margin = new Padding(6, 4, 0, 4), TextAlign = HorizontalAlignment.Right,
            };
            aplicarPuerto = Tema.Boton("Aplicar", false);
            aplicarPuerto.Anchor = AnchorStyles.Right;
            marcha = Tema.Boton("Detener", false);
            marcha.Anchor = AnchorStyles.Right;
            marcha.Margin = new Padding(16, 2, 0, 2);
            opciones.Controls.Add(conWindows, 0, 0);
            opciones.Controls.Add(etiquetaPuerto, 1, 0);
            opciones.Controls.Add(puerto, 2, 0);
            opciones.Controls.Add(aplicarPuerto, 3, 0);
            opciones.Controls.Add(marcha, 4, 0);

            // ── actividad ──
            var tituloActividad = Tema.Titulo("Actividad");
            tituloActividad.Margin = new Padding(0, 2, 0, 6);
            actividad = new ListBox
            {
                Dock = DockStyle.Fill,
                BackColor = Tema.Panel,
                ForeColor = Tema.Texto,
                BorderStyle = BorderStyle.None,
                Font = Tema.Letra(9f),
                IntegralHeight = false,
                HorizontalScrollbar = true,
                SelectionMode = SelectionMode.One,
                Margin = new Padding(0),
            };
            actividad.HandleCreated += delegate { Nativo.ControlOscuro(actividad.Handle); };
            var marcoActividad = new Panel { Dock = DockStyle.Fill, BackColor = Tema.Panel, Padding = new Padding(10, 8, 10, 8), Margin = new Padding(0) };
            marcoActividad.Controls.Add(actividad);

            Control[] filas = { cabecera, detalleError, direcciones, tarjetaPeliculas, tarjetaTmdb, tarjetaUsuarios, tarjetaTeles, tarjetaApps, opciones, tituloActividad, marcoActividad };
            raiz.RowCount = filas.Length;
            for (int i = 0; i < filas.Length; i++)
            {
                raiz.RowStyles.Add(i == filas.Length - 1 ? new RowStyle(SizeType.Absolute, 140f) : new RowStyle(SizeType.AutoSize));
                raiz.Controls.Add(filas[i], 0, i);
            }
            raiz.Layout += delegate { AjustarActividad(raiz); };
            raiz.HandleCreated += delegate { Nativo.ControlOscuro(raiz.Handle); }; // barra de desplazamiento oscura
            Controls.Add(raiz);

            // ── bandeja ──
            var menu = new ContextMenuStrip();
            var abrirWeb = new ToolStripMenuItem("Abrir MiCine") { Font = new Font(menu.Font, FontStyle.Bold) };
            abrirWeb.Click += delegate { AbrirUrl(nucleo.UrlLocal); };
            var verVentana = new ToolStripMenuItem("Mostrar ventana");
            verVentana.Click += delegate { MostrarVentana(); };
            var salir = new ToolStripMenuItem("Salir");
            salir.Click += delegate { Salir(); };
            menu.Items.Add(abrirWeb);
            menu.Items.Add(verVentana);
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add(salir);
            bandeja = new NotifyIcon
            {
                Icon = Tema.Icono(SystemInformation.SmallIconSize.Width),
                Text = "MiCine",
                ContextMenuStrip = menu,
                Visible = true,
            };
            bandeja.DoubleClick += delegate { MostrarVentana(); };
            bandeja.BalloonTipClicked += delegate { MostrarVentana(); };

            // ── acciones ──
            abrirLocal.Click += delegate { AbrirUrl(nucleo.UrlLocal); };
            copiarLocal.Click += delegate { Copiar(nucleo.UrlLocal, copiarLocal); };
            abrirRed.Click += delegate { if (nucleo.UrlRed != null) AbrirUrl(nucleo.UrlRed); };
            copiarRed.Click += delegate { if (nucleo.UrlRed != null) Copiar(nucleo.UrlRed, copiarRed); };
            abrirCarpeta.Click += delegate { AbrirCarpeta(); };
            cambiarCarpeta.Click += delegate { CambiarCarpeta(); };
            anadirCarpeta.Click += delegate { AnadirCarpeta(); };
            actualizarCaratulas.Click += delegate { ActualizarCaratulas(); };
            guardarTmdb.Click += delegate { GuardarTmdb(); };
            claveTmdb.KeyDown += delegate (object o, KeyEventArgs e) { if (e.KeyCode == Keys.Enter) { e.SuppressKeyPress = true; GuardarTmdb(); } };
            reidentificar.Click += delegate { var sv = nucleo.Servidor; if (sv != null) { sv.Identificacion.Reintentar(); PintarTmdb(); } };
            crearAdmin.Click += delegate { Administrador(false); };
            recuperar.Click += delegate { Administrador(true); };
            gestionar.Click += delegate { AbrirUrl(nucleo.UrlLocal + "/#/usuarios"); };
            aplicarPuerto.Click += delegate { CambiarPuerto(); };
            marcha.Click += delegate { AlternarMarcha(); };
            conWindows.CheckedChanged += delegate { if (!cargando) CambiarInicioConWindows(conWindows.Checked); };
            dlnaActivo.CheckedChanged += delegate { if (!cargando) CambiarDlna(dlnaActivo.Checked); };

            ResumeLayout(false);
            PerformLayout();

            // ── arranque ──
            nucleo.Iniciar();
            cargando = true;
            conWindows.Checked = InicioConWindows();
            cargando = false;
            Actualizar();
            PintarCarpetas();
            ContarPeliculas();

            // El identificador de ventana hace falta ya para recibir avisos de otros hilos (BeginInvoke).
            if (!IsHandleCreated) CreateHandle();
            foreach (var r in Registro.Recientes()) AnadirActividad(r.Key, r.Value);
            Registro.Nuevo += AlRegistrar;
            Dlna.Cambio += AlCambiarTeles;
            Dlna.NuevoAparato += AlPedirPermiso;
            PintarTeles();

            // Cada 15 s se repasa el estado (la IP de la wifi puede cambiar); las películas, cada minuto.
            reloj = new System.Windows.Forms.Timer { Interval = 15000 };
            int vueltas = 0;
            reloj.Tick += delegate { Actualizar(); if (++vueltas % 4 == 0) ContarPeliculas(); };
            reloj.Start();

            if (permitirVisible && !Programa.SinNavegador && nucleo.EnMarcha && nucleo.Config != null && nucleo.Config.AbrirNavegador)
            {
                var abrir = new System.Windows.Forms.Timer { Interval = 1000 };
                abrir.Tick += delegate { abrir.Stop(); abrir.Dispose(); AbrirUrl(nucleo.UrlLocal); };
                abrir.Start();
            }
        }

        // ── visibilidad y bandeja ────────────────────────────────────────

        protected override void SetVisibleCore(bool value)
        {
            // Con --minimizado la ventana empieza escondida: solo el icono de la bandeja.
            if (!permitirVisible)
            {
                value = false;
                if (!IsHandleCreated) CreateHandle();
            }
            base.SetVisibleCore(value);
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            Nativo.TituloOscuro(Handle);
        }

        bool ajustandoActividad;

        /// <summary>
        /// La actividad se queda con el alto que sobre, pero nunca menos de un mínimo: si no cabe
        /// todo (muchas carpetas o teles, pantalla baja), la ventana se desplaza en vez de esconderla.
        /// </summary>
        void AjustarActividad(TableLayoutPanel raiz)
        {
            if (ajustandoActividad || raiz.RowStyles.Count == 0) return;
            ajustandoActividad = true;
            try
            {
                int[] altos = raiz.GetRowHeights();
                int ultima = raiz.RowStyles.Count - 1, otros = 0;
                for (int i = 0; i < ultima && i < altos.Length; i++) otros += altos[i];
                int minimo = 140 * DeviceDpi / 96;
                int alto = Math.Max(minimo, raiz.ClientSize.Height - otros);
                RowStyle estilo = raiz.RowStyles[ultima];
                if (Math.Abs(estilo.Height - alto) > 1) estilo.Height = alto;
            }
            finally { ajustandoActividad = false; }
        }

        protected override void OnLoad(EventArgs e)
        {
            base.OnLoad(e);
            // Que no sea más alta que la pantalla (portátiles con el texto al 125 %, por ejemplo).
            Rectangle area = Screen.FromControl(this).WorkingArea;
            if (Height > area.Height)
            {
                MinimumSize = new Size(MinimumSize.Width, Math.Min(MinimumSize.Height, area.Height));
                Height = area.Height;
            }
            if (Top < area.Top || Bottom > area.Bottom) Top = area.Top;
        }

        public void MostrarVentana()
        {
            permitirVisible = true;
            Show();
            if (WindowState == FormWindowState.Minimized) WindowState = FormWindowState.Normal;
            Activate();
            BringToFront();
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            if (e.CloseReason == CloseReason.UserClosing && !saliendo)
            {
                // Cerrar la ventana no apaga MiCine: sigue en la bandeja.
                e.Cancel = true;
                Hide();
                if (!avisadoBandeja)
                {
                    avisadoBandeja = true;
                    bandeja.ShowBalloonTip(4000, "MiCine sigue funcionando",
                        "Las películas siguen disponibles. Para apagar MiCine: clic derecho en este icono → Salir.", ToolTipIcon.Info);
                }
                return;
            }
            base.OnFormClosing(e);
        }

        protected override void OnFormClosed(FormClosedEventArgs e)
        {
            Registro.Nuevo -= AlRegistrar;
            Dlna.Cambio -= AlCambiarTeles;
            Dlna.NuevoAparato -= AlPedirPermiso;
            if (reloj != null) reloj.Stop();
            bandeja.Visible = false;
            bandeja.Dispose();
            nucleo.Detener();
            base.OnFormClosed(e);
        }

        void Salir()
        {
            saliendo = true;
            Close();
        }

        // ── estado ───────────────────────────────────────────────────────

        void Actualizar()
        {
            bool enMarcha = nucleo.EnMarcha;
            string error = nucleo.Error;
            if (enMarcha) { estado.Text = "● En marcha"; estado.ForeColor = Tema.Exito; }
            else if (error != null) { estado.Text = "● Con problemas"; estado.ForeColor = Tema.Acento; }
            else { estado.Text = "● Detenido"; estado.ForeColor = Tema.Tenue; }
            detalleError.Visible = !enMarcha && error != null;
            detalleError.Text = error ?? "";
            detalleError.MaximumSize = new Size(Math.Max(200, ClientSize.Width - Padding.Horizontal), 0);
            bandeja.Text = enMarcha ? "MiCine · en marcha" : "MiCine · detenido";

            urlLocal.Text = nucleo.Puerto > 0 ? nucleo.UrlLocal : "";
            abrirLocal.Enabled = copiarLocal.Enabled = enMarcha;
            string red = enMarcha ? nucleo.UrlRed : null;
            if (red != null)
            {
                urlRed.Text = red;
                urlRed.ForeColor = Tema.Texto;
                pistaRed.Text = "Tienen que estar conectados a la misma wifi que este ordenador.\n" +
                    "En la app de Android de MiCine, escribe esta dirección como servidor.";
            }
            else
            {
                urlRed.ForeColor = Tema.Tenue;
                if (!enMarcha) { urlRed.Text = "—"; pistaRed.Text = "MiCine no está en marcha."; }
                else if (!nucleo.EnTodaLaRed)
                {
                    urlRed.Text = "Solo desde este ordenador";
                    pistaRed.Text = "MiCine escucha solo en " + nucleo.Direccion + ". Para entrar desde otros aparatos,\n" +
                        "pon \"direccion\": \"0.0.0.0\" en config.json.";
                }
                else
                {
                    urlRed.Text = "Sin conexión a la red";
                    pistaRed.Text = "Este ordenador no parece conectado a ninguna red ahora mismo.";
                }
            }
            abrirRed.Enabled = copiarRed.Enabled = red != null;
            // Con MiCine en la red, las direcciones cortas (llevan a GitHub); si no, las de GitHub directamente.
            urlTele.Text = red != null ? red + "/tele" : Apps.FIRE_TV;
            urlMovil.Text = red != null ? red + "/movil" : Apps.MOVIL;

            carpeta.Text = nucleo.Biblioteca != null ? nucleo.Biblioteca.Carpeta : "";
            abrirCarpeta.Enabled = nucleo.Biblioteca != null;
            cambiarCarpeta.Enabled = nucleo.Config != null;
            if (peliculas < 0) cuantas.Text = "Contando películas…";
            else if (peliculas == 0) cuantas.Text = "Todavía no hay películas: cópialas en esta carpeta.";
            else cuantas.Text = (peliculas == 1 ? "1 vídeo" : peliculas.ToString("N0", System.Globalization.CultureInfo.GetCultureInfo("es-ES")) + " vídeos") + TextoCaratulas();
            PintarTmdb();

            var almacen = nucleo.Almacen;
            if (almacen == null)
            {
                usuariosTexto.Text = "—";
                crearAdmin.Visible = gestionar.Visible = recuperar.Visible = false;
            }
            else if (!almacen.HayUsuarios())
            {
                usuariosTexto.Text = "Aún no hay ningún usuario.\nCrea el administrador para empezar.";
                usuariosTexto.ForeColor = Tema.Aviso;
                crearAdmin.Visible = true;
                gestionar.Visible = recuperar.Visible = false;
            }
            else
            {
                int n = almacen.CuantosUsuarios(), admins = almacen.CuantosAdministradores();
                usuariosTexto.Text = (n == 1 ? "1 usuario" : n + " usuarios") + " · " + (admins == 1 ? "1 administrador" : admins + " administradores");
                usuariosTexto.ForeColor = Tema.Texto;
                crearAdmin.Visible = false;
                gestionar.Visible = recuperar.Visible = true;
                gestionar.Enabled = enMarcha;
            }

            marcha.Text = enMarcha ? "Detener" : "Iniciar";
            Tema.Pintar(marcha, !enMarcha);
            if (!puerto.ContainsFocus && nucleo.Puerto > 0) puerto.Value = Math.Max(1, Math.Min(65535, nucleo.Puerto));
        }

        /// <summary>Cuenta las películas en segundo plano: en un disco lento o de red puede tardar.</summary>
        void ContarPeliculas()
        {
            var biblioteca = nucleo.Biblioteca;
            if (biblioteca == null || Interlocked.Exchange(ref contando, 1) == 1) return;
            ThreadPool.QueueUserWorkItem(delegate
            {
                int n = -1;
                try { n = biblioteca.Listar().Count; }
                catch (Exception) { }
                Interlocked.Exchange(ref contando, 0);
                if (n < 0 || !IsHandleCreated) return;
                // De paso se repasa si algún disco se ha conectado o desconectado.
                try { BeginInvoke((MethodInvoker)delegate { peliculas = n; Actualizar(); PintarCarpetas(); }); } catch (Exception) { }
            });
        }

        // ── actividad ────────────────────────────────────────────────────

        void AlRegistrar(DateTime cuando, string mensaje)
        {
            if (!IsHandleCreated) return;
            try
            {
                BeginInvoke((MethodInvoker)delegate
                {
                    AnadirActividad(cuando, mensaje);
                    if (mensaje.Contains("ha subido") || mensaje.Contains("papelera") || mensaje.Contains("usuario")
                        || mensaje.StartsWith("Administrador")) { Actualizar(); ContarPeliculas(); }
                });
            }
            catch (Exception) { }
        }

        void AnadirActividad(DateTime cuando, string mensaje)
        {
            string hora = cuando.Date == DateTime.Today ? cuando.ToString("HH:mm:ss") : cuando.ToString("dd/MM HH:mm");
            actividad.Items.Insert(0, hora + "   " + mensaje);
            while (actividad.Items.Count > Registro.MAXIMO_RECIENTES) actividad.Items.RemoveAt(actividad.Items.Count - 1);
        }

        // ── acciones ─────────────────────────────────────────────────────

        static void AbrirUrl(string url)
        {
            try { Process.Start(url); }
            catch (Exception e) { MessageBox.Show("No se pudo abrir el navegador: " + e.Message, "MiCine", MessageBoxButtons.OK, MessageBoxIcon.Warning); }
        }

        static void Copiar(string texto, Button boton)
        {
            try { Clipboard.SetText(texto); }
            catch (Exception) { return; }
            string antes = boton.Text;
            boton.Text = "Copiado ✓";
            var t = new System.Windows.Forms.Timer { Interval = 1500 };
            t.Tick += delegate { t.Stop(); t.Dispose(); boton.Text = antes; };
            t.Start();
        }

        void AbrirCarpeta()
        {
            if (nucleo.Biblioteca == null) return;
            try { Process.Start("explorer.exe", "\"" + nucleo.Biblioteca.Carpeta + "\""); }
            catch (Exception e) { MessageBox.Show(e.Message, "MiCine", MessageBoxButtons.OK, MessageBoxIcon.Warning); }
        }

        void CambiarCarpeta()
        {
            using (var d = new FolderBrowserDialog())
            {
                d.Description = "Elige la carpeta donde están (o donde quieres guardar) tus películas.";
                d.ShowNewFolderButton = true;
                if (nucleo.Biblioteca != null) d.SelectedPath = nucleo.Biblioteca.Carpeta;
                if (d.ShowDialog(this) != DialogResult.OK || string.IsNullOrEmpty(d.SelectedPath)) return;
                string elegida = Path.GetFullPath(d.SelectedPath);
                if (nucleo.Biblioteca != null && string.Equals(elegida.TrimEnd('\\'), nucleo.Biblioteca.Carpeta.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase)) return;
                string choca = OtrasCarpetas().FirstOrDefault(o => Biblioteca.Solapan(o, elegida));
                if (choca != null)
                {
                    MessageBox.Show(this, "Esa carpeta choca con «" + choca + "», que ya está en «También se leen estas carpetas» (una está dentro de la otra).\n\n" +
                        "Quítala de esa lista primero, o elige otra.", "MiCine", MessageBoxButtons.OK, MessageBoxIcon.Information);
                    return;
                }
                try { Configuracion.Guardar(nucleo.RutaConfig, "carpeta_peliculas", ParaGuardar(elegida)); }
                catch (Exception e)
                {
                    MessageBox.Show("No se pudo guardar config.json: " + e.Message, "MiCine", MessageBoxButtons.OK, MessageBoxIcon.Error);
                    return;
                }
                Registro.Anotar("Carpeta de películas cambiada a «" + elegida + "»");
                Reiniciar();
            }
        }

        /// <summary>Dentro de la carpeta de MiCine se guarda relativa: así se puede mover todo junto.</summary>
        string ParaGuardar(string elegida)
        {
            string prefijo = nucleo.Base.TrimEnd('\\') + "\\";
            if (elegida.StartsWith(prefijo, StringComparison.OrdinalIgnoreCase) && elegida.Length > prefijo.Length)
                return elegida.Substring(prefijo.Length);
            return elegida;
        }

        /// <summary>Las carpetas añadidas, ya como rutas completas (vacío si config.json no se puede leer).</summary>
        List<string> OtrasCarpetas()
        {
            try { return nucleo.Config == null ? new List<string>() : nucleo.Config.OtrasCarpetas(nucleo.Base).Select(Path.GetFullPath).ToList(); }
            catch (Exception) { return new List<string>(); }
        }

        void PintarCarpetas()
        {
            List<string> textos;
            string error = null;
            try { textos = nucleo.Config == null ? new List<string>() : nucleo.Config.OtrasCarpetasTexto; }
            catch (FormatException e) { textos = new List<string>(); error = e.Message; }
            var biblioteca = nucleo.Biblioteca;

            // (ruta completa, texto guardado, problema)
            var filas = new List<string[]>();
            foreach (string texto in textos)
            {
                string completa;
                try { completa = Path.GetFullPath(Configuracion.RutaDeConfig(texto, nucleo.Base)); }
                catch (Exception) { filas.Add(new[] { texto, texto, "no es una ruta válida" }); continue; }
                string problema = null;
                if (!Directory.Exists(completa)) problema = "no se encuentra: ¿disco desconectado?";
                else if (biblioteca != null && !biblioteca.Raices.Any(r => r.Prefijo == "." + Biblioteca.IdCarpeta(completa) + "/"))
                    problema = "no se lee: está dentro de otra carpeta de MiCine o la contiene";
                filas.Add(new[] { completa, texto, problema });
            }
            string firma = (error ?? "") + "|" + string.Join("|", filas.Select(f => f[0] + "?" + f[2]));
            if (firma == firmaCarpetas) return;
            firmaCarpetas = firma;

            listaCarpetas.SuspendLayout();
            var viejos = new List<Control>();
            foreach (Control c in listaCarpetas.Controls) viejos.Add(c);
            listaCarpetas.Controls.Clear();
            foreach (var c in viejos) c.Dispose();
            listaCarpetas.RowStyles.Clear();
            listaCarpetas.RowCount = 0;
            foreach (var f in filas)
            {
                string completa = f[0], texto = f[1], problema = f[2];
                var campo = Tema.Campo(Tema.Letra(9.75f));
                // El nombre y el problema delante: las rutas largas se cortan por el final.
                string nombre;
                try { nombre = Biblioteca.NombreCarpeta(completa); } catch (Exception) { nombre = texto; }
                campo.Text = nombre + (problema == null ? "" : "  ·  " + problema) + "  ·  " + completa;
                campo.ForeColor = problema == null ? Tema.Texto : Tema.Aviso;
                var abrir = Tema.Boton("Abrir", false);
                abrir.Enabled = Directory.Exists(completa);
                abrir.Click += delegate { try { Process.Start("explorer.exe", "\"" + completa + "\""); } catch (Exception) { } };
                var quitar = Tema.Boton("Quitar", false);
                quitar.Click += delegate { QuitarCarpeta(texto, completa); };
                Tema.Fila(listaCarpetas, campo, Tema.Botonera(abrir, quitar));
            }
            etiquetaOtras.Text = error ?? "También se leen estas carpetas";
            etiquetaOtras.ForeColor = error != null ? Tema.Aviso : Tema.Tenue;
            etiquetaOtras.Visible = error != null || filas.Count > 0;
            listaCarpetas.Visible = filas.Count > 0;
            anadirCarpeta.Enabled = nucleo.Config != null && error == null;
            listaCarpetas.ResumeLayout();
        }

        string TextoCaratulas()
        {
            var sv = nucleo.Servidor;
            if (sv == null || !sv.Caratulas.Disponible) return "";
            int n = sv.Caratulas.Pendientes;
            return n > 0 ? " · haciendo carátulas del vídeo: quedan " + n.ToString("N0", System.Globalization.CultureInfo.GetCultureInfo("es-ES")) : "";
        }

        void ActualizarCaratulas()
        {
            var sv = nucleo.Servidor;
            if (sv == null) return;
            if (!sv.Caratulas.Disponible)
            {
                MessageBox.Show(this, "Para sacar las carátulas del vídeo hace falta FFmpeg (carpeta ffmpeg junto a MiCine.exe; ver LEEME.md).",
                    "MiCine", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            if (MessageBox.Show(this, "Se volverán a sacar las carátulas de todos los vídeos que no tienen imagen propia.\n" +
                "Tarda un rato (se hace poco a poco, sin ralentizar el ordenador). ¿Seguir?", "MiCine",
                MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
            int n = sv.Caratulas.Rehacer();
            Registro.Anotar("Rehaciendo las carátulas del vídeo (" + n + ")");
            Actualizar();
        }

        void GuardarTmdb()
        {
            if (nucleo.Config == null) return;
            string clave = claveTmdb.Text.Trim();
            try { Configuracion.Guardar(nucleo.RutaConfig, "tmdb", clave); }
            catch (Exception e)
            {
                MessageBox.Show(this, "No se pudo guardar la clave en config.json: " + e.Message, "MiCine", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
            nucleo.Config.Valores["tmdb"] = clave;
            var sv = nucleo.Servidor;
            if (sv != null) sv.Identificacion.CambiarClave(clave);
            Registro.Anotar(clave == "" ? "Clave de TMDB quitada: se separa por el nombre del archivo" : "Clave de TMDB guardada: identificando películas y series…");
            PintarTmdb();
        }

        void PintarTmdb()
        {
            var sv = nucleo.Servidor;
            if (!claveTmdb.Focused && nucleo.Config != null && claveTmdb.Text == "" && nucleo.Config.Tmdb != "") claveTmdb.Text = nucleo.Config.Tmdb;
            guardarTmdb.Enabled = nucleo.Config != null;
            reidentificar.Enabled = sv != null && sv.Identificacion.ConClave;
            if (sv == null) { tmdbTexto.Text = "MiCine no está en marcha."; tmdbTexto.ForeColor = Tema.Tenue; return; }
            var id = sv.Identificacion;
            var es = System.Globalization.CultureInfo.GetCultureInfo("es-ES");
            string estado = id.Estado;
            if (!id.ConClave)
            {
                tmdbTexto.Text = "Sin clave: se separan series y películas por el nombre del archivo.";
                tmdbTexto.ForeColor = Tema.Tenue;
            }
            else if (estado != null && estado != "Identificando con TMDB…")
            {
                tmdbTexto.Text = estado;
                tmdbTexto.ForeColor = Tema.Aviso;
            }
            else if (id.Pendientes > 0)
            {
                tmdbTexto.Text = "Identificando con TMDB… quedan " + id.Pendientes.ToString("N0", es) + " consultas";
                tmdbTexto.ForeColor = Tema.Tenue;
            }
            else
            {
                tmdbTexto.Text = "Identificados " + id.Identificados.ToString("N0", es) + " de " + id.Total.ToString("N0", es) + " vídeos" +
                    (id.EnMiCine > 0 ? " · " + id.EnMiCine.ToString("N0", es) + " sin identificar (en «MiCine»)" : "");
                tmdbTexto.ForeColor = id.EnMiCine > 0 ? Tema.Tenue : Tema.Exito;
            }
        }

        void GuardarOtras(List<string> textos)
        {
            Configuracion.Guardar(nucleo.RutaConfig, "otras_carpetas", textos.Cast<object>().ToList());
        }

        void AnadirCarpeta()
        {
            if (nucleo.Config == null) return;
            using (var d = new FolderBrowserDialog())
            {
                d.Description = "Elige otra carpeta con películas (en otro disco, por ejemplo). Sus películas saldrán junto a las demás.";
                d.ShowNewFolderButton = false;
                if (d.ShowDialog(this) != DialogResult.OK || string.IsNullOrEmpty(d.SelectedPath)) return;
                string elegida = Path.GetFullPath(d.SelectedPath);
                if (nucleo.Biblioteca != null && Biblioteca.Solapan(nucleo.Biblioteca.Carpeta, elegida))
                {
                    MessageBox.Show(this, "Esa carpeta es la principal, está dentro de ella o la contiene: sus películas ya salen en MiCine.",
                        "MiCine", MessageBoxButtons.OK, MessageBoxIcon.Information);
                    return;
                }
                string choca = OtrasCarpetas().FirstOrDefault(o => Biblioteca.Solapan(o, elegida));
                if (choca != null)
                {
                    MessageBox.Show(this, "Esa carpeta ya está en la lista, o choca con «" + choca + "» (una está dentro de la otra).",
                        "MiCine", MessageBoxButtons.OK, MessageBoxIcon.Information);
                    return;
                }
                var textos = nucleo.Config.OtrasCarpetasTexto;
                textos.Add(ParaGuardar(elegida));
                try { GuardarOtras(textos); }
                catch (Exception e)
                {
                    MessageBox.Show("No se pudo guardar config.json: " + e.Message, "MiCine", MessageBoxButtons.OK, MessageBoxIcon.Error);
                    return;
                }
                Registro.Anotar("Carpeta añadida: «" + elegida + "»");
                Reiniciar();
            }
        }

        void QuitarCarpeta(string texto, string completa)
        {
            if (nucleo.Config == null) return;
            if (MessageBox.Show(this, "¿Quitar «" + completa + "» de MiCine?\n\nNo se borra nada: sus películas solo dejan de salir en MiCine.",
                    "MiCine", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
            var textos = nucleo.Config.OtrasCarpetasTexto;
            textos.Remove(texto);
            try { GuardarOtras(textos); }
            catch (Exception e)
            {
                MessageBox.Show("No se pudo guardar config.json: " + e.Message, "MiCine", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return;
            }
            Registro.Anotar("Carpeta quitada de MiCine: «" + completa + "»");
            Reiniciar();
        }

        void CambiarPuerto()
        {
            int nuevo = (int)puerto.Value;
            if (nuevo == nucleo.Puerto && nucleo.EnMarcha) return;
            try { Configuracion.Guardar(nucleo.RutaConfig, "puerto", (long)nuevo); }
            catch (Exception e)
            {
                MessageBox.Show("No se pudo guardar config.json: " + e.Message, "MiCine", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return;
            }
            nucleo.PuertoForzado = 0;
            Reiniciar();
        }

        void Reiniciar()
        {
            Cursor = Cursors.WaitCursor;
            try { nucleo.Iniciar(); }
            finally { Cursor = Cursors.Default; }
            peliculas = -1;
            Actualizar();
            PintarCarpetas();
            ContarPeliculas();
            PintarTeles();
        }

        void AlternarMarcha()
        {
            if (nucleo.EnMarcha)
            {
                nucleo.Detener();
                Actualizar();
                PintarTeles();
            }
            else Reiniciar();
        }

        // ── teles con DLNA ───────────────────────────────────────────────

        void AlCambiarTeles()
        {
            if (!IsHandleCreated) return;
            try { BeginInvoke((MethodInvoker)PintarTeles); } catch (Exception) { }
        }

        void AlPedirPermiso(Aparato a)
        {
            if (!IsHandleCreated) return;
            try
            {
                BeginInvoke((MethodInvoker)delegate
                {
                    bandeja.ShowBalloonTip(15000, "Una tele quiere ver MiCine",
                        "«" + a.Nombre + "» (" + a.Ip + "). Pulsa aquí y dale permiso en la ventana de MiCine.", ToolTipIcon.Info);
                    PintarTeles();
                });
            }
            catch (Exception) { }
        }

        void CambiarDlna(bool activar)
        {
            try { Configuracion.Guardar(nucleo.RutaConfig, "dlna", activar); }
            catch (Exception e)
            {
                MessageBox.Show("No se pudo guardar config.json: " + e.Message, "MiCine", MessageBoxButtons.OK, MessageBoxIcon.Error);
                PintarTeles();
                return;
            }
            if (nucleo.Config != null) nucleo.Config.Valores["dlna"] = activar;
            nucleo.AplicarDlna();
            if (!activar) Registro.Anotar("DLNA desactivado: las teles ya no ven MiCine en su lista de fuentes");
            PintarTeles();
        }

        void PintarTeles()
        {
            var dlna = nucleo.Dlna;
            cargando = true;
            dlnaActivo.Checked = nucleo.Config == null || nucleo.Config.Dlna;
            dlnaActivo.Enabled = nucleo.EnMarcha;
            cargando = false;

            listaTeles.SuspendLayout();
            var viejos = new List<Control>();
            foreach (Control c in listaTeles.Controls) viejos.Add(c);
            listaTeles.Controls.Clear();
            foreach (var c in viejos) c.Dispose();
            listaTeles.RowStyles.Clear();
            listaTeles.RowCount = 0;

            List<Aparato> aparatos = dlna != null ? dlna.ListaAparatos() : new List<Aparato>();
            if (!nucleo.EnMarcha) telesTexto.Text = "MiCine no está en marcha.";
            else if (nucleo.Config != null && !nucleo.Config.Dlna) telesTexto.Text = "Desactivado: las teles no ven MiCine en su lista de fuentes.";
            else if (dlna == null) telesTexto.Text = nucleo.ErrorDlna ?? "DLNA no está en marcha.";
            else if (aparatos.Count == 0)
                telesTexto.Text = "Para teles sin la app de MiCine (como las Samsung de antes de 2015): en la tele,\n" +
                    "abre Fuente o Multimedia → Vídeos y elige «MiCine». La primera vez, dale permiso aquí.";
            else telesTexto.Text = "En la tele: Fuente o Multimedia → Vídeos → «MiCine». Cada aparato necesita tu permiso.";
            telesTexto.ForeColor = dlna == null && nucleo.ErrorDlna != null ? Tema.Aviso : Tema.Tenue;

            foreach (var a in aparatos)
            {
                string clave = a.Clave;
                string estadoTexto = a.Estado == Dlna.PERMITIDO ? "Puede ver MiCine" : a.Estado == Dlna.BLOQUEADO ? "Sin permiso" : "Pide permiso";
                var etiqueta = Tema.Etiqueta(a.Nombre + "   ·   " + a.Ip + "\n" + estadoTexto,
                    a.Estado == Dlna.PERMITIDO ? Tema.Exito : a.Estado == Dlna.BLOQUEADO ? Tema.Tenue : Tema.Aviso, Tema.Letra(9.5f));
                etiqueta.Anchor = AnchorStyles.Left;
                var botones = new List<Control>();
                if (a.Estado != Dlna.PERMITIDO)
                {
                    var permitir = Tema.Boton("Permitir", a.Estado == Dlna.PENDIENTE);
                    permitir.Click += delegate { var d = nucleo.Dlna; if (d != null) d.CambiarEstado(clave, Dlna.PERMITIDO); };
                    botones.Add(permitir);
                }
                if (a.Estado == Dlna.PENDIENTE)
                {
                    var rechazar = Tema.Boton("Rechazar", false);
                    rechazar.Click += delegate { var d = nucleo.Dlna; if (d != null) d.CambiarEstado(clave, Dlna.BLOQUEADO); };
                    botones.Add(rechazar);
                }
                if (a.Estado == Dlna.PERMITIDO)
                {
                    var quitar = Tema.Boton("Quitar permiso", false);
                    quitar.Click += delegate { var d = nucleo.Dlna; if (d != null) d.CambiarEstado(clave, Dlna.BLOQUEADO); };
                    botones.Add(quitar);
                }
                if (a.Estado == Dlna.BLOQUEADO)
                {
                    var olvidar = Tema.Boton("Olvidar", false);
                    olvidar.Click += delegate { var d = nucleo.Dlna; if (d != null) d.Olvidar(clave); };
                    botones.Add(olvidar);
                }
                Tema.Fila(listaTeles, etiqueta, Tema.Botonera(botones.ToArray()));
            }
            listaTeles.Visible = aparatos.Count > 0;
            listaTeles.ResumeLayout();
        }

        void Administrador(bool recuperando)
        {
            if (nucleo.Almacen == null) return;
            using (var d = new FormAdministrador(nucleo.Almacen, recuperando))
            {
                if (d.ShowDialog(this) != DialogResult.OK) return;
                Actualizar();
                if (nucleo.EnMarcha && MessageBox.Show(this, d.Resultado + "\n\n¿Abrir MiCine en el navegador para entrar?", "MiCine",
                        MessageBoxButtons.YesNo, MessageBoxIcon.Information) == DialogResult.Yes)
                    AbrirUrl(nucleo.UrlLocal);
            }
        }

        // ── inicio con Windows ───────────────────────────────────────────

        static string OrdenInicio { get { return "\"" + Application.ExecutablePath + "\" --minimizado"; } }

        static bool InicioConWindows()
        {
            try
            {
                using (var k = Registry.CurrentUser.OpenSubKey(CLAVE_INICIO))
                {
                    var v = k == null ? null : k.GetValue(VALOR_INICIO) as string;
                    return v != null && v.IndexOf(Application.ExecutablePath, StringComparison.OrdinalIgnoreCase) >= 0;
                }
            }
            catch (Exception) { return false; }
        }

        void CambiarInicioConWindows(bool activar)
        {
            try
            {
                using (var k = Registry.CurrentUser.CreateSubKey(CLAVE_INICIO))
                {
                    if (activar) k.SetValue(VALOR_INICIO, OrdenInicio);
                    else if (k.GetValue(VALOR_INICIO) != null) k.DeleteValue(VALOR_INICIO, false);
                }
            }
            catch (Exception e)
            {
                MessageBox.Show("No se pudo cambiar el inicio con Windows: " + e.Message, "MiCine", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                cargando = true;
                conWindows.Checked = InicioConWindows();
                cargando = false;
            }
        }

        protected override void OnResize(EventArgs e)
        {
            base.OnResize(e);
            if (detalleError != null) detalleError.MaximumSize = new Size(Math.Max(200, ClientSize.Width - Padding.Horizontal), 0);
        }
    }
}
