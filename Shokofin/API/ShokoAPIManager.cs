using MediaBrowser.Controller.Entities;
using MediaBrowser.Model.Entities;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Shokofin.API.Info;
using Shokofin.API.Models;
using Shokofin.Utils;

using ILibraryManager = MediaBrowser.Controller.Library.ILibraryManager;

namespace Shokofin.API
{
    public class ShokoAPIManager
    {
        private readonly ILogger<ShokoAPIManager> Logger;

        private readonly ILibraryManager LibraryManager;

        private static readonly List<Folder> MediaFolderList = new List<Folder>();

        private static readonly ConcurrentDictionary<string, string> SeriesPathToIdDictionary = new ConcurrentDictionary<string, string>();

        private static ConcurrentDictionary<string, string> SeriesIdToGroupIdDictionary = new ConcurrentDictionary<string, string>();

        private static ConcurrentDictionary<string, string> EpisodeIdToSeriesIdDictionary = new ConcurrentDictionary<string, string>();

        /// <summary>
        /// Episodes marked as ignored is skipped when adding missing episode metadata.
        /// </summary>
        private static readonly ConcurrentDictionary<string, ConcurrentDictionary<string, string>> SeriesIdToEpisodeIdIgnoreDictionery = new ConcurrentDictionary<string, ConcurrentDictionary<string, string>>();

        public ShokoAPIManager(ILogger<ShokoAPIManager> logger, ILibraryManager libraryManager)
        {
            Logger = logger;
            LibraryManager = libraryManager;
        }

        private static IMemoryCache DataCache = new MemoryCache(new MemoryCacheOptions() {
            ExpirationScanFrequency = ExpirationScanFrequency,
        });

        private static readonly System.TimeSpan ExpirationScanFrequency = new System.TimeSpan(0, 25, 0);

        private static readonly System.TimeSpan DefaultTimeSpan = new System.TimeSpan(1, 0, 0);

        #region Ignore rule

        public Folder FindMediaFolder(string path, Folder parent, Folder root)
        {
            var mediaFolder = MediaFolderList.Find((folder) => path.StartsWith(folder.Path));
            // Look for the root folder for the current item.
            if (mediaFolder != null) {
                return mediaFolder;
            }
            mediaFolder = parent;
            while (!mediaFolder.ParentId.Equals(root.Id)) {
                if (mediaFolder.Parent == null) {
                    if (mediaFolder.ParentId.Equals(Guid.Empty))
                        break;
                    mediaFolder = LibraryManager.GetItemById(mediaFolder.ParentId) as Folder;
                    continue;
                }
                mediaFolder = mediaFolder.Parent;
            }
            MediaFolderList.Add(mediaFolder);
            return mediaFolder;
        }

        public string StripMediaFolder(string fullPath)
        {
            var mediaFolder = MediaFolderList.Find((folder) => fullPath.StartsWith(folder.Path));
            // If no root folder was found, then we _most likely_ already stripped it out beforehand.
            if (mediaFolder == null || string.IsNullOrEmpty(mediaFolder?.Path))
                return fullPath;
            return fullPath.Substring(mediaFolder.Path.Length);
        }

        #endregion
        #region Clear

        public void Clear()
        {
            Logger.LogDebug("Clearing data.");
            DataCache.Dispose();
            MediaFolderList.Clear();
            EpisodeIdToSeriesIdDictionary.Clear();
            SeriesPathToIdDictionary.Clear();
            SeriesIdToEpisodeIdIgnoreDictionery.Clear();
            SeriesIdToGroupIdDictionary.Clear();
            DataCache = (new MemoryCache((new MemoryCacheOptions() {
                ExpirationScanFrequency = ExpirationScanFrequency,
            })));
        }

        #endregion
        #region People

        public async Task<IEnumerable<PersonInfo>> GetPeople(string seriesId)
        {
            var list = new List<PersonInfo>();
            var roles = await ShokoAPI.GetSeriesCast(seriesId);
            foreach (var role in roles)
            {
                list.Add(new PersonInfo
                {
                    Type = PersonType.Actor,
                    Name = role.Staff.Name,
                    Role = role.Character.Name,
                    ImageUrl = role.Staff.Image?.ToURLString(),
                });
            }
            return list;
        }

