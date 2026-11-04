using System;
using System.Drawing;
using System.Windows.Forms;

namespace MiCine
{
    /// <summary>
    /// Crear el primer administrador (o recuperar uno) desde la propia
    /// ventana: es lo que hace «servidor.py --admin». Como MiCine está en este
    /// ordenador, escribe directamente en datos\usuarios.json, con el mismo
    /// formato, y la web lo ve al momento.
    /// </summary>
    public sealed class FormAdministrador : Form
    {
        readonly Almacen almacen;
        readonly bool recuperando;
        readonly TextBox usuario, contrasena, repetir;
        readonly Label error;

        /// <summary>Lo que ha pasado, para enseñarlo al cerrar.</summary>
        public string Resultado { get; private set; }

        public FormAdministrador(Almacen almacen, bool recuperando)
        {
            this.almacen = almacen;
            this.recuperando = recuperando;

            SuspendLayout();
            AutoScaleDimensions = new SizeF(96f, 96f);
            AutoScaleMode = AutoScaleMode.Dpi;
            Text = recuperando ? "Recuperar administrador" : "Crear administrador";
            Icon = Tema.Icono(32);
            BackColor = Tema.Fondo;
            ForeColor = Tema.Texto;
            Font = Tema.Letra(9.75f);
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = MinimizeBox = false;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.CenterParent;
            AutoSize = true;
            AutoSizeMode = AutoSizeMode.GrowAndShrink;
            Padding = new Padding(18, 16, 18, 14);

            var t = new TableLayoutPanel { AutoSize = true, ColumnCount = 1, Dock = DockStyle.Fill, BackColor = Tema.Fondo };
            t.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 380f));
            var titulo = Tema.Etiqueta(Text, Tema.Texto, Tema.Negrita(13f));
            var explicacion = Tema.Etiqueta(recuperando
                ? "Escribe el nombre de un administrador para ponerle una contraseña nueva\n(se cerrarán sus sesiones abiertas), o un nombre nuevo para crearlo."
                : "Con esta cuenta podrás subir películas y dar acceso a otras personas.", Tema.Tenue, Tema.Letra(9f));
            explicacion.Margin = new Padding(0, 2, 0, 12);
            usuario = Caja(false);
            contrasena = Caja(true);
            repetir = Caja(true);
            error = Tema.Etiqueta("", Tema.Acento, Tema.Letra(9f));
            error.MaximumSize = new Size(380, 0);
            var aceptar = Tema.Boton(recuperando ? "Guardar" : "Crear administrador", true);
            var cancelar = Tema.Boton("Cancelar", false);
            cancelar.DialogResult = DialogResult.Cancel;
            aceptar.Click += delegate { Aceptar(); };
            var botones = Tema.Botonera(cancelar, aceptar);
            botones.Margin = new Padding(0, 10, 0, 0);

            Control[] filas =
            {
                titulo, explicacion,
                Tema.Etiqueta("Usuario", Tema.Tenue, Tema.Letra(9f)), usuario,
                Tema.Etiqueta("Contraseña (mínimo 6 caracteres)", Tema.Tenue, Tema.Letra(9f)), contrasena,
                Tema.Etiqueta("Repite la contraseña", Tema.Tenue, Tema.Letra(9f)), repetir,
                error, botones,
            };
            t.RowCount = filas.Length;
            for (int i = 0; i < filas.Length; i++)
            {
                t.RowStyles.Add(new RowStyle(SizeType.AutoSize));
                t.Controls.Add(filas[i], 0, i);
            }
            Controls.Add(t);
            AcceptButton = aceptar;
            CancelButton = cancelar;
            ResumeLayout(false);
            PerformLayout();
        }

        static TextBox Caja(bool secreta)
        {
            return new TextBox
            {
                UseSystemPasswordChar = secreta,
                BackColor = Tema.Panel2,
                ForeColor = Tema.Texto,
                BorderStyle = BorderStyle.FixedSingle,
                Font = Tema.Letra(10.5f),
                Dock = DockStyle.Fill,
                Margin = new Padding(0, 2, 0, 10),
            };
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            Nativo.TituloOscuro(Handle);
        }

        void Aceptar()
        {
            error.Text = "";
            string nombre, clave;
            try
            {
                nombre = Manejador.ValidarNombre(usuario.Text);
                clave = Manejador.ValidarContrasena(contrasena.Text);
            }
            catch (ErrorPeticion e)
            {
                error.Text = e.Message;
                return;
            }
            if (repetir.Text != contrasena.Text)
            {
                error.Text = "Las contraseñas no coinciden.";
                repetir.Focus();
                repetir.SelectAll();
                return;
            }
            Cursor = Cursors.WaitCursor;
            try
            {
                if (almacen.Existe(nombre))
                {
                    if (!recuperando)
                    {
                        error.Text = "Ese usuario ya existe.";
                        return;
                    }
                    almacen.FijarContrasena(nombre, clave, "admin");
                    almacen.CerrarSesionesDe(nombre, null);
                    Resultado = "Contraseña de «" + nombre + "» cambiada. Ahora es administrador.";
                    Registro.Anotar("Contraseña de «" + nombre + "» cambiada desde la ventana de MiCine (administrador)");
                }
                else
                {
                    almacen.CrearUsuario(nombre, clave, "admin");
                    Resultado = "Administrador «" + nombre + "» creado.";
                    Registro.Anotar("Administrador «" + nombre + "» creado desde la ventana de MiCine");
                }
            }
            catch (ErrorPeticion e)
            {
                error.Text = e.Message;
                return;
            }
            catch (Exception e)
            {
                error.Text = "No se pudo guardar: " + e.Message;
                return;
            }
            finally { Cursor = Cursors.Default; }
            DialogResult = DialogResult.OK;
            Close();
        }
    }
}
