using System.Reflection;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace CompteurCoffres;

/// <summary>
/// Parties comptees : le Battle Royale classique de la saison (avec ou sans construction). Le classe, OG, Reload,
/// les modes temporaires et le Creatif ne comptent pas. Regle reprise de ChestCounter 1.5 (LED-N, licence MIT).
/// </summary>
internal static class ModesJeu
{
    public const string CarteSaison = "/Delilah/Maps/Delilah_Terrain";

    private static readonly Regex BattleRoyale = new(
        @"^Playlist_(Default(Solo|Duo|Duos|Trio|Trios|Squad|Squads)|Trios|NoBuildBR_(Solo|Duo|Duos|Trio|Trios|Squad|Squads))$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    public static bool Compte(Partie partie) =>
        BattleRoyale.IsMatch("Playlist_" + partie.Mode) && (partie.Carte == null || partie.Carte.Equals(CarteSaison, StringComparison.OrdinalIgnoreCase));

    /// <summary>Pourquoi une partie ne compte pas, ou null si elle compte.</summary>
    public static string? Raison(Partie partie) => Compte(partie) ? null
        : partie.Carte != null && !partie.Carte.Equals(CarteSaison, StringComparison.OrdinalIgnoreCase) && BattleRoyale.IsMatch("Playlist_" + partie.Mode)
            ? "pas sur la carte de la saison"
            : "pas le Battle Royale classique";
}

internal sealed record Cote(string Type, int UnSur, bool Hebdo);
internal sealed record LotConnu(string Type, string Nom, string[] Identifiants);
internal sealed record Semaine(int Numero, DateTime DebutUtc, DateTime FinUtc, string Titre, List<LotConnu> Lots);
internal sealed record HeureTresor(DateTime DebutUtc, DateTime FinUtc, Dictionary<string, double> Bonus, bool Confirmee);

/// <summary>
/// Chasse au tresor de la saison : chances publiees par Epic par coffre ouvert, lots de chaque semaine et
/// "heures au tresor" ou certaines chances sont multipliees. Donnees reprises de ChestCounter (content.json),
/// integrees au programme : il ne telecharge rien.
/// </summary>
internal static class Tresors
{
    public const string Tenue = "outfit", Boutique = "pirate-shop", Voiture = "car", Pioche = "pickaxe", Musique = "jam", Ecran = "loading", Gizmo = "gizmo";

    public static IReadOnlyList<Cote> Cotes { get; }
    public static IReadOnlyList<Semaine> Semaines { get; }
    public static IReadOnlyList<HeureTresor> Heures { get; }
    public static LotConnu AccesBoutique { get; }

    static Tresors()
    {
        using var flux = Assembly.GetExecutingAssembly().GetManifestResourceStream("tresors.json")
            ?? throw new InvalidOperationException("Donnees des tresors introuvables");
        using var doc = JsonDocument.Parse(flux);
        var racine = doc.RootElement;
        static DateTime Date(JsonElement e) => DateTime.Parse(e.GetString()!, System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.AdjustToUniversal | System.Globalization.DateTimeStyles.AssumeUniversal);
        static LotConnu Lot(JsonElement e) => new(e.GetProperty("kind").GetString()!, e.GetProperty("name").GetString() ?? "",
            e.TryGetProperty("prizeIds", out var ids) ? ids.EnumerateArray().Select(x => x.GetString() ?? "").ToArray() : Array.Empty<string>());

        Cotes = racine.GetProperty("odds").EnumerateArray()
            .Select(o => new Cote(o.GetProperty("kind").GetString()!, o.GetProperty("oneIn").GetInt32(), o.TryGetProperty("weekly", out var w) && w.GetBoolean()))
            .ToList();
        AccesBoutique = Lot(racine.GetProperty("pirateShop"));
        Semaines = racine.GetProperty("weeks").EnumerateArray()
            .Select(s => new Semaine(s.GetProperty("number").GetInt32(), Date(s.GetProperty("start")), Date(s.GetProperty("end")),
                s.GetProperty("title").GetString() ?? "", s.GetProperty("prizes").EnumerateArray().Select(Lot).ToList()))
            .ToList();
        Heures = racine.TryGetProperty("treasureHours", out var heures)
            ? heures.EnumerateArray().Select(h => new HeureTresor(Date(h.GetProperty("start")), Date(h.GetProperty("end")),
                h.GetProperty("boosts").EnumerateObject().ToDictionary(b => b.Name, b => b.Value.GetDouble()),
                h.TryGetProperty("confirmed", out var c) && c.GetBoolean())).ToList()
            : new List<HeureTresor>();
    }

    public static string NomType(string type) => type switch
    {
        Tenue => "Tenue en or",
        Boutique => "Accès à la boutique pirate",
        Voiture => "Véhicule et autocollant",
        Pioche => "Pioche ou sac à dos",
        Musique => "Piste musicale",
        Ecran => "Écran de chargement",
        _ => "Gizmo (n'importe lequel)",
    };

    /// <summary>Le lot qu'un identifiant de coffre dore designe, s'il est connu.</summary>
    public static LotConnu? Trouver(string identifiant) =>
        Semaines.SelectMany(s => s.Lots).Prepend(AccesBoutique)
            .FirstOrDefault(l => l.Identifiants.Any(i => string.Equals(i, identifiant, StringComparison.OrdinalIgnoreCase)));

    /// <summary>Multiplicateur d'un type de tresor a un instant (1 hors des heures au tresor).</summary>
    public static double Bonus(string type, DateTime utc) =>
        Heures.FirstOrDefault(h => utc >= h.DebutUtc && utc < h.FinUtc)?.Bonus.GetValueOrDefault(type, 1) is { } b and > 0 ? b : 1;

    public static HeureTresor? HeureEnCoursOuProchaine(DateTime utc) => Heures.Where(h => h.FinUtc > utc).OrderBy(h => h.DebutUtc).FirstOrDefault();

    public static Semaine? SemaineDe(DateTime utc) => Semaines.FirstOrDefault(s => utc >= s.DebutUtc && utc < s.FinUtc);

    /// <summary>
    /// Chance d'avoir obtenu ce tresor au moins une fois : 1 − Π(1 − p × bonus), chaque partie avec le bonus de son heure,
    /// plus des coffres sans date (ajoutes a la main) aux chances normales.
    /// </summary>
    public static double Chance(Cote cote, IEnumerable<(int Coffres, DateTime Utc)> parties, int coffresSansDate)
    {
        var p = 1.0 / cote.UnSur;
        var somme = Math.Max(0, coffresSansDate) * double.LogP1(-p);
        foreach (var (coffres, utc) in parties)
            somme += coffres * double.LogP1(-Math.Min(0.999, p * Bonus(cote.Type, utc)));
        return -double.ExpM1(somme);
    }

    /// <summary>Coffres necessaires pour atteindre une chance donnee (0,5 = une chance sur deux).</summary>
    public static long CoffresPour(int unSur, double chance) => (long)Math.Ceiling(Math.Log(1 - chance) / double.LogP1(-1.0 / unSur));
}
