# PilotoOcrApi — Escáner de matrículas en vivo (ANPR)

Piloto de **reconocimiento automático de matrículas españolas** (ANPR/LPR) formado por una API .NET 8 y un cliente web de una sola página pensado para el móvil.

El backend recibe una imagen y devuelve el texto reconocido, su confianza y si el formato es una matrícula válida; además ofrece detección de la placa para encuadrarla automáticamente. El frontend abre la cámara, muestra un **HUD verde fosforito** para encuadrar, escanea en streaming y propone una lectura editable al operario.

Todo el backend vive en `Program.cs` y todo el frontend en `wwwroot/index.html`.

## Características

- **Cámara en vivo** con HUD guía, zoom (1 dedo fuera del recuadro), movimiento del recuadro (1 dedo dentro) y redimensionado con dos dedos.
- **Escaneo en streaming**: envía el recorte periódicamente, muestra texto y confianza en vivo y **auto-confirma** cuando dos lecturas estables son válidas.
- **Foto estática**: sube o captura una imagen y se refina con un *ensemble* de 5 variantes preprocesadas (original, invertida, contraste alto/bajo, escalada ×2).
- **Validación de formato** de matrícula: actual `0000BBB`, especial `E0000AA` e histórica provincial.
- **Auto-encuadre 🎯**: imanta el recuadro a la placa detectada (blob brillante) o al texto leído (bbox de Tesseract).
- **Linterna 🔦** en Android/Chrome y **foto con flash 📸** (cámara nativa) como alternativa en dispositivos que no exponen `torch`.
- **Umbral de aceptación** ajustable desde ⚙ y persistido en el navegador.

## Requisitos

- **.NET 8 SDK**
- **Tesseract CLI 5.x** con el idioma español:

```bash
sudo apt install tesseract-ocr tesseract-ocr-spa
tesseract --version        # 5.x
tesseract --list-langs     # debe incluir "spa"
```

- Opcionales para probar: `curl`, ImageMagick (`convert`) y `node` (para validar el JS inline).

> No se usa el paquete NuGet `Tesseract`: solo trae binarios de Windows y en Linux falla con `DllNotFoundException ... libleptonica-1.82.0.so`. Se invoca el **CLI `tesseract` vía `Process`**.

## Arranque

```bash
dotnet build          # debe salir con 0 warnings
dotnet run
```

Queda escuchando en:

- **HTTP**: http://localhost:5000
- **HTTPS**: https://localhost:5443 (y `https://<IP-de-LAN>:5443`)

> Los puertos reales se fijan en `Program.cs` (`HttpPort=5000`, `HttpsPort=5443`). `launchSettings.json` no los refleja.

### Certificado HTTPS

El HTTPS es necesario para la **cámara en vivo** (los navegadores exigen contexto seguro, `getUserMedia`). En `certs/` están `dev.crt` y `dev.key`, un certificado autofirmado con SAN para `localhost`, `127.0.0.1` y la IP `192.168.1.40`. Si tu IP de LAN cambia, regéneralo:

```bash
mkdir -p certs
openssl req -x509 -newkey rsa:2048 -nodes \
  -keyout certs/dev.key -out certs/dev.crt -days 825 \
  -subj "/CN=PilotoOcrApi Dev" \
  -addext "subjectAltName=IP:TU_IP,IP:127.0.0.1,DNS:localhost"
```

Sin los ficheros de `certs/`, la app arranca solo en HTTP y la cámara en vivo no estará disponible.

## Guía de uso

Abre http://localhost:5000 y pulsa **Activar cámara**.

- **Cámara en vivo**
  - Encuadra la matrícula dentro del recuadro verde.
  - El readout superior muestra el texto y el % de confianza en tiempo real (`✓` = formato válido).
  - Al conseguir dos lecturas iguales válidas por encima del umbral, la imagen se **congela** y aparece la lectura **editable** con **Aceptar / Cancelar**.
  - **Doble toque** en cualquier punto fuerza una captura manual.
- **Foto estática** (🖼) o **foto con flash** (📸)
  - Se carga la imagen y se puede encuadrar/ajustar como en el modo en vivo.
  - **Doble toque** lanza el *ensemble* de variantes y propone la mejor lectura.
- **Ajustes** (⚙): umbral de aceptación y activación del **auto-encuadre**.
- **Atajos de teclado** (escritorio): `Enter` acepta, `Esc` cancela.

## API

### `POST /api/ocr/scan`

