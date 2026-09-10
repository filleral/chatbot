# Poner el bot en producción (24/7) con Render — paso a paso

El bot es una app web de .NET 8. Se despliega en **Render** (plan Free, sin tarjeta) conectando
este repositorio de GitHub: cada `git push` vuelve a desplegar solo.

## Panorama

```
Tu WhatsApp Business (Meta Cloud API)
        │  webhook HTTPS
        ▼
Render  (Docker, plan Free)  ──►  PostgreSQL en Neon (estado + leads)
        │
        ├──►  bienesraiceswhite.co/inmuebles/   (catálogo, scraping)
        └──►  WhatsApp Cloud API                 (respuestas y aviso al asesor)
```

| Pieza | Costo | Nota |
|---|---|---|
| Render (web service, Free) | **$0** | Se "duerme" tras 15 min sin tráfico; se despierta solo en ~30–60 s. Ver el truco del "ping" al final para tenerlo despierto 24/7 gratis. |
| PostgreSQL en Neon (Free) | **$0** | 0,5 GB, siempre accesible |
| WhatsApp Cloud API | $0 hoy para mensajes de respuesta | Revisa tu tarifa antes del **1-oct-2026** |

---

## 0. Requisitos previos

- Cuenta de **GitHub** (ya tienes el repo `filleral/chatbot`).
- Cuenta de **Render** → https://render.com (entra con GitHub, sin tarjeta).
- Cuenta de **Neon** → https://neon.tech (sin tarjeta).
- De **Meta**:
  - **Phone Number ID** — en tu caso: `526644653870463`
  - **Token de acceso permanente** (`EAA...`) — usuario del sistema con caducidad "Nunca" y
    permisos `whatsapp_business_messaging` + `whatsapp_business_management`.

---

## 1. Base de datos en Neon

1. https://console.neon.tech → **New Project** (nombre `whatsapp-bot`, región *AWS US East*).
2. Menú **SQL Editor** → pega todo [`sql/schema.sql`](sql/schema.sql) → **Run**. Deben quedar las
   tablas `conversation_state`, `leads`, `message_log`.
3. Botón **Connect** → selector de formato → **`.NET`** → copia la cadena. Debe verse así
   (usa el host que **termina en `-pooler`**):
   ```
   Host=ep-xxxx-pooler.c-5.us-east-2.aws.neon.tech;Database=neondb;Username=neondb_owner;Password=npg_xxxx;SSL Mode=Require;Trust Server Certificate=true
   ```
   > Si Neon te da `SSL Mode=VerifyFull;Channel Binding=Require`, cámbialo por
   > `SSL Mode=Require;Trust Server Certificate=true` (más tolerante en el primer despliegue).

---

## 2. Sube el código a GitHub

En **Git Bash**, dentro de la carpeta del proyecto:

```bash
cd "C:/Users/POWER/Downloads/whatsapp-bot-inmobiliaria/whatsapp-bot-inmobiliaria"

git init
git branch -M main
git add -A
git commit -m "WhatsApp bot inmobiliaria — menús + scraping + PostgreSQL"
git remote add origin https://github.com/filleral/chatbot.git
git push -u origin main
```

La primera vez, Git abrirá una ventana para iniciar sesión en GitHub (o pedirá usuario +
*Personal Access Token* como contraseña — se crea en GitHub → Settings → Developer settings →
Tokens, marcando el permiso `repo`).

> `.gitignore` ya excluye `bin/`, `obj/`, `.env` y `appsettings.Development.json`, así que
> **no se sube ningún secreto**.

---

## 3. Crea el servicio en Render

**Opción A — Blueprint (automático, usa el `render.yaml` del repo):**

1. https://dashboard.render.com/blueprints → **New Blueprint Instance**.
2. Elige el repo `filleral/chatbot` → **Apply**.
3. Render crea el web service `whatsapp-bot-inmobiliaria`. Te va a pedir los valores marcados
   como secretos — pasa al Paso 4 para llenarlos.

**Opción B — a mano:**

1. https://dashboard.render.com → **New → Web Service** → conecta el repo `filleral/chatbot`.
2. Configuración:
   - **Language / Runtime:** `Docker`
   - **Branch:** `main`
   - **Instance Type:** `Free`
   - El resto por defecto (Render detecta el `Dockerfile`).
3. **Create Web Service**.

---

## 4. Variables de entorno en Render

En el servicio → pestaña **Environment** → añade estas (las mismas de [`.env.example`](.env.example)):

