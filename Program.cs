using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography.X509Certificates;
using SkiaSharp;

var builder = WebApplication.CreateBuilder(args);

// HTTP en 5000 y HTTPS en 5443 (necesario para la cámara en vivo: getUserMedia exige contexto seguro)
const int HttpPort = 5000;
const int HttpsPort = 5443;

var certPath = Path.Combine(builder.Environment.ContentRootPath, "certs", "dev.crt");
var keyPath = Path.Combine(builder.Environment.ContentRootPath, "certs", "dev.key");

builder.WebHost.ConfigureKestrel(options =>
{
    options.ListenAnyIP(HttpPort);

    if (File.Exists(certPath) && File.Exists(keyPath))
    {
        var cert = X509Certificate2.CreateFromPemFile(certPath, keyPath);
        options.ListenAnyIP(HttpsPort, listen => listen.UseHttps(cert));
    }
});

// Permitir peticiones CORS desde el móvil/navegador local
builder.Services.AddCors(options =>
{
    options.AddDefaultPolicy(policy =>
        policy.AllowAnyOrigin().AllowAnyMethod().AllowAnyHeader());
});

var app = builder.Build();
app.UseCors();

// Cliente web de pruebas (wwwroot/index.html)
app.UseDefaultFiles();
app.UseStaticFiles();

const long MaxFileSizeBytes = 10 * 1024 * 1024;
const int MaxImageDimension = 8000;

// Verificamos al arrancar que el binario de Tesseract esté disponible
if (!TesseractCli.IsAvailable(out var tesseractVersion))
{
    app.Logger.LogCritical(
        "No se encontró el ejecutable 'tesseract'. Instálalo con: sudo apt install tesseract-ocr tesseract-ocr-spa");
    return 1;
}

app.Logger.LogInformation("Tesseract listo: {Version}", tesseractVersion);

app.MapPost("/api/ocr/scan", async (IFormFile file, bool spainOnly, CancellationToken requestAborted) =>
{
    if (file is null || file.Length == 0)
        return Results.BadRequest("Archivo no válido.");

    if (file.Length > MaxFileSizeBytes)
        return Results.BadRequest("El archivo supera el tamaño máximo permitido (10 MB).");

    // Leemos solo las dimensiones antes de decodificar para evitar imágenes descomprimidas desmesuradas
    SKImageInfo bounds;
    try
    {
        using var probeStream = file.OpenReadStream();
        bounds = SKBitmap.DecodeBounds(probeStream);
    }
    catch (Exception)
    {
        return Results.BadRequest("El archivo proporcionado no es una imagen válida.");
    }

    if (bounds.Width <= 0 || bounds.Height <= 0)
        return Results.BadRequest("El archivo proporcionado no es una imagen válida.");

    if (bounds.Width > MaxImageDimension || bounds.Height > MaxImageDimension)
        return Results.BadRequest($"La imagen supera las dimensiones máximas ({MaxImageDimension}px).");

    var tempFile = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.png");

    try
    {
        using var originalBitmap = SKBitmap.Decode(file.OpenReadStream());

        if (originalBitmap is null)
            return Results.BadRequest("El archivo proporcionado no es una imagen válida.");

        // Preprocesamiento con SkiaSharp: Escala de grises, Contraste y Binarización
        using var processedBitmap = PreprocessImage(originalBitmap, contrast: 1.3f, threshold: 0.5f);

        using var image = SKImage.FromBitmap(processedBitmap);
        using var data = image.Encode(SKEncodedImageFormat.Png, 100);

        await File.WriteAllBytesAsync(tempFile, data.ToArray(), requestAborted);

        // Tesseract CLI: --psm 7 = una sola línea de texto, salida TSV para obtener confianza y bbox
        // El whitelist con el motor LSTM no mejora (verificado: empata en aciertos y baja confianza),
        // por eso queda DESACTIVADO por defecto. OCR_WHITELIST=on lo activa; un valor propio lo define.
        // OCR_OEM permite forzar el motor (0=legacy, 1=LSTM).
        var whitelistEnv = Environment.GetEnvironmentVariable("OCR_WHITELIST");
        var whitelist = whitelistEnv switch
        {
            null or "off" or "" => null,
            "on" or "default" => TesseractCli.AlphanumericWhitelist,
            _ => whitelistEnv
        };
        int? oem = int.TryParse(Environment.GetEnvironmentVariable("OCR_OEM"), out var oemValue) ? oemValue : null;

        var result = await TesseractCli.RunAsync(tempFile, language: "spa", pageSegMode: 7, requestAborted, whitelist, oem);

        var normalized = PlateValidator.Normalize(result.Text);
        var repaired = false;
        if (spainOnly)
        {
            (normalized, repaired) = PlateValidator.RepairCurrent(result.Text);
        }
        var valid = PlateValidator.IsValid(normalized, spainOnly: spainOnly);

        return Results.Ok(new
        {
            result.Text,
            result.Confidence,
            normalized,
            valid,
            repaired,
            bbox = result.Bbox  // píxeles del PNG procesado (o null si no hay palabras)
        });
    }
    catch (OperationCanceledException)
    {
        return Results.StatusCode(499);
    }
    catch (Exception ex)
    {
        app.Logger.LogError(ex, "Error procesando la imagen con OCR");
        return Results.Problem("No se pudo procesar la imagen con OCR.", statusCode: 500);
    }
    finally
    {
        try
        {
            if (File.Exists(tempFile))
                File.Delete(tempFile);
        }
        catch (Exception)
        {
            // La limpieza del temporal es best-effort
        }
    }
}).DisableAntiforgery();

