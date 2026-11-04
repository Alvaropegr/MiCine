using System;
using System.Drawing;
using System.IO;
using System.Reflection;
using System.Windows.Forms;

namespace MiCine
{
    /// <summary>Colores y controles con el mismo aspecto oscuro que la web (estilos.css).</summary>
    public static class Tema
    {
        public static readonly Color Fondo = Color.FromArgb(0x0E, 0x10, 0x14);
        public static readonly Color Panel = Color.FromArgb(0x17, 0x1A, 0x21);
        public static readonly Color Panel2 = Color.FromArgb(0x1F, 0x23, 0x2C);
        public static readonly Color Borde = Color.FromArgb(0x2B, 0x30, 0x3B);
        public static readonly Color Texto = Color.FromArgb(0xEC, 0xEE, 0xF3);
        public static readonly Color Tenue = Color.FromArgb(0x9A, 0xA1, 0xAE);
        public static readonly Color Acento = Color.FromArgb(0xE5, 0x48, 0x4D);
        public static readonly Color AcentoClaro = Color.FromArgb(0xF0, 0x59, 0x5E);
        public static readonly Color AcentoOscuro = Color.FromArgb(0xC9, 0x3A, 0x3F);
        public static readonly Color Exito = Color.FromArgb(0x3E, 0xCF, 0x8E);
        public static readonly Color Aviso = Color.FromArgb(0xF5, 0xA5, 0x24);

        public static Font Letra(float puntos) { return new Font("Segoe UI", puntos, FontStyle.Regular, GraphicsUnit.Point); }
        public static Font Negrita(float puntos) { return new Font("Segoe UI Semibold", puntos, FontStyle.Regular, GraphicsUnit.Point); }

        public static Button Boton(string texto, bool principal)
        {
            var b = new Button
            {
                Text = texto,
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink,
                FlatStyle = FlatStyle.Flat,
                UseVisualStyleBackColor = false,
                Cursor = Cursors.Hand,
                Margin = new Padding(6, 2, 0, 2),
                Padding = new Padding(8, 2, 8, 2),
                Font = Negrita(9.5f),
            };
            b.FlatAppearance.BorderSize = 1;
            Pintar(b, principal);
            return b;
        }

        /// <summary>Cambia un botón entre principal (rojo) y secundario (gris).</summary>
        public static void Pintar(Button b, bool principal)
        {
            if (principal)
            {
                b.BackColor = Acento;
                b.ForeColor = Color.White;
                b.FlatAppearance.BorderColor = Acento;
                b.FlatAppearance.MouseOverBackColor = AcentoClaro;
                b.FlatAppearance.MouseDownBackColor = AcentoOscuro;
            }
            else
            {
                b.BackColor = Panel2;
                b.ForeColor = Texto;
                b.FlatAppearance.BorderColor = Borde;
                b.FlatAppearance.MouseOverBackColor = Color.FromArgb(0x27, 0x2C, 0x37);
                b.FlatAppearance.MouseDownBackColor = Color.FromArgb(0x30, 0x36, 0x44);
            }
        }

        public static Label Etiqueta(string texto, Color color, Font letra)
        {
            return new Label
            {
                Text = texto,
                AutoSize = true,
                ForeColor = color,
                Font = letra,
                BackColor = Color.Transparent,
                Margin = new Padding(0, 2, 0, 2),
            };
        }

        /// <summary>Título pequeño en mayúsculas de cada tarjeta.</summary>
        public static Label Titulo(string texto)
        {
            var l = Etiqueta(texto.ToUpperInvariant(), Tenue, Negrita(8.25f));
            l.Margin = new Padding(0, 0, 0, 6);
            return l;
        }

        /// <summary>
        /// TextBox que no pide más ancho que el que tiene: el normal pide el
        /// ancho de todo su texto, y una ruta larga ensanchaba la ventana entera
        /// por encima de su tamaño (los botones quedaban fuera).
        /// </summary>
        sealed class CampoTexto : TextBox
        {
            public override Size GetPreferredSize(Size propuesto)
            {
                Size s = base.GetPreferredSize(propuesto);
                return new Size(Math.Min(s.Width, 60), s.Height);
            }
        }

        /// <summary>Texto de solo lectura que se puede seleccionar y copiar (direcciones, carpeta).</summary>
        public static TextBox Campo(Font letra)
        {
            return new CampoTexto
            {
                ReadOnly = true,
                BorderStyle = BorderStyle.None,
                BackColor = Panel,
                ForeColor = Texto,
                Font = letra,
                Anchor = AnchorStyles.Left | AnchorStyles.Right,
                Margin = new Padding(0, 4, 8, 4),
                TabStop = false,
            };
        }

        /// <summary>Tarjeta: panel con dos columnas (contenido que se estira y botones).</summary>
        public static TableLayoutPanel Tarjeta()
        {
            var t = new TableLayoutPanel
            {
                BackColor = Panel,
                Dock = DockStyle.Fill,
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink,
                ColumnCount = 2,
                Padding = new Padding(14, 12, 14, 12),
                Margin = new Padding(0, 0, 0, 10),
            };
            t.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
            t.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            return t;
        }

        /// <summary>Añade una fila a una tarjeta: un control a todo lo ancho, o contenido + botones.</summary>
        public static void Fila(TableLayoutPanel t, Control izquierda, Control derecha)
        {
            int fila = t.RowCount;
            t.RowCount = fila + 1;
            t.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            t.Controls.Add(izquierda, 0, fila);
            if (derecha == null) t.SetColumnSpan(izquierda, 2);
            else t.Controls.Add(derecha, 1, fila);
        }

        public static FlowLayoutPanel Botonera(params Control[] controles)
        {
            var f = new FlowLayoutPanel
            {
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink,
                WrapContents = false,
                FlowDirection = FlowDirection.LeftToRight,
                BackColor = Color.Transparent,
                Margin = new Padding(0),
                Anchor = AnchorStyles.Right,
            };
            f.Controls.AddRange(controles);
            return f;
        }

        /// <summary>El icono de MiCine (el mismo del .exe), al tamaño pedido.</summary>
        public static Icon Icono(int lado)
        {
            using (Stream s = Assembly.GetExecutingAssembly().GetManifestResourceStream("MiCine.ico"))
            {
                if (s == null) return SystemIcons.Application;
                return new Icon(s, new Size(lado, lado));
            }
        }
    }
}
