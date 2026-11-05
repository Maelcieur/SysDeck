/*
 * SwitchDeck - session USB avec SysDeck.
 *
 * On réutilise le gadget UMS de la BDK pour son transport bulk EP1 IN/OUT ;
 * ses descripteurs sont remplacés dans usb_descriptors.c (classe vendeur, WinUSB).
 */

#include <string.h>

#include <bdk.h>

#include "deck_link.h"
#include "deck_proto.h"
#include "night.h"
#include "screen.h"

// Silence du PC pendant une session avant de repasser à l'attente (SysDeck envoie un PING toutes les 100 ms
// au moins). L'USB reste initialisé et énuméré : c'est le PC qui réveille la Switch, pas l'inverse.
#define CMD_TIMEOUT_MS 4000

// Hôte considéré perdu (PC éteint, câble débranché, bus en suspension, erreurs de transfert) après ce délai :
// seulement alors l'USB est désinitialisé pour attendre une nouvelle énumération.
#define HOST_LOST_MS 5000

// Les lectures synchrones de la BDK ne traitent pas les requêtes de contrôle de l'hôte
// (CLEAR_FEATURE...) : on attend donc la commande par tranches courtes (~2 µs par unité).
#define CMD_SLICE 25000 // ~50 ms.

// Délai des transferts dont le PC est déjà à l'origine (données, réponse).
#define XFER_TIMEOUT USB_XFER_SYNCED_DATA

#define BUTTONS_REBOOT (BTN_VOL_UP | BTN_VOL_DOWN)

// Crochet de btn.c (copie modifiée de la BDK) appelé à chaque tour de la boucle d'attente USB.
extern void (*btn_vol_hook)();

static usb_ops_t usb_ops;

static u8 *const out_buf = (u8 *)USB_EP_BULK_OUT_BUF_ADDR;
static u8 *const in_buf  = (u8 *)USB_EP_BULK_IN_BUF_ADDR;

// Événements tactiles en attente d'envoi au PC.
static deck_touch_ev_t touch_ev[DECK_MAX_EVENTS];
static u32 touch_ev_count;
static bool touch_down;
static u16 touch_x, touch_y;

// Vrai jusqu'à la première réponse d'une session : le PC doit alors renvoyer tout l'affichage et la luminosité.
static bool session_new;

// Joy-Con (v6) : alimentés pendant la session, ou en attente si la Switch reçoit une alimentation externe.
static bool jc_on;
static u32 jc_pressed; // Appuis accumulés depuis la dernière réponse.
static u32 jc_last;

static void _touch_push(u16 type, u16 x, u16 y)
{
	// File pleine : on écrase le dernier déplacement plutôt que de perdre un appui ou un relâchement.
	if (touch_ev_count == DECK_MAX_EVENTS)
	{
		if (type == DECK_TOUCH_MOVE)
			return;
		touch_ev_count--;
	}

	deck_touch_ev_t *ev = &touch_ev[touch_ev_count++];
	ev->type = type;
	ev->x = x;
	ev->y = y;
	ev->rsvd = 0;
}

static void _touch_update()
{
	touch_event_t ev;
	if (touch_poll(&ev))
		return;

	if (ev.touch)
	{
		if (!touch_down)
			_touch_push(DECK_TOUCH_DOWN, ev.x, ev.y);
		else if (ev.x != touch_x || ev.y != touch_y)
			_touch_push(DECK_TOUCH_MOVE, ev.x, ev.y);

		touch_down = true;
		touch_x = ev.x;
		touch_y = ev.y;
	}
	else if (touch_down)
	{
		// Au relâchement, seul x est fiable (voir touch.c) : on renvoie la dernière position connue.
		_touch_push(DECK_TOUCH_UP, touch_x, touch_y);
		touch_down = false;
	}
}

static void _touch_reset()
{
	touch_ev_count = 0;
	touch_down = false;
}

// Lit les Joy-Con (le pilote limite lui-même le rythme des échanges) et retient les nouveaux appuis.
static void _jc_update()
{
	if (!jc_on)
		return;
	jc_gamepad_rpt_t *pad = joycon_poll();
	if (!pad)
		return;
	jc_pressed |= pad->buttons & ~jc_last;
	jc_last = pad->buttons;
}

