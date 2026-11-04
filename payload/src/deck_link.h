/*
 * SwitchDeck - session USB avec SysDeck (voir deck_proto.h).
 */

#ifndef _DECK_LINK_H_
#define _DECK_LINK_H_

#include <utils/types.h>

typedef enum _deck_link_res_t
{
	DECK_LINK_INIT_ERROR,   // Contrôleur USB non initialisé.
	DECK_LINK_TIMEOUT,      // Aucun PC pendant ~90 s.
	DECK_LINK_USER_ABORT,   // VOL+ et VOL- pendant l'attente.
	DECK_LINK_DISCONNECTED, // Hôte perdu (câble, PC éteint) ou erreur de transfert.
	DECK_LINK_POWER_OFF,    // Bouton POWER.
	DECK_LINK_REBOOT,       // VOL+ et VOL- pendant la session.
	DECK_LINK_IDLE          // Interne : silence du PC, retour à l'attente sans réinitialiser l'USB.
} deck_link_res_t;

// Attend l'énumération par un PC, puis reste connecté tant que l'hôte est là (sans limite, même sans commande) :
// la première commande reçue appelle on_connected (session normale), BYE ou un silence appellent on_idle (attente
// ou mode nuit). Ne retourne que si l'hôte disparaît, sur erreur, ou sur action de l'utilisateur.
deck_link_res_t deck_link_session(void (*on_connected)(), void (*on_idle)());

// Joy-Con (rails ou Switch Lite) : allumés pendant une session avec le PC, éteints en attente.
void deck_link_jc_power(bool on);

#endif