// Detección de la placa (región clara rectangular) para auto-encuadre en el cliente
app.MapPost("/api/ocr/detect", (IFormFile file) =>
{
    if (file is null || file.Length == 0)
        return Results.BadRequest("Archivo no válido.");

    if (file.Length > MaxFileSizeBytes)
        return Results.BadRequest("El archivo supera el tamaño máximo permitido (10 MB).");

    try
    {
        SKImageInfo detectBounds;
        using (var probe = file.OpenReadStream())
        {
            detectBounds = SKBitmap.DecodeBounds(probe);
        }

        if (detectBounds.Width <= 0 || detectBounds.Height <= 0)
            return Results.BadRequest("El archivo proporcionado no es una imagen válida.");

        if (detectBounds.Width > MaxImageDimension || detectBounds.Height > MaxImageDimension)
            return Results.BadRequest($"La imagen supera las dimensiones máximas ({MaxImageDimension}px).");

        using var bitmap = SKBitmap.Decode(file.OpenReadStream());
        if (bitmap is null)
            return Results.BadRequest("El archivo proporcionado no es una imagen válida.");

        var detection = PlateDetector.Detect(bitmap);
        return Results.Ok(detection);
    }
    catch (Exception ex)
    {
        app.Logger.LogError(ex, "Error detectando la placa");
        return Results.Problem("No se pudo detectar la placa.", statusCode: 500);
    }
}).DisableAntiforgery();

// Captura estática: localiza el ROI con el detector prioritario y procesa EXCLUSIVAMENTE esa región
app.MapPost("/api/ocr/capture", async (IFormFile file, bool spainOnly, CancellationToken requestAborted) =>
{
    if (file is null || file.Length == 0)
        return Results.BadRequest("Archivo no válido.");

    if (file.Length > MaxFileSizeBytes)
        return Results.BadRequest("El archivo supera el tamaño máximo permitido (10 MB).");

    SKImageInfo captureBounds;
    try
    {
        using var probeStream = file.OpenReadStream();
        captureBounds = SKBitmap.DecodeBounds(probeStream);
    }
    catch (Exception)
    {
        return Results.BadRequest("El archivo proporcionado no es una imagen válida.");
    }

    if (captureBounds.Width <= 0 || captureBounds.Height <= 0)
        return Results.BadRequest("El archivo proporcionado no es una imagen válida.");

    if (captureBounds.Width > MaxImageDimension || captureBounds.Height > MaxImageDimension)
        return Results.BadRequest($"La imagen supera las dimensiones máximas ({MaxImageDimension}px).");

    var tempFile = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.png");

    try
    {
        using var original = SKBitmap.Decode(file.OpenReadStream());
        if (original is null)
            return Results.BadRequest("El archivo proporcionado no es una imagen válida.");

        // Detector prioritario sobre la imagen completa (a resolución de captura)
        var locate = PlateDetector.Locate(original, PlateDetector.CaptureWidth);

        SKBitmap? owned = null;
        RectDto? roi = null;
        var mode = "full";
        try
        {
            SKBitmap toProcess;
            if (locate.Found && locate.PixelRect is not null)
            {
                var r = locate.PixelRect;
                owned = CropForOcr(original, r);
                toProcess = owned;
                roi = new RectDto(
                    (double)r.X / original.Width,
                    (double)r.Y / original.Height,
                    (double)r.Width / original.Width,
                    (double)r.Height / original.Height);
                mode = "roi";
            }
            else
            {
                // Fallback: sin ROI plausible, procesamos la imagen completa (reducida si es enorme)
                owned = DownscaleForOcr(original);
                toProcess = owned ?? original;
            }

            using var processedBitmap = PreprocessImage(toProcess, contrast: 1.3f, threshold: 0.5f);
            using var image = SKImage.FromBitmap(processedBitmap);
            using var data = image.Encode(SKEncodedImageFormat.Png, 100);
            await File.WriteAllBytesAsync(tempFile, data.ToArray(), requestAborted);

            var whitelistEnv = Environment.GetEnvironmentVariable("OCR_WHITELIST");
            var whitelist = whitelistEnv switch
            {
                null or "off" or "" => null,
                "on" or "default" => TesseractCli.AlphanumericWhitelist,
                _ => whitelistEnv
            };
            int? oem = int.TryParse(Environment.GetEnvironmentVariable("OCR_OEM"), out var oemValue) ? oemValue : null;

            var result = await TesseractCli.RunAsync(tempFile, language: "spa", pageSegMode: 7, requestAborted, whitelist, oem);

            var normalized = PlateValidator.Normalize(result.Text);
            var repaired = false;
            if (spainOnly)
            {
                (normalized, repaired) = PlateValidator.RepairCurrent(result.Text);
            }
            var valid = PlateValidator.IsValid(normalized, spainOnly: spainOnly);

            return Results.Ok(new
            {
                result.Text,
                result.Confidence,
                normalized,
                valid,
                repaired,
                roi,
                mode,
                chars = locate.Chars
            });
        }
        finally
        {
            owned?.Dispose();
        }
    }
    catch (OperationCanceledException)
    {
        return Results.StatusCode(499);
    }
    catch (Exception ex)
    {
        app.Logger.LogError(ex, "Error procesando la captura estática");
        return Results.Problem("No se pudo procesar la captura.", statusCode: 500);
    }
    finally
    {
        try
        {
            if (File.Exists(tempFile))
                File.Delete(tempFile);
        }
        catch (Exception)
        {
            // La limpieza del temporal es best-effort
        }
    }
}).DisableAntiforgery();

