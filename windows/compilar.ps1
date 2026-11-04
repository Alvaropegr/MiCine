# ═══════════════════════════════════════════════════════════════════════════
#  MiCine para Windows · compilación
# ═══════════════════════════════════════════════════════════════════════════
#
#      powershell -ExecutionPolicy Bypass -File windows\compilar.ps1
#
#  Deja MiCine.exe en la carpeta del proyecto, junto a config.json, datos\ y
#  peliculas\: así el .exe y servidor.py comparten configuración y datos.
#
#  Usa el csc.exe que YA TRAE Windows (.NET Framework 4.8). No hay que
#  instalar SDK, Visual Studio ni NuGet, y el .exe resultante tampoco pide
#  nada: .NET 4.8 viene con Windows 10 (1903+) y 11.
#
#  Avisos que cuestan tiempo:
#   1. Ese compilador es C# 5: sin $"...", sin ?., sin nameof, sin => en
#      miembros, sin out var ni tuplas.
#   2. /codepage:65001 o los acentos de la interfaz salen rotos.
#   3. La web (carpeta web\) viaja DENTRO del .exe como recursos «web/...»:
#      después de cambiar la web hay que volver a compilar.
# ═══════════════════════════════════════════════════════════════════════════

$ErrorActionPreference = 'Stop'
$raiz = Split-Path -Parent $MyInvocation.MyCommand.Path      # MiCine\windows
$proyecto = Split-Path -Parent $raiz                          # MiCine

$csc = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
if (-not (Test-Path $csc)) { $csc = Join-Path $env:WINDIR 'Microsoft.NET\Framework\v4.0.30319\csc.exe' }
if (-not (Test-Path $csc)) { Write-Host 'No encuentro csc.exe de .NET Framework 4.' -ForegroundColor Red; exit 1 }

# ── Icono: cuadrado rojo redondeado con el triángulo de «play» (el de web\icono.svg) ──
$icono = Join-Path $raiz 'MiCine.ico'
$generador = @'
using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;

public static class IconoMiCine
{
    static Bitmap Dibujar(int lado)
    {
        var bmp = new Bitmap(lado, lado, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;
            g.Clear(Color.Transparent);
            float f = lado / 64f, d = 28f * f;
            using (var p = new GraphicsPath())
            {
                p.AddArc(0, 0, d, d, 180, 90);
                p.AddArc(lado - d, 0, d, d, 270, 90);
                p.AddArc(lado - d, lado - d, d, d, 0, 90);
                p.AddArc(0, lado - d, d, d, 90, 90);
                p.CloseFigure();
                using (var b = new SolidBrush(Color.FromArgb(255, 0xE5, 0x48, 0x4D))) g.FillPath(b, p);
            }
            var t = new[] { new PointF(25 * f, 19 * f), new PointF(25 * f, 45 * f), new PointF(46 * f, 32 * f) };
            g.FillPolygon(Brushes.White, t);
        }
        return bmp;
    }

    // Imagen DIB de 32 bits (la que entienden todas las versiones de Windows y System.Drawing.Icon).
    static byte[] Dib(Bitmap bmp)
    {
        int lado = bmp.Width;
        var datos = bmp.LockBits(new Rectangle(0, 0, lado, lado), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        var px = new byte[datos.Stride * lado];
        Marshal.Copy(datos.Scan0, px, 0, px.Length);
        int stride = datos.Stride;
        bmp.UnlockBits(datos);
        int mascara = ((lado + 31) / 32) * 4 * lado;
        using (var ms = new MemoryStream())
        using (var w = new BinaryWriter(ms))
        {
            w.Write(40); w.Write(lado); w.Write(lado * 2); w.Write((short)1); w.Write((short)32);
            w.Write(0); w.Write(lado * 4 * lado + mascara); w.Write(0); w.Write(0); w.Write(0); w.Write(0);
            for (int y = lado - 1; y >= 0; y--) w.Write(px, y * stride, lado * 4);
            w.Write(new byte[mascara]);
            w.Flush();
            return ms.ToArray();
        }
    }

    public static void Escribir(string ruta)
    {
        int[] lados = { 16, 20, 24, 32, 40, 48, 64, 256 };
        var imagenes = new byte[lados.Length][];
        for (int i = 0; i < lados.Length; i++)
        {
            using (var bmp = Dibujar(lados[i]))
            {
                if (lados[i] == 256)
                {
                    using (var ms = new MemoryStream()) { bmp.Save(ms, ImageFormat.Png); imagenes[i] = ms.ToArray(); }
                }
                else imagenes[i] = Dib(bmp);
            }
        }
        using (var fs = new FileStream(ruta, FileMode.Create))
        using (var w = new BinaryWriter(fs))
        {
            w.Write((short)0); w.Write((short)1); w.Write((short)lados.Length);
            int desplazamiento = 6 + 16 * lados.Length;
            for (int i = 0; i < lados.Length; i++)
            {
                w.Write((byte)(lados[i] >= 256 ? 0 : lados[i]));
                w.Write((byte)(lados[i] >= 256 ? 0 : lados[i]));
                w.Write((byte)0); w.Write((byte)0);
                w.Write((short)1); w.Write((short)32);
                w.Write(imagenes[i].Length);
                w.Write(desplazamiento);
                desplazamiento += imagenes[i].Length;
            }
            foreach (var img in imagenes) w.Write(img);
        }
    }
}
'@
Add-Type -TypeDefinition $generador -ReferencedAssemblies System.Drawing
[IconoMiCine]::Escribir($icono)

# ── Fuentes y recursos ──
$fuentes = Get-ChildItem (Join-Path $raiz 'src') -Filter *.cs | ForEach-Object { $_.FullName }

# La web va dentro del .exe. El nombre del recurso es el que busca Web.cs: web/<ruta con />
$web = Join-Path $proyecto 'web'
$recursos = @(Get-ChildItem $web -Recurse -File | ForEach-Object {
    $relativa = $_.FullName.Substring($web.Length + 1).Replace('\', '/')
    "/resource:$($_.FullName),web/$relativa"
})
$recursos += "/resource:$icono,MiCine.ico"

$refs = @('System.dll', 'System.Core.dll', 'System.Drawing.dll', 'System.Windows.Forms.dll',
          'System.Web.Extensions.dll', 'System.Security.dll', 'System.Xml.dll') | ForEach-Object { "/reference:$_" }

$manifiesto = Join-Path $raiz 'MiCine.manifest'
$salida = Join-Path $proyecto 'MiCine.exe'
& $csc /nologo /codepage:65001 /platform:anycpu /target:winexe /optimize+ /warn:4 "/out:$salida" "/win32icon:$icono" "/win32manifest:$manifiesto" @refs @recursos $fuentes
if ($LASTEXITCODE -ne 0) { Write-Host 'La compilación ha fallado. (¿Está MiCine.exe abierto? Ciérralo desde el icono de la bandeja → Salir.)' -ForegroundColor Red; exit 1 }

$kb = [math]::Round((Get-Item $salida).Length / 1KB)
Write-Host "Hecho: $salida ($kb KB, $($recursos.Count - 1) archivos de la web dentro)" -ForegroundColor Green