        #endregion
        #region Tags

        public async Task<string[]> GetTags(string seriesId)
        {
            return (await ShokoAPI.GetSeriesTags(seriesId, GetTagFilter()))?.Select(tag => tag.Name).ToArray() ?? new string[0];
        }

        /// <summary>
        /// Get the tag filter
        /// </summary>
        /// <returns></returns>
        private int GetTagFilter()
        {
            var config = Plugin.Instance.Configuration;
            var filter = 0;

            if (config.HideAniDbTags) filter = 1;
            if (config.HideArtStyleTags) filter |= (filter << 1);
            if (config.HideSourceTags) filter |= (filter << 2);
            if (config.HideMiscTags) filter |= (filter << 3);
            if (config.HidePlotTags) filter |= (filter << 4);

            return filter;
        }

        #endregion
        #region File Info

        public (FileInfo, EpisodeInfo, SeriesInfo, GroupInfo) GetFileInfoByPathSync(string path, Ordering.GroupFilterType? filterGroupByType)
        {
            return GetFileInfoByPath(path, filterGroupByType).GetAwaiter().GetResult();
        }

        public async Task<(FileInfo, EpisodeInfo, SeriesInfo, GroupInfo)> GetFileInfoByPath(string path, Ordering.GroupFilterType? filterGroupByType)
        {
            var partialPath = StripMediaFolder(path);
            Logger.LogDebug("Looking for file matching {Path}", partialPath);
            var result = await ShokoAPI.GetFileByPath(partialPath);

            var file = result?.FirstOrDefault();
            if (file == null)
                return (null, null, null, null);

            var series = file?.SeriesIDs.FirstOrDefault();
            var seriesId = series?.SeriesID.ID.ToString();
            var episodes = series?.EpisodeIDs?.FirstOrDefault();
            var episodeId = episodes?.ID.ToString();
            if (string.IsNullOrEmpty(seriesId) || string.IsNullOrEmpty(episodeId))
                return (null, null, null, null);

            GroupInfo groupInfo = null;
            if (filterGroupByType != null) {
                groupInfo =  await GetGroupInfoForSeries(seriesId, (Ordering.GroupFilterType)filterGroupByType);
                if (groupInfo == null)
                    return (null, null, null, null);
            }

            var seriesInfo = await GetSeriesInfo(seriesId);
            if (seriesInfo == null)
                return (null, null, null, null);

            var episodeInfo = await GetEpisodeInfo(episodeId);
            if (episodeInfo == null)
                return (null, null, null, null);

            var fileInfo = CreateFileInfo(file, file.ID.ToString(), series?.EpisodeIDs?.Count ?? 0);

            return (fileInfo, episodeInfo, seriesInfo, groupInfo);
        }

        public async Task<FileInfo> GetFileInfo(string fileId)
        {
            var file = await ShokoAPI.GetFile(fileId);
            if (file == null)
                return null;
            return CreateFileInfo(file);
        }

        private FileInfo CreateFileInfo(File file, string fileId = null, int episodeCount = 0)
        {
            if (file == null)
                return null;
            if (string.IsNullOrEmpty(fileId))
                fileId = file.ID.ToString();
            var cacheKey = $"file:{fileId}:{episodeCount}";
            FileInfo info = null;
            if (DataCache.TryGetValue<FileInfo>(cacheKey, out info))
                return info;
            Logger.LogDebug("Creating info object for file. (File={FileId})", fileId);
            info = new FileInfo
            {
                Id = fileId,
                Shoko = file,

            };
            DataCache.Set<FileInfo>(cacheKey, info, DefaultTimeSpan);
            return info;
        }

        #endregion
        #region Episode Info

        public EpisodeInfo GetEpisodeInfoSync(string episodeId)
        {
            if (string.IsNullOrEmpty(episodeId))
                return null;
            if (DataCache.TryGetValue<EpisodeInfo>($"episode:{episodeId}", out var info))
                return info;
            return GetEpisodeInfo(episodeId).GetAwaiter().GetResult();
        }