app.Run();
return 0;

// Función auxiliar para el preprocesamiento de la imagen con SkiaSharp
static SKBitmap PreprocessImage(SKBitmap original, float contrast, float threshold)
{
    var width = original.Width;
    var height = original.Height;

    // 1. Convertir a escala de grises usando una matriz de color ajustada por contraste
    var info = new SKImageInfo(width, height, SKColorType.Gray8, SKAlphaType.Opaque);
    using var grayscaleBitmap = new SKBitmap(info);

    // Factor de contraste (1.3f)
    float t = (1.0f - contrast) / 2.0f;

    // Matriz que combina luminancia NTSC (grises) y ajuste de contraste
    float[] colorMatrix = new float[]
    {
        0.2126f * contrast, 0.7152f * contrast, 0.0722f * contrast, 0, t,
        0.2126f * contrast, 0.7152f * contrast, 0.0722f * contrast, 0, t,
        0.2126f * contrast, 0.7152f * contrast, 0.0722f * contrast, 0, t,
        0,                  0,                  0,                  1, 0
    };

    using (var surface = SKSurface.Create(info))
    {
        using var canvas = surface.Canvas;
        using var paint = new SKPaint();
        paint.ColorFilter = SKColorFilter.CreateColorMatrix(colorMatrix);

        var sampling = new SKSamplingOptions(SKFilterMode.Nearest, SKMipmapMode.None);
        canvas.DrawBitmap(original, 0, 0, sampling, paint);
        canvas.Flush();

        using var snap = surface.Snapshot();
        snap.ReadPixels(info, grayscaleBitmap.GetPixels(), info.RowBytes, 0, 0);
    }

    // 2. Aplicar Binarización / Umbralizado (Thresholding) directamente sobre los píxeles
    var binaryBitmap = new SKBitmap(width, height, SKColorType.Gray8, SKAlphaType.Opaque);

    // Sustituto seguro sin bloque 'unsafe'
    var srcPixels = grayscaleBitmap.GetPixelSpan();
    var dstPixels = binaryBitmap.GetPixelSpan();
    byte thresholdByte = (byte)(threshold * 255);

    for (int i = 0; i < srcPixels.Length; i++)
    {
        dstPixels[i] = srcPixels[i] >= thresholdByte ? (byte)255 : (byte)0;
    }

    return binaryBitmap;
}

