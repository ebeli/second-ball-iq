using MatchLens.Contracts;

namespace MatchLens.Forge;

/// <summary>Two fictional clubs, eleven starters and three substitutes each. Names are invented; any resemblance to real clubs or people is unintended.</summary>
public static class Rosters
{
    public static TeamInfo HarborCity { get; } = new("team-har", "Harbor City", "HAR", "#1F4E8C",
    [
        new("har-01", "Tomas Reyes", 1, PlayerPosition.Goalkeeper),
        new("har-02", "Eli Nakamura", 2, PlayerPosition.Defender),
        new("har-04", "Jonah Brandt", 4, PlayerPosition.Defender),
        new("har-05", "Sami Virtanen", 5, PlayerPosition.Defender),
        new("har-03", "Kofi Mensah", 3, PlayerPosition.Defender),
        new("har-06", "Mira Okafor", 6, PlayerPosition.Midfielder),
        new("har-08", "Lucas Ferreira", 8, PlayerPosition.Midfielder),
        new("har-10", "Aino Lehto", 10, PlayerPosition.Midfielder),
        new("har-07", "Dario Costa", 7, PlayerPosition.Forward),
        new("har-09", "Ravi Sharma", 9, PlayerPosition.Forward),
        new("har-11", "Noah Lindqvist", 11, PlayerPosition.Forward),
        new("har-14", "Teo Halonen", 14, PlayerPosition.Midfielder, Starter: false),
        new("har-17", "Marcus Adeyemi", 17, PlayerPosition.Forward, Starter: false),
        new("har-15", "Pablo Ruiz", 15, PlayerPosition.Defender, Starter: false),
    ]);

    public static TeamInfo Northvale { get; } = new("team-nor", "Northvale", "NOR", "#8C1D40",
    [
        new("nor-01", "Ivo Petrov", 1, PlayerPosition.Goalkeeper),
        new("nor-02", "Leo Hartmann", 2, PlayerPosition.Defender),
        new("nor-05", "Mateo Silva", 5, PlayerPosition.Defender),
        new("nor-04", "Oskar Nieminen", 4, PlayerPosition.Defender),
        new("nor-03", "Yusuf Demir", 3, PlayerPosition.Defender),
        new("nor-08", "Felix Moreau", 8, PlayerPosition.Midfielder),
        new("nor-06", "Emma Kowalski", 6, PlayerPosition.Midfielder),
        new("nor-10", "Hugo Laine", 10, PlayerPosition.Midfielder),
        new("nor-07", "Ken Watanabe", 7, PlayerPosition.Forward),
        new("nor-09", "Arlo Quinn", 9, PlayerPosition.Forward),
        new("nor-11", "Vilja Korhonen", 11, PlayerPosition.Forward),
        new("nor-19", "Elias Varga", 19, PlayerPosition.Forward, Starter: false),
        new("nor-14", "Jonas Berg", 14, PlayerPosition.Midfielder, Starter: false),
        new("nor-15", "Ari Salonen", 15, PlayerPosition.Defender, Starter: false),
    ]);
}