        public async Task<EpisodeInfo> GetEpisodeInfo(string episodeId)
        {
            if (string.IsNullOrEmpty(episodeId))
                return null;
            if (DataCache.TryGetValue<EpisodeInfo>($"episode:{episodeId}", out var info))
                return info;
            var episode = await ShokoAPI.GetEpisode(episodeId);
            return await CreateEpisodeInfo(episode, episodeId);
        }

        private async Task<EpisodeInfo> CreateEpisodeInfo(Episode episode, string episodeId = null)
        {
            if (episode == null)
                return null;
            if (string.IsNullOrEmpty(episodeId))
                episodeId = episode.IDs.ID.ToString();
            var cacheKey = $"episode:{episodeId}";
            EpisodeInfo info = null;
            if (DataCache.TryGetValue<EpisodeInfo>(cacheKey, out info))
                return info;
            Logger.LogDebug("Creating info object for episode {EpisodeName}. (Episode={EpisodeId})", episode.Name, episodeId);
            var aniDB = (await ShokoAPI.GetEpisodeAniDb(episodeId));
            info = new EpisodeInfo
            {
                Id = episodeId,
                ExtraType = GetExtraType(aniDB),
                Shoko = (await ShokoAPI.GetEpisode(episodeId)),
                AniDB = aniDB,
                TvDB = ((await ShokoAPI.GetEpisodeTvDb(episodeId))?.FirstOrDefault()),
            };
            DataCache.Set<EpisodeInfo>(cacheKey, info, DefaultTimeSpan);
            return info;
        }

        public bool MarkEpisodeAsIgnored(string episodeId, string seriesId, string fullPath)
        {
            if (!(SeriesIdToEpisodeIdIgnoreDictionery.TryGetValue(seriesId, out var dictionary) || SeriesIdToEpisodeIdIgnoreDictionery.TryAdd(seriesId, dictionary = new ConcurrentDictionary<string, string>())))
                return false;
            return dictionary.TryAdd(episodeId, fullPath);
        }

        private static ExtraType? GetExtraType(Episode.AniDB episode)
        {
            switch (episode.Type)
            {
                case EpisodeType.Normal:
                case EpisodeType.Other:
                    return null;
                case EpisodeType.ThemeSong:
                case EpisodeType.OpeningSong:
                case EpisodeType.EndingSong:
                    return ExtraType.ThemeVideo;
                case EpisodeType.Trailer:
                    return ExtraType.Trailer;
                case EpisodeType.Special: {
                    var title = Text.GetTitleByLanguages(episode.Titles, "en") ?? "";
                    // Interview
                    if (title.Contains("interview", System.StringComparison.OrdinalIgnoreCase))
                        return ExtraType.Interview;
                    // Cinema intro/outro
                    if (title.StartsWith("cinema ", System.StringComparison.OrdinalIgnoreCase) &&
                    (title.Contains("intro", System.StringComparison.OrdinalIgnoreCase) || title.Contains("outro", System.StringComparison.OrdinalIgnoreCase)))
                        return ExtraType.Clip;
                    return null;
                }
                default:
                    return ExtraType.Unknown;
            }
        }

        #endregion
        #region Series Info

        public SeriesInfo GetSeriesInfoByPathSync(string path)
        {
            if (SeriesPathToIdDictionary.ContainsKey(path))
            {
                var seriesId = SeriesPathToIdDictionary[path];
                if (DataCache.TryGetValue<SeriesInfo>( $"series:{seriesId}", out var info))
                    return info;
                return GetSeriesInfo(seriesId).GetAwaiter().GetResult();
            }
            return GetSeriesInfoByPath(path).GetAwaiter().GetResult();
        }

        public async Task<SeriesInfo> GetSeriesInfoByPath(string path)
        {
            var partialPath = StripMediaFolder(path);
            Logger.LogDebug("Looking for series matching {Path}", partialPath);
            string seriesId;
            if (SeriesPathToIdDictionary.ContainsKey(partialPath))
            {
                seriesId = SeriesPathToIdDictionary[partialPath];
            }
            else
            {
                var result = await ShokoAPI.GetSeriesPathEndsWith(partialPath);
                seriesId = result?.FirstOrDefault()?.IDs?.ID.ToString();

                SeriesPathToIdDictionary[partialPath] = seriesId;
            }

            if (string.IsNullOrEmpty(seriesId))
                return null;

            if (DataCache.TryGetValue<SeriesInfo>( $"series:{seriesId}", out var info))
                return info;

            var series = await ShokoAPI.GetSeries(seriesId);
            return await CreateSeriesInfo(series, seriesId);
        }

