using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;

namespace MiCine
{
    /// <summary>
    /// Objeto JSON que conserva el orden de las claves, como un dict de
    /// Python: así los archivos de datos/ salen igual que los escribe
    /// servidor.py y las respuestas de la API tienen los campos en el mismo
    /// orden. Reasignar una clave existente no la mueve de sitio.
    /// </summary>
    public sealed class Objeto : IEnumerable<KeyValuePair<string, object>>
    {
        readonly List<string> claves = new List<string>();
        readonly Dictionary<string, object> valores = new Dictionary<string, object>(StringComparer.Ordinal);

        public object this[string clave]
        {
            get { object v; return valores.TryGetValue(clave, out v) ? v : null; }
            set
            {
                if (!valores.ContainsKey(clave)) claves.Add(clave);
                valores[clave] = value;
            }
        }

        /// <summary>Para los inicializadores { { "clave", valor }, … }.</summary>
        public void Add(string clave, object valor) { this[clave] = valor; }

        public int Count { get { return claves.Count; } }
        public bool Contiene(string clave) { return clave != null && valores.ContainsKey(clave); }
        public List<string> Claves { get { return new List<string>(claves); } }

        /// <summary>dict.get(clave, defecto): distingue «no está» de «está y vale null».</summary>
        public object Obtener(string clave, object defecto)
        {
            object v;
            if (clave != null && valores.TryGetValue(clave, out v)) return v;
            return defecto;
        }

        public Objeto ObjetoDe(string clave) { return this[clave] as Objeto; }

        public bool Quitar(string clave)
        {
            if (clave == null || !valores.Remove(clave)) return false;
            claves.Remove(clave);
            return true;
        }

        /// <summary>dict(self): copia superficial.</summary>
        public Objeto Copia()
        {
            var o = new Objeto();
            foreach (string k in claves) o[k] = valores[k];
            return o;
        }

        public IEnumerator<KeyValuePair<string, object>> GetEnumerator()
        {
            foreach (string k in claves.ToArray()) yield return new KeyValuePair<string, object>(k, valores[k]);
        }

        IEnumerator IEnumerable.GetEnumerator() { return GetEnumerator(); }
    }

    /// <summary>Error de formato JSON (en Python, un ValueError).</summary>
    public sealed class ErrorJson : Exception
    {
        public ErrorJson(string mensaje) : base(mensaje) { }
    }

    /// <summary>
    /// JSON sin dependencias, con las reglas del módulo json de Python:
    /// acepta NaN/Infinity, los enteros son long y los decimales double, una
    /// clave repetida se queda con el último valor, y al escribir se usa
    /// ensure_ascii=False con los mismos separadores que json.dumps.
    /// </summary>
    public static class Json
    {
        // ── lectura ──────────────────────────────────────────────────────

        public static object Leer(string texto)
        {
            var l = new Lector(texto);
            l.Espacios();
            if (l.i >= texto.Length) throw new ErrorJson("Expecting value");
            object v = l.Valor(0);
            l.Espacios();
            if (l.i != texto.Length) throw new ErrorJson("Extra data");
            return v;
        }

        /// <summary>
        /// json.loads(bytes): detecta UTF-8 (con o sin BOM), UTF-16 y UTF-32
        /// igual que json.detect_encoding.
        /// </summary>
        public static object LeerBytes(byte[] b)
        {
            Encoding cod;
            int salto = 0;
            if (b.Length >= 4 && ((b[0] == 0 && b[1] == 0 && b[2] == 0xFE && b[3] == 0xFF) || (b[0] == 0xFF && b[1] == 0xFE && b[2] == 0 && b[3] == 0)))
            { cod = new UTF32Encoding(b[0] == 0, false, true); salto = 4; }
            else if (b.Length >= 2 && ((b[0] == 0xFE && b[1] == 0xFF) || (b[0] == 0xFF && b[1] == 0xFE)))
            { cod = new UnicodeEncoding(b[0] == 0xFE, false, true); salto = 2; }
            else if (b.Length >= 3 && b[0] == 0xEF && b[1] == 0xBB && b[2] == 0xBF)
            { cod = Py.Utf8Estricto; salto = 3; }
            else if (b.Length >= 4)
            {
                if (b[0] == 0) cod = b[1] != 0 ? (Encoding)new UnicodeEncoding(true, false, true) : new UTF32Encoding(true, false, true);
                else if (b[1] == 0) cod = (b[2] != 0 || b[3] != 0) ? (Encoding)new UnicodeEncoding(false, false, true) : new UTF32Encoding(false, false, true);
                else cod = Py.Utf8Estricto;
            }
            else if (b.Length == 2 && b[0] == 0) cod = new UnicodeEncoding(true, false, true);
            else if (b.Length == 2 && b[1] == 0) cod = new UnicodeEncoding(false, false, true);
            else cod = Py.Utf8Estricto;
            string texto;
            try { texto = cod.GetString(b, salto, b.Length - salto); }
            catch (ArgumentException) { throw new ErrorJson("No se puede descodificar"); }
            return Leer(texto);
        }

