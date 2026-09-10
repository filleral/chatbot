# Chatbot de WhatsApp para inmobiliaria — guía completa

Bot conversacional por menús (botones/listas, no texto libre con IA) para WhatsApp Business, que:

- guía al usuario paso a paso: **arriendo o venta → zona → presupuesto → habitaciones**,
- consulta los inmuebles disponibles leyéndolos de tu página web **igual que hacía el flujo de n8n**
  (scraping del listado público + deducción de habitaciones desde el título),
- y al final, si el usuario quiere seguir, **le avisa a un asesor humano** con los datos capturados para que atienda personalmente.

Es una app web de **C#/.NET 8 (ASP.NET Core)** que se despliega en **Render** (plan Free, sin tarjeta)
con **PostgreSQL** en Neon (gratis), usando la **API oficial de WhatsApp Business (Meta Cloud API)**.

Reemplaza al workflow `WhatsApp Gemini Chatbot.json` de n8n: mismo **modelo de datos** de los
inmuebles (`titulo`, `url`, `habitaciones`, `capacidad_maxima`, `mascotas_maximas`) y misma forma de
**obtenerlos**, pero el "cerebro" ya no es un agente de IA sino una máquina de estados con menús.

> **¿Cómo lo pongo en producción y lo pruebo?** → [`DEPLOY.md`](DEPLOY.md) (paso a paso, costo ≈ $0).

---

## 1. Arquitectura

```
Usuario en WhatsApp
        │
        ▼
WhatsApp Business Platform (Meta Cloud API)
        │  webhook (HTTPS POST)
        ▼
App web .NET en Render  (endpoint /webhook)  ──────►  PostgreSQL / Neon (estado de la conversación + leads)
        │
        ├──► Listado público de tu página web (inmuebles en arriendo/venta)
        │
        └──► WhatsApp Cloud API (envía la respuesta al usuario)
                        │
                        └──► Mensaje de WhatsApp al asesor humano cuando el flujo termina
```

Piezas:

- **Meta Cloud API**: recibe los mensajes del cliente y los reenvía a tu webhook; también es el canal por el que el bot responde.
- **App web ASP.NET Core (.NET 8)**: expone `GET/POST /webhook`. Corre en un contenedor Docker en Render (plan Free). Se despliega solo con cada `git push`.
- **PostgreSQL (Neon, plan gratis)**: guarda en qué paso del menú va cada número de teléfono (para que el bot "recuerde" la conversación entre mensajes) y los leads capturados.
- **Tu página web**: el bot descarga el listado público de inmuebles y lo parsea (igual que el n8n). Ver sección 5.
- **Notificación al asesor**: el bot le manda un WhatsApp normal al número (o números) del asesor. Es gratis y no requiere integrar nada más — pero puedes cambiarlo por correo o Teams (ver sección 7).

## 2. Flujo conversacional

```
Usuario escribe algo
        │
        ▼
  Menú principal (lista interactiva)
  ┌─────────────┬─────────────┬────────────────────┐
  │ 🏠 Arriendo │ 🏡 Venta    │ 🗣️ Hablar con asesor │
  └──────┬──────┴──────┬──────┴──────────┬──────────┘
         │             │                 │
         ▼             ▼                 ▼
   ¿Zona/barrio?  ¿Zona/barrio?    Pide nombre
         │             │                 │
         ▼             ▼                 ▼
  ¿Presupuesto?  ¿Presupuesto?     Notifica al asesor
         │             │            (WhatsApp interno)
         ▼             ▼                 │
  ¿Habitaciones? ¿Habitaciones?          ▼
         │             │          "Te contactará
         ▼             ▼           en breve"
   Consulta el listado de tu página
         │
         ▼
  Muestra hasta 3 resultados
         │
         ▼
  "¿Quieres hablar con un asesor
   sobre estas opciones?"
      Sí ──► Pide nombre ──► Notifica al asesor
      No ──► "Quedo atento, escribe cuando quieras"
```

En cualquier punto, si el usuario escribe **"asesor"**, salta directo a pedir su nombre y notificar — no lo obligas a completar todo el árbol si no quiere. Y si escribe **"menú"** vuelve al menú principal.

Detalles del flujo:

- **Zona**: se ofrece con botones (Facatativá / Otra ciudad / Cualquiera) y también acepta texto libre (un barrio o municipio puntual).
- **Presupuesto**: los rangos de los botones cambian según sea arriendo (mensual) o venta (valor total).
- **Zona y presupuesto no filtran de verdad** el catálogo, porque el listado del sitio no expone precio ni barrio estructurados (igual que en n8n). Se capturan para pasárselos al asesor; el filtro real es por **tipo** (arriendo/venta) y **habitaciones**, y la zona filtra de forma suave contra el texto del título.
- Cada resultado muestra habitaciones, **capacidad máxima** (hab × 2) y **mascotas máximas** (mín(hab, 2)) — las mismas reglas del n8n.

Este árbol vive en un solo archivo (`Flow/FlowEngine.cs`), como una máquina de estados: cada "paso" sabe qué mensaje mandar y a qué paso siguiente pasar. Agregar una pregunta nueva (p. ej. "¿parqueadero?") es agregar un `case` más.

> Las preguntas de calificación del flujo viejo de n8n (ingresos ≥ 2× canon, documentos, no extranjeros en arriendo, etc.) **no** están en esta versión porque esta guía no las contempla. Si las quieres de vuelta, son pasos `case` adicionales antes de `MostrarResultadosAsync`.

## 3. ¿Es realmente gratis?

Sí, con matices que debes conocer para no llevarte una sorpresa:

**La API en sí (Meta Cloud API) no tiene costo de licencia.** No pagas por usarla, solo por volumen de mensajes según reglas de Meta:

- **Mensajes que responden a una conversación que el usuario inició** (que es prácticamente todo tu flujo: el cliente escribe primero) se facturan como **"service messages"**. Hoy no tienen costo aparte para la mayoría de cuentas nuevas, pero **Meta anunció que a partir del 1 de octubre de 2026 este tipo de mensaje también empieza a cobrarse por unidad**, según y como esté tu cuenta. Como estamos a pocas semanas de esa fecha, revisa el panel de **WhatsApp Manager → Facturación** de tu cuenta para confirmar tu tarifa real antes de lanzar el bot a producción.
- **Mensajes que el negocio inicia sin que el usuario haya escrito antes** (plantillas de marketing, recordatorios, etc.) siempre se han cobrado por mensaje según categoría (marketing/utility/authentication) y país del destinatario — esto no aplica a tu caso porque el bot solo responde, nunca escribe primero.
- Colombia no tiene tarifa fija informada por Meta que haya podido confirmar; se factura por Meta directamente o por un BSP (proveedor autorizado), y varía. Con el volumen típico de una inmobiliaria (decenas o pocos cientos de conversaciones al mes) el costo, si aplica, suele ser de unos pocos dólares al mes — no una barrera, pero no lo prometas como "100% gratis para siempre" a tu jefe/cliente.

**La infraestructura sí puede ser gratis de verdad:**

- **Render (web service, plan Free)**: $0, sin tarjeta. Se apaga tras 15 min sin tráfico y tarda ~1 min en despertar; un "ping" cada 10 min lo mantiene encendido 24/7 (ver `DEPLOY.md` §9). Alternativas: Fly.io, Koyeb, una VM gratis de Oracle Cloud (siempre encendida, sin cold-start).
- **PostgreSQL en Neon (plan Free)**: 0,5 GB, siempre accesible, $0. Alternativa equivalente: CockroachDB Serverless (gratis, 10 GB). Supabase también tiene plan gratis pero **pausa** el proyecto tras 1 semana sin uso, así que evítalo para un bot que puede estar días callado.
- **Logs**: los da Render en su panel, gratis.

## 4. Configuración en Meta for Developers

Como ya tienes la Cloud API configurada, probablemente ya hiciste los pasos 1-3. Los dejo igual para que verifiques que no falte nada:

1. En **Meta for Developers → tu App → WhatsApp → Configuración de API**, confirma que tienes:
   - `Phone Number ID` (lo vas a poner en `WhatsApp:PhoneNumberId`).
   - Un **token de acceso permanente** (no el temporal de 24h que da la consola de pruebas) — se genera creando un **System User** en Meta Business Suite con permiso `whatsapp_business_messaging`.
