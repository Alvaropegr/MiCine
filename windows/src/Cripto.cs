using System;
using System.Security.Cryptography;
using System.Text;

namespace MiCine
{
    /// <summary>
    /// Contraseñas, tokens y firmas, con los mismos formatos que servidor.py:
    /// un usuario creado por uno entra en el otro, y un enlace firmado por
    /// uno vale en el otro.
    /// </summary>
    public static class Cripto
    {
        public const int Iteraciones = 600000;
        static readonly RNGCryptoServiceProvider azar = new RNGCryptoServiceProvider();

        public static byte[] Aleatorio(int n)
        {
            var b = new byte[n];
            lock (azar) azar.GetBytes(b);
            return b;
        }

        /// <summary>bytes.hex()</summary>
        public static string Hex(byte[] b)
        {
            var sb = new StringBuilder(b.Length * 2);
            foreach (byte x in b) sb.Append(x.ToString("x2", Py.Inv));
            return sb.ToString();
        }

        /// <summary>bytes.fromhex(): admite espacios entre parejas de cifras.</summary>
        public static byte[] DeHex(string h)
        {
            var salida = new System.Collections.Generic.List<byte>();
            int i = 0;
            while (i < h.Length)
            {
                if (h[i] == ' ' || h[i] == '\t' || h[i] == '\n' || h[i] == '\r' || h[i] == '\f' || h[i] == '\v') { i++; continue; }
                if (i + 1 >= h.Length) throw new FormatException("Hexadecimal incompleto");
                int a = Uri.FromHex(h[i]), b = Uri.FromHex(h[i + 1]);
                salida.Add((byte)(a * 16 + b));
                i += 2;
            }
            return salida.ToArray();
        }

        /// <summary>secrets.token_hex(n)</summary>
        public static string TokenHex(int n) { return Hex(Aleatorio(n)); }

        /// <summary>secrets.token_urlsafe(n): base64 para URL, sin '='.</summary>
        public static string TokenUrlSafe(int n)
        {
            return Convert.ToBase64String(Aleatorio(n)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        }

        public static string Sha256Hex(string texto)
        {
            using (var sha = SHA256.Create()) return Hex(sha.ComputeHash(Py.Utf8.GetBytes(texto)));
        }

        public static string HmacSha256Hex(byte[] clave, string mensaje)
        {
            using (var h = new HMACSHA256(clave)) return Hex(h.ComputeHash(Py.Utf8.GetBytes(mensaje)));
        }

        /// <summary>hmac.compare_digest: tarda lo mismo acierte o falle.</summary>
        public static bool Iguales(string a, string b)
        {
            if (a == null || b == null) return false;
            int diferencia = a.Length ^ b.Length;
            string referencia = a.Length == b.Length ? b : a;
            for (int i = 0; i < a.Length; i++) diferencia |= a[i] ^ referencia[i];
            return diferencia == 0;
        }

        public static byte[] Pbkdf2(byte[] clave, byte[] sal, long iteraciones)
        {
            if (iteraciones < 1) throw new ArgumentException("iteraciones debe ser al menos 1");
            byte[] r = Nativo.Pbkdf2Sha256(clave, sal, iteraciones, 32);
            if (r != null) return r;
            using (var k = new Rfc2898DeriveBytes(clave, sal, checked((int)iteraciones), HashAlgorithmName.SHA256)) return k.GetBytes(32);
        }

        /// <summary>hash_contrasena(): {"sal", "hash", "iteraciones"}.</summary>
        public static Objeto HashContrasena(string contrasena, byte[] sal, long iteraciones)
        {
            if (sal == null || sal.Length == 0) sal = Aleatorio(16);
            byte[] clave = Pbkdf2(Py.Utf8Estricto.GetBytes(contrasena), sal, iteraciones);
            return new Objeto { { "sal", Hex(sal) }, { "hash", Hex(clave) }, { "iteraciones", iteraciones } };
        }

        public static Objeto HashContrasena(string contrasena)
        {
            return HashContrasena(contrasena, null, Iteraciones);
        }

        /// <summary>comprobar_contrasena()</summary>
        public static bool ComprobarContrasena(string contrasena, Objeto registro)
        {
            long iteraciones = Convert.ToInt64(registro["iteraciones"], Py.Inv);
            var calculado = HashContrasena(contrasena, DeHex((string)registro["sal"]), iteraciones);
            return Iguales((string)calculado["hash"], (string)registro["hash"]);
        }
    }
}