        /// <summary>Lee un archivo; si no existe, devuelve el valor por defecto (leer_json).</summary>
        public static object LeerArchivo(string ruta, object defecto)
        {
            if (!File.Exists(ruta)) return defecto;
            return Leer(File.ReadAllText(ruta, Encoding.UTF8));
        }

        /// <summary>Profundidad máxima: Python lanza RecursionError a ~1000 niveles.</summary>
        public sealed class ErrorProfundidad : Exception
        {
            public ErrorProfundidad() : base("Demasiados niveles de anidamiento en el JSON") { }
        }

        sealed class Lector
        {
            readonly string s;
            public int i;
            public Lector(string texto) { s = texto; }

            public void Espacios()
            {
                while (i < s.Length && (s[i] == ' ' || s[i] == '\t' || s[i] == '\n' || s[i] == '\r')) i++;
            }

            bool Sigue(string literal)
            {
                return string.CompareOrdinal(s, i, literal, 0, literal.Length) == 0;
            }

            public object Valor(int nivel)
            {
                if (nivel > 990) throw new ErrorProfundidad();
                if (i >= s.Length) throw new ErrorJson("Expecting value");
                char c = s[i];
                if (c == '{') return Objeto(nivel);
                if (c == '[') return Lista(nivel);
                if (c == '"') { i++; return Texto(); }
                if (c == 'n' && Sigue("null")) { i += 4; return null; }
                if (c == 't' && Sigue("true")) { i += 4; return true; }
                if (c == 'f' && Sigue("false")) { i += 5; return false; }
                if (c == 'N' && Sigue("NaN")) { i += 3; return double.NaN; }
                if (c == 'I' && Sigue("Infinity")) { i += 8; return double.PositiveInfinity; }
                if (c == '-' && Sigue("-Infinity")) { i += 9; return double.NegativeInfinity; }
                if (c == '-' || (c >= '0' && c <= '9')) return Numero();
                throw new ErrorJson("Expecting value");
            }

            object Numero()
            {
                int ini = i;
                if (s[i] == '-') i++;
                if (i >= s.Length) throw new ErrorJson("Expecting value");
                if (s[i] == '0') i++;
                else if (s[i] >= '1' && s[i] <= '9') { while (i < s.Length && s[i] >= '0' && s[i] <= '9') i++; }
                else throw new ErrorJson("Expecting value");
                bool real = false;
                if (i + 1 < s.Length && s[i] == '.' && s[i + 1] >= '0' && s[i + 1] <= '9')
                {
                    real = true;
                    i++;
                    while (i < s.Length && s[i] >= '0' && s[i] <= '9') i++;
                }
                if (i < s.Length && (s[i] == 'e' || s[i] == 'E'))
                {
                    int j = i + 1;
                    if (j < s.Length && (s[j] == '+' || s[j] == '-')) j++;
                    if (j < s.Length && s[j] >= '0' && s[j] <= '9')
                    {
                        while (j < s.Length && s[j] >= '0' && s[j] <= '9') j++;
                        real = true;
                        i = j;
                    }
                }
                string t = s.Substring(ini, i - ini);
                if (!real)
                {
                    long l;
                    if (long.TryParse(t, NumberStyles.AllowLeadingSign, Py.Inv, out l)) return l;
                }
                try { return double.Parse(t, NumberStyles.Float, Py.Inv); }
                catch (OverflowException) { return t[0] == '-' ? double.NegativeInfinity : double.PositiveInfinity; }
            }

            string Texto()
            {
                var sb = new StringBuilder();
                while (true)
                {
                    if (i >= s.Length) throw new ErrorJson("Unterminated string");
                    char c = s[i++];
                    if (c == '"') return sb.ToString();
                    if (c < 0x20) throw new ErrorJson("Invalid control character");
                    if (c != '\\') { sb.Append(c); continue; }
                    if (i >= s.Length) throw new ErrorJson("Unterminated string");
                    char e = s[i++];
                    switch (e)
                    {
                        case '"': sb.Append('"'); break;
                        case '\\': sb.Append('\\'); break;
                        case '/': sb.Append('/'); break;
                        case 'b': sb.Append('\b'); break;
                        case 'f': sb.Append('\f'); break;
                        case 'n': sb.Append('\n'); break;
                        case 'r': sb.Append('\r'); break;
                        case 't': sb.Append('\t'); break;
                        case 'u': sb.Append(Unicode()); break;
                        default: throw new ErrorJson("Invalid \\escape");
                    }
                }
            }

            char Unicode()
            {
                if (i + 4 > s.Length) throw new ErrorJson("Invalid \\uXXXX escape");
                int v;
                if (!int.TryParse(s.Substring(i, 4), NumberStyles.AllowHexSpecifier, Py.Inv, out v)) throw new ErrorJson("Invalid \\uXXXX escape");
                i += 4;
                return (char)v;
            }

