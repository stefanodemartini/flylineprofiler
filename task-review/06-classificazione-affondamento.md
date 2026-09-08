# Task: Classificazione industriale della velocità di affondamento (Type I-VI / Sink 1-8)

## Domanda

Oltre al numero grezzo in in/s e al badge AFFTA (peso), esiste anche una classificazione
industriale standard per la velocità di affondamento (tipo "Sink Type III", "S3") da poter
mostrare in app?

## Verifica

Nessuna classificazione di questo tipo è codificata oggi nell'app (verificato con ricerca nel
codice — nessun riferimento a "Sink Type", "S1"-"S8", "Intermediate", ecc.). A differenza del
sistema AFTMA per il peso linea (grammi per classe, standard rigido con tolleranze fisse), la
classificazione per velocità di affondamento **non è mai stata un vero standard vincolante** — è
nata come riferimento di categoria ma i produttori (RIO, Scientific Anglers, Airflo, ecc.)
etichettano le proprie linee con range in ips che variano da marca a marca.

## Conclusione — CHIUSO, nessuna azione

Confermato da Stefano con un esempio diretto: diciture come **"Hover"** ed **"Extra Hover"** (fasce
di affondamento lentissime, quasi neutre, usate da alcuni produttori) non hanno posto in nessuna
tabella Type I-VI/Sink 1-8 — prova che non esiste un insieme chiuso di classi su cui basare una
funzione di classificazione affidabile nell'app. Aggiungerne una fissa produrrebbe categorie
sbagliate o mancanti appena compare una nuova dicitura commerciale.

**Deciso**: nessuna classificazione a fasce viene aggiunta. L'app resta con il numero grezzo in
in/s (colonna Sink, ora coerente con la mappa — vedi [task 05](05-mappa-velocita-affondamento.md))
come unico riferimento per la velocità di affondamento.