        public async Task<SeriesInfo> GetSeriesInfoFromGroup(string groupId, int seasonNumber, Ordering.GroupFilterType filterByType = Ordering.GroupFilterType.Default)
        {
            var groupInfo = await GetGroupInfo(groupId, filterByType);
            if (groupInfo == null)
                return null;
            int seriesIndex = seasonNumber > 0 ? seasonNumber - 1 : seasonNumber;
            var index = groupInfo.DefaultSeriesIndex + seriesIndex;
            var seriesInfo = groupInfo.SeriesList[index];
            if (seriesInfo == null)
                return null;

            return seriesInfo;
        }
        public SeriesInfo GetSeriesInfoSync(string seriesId)
        {
            if (string.IsNullOrEmpty(seriesId))
                return null;
            if (DataCache.TryGetValue<SeriesInfo>( $"series:{seriesId}", out var info))
                return info;
            var series = ShokoAPI.GetSeries(seriesId).GetAwaiter().GetResult();
            return CreateSeriesInfo(series, seriesId).GetAwaiter().GetResult();
        }

        public async Task<SeriesInfo> GetSeriesInfo(string seriesId)
        {
            if (string.IsNullOrEmpty(seriesId))
                return null;
            if (DataCache.TryGetValue<SeriesInfo>( $"series:{seriesId}", out var info))
                return info;
            var series = await ShokoAPI.GetSeries(seriesId);
            return await CreateSeriesInfo(series, seriesId);
        }

        public SeriesInfo GetSeriesInfoForEpisodeSync(string episodeId)
        {
            if (EpisodeIdToSeriesIdDictionary.ContainsKey(episodeId)) {
                var seriesId = EpisodeIdToSeriesIdDictionary[episodeId];
                if (DataCache.TryGetValue<SeriesInfo>($"series:{seriesId}", out var info))
                    return info;

                return GetSeriesInfo(seriesId).GetAwaiter().GetResult();
            }

            return GetSeriesInfoForEpisode(episodeId).GetAwaiter().GetResult();
        }

        public async Task<SeriesInfo> GetSeriesInfoForEpisode(string episodeId)
        {
            string seriesId;
            if (EpisodeIdToSeriesIdDictionary.ContainsKey(episodeId)) {
                seriesId = EpisodeIdToSeriesIdDictionary[episodeId];
            }
            else {
                var group = await ShokoAPI.GetGroupFromSeries(episodeId);
                if (group == null)
                    return null;
                seriesId = group.IDs.ID.ToString();
            }

            return await GetSeriesInfo(seriesId);
        }