| Key | Value |
|---|---|
| `WhatsApp__VerifyToken` | un texto que te inventes, ej. `mi-token-secreto-123` |
| `WhatsApp__AccessToken` | tu token `EAA...` de Meta |
| `WhatsApp__PhoneNumberId` | `526644653870463` |
| `WhatsApp__AdvisorPhoneNumber` | número(s) que reciben la alerta de lead, con código de país, sin `+` (varios separados por coma) |
| `PostgresConnectionString` | la cadena de Neon del Paso 1 |
| `Dashboard__Email` | correo para entrar al panel, ej. `gerencia@bienesraiceswhite.co` |
| `Dashboard__Password` | contraseña del panel (elige una fuerte) |
| `PropertyScraper__ListingUrl` | `https://bienesraiceswhite.co/inmuebles/` |

**Save changes** → Render vuelve a desplegar. Espera a que el estado sea **Live** (verde).

Tu URL queda como `https://whatsapp-bot-inmobiliaria.onrender.com` (Render te la muestra arriba):

- `…/webhook` → lo que registras en Meta.
- `…/panel` → el panel de control (pide el `Dashboard__Email` / `Dashboard__Password`).

### 4.1 Aviso de leads por correo (recomendado)

El aviso por WhatsApp al asesor **solo llega si ese número le escribió al bot en las últimas
24 h** (regla de Meta, error `131047`). Para que la coordinación reciba *siempre* el lead, se
manda también por correo — con una API HTTPS, porque **Render bloquea el SMTP**.

