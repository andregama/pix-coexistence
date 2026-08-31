# Coexistence Analytics (standalone frontend)

A self-contained, dependency-free dashboard for the coexistence flows. It calls the
`spi-proxy-api` analytics endpoint (`GET /api/v1/analytics/summary`) — nothing is bundled
with the API, so you point it at whatever host is running the API.

## Run it

Either just open the file:

```bash
open frontend/analytics/index.html
```

…or serve the folder with any static server (avoids `file://` quirks):

```bash
python3 -m http.server 8080 --directory frontend/analytics
# then browse to http://localhost:8080
```

## Point it at an API

Set the **API base URL** field at the top of the page and click **Save & reload**. The value
is remembered per-browser (localStorage). You can also pass it once via query string:

```
http://localhost:8080/?api=http://localhost:5152
```

Default is `http://localhost:5152` (the API's Development HTTP endpoint — using HTTP avoids the
self-signed dev-cert prompt you'd hit against `https://localhost:7101`).

The API enables permissive CORS on the analytics endpoint for exactly this cross-origin use.
The endpoint is anonymous and intended for local/homologation use only.