        private async Task<SeriesInfo> CreateSeriesInfo(Series series, string seriesId = null)
        {
            if (series == null)
                return null;

            if (string.IsNullOrEmpty(seriesId))
                seriesId = series.IDs.ID.ToString();

            SeriesInfo info = null;
            var cacheKey = $"series:{seriesId}";
            if (DataCache.TryGetValue<SeriesInfo>(cacheKey, out info))
                return info;
            Logger.LogDebug("Creating info object for series {SeriesName}. (Series={SeriesId})", series.Name, seriesId);

            var aniDb = await ShokoAPI.GetSeriesAniDB(seriesId);
            var tvDbId = series.IDs.TvDB?.FirstOrDefault();
            var episodeCount = 0;
            Dictionary<string, string> filteredSpecialsMapping = new Dictionary<string, string>();
            List<EpisodeInfo> filteredSpecialsList = new List<EpisodeInfo>();

            // The episode list is ordered by air date
            var episodeList = ShokoAPI.GetEpisodesFromSeries(seriesId)
                .ContinueWith(task => Task.WhenAll(task.Result.Select(e => CreateEpisodeInfo(e))))
                .Unwrap()
                .GetAwaiter()
                .GetResult()
                .Where(e => e != null && e.Shoko != null && e.AniDB != null)
                .OrderBy(e => e.AniDB.AirDate)
                .ToList();

            // Iterate over the episodes once and store some values for later use.
            for (var index = 0; index < episodeList.Count; index++) {
                var episode = episodeList[index];
                EpisodeIdToSeriesIdDictionary[episode.Id] = seriesId;
                if (episode.AniDB.Type == EpisodeType.Normal)
                    episodeCount++;
                else if (episode.AniDB.Type == EpisodeType.Special && episode.ExtraType == null) {
                    filteredSpecialsList.Add(episode);
                    var previousEpisode = episodeList
                        .GetRange(0, index)
                        .LastOrDefault(e => e.AniDB.Type == EpisodeType.Normal);
                    if (previousEpisode != null)
                        filteredSpecialsMapping[episode.Id] = previousEpisode.Id;
                }
            }

            // While the filtered specials list is ordered by episode number
            filteredSpecialsList = filteredSpecialsList
                .OrderBy(e => e.AniDB.EpisodeNumber)
                .ToList();

            info = new SeriesInfo {
                Id = seriesId,
                Shoko = series,
                AniDB = aniDb,
                TvDBId = tvDbId != 0 ? tvDbId.ToString() : null,
                TvDB = tvDbId != 0 ? (await ShokoAPI.GetSeriesTvDB(seriesId)).FirstOrDefault() : null,
                EpisodeList = episodeList,
                EpisodeCount = episodeCount,
                SpesialsAnchors = filteredSpecialsMapping,
                SpecialsList = filteredSpecialsList,
            };

            DataCache.Set<SeriesInfo>(cacheKey, info, DefaultTimeSpan);
            return info;
        }

        #endregion
        #region Group Info

        public GroupInfo GetGroupInfoByPathSync(string path, Ordering.GroupFilterType filterByType = Ordering.GroupFilterType.Default)
        {
            return GetGroupInfoByPath(path, filterByType).GetAwaiter().GetResult();
        }

        public async Task<GroupInfo> GetGroupInfoByPath(string path, Ordering.GroupFilterType filterByType = Ordering.GroupFilterType.Default)
        {
            var partialPath = StripMediaFolder(path);
            Logger.LogDebug("Looking for group matching {Path}", partialPath);
            var result = await ShokoAPI.GetSeriesPathEndsWith(partialPath);

            var seriesId = result?.FirstOrDefault()?.IDs?.ID.ToString();
            if (string.IsNullOrEmpty(seriesId))
                return null;

            var groupInfo = await GetGroupInfoForSeries(seriesId, filterByType);
            if (groupInfo == null)
                return null;

            return groupInfo;
        }

        public async Task<GroupInfo> GetGroupInfo(string groupId, Ordering.GroupFilterType filterByType = Ordering.GroupFilterType.Default)
        {
            if (string.IsNullOrEmpty(groupId))
                return null;

            if (DataCache.TryGetValue<GroupInfo>($"group:{filterByType}:{groupId}", out var info))
                return info;

            var group = await ShokoAPI.GetGroup(groupId);
            return await CreateGroupInfo(group, groupId, filterByType);
        }

        public GroupInfo GetGroupInfoForSeriesSync(string seriesId, Ordering.GroupFilterType filterByType = Ordering.GroupFilterType.Default)
        {
            if (SeriesIdToGroupIdDictionary.ContainsKey(seriesId)) {
                var groupId = SeriesIdToGroupIdDictionary[seriesId];
                if (DataCache.TryGetValue<GroupInfo>($"group:{filterByType}:{groupId}", out var info))
                    return info;

                return GetGroupInfo(groupId, filterByType).GetAwaiter().GetResult();
            }

            return GetGroupInfoForSeries(seriesId, filterByType).GetAwaiter().GetResult();
        }

        public async Task<GroupInfo> GetGroupInfoForSeries(string seriesId, Ordering.GroupFilterType filterByType = Ordering.GroupFilterType.Default)
        {
            string groupId;
            if (SeriesIdToGroupIdDictionary.ContainsKey(seriesId)) {
                groupId = SeriesIdToGroupIdDictionary[seriesId];
            }
            else {
                var group = await ShokoAPI.GetGroupFromSeries(seriesId);
                if (group == null)
                    return null;
                groupId = group.IDs.ID.ToString();
            }

            return await GetGroupInfo(groupId, filterByType);
        }

