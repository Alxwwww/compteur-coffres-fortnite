using System.Text.Json;

namespace CompteurCoffres;

public sealed class Calage
{
    // Valeur lue sur la quete par le joueur, et date de cette lecture.
    public DateTimeOffset Date { get; set; }
    public int Valeur { get; set; }
}

public sealed class Echec
{
    public string Fichier { get; set; } = "";
    public int Essais { get; set; }
    public DateTimeOffset DernierEssai { get; set; }
    public string Message { get; set; } = "";
}

public sealed class ReglagesFenetre
{
    public bool Visible { get; set; } = true;
    public bool Verrouillee { get; set; }
    // Total d'escouade (tes coffres + ceux de tes coequipiers) dans la petite fenetre.
    public bool Escouade { get; set; }
    public int? X { get; set; }
    public int? Y { get; set; }
}

public sealed class Donnees
{
    public int VersionAnalyse { get; set; }
    // Absent tant que le joueur n'a pas indique la valeur de sa quete.
    public Calage? Calage { get; set; }
    // Coffres ouverts avant d'utiliser le compteur, ajoutes a la main.
    public int CoffresAvant { get; set; }
    // Chance d'avoir l'acces a la boutique par coffre (1 sur N), si le joueur la change dans les reglages du tableau.
    public int? CoteBoutique { get; set; }
    // Le joueur a indique avoir eu l'acces a la boutique (le replay ne dit pas toujours quel tresor a ete remis).
    public DateTimeOffset? BoutiqueObtenue { get; set; }
    public ReglagesFenetre Fenetre { get; set; } = new();
    public List<Partie> Parties { get; set; } = new();
    public List<Echec> Echecs { get; set; } = new();
    // Replays deja lus, pour ne jamais relire ni recompter un fichier.
    public HashSet<string> FichiersVus { get; set; } = new();
}

/// <summary>Tout ce que la petite fenetre affiche, calcule d'un coup.</summary>
public sealed record Resume(int Total, int? Quete, int? DernierePartie, int SessionCoffres, int SessionParties,
    int Casses, int CassesAPied, int CassesVoiture, int Parties, double ChanceBoutique, int? TotalEquipe, int? DerniereEquipe,
    bool BoutiqueObtenue = false);