// Recorta el ROI (en píxeles del original) con márgenes y lo normaliza para OCR:
// sube si es pequeño (altura objetivo) y baja si es enorme, para dar una entrada razonable a Tesseract.
static SKBitmap CropForOcr(SKBitmap source, Rect rect)
{
    const int TargetHeight = 220;
    const int MinOcrHeight = 110;
    const int MaxOcrHeight = 420;
    const double MarginX = 0.12;
    const double MarginY = 0.25;

    var mx = (int)Math.Round(rect.Width * MarginX);
    var my = (int)Math.Round(rect.Height * MarginY);
    var x = Math.Clamp(rect.X - mx, 0, Math.Max(0, source.Width - 1));
    var y = Math.Clamp(rect.Y - my, 0, Math.Max(0, source.Height - 1));
    var w = Math.Clamp(rect.Width + mx * 2, 1, source.Width - x);
    var h = Math.Clamp(rect.Height + my * 2, 1, source.Height - y);

    var targetH = h < MinOcrHeight ? Math.Min(MaxOcrHeight, TargetHeight)
        : h > MaxOcrHeight ? MaxOcrHeight
        : h;
    var scale = (double)targetH / h;
    var nw = Math.Max(1, (int)Math.Round(w * scale));

    var result = new SKBitmap(new SKImageInfo(nw, targetH, source.ColorType, source.AlphaType));
    using var canvas = new SKCanvas(result);
    canvas.Clear(SKColors.Black);
    var sampling = new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.Linear);
    canvas.DrawBitmap(source,
        new SKRect(x, y, x + w, y + h),
        new SKRect(0, 0, nw, targetH),
        sampling);
    canvas.Flush();
    return result;
}

// Reduce una imagen completa a un ancho manejable para el OCR (fallback sin ROI)
static SKBitmap DownscaleForOcr(SKBitmap source)
{
    const int MaxOcrWidth = 1400;
    if (source.Width <= MaxOcrWidth)
        return source.Copy();

    var scale = (double)MaxOcrWidth / source.Width;
    var nh = Math.Max(1, (int)Math.Round(source.Height * scale));
    return source.Resize(new SKSizeI(MaxOcrWidth, nh), new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.None));
}

record OcrResult(string Text, float Confidence, Rect? Bbox);
record Rect(int X, int Y, int Width, int Height);
record DetectionResult(bool Found, RectDto? Rect, int Candidates);
record RectDto(double X, double Y, double W, double H); // normalizado 0..1
record LocateResult(bool Found, Rect? PixelRect, double Score, int Chars, int Candidates);

// Validación de formatos de matrícula española (actual, especial e histórica provincial)
static class PlateValidator
{
    private const string Letters = "ABCDEFGHIJKLMNOPQRSTUVWXYZ";

    // Códigos provinciales históricos (hasta 2000)
    private static readonly HashSet<string> ProvinceCodes = new(StringComparer.Ordinal)
    {
        "A", "AB", "AL", "AV", "B", "BA", "BI", "BU", "C", "CA", "CC", "CE", "CO", "CR",
        "CS", "CU", "GC", "GE", "GI", "GR", "GU", "H", "HU", "J", "L", "LE", "LO", "LU",
        "M", "MA", "ME", "MU", "NA", "O", "OR", "OU", "P", "PM", "PO", "S", "SA", "SE",
        "SG", "SO", "SS", "T", "TE", "TF", "TO", "V", "VA", "VI", "Z", "ZA"
    };

    /// Mayúsculas y sin espacios, guiones ni puntos.
    public static string Normalize(string text) =>
        new string((text ?? string.Empty).Where(c => !char.IsWhiteSpace(c) && c != '-' && c != '.').ToArray())
            .ToUpperInvariant();

    public static bool IsValid(string text) => IsValid(text, spainOnly: true);

    // spainOnly=true (check 🇪🇸): SOLO formato actual 0000 BBB.
    // spainOnly=false (general): actual + especial E0000AA + histórica provincial + plausible internacional.
    public static bool IsValid(string text, bool spainOnly)
    {
        var s = Normalize(text);
        if (s.Length == 0)
            return false;

        if (spainOnly)
            return IsValidCurrent(s);

        return IsValidCurrent(s) || IsValidSpecial(s) || IsValidHistoric(s) || IsPlausible(s);
    }

    // Actual: 0000 BBB (4 dígitos + 3 letras)
    public static bool IsValidCurrent(string text)
    {
        var s = Normalize(text);
        return s.Length == 7 && IsDigits(s, 0, 4) && IsLetters(s, 4, 3);
    }

    // Especial: E 0000 AA (E + 4 dígitos + 2 letras)
    private static bool IsValidSpecial(string s) =>
        s.Length == 7 && s[0] == 'E' && IsDigits(s, 1, 4) && IsLetters(s, 5, 2);

    // Histórica provincial: [código] + 1..6 dígitos + sufijo opcional de 2 letras
    private static bool IsValidHistoric(string s)
    {
        for (var codeLen = 2; codeLen >= 1; codeLen--)
        {
            if (s.Length <= codeLen)
                continue;

            var code = s[..codeLen];
            if (!ProvinceCodes.Contains(code))
                continue;

            var rest = s[codeLen..];
            if (rest.Length >= 3 && rest.Length <= 6 && rest.All(char.IsAsciiDigit))
                return true;
            if (rest.Length >= 5 && rest.Length <= 8 &&
                rest[..^2].Length >= 3 && rest[..^2].All(char.IsAsciiDigit) && IsLetters(rest, rest.Length - 2, 2))
                return true;
        }

        return false;
    }