void deck_link_jc_power(bool on)
{
	if (on == jc_on)
		return;
	jc_on = on;
	jc_pressed = 0;
	jc_last = 0;
	if (on)
		jc_init_hw();
	else
		jc_deinit();
}

u8 jc_debug_state(bool left); // joycon.c (copie locale du pilote de la BDK)

static void _fill_jc(deck_reply_t *rpl)
{
	_jc_update();
	jc_gamepad_rpt_t *pad = jc_on ? joycon_poll() : NULL;
	if (!pad)
		return;
	rpl->jc_dbg_l = jc_debug_state(true);
	rpl->jc_dbg_r = jc_debug_state(false);
	rpl->jc_buttons = pad->buttons & 0xFFFFFF;
	rpl->jc_pressed = jc_pressed & 0xFFFFFF;
	rpl->lstick_x = pad->lstick_x;
	rpl->lstick_y = pad->lstick_y;
	rpl->rstick_x = pad->rstick_x;
	rpl->rstick_y = pad->rstick_y;
	rpl->jc_flags = (pad->conn_l ? DECK_JC_LEFT : 0) | (pad->conn_r ? DECK_JC_RIGHT : 0) | (pad->sio_mode ? DECK_JC_LITE : 0);
	rpl->jc_batt_l = pad->batt_info_l;
	rpl->jc_batt_r = pad->batt_info_r;
}

static int _send_reply(u32 seq, u16 status)
{
	_touch_update();

	deck_reply_t *rpl = (deck_reply_t *)in_buf;
	memset(in_buf, 0, DECK_BLOCK_SIZE);

	int bat = screen_battery_percent();

	rpl->magic    = DECK_REPLY_MAGIC;
	rpl->version  = DECK_PROTO_VERSION;
	rpl->status   = status;
	rpl->seq      = seq;
	rpl->screen_w = SCREEN_W;
	rpl->screen_h = SCREEN_H;
	rpl->battery  = bat >= 0 ? bat : 0xFF;
	rpl->charging = (screen_charging() ? DECK_REPLY_CHARGING : 0) | (session_new ? DECK_REPLY_NEW_SESSION : 0);
	rpl->buttons  = btn_read_vol();
	rpl->ev_count = touch_ev_count;
	memcpy(rpl->ev, touch_ev, touch_ev_count * sizeof(deck_touch_ev_t));
	_fill_jc(rpl);

	u32 written = 0;
	int res = usb_ops.usb_device_ep1_in_write(in_buf, DECK_BLOCK_SIZE, &written, XFER_TIMEOUT);
	if (!res)
	{
		touch_ev_count = 0;
		session_new = false;
		jc_pressed = 0;
	}

	return res;
}

static bool _rect_valid(const deck_cmd_t *cmd)
{
	u32 x = cmd->x, y = cmd->y, w = cmd->w, h = cmd->h;

	return w && h && x + w <= SCREEN_W && y + h <= SCREEN_H && cmd->data_len >= w * h * 4;
}

// Un rectangle pleine largeur est déjà au format du tampon : on le reçoit directement dedans,
// sans copie, si son adresse respecte l'alignement exigé par l'USB.
static u8 *_rect_direct_dest(const deck_cmd_t *cmd)
{
	if (cmd->cmd != DECK_CMD_RECT || !_rect_valid(cmd) || cmd->x || cmd->w != SCREEN_W
		|| cmd->data_len != cmd->w * cmd->h * 4)
		return NULL;

	u8 *dest = (u8 *)(screen_fb() + cmd->y * SCREEN_W);
	if ((u32)dest % USB_EP_BUFFER_ALIGN)
		return NULL;

	return dest;
}

static u16 _handle_rect(const deck_cmd_t *cmd, bool received_in_place)
{
	if (!_rect_valid(cmd))
		return DECK_STATUS_BAD_RECT;

	if (!received_in_place)
		screen_blit(cmd->x, cmd->y, cmd->w, cmd->h, (const u32 *)out_buf);

	if (cmd->flags & DECK_FLAG_PRESENT)
		screen_present();

	return DECK_STATUS_OK;
}

