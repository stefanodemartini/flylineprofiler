# Task: Diametri compressi dalla rotella del calibro durante lo scan

## Problema

Durante lo scan, la rotella del calibro preme sul rivestimento morbido della flyline per
misurarla, riducendone di fatto il diametro rilevato rispetto a quello reale a riposo. Le
posizioni X (dall'encoder del motore) restano affidabili; è il diametro (Y) letto dallo scan a
essere sistematicamente più basso del vero.

## Perché non è automatizzabile

Prima ipotesi valutata — una correzione di calibrazione applicata alla curva scan (offset fisso,
o offset + fattore di scala) — **scartata**: lo scarto introdotto dalla compressione non è
costante né tra linee diverse né all'interno della stessa linea, perché dipende da diametro e
durezza/materiale del rivestimento punto per punto. Nessuna formula unica (fissa o lineare)
applicata all'intera curva sarebbe corretta ovunque.

## Conclusione — CHIUSO

Confermato con Stefano: **nessuna correzione automatica**. Il diametro va verificato a mano,
punto per punto, con uno strumento indipendente che non schiacci il rivestimento (micrometro a
becchi piatti, non la rotella). Lo scan resta comunque utile per due cose affidabili: le
posizioni X esatte e la forma generale del taper (dove cambia pendenza, dove inizia/finisce un
tratto cilindrico) — il diametro assoluto letto dallo scan è solo una guida approssimativa da
correggere nei punti che contano (soprattutto ai confini tra segmenti).

Nessuna modifica al codice necessaria: editare il diametro di un nodo direttamente in tabella
(già supportato, vedi [task 02](02-ricalco-segmenti-da-scan.md)) è sufficiente per applicare la
correzione manuale.

Proposto un flag "✓ verificato" per-nodo per tracciare quali diametri sono ancora grezzi vs
ricontrollati a mano — **rifiutato da Stefano**, non utile. Nessuna azione.
