using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;

namespace CompteurCoffres;

/// <summary>
/// Ce que les boutons du tableau (http://localhost:8766) demandent au programme : ajouter des replays,
/// importer ou exporter les parties, changer les reglages. Le serveur verifie avant que la demande vient bien du tableau.
/// </summary>
internal sealed class ActionsTableau
{
    private const long TailleMinReplay = 1024;
    private const long TailleMaxReplay = 1024L * 1024 * 1024;
    private const long TailleMaxImport = 64L * 1024 * 1024;
    private const long TailleMaxReglages = 64 * 1024;
    private static readonly JsonSerializerOptions Lisible = new() { WriteIndented = true };

    private readonly Moteur _moteur;

    public ActionsTableau(Moteur moteur) => _moteur = moteur;

    private Stockage Stockage => _moteur.Stockage;

    /// <summary>Un replay depose dans le tableau : il rejoint le dossier des replays ajoutes, lu comme celui de Fortnite.</summary>
    public (int, object) AjouterReplay(HttpListenerRequest requete)
    {
        var nom = Path.GetFileName(requete.QueryString["nom"] ?? "").Trim();
        var interdits = Path.GetInvalidFileNameChars();
        nom = new string(nom.Select(c => interdits.Contains(c) ? '_' : c).ToArray());
        if (nom.Length is < 8 or > 160 || nom.StartsWith('.') || !nom.EndsWith(".replay", StringComparison.OrdinalIgnoreCase))
            return (400, new { erreur = "Ce n'est pas un replay Fortnite." });
        var taille = requete.ContentLength64;
        if (taille < TailleMinReplay || taille > TailleMaxReplay)
            return (400, new { erreur = "Fichier vide, incomplet ou trop gros." });

        lock (Stockage.Verrou)
            if (Stockage.Donnees.FichiersVus.Contains(nom)) return (200, new { etat = "deja" });

        Directory.CreateDirectory(_moteur.DossierAjoutes);
        var cible = Path.Combine(_moteur.DossierAjoutes, nom);
        if (File.Exists(cible) && new FileInfo(cible).Length == taille) return (200, new { etat = "deja" });

        var temporaire = cible + ".envoi";
        try
        {
            using (var fichier = File.Create(temporaire)) requete.InputStream.CopyTo(fichier);
            if (new FileInfo(temporaire).Length != taille) throw new IOException("envoi incomplet");
            // La date de la partie est celle du fichier : on garde celle du replay d'origine.
            var date = DateTime.Now;
            if (long.TryParse(requete.QueryString["date"], NumberStyles.None, CultureInfo.InvariantCulture, out var ms) && ms > 0)
            {
                var origine = DateTimeOffset.FromUnixTimeMilliseconds(Math.Min(ms, DateTimeOffset.MaxValue.ToUnixTimeMilliseconds())).LocalDateTime;
                if (origine.Year >= 2017 && origine <= DateTime.Now.AddDays(1)) date = origine;
            }
            File.SetLastWriteTime(temporaire, date);
            File.Move(temporaire, cible, overwrite: true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or HttpListenerException)
        {
            try { File.Delete(temporaire); } catch (Exception) { /* deja absent */ }
            return (500, new { erreur = "Le replay n'a pas pu être enregistré : " + e.Message });
        }
        _moteur.Reveiller();
        return (200, new { etat = "ajoute" });
    }

    /// <summary>Sauvegarde de ce compteur, ou export du site Fortnite Chest Counter : on ajoute les parties absentes.</summary>
    public (int, object) Importer(HttpListenerRequest requete)
    {
        if (requete.ContentLength64 <= 0 || requete.ContentLength64 > TailleMaxImport)
            return (400, new { erreur = "Fichier vide ou trop gros." });
        JsonDocument document;
        try
        {
            using var lecteur = new StreamReader(requete.InputStream, Encoding.UTF8);
            document = JsonDocument.Parse(lecteur.ReadToEnd());
        }
        catch (JsonException)
        {
            return (400, new { erreur = "Ce fichier n'est pas une sauvegarde du compteur." });
        }

        using (document)
        {
            var racine = document.RootElement;
            var importees = new List<Partie>();
            int? coffresAvant = null;
            Calage? calage = null;
            DateTimeOffset? boutiqueObtenue = null;
            if (racine.ValueKind == JsonValueKind.Object && racine.TryGetProperty("parties", out var parties) && parties.ValueKind == JsonValueKind.Array)
            {
                foreach (var element in parties.EnumerateArray())
                {
                    try
                    {
                        if (element.Deserialize<Partie>() is { } partie && Valide(partie)) importees.Add(partie);
                    }
                    catch (Exception e) when (e is JsonException or InvalidOperationException or FormatException) { /* entree abimee : ignoree */ }
                }
                if (racine.TryGetProperty("coffresAvant", out var avant) && avant.ValueKind == JsonValueKind.Number && avant.TryGetInt32(out var n) && n is > 0 and <= 10_000_000)
                    coffresAvant = n;
                if (racine.TryGetProperty("calage", out var c) && c.ValueKind == JsonValueKind.Object)
                {
                    try { calage = c.Deserialize<Calage>(); } catch (JsonException) { /* ignore */ }
                    if (calage is { Valeur: < 0 or > 1_000_000 }) calage = null;
                }
                if (racine.TryGetProperty("boutiqueObtenue", out var b) && b.ValueKind == JsonValueKind.String && b.TryGetDateTimeOffset(out var date))
                    boutiqueObtenue = date;
            }
            else
            {
                // Export du site : une liste de parties, seule ou dans { "matches": [...] }.
                var liste = racine.ValueKind == JsonValueKind.Array ? racine
                    : racine.ValueKind == JsonValueKind.Object && racine.TryGetProperty("matches", out var m) ? m : default;
                if (liste.ValueKind == JsonValueKind.Array)
                    foreach (var element in liste.EnumerateArray())
                        if (DepuisSite(element) is { } partie && Valide(partie)) importees.Add(partie);
            }
            if (importees.Count == 0) return (400, new { erreur = "Aucune partie trouvée dans ce fichier." });

            int ajoutees, ignorees;
            lock (Stockage.Verrou)
            {
                (ajoutees, ignorees) = Stockage.Fusionner(importees);
                if (Stockage.Donnees.CoffresAvant == 0 && coffresAvant is { } avantImporte) Stockage.Donnees.CoffresAvant = avantImporte;
                if (Stockage.Donnees.Calage == null && calage != null) Stockage.Donnees.Calage = calage;
                if (Stockage.Donnees.BoutiqueObtenue == null && boutiqueObtenue != null) Stockage.Donnees.BoutiqueObtenue = boutiqueObtenue;
            }
            _moteur.Enregistrer();
            return (200, new { ajoutees, ignorees });
        }
    }

    /// <summary>Les reglages du tableau : coffres hors replays, quete, chance de la boutique, relecture de tout.</summary>
    public (int, object) Reglages(HttpListenerRequest requete)
    {
        if (requete.ContentLength64 <= 0 || requete.ContentLength64 > TailleMaxReglages) return (400, new { erreur = "Réglages illisibles." });
        JsonDocument document;
        try
        {
            using var lecteur = new StreamReader(requete.InputStream, Encoding.UTF8);
            document = JsonDocument.Parse(lecteur.ReadToEnd());
        }
        catch (JsonException)
        {
            return (400, new { erreur = "Réglages illisibles." });
        }
        using (document)
        {
            var racine = document.RootElement;
            if (racine.ValueKind != JsonValueKind.Object) return (400, new { erreur = "Réglages illisibles." });

            int? Entier(string nom, int min, int max, out bool invalide)
            {
                invalide = false;
                if (!racine.TryGetProperty(nom, out var v) || v.ValueKind == JsonValueKind.Null) return null;
                if (v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var n) && n >= min && n <= max) return n;
                invalide = true;
                return null;
            }
            var avant = Entier("coffresAvant", 0, 10_000_000, out var e1);
            var quete = Entier("quete", 0, 1_000_000, out var e2);
            var cote = Entier("coteBoutique", 1, 1_000_000_000, out var e3);
            if (e1 || e2 || e3) return (400, new { erreur = "Un des nombres n'est pas valable." });
            var changeCote = racine.TryGetProperty("coteBoutique", out _);
            var relire = racine.TryGetProperty("relire", out var r) && r.ValueKind == JsonValueKind.True;
            bool? boutique = racine.TryGetProperty("boutiqueObtenue", out var bo) && bo.ValueKind is JsonValueKind.True or JsonValueKind.False
                ? bo.ValueKind == JsonValueKind.True : null;

            lock (Stockage.Verrou)
            {
                if (avant is { } a) Stockage.Donnees.CoffresAvant = a;
                if (quete is { } q) Stockage.Caler(q);
                if (changeCote) Stockage.Donnees.CoteBoutique = cote == Tresors.Cotes.First(c => c.Type == Tresors.Boutique).UnSur ? null : cote;
                if (boutique == true) Stockage.Donnees.BoutiqueObtenue ??= DateTimeOffset.Now;
                if (boutique == false) Stockage.Donnees.BoutiqueObtenue = null;
                if (relire)
                {
                    // Les replays encore presents sont relus et remplacent leurs anciens chiffres.
                    Stockage.Donnees.FichiersVus.Clear();
                    Stockage.Donnees.Echecs.Clear();
                }
            }
            _moteur.Enregistrer();
            if (relire) _moteur.Reveiller();
            return (200, new { ok = true });
        }
    }

