# Chatbot de WhatsApp para inmobiliaria — guía completa

Bot conversacional por menús (botones/listas, no texto libre con IA) para WhatsApp Business, que:

- guía al usuario por un **menú de 8 opciones** (arriendo, administración, compra, venta, asesoría de venta, notarial, jurídica, otro) — los mismos flujos que el bot de n8n,
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

Menú principal (lista interactiva con los 8 flujos de n8n). El usuario elige tocando la lista
o escribiendo el número:

```
1️⃣ Tomar en arriendo   →  personas → mascotas → documentos → ingresos 2× → inmuebles → asesor
2️⃣ Administrar inmueble →  nº inmuebles → ocupación → ciudad → asesor
3️⃣ Comprar inmueble     →  tipo → zona → presupuesto → uso → inmuebles → asesor
4️⃣ Vender inmueble      →  tipo → zona → valor esperado → asesor
5️⃣ Asesoría para vender →  tipo → precio definido / avalúo → asesor
6️⃣ Asesoría notarial    →  ¿qué trámite? → asesor
7️⃣ Asesoría jurídica    →  ¿tu situación? → asesor
8️⃣ Otro                 →  cuéntanos → asesor
```

Todos los flujos terminan **avisando a un asesor** por WhatsApp con el resumen de las respuestas,
y guardando el lead en la tabla `leads`. Las opciones 1 y 3 además muestran hasta 3 inmuebles del
sitio antes de ofrecer el asesor.

- Si el usuario escribe **"menú"** en cualquier punto, vuelve al inicio.
- El **nombre** sale del perfil de WhatsApp (como en n8n); solo se pide si el perfil no lo trae.
- **Arriendo (1):** filtra los inmuebles por capacidad — `personas ≤ habitaciones × 2` y
  `mascotas ≤ min(habitaciones, 2)` (reglas de n8n).
- **Compra (3):** filtra inmuebles en venta por tipo (apartamento/casa/local/lote, buscando la
  palabra en el título) y zona (coincidencia suave). El presupuesto se captura para el asesor
  (el listado del sitio no expone precio).
- Si un filtro deja 0 resultados, se relaja (catálogo pequeño); si no hay ninguno, el bot lo dice
  y ofrece el asesor igual.

Todo esto vive en `Flow/FlowEngine.cs` como una máquina de estados: `FlujoActivo` + `Paso`.
Agregar/quitar una pregunta de un flujo es tocar `EnviarPreguntaActualAsync` y `ContinuarFlujoAsync`.

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

## 7. Notificación al asesor

Cuando un flujo termina, `Services/NotificationService.cs` avisa por **dos canales** (y guarda el
lead en la tabla `leads`):

1. **WhatsApp** a cada número de `WhatsApp__AdvisorPhoneNumber`. ⚠️ Meta **solo entrega** este
   mensaje si ese número le escribió al bot en las últimas 24 h (error `131047` si no). Sirve para
   asesores que están en contacto frecuente, no para alguien que nunca ha escrito.
2. **Correo** a `Email__To` vía API HTTPS (Resend o Brevo — Render bloquea el SMTP).
   Sin la restricción de 24 h: es el canal fiable. Si no configuras `Email__ApiKey`, se omite.

Los dos envíos quedan en `message_log`, así que en `/panel/errores` ves si alguno falló y por qué.
Otras alternativas fáciles de añadir en `NotificationService`: webhook de Slack/Discord/Teams,
o simplemente que la coordinación mire `/panel/leads`.

## 8. Panel de control (`/panel`)

La misma app sirve un panel web protegido con login para que el equipo comercial vea la
actividad del bot, sin abrir la base de datos.

| Página | Qué muestra |
|---|---|
| `/panel` | Tarjetas con totales (conversaciones, leads hoy/semana/sin atender, mensajes hoy, **errores de envío**), gráfico de solicitudes por tipo, y las conversaciones más recientes. |
| `/panel/conversaciones` | Todos los números que han escrito, con buscador; en qué flujo y paso quedaron. |
| `/panel/conversacion?tel=…` | Ficha de un contacto: estado, **respuestas capturadas**, inmuebles mostrados y la **transcripción completa** (chat) con los envíos que fallaron marcados en rojo. |
| `/panel/leads` | Los leads (flujo completado → asesor notificado), con filtros y botón para marcarlos **atendido**. |
| `/panel/errores` | Respuestas que WhatsApp **no** pudo entregar, con el error exacto de Meta. |

