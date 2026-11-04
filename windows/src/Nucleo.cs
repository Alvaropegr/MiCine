using System;
using System.IO;
using System.Linq;

namespace MiCine
{
    /// <summary>
    /// Lo que comparten la ventana y el modo sin ventana: config.json, la
    /// carpeta datos\, la biblioteca y el servidor que escucha. La carpeta
    /// base es la del .exe, la misma que la de servidor.py.
    /// </summary>
    public sealed class Nucleo
    {
        public readonly string Base;
        public readonly string RutaConfig;
        public readonly string Datos;

        /// <summary>--puerto y --direccion de la línea de órdenes (0 / null = lo de config.json).</summary>
        public int PuertoForzado;
        public string DireccionForzada;

        public Configuracion Config { get; private set; }
        public Almacen Almacen { get; private set; }
        public Biblioteca Biblioteca { get; private set; }
        public Servidor Servidor { get; private set; }
        /// <summary>El servidor DLNA para las teles (null si está desactivado o no se pudo poner en marcha).</summary>
        public Dlna Dlna { get; private set; }
        /// <summary>Por qué no está en marcha el DLNA (null si lo está o si se desactivó a propósito).</summary>
        public string ErrorDlna { get; private set; }

        /// <summary>Por qué no está en marcha (null si lo está o si se paró a propósito).</summary>
        public string Error { get; private set; }
        public int Puerto { get; private set; }
        public string Direccion { get; private set; }

        public Nucleo(string baseDir)
        {
            Base = baseDir;
            RutaConfig = Path.Combine(baseDir, "config.json");
            Datos = Path.Combine(baseDir, "datos");
        }

        public bool EnMarcha { get { return Servidor != null && Servidor.Activo; } }

        /// <summary>
        /// Lee config.json y los datos, y empieza a escuchar. Si algo falla,
        /// devuelve el motivo (y lo deja en <see cref="Error"/>).
        /// </summary>
        public string Iniciar()
        {
            Detener();
            Error = null;
            try
            {
                Config = Configuracion.Cargar(RutaConfig);
                Almacen = new Almacen(Datos, Config.DuracionSesion);
                Biblioteca = new Biblioteca(Config.CarpetaPeliculas(Base), Config.OtrasCarpetas(Base));
                Puerto = PuertoForzado > 0 ? PuertoForzado : Config.Puerto;
                Direccion = !string.IsNullOrEmpty(DireccionForzada) ? DireccionForzada : Config.Direccion;
            }
            catch (ErrorJson e)
            {
                Error = "Hay un archivo JSON dañado (config.json o datos\\): " + e.Message;
                return Error;
            }
            catch (Exception e)
            {
                Error = "No se pudo preparar MiCine: " + e.Message;
                return Error;
            }
            var conversor = Conversor.Buscar(Base);
            Servidor = new Servidor(Base, Config, Almacen, Biblioteca, conversor);
            Error = Servidor.Arrancar(Direccion, Puerto);
            if (Error != null)
            {
                Registro.Anotar(Error);
                return Error;
            }
            Registro.Anotar("MiCine en marcha en http://" + (Direccion == "0.0.0.0" || Direccion == "" ? "localhost" : Direccion) + ":" + Puerto
                + " · películas en " + Biblioteca.Carpeta);
            foreach (var r in Biblioteca.Raices.Skip(1))
                Registro.Anotar("También se leen las películas de " + r.Ruta + (Directory.Exists(r.Ruta) ? "" : " (no se encuentra: ¿disco desconectado?)"));
            if (conversor == null) Registro.Anotar("Sin FFmpeg: la tele y el navegador solo verán los formatos que entiendan (ver LEEME.md)");
            AplicarDlna();
            return null;
        }

        /// <summary>Pone en marcha o para el DLNA según config.json, sin tocar el resto.</summary>
        public void AplicarDlna()
        {
            PararDlna();
            ErrorDlna = null;
            if (!EnMarcha || !Config.Dlna) return;
            var dlna = new Dlna(Datos, Servidor);
            string error = dlna.Arrancar();
            if (error != null)
            {
                ErrorDlna = error;
                Registro.Anotar(error);
                return;
            }
            Dlna = dlna;
            Servidor.Dlna = dlna;
            Registro.Anotar("Las teles de casa ya pueden encontrar MiCine por DLNA");
        }

        void PararDlna()
        {
            if (Dlna == null) return;
            if (Servidor != null) Servidor.Dlna = null;
            Dlna.Parar();
            Dlna = null;
        }

        public void Detener()
        {
            PararDlna();
            if (Servidor != null && Servidor.Activo)
            {
                Servidor.Parar();
                Registro.Anotar("MiCine detenido");
            }
        }

        /// <summary>Escucha en toda la red de casa (no solo en este ordenador).</summary>
        public bool EnTodaLaRed { get { return Direccion == "0.0.0.0" || Direccion == ""; } }

        /// <summary>
        /// La dirección para este ordenador: localhost, salvo que MiCine escuche
        /// solo en una IP concreta de la red (entonces localhost no contesta).
        /// </summary>
        public string UrlLocal
        {
            get
            {
                if (EnTodaLaRed || Direccion == null || Direccion == "localhost" || Direccion.StartsWith("127.") || Direccion == "::1")
                    return "http://localhost:" + Puerto;
                return "http://" + Direccion + ":" + Puerto;
            }
        }

        /// <summary>La dirección para el móvil, la tele o la app de Android (null si no hay).</summary>
        public string UrlRed
        {
            get
            {
                if (Direccion == null) return null;
                if (EnTodaLaRed)
                {
                    string ip = Servidor.IpEnRed();
                    return ip == null ? null : "http://" + ip + ":" + Puerto;
                }
                if (Direccion.StartsWith("127.") || Direccion == "localhost" || Direccion == "::1") return null;
                return "http://" + Direccion + ":" + Puerto;
            }
        }
    }
}
