/*
 * SwitchDeck - mode nuit : PC éteint, la Switch affiche l'image de nuit reçue et l'heure de sa RTC
 * (voir DECK_CMD_NIGHT dans deck_proto.h).
 */

#ifndef _NIGHT_H_
#define _NIGHT_H_

#include <utils/types.h>

// power_off : extinction propre de la Switch, appelée si la batterie est presque vide.
void night_init(void (*power_off)());

// Invalide le kit en cours (data_len = 0 : effacement ; sinon il ne redevient valide qu'après night_load()).
// Renvoie le tampon (aligné pour l'USB) où recevoir directement un kit de data_len octets, ou NULL s'il est refusé.
u8 *night_dest(u32 data_len);

// Valide le kit reçu dans le tampon (en-tête, positions, tailles) ; faux = refusé.
bool night_load(u32 data_len);

// Recale l'heure de nuit sur l'heure locale du PC (secondes epoch « locale »).
void night_set_time(u32 local_time);

// Change la luminosité de veille du kit déjà reçu (0-100), sans le renvoyer. Faux s'il n'y a pas de kit valide.
bool night_set_brightness(u32 percent);

bool night_valid();

// Affiche la nuit (image, heure, luminosité de veille) ; sans effet visible si déjà affichée.
void night_show();

// L'écran a été redessiné par autre chose : la nuit sera réaffichée en entier.
void night_hide();

// À appeler souvent pendant l'attente : met l'heure à jour à chaque minute et surveille la batterie.
void night_tick();

#endif
