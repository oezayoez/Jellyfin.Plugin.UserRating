using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using Jellyfin.Plugin.UserRatings.Models;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;

namespace Jellyfin.Plugin.UserRatings.Data
{
    public class RatingRepository
    {
        private readonly string _dataPath;
        private Dictionary<string, UserRating> _ratings = new();
        private readonly object _lock = new object();
        private readonly ILibraryManager _libraryManager;
        private bool _backfillDone = false;

        public RatingRepository(IApplicationPaths appPaths, ILibraryManager libraryManager)
        {
            _dataPath = Path.Combine(appPaths.PluginConfigurationsPath, "UserRatings", "ratings.json");
            Directory.CreateDirectory(Path.GetDirectoryName(_dataPath)!);
            _libraryManager = libraryManager;
            LoadRatings();
        }

        private void BackfillProviderIds()
        {
            lock (_lock)
            {
                bool needsSave = false;

                foreach (var rating in _ratings.Values)
                {
                    if (rating.ProviderIds != null && rating.ProviderIds.Count > 0) continue;

                    var item = _libraryManager.GetItemById(rating.ItemId);
                    if (item?.ProviderIds == null) continue;

                    rating.ProviderIds = new Dictionary<string, string>(item.ProviderIds);
                    needsSave = true;
                }

                if (needsSave) SaveRatings();
            }
        }

        private void LoadRatings()
        {
            lock (_lock)
            {
                try
                {
                    if (File.Exists(_dataPath))
                    {
                        var json = File.ReadAllText(_dataPath);
                        _ratings = JsonSerializer.Deserialize<Dictionary<string, UserRating>>(json) ?? new();
                    }
                }
                catch (Exception)
                {
                    _ratings = new Dictionary<string, UserRating>();
                }
            }
        }

        private void SaveRatings()
        {
            lock (_lock)
            {
                try
                {
                    var json = JsonSerializer.Serialize(_ratings, new JsonSerializerOptions { WriteIndented = true });
                    File.WriteAllText(_dataPath, json);
                }
                catch (Exception)
                {
                    // Log error
                }
            }
        }

        private static string GetKey(Guid itemId, Guid userId) => $"{itemId}_{userId}";

        private Guid ResolveItemId(Guid itemId)
        {
            bool hasRatings;
            lock (_lock)
            {
                hasRatings = _ratings.Values.Any(r => r.ItemId == itemId);
            }

            if (hasRatings) return itemId;

            var newItem = _libraryManager.GetItemById(itemId);
            if (newItem?.ProviderIds == null || newItem.ProviderIds.Count == 0)
                return itemId;

            lock (_lock)
            {
                foreach (var kv in newItem.ProviderIds)
                {
                    var match = _ratings.Values.FirstOrDefault(r =>
                        r.ProviderIds != null &&
                        r.ProviderIds.TryGetValue(kv.Key, out var val) &&
                        val == kv.Value);

                    if (match == null) continue;

                    var oldId = match.ItemId;
                    var staleEntries = _ratings.Where(e => e.Value.ItemId == oldId).ToList();
                    foreach (var entry in staleEntries)
                    {
                        _ratings.Remove(entry.Key);
                        entry.Value.ItemId = itemId;
                        _ratings[GetKey(itemId, entry.Value.UserId)] = entry.Value;
                    }
                    SaveRatings();
                    return itemId;
                }
            }

            return itemId;
        }

        public void SaveRating(UserRating rating)
        {
            lock (_lock)
            {
                var key = GetKey(rating.ItemId, rating.UserId);
                _ratings[key] = rating;
                SaveRatings();
            }
        }

        public UserRating? GetRating(Guid itemId, Guid userId)
        {
            itemId = ResolveItemId(itemId);
            lock (_lock)
            {
                var key = GetKey(itemId, userId);
                return _ratings.TryGetValue(key, out var rating) ? rating : null;
            }
        }

        public List<UserRating> GetRatingsForItem(Guid itemId)
        {
            itemId = ResolveItemId(itemId);
            lock (_lock)
            {
                return _ratings.Values
                    .Where(r => r.ItemId == itemId)
                    .OrderByDescending(r => r.Timestamp)
                    .ToList();
            }
        }

        public List<UserRating> GetRatingsForUser(Guid userId)
        {
            lock (_lock)
            {
                return _ratings.Values
                    .Where(r => r.UserId == userId)
                    .OrderByDescending(r => r.Timestamp)
                    .ToList();
            }
        }

        public void DeleteRating(Guid itemId, Guid userId)
        {
            itemId = ResolveItemId(itemId);
            lock (_lock)
            {
                var key = GetKey(itemId, userId);
                _ratings.Remove(key);
                SaveRatings();
            }
        }

        public RatingStats GetStatsForItem(Guid itemId)
        {
            itemId = ResolveItemId(itemId);
            lock (_lock)
            {
                var ratings = _ratings.Values.Where(r => r.ItemId == itemId).ToList();

                return new RatingStats
                {
                    AverageRating = ratings.Any() ? ratings.Average(r => r.Rating) : 0,
                    TotalRatings = ratings.Count,
                    UserRatings = ratings.ToDictionary(r => r.UserId, r => r)
                };
            }
        }

        public void DeleteAllRatings()
        {
            lock (_lock)
            {
                _ratings.Clear();
                SaveRatings();
            }
        }

        public List<RatedItemSummary> GetAllRatedItems()
        {
            lock (_lock)
            {
                if (!_backfillDone)
                {
                    _backfillDone = true;
                    BackfillProviderIds();
                }

                bool needsSave = false;

                var staleGroups = _ratings.Values
                    .GroupBy(r => r.ItemId)
                    .Where(g => _libraryManager.GetItemById(g.Key) == null)
                    .ToList();


                foreach (var group in staleGroups)
                {
                    var providerIds = group
                        .FirstOrDefault(r => r.ProviderIds is { Count: > 0 })
                        ?.ProviderIds;

                    if (providerIds == null) continue;

                    BaseItem? match = null;
                    foreach (var kv in providerIds)
                    {
                        var query = new MediaBrowser.Controller.Entities.InternalItemsQuery
                        {
                            HasAnyProviderId = new Dictionary<string, string> { { kv.Key, kv.Value } }
                        };
                        match = _libraryManager.GetItemsResult(query).Items.FirstOrDefault();
                        if (match != null) break;
                    }

                    if (match == null) continue;

                    var oldEntries = _ratings
                        .Where(kv => kv.Value.ItemId == group.Key)
                        .ToList();

                    foreach (var entry in oldEntries)
                    {
                        _ratings.Remove(entry.Key);
                        entry.Value.ItemId = match.Id;
                        _ratings[GetKey(match.Id, entry.Value.UserId)] = entry.Value;
                    }

                    needsSave = true;
                }

                if (needsSave) SaveRatings();

                return _ratings.Values
                    .GroupBy(r => r.ItemId)
                    .Select(g => new RatedItemSummary
                    {
                        ItemId = g.Key,
                        AverageRating = g.Average(r => r.Rating),
                        TotalRatings = g.Count(),
                        LastRated = g.Max(r => r.Timestamp)
                    })
                    .OrderByDescending(s => s.LastRated)
                    .ToList();
            }
        }
    }
}