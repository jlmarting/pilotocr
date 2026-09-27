# LEARN: Detector de ROI por caracteres

## Concepto

En vez de buscar la matrícula como **un rectángulo blanco**, la buscamos por **sus letras y números**. La idea es: *"una matrícula es, ante todo, una fila de 4 a 9 caracteres alineados con proporciones muy concretas"*. Localizamos esos caracteres (componentes conexos oscuros), los agrupamos en líneas de texto y puntuamos las líneas según las **proporciones de una matrícula española**.

Este es el algoritmo `PlateDetector` v2 de este proyecto, usado tanto para la asistencia en vivo (~3 fps) como para localizar el ROI en la captura estática.

## Por qué es importante

- **Descarta lo que no es matrícula.** Un detector de "región blanca" se confunde con faros, reflectores, matrículas en blanco, carteles o carrocería. Un detector de *caracteres* exige que dentro haya **letras y números de verdad**: si no hay texto, no hay matrícula.
- **Es más robusto con iluminación.** La linterna puede quemar el fondo blanco, pero los caracteres oscuros siguen separados por Otsu.
- **Aprovecha el conocimiento del dominio.** Sabemos que la matrícula mide ~520×110 mm (aspecto ≈4.7) y que el texto tiene entre 4 y 9 caracteres de altura parecida. No usar ese dato sería tirar información gratis.
- **Es rápido.** Todo con arrays y flood-fill, sin dependencias: **~35 ms por frame**, suficiente para sostener 3 imágenes por segundo.

## Explicación sencilla

Imagina que buscas una inscripción grabada en una pared con muchas manchas. Un enfoque ingenuo es buscar "la zona más clara de la pared": pero hay manchas claras por todas partes. Un enfoque mejor es buscar **letras sueltas** y ver cuáles se alinean formando una palabra.

Eso es exactamente lo que hacemos en dos fases:

```
FASE 1 — Encontrar "letras" candidatas (componentes conexos oscuros)
   ✗ se descarta: manchas sin forma de carácter, ruido, bordes de la imagen

FASE 2 — Agrupar letras en líneas y puntuarlas
   ✗ se descarta: grupos con pocos/muchos caracteres, alturas dispares,
                  huecos enormes, aspecto que no cuadra con una matrícula
   ✓ ganador: la línea cuyo aspecto, nº de caracteres y consistencia
              se parecen más a una matrícula real
```

La clave son los **filtros agresivos**: preferimos no detectar (fallback) antes que detectar basura. Es mucho mejor un "no la encontré" que una caja verde señalando un faro.

## Ejemplo práctico

### Fase 1: filtrar por carácter

Tras Otsu y un flood-fill de 4 vecinos, cada componente oscuro se evalúa. Si no parece un carácter, se tira:

```csharp
// Filtros agresivos por carácter: qué se considera un posible carácter
const double MinCharAspect = 0.10;       // w/h  → la "I" o el "1" son muy estrechos
const double MaxCharAspect = 1.40;       // w/h  → la "M" o la "W" son anchos
const double MinCharHeightRatio = 0.015; // altura respecto al frame (nada diminuto)
const double MaxCharHeightRatio = 0.25;  // ni tan grande como para ser la matrícula entera
const double MinCharFill = 0.25;         // % de "tinta" dentro de su caja (descarta trazos sueltos)
const int MinCharPixels = 6;             // umbral de ruido

var bw = maxX - minX + 1;                // ancho de la caja del componente
var bh = maxY - minY + 1;                // alto
if (count < MinCharPixels) continue;                     // demasiado pequeño → ruido
if (bh < minH || bh > maxH) continue;                    // altura fuera de rango
var aspect = (double)bw / bh;
if (aspect < MinCharAspect || aspect > MaxCharAspect) continue;
var fill = count / ((double)bw * bh);
if (fill < MinCharFill) continue;                        // hueco: no es una letra
// Un componente que toca el borde pudo quedar recortado: lo descartamos
if (minX == 0 || maxX == w - 1 || minY == 0 || maxY == h - 1) continue;
```

> 💡 **Detalle fino:** comprobar `x == 0 || y == 0 || x == w-1 || y == h-1` evita que un "pedazo" de algo más grande (una barandilla, un coche) pase por carácter.

### Fase 2: agrupar en línea y puntuar con proporciones de matrícula

Los caracteres se agrupan por **solape vertical** (los que "van en la misma fila") y ordenan por X. Después se aplican los filtros de grupo:

```csharp
// Filtros de la línea (proporciones de matrícula)
const double TargetLineAspect = 5.0;   // aspecto ideal de la línea de texto
const double MinLineAspect = 2.6;      // por debajo: no parece matrícula
const double MaxLineAspect = 8.0;      // por encima: probablemente no lo es
const int MinChars = 4;                // una matrícula tiene al menos 4 caracteres
const int MaxChars = 9;                // ...y como mucho 9 (históricas incluidas)
const double MaxHeightRatio = 2.4;     // altura máx/mín: misma tipografía

if (g.Count < MinChars || g.Count > MaxChars) continue;
// ... calcular gMinX/gMaxX/gMinY/gMaxY y alturas ...
var lineAspect = (double)lw / lh;
if (lineAspect < MinLineAspect || lineAspect > MaxLineAspect) continue;
var heightRatio = (double)maxCharH / minCharH;   // si los caracteres son coherentes
if (heightRatio > MaxHeightRatio) continue;
```

La puntuación combina varias señales: **aspecto** cercano a 5.0, **número de caracteres** cercano a 7, **consistencia de alturas**, **uniformidad de huecos** y **área**. El grupo ganador se expande con un margen proporcional al alto del carácter (para incluir el marco de la matrícula):

```csharp
var aspectScore = 1.0 / (1.0 + Math.Abs(lineAspect - TargetLineAspect)); // 1.0 = perfecto
var countScore  = 1.0 / (1.0 + Math.Abs(g.Count - 7) * 0.35);            // premia ~7 chars
var heightScore = (double)minCharH / maxCharH;                          // 1.0 = todos iguales
var score = aspectScore * countScore * heightScore * (0.7 + 0.3 * gapScore) * ...;
```

### El contrato: mismo endpoint, dos resoluciones

`Detect()` es solo un envoltorio de `Locate()` para no romper el contrato del endpoint `/api/ocr/detect`:

```csharp
// Asistencia en vivo: 640 px basta (menos bytes, ~35 ms)
var r = PlateDetector.Locate(bitmap, maxProcessWidth: 640);

// Captura estática: 960 px para un ROI más preciso antes de recortar y OCR
var r = PlateDetector.Locate(bitmap, PlateDetector.CaptureWidth); // 960
```

## Consejo pro

**Calibra los filtros con casos negativos, no solo positivos.** Cualquiera detecta "1234 BCD"; lo difícil es *no* detectar un faro. Prueba siempre con:

1. Una matrícula real → debe encontrarla.
2. Un rectángulo blanco **sin** caracteres → debe decir `found:false`.
3. Texto no-matrícula (un cartel largo, una palabra suelta) → debe descartarlo.

Y cuando dudes, **sube la resolución de proceso** (`StreamProcessWidth` / `CaptureProcessWidth`) antes de relajar los filtros: separar mejor los caracteres suele arreglar más que aceptar más ruido. Un "no la encuentro" honesto vale más que un falso positivo que manda al usuario a reencuadrar sin motivo.