public sealed class Stockage
{
    // A augmenter quand le comptage change : les replays encore presents sont alors relus une fois.
    private const int VersionAnalyse = 5;
    public const int ObjectifQuete = 1000;
    private static readonly TimeSpan EcartSession = TimeSpan.FromMinutes(90);
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = false };

    private readonly string _chemin;

    public Stockage(string dossier)
    {
        Dossier = dossier;
        Directory.CreateDirectory(dossier);
        _chemin = Path.Combine(dossier, "donnees.json");
        PremierLancement = !File.Exists(_chemin);
        Donnees = PremierLancement
            ? new Donnees()
            : JsonSerializer.Deserialize<Donnees>(File.ReadAllText(_chemin), Options) ?? new Donnees();
        Donnees.Fenetre ??= new ReglagesFenetre();
        if (Donnees.VersionAnalyse < VersionAnalyse)
        {
            Donnees.FichiersVus.Clear();
            Donnees.VersionAnalyse = VersionAnalyse;
        }
    }

    public string Dossier { get; }
    public Donnees Donnees { get; }
    public bool PremierLancement { get; }

    /// <summary>Le lecteur de replays et la fenetre modifient les donnees depuis deux fils differents.</summary>
    public object Verrou { get; } = new();

    /// <summary>Les parties qui comptent : le Battle Royale classique de la saison.</summary>
    public List<Partie> Comptees() => Donnees.Parties.Where(p => p.Comptee).ToList();

    public void Ajouter(Partie partie)
    {
        Donnees.FichiersVus.Add(partie.Fichier);
        Donnees.Echecs.RemoveAll(e => e.Fichier == partie.Fichier);
        // Un replay renomme par Fortnite ou relu garde la meme cle : on remplace, on ne compte pas deux fois.
        // Une partie importee porte le nom de son replay : la lecture de ce replay la remplace.
        var existante = Donnees.Parties.FindIndex(p => p.Cle == partie.Cle || (partie.Fichier != "" && p.Fichier == partie.Fichier));
        if (existante >= 0) Donnees.Parties[existante] = partie;
        else Donnees.Parties.Add(partie);
        Donnees.Parties.Sort((a, b) => a.Fin.CompareTo(b.Fin));
    }

    /// <summary>Ajoute les parties d'une sauvegarde importee qui ne sont pas deja la.</summary>
    public (int Ajoutees, int Ignorees) Fusionner(IEnumerable<Partie> importees)
    {
        int ajoutees = 0, ignorees = 0;
        foreach (var partie in importees)
        {
            if (Donnees.Parties.Any(p => p.Cle == partie.Cle || (partie.Fichier != "" && p.Fichier == partie.Fichier))) { ignorees++; continue; }
            Donnees.Parties.Add(partie);
            ajoutees++;
        }
        Donnees.Parties.Sort((a, b) => a.Fin.CompareTo(b.Fin));
        return (ajoutees, ignorees);
    }

    /// <summary>La chance par coffre d'avoir l'acces a la boutique : celle d'Epic, ou celle choisie dans les reglages.</summary>
    internal Cote CoteBoutique()
    {
        var defaut = Tresors.Cotes.First(c => c.Type == Tresors.Boutique);
        return Donnees.CoteBoutique is int n && n > 0 ? defaut with { UnSur = n } : defaut;
    }

    public void NoterEchec(string fichier, string message, bool definitif = false)
    {
        var echec = Donnees.Echecs.FirstOrDefault(e => e.Fichier == fichier);
        if (echec == null) Donnees.Echecs.Add(echec = new Echec { Fichier = fichier });
        echec.Essais = definitif ? Math.Max(echec.Essais + 1, Moteur.EssaisMax) : echec.Essais + 1;
        echec.DernierEssai = DateTimeOffset.Now;
        echec.Message = message.Length > 200 ? message[..200] : message;
    }

    public void Sauvegarder()
    {
        var temporaire = _chemin + ".tmp";
        File.WriteAllText(temporaire, JsonSerializer.Serialize(Donnees, Options));
        File.Move(temporaire, _chemin, overwrite: true);
    }

    /// <summary>Le joueur indique ce que sa quete affiche : le compteur s'aligne dessus a partir de maintenant.</summary>
    public void Caler(int valeur) => Donnees.Calage = new Calage { Date = DateTimeOffset.Now, Valeur = valeur };

    /// <summary>Valeur probable de la quete (elle ne compte que les coffres ouverts), si le joueur l'a indiquee.</summary>
    public int? Quete() => Donnees.Calage is { } calage
        ? calage.Valeur + Comptees().Where(p => p.Fin > calage.Date).Sum(p => p.Ouverts)
        : null;

    /// <summary>Tous les coffres : la quete et les coffres casses, ou a defaut tout ce que les replays contiennent, plus ceux d'avant.</summary>
    public int Total() => (Quete() is { } quete ? quete + Comptees().Sum(p => p.Casses) : Comptees().Sum(p => p.Total)) + Donnees.CoffresAvant;

    /// <summary>Les dernieres parties jouees a la suite (moins d'1 h 30 entre deux), si la derniere date de moins de 3 h.</summary>
    public List<Partie> Session()
    {
        var parties = Comptees();
        if (parties.Count == 0 || DateTimeOffset.Now - parties[^1].Fin > TimeSpan.FromHours(3)) return new();
        var debut = parties.Count - 1;
        while (debut > 0 && parties[debut].Fin - parties[debut - 1].Fin <= EcartSession) debut--;
        return parties.Skip(debut).ToList();
    }

    /// <summary>Tes parties pour le calcul des chances : chaque coffre avec l'heure de sa partie (heures au tresor).</summary>
    /// <param name="equipe">Ajoute les coffres de tes coequipiers : un tresor trouve par l'un d'eux est remis a toute l'escouade.</param>
    public IEnumerable<(int Coffres, DateTime Utc)> CoffresDates(bool equipe = false) =>
        Comptees().Select(p => (p.Total + (equipe ? p.CoffresEquipe : 0), p.Fin.UtcDateTime));

    public Resume Resumer()
    {
        lock (Verrou)
        {
            var session = Session();
            var parties = Comptees();
            var coteBoutique = CoteBoutique();
            var escouade = Donnees.Fenetre.Escouade;
            return new Resume(
                Total(),
                Quete(),
                parties.Count > 0 ? parties[^1].Total : null,
                session.Sum(p => p.Total),
                session.Count,
                parties.Sum(p => p.Casses),
                parties.Sum(p => p.CassesAPied),
                parties.Sum(p => p.CassesVoiture),
                parties.Count,
                Tresors.Chance(coteBoutique, CoffresDates(), Donnees.CoffresAvant),
                escouade ? parties.Sum(p => p.Total + p.CoffresEquipe) + Donnees.CoffresAvant : null,
                escouade && parties.Count > 0 ? parties[^1].Total + parties[^1].CoffresEquipe : null,
                Donnees.BoutiqueObtenue != null);
        }
    }
}
