using System.Reflection;
using System.Text.Json;

namespace CompteurCoffres;

internal static class Tableau
{
    public static string Chemin(Stockage stockage) => Path.Combine(stockage.Dossier, "tableau.html");

    /// <summary>Regenere la page du tableau de bord a partir des donnees enregistrees.</summary>
    public static void Ecrire(Stockage stockage)
    {
        var parties = stockage.Donnees.Parties;
        var comptees = stockage.Comptees();
        var calage = stockage.Donnees.Calage;
        var maintenant = DateTime.UtcNow;
        // Seules les parties recentes pesent sur le total : les anciennes sont couvertes par le calage.
        var recentes = calage != null ? comptees.Where(p => p.Fin > calage.Date) : comptees.TakeLast(20);

        string Nom(string identifiant) => Tresors.Trouver(identifiant)?.Nom ?? "Lot inconnu";
        string TypeDe(string identifiant) => Tresors.Trouver(identifiant)?.Type ?? "inconnu";
        var heure = Tresors.HeureEnCoursOuProchaine(maintenant);
        var semaine = Tresors.SemaineDe(maintenant);
        var coffresDates = stockage.CoffresDates().ToList();

        var donnees = new
        {
            genereLe = DateTimeOffset.Now,
            total = stockage.Total(),
            quete = stockage.Quete(),
            objectif = Stockage.ObjectifQuete,
            coffresAvant = stockage.Donnees.CoffresAvant,
            calage = calage == null ? null : new { date = calage.Date, valeur = calage.Valeur },
            escouade = stockage.Donnees.Fenetre.Escouade ? new { total = comptees.Sum(p => p.Total + p.CoffresEquipe) + stockage.Donnees.CoffresAvant } : null,
            parties = parties.Select(p => new
            {
                fin = p.Fin,
                mode = p.Mode,
                minutes = p.Minutes,
                classement = p.Classement,
                eliminations = p.Eliminations,
                ouverts = p.Ouverts,
                casses = p.Casses,
                aPied = p.CassesAPied,
                voiture = p.CassesVoiture,
                tombes = p.CassesTombes,
                total = p.Total,
                equipe = p.CoffresEquipe,
                types = p.ParType,
                compte = p.Comptee,
                raison = ModesJeu.Raison(p),
                tresor = p.TresorRecu,
                lots = p.Tresors.Select(Nom).ToList(),
            }),
            chances = new
            {
                liste = Tresors.Cotes.Select(c => new
                {
                    type = c.Type,
                    nom = Tresors.NomType(c.Type),
                    unSur = c.UnSur,
                    hebdo = c.Hebdo,
                    chance = Tresors.Chance(c, coffresDates, stockage.Donnees.CoffresAvant),
                    pour50 = Tresors.CoffresPour(c.UnSur, 0.5),
                    bonus = Tresors.Bonus(c.Type, maintenant),
                }),
                heure = heure == null ? null : new
                {
                    debut = new DateTimeOffset(heure.DebutUtc, TimeSpan.Zero),
                    fin = new DateTimeOffset(heure.FinUtc, TimeSpan.Zero),
                    enCours = maintenant >= heure.DebutUtc,
                    confirmee = heure.Confirmee,
                    bonus = heure.Bonus.Where(b => b.Value > 1).Select(b => new { nom = Tresors.NomType(b.Key), fois = b.Value }),
                },
                semaine = semaine == null ? null : new
                {
                    numero = semaine.Numero,
                    titre = semaine.Titre,
                    fin = new DateTimeOffset(semaine.FinUtc, TimeSpan.Zero),
                    lots = semaine.Lots.Select(l => new { nom = l.Nom, type = Tresors.NomType(l.Type) }),
                },
            },
            collection = parties.SelectMany(p => p.Tresors.Select(i => new { nom = Nom(i), type = Tresors.NomType(TypeDe(i)), date = p.Fin })),
            partiesTresor = comptees.Count(p => p.TresorRecu),
            autres = parties.SelectMany(p => p.TresorsAutres).GroupBy(i => Nom(i)).Select(g => new { nom = g.Key, type = Tresors.NomType(TypeDe(g.First())), nombre = g.Count() }),
            echecs = stockage.Donnees.Echecs.Where(e => e.Essais >= Moteur.EssaisMax).Select(e => new { fichier = e.Fichier, message = e.Message }),
            suspectes = recentes.Count(EstSuspecte),
        };

        using var ressource = Assembly.GetExecutingAssembly().GetManifestResourceStream("tableau.html")
            ?? throw new InvalidOperationException("Modele du tableau introuvable");
        using var lecteur = new StreamReader(ressource);
        var page = lecteur.ReadToEnd().Replace("/*DONNEES*/null", JsonSerializer.Serialize(donnees));

        var chemin = Chemin(stockage);
        var temporaire = chemin + ".tmp";
        File.WriteAllText(temporaire, page);
        File.Move(temporaire, chemin, overwrite: true);
    }

    private static bool EstSuspecte(Partie partie) =>
        !partie.PersonnageTrouve || (partie.CoffresVus == 0 && partie.Minutes >= 2);
}
