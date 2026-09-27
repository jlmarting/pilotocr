#!/usr/bin/env bash
set -euo pipefail

# --- run.sh: arranca PilotoOcrApi + túnel cloudflared ---

APP_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
LOG_DIR="/tmp/opencode"
mkdir -p "$LOG_DIR"

CLOUDFLARED="${LOG_DIR}/cloudflared"
APP_LOG="${LOG_DIR}/app.log"
TUNNEL_LOG="${LOG_DIR}/tunnel.log"
HTTP_PORT=5000

# 1. Verificar dotnet
if ! command -v dotnet &> /dev/null; then
    echo "Error: dotnet no está instalado o no está en PATH."
    exit 1
fi

# 2. Descargar cloudflared si no existe
if [[ ! -x "$CLOUDFLARED" ]]; then
    echo "cloudflared no encontrado. Descargando..."
    ARCH=$(uname -m)
    if [[ "$ARCH" == "x86_64" ]]; then
        URL="https://github.com/cloudflare/cloudflared/releases/latest/download/cloudflared-linux-amd64"
    elif [[ "$ARCH" == "aarch64" ]]; then
        URL="https://github.com/cloudflare/cloudflared/releases/latest/download/cloudflared-linux-arm64"
    else
        echo "Arquitectura $ARCH no soportada para descarga automática de cloudflared."
        exit 1
    fi
    curl -fsSL -o "$CLOUDFLARED" "$URL"
    chmod +x "$CLOUDFLARED"
    echo "cloudflared instalado en $CLOUDFLARED"
fi

# 3. Compilar
pushd "$APP_DIR" > /dev/null
    dotnet build -c Release --nologo -v q > "$LOG_DIR/build.log" 2>&1 || {
        echo "Error de compilación. Revisa $LOG_DIR/build.log"
        exit 1
    }
popd > /dev/null

# 4. Arrancar la app en segundo plano con setsid (patrón robusto de supervivencia)
echo "Arrancando PilotoOcrApi en http://localhost:${HTTP_PORT} (log: $APP_LOG)..."
(setsid bash -c "cd '$APP_DIR' && dotnet run --no-build --configuration Release > '$APP_LOG' 2>&1" >/dev/null 2>&1 &)

# Esperar a que el puerto responda
echo -n "Esperando a que la API esté lista"
for i in {1..30}; do
    if curl -fs http://localhost:${HTTP_PORT}/ > /dev/null 2>&1; then
        echo " OK"
        break
    fi
    echo -n "."
    sleep 1
done

# Verificar
if ! curl -fs http://localhost:${HTTP_PORT}/ > /dev/null 2>&1; then
    echo ""
    echo "Error: la API no arrancó en el puerto ${HTTP_PORT}. Revisa $APP_LOG"
    exit 1
fi

# 5. Arrancar el túnel cloudflared en segundo plano
echo "Arrancando túnel cloudflared..."
(setsid "$CLOUDFLARED" tunnel --url "http://localhost:${HTTP_PORT}" --no-autoupdate > "$TUNNEL_LOG" 2>&1 &)

# Extraer la URL del log (trycloudflare.com)
echo -n "Esperando URL del túnel"
TUNNEL_URL=""
for i in {1..30}; do
    TUNNEL_URL=$(grep -oP 'https://[a-zA-Z0-9-]+\.trycloudflare\.com' "$TUNNEL_LOG" | head -1 || true)
    if [[ -n "$TUNNEL_URL" ]]; then
        echo " OK"
        break
    fi
    echo -n "."
    sleep 1
done

if [[ -z "$TUNNEL_URL" ]]; then
    echo ""
    echo "Error: no se pudo obtener la URL del túnel. Revisa $TUNNEL_LOG"
    exit 1
fi

# 6. Mostrar información
echo ""
echo "========================================"
echo "  PilotoOcrApi en ejecución"
echo "  Local:  http://localhost:${HTTP_PORT}"
echo "  Túnel:  ${TUNNEL_URL}"
echo "========================================"
echo ""
echo "Logs:"
echo "  App:    $APP_LOG"
echo "  Túnel:  $TUNNEL_LOG"
echo ""
echo "Para detener:"
echo "  kill \$(lsof -t -i:${HTTP_PORT})   # app"
echo "  pkill -f cloudflared               # túnel"
echo ""

# Dejar el script en foreground para que Ctrl+C pueda detener fácilmente si se desea
# (aunque los procesos sean setsid, el usuario puede Ctrl+C este script de control)
tail -f "$APP_LOG" "$TUNNEL_LOG"