2. En **WhatsApp → Configuración → Webhooks**, registra:
   - **Callback URL**: la URL pública de tu servicio en Render, `https://<tu-servicio>.onrender.com/webhook`.
   - **Verify Token**: cualquier cadena secreta que tú inventes — debe ser exactamente igual a la variable `WhatsApp__VerifyToken` en Render. Meta la usa una sola vez para confirmar que el webhook es tuyo.
   - Suscríbete al campo `messages`.
3. Verifica el número de WhatsApp Business que vas a usar (si aún no tiene el ✅ verde en el panel).

## 5. De dónde salen los inmuebles (igual que el n8n)

`Services/PropertyCatalogService.cs` hace exactamente lo que hacían los nodos *Fetch Inmuebles Página 1/2* + *Extraer Inmuebles* del workflow de n8n:

1. Descarga el HTML de `https://bienesraiceswhite.co/inmuebles/` y, si hay, `/inmuebles/page/2/`, `/page/3/`… (hasta `PropertyScraper:MaxPages`, y para en cuanto una página no trae fichas nuevas).
2. Por cada ficha extrae **título** y **URL** con la misma expresión regular:
   `<h2[^>]*>\s*<a[^>]*href="([^"]+)"[^>]*>([\s\S]+?)</a>\s*</h2>`
3. Deduce las **habitaciones** desde el título con las mismas reglas: número explícito (`3 habitaciones`) → dúplex = 2 → apartamento/casa = 2 → 1 por defecto.
4. `capacidad_maxima = habitaciones × 2` y `mascotas_maximas = mín(habitaciones, 2)`.
5. Extra respecto al n8n: deduce el **tipo** (`arriendo` / `venta`) a partir de palabras del título, que es lo que necesita el menú para no mezclar.

El resultado se guarda en **caché en memoria** durante `PropertyScraper:CacheMinutes` (10 por defecto) para no descargar el sitio en cada mensaje.

**Config** (todo opcional, trae valores por defecto):

| Clave | Default | Para qué |
|---|---|---|
| `PropertyScraper:ListingUrl` | `https://bienesraiceswhite.co/inmuebles/` | Página del listado a scrapear |
| `PropertyScraper:MaxPages` | `3` | Cuántas páginas del listado recorrer |
| `PropertyScraper:CacheMinutes` | `10` | Minutos de caché del catálogo |

**Si algún día cambias de página o de CMS**: si el nuevo sitio marca las fichas con otra etiqueta (no `<h2><a href>`), ajusta `FichaRegex` en ese archivo. Si consigues una API JSON de verdad, reemplaza el cuerpo de `DescargarYParsearAsync()` por la llamada al endpoint — la interfaz `IPropertyCatalogService` y el resto del bot no cambian.

**Modo sin internet / cambios de sitio**: si la descarga falla o no devuelve fichas, el servicio reutiliza el último catálogo bueno que tenga en caché; si nunca pudo bajar nada, `BuscarAsync` devuelve vacío y el bot ofrece pasar el cliente a un asesor.

## 6. Cómo correr y desplegar

El paso a paso completo (Neon → GitHub → Render → webhook en Meta → probar) está en
**[`DEPLOY.md`](DEPLOY.md)**. Resumen:

1. **Base de datos:** crea un proyecto en https://neon.tech, corre [`sql/schema.sql`](sql/schema.sql) en su SQL Editor y copia la cadena de conexión (formato `.NET`).
2. **Sube el repo a GitHub:**
   ```bash
   git init && git branch -M main && git add -A
   git commit -m "primer commit"
   git remote add origin https://github.com/filleral/chatbot.git
   git push -u origin main
   ```
3. **Render:** https://dashboard.render.com/blueprints → *New Blueprint Instance* → elige el repo → *Apply*. (Render lee `render.yaml`.)
4. **Variables** en Render → servicio → *Environment* (ver [`.env.example`](.env.example)):
   `WhatsApp__VerifyToken`, `WhatsApp__AccessToken`, `WhatsApp__PhoneNumberId`,
   `WhatsApp__AdvisorPhoneNumber`, `PostgresConnectionString`.
5. **Meta:** Callback URL = `https://<tu-servicio>.onrender.com/webhook`, Verify Token = el mismo
   texto de `WhatsApp__VerifyToken`, y suscríbete a `messages`.

**Correr en local:**

```bash
cd src/WhatsappBot.Functions
cp ../../.env.example .env      # y edítalo con tus valores
# exporta las variables del .env, o usa dotnet user-secrets, luego:
dotnet run
```

