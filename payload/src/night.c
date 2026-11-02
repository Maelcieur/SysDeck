/*
 * SwitchDeck - mode nuit : image de nuit + heure dessinée par la Switch (RTC MAX77620).
 *
 * Le kit reçu (deck_proto.h) est gardé en DRAM, dans un tampon alloué une fois dans le tas. L'heure
 * affichée = heure RTC + décalage calculé à la réception de l'heure locale du PC.
 */

#include <stddef.h>
#include <string.h>

#include <bdk.h>

#include "gfx/gfx.h"

#include "deck_proto.h"
#include "night.h"
#include "screen.h"

// Taille maximale d'un kit = taille maximale des données d'une commande.
#define KIT_MAX USB_EP_BULK_OUT_MAX_XFER

#define TICK_MS          250   // Période minimale entre deux passages (animation de charge, heure).
#define BATTERY_CHECK_MS 5000  // Période de contrôle de la batterie (affichage et sécurité).
#define BATTERY_MIN      5     // Sous ce niveau, sans charge : extinction.
#define BATTERY_STRIKES  2     // Contrôles consécutifs avant extinction.
#define ANIM_STEPS       6     // Pas de l'animation de charge : 6 x 250 ms = 1,5 s par cycle.
#define BAT_LOW          20    // Sous ce niveau, remplissage "faible" ; sous BAT_CRIT, "critique".
#define BAT_CRIT         10
#define BAT_MAX_DIM      256   // Taille maximale (px) des images de l'icône, bornée pour les calculs.
#define GLYPH_PERCENT    10    // Index de '%' dans les glyphes de l'icône.

// Texte de secours (kit sans icône), haut à droite, restauré depuis l'image avant chaque redessin.
#define BAT_X 960
#define BAT_Y 10
#define BAT_W (SCREEN_W - 8 - BAT_X)
#define BAT_H 20
#define BRIGHTNESS_MIN   2     // Rétroéclairage minimum visible (0-255).

#define GLYPH_COLON 10

static void (*power_off_cb)();

static u8 *kit_mem; // Tampon alloué (marge d'alignement incluse).
static u8 *kit;     // Début aligné.
static bool kit_ok;

static deck_night_hdr_t hdr;
static const u32 *image;
static const u32 *glyph[DECK_NIGHT_GLYPHS];

// La RTC de la Switch dérive (~0,4 % trop vite) : le PC étant éteint, l'heure affichée serait en avance.
// Les recalages du PC servent donc aussi à mesurer cette dérive, sur une longue base pour que la
// résolution d'une seconde reste négligeable.
#define RATE_ONE_PPM 1000000u // 1 s de RTC = 1 s locale
#define RATE_MIN_PPM 990000u  // Mesures hors de cette plage ignorées (changement d'heure, réglage du PC).
#define RATE_MAX_PPM 1010000u
#define CAL_MIN_S 1800u       // Base minimale avant de mesurer la dérive.
#define CAL_MAX_S 604800u     // Base repartie de zéro au bout de 7 jours (suit la température).

static bool time_set;
static u32 sync_rtc;   // RTC au dernier recalage.
static u32 sync_local; // Heure locale du PC au dernier recalage.
static bool cal_set;
static u32 cal_rtc, cal_local; // Point de départ de la mesure de dérive.
static u32 rate_ppm = RATE_ONE_PPM; // Secondes locales par seconde de RTC, en millionièmes.

static bool shown;
static int shown_minute;
static u32 drawn_x, drawn_w; // Zone où l'heure est actuellement dessinée (w = 0 : rien).

// Icône de batterie (kit v4) : images et zone validées par night_load(), icon_ok faux = texte de secours.
static bool icon_ok;
static const u32 *icon_outline, *icon_bolt;
static const u32 *icon_digit[DECK_NIGHT_BAT_GLYPHS];
static u32 icon_area_w; // Largeur de la zone (icône + espace + "100%").

static u32 next_tick, next_battery;
static u32 battery_strikes;
static int bat_cur = -2; // Dernier niveau lu (-2 : rien lu, à dessiner) et dernier état de charge lu.
static bool chg_cur;
static u32 anim_step;
static bool in_tick;

void night_init(void (*power_off)())
{
	power_off_cb = power_off;
}