    // Plausibilidad internacional (matrículas extranjeras): ≥5 caracteres alfanuméricos
    private static bool IsPlausible(string s) => s.Length >= 5 && s.All(char.IsAsciiLetterOrDigit);

    /// <summary>
    /// Reparación del formato actual (solo con spainOnly). Descarta ruido de los extremos del
    /// recorte, típicamente la banda azul UE pegada a la izquierda de las cifras:
    ///  - más de 4 dígitos → se descartan los más a la IZQUIERDA (se conservan los 4 de la derecha);
    ///  - letras sueltas iniciales (la "E" de la banda) → se descartan;
    ///  - más de 3 letras finales → se descartan las más a la DERECHA.
    /// Solo devuelve un texto reparado si encaja en 0000 BBB; si no, deja el texto como estaba.
    /// </summary>
    public static (string Normalized, bool Repaired) RepairCurrent(string text)
    {
        var s = new string(Normalize(text).Where(char.IsAsciiLetterOrDigit).ToArray());
        if (IsValidCurrent(s))
            return (s, false);

        var (_, digits, tailLetters) = SplitRuns(s);
        if (digits.Length == 0 || tailLetters.Length == 0)
            return (s, false);

        var fixedDigits = digits.Length > 4 ? digits[^4..] : digits;
        var fixedTail = tailLetters.Length > 3 ? tailLetters[..3] : tailLetters;
        var candidate = fixedDigits + fixedTail;

        return IsValidCurrent(candidate) ? (candidate, candidate != s) : (s, false);
    }

    // Descompone "letras? dígitos letras?" en sus tres runs; estructura inesperada → todo vacío.
    private static (string Lead, string Digits, string Tail) SplitRuns(string s)
    {
        var i = 0;
        while (i < s.Length && char.IsAsciiLetter(s[i])) i++;
        var lead = s[..i];

        var d = i;
        while (d < s.Length && char.IsAsciiDigit(s[d])) d++;
        var digits = s[i..d];

        var t = d;
        while (t < s.Length && char.IsAsciiLetter(s[t])) t++;
        var tail = s[d..t];

        if (t != s.Length || digits.Length == 0)
            return (string.Empty, string.Empty, string.Empty);

        return (lead, digits, tail);
    }

    private static bool IsDigits(string s, int start, int count) =>
        s.Length >= start + count && s.AsSpan(start, count).ToString().All(char.IsAsciiDigit);

    private static bool IsLetters(string s, int start, int count) =>
        s.Length >= start + count && s.AsSpan(start, count).ToString().All(c => Letters.Contains(c));
}


