/*
 * SwitchDeck - protocole USB entre SysDeck (PC, hôte) et la Switch (périphérique).
 *
 * Transport : bulk EP1 OUT (PC -> Switch) et EP1 IN (Switch -> PC), WinUSB côté PC.
 * Échange question/réponse, à l'initiative du PC :
 *   1. le PC envoie un bloc commande de 512 octets (deck_cmd_t, complété par des zéros) ;
 *   2. si data_len > 0, le PC envoie data_len octets (multiple de 512) ;
 *   3. la Switch répond par un bloc de 512 octets (deck_reply_t) qui contient
 *      les événements tactiles survenus depuis la réponse précédente.
 * Sans rien à afficher, le PC envoie DECK_CMD_PING au moins toutes les 100 ms ;
 * au-delà de ~4 s sans commande, la Switch se remet en attente (ou en mode nuit) sans quitter l'USB :
 * elle reste énumérée et c'est la première commande suivante du PC qui la réveille.
 *
 * Tout est en little-endian. Doit rester identique à DeckProtocol.cs côté SysDeck.
 */

#ifndef _DECK_PROTO_H_
#define _DECK_PROTO_H_

#include <utils/types.h>

#define DECK_PROTO_VERSION 6
#define DECK_BLOCK_SIZE    512

#define DECK_CMD_MAGIC   0x4B434453 // "SDCK"
#define DECK_REPLY_MAGIC 0x52434453 // "SDCR"

#define DECK_SCREEN_W 1280
#define DECK_SCREEN_H 720

typedef enum _deck_cmd_id_t
{
	DECK_CMD_PING       = 1, // Rien à faire, sert à relever le tactile.
	DECK_CMD_RECT       = 2, // Pixels XRGB8888 (w * h * 4 octets, lignes contiguës) à placer en (x, y).
	DECK_CMD_PRESENT    = 3, // Affiche ce qui a été dessiné.
	DECK_CMD_BRIGHTNESS = 4, // arg = luminosité 0-100.
	DECK_CMD_BYE        = 5, // Le PC s'arrête (veille, extinction) : retour à l'écran d'attente.
	DECK_CMD_NIGHT      = 6, // Kit nuit (deck_night_hdr_t + image + glyphes), stocké sans rien changer à l'affichage.
	DECK_CMD_NIGHT_TIME = 7, // arg = heure locale du PC (secondes epoch « locale »), recale l'horloge de nuit.
	DECK_CMD_NIGHT_BRIGHT = 8 // (v5) arg = luminosité de veille 0-100 : change seulement celle du kit nuit déjà reçu.
} deck_cmd_id_t;

#define DECK_FLAG_PRESENT BIT(0) // DECK_CMD_RECT : afficher juste après.

typedef enum _deck_status_t
{
	DECK_STATUS_OK          = 0,
	DECK_STATUS_BAD_CMD     = 1,
	DECK_STATUS_BAD_RECT    = 2,
	DECK_STATUS_BAD_NIGHT   = 3 // Kit nuit refusé (trop grand, en-tête ou positions invalides), ou aucun kit pour NIGHT_BRIGHT.
} deck_status_t;

typedef struct _deck_cmd_t
{
	u32 magic;
	u16 cmd;
	u16 flags;
	u32 seq;      // Renvoyé tel quel dans la réponse.
	u16 x, y, w, h;
	u32 data_len; // Octets qui suivent le bloc, multiple de DECK_BLOCK_SIZE.
	u32 arg;
} __attribute__((packed)) deck_cmd_t;

/*
 * Mode nuit : quand le PC est éteint (câble toujours alimenté), la Switch affiche l'image de nuit et
 * dessine l'heure elle-même avec les glyphes reçus, à partir de sa RTC.
 * Données de DECK_CMD_NIGHT (multiple de 512) :
 *   - 512 o : deck_night_hdr_t complété par des zéros ;
 *   - DECK_NIGHT_IMAGE_BYTES : image de nuit 1280x720 XRGB8888 (fond + carte de l'horloge, sans texte) ;
 *   - les 11 glyphes à la suite, dans l'ordre '0'..'9' ':', BGRA prémultiplié (alpha dans l'octet de poids
 *     fort), glyph_w[i] * glyph_h pixels chacun, lignes contiguës.
 * La ligne d'heure "HH:MM" est centrée sur center_x (somme des largeurs), glyphes posés en top_y.
 * glyph_h = 0 : pas d'heure (fond seul). Recevoir un kit ne change pas l'affichage.
 *   - (v4, facultatif) après les glyphes de l'heure : contour de la batterie (bat_w * bat_h pixels), éclair
 *     (bolt_w * bolt_h), puis les glyphes '0'..'9' '%' (txt_w[i] * txt_h), même format BGRA prémultiplié.
 */
#define DECK_NIGHT_MAGIC       0x4B4E4453 // "SDNK"
#define DECK_NIGHT_VERSION     1
#define DECK_NIGHT_GLYPHS      11
#define DECK_NIGHT_BAT_GLYPHS  11 // '0'..'9' puis '%'.
#define DECK_NIGHT_IMAGE_BYTES (DECK_SCREEN_W * DECK_SCREEN_H * 4)