u8 *night_dest(u32 data_len)
{
	kit_ok = false;
	shown = false;

	if (data_len < DECK_BLOCK_SIZE + DECK_NIGHT_IMAGE_BYTES || data_len > KIT_MAX || data_len % DECK_BLOCK_SIZE)
		return NULL;

	if (!kit_mem)
	{
		kit_mem = malloc(KIT_MAX + USB_EP_BUFFER_ALIGN);
		if (!kit_mem)
			return NULL;
		kit = (u8 *)ALIGN((u32)kit_mem, USB_EP_BUFFER_ALIGN);
	}

	return kit;
}

// Heure de la RTC en secondes epoch.
static u32 _rtc_epoch()
{
	rtc_time_t t;
	max77620_rtc_get_time(&t);
	return max77620_rtc_date_to_epoch(&t);
}

// Valide l'icône de batterie du kit (positions, tailles, octets disponibles) et repère ses images, placées
// après les glyphes de l'heure. Faux (kit plus ancien ou incohérent) : le texte de secours sera utilisé.
static bool _load_icon(u32 data_len)
{
	u32 bw = hdr.bat_w, bh = hdr.bat_h, th = hdr.txt_h;
	if (!bw || !bh || bw > BAT_MAX_DIM || bh > BAT_MAX_DIM || !th || th > bh)
		return false;

	// Intérieur à remplir dans le contour, rayon des coins, éclair (facultatif) dans le contour.
	u32 fx = hdr.fill_x, fy = hdr.fill_y, fw = hdr.fill_w, fh = hdr.fill_h;
	if (!fw || !fh || fx + fw > bw || fy + fh > bh || hdr.fill_r > MIN(fw, fh) / 2)
		return false;
	u32 ow = hdr.bolt_w, oh = hdr.bolt_h;
	if (ow > BAT_MAX_DIM || oh > BAT_MAX_DIM || !ow != !oh || ow > bw || oh > bh)
		return false;

	// Largeurs des chiffres et de '%' ; la zone doit tenir à l'écran (3 chiffres au plus + '%').
	u32 digit_max = 0, text_px = 0;
	for (u32 i = 0; i < DECK_NIGHT_BAT_GLYPHS; i++)
	{
		u32 w = hdr.txt_w[i];
		if (!w || w > BAT_MAX_DIM)
			return false;
		text_px += w;
		if (i < GLYPH_PERCENT && w > digit_max)
			digit_max = w;
	}
	u32 area_w = bw + hdr.txt_gap + 3 * digit_max + hdr.txt_w[GLYPH_PERCENT];
	if (hdr.txt_gap > BAT_MAX_DIM || (u32)hdr.bat_x + area_w > SCREEN_W || (u32)hdr.bat_y + bh > SCREEN_H)
		return false;

	// Octets : contour + éclair + glyphes du texte (tous <= 256 x 256 x 4, sans débordement de u32).
	u32 base = DECK_BLOCK_SIZE + DECK_NIGHT_IMAGE_BYTES;
	if (hdr.glyph_h)
	{
		u32 time_px = 0;
		for (u32 i = 0; i < DECK_NIGHT_GLYPHS; i++)
			time_px += hdr.glyph_w[i];
		base += time_px * hdr.glyph_h * 4; // Déjà vérifié par night_load : tient dans data_len.
	}
	u32 need = (bw * bh + ow * oh + text_px * th) * 4;
	if (base > data_len || need > data_len - base)
		return false;

	const u8 *p = kit + base;
	icon_outline = (const u32 *)p;
	p += bw * bh * 4;
	icon_bolt = (const u32 *)p;
	p += ow * oh * 4;
	for (u32 i = 0; i < DECK_NIGHT_BAT_GLYPHS; i++)
	{
		icon_digit[i] = (const u32 *)p;
		p += hdr.txt_w[i] * th * 4;
	}
	icon_area_w = area_w;

	return true;
}

bool night_load(u32 data_len)
{
	kit_ok = false;
	if (!kit || data_len < DECK_BLOCK_SIZE + DECK_NIGHT_IMAGE_BYTES || data_len > KIT_MAX)
		return false;

	memcpy(&hdr, kit, sizeof(hdr));
	if (hdr.magic != DECK_NIGHT_MAGIC || hdr.version != DECK_NIGHT_VERSION)
		return false;

	image = (const u32 *)(kit + DECK_BLOCK_SIZE);

	// Glyphes : tout est vérifié ici pour que le dessin n'ait plus à s'en soucier.
	if (hdr.glyph_h)
	{
		if (hdr.glyph_h > SCREEN_H || hdr.top_y + hdr.glyph_h > SCREEN_H || hdr.center_x > SCREEN_W)
			return false;

		u32 avail = data_len - DECK_BLOCK_SIZE - DECK_NIGHT_IMAGE_BYTES;
		u32 used = 0;
		const u8 *p = kit + DECK_BLOCK_SIZE + DECK_NIGHT_IMAGE_BYTES;
		for (u32 i = 0; i < DECK_NIGHT_GLYPHS; i++)
		{
			u32 w = hdr.glyph_w[i];
			if (!w || w > SCREEN_W)
				return false;

			u32 bytes = w * hdr.glyph_h * 4; // <= 1280 * 720 * 4 : pas de débordement.
			if (bytes > avail - used)
				return false;

			glyph[i] = (const u32 *)(p + used);
			used += bytes;
		}
	}

	icon_ok = _load_icon(data_len);

	night_set_time(hdr.local_time);
	kit_ok = true;
	return true;
}

