# AGENTS.md — PilotoOcrApi

API OCR de matrículas españolas (.NET 8 Minimal API) + cliente web de una sola página. Todo el backend está en `Program.cs`; todo el front en `wwwroot/index.html`.

## Comandos

```bash
dotnet build                 # debe salir con 0 warnings
dotnet run                   # lee certs/ si existe; HTTP :5000 y HTTPS :5443
```

- **No hay tests, lint, CI ni git** (el directorio no es repo). La verificación es manual y así se espera:
  1. `dotnet build` sin warnings.
  2. `curl` con imágenes generadas por ImageMagick (`convert`).
  3. Para el front: extraer el JS inline y validarlo con `node --check`.

```bash
# Verificación típica de OCR
convert -size 640x140 xc:white -gravity center -pointsize 62 -fill black -annotate 0 "1234 BCD" /tmp/p.png
curl -s -X POST -F "file=@/tmp/p.png" http://localhost:5000/api/ocr/scan

# Validar el JS inline del front (sin build step)
python3 - <<'EOF'
import re; html=open('wwwroot/index.html').read()
open('/tmp/app.js','w').write(re.search(r'<script>(.*?)</script>', html, re.S).group(1))
EOF
node --check /tmp/app.js
```

## Restricciones duras

- **No reinstales el paquete NuGet `Tesseract`**: solo trae binarios de Windows y en Linux falla con `DllNotFoundException ... libleptonica-1.82.0.so`. Se usa el **CLI `tesseract` vía `Process`**. Requiere `sudo apt install tesseract-ocr tesseract-ocr-spa`.
- **`launchSettings.json` miente**: los puertos reales se fijan en `Program.cs` con `ConfigureKestrel` + `ListenAnyIP` (`HttpPort=5000`, `HttpsPort=5443`). No añadas URL a `app.Run(...)`.
- **HTTPS :5443 es obligatorio para la cámara en vivo** (`getUserMedia` exige contexto seguro). Si falta `certs/dev.crt`/`certs/dev.key`, arranca solo HTTP y no hay cámara. El cert tiene SAN para `192.168.1.40`, `localhost`; si cambia la IP de LAN hay que regenerarlo (`openssl` con `-addext "subjectAltName=..."`).
- **`.DisableAntiforgery()` en cada endpoint con `IFormFile`** (obligatorio en .NET 8 Minimal API; sin él el runtime responde con error de middleware antiforgery).
- **No hay build step en el front**: `wwwroot` se sirve estático; editar `index.html` se ve al refrescar. Solo `Program.cs`/`.csproj` requieren `dotnet build`.

## Backend (`Program.cs`)

- Endpoints:
  - `POST /api/ocr/scan` → `{text, confidence, normalized, valid, repaired, bbox}` (OCR del recorte que envía el cliente; circuitos clásicos).
  - `POST /api/ocr/detect` → `{found, rect(normalizado 0..1), candidates}` (asistencia de localización del ROI, ~3 fps).
  - `POST /api/ocr/capture` → `{text, confidence, normalized, valid, repaired, roi(normalizado en la imagen original)|null, mode:"roi"|"full", chars}` (captura estática: localiza el ROI en la imagen COMPLETA y preprocesa+OCRa **solo esa región**; si no la encuentra, `mode:"full"`).
  Todos aceptan multipart `file` (máx 10 MB, 8000 px de lado) y `?spainOnly=`.
- Clases auxiliares: `TesseractCli` (proceso, `--psm 7` + salida `tsv`), `PlateValidator`, `PlateDetector` **v2** y helpers `CropForOcr`/`DownscaleForOcr`.
- **`PlateValidator` y el toggle `?spainOnly=`**:
  - `spainOnly=true` (check 🇪🇸) → **SOLO** formato actual `0000BBB` (4 dígitos + 3 letras).
  - `spainOnly=false` (general) → actual **o** especial `E0000AA` **o** histórica provincial **o** plausible internacional (≥5 alfanuméricos, matrículas extranjeras). Los formatos españoles antiguos quedan aquí; la validación histórica es **intencionadamente permisiva** (acepta falsos positivos tipo `AB123CD` como Albacete) — decisión aceptada, no un bug.