file static class TesseractCli
{
    private const int TimeoutMs = 15000;
    // Alfabeto alfanumérico completo + separadores de matrícula (espacio/guion)
    public const string AlphanumericWhitelist = "ABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789 -";

    public static bool IsAvailable(out string version)
    {
        version = string.Empty;

        try
        {
            using var process = Process.Start(new ProcessStartInfo("tesseract", "--version")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            });

            if (process is null)
                return false;

            version = (process.StandardOutput.ReadToEnd() + process.StandardError.ReadToEnd())
                .Split('\n', StringSplitOptions.RemoveEmptyEntries)
                .FirstOrDefault()?.Trim() ?? string.Empty;

            process.WaitForExit(5000);
            return process.ExitCode == 0;
        }
        catch (Exception)
        {
            return false;
        }
    }

    public static async Task<OcrResult> RunAsync(
        string imagePath,
        string language,
        int pageSegMode,
        CancellationToken ct,
        string? charWhitelist = null,
        int? oem = null)
    {
        var startInfo = new ProcessStartInfo("tesseract")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        startInfo.ArgumentList.Add(imagePath);
        startInfo.ArgumentList.Add("stdout");
        startInfo.ArgumentList.Add("-l");
        startInfo.ArgumentList.Add(language);
        startInfo.ArgumentList.Add("--psm");
        startInfo.ArgumentList.Add(pageSegMode.ToString(CultureInfo.InvariantCulture));
        if (oem.HasValue)
        {
            startInfo.ArgumentList.Add("--oem");
            startInfo.ArgumentList.Add(oem.Value.ToString(CultureInfo.InvariantCulture));
        }
        if (!string.IsNullOrEmpty(charWhitelist))
        {
            startInfo.ArgumentList.Add("-c");
            startInfo.ArgumentList.Add($"tessedit_char_whitelist={charWhitelist}");
        }
        startInfo.ArgumentList.Add("tsv");

        using var process = new Process { StartInfo = startInfo };

        if (!process.Start())
            throw new InvalidOperationException("No se pudo iniciar el proceso de Tesseract.");

        var stdoutTask = process.StandardOutput.ReadToEndAsync(ct);
        var stderrTask = process.StandardError.ReadToEndAsync(ct);

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(TimeoutMs);

        try
        {
            await process.WaitForExitAsync(timeoutCts.Token);
        }
        catch (OperationCanceledException)
        {
            try
            {
                process.Kill(entireProcessTree: true);
            }
            catch (Exception)
            {
                // El proceso ya pudo haber terminado
            }

            throw;
        }

        var stdout = await stdoutTask;
        var stderr = await stderrTask;

        if (process.ExitCode != 0)
            throw new InvalidOperationException($"Tesseract falló (código {process.ExitCode}): {stderr.Trim()}");

        return ParseTsv(stdout);
    }

    private static OcrResult ParseTsv(string tsv)
    {
        var words = new List<string>();
        double confidenceSum = 0;
        var confidenceCount = 0;
        int? minX = null, minY = null, maxX = null, maxY = null;

        foreach (var line in tsv.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var columns = line.Split('\t');

            // Formato TSV: level, page, block, par, line, word, left, top, width, height, conf, text
            if (columns.Length < 12 || columns[0] != "5")
                continue;

            if (!float.TryParse(columns[10], NumberStyles.Float, CultureInfo.InvariantCulture, out var confidence) || confidence < 0)
                continue;

            var text = columns[11].Trim();
            if (text.Length == 0)
                continue;

            words.Add(text);
            confidenceSum += confidence;
            confidenceCount++;

            // Unión del bbox de las palabras para auto-encuadre
            if (int.TryParse(columns[6], out var left) && int.TryParse(columns[7], out var top) &&
                int.TryParse(columns[8], out var width) && int.TryParse(columns[9], out var height))
            {
                minX = minX is null ? left : Math.Min(minX.Value, left);
                minY = minY is null ? top : Math.Min(minY.Value, top);
                maxX = maxX is null ? left + width : Math.Max(maxX.Value, left + width);
                maxY = maxY is null ? top + height : Math.Max(maxY.Value, top + height);
            }
        }

        // Tesseract expone la confianza en rango 0-100; la normalizamos a 0-1 como antes
        var meanConfidence = confidenceCount > 0 ? (float)(confidenceSum / confidenceCount) / 100f : 0f;

        Rect? bbox = minX.HasValue
            ? new Rect(minX.Value, minY!.Value, maxX!.Value - minX.Value, maxY!.Value - minY.Value)
            : null;

        return new OcrResult(string.Join(" ", words), meanConfidence, bbox);
    }
}

// Detector de la placa v2: localiza el ROI a partir de las proporciones de la matrícula
// y de filtros agresivos sobre los propios caracteres (descarta regiones sin texto).
// Otsu + componentes conexos de los caracteres (oscuros) + agrupación en línea de texto.
static class PlateDetector
{
    private readonly record struct Char(int MinX, int MinY, int MaxX, int MaxY, int Count)
    {
        public int W => MaxX - MinX + 1;
        public int H => MaxY - MinY + 1;
        public int Cx => (MinX + MaxX) / 2;
        public int Cy => (MinY + MaxY) / 2;
    }

    private const int StreamProcessWidth = 640;   // asistencia en vivo (~3 fps)
    private const int CaptureProcessWidth = 960;  // captura estática (ROI más preciso)

    // Filtros agresivos por carácter: qué se considera un posible carácter
    private const double MinCharAspect = 0.10;      // w/h (la "I"/"1" son estrechos)
    private const double MaxCharAspect = 1.40;      // w/h (la "M"/"W" son anchos)
    private const double MinCharHeightRatio = 0.015; // respecto a la altura del frame
    private const double MaxCharHeightRatio = 0.25;
    private const double MinCharFill = 0.25;         // tinta dentro del bbox
    private const int MinCharPixels = 6;

    // Filtros de la línea de texto (proporciones de matrícula)
    private const double TargetLineAspect = 5.0;
    private const double MinLineAspect = 2.6;
    private const double MaxLineAspect = 8.0;
    private const int MinChars = 4;
    private const int MaxChars = 9;
    private const double MaxHeightRatio = 2.4; // altura máx/mín de los caracteres

    /// Localiza el ROI y devuelve el rectángulo en píxeles del bitmap ORIGINAL.
    public static LocateResult Locate(SKBitmap source, int maxProcessWidth)
    {
        if (source.Width <= 0 || source.Height <= 0)
            return new LocateResult(false, null, 0, 0, 0);

        var scale = source.Width > maxProcessWidth ? (double)maxProcessWidth / source.Width : 1.0;
        var w = Math.Max(1, (int)Math.Round(source.Width * scale));
        var h = Math.Max(1, (int)Math.Round(source.Height * scale));

        using var resized = source.Resize(new SKSizeI(w, h), new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.None));
        using var gray = resized.Copy(SKColorType.Gray8);
        if (gray is null)
            return new LocateResult(false, null, 0, 0, 0);