void night_set_time(u32 local_time)
{
	u32 rtc = _rtc_epoch();

	u32 base_rtc = rtc - cal_rtc;
	if (!cal_set || (s32)base_rtc < 0 || base_rtc > CAL_MAX_S)
	{
		cal_rtc = rtc;
		cal_local = local_time;
		cal_set = true;
	}
	else if (base_rtc >= CAL_MIN_S)
	{
		u32 base_local = local_time - cal_local;
		u32 rate = (u32)((u64)base_local * RATE_ONE_PPM / base_rtc);
		if (rate >= RATE_MIN_PPM && rate <= RATE_MAX_PPM)
		{
			rate_ppm = rate;
		}
		else
		{
			cal_rtc = rtc; // Saut de l'heure du PC : mesure reprise à zéro, dérive précédente conservée.
			cal_local = local_time;
		}
	}

	sync_rtc = rtc;
	sync_local = local_time;
	time_set = true;
	shown_minute = -1; // Redessine l'heure au prochain contrôle.
}

bool night_valid()
{
	return kit_ok;
}

// Fusionne un pixel BGRA prémultiplié sur un pixel opaque : dst = src + dst * (255 - alpha) / 255.
static u32 _blend(u32 src, u32 dst)
{
	u32 a = src >> 24;
	if (!a)
		return dst;

	u32 ia = 255 - a;
	u32 out = 0xFF000000;
	for (u32 shift = 0; shift < 24; shift += 8)
	{
		u32 d = ((dst >> shift) & 0xFF) * ia;
		u32 c = ((src >> shift) & 0xFF) + ((d + 1 + (d >> 8)) >> 8); // d / 255
		out |= MIN(c, 255) << shift;
	}

	return out;
}

// Restaure depuis l'image de nuit la zone où l'heure était dessinée.
static void _restore_time_area()
{
	if (!drawn_w)
		return;

	u32 *fb = screen_fb();
	for (u32 y = hdr.top_y; y < (u32)hdr.top_y + hdr.glyph_h; y++)
		memcpy(fb + y * SCREEN_W + drawn_x, image + y * SCREEN_W + drawn_x, drawn_w * 4);

	drawn_w = 0;
}

// Dessine "HH:MM" centré sur center_x.
static void _draw_time(u32 hour, u32 min)
{
	const u32 digits[5] = { hour / 10, hour % 10, GLYPH_COLON, min / 10, min % 10 };

	u32 total = 0;
	for (u32 i = 0; i < 5; i++)
		total += hdr.glyph_w[digits[i]];
	if (total > SCREEN_W)
		return;

	s32 x0 = (s32)hdr.center_x - (s32)(total / 2);
	x0 = MAX(0, MIN(x0, (s32)(SCREEN_W - total)));

	u32 x = x0;
	u32 *fb = screen_fb();
	for (u32 i = 0; i < 5; i++)
	{
		u32 w = hdr.glyph_w[digits[i]];
		const u32 *src = glyph[digits[i]];
		for (u32 j = 0; j < hdr.glyph_h; j++)
		{
			u32 *dst = fb + (hdr.top_y + j) * SCREEN_W + x;
			for (u32 k = 0; k < w; k++)
				dst[k] = _blend(src[j * w + k], dst[k]);
		}
		x += w;
	}

	drawn_x = x0;
	drawn_w = total;
}

// Redessine l'heure si la minute a changé (ou si elle est à refaire). Vrai si l'écran a changé.
static bool _update_time()
{
	if (!hdr.glyph_h || !time_set)
		return false;

	rtc_time_t t;
	u32 elapsed = _rtc_epoch() - sync_rtc;
	max77620_rtc_epoch_to_date(sync_local + (u32)((u64)elapsed * rate_ppm / RATE_ONE_PPM), &t);

	int minute = t.hour * 60 + t.min;
	if (minute == shown_minute)
		return false;

	_restore_time_area();
	_draw_time(t.hour, t.min);
	shown_minute = minute;

	return true;
}

