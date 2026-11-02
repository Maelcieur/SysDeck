/*
 * SwitchDeck - payload bare-metal lancé depuis hekate.
 * Transforme la Switch en écran de dashboard et stream deck pour le PC (SysDeck) :
 * le PC dessine tout et l'envoie par USB, la Switch affiche et renvoie le tactile.
 *
 * Basé sur la BDK de hekate (CTCaer), GPLv2.
 */

#include <string.h>

#include <bdk.h>

#include "deck_link.h"
#include "night.h"
#include "screen.h"

extern void pivot_stack(u32 stack_top);
extern u8 launch_reloc[], launch_reloc_end[];

static void _power_off()
{
	screen_brightness(0);
	power_set_state(POWER_OFF_RESET);
}

static void _reboot()
{
	screen_brightness(0);
	power_set_state(POWER_OFF_REBOOT);
}

static void _wait_buttons_released()
{
	while (btn_read())
		msleep(10);
}

static bool ram_fast_ok;

// Entraîne la DRAM avec Minerva (bibliothèque de hekate sur la SD). Sans elle, la DRAM
// reste à 204 MHz : tout marche, mais les transferts d'images sont environ deux fois plus lents.
static void _ram_init()
{
	if (sd_mount())
		return;

	minerva_str_t *mtc = zalloc(sizeof(minerva_str_t));
	ram_fast_ok = !minerva_init(mtc);
	sd_end();

	// 800 MHz se passe d'entraînement périodique : idéal pendant l'attente du PC.
	if (ram_fast_ok)
		minerva_change_freq(FREQ_800);
}

// 1600 MHz pendant une session (entraînement périodique fait par deck_link), 800 MHz sinon.
static void _ram_fast(bool fast)
{
	if (ram_fast_ok)
		minerva_change_freq(fast ? FREQ_1600 : FREQ_800);
}

static bool usb_ready = true;

// Écran atténué tant qu'aucun PC ne parle : mode nuit (image + heure + batterie) si un kit a été reçu,
// sinon écran d'attente. Sans effet visible si déjà affiché.
static void _show_idle()
{
	if (usb_ready && night_valid())
		night_show();
	else
	{
		night_hide();
		screen_draw_waiting(usb_ready);
		screen_brightness(BRIGHTNESS_IDLE);
	}
}

// Le PC a dit BYE ou s'est tu : retour à l'attente, l'USB reste connecté.
static void _on_idle()
{
	_ram_fast(false);
	_show_idle();
}

static void _on_connected()
{
	night_hide();
	_ram_fast(true);
	screen_brightness(BRIGHTNESS_NORMAL);
	screen_draw_connected(ram_fast_ok);
}

// Retour à hekate : même principe que _launch_payload de hekate. Le fichier est lu dans le tas (DRAM), le
// matériel est remis dans l'état laissé par RCM, puis un relogeur recopié sous l'entrée standard des payloads
// (0x40010000) le charge à sa place et y saute. Renvoie faux (rien n'a changé) si hekate est introuvable.
#define HEKATE_ENTRY 0x40010000
#define HEKATE_MAX   0x30000     // Tient dans l'IRAM, comme la limite de hekate.
#define RELOC_STUB   0x40005000  // Zone de pile libre sous notre image : ni écrasée par hekate, ni utilisée plus tard.

static bool _launch_hekate()
{
	static const char *const paths[] = { "bootloader/update.bin", "payload.bin" };

	if (sd_mount())
		return false;

	void *buf = NULL;
	u32 size = 0;
	for (u32 i = 0; i < ARRAY_SIZE(paths) && !buf; i++)
	{
		size = 0;
		buf = sd_file_read(paths[i], &size);
		if (buf && (!size || size > HEKATE_MAX))
		{
			free(buf);
			buf = NULL;
		}
	}
	sd_end();

	if (!buf)
		return false;

	// Le relogeur copie par blocs de 16 octets.
	u32 len = ALIGN(size, 0x10);
	memcpy((void *)RELOC_STUB, launch_reloc, launch_reloc_end - launch_reloc);

	screen_brightness(0);
	hw_deinit(false);

	void (*reloc)(u32, u32, u32, u32) = (void *)RELOC_STUB;
	reloc((u32)buf, HEKATE_ENTRY, len, HEKATE_ENTRY);

	return true; // Inatteignable.
}

// Sans appui pendant ce délai, le menu (ouvert par erreur ?) se referme et l'attente du PC reprend.
#define MENU_TIMEOUT_MS 30000

// Menu ouvert par VOL+ et VOL- pendant l'attente du PC.
static void _menu()
{
	night_hide();
	screen_brightness(BRIGHTNESS_NORMAL);
	screen_draw_menu(NULL);
	_wait_buttons_released();

	u32 deadline = get_tmr_ms() + MENU_TIMEOUT_MS;
	while (get_tmr_ms() < deadline)
	{
		u8 btn = btn_read();

		if (btn & BTN_POWER)
			_power_off();
		else if (btn & BTN_VOL_UP)
			_reboot();
		else if (btn & BTN_VOL_DOWN)
		{
			if (!_launch_hekate())
			{
				screen_draw_menu("hekate introuvable sur la SD (bootloader/update.bin ou payload.bin).");
				_wait_buttons_released();
				deadline = get_tmr_ms() + MENU_TIMEOUT_MS;
			}
		}

		msleep(10);
	}

	_wait_buttons_released();
}

void ipl_main()
{
	hw_init();

	// Pile sous le payload, tas en DRAM, comme hekate.
	pivot_stack(IPL_LOAD_ADDR);
	heap_init((void *)IPL_HEAP_START);

	display_init();
	bpmp_clk_rate_set(BPMP_CLK_NORMAL);

	screen_init();
	night_init(_power_off);
	touch_power_on();
	_ram_init();

	// Le bouton qui a servi à lancer le payload depuis hekate peut être encore enfoncé.
	_wait_buttons_released();

	while (true)
	{
		_show_idle();

		deck_link_res_t res = deck_link_session(_on_connected, _on_idle);
		usb_ready = res != DECK_LINK_INIT_ERROR;
		_ram_fast(false);

		switch (res)
		{
		case DECK_LINK_POWER_OFF:
			_power_off();
			break;

		case DECK_LINK_REBOOT:
			_reboot();
			break;

		case DECK_LINK_USER_ABORT:
			_menu();
			break;

		case DECK_LINK_INIT_ERROR:
			msleep(1000);
			break;

		default: // Pas de PC ou PC parti : on se remet en attente.
			break;
		}
	}
}