        var pixels = gray.GetPixelSpan();
        var total = pixels.Length;

        var threshold = Otsu(pixels);
        var frameArea = (double)total;

        // --- Componentes conexos de los píxeles OSCUROS (los caracteres) ---
        var chars = new List<Char>();
        var visited = new bool[total];
        var stack = new int[total];
        var minH = Math.Max(3, (int)(h * MinCharHeightRatio));
        var maxH = Math.Max(minH, (int)(h * MaxCharHeightRatio));

        for (var start = 0; start < total; start++)
        {
            if (pixels[start] >= threshold || visited[start]) continue;

            var sp = 0;
            stack[sp++] = start;
            visited[start] = true;

            int minX = w, minY = h, maxX = -1, maxY = -1, count = 0;

            while (sp > 0)
            {
                var idx = stack[--sp];
                var x = idx % w;
                var y = idx / w;
                count++;
                if (x < minX) minX = x;
                if (x > maxX) maxX = x;
                if (y < minY) minY = y;
                if (y > maxY) maxY = y;

                // 4-vecinos sobre píxeles oscuros
                if (x > 0 && pixels[idx - 1] < threshold && !visited[idx - 1]) { visited[idx - 1] = true; stack[sp++] = idx - 1; }
                if (x < w - 1 && pixels[idx + 1] < threshold && !visited[idx + 1]) { visited[idx + 1] = true; stack[sp++] = idx + 1; }
                if (y > 0 && pixels[idx - w] < threshold && !visited[idx - w]) { visited[idx - w] = true; stack[sp++] = idx - w; }
                if (y < h - 1 && pixels[idx + w] < threshold && !visited[idx + w]) { visited[idx + w] = true; stack[sp++] = idx + w; }
            }

            var bw = maxX - minX + 1;
            var bh = maxY - minY + 1;
            if (bw <= 0 || bh <= 0) continue;

            // Filtros agresivos por carácter
            if (count < MinCharPixels) continue;
            if (bh < minH || bh > maxH) continue;
            var aspect = (double)bw / bh;
            if (aspect < MinCharAspect || aspect > MaxCharAspect) continue;
            var fill = count / ((double)bw * bh);
            if (fill < MinCharFill) continue;
            // Descartamos componentes que tocan el borde del frame (posiblemente recortados)
            if (minX == 0 || maxX == w - 1 || minY == 0 || maxY == h - 1) continue;

            chars.Add(new Char(minX, minY, maxX, maxY, count));
        }

        if (chars.Count < MinChars)
            return new LocateResult(false, null, 0, 0, 0);

        // --- Agrupación en líneas de texto por solape vertical ---
        chars.Sort((a, b) => a.Cy.CompareTo(b.Cy));
        var groups = new List<List<Char>>();
        foreach (var c in chars)
        {
            List<Char>? target = null;
            foreach (var g in groups)
            {
                var gMinY = int.MaxValue;
                var gMaxY = int.MinValue;
                foreach (var gc in g)
                {
                    if (gc.MinY < gMinY) gMinY = gc.MinY;
                    if (gc.MaxY > gMaxY) gMaxY = gc.MaxY;
                }
                var overlap = Math.Min(gMaxY, c.MaxY) - Math.Max(gMinY, c.MinY) + 1;
                var smaller = Math.Min(gMaxY - gMinY + 1, c.H);
                if (overlap >= 0.5 * smaller) { target = g; break; }
            }
            if (target is null) groups.Add(new List<Char> { c });
            else target.Add(c);
        }

        double bestScore = 0;
        Char? best = null;
        var evaluated = 0;