            Objeto Objeto(int nivel)
            {
                var o = new Objeto();
                i++;
                Espacios();
                if (i < s.Length && s[i] == '}') { i++; return o; }
                while (true)
                {
                    Espacios();
                    if (i >= s.Length || s[i] != '"') throw new ErrorJson("Expecting property name enclosed in double quotes");
                    i++;
                    string clave = Texto();
                    Espacios();
                    if (i >= s.Length || s[i] != ':') throw new ErrorJson("Expecting ':' delimiter");
                    i++;
                    Espacios();
                    o[clave] = Valor(nivel + 1);
                    Espacios();
                    if (i < s.Length && s[i] == ',') { i++; continue; }
                    if (i < s.Length && s[i] == '}') { i++; return o; }
                    throw new ErrorJson("Expecting ',' delimiter");
                }
            }

            List<object> Lista(int nivel)
            {
                var l = new List<object>();
                i++;
                Espacios();
                if (i < s.Length && s[i] == ']') { i++; return l; }
                while (true)
                {
                    Espacios();
                    l.Add(Valor(nivel + 1));
                    Espacios();
                    if (i < s.Length && s[i] == ',') { i++; continue; }
                    if (i < s.Length && s[i] == ']') { i++; return l; }
                    throw new ErrorJson("Expecting ',' delimiter");
                }
            }
        }

        // ── escritura ────────────────────────────────────────────────────

        /// <summary>json.dumps(valor, ensure_ascii=False): separadores ", " y ": ".</summary>
        public static string Escribir(object valor)
        {
            var sb = new StringBuilder();
            Escribir(sb, valor, -1, 0);
            return sb.ToString();
        }

        /// <summary>json.dumps(valor, ensure_ascii=False, indent=2), con los saltos de línea que pone Windows (\r\n).</summary>
        public static string EscribirSangrado(object valor)
        {
            var sb = new StringBuilder();
            Escribir(sb, valor, 2, 0);
            // Python escribe en modo texto: en Windows cada \n sale como \r\n.
            return sb.ToString().Replace("\n", "\r\n");
        }

        static void Escribir(StringBuilder sb, object v, int sangria, int nivel)
        {
            if (v == null) { sb.Append("null"); return; }
            if (v is bool) { sb.Append((bool)v ? "true" : "false"); return; }
            if (v is long) { sb.Append(((long)v).ToString(Py.Inv)); return; }
            if (v is int) { sb.Append(((int)v).ToString(Py.Inv)); return; }
            if (v is double) { sb.Append(Py.Repr((double)v)); return; }
            var s = v as string;
            if (s != null) { Texto(sb, s); return; }
            var o = v as Objeto;
            if (o != null)
            {
                if (o.Count == 0) { sb.Append("{}"); return; }
                sb.Append('{');
                bool primero = true;
                foreach (var kv in o)
                {
                    if (!primero) sb.Append(sangria >= 0 ? "," : ", ");
                    primero = false;
                    if (sangria >= 0) { sb.Append('\n'); sb.Append(' ', sangria * (nivel + 1)); }
                    Texto(sb, kv.Key);
                    sb.Append(": ");
                    Escribir(sb, kv.Value, sangria, nivel + 1);
                }
                if (sangria >= 0) { sb.Append('\n'); sb.Append(' ', sangria * nivel); }
                sb.Append('}');
                return;
            }
            var l = v as IList;
            if (l != null)
            {
                if (l.Count == 0) { sb.Append("[]"); return; }
                sb.Append('[');
                for (int k = 0; k < l.Count; k++)
                {
                    if (k > 0) sb.Append(sangria >= 0 ? "," : ", ");
                    if (sangria >= 0) { sb.Append('\n'); sb.Append(' ', sangria * (nivel + 1)); }
                    Escribir(sb, l[k], sangria, nivel + 1);
                }
                if (sangria >= 0) { sb.Append('\n'); sb.Append(' ', sangria * nivel); }
                sb.Append(']');
                return;
            }
            Texto(sb, Convert.ToString(v, Py.Inv));
        }

        static void Texto(StringBuilder sb, string s)
        {
            sb.Append('"');
            foreach (char c in s)
            {
                switch (c)
                {
                    case '"': sb.Append("\\\""); break;
                    case '\\': sb.Append("\\\\"); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\r': sb.Append("\\r"); break;
                    case '\t': sb.Append("\\t"); break;
                    case '\b': sb.Append("\\b"); break;
                    case '\f': sb.Append("\\f"); break;
                    default:
                        if (c < 0x20) sb.Append("\\u").Append(((int)c).ToString("x4", Py.Inv));
                        else sb.Append(c);
                        break;
                }
            }
            sb.Append('"');
        }

        // ── utilidades ───────────────────────────────────────────────────

        /// <summary>Escritura atómica: archivo temporal y reemplazo (escribir_json).</summary>
        public static void EscribirArchivo(string ruta, object datos)
        {
            string temporal = ruta + ".tmp";
            File.WriteAllBytes(temporal, Py.Utf8.GetBytes(EscribirSangrado(datos)));
            Nativo.ReemplazarConReintentos(temporal, ruta);
        }
    }
}