**Opción A · Resend** (la más rápida, https://resend.com — gratis, sin tarjeta):

1. Regístrate con `gerencia@bienesraiceswhite.co`.
2. *API Keys* → **Create API Key** → copia la clave (`re_...`).
3. En Render añade:

   | Key | Value |
   |---|---|
   | `Email__ApiKey` | la clave `re_...` |
   | `Email__To` | `gerencia@bienesraiceswhite.co` |

   Sin verificar dominio, Resend solo deja enviar **a tu propio correo** (que es justo el
   destinatario aquí) desde `onboarding@resend.dev`. Si luego quieres enviar a más gente o
   desde `@bienesraiceswhite.co`, verifica el dominio en Resend (3 registros DNS) y pon
   `Email__From=gerencia@bienesraiceswhite.co`.

**Opción B · Brevo** (https://brevo.com — gratis, 300/día, permite varios destinatarios):

1. Regístrate. *Senders, Domains & Dedicated IPs* → añade y **verifica** `gerencia@bienesraiceswhite.co`
   (te llega un correo, haces clic en el enlace).
2. *SMTP & API* → *API Keys* → genera una (`xkeysib-...`).
3. En Render:

   | Key | Value |
   |---|---|
   | `Email__ApiKey` | la clave `xkeysib-...` |
   | `Email__From` | `gerencia@bienesraiceswhite.co` (el remitente verificado) |
   | `Email__To` | `gerencia@bienesraiceswhite.co` (varios separados por coma) |

Si dejas `Email__ApiKey` vacía, el bot funciona igual pero solo avisa por WhatsApp.

> Las variables `Email__SmtpHost/SmtpPort/User/Password` de versiones anteriores ya **no se usan**
> (bórralas de Render si las tienes).

---

## 5. Verifica que responde (sin Meta todavía)

Abre en el navegador (cambia el dominio y el token por los tuyos):

```
https://whatsapp-bot-inmobiliaria.onrender.com/webhook?hub.mode=subscribe&hub.verify_token=mi-token-secreto-123&hub.challenge=hola123
```

Debe devolver exactamente **`hola123`**. (La primera vez puede tardar ~1 min mientras Render
levanta el servicio.)

---

## 6. Conecta el webhook en Meta

*Meta for Developers → tu App → WhatsApp → Configuración → Webhooks → Editar:*

| Campo | Valor |
|---|---|
| URL de devolución de llamada | `https://whatsapp-bot-inmobiliaria.onrender.com/webhook` |
| Token de verificación | `mi-token-secreto-123` (el mismo del Paso 4) |

**Verificar y guardar** → suscríbete al campo **`messages`**.

---

## 7. Prueba desde tu WhatsApp

1. *WhatsApp → Configuración de la API* → agrega tu número personal como destinatario de prueba.
2. Escríbele al número del negocio → te llega el menú.
3. Recorre un flujo del menú (ej. *3 Comprar* o *6 Notarial*).
4. Al terminar: llega el aviso a `AdvisorPhoneNumber` y el lead queda guardado.
5. Abre **`https://…onrender.com/panel`**, entra con `Dashboard__Email` / `Dashboard__Password`
   y revisa la conversación, el lead y (si hubo) los errores de envío.

**Logs:** en Render, el servicio → pestaña **Logs** (en vivo).

---

## 8. Actualizar el bot (GitHub Actions)

Cambias código y:

```bash
git add -A
git commit -m "descripción del cambio"
git push
```

Al hacer push, **GitHub Actions** (`.github/workflows/ci.yml`) compila el proyecto. Si compila:

- **Por defecto:** Render detecta el push y redepliega solo (~2–3 min).
- **Deploy solo si compila (recomendado):**
  1. Render → tu servicio → *Settings* → **Deploy Hook** → copia la URL.
  2. GitHub → repo → *Settings* → *Secrets and variables* → *Actions* → **New repository secret**
     → nombre `RENDER_DEPLOY_HOOK_URL`, valor = esa URL.
  3. Render → tu servicio → *Settings* → **Auto-Deploy** → **Off**.

  Ahora el deploy lo dispara la Action *después* de compilar bien: un error de código nunca
  llega a producción.

---

## 9. Que no se "duerma" (24/7 real, gratis)

El plan Free de Render apaga el servicio tras 15 min sin tráfico; el primer mensaje después
tarda ~1 min (Meta reintenta, no se pierde). Para evitarlo, ponle un "ping" cada 10 minutos:

1. https://cron-job.org (gratis) → **Create cronjob**.
2. URL: `https://whatsapp-bot-inmobiliaria.onrender.com/health`
3. Cada **10 minutos**.

Con eso el servicio nunca se apaga y responde al instante. (Un servicio Free tiene 750 horas/mes
gratis ≈ suficiente para tenerlo encendido todo el mes.)

---

## 10. Publicar inmuebles en Facebook + Instagram (opcional)

Cuando se publica un inmueble en WordPress, el bot lo publica en la página de Facebook y en
Instagram (carrusel con todas las fotos), igual que el workflow "WordPress a Redes Sociales" de n8n.

### 10.1 Token de Meta con permisos de páginas e Instagram

Distinto al de WhatsApp. Lo más estable: un **usuario del sistema** (Business Settings →
*Usuarios del sistema* → uno nuevo, p. ej. "redes"):

1. **Asignar activos** → la **página de Facebook** y la **cuenta de Instagram** de la inmobiliaria, con control total.
2. **Generar token** → caducidad **Nunca** → permisos:
   `pages_show_list`, `pages_manage_posts`, `pages_read_engagement`,
   `instagram_basic`, `instagram_content_publish`, `business_management`.
3. Copia el token (`EAA...`).

### 10.2 IDs de la página y de Instagram

- **Facebook Page ID:** en la página → *Información* → abajo del todo, o con
  `https://graph.facebook.com/v21.0/me/accounts?access_token=TU_TOKEN`. n8n usaba `417059049062092`.
- **Instagram User ID:** `https://graph.facebook.com/v21.0/{PAGE_ID}?fields=instagram_business_account&access_token=TU_TOKEN`.
  n8n usaba `17841411886661916`.

### 10.3 Variables en Render

| Key | Value |
|---|---|
| `Social__WebhookSecret` | un texto que inventes (va igual en WordPress) |
| `Social__MetaAccessToken` | el token `EAA...` del paso 10.1 |
| `Social__FacebookPageId` | el Page ID |
| `Social__InstagramUserId` | el Instagram User ID |
| `Social__WpBaseUrl` | `https://bienesraiceswhite.co` |
| `Social__PostType` | `em_portfolio` |

### 10.4 Avisar desde WordPress

Sube `wordpress/publicar-inmuebles-en-redes.php` a `wp-content/mu-plugins/` (se activa solo), o
pega su contenido en el `functions.php` del tema hijo. Antes, cambia dentro del archivo:
`BOT_REDES_SECRET` = el mismo `Social__WebhookSecret`.

### 10.5 Probar

- En `/panel/publicaciones` hay un campo para publicar un inmueble a mano (por id o URL).
- O publica un inmueble de prueba en WordPress y míralo aparecer en el historial del panel,
  con enlaces al post de Facebook y al de Instagram.

---

## 11. Problemas comunes

| Síntoma | Causa / arreglo |
|---|---|
| Meta: "No se pudo validar la URL" | El `WhatsApp__VerifyToken` de Render no coincide con el de Meta. Prueba primero el Paso 5. |
| El bot recibe pero no responde | Token de WhatsApp vencido o `PhoneNumberId` mal. Míralo en **Logs** de Render. |
| Error de conexión a la base | Cadena de Neon mal pegada. Usa el host `-pooler` y `SSL Mode=Require;Trust Server Certificate=true`. |
| `git push` pide contraseña y falla | GitHub ya no acepta contraseña: usa un *Personal Access Token* (permiso `repo`) como contraseña. |
| La primera respuesta tras un rato tarda ~1 min | El servicio estaba dormido. Aplica el Paso 9. |
| Redes: `Invalid OAuth access token` | El `Social__MetaAccessToken` venció o le faltan permisos (paso 10.1). |
| Redes: Instagram falla y Facebook no | IG exige mínimo 1 foto y máximo 10; las URLs de las fotos deben ser públicas (lo son en WordPress). |