typedef struct _deck_night_hdr_t
{
	u32 magic;
	u16 version;
	u16 brightness;  // Luminosité de veille, 0-100 % (au moins le minimum visible côté Switch).
	u32 local_time;  // Heure locale du PC, secondes depuis 1970 comptées en heure locale (sans fuseau).
	u16 center_x;    // Centre X de la ligne d'heure.
	u16 top_y;       // Y du haut des glyphes.
	u16 glyph_h;     // Hauteur commune des glyphes.
	u16 glyph_w[DECK_NIGHT_GLYPHS];

	// Icône de batterie (v4, octets à zéro dans un kit plus ancien : bat_w = 0 -> affichage texte de secours).
	// Zone = icône à (bat_x, bat_y), puis à droite après txt_gap le pourcentage, centré verticalement sur l'icône.
	u16 bat_x, bat_y;               // Coin haut-gauche de l'icône.
	u16 bat_w, bat_h;               // Taille de l'image du contour.
	u16 fill_x, fill_y, fill_w, fill_h; // Intérieur à remplir, relatif au contour.
	u16 fill_r;                     // Rayon des coins du remplissage.
	u16 bolt_w, bolt_h;             // Éclair (0 = aucun), centré dans l'intérieur quand la batterie charge.
	u16 txt_h, txt_gap;             // Hauteur des chiffres, espace entre l'icône et le texte.
	u16 txt_w[DECK_NIGHT_BAT_GLYPHS]; // Largeurs de '0'..'9' puis '%'.
	u32 col_normal, col_low, col_crit, col_charge; // Couleurs XRGB du remplissage (>= 20 %, < 20 %, < 10 %, en charge).
} __attribute__((packed)) deck_night_hdr_t;

typedef enum _deck_touch_type_t
{
	DECK_TOUCH_DOWN = 1,
	DECK_TOUCH_MOVE = 2,
	DECK_TOUCH_UP   = 3
} deck_touch_type_t;

typedef struct _deck_touch_ev_t
{
	u16 type;
	u16 x, y;  // Coordonnées écran paysage 1280x720.
	u16 rsvd;
} __attribute__((packed)) deck_touch_ev_t;

#define DECK_MAX_EVENTS 48

// Octet charging de la réponse. La Switch reste connectée entre deux sessions (le PC la réveille par sa première
// commande) : NEW_SESSION est présent dans la première réponse après ce réveil, le PC renvoie alors tout l'affichage
// et la luminosité (la Switch a pu repasser en attente sans le prévenir).
#define DECK_REPLY_CHARGING    BIT(0)
#define DECK_REPLY_NEW_SESSION BIT(1)

// (v6) Joy-Con fixés sur les rails (ou commandes de la Switch Lite) : état lu par le pilote Joy-Con de la BDK.
// Bits de jc_buttons / jc_pressed : ceux de jc_gamepad_rpt_t (Y=0, X, B, A, SR_R, SL_R, R, ZR, -=8, +, R3, L3, HOME, CAPTURE,
// bas=16, haut, droite, gauche, SR_L, SL_L, L, ZL=23).
#define DECK_JC_LEFT  BIT(0) // Joy-Con (L) connecté.
#define DECK_JC_RIGHT BIT(1) // Joy-Con (R) connecté.
#define DECK_JC_LITE  BIT(2) // Commandes intégrées (Switch Lite) : axe Y des sticks inversé.

typedef struct _deck_reply_t
{
	u32 magic;
	u16 version;
	u16 status;    // deck_status_t de la commande traitée.
	u32 seq;
	u16 screen_w, screen_h;
	u8  battery;   // Pourcentage, 0xFF si inconnu.
	u8  charging;  // Bits DECK_REPLY_* (v3 : avant, 0 ou 1).
	u8  buttons;   // Boutons BDK (BTN_VOL_UP, BTN_VOL_DOWN...) actuellement enfoncés.
	u8  ev_count;
	deck_touch_ev_t ev[DECK_MAX_EVENTS];

	// (v6) Joy-Con, octets 404 et suivants.
	u32 jc_buttons;             // Boutons enfoncés maintenant.
	u32 jc_pressed;             // Boutons enfoncés depuis la réponse précédente (un appui bref n'est jamais perdu).
	u16 lstick_x, lstick_y;     // Sticks bruts (12 bits, centre ~2048) : calibrés côté PC.
	u16 rstick_x, rstick_y;
	u8  jc_flags;               // DECK_JC_*.
	u8  jc_batt_l, jc_batt_r;   // Batterie brute (niveau 0-4 dans les bits 1-3, bit 0 = en charge).
	u8  jc_dbg_l, jc_dbg_r;     // Diagnostic du pilote (jc_debug_state) : bit 7 détecté, bit 6 connecté, bits 0-3 étape.
} __attribute__((packed)) deck_reply_t;

#endif
