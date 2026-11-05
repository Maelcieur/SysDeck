/*
 * SwitchDeck - écran paysage 1280x720 (rotation matérielle VIC, comme Nyx).
 */

#include <string.h>

#include <bdk.h>
#include <power/bq24193.h>

#include "gfx/gfx.h"
#include "screen.h"

// Tampon paysage où l'on dessine, recopié et tourné par le VIC vers le tampon affiché.
static u32 *const fb_land = (u32 *)NYX_FB2_ADDRESS;

static u32 brightness_cur;

void screen_init()
{
	// Fenêtre A en portrait (720x1280), comme hekate.
	display_init_window_a_pitch();
	display_backlight_pwm_init();

	memset(fb_land, 0, SCREEN_W * SCREEN_H * 4);

	// Même configuration que Nyx : on dessine en 1280x720, le VIC tourne de 270° vers l'écran.
	vic_surface_t vic_sfc;
	vic_sfc.src_buf  = NYX_FB2_ADDRESS;
	vic_sfc.dst_buf  = NYX_FB_ADDRESS;
	vic_sfc.width    = SCREEN_W;
	vic_sfc.height   = SCREEN_H;
	vic_sfc.pix_fmt  = VIC_PIX_FORMAT_X8R8G8B8;
	vic_sfc.rotation = VIC_ROTATION_270;

	vic_init();
	vic_set_surface(&vic_sfc);
	screen_present();

	display_init_window_a_pitch_vic();

	// La console texte de hekate dessine directement dans le tampon paysage.
	gfx_init_ctxt(fb_land, SCREEN_W, SCREEN_H, SCREEN_W);
	gfx_con_init();

	screen_brightness(BRIGHTNESS_NORMAL);
}

u32 *screen_fb()
{
	return fb_land;
}

void screen_present()
{
	vic_compose();
	vic_wait_idle();
}

void screen_brightness(u32 value)
{
	if (value == brightness_cur)
		return;

	display_backlight_brightness(value, 1000);
	brightness_cur = value;
}

void screen_fill_rect(u32 x, u32 y, u32 w, u32 h, u32 color)
{
	if (x >= SCREEN_W || y >= SCREEN_H)
		return;
	if (x + w > SCREEN_W)
		w = SCREEN_W - x;
	if (y + h > SCREEN_H)
		h = SCREEN_H - y;

	for (u32 j = 0; j < h; j++)
	{
		u32 *line = fb_land + (y + j) * SCREEN_W + x;
		for (u32 i = 0; i < w; i++)
			line[i] = color;
	}
}

// Le rectangle doit tenir dans l'écran (vérifié par l'appelant).
void screen_blit(u32 x, u32 y, u32 w, u32 h, const u32 *pixels)
{
	for (u32 j = 0; j < h; j++)
		memcpy(fb_land + (y + j) * SCREEN_W + x, pixels + j * w, w * 4);
}

int screen_battery_percent()
{
	int val = 0;
	if (max17050_get_property(MAX17050_RepSOC, &val))
		return -1;

	return val >> 8;
}

bool screen_charging()
{
	int status = 0;
	if (bq24193_get_property(BQ24193_ChargeStatus, &status))
		return false;

	// 1 : pré-charge, 2 : charge rapide.
	return status == 1 || status == 2;
}

bool screen_has_external_power()
{
	u8 status = i2c_recv_byte(I2C_1, BQ24193_I2C_ADDR, BQ24193_Status);
	return (status & BQ24193_STATUS_PG_MASK) != 0;
}

static void _draw_header()
{
	screen_fill_rect(0, 0, SCREEN_W, SCREEN_H, COL_BG);
	screen_fill_rect(0, 0, SCREEN_W, 64, COL_PANEL);
	screen_fill_rect(0, 64, SCREEN_W, 3, COL_ACCENT);

	gfx_con.fntsz = 16;
	gfx_con_setcol(COL_TEXT, 0, 0);
	gfx_con_setpos(32, 24);
	gfx_printf("SwitchDeck");

	int bat = screen_battery_percent();
	gfx_con_setcol(COL_DIM, 0, 0);
	gfx_con_setpos(SCREEN_W - 260, 24);
	if (bat >= 0)
		gfx_printf("Batterie %d%%%s", bat, screen_charging() ? " (charge)" : "");
}

void screen_draw_waiting(bool usb_ready)
{
	_draw_header();

	gfx_con_setcol(COL_TEXT, 0, 0);
	gfx_con_setpos(32, 140);
	gfx_printf("En attente du PC...");

	gfx_con_setcol(COL_DIM, 0, 0);
	gfx_con_setpos(32, 180);
	gfx_printf("Branche le cable USB-C et lance SysDeck sur le PC.");

	if (!usb_ready)
	{
		gfx_con_setpos(32, 220);
		gfx_printf("Erreur d'initialisation USB, nouvel essai...");
	}

	gfx_con_setpos(32, SCREEN_H - 48);
	gfx_printf("VOL+ et VOL- : menu (eteindre, redemarrer)");

	screen_present();
}

void screen_draw_connected(bool ram_fast)
{
	_draw_header();

	gfx_con_setcol(COL_OK, 0, 0);
	gfx_con_setpos(32, 140);
	gfx_printf("Connecte au PC.");

	gfx_con_setcol(COL_DIM, 0, 0);
	gfx_con_setpos(32, 180);
	gfx_printf("En attente de l'affichage de SysDeck...");

	gfx_con_setpos(32, 220);
	gfx_printf(ram_fast ? "Memoire : 1600 MHz" : "Memoire : 204 MHz (libsys_minerva.bso introuvable)");

	gfx_con_setpos(32, SCREEN_H - 48);
	gfx_printf("POWER : eteindre la Switch    VOL+ et VOL- : redemarrer");

	screen_present();
}

void screen_draw_menu(const char *note)
{
	_draw_header();

	gfx_con_setcol(COL_TEXT, 0, 0);
	gfx_con_setpos(32, 140);
	gfx_printf("Menu");

	gfx_con_setcol(COL_DIM, 0, 0);
	gfx_con_setpos(32, 200);
	gfx_printf("POWER : eteindre la Switch");
	gfx_con_setpos(32, 240);
	gfx_printf("VOL+  : redemarrer");
	gfx_con_setpos(32, 280);
	gfx_printf("VOL-  : retour a hekate");

	gfx_con_setcol(COL_DIM, 0, 0);
	gfx_con_setpos(32, SCREEN_H - 48);
	gfx_printf("Retour automatique a l'attente du PC sans appui pendant 30 s.");

	if (note)
	{
		gfx_con_setcol(0xFFFF5555, 0, 0);
		gfx_con_setpos(32, 340);
		gfx_printf("%s", note);
	}

	screen_present();
}