// Attend le prochain bloc commande en restant réactif (contrôle USB, boutons, tactile, heure de nuit).
// active : session en cours, un silence de CMD_TIMEOUT_MS renvoie DECK_LINK_IDLE ; sinon (attente) on attend
// sans limite. Retourne DECK_LINK_DISCONNECTED si un bloc est arrivé (à lire dans out_buf), sinon le motif d'arrêt.
// L'hôte est perdu si le bus reste en suspension (PC éteint ou câble retiré) ou en erreur HOST_LOST_MS.
static deck_link_res_t _wait_cmd(bool active, bool *received)
{
	static u32 next_dram_train;
	static u32 next_jc_power_check;

	*received = false;
	u32 deadline = get_tmr_ms() + CMD_TIMEOUT_MS;
	u32 bad_since = 0; // Début de la suspension ou des erreurs en cours, 0 si le bus est sain.

	while (!active || get_tmr_ms() < deadline)
	{
		night_tick();

		// DRAM à 1600 MHz : entraînement périodique obligatoire (sans effet sinon).
		if (get_tmr_ms() >= next_dram_train)
		{
			minerva_periodic_training();
			next_dram_train = get_tmr_ms() + EMC_PERIODIC_TRAIN_MS;
		}

		u8 btn = btn_read();
		if (btn & BTN_POWER)
			return DECK_LINK_POWER_OFF;
		if ((btn & BUTTONS_REBOOT) == BUTTONS_REBOOT)
			return active ? DECK_LINK_REBOOT : DECK_LINK_USER_ABORT;

		// Requêtes de contrôle de l'hôte (reset, reconfiguration...). Un reset du canal bulk n'a de sens qu'en session.
		if (usb_ops.usbd_handle_ep0_ctrl_setup(NULL) && active)
			return DECK_LINK_DISCONNECTED;

		_touch_update();
		if (!active && get_tmr_ms() >= next_jc_power_check)
		{
			deck_link_jc_power(screen_has_external_power());
			next_jc_power_check = get_tmr_ms() + 1000;
		}
		_jc_update();

		bool bad = usb_ops.usb_device_get_suspended();

		// Effacé avant chaque essai : si le bloc arrive pile à l'expiration de la tranche,
		// la marque reste visible et on ne le perd pas.
		*(vu32 *)out_buf = 0;

		u32 got = 0;
		int res = usb_ops.usb_device_ep1_out_read(out_buf, DECK_BLOCK_SIZE, &got, CMD_SLICE);
		if (!res || (res == USB_ERROR_TIMEOUT && *(vu32 *)out_buf == DECK_CMD_MAGIC))
		{
			*received = true;
			return DECK_LINK_DISCONNECTED;
		}

		if (res != USB_ERROR_TIMEOUT)
		{
			// En session une erreur de transfert est fatale ; en attente on laisse le temps à l'hôte de se réénumérer.
			if (active)
				return DECK_LINK_DISCONNECTED;

			bad = true;
			msleep(10);
		}

		if (!bad)
			bad_since = 0;
		else if (!bad_since)
			bad_since = get_tmr_ms() | 1;
		else if (get_tmr_ms() - bad_since >= HOST_LOST_MS)
			return DECK_LINK_DISCONNECTED;
	}

	return DECK_LINK_IDLE;
}

