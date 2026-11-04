using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

namespace MiCine
{
    /// <summary>No se pudo mover/renombrar un archivo. Lleva el código de error de Windows.</summary>
    public sealed class ErrorMover : IOException
    {
        public readonly int Codigo;
        public ErrorMover(string mensaje, int codigo) : base(mensaje) { Codigo = codigo; }

        /// <summary>Lo que Python convierte en PermissionError (acceso denegado, archivo en uso, bloqueo).</summary>
        public bool EsPermiso { get { return Codigo == 5 || Codigo == 32 || Codigo == 33; } }
    }

    /// <summary>
    /// Llamadas a Windows que .NET Framework no trae o trae peor: os.replace,
    /// Path.resolve, shutil.disk_usage, PBKDF2 nativo, barra de título oscura…
    /// </summary>
    public static class Nativo
    {
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        static extern bool MoveFileEx(string origen, string destino, int opciones);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        static extern IntPtr CreateFile(string ruta, int acceso, int compartir, IntPtr seguridad, int disposicion, int atributos, IntPtr plantilla);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        static extern int GetFinalPathNameByHandle(IntPtr archivo, StringBuilder ruta, int tamano, int opciones);

        [DllImport("kernel32.dll", SetLastError = true)]
        static extern bool CloseHandle(IntPtr h);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        static extern bool GetDiskFreeSpaceEx(string ruta, out ulong libreUsuario, out ulong total, out ulong libre);

        [DllImport("bcrypt.dll", CharSet = CharSet.Unicode)]
        static extern int BCryptOpenAlgorithmProvider(out IntPtr algoritmo, string id, string implementacion, int opciones);

        [DllImport("bcrypt.dll")]
        static extern int BCryptDeriveKeyPBKDF2(IntPtr algoritmo, byte[] clave, int largoClave, byte[] sal, int largoSal,
            ulong iteraciones, byte[] salida, int largoSalida, int opciones);

        [DllImport("bcrypt.dll")]
        static extern int BCryptCloseAlgorithmProvider(IntPtr algoritmo, int opciones);

        [DllImport("dwmapi.dll")]
        static extern int DwmSetWindowAttribute(IntPtr ventana, int atributo, ref int valor, int tamano);

        [DllImport("user32.dll")]
        static extern bool SetProcessDPIAware();

        [DllImport("uxtheme.dll", CharSet = CharSet.Unicode)]
        static extern int SetWindowTheme(IntPtr ventana, string tema, string lista);

        const int MOVEFILE_REPLACE_EXISTING = 1;
        const int FILE_SHARE_TODO = 1 | 2 | 4;
        const int OPEN_EXISTING = 3;
        const int FILE_FLAG_BACKUP_SEMANTICS = 0x02000000;
        static readonly IntPtr INVALIDO = new IntPtr(-1);

        /// <summary>os.replace(origen, destino): renombra sustituyendo el destino si existe.</summary>
        public static void Reemplazar(string origen, string destino)
        {
            if (!MoveFileEx(origen, destino, MOVEFILE_REPLACE_EXISTING))
            {
                int codigo = Marshal.GetLastWin32Error();
                throw new ErrorMover("No se pudo mover «" + origen + "» a «" + destino + "» (error " + codigo + ")", codigo);
            }
        }

        /// <summary>
        /// Igual, pero reintenta un momento si el archivo está bloqueado (un
        /// antivirus o el indexador lo abren a veces justo al escribirlo).
        /// </summary>
        public static void ReemplazarConReintentos(string origen, string destino)
        {
            for (int intento = 0; ; intento++)
            {
                try { Reemplazar(origen, destino); return; }
                catch (ErrorMover e)
                {
                    if (!e.EsPermiso || intento >= 5) throw;
                    Thread.Sleep(40);
                }
            }
        }

        /// <summary>
        /// La ruta real de un archivo o carpeta que existe, siguiendo enlaces
        /// simbólicos y uniones (lo que hace Path.resolve() en Windows). Si no
        /// existe, devuelve la ruta tal cual.
        /// </summary>
        public static string RutaReal(string ruta)
        {
            IntPtr h = CreateFile(ruta, 0, FILE_SHARE_TODO, IntPtr.Zero, OPEN_EXISTING, FILE_FLAG_BACKUP_SEMANTICS, IntPtr.Zero);
            if (h == INVALIDO) return ruta;
            try
            {
                var sb = new StringBuilder(512);
                int n = GetFinalPathNameByHandle(h, sb, sb.Capacity, 0);
                if (n >= sb.Capacity)
                {
                    sb = new StringBuilder(n + 1);
                    n = GetFinalPathNameByHandle(h, sb, sb.Capacity, 0);
                }
                if (n <= 0 || n >= sb.Capacity) return ruta;
                string s = sb.ToString();
                if (s.StartsWith(@"\\?\UNC\", StringComparison.Ordinal)) return @"\\" + s.Substring(8);
                if (s.StartsWith(@"\\?\", StringComparison.Ordinal)) return s.Substring(4);
                return s;
            }
            finally { CloseHandle(h); }
        }

        /// <summary>shutil.disk_usage(ruta).free: bytes libres del disco.</summary>
        public static long Libre(string ruta)
        {
            ulong usuario, total, libre;
            if (!GetDiskFreeSpaceEx(ruta, out usuario, out total, out libre))
                throw new IOException("No se puede saber el espacio libre de " + ruta + " (error " + Marshal.GetLastWin32Error() + ")");
            return (long)libre;
        }

        /// <summary>
        /// PBKDF2-HMAC-SHA256 con la implementación nativa de Windows (CNG):
        /// 600 000 iteraciones tardan ~0,1 s, frente a ~1,5 s de
        /// Rfc2898DeriveBytes, que en .NET Framework es código gestionado.
        /// Devuelve null si CNG no está disponible.
        /// </summary>
        public static byte[] Pbkdf2Sha256(byte[] clave, byte[] sal, long iteraciones, int largo)
        {
            try
            {
                IntPtr alg;
                if (BCryptOpenAlgorithmProvider(out alg, "SHA256", null, 8 /* BCRYPT_ALG_HANDLE_HMAC_FLAG */) != 0) return null;
                try
                {
                    var salida = new byte[largo];
                    int r = BCryptDeriveKeyPBKDF2(alg, clave.Length == 0 ? null : clave, clave.Length,
                        sal.Length == 0 ? null : sal, sal.Length, (ulong)iteraciones, salida, largo, 0);
                    return r == 0 ? salida : null;
                }
                finally { BCryptCloseAlgorithmProvider(alg, 0); }
            }
            catch (DllNotFoundException) { return null; }
            catch (EntryPointNotFoundException) { return null; }
        }

        /// <summary>Barra de título oscura (Windows 10 20H1+ y 11). En los demás no hace nada.</summary>
        public static void TituloOscuro(IntPtr ventana)
        {
            try
            {
                int si = 1;
                if (DwmSetWindowAttribute(ventana, 20, ref si, 4) != 0) DwmSetWindowAttribute(ventana, 19, ref si, 4);
            }
            catch (Exception) { }
        }

        /// <summary>Barras de desplazamiento oscuras en una lista (Windows 10 1809+ y 11).</summary>
        public static void ControlOscuro(IntPtr control)
        {
            try { SetWindowTheme(control, "DarkMode_Explorer", null); } catch (Exception) { }
        }

        public static void DpiConsciente()
        {
            try { SetProcessDPIAware(); } catch (Exception) { }
        }
    }
}