        private async Task<GroupInfo> CreateGroupInfo(Group group, string groupId, Ordering.GroupFilterType filterByType)
        {
            if (group == null)
                return null;

            if (string.IsNullOrEmpty(groupId))
                groupId = group.IDs.ID.ToString();

            var cacheKey = $"group:{filterByType}:{groupId}";
            GroupInfo groupInfo = null;
            if (DataCache.TryGetValue<GroupInfo>(cacheKey, out groupInfo))
                return groupInfo;
            Logger.LogDebug("Creating info object for group {GroupName}. (Group={GroupId})", group.Name, groupId);

            var seriesList = (await ShokoAPI.GetSeriesInGroup(groupId)
                .ContinueWith(task => Task.WhenAll(task.Result.Select(s => CreateSeriesInfo(s))))
                .Unwrap())
                .Where(s => s != null)
                .ToList();
            if (seriesList != null && seriesList.Count > 0)  switch (filterByType) {
                default:
                    break;
                case Ordering.GroupFilterType.Movies:
                    seriesList = seriesList.Where(s => s.AniDB.Type == SeriesType.Movie).ToList();
                    break;
                case Ordering.GroupFilterType.Others:
                    seriesList = seriesList.Where(s => s.AniDB.Type != SeriesType.Movie).ToList();
                    break;
            }

            // Return ealty if no series matched the filter or if the list was empty.
            if (seriesList == null || seriesList.Count == 0)
                return null;

            // Order series list
            var orderingType = filterByType == Ordering.GroupFilterType.Movies ? Plugin.Instance.Configuration.MovieOrdering : Plugin.Instance.Configuration.SeasonOrdering;
            switch (orderingType) {
                case Ordering.OrderType.Default:
                    break;
                case Ordering.OrderType.ReleaseDate:
                    seriesList = seriesList.OrderBy(s => s?.AniDB?.AirDate ?? System.DateTime.MaxValue).ToList();
                    break;
                // Should not be selectable unless a user fiddles with DevTools in the browser to select the option.
                case Ordering.OrderType.Chronological:
                    throw new System.Exception("Not implemented yet");
            }

            // Select the targeted id if a group spesify a default series.
            int foundIndex = -1;
            int targetId = (group.IDs.DefaultSeries ?? 0);
            if (targetId != 0)
                foundIndex = seriesList.FindIndex(s => s.Shoko.IDs.ID == targetId);
            // Else select the default series as first-to-be-released.
            else switch (orderingType) {
                // The list is already sorted by release date, so just return the first index.
                case Ordering.OrderType.ReleaseDate:
                    foundIndex = 0;
                    break;
                // We don't know how Shoko may have sorted it, so just find the earliest series
                case Ordering.OrderType.Default:
                // We can't be sure that the the series in the list was _released_ chronologically, so find the earliest series, and use that as a base.
                case Ordering.OrderType.Chronological: {
                    var earliestSeries = seriesList.Aggregate((cur, nxt) => (cur == null || (nxt?.AniDB.AirDate ?? System.DateTime.MaxValue) < (cur.AniDB.AirDate ?? System.DateTime.MaxValue)) ? nxt : cur);
                    foundIndex = seriesList.FindIndex(s => s == earliestSeries);
                    break;
                }
            }

            // Throw if we can't get a base point for seasons.
            if (foundIndex == -1)
                throw new System.Exception("Unable to get a base-point for seasions withing the group");

            groupInfo = new GroupInfo {
                Id = groupId,
                Shoko = group,
                SeriesList = seriesList,
                DefaultSeries = seriesList[foundIndex],
                DefaultSeriesIndex = foundIndex,
            };
            foreach (var series in seriesList)
                SeriesIdToGroupIdDictionary[series.Id] = groupId;
            DataCache.Set<GroupInfo>(cacheKey, groupInfo, DefaultTimeSpan);
            return groupInfo;
        }

        #endregion
        #region Post Process Library Changes

        public Task PostProcess(IProgress<double> progress, CancellationToken token)
        {
            Logger.LogInformation("Hi");
            return Task.CompletedTask;
        }

        #endregion
    }
}