Queda escuchando en `http://localhost:8080/webhook`. Para que Meta le llegue durante pruebas,
expón el puerto con un túnel (Cloudflare Tunnel, VS Code Port Forwarding, ngrok…).

## 7. Notificación al asesor: alternativas

El starter usa la opción más simple: el propio bot le manda un WhatsApp a cada número de `WhatsApp:AdvisorPhoneNumber` (uno o varios, separados por coma) con los datos del lead y los inmuebles que se le mostraron. No necesitas contratar nada extra. El lead también queda en la tabla `leads` de PostgreSQL.

Si prefieres otro canal, en `Services/NotificationService.cs` puedes agregar, junto al WhatsApp o en vez de él:

- **Correo**: SendGrid o Brevo (ambos con capa gratuita) — útil si el equipo comercial vive en el correo.
- **Microsoft Teams / Slack / Discord**: un webhook entrante del canal — un simple `HttpClient.PostAsync` con el mensaje en JSON.
- **Guardar el lead y que un dashboard lo muestre**: ya se guarda en la tabla `leads` de PostgreSQL; se podría montar una vista sencilla más adelante.

## 8. Qué contiene el proyecto

```
whatsapp-bot-inmobiliaria/
├── DEPLOY.md                                   Guía de despliegue (Neon + GitHub + Render + Meta) paso a paso
├── Dockerfile                                  Imagen de la app (la usa Render)
├── render.yaml                                 Blueprint de Render (crea el servicio solo)
├── .env.example                                Lista de variables de entorno que necesita el bot
├── WhatsApp Gemini Chatbot.json                Workflow viejo de n8n (referencia)
├── sql/schema.sql                              Tablas PostgreSQL: conversation_state, leads, message_log
└── src/WhatsappBot.Functions/
    ├── Program.cs                              Arranque de la app web + endpoints GET/POST /webhook
    ├── appsettings.json                        Config de logging (sin secretos)
    ├── Flow/FlowEngine.cs                      Máquina de estados del menú (el "cerebro" del bot)
    ├── Models/                                 DTOs del payload de Meta + Property + ConversationState
    └── Services/
        ├── WhatsAppService.cs                  Enviar texto / botones / listas vía Graph API
        ├── PropertyCatalogService.cs           Scrapea el listado del sitio (misma lógica que el n8n)
        ├── PostgresConversationStateService.cs Persistencia del estado de la conversación en PostgreSQL
        ├── PostgresLeadRepository.cs           Guarda los leads capturados en la tabla leads
        └── NotificationService.cs              Aviso al asesor humano (WhatsApp, uno o varios números)
```

> La carpeta se llama `src/WhatsappBot.Functions/` por herencia de una versión anterior sobre Azure
> Functions; hoy es una app web normal de ASP.NET Core (el ensamblado se llama `WhatsappBot`).

## 9. Siguientes pasos sugeridos

1. Sigue [`DEPLOY.md`](DEPLOY.md) para dejarlo corriendo 24/7 y probarlo con tu número.
2. Confirma en tu panel de Meta la tarifa real de "service messages" antes del 1 de octubre de 2026.
3. Cuando estés conforme, deja en `WhatsApp:AdvisorPhoneNumber` los números reales de los asesores.
4. Mejoras naturales más adelante: enriquecer cada inmueble leyendo también su ficha (precio, zona, área) para poder filtrar por presupuesto de verdad; mandar la foto del inmueble (WhatsApp soporta mensajes tipo `image` con `link`); o un mini panel web para que el asesor vea los leads sin depender solo del WhatsApp.

---

### Fuentes consultadas para la sección de costos

- [WhatsApp API Pricing 2026: Channels, Regions & Templates — Wati](https://www.wati.io/en/blog/whatsapp-api-pricing-guide/)
- [Service messages — Meta for Developers](https://developers.facebook.com/documentation/business-messaging/whatsapp/messages/send-messages)
- [Interactive List — WhatsApp Cloud API — Meta for Developers](https://developers.facebook.com/docs/whatsapp/cloud-api/messages/interactive-list-messages/)
- [Interactive reply buttons messages — Meta for Developers](https://developers.facebook.com/documentation/business-messaging/whatsapp/messages/interactive-reply-buttons-messages)