Multipart con campo `file` (máx. 10 MB, lado máx. 8000 px). Lee una línea de texto (Tesseract `--psm 7`, idioma `spa`).

```json
{
  "text": "1234 BCD",
  "confidence": 0.923,
  "normalized": "1234BCD",
  "valid": true,
  "bbox": { "x": 196, "y": 50, "width": 255, "height": 44 }
}
```

### `POST /api/ocr/detect`

Multipart con campo `file`. Localiza la región con aspecto de matrícula (región clara rectangular) para el auto-encuadre. Las coordenadas van normalizadas 0..1.

```json
{ "found": true, "rect": { "x": 0.25, "y": 0.78, "w": 0.55, "h": 0.12 }, "candidates": 1 }
```

## Cómo probar la aplicación

### 1. Compilar sin warnings

```bash
dotnet build
```

### 2. Probar el OCR por consola

Genera una imagen con texto y envíala a la API:

```bash
convert -size 640x140 xc:white -gravity center -pointsize 62 -fill black \
  -annotate 0 "1234 BCD" /tmp/plate.png

curl -s -X POST -F "file=@/tmp/plate.png" http://localhost:5000/api/ocr/scan
# {"text":"1234 BCD","confidence":0.923,"normalized":"1234BCD","valid":true,"bbox":{...}}
```

Detección de la zona de la placa (fondo oscuro con placa blanca):

```bash
convert -size 800x600 xc:'#202028' -fill white \
  -draw "roundrectangle 200,470 640,540 6,6" /tmp/frame.png

curl -s -X POST -F "file=@/tmp/frame.png" http://localhost:5000/api/ocr/detect
# {"found":true,"rect":{"x":0.25,"y":0.7833,"w":0.5521,"h":0.1194},"candidates":1}
```

Un texto que no sea matrícula devuelve `"valid": false` (útil para comprobar la validación de formato).

### 3. Validar el JS inline del front (sin build step)

`wwwroot/index.html` se sirve estático: editarlo se ve al refrescar. Para comprobar que el JavaScript no tiene errores de sintaxis:

```bash
python3 - <<'EOF'
import re
html = open('wwwroot/index.html').read()
open('/tmp/app.js', 'w').write(re.search(r'<script>(.*?)</script>', html, re.S).group(1))
EOF
node --check /tmp/app.js
```

### 4. Probar en el móvil

La cámara en vivo requiere **HTTPS**. Dos caminos:

- **Misma red WiFi (HTTPS con certificado propio)**: abre `https://<IP-de-LAN>:5443` y acepta el aviso del certificado autofirmado (una vez por dispositivo).
- **Túnel público (sin tocar el router ni el firewall)**: con `cloudflared` obtienes una URL `https://` temporal válida desde cualquier red:

```bash
cloudflared tunnel --url http://localhost:5000 --no-autoupdate
# ... Visit it at: https://xxxx.trycloudflare.com
```

> La URL `*.trycloudflare.com` cambia en cada arranque del túnel.

### 5. Recrear el binario de Tesseract si no está instalado

No es necesario en esta máquina, pero para reproducir el entorno en otro sitio: ver la sección [Requisitos](#requisitos).

## Lanzar en segundo plano

Al lanzar desde una sesión de agente, el patrón que sobrevive al cierre del shell es la subshell con `setsid`:

```bash
(setsid bash -c 'dotnet run > /tmp/opencode/api.log 2>&1' >/dev/null 2>&1 &)
(setsid bash -c '/tmp/opencode/cloudflared tunnel --url http://localhost:5000 --no-autoupdate > /tmp/opencode/cf.log 2>&1' >/dev/null 2>&1 &)
```

Para detenerlo:

```bash
pkill -f PilotoOcrApi
pkill -f cloudflared
```

## Notas y limitaciones

- **Sin tests, lint ni CI**: la verificación es manual (build, `curl` e `node --check`), como se describe arriba.
- La **validación histórica es permisiva** por diseño: puede aceptar falsos positivos (p. ej. `AB123CD` como Albacete). Es una decisión aceptada, no un bug.
- El *whitelist* de caracteres de Tesseract está **desactivado por defecto** (no mejora con el motor LSTM). Se puede experimentar sin recompilar con las variables de entorno `OCR_WHITELIST` y `OCR_OEM`.
- En **iOS Safari** (y algunos navegadores) no existe `torch`: la linterna no está disponible y se ofrece la foto con flash nativo (📸).
- Para el detalle de decisiones e implementación, consulta `AGENTS.md`.