- **`PlateValidator.RepairCurrent()`** (solo con `spainOnly=true`): repara ruido de los extremos del recorte (banda azul UE pegada a la izquierda). Normaliza + filtra a alfanuméricos y descompone en `[letras?][dígitos][letras?]`; si no es ya `0000BBB`: **>4 dígitos → conserva los 4 de la derecha** (descarta los de la izquierda), **letras iniciales sueltas → se descartan**, **>3 letras finales → conserva las 3 de la izquierda** (descarta las de la derecha). Solo aplica si el resultado encaja en `0000BBB`. `scan`/`capture` devuelven `normalized` ya reparado y el flag `repaired`. Verificado: `E71234 BCD`→`1234BCD`, `51234 BCD`→`1234BCD`, `1234 ABCD`→`1234ABC`.
- **`PlateDetector` v2** — algoritmo prioritario para ubicar el ROI: Otsu + componentes conexos de los **caracteres** (píxeles oscuros) + agrupación en línea de texto. Filtros agresivos por carácter (aspecto 0.10–1.40, altura 1.5–25% del frame, fill ≥0.25, área ≥6 px, sin tocar bordes) y filtros de grupo por **proporciones de matrícula** (4–9 caracteres, aspecto de línea 2.6–8.0 objetivo 5.0, alturas consistentes ≤2.4, espaciado uniforme). Puntuación: aspecto + nº≈7 + consistencia + huecos + área. Anchos: `StreamProcessWidth=640` (asistencia), `CaptureProcessWidth=960` (captura). `Detect()` envuelve `Locate()` para mantener el contrato del endpoint. Verificado: descarta rectángulos blancos sin caracteres; detecta la matrícula en ~35 ms.
- **Whitelist DESACTIVADO por defecto** — se comprobó que con LSTM no mejora. Variables de entorno para experimentar sin recompilar: `OCR_WHITELIST` (`on`/`default` = alfabético completo; `off`/vacío = sin whitelist; otro valor = whitelist literal) y `OCR_OEM` (`1`=LSTM, `0`=legacy).
  - El motor legacy **necesita un `spa.traineddata` con componentes legacy** (el de Ubuntu es LSTM-only y falla con `--oem 0`); además `TESSDATA_PREFIX` a un directorio propio debe incluir la subcarpeta `configs/` o el `tsv` falla con `read_params_file: Can't open tsv`.
- Orden de argumentos en `TesseractCli`: `--oem` y `-c tessedit_char_whitelist=...` **antes** de `tsv` (la config de salida va al final).

## Front (`wwwroot/index.html`)