**Login**: usuario/clave únicos en las variables `Dashboard__Email` y `Dashboard__Password`
(no van en el repo; se ponen en Render). El login tiene bloqueo tras 6 intentos fallidos.

Para que el panel tenga transcripciones, el bot ahora **registra cada mensaje** (entrante y saliente,
con el resultado del envío) en la tabla `message_log`.

## 9. Qué contiene el proyecto

```
whatsapp-bot-inmobiliaria/
├── DEPLOY.md                                   Guía de despliegue (Neon + GitHub + Render + Meta)
├── Dockerfile                                  Imagen de la app (la usa Render)
├── render.yaml                                 Blueprint de Render (crea el servicio solo)
├── .env.example                                Variables de entorno que necesita la app
├── .github/workflows/ci.yml                    GitHub Actions: compila y dispara el deploy en Render
├── WhatsApp Gemini Chatbot.json                Workflow viejo de n8n (referencia)
├── sql/schema.sql                              Tablas PostgreSQL (la app también las crea al arrancar)
└── src/WhatsappBot.Functions/
    ├── Program.cs                              Arranque, endpoints /webhook y montaje del panel
    ├── Flow/FlowEngine.cs                      Máquina de estados de los 8 flujos (el "cerebro")
    ├── Models/                                 DTOs de Meta + Property + ConversationState
    ├── Pages/Panel/                            El panel de control (Razor Pages + login)
    └── Services/
        ├── WhatsAppService.cs                  Enviar texto / botones / listas vía Graph API + bitácora
        ├── PropertyCatalogService.cs           Scrapea el listado del sitio (misma lógica que el n8n)
        ├── PostgresConversationStateService.cs Estado de la conversación en PostgreSQL
        ├── PostgresLeadRepository.cs           Guarda los leads en la tabla leads
        ├── PostgresMessageLog.cs               Bitácora de mensajes (alimenta el panel)
        ├── PostgresDbInitializer.cs            Crea/actualiza las tablas al arrancar
        ├── PanelData.cs                        Consultas de lectura del panel
        ├── EmailSender.cs                      Aviso de lead por correo vía API (Resend / Brevo)
        └── NotificationService.cs              Aviso al asesor: WhatsApp + correo, con bitácora
```

> La carpeta se llama `src/WhatsappBot.Functions/` por herencia de una versión anterior sobre Azure
> Functions; hoy es una app web normal de ASP.NET Core (el ensamblado se llama `WhatsappBot`).

## 10. Siguientes pasos sugeridos

1. Sigue [`DEPLOY.md`](DEPLOY.md) para dejarlo corriendo 24/7 y probarlo con tu número.
2. Confirma en tu panel de Meta la tarifa real de "service messages" antes del 1 de octubre de 2026.
3. Cuando estés conforme, deja en `WhatsApp__AdvisorPhoneNumber` los números reales de los asesores.
4. Mejoras naturales más adelante: enriquecer cada inmueble leyendo también su ficha (precio, zona, área) para poder filtrar por presupuesto de verdad; mandar la foto del inmueble (WhatsApp soporta mensajes tipo `image` con `link`); exportar los leads del panel a CSV/Excel.

---

### Fuentes consultadas para la sección de costos

- [WhatsApp API Pricing 2026: Channels, Regions & Templates — Wati](https://www.wati.io/en/blog/whatsapp-api-pricing-guide/)
- [Service messages — Meta for Developers](https://developers.facebook.com/documentation/business-messaging/whatsapp/messages/send-messages)
- [Interactive List — WhatsApp Cloud API — Meta for Developers](https://developers.facebook.com/docs/whatsapp/cloud-api/messages/interactive-list-messages/)
- [Interactive reply buttons messages — Meta for Developers](https://developers.facebook.com/documentation/business-messaging/whatsapp/messages/interactive-reply-buttons-messages)