    /// <summary>Toutes les parties comptees, dans un fichier a garder ou a importer sur un autre PC.</summary>
    public byte[] Exporter()
    {
        lock (Stockage.Verrou)
        {
            var d = Stockage.Donnees;
            return JsonSerializer.SerializeToUtf8Bytes(new
            {
                format = "compteur-coffres",
                version = 1,
                exporteLe = DateTimeOffset.Now,
                coffresAvant = d.CoffresAvant,
                calage = d.Calage,
                boutiqueObtenue = d.BoutiqueObtenue,
                parties = d.Parties,
            }, Lisible);
        }
    }

    // Une partie du site Fortnite Chest Counter (Jose-AE) : memes replays, moins de details.
    private static Partie? DepuisSite(JsonElement m)
    {
        if (m.ValueKind != JsonValueKind.Object) return null;
        string? Texte(string nom) => m.TryGetProperty(nom, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
        double Nombre(string nom) => m.TryGetProperty(nom, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetDouble(out var d) ? d : 0;
        int Entier(string nom) => (int)Math.Clamp(Math.Round(Nombre(nom)), 0, 100_000);

        var id = Texte("matchId");
        if (string.IsNullOrWhiteSpace(id) || !DateTimeOffset.TryParse(Texte("date"), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var fin))
            return null;
        return new Partie
        {
            Fichier = Path.GetFileName(Texte("fileName") ?? ""),
            Cle = "site:" + id,
            Fin = fin.ToLocalTime(),
            Mode = (Texte("playlist") ?? "Inconnu").Replace("Playlist_", "", StringComparison.OrdinalIgnoreCase),
            Minutes = Math.Round(Nombre("durationSeconds") / 60, 1),
            Ouverts = Entier("chestCountSearched"),
            CassesAPied = Entier("chestCountBroken"),
            CoffresEquipe = Entier("teamChestCountSearched") + Entier("teamChestCountBroken"),
            PersonnageTrouve = true,
            CoffresVus = 1,
        };
    }

    // Un fichier importe peut venir de n'importe ou : on refuse les valeurs impossibles.
    private static bool Valide(Partie p)
    {
        static bool Petit(int n, int max) => n >= 0 && n <= max;
        p.Fichier = Path.GetFileName(p.Fichier ?? "");
        p.Tresors ??= new();
        p.TresorsAutres ??= new();
        p.ParType ??= new();
        return !string.IsNullOrEmpty(p.Cle) && p.Cle.Length <= 200 && p.Fichier.Length <= 200
            && !string.IsNullOrEmpty(p.Mode) && p.Mode.Length <= 100 && (p.Carte?.Length ?? 0) <= 200
            && p.Fin.Year >= 2017 && p.Fin <= DateTimeOffset.Now.AddDays(1)
            && p.Minutes is >= 0 and <= 300
            && Petit(p.Ouverts, 5000) && Petit(p.Rouverts, 5000) && Petit(p.CassesAPied, 5000) && Petit(p.CassesVoiture, 5000)
            && Petit(p.CassesTombes, 5000) && Petit(p.CoffresEquipe, 20000) && Petit(p.Equipiers, 100)
            && p.ParType.Count <= 100 && p.ParType.All(t => t.Key.Length <= 100 && Petit(t.Value, 5000))
            && p.Tresors.Count <= 100 && p.TresorsAutres.Count <= 1000;
    }
}