// Texte de secours (kit sans icône) avec la console texte de hekate : vert "NN % En charge" en charge,
// "NN %" sinon. Le fond de chaque caractère est noir pour rester lisible sur n'importe quelle image.
static void _draw_battery_text(int bat, bool charging)
{
	u32 *fb = screen_fb();
	for (u32 y = BAT_Y; y < BAT_Y + BAT_H; y++)
		memcpy(fb + y * SCREEN_W + BAT_X, image + y * SCREEN_W + BAT_X, BAT_W * 4);

	if (bat >= 0)
	{
		gfx_con.fntsz = 16;
		gfx_con_setcol(charging ? COL_OK : COL_TEXT, 1, 0xFF000000);
		gfx_con_setpos(BAT_X + 32, BAT_Y + 2);
		gfx_printf(charging ? " %d%% En charge " : " %d%% ", bat);
	}
}

// Remplit en couleur opaque un rectangle à coins arrondis (arc de cercle de rayon r, sans anticrénelage).
static void _fill_round(u32 x, u32 y, u32 w, u32 h, u32 r, u32 color)
{
	u32 *fb = screen_fb();
	r = MIN(r, MIN(w, h) / 2);

	for (u32 j = 0; j < h; j++)
	{
		u32 edge = MIN(j, h - 1 - j); // Distance à la ligne du haut ou du bas.
		u32 inset = 0;
		if (edge < r)
		{
			// Plus petit retrait dont le centre du pixel est dans le cercle (valeurs doublées : centres à +0,5).
			s32 dy2 = 2 * (s32)r - 2 * (s32)edge - 1;
			while (inset < r)
			{
				s32 dx2 = 2 * (s32)r - 2 * (s32)inset - 1;
				if (dx2 * dx2 + dy2 * dy2 <= 4 * (s32)(r * r))
					break;
				inset++;
			}
		}

		u32 *line = fb + (y + j) * SCREEN_W + x;
		for (u32 i = inset; i + inset < w; i++)
			line[i] = color;
	}
}

// Pose une image BGRA prémultiplié (w x h) en (x, y) sur l'écran, dans la zone validée par _load_icon().
static void _blend_image(const u32 *src, u32 w, u32 h, u32 x, u32 y)
{
	u32 *fb = screen_fb();
	for (u32 j = 0; j < h; j++)
	{
		u32 *dst = fb + (y + j) * SCREEN_W + x;
		for (u32 i = 0; i < w; i++)
			dst[i] = _blend(src[j * w + i], dst[i]);
	}
}

// Couleur du remplissage : charge, sinon selon le niveau.
static u32 _fill_color(int bat, bool charging)
{
	if (charging)
		return hdr.col_charge | 0xFF000000;

	return (bat < BAT_CRIT ? hdr.col_crit : bat < BAT_LOW ? hdr.col_low : hdr.col_normal) | 0xFF000000;
}

