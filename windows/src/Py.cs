using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace MiCine
{
    /// <summary>
    /// Pequeñas piezas que se comportan EXACTAMENTE como su equivalente en
    /// Python: servidor.py es la especificación, y los dos servidores
    /// comparten datos, enlaces firmados y cookies. Donde .NET hace algo
    /// parecido pero no igual (strip, int(), quote, unquote, parse_qs,
    /// splitext, repr de float…), se usa esto.
    /// </summary>
    public static class Py
    {
        public static readonly CultureInfo Inv = CultureInfo.InvariantCulture;
        public static readonly Encoding Latin1 = Encoding.GetEncoding(28591);
        public static readonly Encoding Utf8 = new UTF8Encoding(false, false);
        public static readonly Encoding Utf8Estricto = new UTF8Encoding(false, true);

        // ── texto ────────────────────────────────────────────────────────

        /// <summary>str.isspace(): lo de .NET más los separadores \x1c-\x1f.</summary>
        public static bool EsEspacio(char c)
        {
            return char.IsWhiteSpace(c) || (c >= '\x1c' && c <= '\x1f');
        }

        public static string Strip(string s)
        {
            if (s == null) return null;
            int i = 0, j = s.Length;
            while (i < j && EsEspacio(s[i])) i++;
            while (j > i && EsEspacio(s[j - 1])) j--;
            return s.Substring(i, j - i);
        }

        public static string LStrip(string s)
        {
            int i = 0;
            while (i < s.Length && EsEspacio(s[i])) i++;
            return s.Substring(i);
        }

        /// <summary>str.strip(caracteres).</summary>
        public static string Strip(string s, string caracteres)
        {
            int i = 0, j = s.Length;
            while (i < j && caracteres.IndexOf(s[i]) >= 0) i++;
            while (j > i && caracteres.IndexOf(s[j - 1]) >= 0) j--;
            return s.Substring(i, j - i);
        }

        /// <summary>str.split() sin argumentos.</summary>
        public static List<string> Split(string s)
        {
            var partes = new List<string>();
            int i = 0;
            while (i < s.Length)
            {
                while (i < s.Length && EsEspacio(s[i])) i++;
                int j = i;
                while (j < s.Length && !EsEspacio(s[j])) j++;
                if (j > i) partes.Add(s.Substring(i, j - i));
                i = j;
            }
            return partes;
        }

        /// <summary>len() de Python: cuenta caracteres Unicode, no unidades UTF-16.</summary>
        public static int Longitud(string s)
        {
            int n = 0;
            for (int i = 0; i < s.Length; i++)
            {
                if (char.IsHighSurrogate(s[i]) && i + 1 < s.Length && char.IsLowSurrogate(s[i + 1])) i++;
                n++;
            }
            return n;
        }

        /// <summary>s[:n] de Python (por caracteres Unicode).</summary>
        public static string Recortar(string s, int n)
        {
            int cuenta = 0, i = 0;
            while (i < s.Length && cuenta < n)
            {
                if (char.IsHighSurrogate(s[i]) && i + 1 < s.Length && char.IsLowSurrogate(s[i + 1])) i++;
                i++;
                cuenta++;
            }
            return s.Substring(0, i);
        }

        public static string Lower(string s) { return s.ToLowerInvariant(); }
        public static string Upper(string s) { return s.ToUpperInvariant().Replace("ß", "SS"); }

        /// <summary>str.casefold(), aproximado: minúsculas más los plegados más comunes.</summary>
        public static string Casefold(string s)
        {
            return s.ToLowerInvariant().Replace("ß", "ss").Replace("ς", "σ");
        }

        /// <summary>Compara como Python: por punto de código, no por unidad UTF-16.</summary>
        public static int Comparar(string a, string b)
        {
            int i = 0, j = 0;
            while (i < a.Length && j < b.Length)
            {
                int ca = PuntoCodigo(a, ref i);
                int cb = PuntoCodigo(b, ref j);
                if (ca != cb) return ca < cb ? -1 : 1;
            }
            if (i < a.Length) return 1;
            if (j < b.Length) return -1;
            return 0;
        }

        /// <summary>Lee un punto de código y avanza; un suplente suelto vale por sí mismo.</summary>
        static int PuntoCodigo(string s, ref int i)
        {
            char c = s[i];
            if (char.IsHighSurrogate(c) && i + 1 < s.Length && char.IsLowSurrogate(s[i + 1]))
            {
                int r = char.ConvertToUtf32(c, s[i + 1]);
                i += 2;
                return r;
            }
            i++;
            return c;
        }

        public static readonly Comparison<string> OrdenPython = Comparar;

        public static List<string> Ordenados(IEnumerable<string> textos)
        {
            var l = new List<string>(textos);
            // List.Sort no es estable, pero con claves distintas da igual; con
            // claves iguales el resultado es el mismo texto.
            l.Sort(OrdenPython);
            return l;
        }

        // ── números ──────────────────────────────────────────────────────

        /// <summary>
        /// int(texto) de Python en base 10: espacios alrededor, signo, dígitos
        /// Unicode y guiones bajos entre dígitos. Si no cabe en un long, se
        /// satura (el resultado de las comparaciones es el mismo).
        /// </summary>
        public static bool Entero(string texto, out long valor)
        {
            valor = 0;
            if (texto == null) return false;
            string s = Strip(texto);
            int i = 0;
            bool negativo = false;
            if (i < s.Length && (s[i] == '+' || s[i] == '-')) { negativo = s[i] == '-'; i++; }
            if (i >= s.Length) return false;
            bool hayDigito = false, ultimoGuion = false, desborda = false;
            decimal acumulado = 0;
            for (; i < s.Length; i++)
            {
                char c = s[i];
                if (c == '_')
                {
                    if (!hayDigito || ultimoGuion) return false;
                    ultimoGuion = true;
                    continue;
                }
                int d = CharUnicodeInfo.GetDecimalDigitValue(c);
                if (d < 0 || !char.IsDigit(c)) return false;
                hayDigito = true;
                ultimoGuion = false;
                if (!desborda)
                {
                    acumulado = acumulado * 10 + d;
                    if (acumulado > long.MaxValue) desborda = true;
                }
            }
            if (!hayDigito || ultimoGuion) return false;
            if (desborda) valor = negativo ? long.MinValue : long.MaxValue;
            else valor = negativo ? -(long)acumulado : (long)acumulado;
            return true;
        }

        /// <summary>Dígitos Unicode a ASCII, sin ceros a la izquierda (int(x) y luego str()).</summary>
        public static string DigitosAscii(string digitos)
        {
            var sb = new StringBuilder();
            foreach (char c in digitos)
            {
                int d = CharUnicodeInfo.GetDecimalDigitValue(c);
                sb.Append((char)('0' + (d < 0 ? 0 : d)));
            }
            string r = sb.ToString().TrimStart('0');
            return r.Length == 0 ? "0" : r;
        }

        /// <summary>repr(float) de Python: el texto más corto que vuelve a dar el mismo número.</summary>
        public static string Repr(double d)
        {
            if (double.IsNaN(d)) return "NaN";
            if (double.IsPositiveInfinity(d)) return "Infinity";
            if (double.IsNegativeInfinity(d)) return "-Infinity";
            if (d == 0) return (1 / d < 0) ? "-0.0" : "0.0";
            string e = null;
            for (int p = 1; p <= 17 && e == null; p++)
            {
                string s = d.ToString("E" + (p - 1), Inv);
                double v;
                if (double.TryParse(s, NumberStyles.Float, Inv, out v) && v == d) e = s;
            }
            if (e == null) e = d.ToString("E16", Inv);
            bool negativo = e[0] == '-';
            if (negativo) e = e.Substring(1);
            int iE = e.IndexOf('E');
            string mantisa = e.Substring(0, iE).Replace(".", "").TrimEnd('0');
            if (mantisa.Length == 0) mantisa = "0";
            int exp = int.Parse(e.Substring(iE + 1), NumberStyles.AllowLeadingSign, Inv);
            string r;
            if (exp >= -4 && exp < 16)
            {
                if (exp < 0) r = "0." + new string('0', -exp - 1) + mantisa;
                else if (mantisa.Length <= exp + 1) r = mantisa + new string('0', exp + 1 - mantisa.Length) + ".0";
                else r = mantisa.Substring(0, exp + 1) + "." + mantisa.Substring(exp + 1);
            }
            else
            {
                r = mantisa.Substring(0, 1) + (mantisa.Length > 1 ? "." + mantisa.Substring(1) : "")
                    + "e" + (exp < 0 ? "-" : "+") + Math.Abs(exp).ToString("00", Inv);
            }
            return (negativo ? "-" : "") + r;
        }

        /// <summary>
        /// round(x, 1) de Python: redondeo al par sobre el valor binario EXACTO
        /// (round(0.35, 1) da 0.3 porque 0.35 es en realidad 0.34999…).
        /// Solo para 0 ≤ x &lt; 1e6, que es lo que admite /api/progreso.
        /// </summary>
        public static double Redondear1(double x)
        {
            if (double.IsNaN(x) || double.IsInfinity(x) || x == 0) return x;
            if (x < 0) return -Redondear1(-x);
            long bits = BitConverter.DoubleToInt64Bits(x);
            int expBits = (int)((bits >> 52) & 0x7FF);
            long frac = bits & 0xFFFFFFFFFFFFFL;
            ulong mantisa; int exp;
            if (expBits == 0) { mantisa = (ulong)frac; exp = -1074; }
            else { mantisa = (ulong)(frac | (1L << 52)); exp = expBits - 1075; }
            // x = mantisa · 2^exp  →  x·10 = 10·mantisa · 2^exp
            if (exp >= 0 || mantisa >= (1UL << 59)) return Math.Round(x, 1, MidpointRounding.ToEven);
            ulong diez = mantisa * 10;
            int desplaza = -exp;
            ulong q, resto, mitad;
            if (desplaza >= 64) return 0.0 * x; // menor que una décima de 1e-18: 0
            q = diez >> desplaza;
            resto = diez & ((1UL << desplaza) - 1);
            mitad = 1UL << (desplaza - 1);
            if (resto > mitad || (resto == mitad && (q & 1) == 1)) q++;
            return q / 10.0;
        }

        // ── URL ──────────────────────────────────────────────────────────

        static bool SiempreSeguro(char c)
        {
            return (c >= 'A' && c <= 'Z') || (c >= 'a' && c <= 'z') || (c >= '0' && c <= '9') || c == '_' || c == '.' || c == '-' || c == '~';
        }

        /// <summary>urllib.parse.quote(texto, safe=seguro).</summary>
        public static string Citar(string texto, string seguro)
        {
            var sb = new StringBuilder();
            foreach (byte b in Utf8.GetBytes(texto))
            {
                char c = (char)b;
                if (b < 128 && (SiempreSeguro(c) || seguro.IndexOf(c) >= 0)) sb.Append(c);
                else sb.Append('%').Append(b.ToString("X2", Inv));
            }
            return sb.ToString();
        }

        public static string Citar(string texto) { return Citar(texto, "/"); }

        /// <summary>urllib.parse.quote_plus(texto, safe='').</summary>
        public static string CitarMas(string texto)
        {
            if (texto.IndexOf(' ') >= 0) return Citar(texto, " ").Replace(' ', '+');
            return Citar(texto, "");
        }

        static int Hex(char c)
        {
            if (c >= '0' && c <= '9') return c - '0';
            if (c >= 'a' && c <= 'f') return c - 'a' + 10;
            if (c >= 'A' && c <= 'F') return c - 'A' + 10;
            return -1;
        }

        /// <summary>
        /// urllib.parse.unquote(texto): los %XX se descodifican como UTF-8 (con
        /// sustitución si no es válido) por tramos ASCII; lo que no es ASCII
        /// pasa tal cual; un % sin dos cifras hexadecimales se queda.
        /// </summary>
        public static string Desescapar(string texto)
        {
            if (texto == null || texto.IndexOf('%') < 0) return texto;
            var sb = new StringBuilder();
            int i = 0;
            while (i < texto.Length)
            {
                if (texto[i] >= 0x80) { sb.Append(texto[i]); i++; continue; }
                int j = i;
                while (j < texto.Length && texto[j] < 0x80) j++;
                var bytes = new List<byte>();
                for (int k = i; k < j; k++)
                {
                    char c = texto[k];
                    // Las dos cifras tienen que estar dentro del mismo tramo ASCII.
                    if (c == '%' && k + 2 < j)
                    {
                        int a = Hex(texto[k + 1]), b = Hex(texto[k + 2]);
                        if (a >= 0 && b >= 0) { bytes.Add((byte)(a * 16 + b)); k += 2; continue; }
                    }
                    bytes.Add((byte)c);
                }
                sb.Append(Utf8.GetString(bytes.ToArray()));
                i = j;
            }
            return sb.ToString();
        }

        /// <summary>urllib.parse.parse_qs(consulta) (sin valores vacíos, como por defecto).</summary>
        public static Dictionary<string, List<string>> ParseQs(string consulta)
        {
            var r = new Dictionary<string, List<string>>(StringComparer.Ordinal);
            if (string.IsNullOrEmpty(consulta)) return r;
            foreach (string par in consulta.Split('&'))
            {
                if (par.Length == 0) continue;
                int i = par.IndexOf('=');
                if (i < 0) continue;
                string valor = par.Substring(i + 1);
                if (valor.Length == 0) continue;
                string nombre = Desescapar(par.Substring(0, i).Replace('+', ' '));
                valor = Desescapar(valor.Replace('+', ' '));
                List<string> lista;
                if (!r.TryGetValue(nombre, out lista)) r[nombre] = lista = new List<string>();
                lista.Add(valor);
            }
            return r;
        }

        // ── rutas ────────────────────────────────────────────────────────

        /// <summary>os.path.splitext de Windows: los puntos iniciales no cuentan como extensión.</summary>
        public static void SplitExt(string p, out string raiz, out string ext)
        {
            int sep = Math.Max(p.LastIndexOf('/'), p.LastIndexOf('\\'));
            int punto = p.LastIndexOf('.');
            if (punto > sep)
            {
                for (int f = sep + 1; f < punto; f++)
                {
                    if (p[f] != '.')
                    {
                        raiz = p.Substring(0, punto);
                        ext = p.Substring(punto);
                        return;
                    }
                }
            }
            raiz = p;
            ext = "";
        }

        public static string Ext(string nombre)
        {
            string raiz, ext;
            SplitExt(nombre, out raiz, out ext);
            return ext;
        }

        /// <summary>Path.suffix de pathlib (Python 3.13).</summary>
        public static string Sufijo(string nombre)
        {
            int i = nombre.LastIndexOf('.');
            return (i > 0 && i < nombre.Length - 1) ? nombre.Substring(i) : "";
        }

        // ── valores JSON ─────────────────────────────────────────────────

        /// <summary>bool(valor) de Python.</summary>
        public static bool Verdad(object v)
        {
            if (v == null) return false;
            if (v is bool) return (bool)v;
            if (v is long) return (long)v != 0;
            if (v is int) return (int)v != 0;
            if (v is double) return (double)v != 0;
            var s = v as string;
            if (s != null) return s.Length > 0;
            var l = v as List<object>;
            if (l != null) return l.Count > 0;
            var o = v as Objeto;
            if (o != null) return o.Count > 0;
            return true;
        }

        public static bool EsNumero(object v)
        {
            return v is long || v is int || v is double || v is bool;
        }

        /// <summary>float(valor) para lo que isinstance(x, (int, float)) acepta (bool incluido).</summary>
        public static double ANumero(object v)
        {
            if (v is bool) return (bool)v ? 1 : 0;
            if (v is long) return (long)v;
            if (v is int) return (int)v;
            if (v is double) return (double)v;
            throw new InvalidCastException();
        }

        /// <summary>str(valor) de Python.</summary>
        public static string Str(object v)
        {
            var s = v as string;
            if (s != null) return s;
            return ReprValor(v);
        }

        /// <summary>repr(valor) de Python para los tipos que salen de un JSON.</summary>
        public static string ReprValor(object v)
        {
            if (v == null) return "None";
            if (v is bool) return (bool)v ? "True" : "False";
            if (v is long) return ((long)v).ToString(Inv);
            if (v is int) return ((int)v).ToString(Inv);
            if (v is double)
            {
                double d = (double)v;
                if (double.IsNaN(d)) return "nan";
                if (double.IsPositiveInfinity(d)) return "inf";
                if (double.IsNegativeInfinity(d)) return "-inf";
                return Repr(d);
            }
            var s = v as string;
            if (s != null) return ReprTexto(s);
            var l = v as List<object>;
            if (l != null)
            {
                var partes = new List<string>();
                foreach (object x in l) partes.Add(ReprValor(x));
                return "[" + string.Join(", ", partes) + "]";
            }
            var o = v as Objeto;
            if (o != null)
            {
                var partes = new List<string>();
                foreach (var kv in o) partes.Add(ReprTexto(kv.Key) + ": " + ReprValor(kv.Value));
                return "{" + string.Join(", ", partes) + "}";
            }
            return Convert.ToString(v, Inv);
        }

        /// <summary>repr(str) de Python (comillas simples salvo que el texto las lleve).</summary>
        public static string ReprTexto(string s)
        {
            char comilla = (s.IndexOf('\'') >= 0 && s.IndexOf('"') < 0) ? '"' : '\'';
            var sb = new StringBuilder();
            sb.Append(comilla);
            foreach (char c in s)
            {
                if (c == comilla || c == '\\') sb.Append('\\').Append(c);
                else if (c == '\n') sb.Append("\\n");
                else if (c == '\r') sb.Append("\\r");
                else if (c == '\t') sb.Append("\\t");
                else if (c < 0x20 || (c >= 0x7f && c < 0xa0)) sb.Append("\\x").Append(((int)c).ToString("x2", Inv));
                else sb.Append(c);
            }
            sb.Append(comilla);
            return sb.ToString();
        }

        // ── tiempo ───────────────────────────────────────────────────────

        static readonly DateTime Epoca = new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc);

        /// <summary>time.time()</summary>
        public static double Ahora()
        {
            return (DateTime.UtcNow - Epoca).Ticks / 1e7;
        }

        /// <summary>int(time.time())</summary>
        public static long AhoraEntero()
        {
            return (DateTime.UtcNow - Epoca).Ticks / 10000000L;
        }

        /// <summary>int(st_mtime): segundos desde 1970, truncados hacia cero.</summary>
        public static long Segundos(DateTime utc)
        {
            return (utc.ToUniversalTime() - Epoca).Ticks / 10000000L;
        }
    }
}