- Un único HTML con CSS y un IIFE JS inline. Sin frameworks ni dependencias.
- **Circuito único de captura**: recuadro verde sobre cámara en vivo o foto cargada; **botón de disparo `#btnShutter`** (abajo-centro) reconoce y captura. El **tap** ya NO captura: es **tap-on-focus** (círculo `#focusRing` + `applyFocus()` con `advanced:[{focusMode:"continuous"}]`). 1 dedo dentro del recuadro = mover, fuera = zoom, 2 dedos = redimensionar; `⌂` (`#btnHome`) sale al menú (pantalla de inicio, para la cámara). El antiguo circuito de "captura estática" (toggle 📸, ROI server-side `/api/ocr/capture`, asistencia ~3 fps) se **retiró** del cliente.
- **Recorte en cliente antes de enviar** (en `captureHudBlob`, canvas global reutilizable `workCanvas`): escala el recorte del recuadro para que el carácter mida ~`OCR_CHAR_TARGET=45` px de alto (`OCR_CHAR_RATIO=0.5`, factor acotado 0.3–3.0), limita el ancho a `OCR_MAX_WIDTH=1024`, y exporta **JPEG calidad `JPEG_QUALITY=0.85`**. Se envía a `/api/ocr/scan`.
- **Disparo**: `SHUTTER_DELAY_MS=200` de retraso tras el toque (estabilizar la mano) + animación de escaneo `#scanFx` sobre el recuadro; la linterna se enciende durante la captura y se restaura.
- **Nivel (DeviceOrientationEvent)**: si la inclinación supera `LEVEL_MAX_DEG=15`, el marco se pinta en rojo y aparece `#levelWarn` ("Mantenga la cámara nivelada"). `startLevelWatch/stopLevelWatch`; pide permiso en iOS 13+.
- **Foto cargada**: ensemble de 5 variantes filtradas (`VARIANTS`, sin `scale`; el escalado de densidad lo hace `captureHudBlob`) con `REFINE_BUDGET_MS=5000`; el resultado se agrega por consenso (`scoreResults`, usa `normalized`).
- El resultado depende del toggle 🇪🇸:
  - **`spainOnly=true`** → **celdas** (`#cellRow`): exactamente **4 celdas de dígitos (fondo blanco) + 3 de letras (amarillo claro `#fef08a`)** en dos filas apiladas; la lectura se reparte dígitos→arriba / letras→abajo (máx. 4+3, rellenas con celdas vacías si falta texto). Doble toque vacía, teclear corrige con auto-avance, Aceptar compone mayúsculas sin espacios. Toast si `repaired=true`.
  - **`spainOnly=false`** → **todo el texto capturado en una única casilla** `#ocrText` (contenteditable, seleccionado al abrir); Aceptar copia su contenido.
- HUD `#hudClock` (siempre visible con cámara/foto): hora `HH:MM:SS.mmm` + fps medidos por el cliente (ventana `FPS_WINDOW=8` sobre las respuestas del bucle activo). `resetFps()` al parar.
- Constantes a ajustar suelen estar agrupadas al inicio del `<script>`: `STREAM_INTERVAL (800)`, `DETECT_INTERVAL (600)`, `FPS_WINDOW (8)`, `CLOCK_TICK (47)`, `MIN_ZOOM/MAX_ZOOM`, `ASPECT (520/110)`, `REFINE_BUDGET_MS (5000)`, `OCR_CHAR_TARGET (45)`, `OCR_MAX_WIDTH (1024)`, `JPEG_QUALITY (0.85)`, `SHUTTER_DELAY_MS (200)`, `LEVEL_MAX_DEG (15)`.
- El umbral de aceptación persiste en `localStorage` con clave `ocrThreshold`; el modo España en `ocrSpainOnly`.
- Auto-encuadre (toggle 🎯): `bbox` de Tesseract (streaming) y `/api/ocr/detect` (detector v2); requiere 2 detecciones estables con IoU>0.6 y se pausa 4 s tras un ajuste manual.
- Linterna (`torch`) solo funciona en dispositivos que la exponen (Chrome/Android); **iOS Safari no**, por eso hay fallback 📸 con la cámara nativa (`capture="environment"`).
- Verificación e2e sin cámara real: Chrome headless (`chrome-headless-shell`) con `--use-file-for-fake-video-capture=<archivo.y4m>` + `--use-fake-ui-for-media-stream` vía CDP; `DOM.setFileInputFiles` sobre `#photoInput` para el circuito de foto. Instrumentar `HTMLCanvasElement.prototype.toBlob` para comprobar dimensiones/calidad del recorte enviado.

## Operación (esta máquina)

- Al lanzar en segundo plano desde una sesión de agente, el patrón que sobrevive es:
  `(setsid bash -c '<cmd> >> /tmp/opencode/x.log 2>&1' >/dev/null 2>&1 &)` — `nohup ... & disown` se muere al expirar el comando.
- Para probar en móvil: túnel `cloudflared` (`/tmp/opencode/cloudflared tunnel --url http://localhost:5000 --no-autoupdate`). La URL `*.trycloudflare.com` cambia en cada arranque.
- `certs/dev.key` es una clave privada: no la commitees ni la expongas aunque no haya `.gitignore`.
