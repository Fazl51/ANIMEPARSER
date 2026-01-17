using ANIMEPARSER.Models;
using ANIMEPARSER.Services;
using ANIMEPARSER.Storage;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace ANIMEPARSER
{
    internal class Program
    {
        private const int TopToDisplay = 50;
        private static readonly string WatchListPath = "Data/watched.txt";
        private static readonly string RecommendationsPath = "Data/recommendations.txt";

        private static async Task Main()
        {
            Console.OutputEncoding = Encoding.UTF8;
            Console.InputEncoding = Encoding.UTF8;

            Console.WriteLine("Добро пожаловать в Anime Recommender CLI!");

            var client = new ShikimoriClient();
            Console.WriteLine("Загружаю данные Shikimori, подождите...");
            var topAnime = await client.GetTopAnimeAsync(150);

            if (topAnime == null || topAnime.Count == 0)
            {
                Console.WriteLine("Не удалось получить список аниме.");
                return;
            }

            var profile = UserProfileStorage.Load() ?? new UserProfile();
            var watched = await CollectWatchedAsync(client, topAnime);
            if (watched.Count == 0)
            {
                Console.WriteLine("Список просмотренных пуст. Попробуйте позже.");
                return;
            }

            Console.WriteLine("\nВы отметили как просмотренные:");
            foreach (var anime in watched)
            {
                Console.WriteLine($"- {anime.Title}");
            }

            AskToSaveWatchList(watched);

            Console.WriteLine("\nЧаще всего встречающиеся жанры:");
            var genres = watched
                .SelectMany(a => a.Genres)
                .GroupBy(g => g)
                .OrderByDescending(g => g.Count());
            foreach (var g in genres)
            {
                Console.WriteLine($"- {g.Key}: {g.Count()}");
            }

            var recommendationLimit = AskRecommendationCount();

            var engine = new RecommendationEngine();
            var recommendations = engine.GetRecommendations(topAnime, watched, profile.GenrePreferences, recommendationLimit);

            if (recommendations.Count == 0)
            {
                Console.WriteLine("\nНе нашлось релевантных рекомендаций, предлагаю случайную подборку:");
                recommendations = topAnime
                    .Where(a => watched.All(w => w.Id != a.Id))
                    .OrderBy(_ => Guid.NewGuid())
                    .Take(recommendationLimit)
                    .Select(a => new RecommendationResult
                    {
                        Anime = a,
                        Score = 0,
                        Explanation = "Случайный выбор",
                        MatchedGenres = new List<string>()
                    })
                    .ToList();
            }

            Console.WriteLine("\nПодборка для вас:");
            foreach (var recommendation in recommendations)
            {
                var reason = !string.IsNullOrWhiteSpace(recommendation.Explanation)
                    ? recommendation.Explanation
                    : recommendation.MatchedGenres.Count > 0
                        ? $"Совпали жанры: {string.Join(", ", recommendation.MatchedGenres)}"
                        : "Популярное аниме";

                Console.WriteLine($"- {recommendation.Anime.Title} (оценка совпадений: {recommendation.Score:F2})");
                Console.WriteLine($"  {reason}");
            }

            AskToSaveRecommendations(recommendations);

            UpdateUserProfile(profile, watched, recommendations);
            UserProfileStorage.Save(profile);
            PrintProfileSummary(profile);
        }

        private static async Task<List<Anime>> CollectWatchedAsync(ShikimoriClient client, List<Anime> topAnime)
        {
            var watched = new List<Anime>();

            while (watched.Count == 0)
            {
                Console.WriteLine("\nВыберите источник просмотренных аниме:");
                Console.WriteLine("1 — выбрать из списка текущего топа");
                Console.WriteLine("2 — загрузить из файла Data/watched.txt");
                Console.WriteLine("3 — объединить файл и ручной выбор");
                Console.Write("Ваш выбор: ");
                var choice = Console.ReadLine()?.Trim();

                switch (choice)
                {
                    case "1":
                        watched = SelectFromTop(topAnime);
                        break;
                    case "2":
                        watched = await LoadFromFileAsync(client, topAnime);
                        break;
                    case "3":
                        var fromFile = await LoadFromFileAsync(client, topAnime);
                        var fromSelection = SelectFromTop(topAnime);
                        watched = MergeAnimeLists(fromFile, fromSelection);
                        break;
                    default:
                        Console.WriteLine("Пожалуйста, выберите 1, 2 или 3.");
                        break;
                }

                if (watched.Count == 0)
                {
                    Console.WriteLine("Не удалось собрать список просмотренного. Попробуйте снова.");
                }
            }

            return watched;
        }

        private static List<Anime> SelectFromTop(List<Anime> topAnime)
        {
            Console.WriteLine("\nТоп-50 аниме по версии Shikimori:");
            for (var i = 0; i < Math.Min(TopToDisplay, topAnime.Count); i++)
            {
                Console.WriteLine($"{i + 1}. {topAnime[i].Title}");
            }

            Console.Write("Введите номера просмотренных через запятую: ");
            var input = Console.ReadLine();
            if (string.IsNullOrWhiteSpace(input))
            {
                return new List<Anime>();
            }

            var indexes = input
                .Split(',', StringSplitOptions.RemoveEmptyEntries)
                .Select(s => int.TryParse(s.Trim(), out var index) ? index : -1)
                .Where(i => i > 0 && i <= Math.Min(TopToDisplay, topAnime.Count))
                .Distinct()
                .ToList();

            var selected = new List<Anime>();
            var seen = new HashSet<int>();

            foreach (var i in indexes)
            {
                var anime = topAnime[i - 1];
                if (seen.Add(anime.Id))
                {
                    selected.Add(anime);
                }
            }

            return selected;
        }

        private static async Task<List<Anime>> LoadFromFileAsync(ShikimoriClient client, List<Anime> topAnime)
        {
            var titles = FileParser.LoadWatchedTitles()
                .Select(t => t.Trim())
                .Where(t => !string.IsNullOrWhiteSpace(t))
                .ToList();

            if (titles.Count == 0)
            {
                Console.WriteLine($"Файл {WatchListPath} пуст или отсутствует.");
                return new List<Anime>();
            }

            Console.WriteLine($"Загружаю данные из {WatchListPath}...");
            var result = new List<Anime>();
            var seen = new HashSet<int>();

            foreach (var title in titles)
            {
                var match = topAnime.FirstOrDefault(a =>
                    string.Equals(a.Title, title, StringComparison.OrdinalIgnoreCase));

                if (match == null)
                {
                    match = await client.FindAnimeByTitleAsync(title);
                    if (match != null && topAnime.All(a => a.Id != match.Id))
                    {
                        topAnime.Add(match);
                    }
                }

                if (match == null)
                {
                    Console.WriteLine($"Не удалось найти аниме \"{title}\".");
                    continue;
                }

                if (seen.Add(match.Id))
                {
                    result.Add(match);
                }
            }

            if (result.Count > 0)
            {
                Console.WriteLine("Загруженные просмотренные:");
                foreach (var anime in result)
                {
                    Console.WriteLine($"- {anime.Title}");
                }
            }

            return result;
        }

        private static List<Anime> MergeAnimeLists(List<Anime> first, List<Anime> second)
        {
            var merged = new List<Anime>();
            var seen = new HashSet<int>();

            foreach (var anime in first.Concat(second))
            {
                if (seen.Add(anime.Id))
                {
                    merged.Add(anime);
                }
            }

            return merged;
        }

        private static int AskRecommendationCount()
        {
            Console.Write("\nСколько рекомендаций показать (по умолчанию 10)? ");
            var input = Console.ReadLine();
            if (int.TryParse(input, out var value) && value > 0 && value <= 30)
            {
                return value;
            }

            return 10;
        }

        private static void AskToSaveWatchList(List<Anime> watched)
        {
            if (watched.Count == 0)
            {
                return;
            }

            Console.Write($"\nСохранить список в {WatchListPath}? (y/n): ");
            var answer = Console.ReadLine()?.Trim().ToLowerInvariant();
            if (string.IsNullOrEmpty(answer))
            {
                return;
            }

            if (answer.StartsWith("y") || answer.StartsWith("д"))
            {
                FileParser.SaveWatchedAnime(watched);
                Console.WriteLine("Список сохранён.");
            }
        }

        private static void AskToSaveRecommendations(List<RecommendationResult> recommendations)
        {
            if (recommendations.Count == 0)
            {
                return;
            }

            Console.Write($"\nСохранить рекомендации в {RecommendationsPath}? (y/n): ");
            var answer = Console.ReadLine()?.Trim().ToLowerInvariant();
            if (string.IsNullOrEmpty(answer))
            {
                return;
            }

            if (!answer.StartsWith("y") && !answer.StartsWith("д"))
            {
                return;
            }

            var directory = Path.GetDirectoryName(RecommendationsPath);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            var lines = recommendations
                .Select(r => $"{r.Anime.Title} — {r.Explanation} (оценка {r.Score:F2})");

            File.WriteAllLines(RecommendationsPath, lines);
            Console.WriteLine("Рекомендации сохранены.");
        }

        private static void UpdateUserProfile(UserProfile profile, List<Anime> watched, List<RecommendationResult> recommendations)
        {
            if (profile == null)
            {
                return;
            }

            var watchedSet = new HashSet<int>(profile.WatchedAnimeIds);

            foreach (var anime in watched)
            {
                var isNew = watchedSet.Add(anime.Id);
                if (isNew)
                {
                    foreach (var genre in anime.Genres)
                    {
                        if (!profile.GenrePreferences.ContainsKey(genre))
                        {
                            profile.GenrePreferences[genre] = 0;
                        }

                        profile.GenrePreferences[genre] += 1;
                    }
                }
            }

            profile.WatchedAnimeIds = watchedSet.OrderBy(id => id).ToList();
            profile.LastRecommendations = recommendations
                .Select(r => r.Anime.Title)
                .ToList();
        }

        private static void PrintProfileSummary(UserProfile profile)
        {
            if (profile == null)
            {
                return;
            }

            Console.WriteLine("\nПрофиль пользователя обновлён:");
            Console.WriteLine($"Общее количество просмотренных: {profile.WatchedAnimeIds.Count}");

            var topGenres = profile.GenrePreferences
                .OrderByDescending(g => g.Value)
                .Take(5)
                .ToList();

            if (topGenres.Count > 0)
            {
                Console.WriteLine("Любимые жанры:");
                foreach (var genre in topGenres)
                {
                    Console.WriteLine($"- {genre.Key}: {genre.Value}");
                }
            }
            else
            {
                Console.WriteLine("Пока нет накопленной статистики по жанрам.");
            }

            Console.WriteLine("Данные сохранены в Storage/user_profile.json");
        }
    }
}