// Dessine l'icône de batterie (contour, remplissage proportionnel, éclair en charge) puis "NN%". Pendant la
// charge, le remplissage monte du niveau réel jusqu'à plein selon anim_step. Seule la zone de l'icône change.
static void _draw_icon(int bat, bool charging)
{
	u32 ax = hdr.bat_x, ay = hdr.bat_y, bw = hdr.bat_w, bh = hdr.bat_h, th = hdr.txt_h;

	u32 *fb = screen_fb();
	for (u32 y = ay; y < ay + bh; y++)
		memcpy(fb + y * SCREEN_W + ax, image + y * SCREEN_W + ax, icon_area_w * 4);

	if (bat < 0)
	{
		_blend_image(icon_outline, bw, bh, ax, ay);
		return;
	}

	u32 level = bat > 100 ? 100 : bat;
	if (charging && level < 100)
		level += (100 - level) * anim_step / (ANIM_STEPS - 1);

	u32 fw = hdr.fill_w * level / 100;
	if (level && fw < 2u * hdr.fill_r)
		fw = MIN(2u * hdr.fill_r, hdr.fill_w);
	if (fw)
		_fill_round(ax + hdr.fill_x, ay + hdr.fill_y, fw, hdr.fill_h, hdr.fill_r, _fill_color(bat, charging));

	_blend_image(icon_outline, bw, bh, ax, ay);

	if (charging && hdr.bolt_w)
	{
		s32 bx = (s32)hdr.fill_x + ((s32)hdr.fill_w - (s32)hdr.bolt_w) / 2;
		s32 by = (s32)hdr.fill_y + ((s32)hdr.fill_h - (s32)hdr.bolt_h) / 2;
		bx = MAX(0, MIN(bx, (s32)(bw - hdr.bolt_w)));
		by = MAX(0, MIN(by, (s32)(bh - hdr.bolt_h)));
		_blend_image(icon_bolt, hdr.bolt_w, hdr.bolt_h, ax + bx, ay + by);
	}

	// Texte : chiffres du niveau réel puis '%', centré verticalement sur l'icône.
	u32 pct = MIN((u32)bat, 100u);
	u32 digits[4];
	u32 count = 0;
	if (pct >= 100)
		digits[count++] = 1;
	if (pct >= 10)
		digits[count++] = (pct / 10) % 10;
	digits[count++] = pct % 10;
	digits[count++] = GLYPH_PERCENT;

	u32 tx = ax + bw + hdr.txt_gap;
	u32 ty = ay + (bh - th) / 2;
	for (u32 i = 0; i < count; i++)
	{
		_blend_image(icon_digit[digits[i]], hdr.txt_w[digits[i]], th, tx, ty);
		tx += hdr.txt_w[digits[i]];
	}
}

// Redessine l'indicateur de batterie (icône, ou texte de secours) avec le dernier état lu.
static void _draw_battery()
{
	if (icon_ok)
		_draw_icon(bat_cur, chg_cur);
	else
		_draw_battery_text(bat_cur, chg_cur);
}

// Animation de charge : un pas de plus dans le cycle. Vrai si l'écran a changé.
static bool _animate_charge()
{
	if (!icon_ok || !chg_cur || bat_cur < 0 || bat_cur >= 100)
		return false;

	anim_step = (anim_step + 1) % ANIM_STEPS;
	_draw_battery();
	return true;
}

// Contrôle la batterie : redessine l'indicateur si le niveau ou l'état de charge a changé (vrai si l'écran a
// changé) et éteint la Switch si elle est presque vide sans se recharger.
static bool _check_battery()
{
	int bat = screen_battery_percent();
	bool charging = screen_charging();

	if (bat >= 0 && bat < BATTERY_MIN && !charging)
		battery_strikes++;
	else
		battery_strikes = 0;

	if (battery_strikes >= BATTERY_STRIKES && power_off_cb)
		power_off_cb();

	if (bat == bat_cur && charging == chg_cur)
		return false;

	bat_cur = bat;
	chg_cur = charging;
	anim_step = 0;
	_draw_battery();
	return true;
}

bool night_set_brightness(u32 percent)
{
	if (!kit_ok)
		return false;

	hdr.brightness = MIN(percent, 100);
	// Le kit gardé en mémoire reste cohérent avec hdr (même octets que l'en-tête reçu).
	memcpy(kit + offsetof(deck_night_hdr_t, brightness), &hdr.brightness, sizeof(hdr.brightness));

	// Nuit à l'écran : applique tout de suite (sinon, c'est night_show() qui la lira).
	if (shown)
		screen_brightness(MAX(hdr.brightness * 255 / 100, BRIGHTNESS_MIN));
	return true;
}

void night_show()
{
	if (!kit_ok)
		return;

	screen_brightness(MAX(hdr.brightness * 255 / 100, BRIGHTNESS_MIN));

	if (shown)
		return;

	memcpy(screen_fb(), image, DECK_NIGHT_IMAGE_BYTES);
	shown = true;
	shown_minute = -1;
	drawn_w = 0;
	next_tick = 0;
	next_battery = get_tmr_ms() + BATTERY_CHECK_MS;
	battery_strikes = 0;
	bat_cur = -2;

	_update_time();
	_check_battery();
	screen_present();
}

void night_hide()
{
	shown = false;
}

void night_tick()
{
	if (!shown || in_tick || get_tmr_ms() < next_tick)
		return;

	in_tick = true;
	next_tick = get_tmr_ms() + TICK_MS;

	bool changed = _update_time();

	if (get_tmr_ms() >= next_battery)
	{
		next_battery = get_tmr_ms() + BATTERY_CHECK_MS;
		changed |= _check_battery();
	}
	else
		changed |= _animate_charge();

	if (changed)
		screen_present();

	in_tick = false;
}