        foreach (var g in groups)
        {
            if (g.Count < MinChars || g.Count > MaxChars) continue;

            g.Sort((a, b) => a.Cx.CompareTo(b.Cx));

            int gMinX = int.MaxValue, gMaxX = int.MinValue, gMinY = int.MaxValue, gMaxY = int.MinValue;
            int minCharH = int.MaxValue, maxCharH = 0;
            foreach (var c in g)
            {
                if (c.MinX < gMinX) gMinX = c.MinX;
                if (c.MaxX > gMaxX) gMaxX = c.MaxX;
                if (c.MinY < gMinY) gMinY = c.MinY;
                if (c.MaxY > gMaxY) gMaxY = c.MaxY;
                if (c.H < minCharH) minCharH = c.H;
                if (c.H > maxCharH) maxCharH = c.H;
            }

            var lw = gMaxX - gMinX + 1;
            var lh = gMaxY - gMinY + 1;
            var lineAspect = (double)lw / lh;
            if (lineAspect < MinLineAspect || lineAspect > MaxLineAspect) continue;

            // Consistencia de alturas (misma tipografía)
            var heightRatio = (double)maxCharH / minCharH;
            if (heightRatio > MaxHeightRatio) continue;

            // Espaciado razonablemente uniforme entre caracteres consecutivos
            var gapSum = 0.0;
            var gapMax = 0;
            var gapCount = 0;
            for (var i = 1; i < g.Count; i++)
            {
                var gap = g[i].MinX - g[i - 1].MaxX;
                if (gap < 0) gap = 0;
                gapSum += gap;
                gapCount++;
                if (gap > gapMax) gapMax = gap;
            }
            var gapMean = gapCount > 0 ? gapSum / gapCount : 0;
            if (gapMax > 2.0 * maxCharH) continue; // huecos enormes: no es una matrícula

            evaluated++;

            var aspectScore = 1.0 / (1.0 + Math.Abs(lineAspect - TargetLineAspect));
            var countScore = 1.0 / (1.0 + Math.Abs(g.Count - 7) * 0.35);
            var heightScore = (double)minCharH / maxCharH;
            var gapScore = gapMean > 0 ? 1.0 / (1.0 + gapMax / (2.0 * gapMean)) : 1.0;
            var areaRatio = (double)lw * lh / frameArea;
            var score = aspectScore * countScore * heightScore * (0.7 + 0.3 * gapScore) * Math.Sqrt(Math.Min(1.0, areaRatio * 8.0));

            if (best is null || score > bestScore)
            {
                bestScore = score;
                best = new Char(gMinX, gMinY, gMaxX, gMaxY, g.Count);
            }
        }

        if (best is null)
            return new LocateResult(false, null, 0, 0, evaluated);

        // Expandimos el bbox de la línea a la matrícula (márgenes proporcionales)
        var bestChar = best.Value;
        var charH = bestChar.H;
        var mx = (int)Math.Round(charH * 0.9);
        var my = (int)Math.Round(charH * 0.4);
        var rx = Math.Max(0, bestChar.MinX - mx);
        var ry = Math.Max(0, bestChar.MinY - my);
        var rx2 = Math.Min(w - 1, bestChar.MaxX + mx);
        var ry2 = Math.Min(h - 1, bestChar.MaxY + my);
        var rw = rx2 - rx + 1;
        var rh = ry2 - ry + 1;

        // Volvemos a coordenadas del bitmap original
        var ox = (int)Math.Round(rx / scale);
        var oy = (int)Math.Round(ry / scale);
        var ow = (int)Math.Round(rw / scale);
        var oh = (int)Math.Round(rh / scale);
        ox = Math.Clamp(ox, 0, Math.Max(0, source.Width - 1));
        oy = Math.Clamp(oy, 0, Math.Max(0, source.Height - 1));
        ow = Math.Clamp(ow, 1, source.Width - ox);
        oh = Math.Clamp(oh, 1, source.Height - oy);

        return new LocateResult(true, new Rect(ox, oy, ow, oh), bestScore, bestChar.Count, evaluated);
    }

    /// Detección para el cliente (rect normalizado 0..1). Usada por /api/ocr/detect
    public static DetectionResult Detect(SKBitmap source)
    {
        var r = Locate(source, StreamProcessWidth);
        if (!r.Found || r.PixelRect is null)
            return new DetectionResult(false, null, r.Candidates);

        var p = r.PixelRect;
        var rect = new RectDto(
            (double)p.X / source.Width,
            (double)p.Y / source.Height,
            (double)p.Width / source.Width,
            (double)p.Height / source.Height);
        return new DetectionResult(true, rect, r.Candidates);
    }

    public static int CaptureWidth => CaptureProcessWidth;

    private static int Otsu(ReadOnlySpan<byte> pixels)
    {
        var histogram = new int[256];
        long sum = 0;
        for (var i = 0; i < pixels.Length; i++)
        {
            histogram[pixels[i]]++;
            sum += pixels[i];
        }

        var total = pixels.Length;
        double sumB = 0;
        var weightB = 0;
        double maxVariance = -1;
        var threshold = 128;
        for (var t = 0; t < 256; t++)
        {
            weightB += histogram[t];
            if (weightB == 0) continue;
            var weightF = total - weightB;
            if (weightF == 0) break;

            sumB += (double)t * histogram[t];
            var meanB = sumB / weightB;
            var meanF = (sum - sumB) / weightF;
            var between = (double)weightB * weightF * (meanB - meanF) * (meanB - meanF);
            if (between > maxVariance)
            {
                maxVariance = between;
                threshold = t;
            }
        }
        return threshold;
    }
}
