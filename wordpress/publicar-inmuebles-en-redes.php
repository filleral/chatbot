<?php
/**
 * Publicar inmuebles en redes
 *
 * Avisa al bot cada vez que se PUBLICA un inmueble (post type "em_portfolio") para que
 * el bot lo publique automáticamente en Facebook e Instagram.
 *
 * Instalación (elige una):
 *   A) Pega TODO este archivo (sin la etiqueta <?php de arriba si tu functions.php ya la tiene)
 *      al final del functions.php de tu tema hijo.
 *   B) Súbelo como  wp-content/mu-plugins/publicar-inmuebles-en-redes.php  (se activa solo).
 *
 * Antes de usarlo, cambia las dos constantes de abajo.
 */

if (!defined('ABSPATH')) exit;

define('BOT_REDES_URL',    'https://whatsapp-bot-inmobiliaria-0um6.onrender.com/wordpress/nuevo-inmueble');
define('BOT_REDES_SECRET', 'PON-AQUI-EL-MISMO-VALOR-QUE-Social__WebhookSecret-EN-RENDER');
define('BOT_REDES_POST_TYPE', 'em_portfolio'); // el tipo de contenido de los inmuebles

add_action('transition_post_status', function ($new_status, $old_status, $post) {
    if ($post->post_type !== BOT_REDES_POST_TYPE) return;

    // Solo la PRIMERA vez que pasa a publicado (evita re-publicar en cada edición).
    if ($new_status !== 'publish' || $old_status === 'publish') return;

    wp_remote_post(BOT_REDES_URL, array(
        'timeout'  => 15,
        'blocking' => false, // no frenar el guardado en el admin
        'headers'  => array(
            'Content-Type'     => 'application/json',
            'X-Webhook-Secret' => BOT_REDES_SECRET,
        ),
        'body' => wp_json_encode(array('post_id' => $post->ID)),
    ));
}, 20, 3);

/**
 * Opcional: un botón "Publicar en redes ahora" en la pantalla de edición del inmueble,
 * para re-publicar a mano sin tocar el estado del post.
 */
add_action('post_submitbox_misc_actions', function () {
    global $post;
    if (!$post || $post->post_type !== BOT_REDES_POST_TYPE) return;
    $nonce = wp_create_nonce('bot_redes_' . $post->ID);
    echo '<div class="misc-pub-section">'
       . '<a href="' . esc_url(admin_url('admin-post.php?action=bot_redes_publicar&post=' . $post->ID . '&_wpnonce=' . $nonce)) . '" '
       . 'class="button">📢 Publicar en redes ahora</a></div>';
});

add_action('admin_post_bot_redes_publicar', function () {
    $post_id = isset($_GET['post']) ? (int) $_GET['post'] : 0;
    if (!$post_id || !current_user_can('edit_post', $post_id)
        || !wp_verify_nonce($_GET['_wpnonce'] ?? '', 'bot_redes_' . $post_id)) {
        wp_die('No autorizado');
    }
    wp_remote_post(BOT_REDES_URL, array(
        'timeout'  => 40,
        'headers'  => array('Content-Type' => 'application/json', 'X-Webhook-Secret' => BOT_REDES_SECRET),
        'body'     => wp_json_encode(array('post_id' => $post_id)),
    ));
    wp_safe_redirect(get_edit_post_link($post_id, 'url') . '&bot_redes=ok');
    exit;
});
