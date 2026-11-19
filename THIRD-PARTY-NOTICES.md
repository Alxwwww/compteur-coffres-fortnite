# Logiciels et données de tiers

## Fortnite Chest Counter (Jose-AE)

https://github.com/Jose-AE/fortnite-chest-counter

La présentation du tableau (gros total, bascule « Rien que toi / Avec tes coéquipiers », tuiles de couleur, graphique empilé, zone de dépôt des replays, import et export) s'inspire de ce site. Aucun code, icône ni image n'en est repris : ce projet est sous licence AGPL-3.0, et le tableau a été écrit à part. Le compteur sait aussi importer les fichiers exportés par ce site.

## ChestCounter (LED-N)

https://github.com/LED-N/Fortnite-ChestCounter

Repris dans ce projet :
- l'idée de la méthode de comptage : signature des coffres (`SearchingPawn`), coffres rechargés, coffres tombés retrouvés grâce à leur butin, réparation des coffres mal nommés, parties comptées (Battle Royale de la saison) ;
- le fichier de données `src/CompteurCoffres/tresors.json` (copie de `content/content.json` : chances, lots de chaque semaine, heures au trésor).

Le code de ce projet a été réécrit ; il n'inclut pas le code de ChestCounter.

```
MIT License

Copyright (c) 2026 LED-N

Permission is hereby granted, free of charge, to any person obtaining a copy
of this software and associated documentation files (the "Software"), to deal
in the Software without restriction, including without limitation the rights
to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
copies of the Software, and to permit persons to whom the Software is
furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in all
copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
SOFTWARE.
```

## FortniteReplayReader (Shiqan)

https://github.com/Shiqan/FortniteReplayDecompressor

Utilisé tel quel, comme paquet NuGet (`FortniteReplayReader` 3.1.0), pour lire les replays.

```
MIT License

Copyright (c) 2020-2026 Shiqan

Permission is hereby granted, free of charge, to any person obtaining a copy
of this software and associated documentation files (the "Software"), to deal
in the Software without restriction, including without limitation the rights
to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
copies of the Software, and to permit persons to whom the Software is
furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in all
copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
SOFTWARE.
```