// Sert le PC tant que l'hôte est là. Le premier bloc commande réveille la Switch (on_connected) ; BYE ou un
// silence la remettent en attente (on_idle) sans toucher à l'USB. Retourne le motif d'arrêt de la session.
static deck_link_res_t _serve(void (*on_connected)(), void (*on_idle)())
{
	bool active = false;
	deck_link_res_t requested_action = DECK_LINK_IDLE;

	while (true)
	{
		bool received;
		deck_link_res_t wait_res = _wait_cmd(active, &received);
		if (!received)
		{
			if (wait_res != DECK_LINK_IDLE)
				return wait_res;

			active = false;
			if (on_idle)
				on_idle();
			continue;
		}

		u32 got = 0;
		deck_cmd_t cmd;
		memcpy(&cmd, out_buf, sizeof(cmd));
		if (cmd.magic != DECK_CMD_MAGIC)
			return DECK_LINK_DISCONNECTED; // Désynchronisé : on repart d'une session propre.

		if (!active)
		{
			active = true;
			session_new = true;
			_touch_reset();
			if (on_connected)
				on_connected();
		}

		// Données éventuelles (pixels), toujours lues pour rester synchronisé.
		u8 *direct = _rect_direct_dest(&cmd);
		if (!direct && cmd.cmd == DECK_CMD_NIGHT)
			direct = night_dest(cmd.data_len); // Le kit nuit est reçu directement à sa place.
		if (cmd.data_len)
		{
			if (cmd.data_len % DECK_BLOCK_SIZE || cmd.data_len > USB_EP_BULK_OUT_MAX_XFER)
				return DECK_LINK_DISCONNECTED;

			if (usb_ops.usb_device_ep1_out_read_big(direct ? direct : out_buf, cmd.data_len, &got))
				return DECK_LINK_DISCONNECTED;
		}

		u16 status = DECK_STATUS_OK;
		switch (cmd.cmd)
		{
		case DECK_CMD_PING:
			break;

		case DECK_CMD_RECT:
			status = _handle_rect(&cmd, direct != NULL);
			break;

		case DECK_CMD_PRESENT:
			screen_present();
			break;

		case DECK_CMD_BRIGHTNESS:
			screen_brightness(MIN(cmd.arg, 100) * 255 / 100);
			break;

		case DECK_CMD_NIGHT:
			// Sans données : efface le kit (déjà invalidé par night_dest). Sans tampon (refusé), les données
			// ont été lues dans out_buf et sont ignorées.
			if (cmd.data_len && (!direct || !night_load(cmd.data_len)))
				status = DECK_STATUS_BAD_NIGHT;
			break;

		case DECK_CMD_NIGHT_TIME:
			night_set_time(cmd.arg);
			break;

		case DECK_CMD_NIGHT_BRIGHT:
			if (!night_set_brightness(cmd.arg))
				status = DECK_STATUS_BAD_NIGHT;
			break;

		case DECK_CMD_SWITCH_ACTION:
			if (cmd.data_len)
				status = DECK_STATUS_BAD_CMD;
			else if (cmd.arg == DECK_SWITCH_ACTION_RCM)
				requested_action = DECK_LINK_ENTER_RCM;
			else if (cmd.arg == DECK_SWITCH_ACTION_HEKATE)
				requested_action = DECK_LINK_LAUNCH_HEKATE;
			else
				status = DECK_STATUS_BAD_CMD;
			break;

		case DECK_CMD_BYE:
			if (_send_reply(cmd.seq, status))
				return DECK_LINK_DISCONNECTED;

			active = false;
			if (on_idle)
				on_idle();
			continue;

		default:
			status = DECK_STATUS_BAD_CMD;
			break;
		}

		if (_send_reply(cmd.seq, status))
			return DECK_LINK_DISCONNECTED;
		if (status == DECK_STATUS_OK && requested_action != DECK_LINK_IDLE)
			return requested_action;
	}
}

deck_link_res_t deck_link_session(void (*on_connected)(), void (*on_idle)())
{
	if (hw_get_chip_id() == GP_HIDREV_MAJOR_T210)
		usb_device_get_ops(&usb_ops);
	else
		xusb_device_get_ops(&usb_ops);

	if (usb_ops.usb_device_init())
	{
		usb_ops.usbd_end(false, true);
		return DECK_LINK_INIT_ERROR;
	}

	deck_link_res_t res;

	// Attente du PC (jusqu'à ~90 s, VOL+ et VOL- pour interrompre).
	// Pendant ce temps, l'heure de nuit continue d'avancer.
	btn_vol_hook = night_tick;
	int enum_res = usb_ops.usb_device_enumerate(USB_GADGET_UMS);
	btn_vol_hook = NULL;
	if (enum_res)
	{
		// La BDK renvoie USER_ABORT aussi au bout des ~90 s sans PC : le menu ne s'ouvre que si VOL+ et VOL-
		// sont réellement enfoncés, sinon on relance simplement l'attente (nuit ou écran d'attente conservés).
		bool user_abort = enum_res == USB_ERROR_USER_ABORT && btn_read_vol() == (BTN_VOL_UP | BTN_VOL_DOWN);
		res = user_abort ? DECK_LINK_USER_ABORT : DECK_LINK_TIMEOUT;
		goto out;
	}

	res = _serve(on_connected, on_idle);

out:
	usb_ops.usbd_end(true, false);

	return res;
}
