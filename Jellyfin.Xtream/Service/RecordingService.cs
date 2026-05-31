// Copyright (C) 2022  Kevin Jilissen

// This program is free software: you can redistribute it and/or modify
// it under the terms of the GNU General Public License as published by
// the Free Software Foundation, either version 3 of the License, or
// (at your option) any later version.

// This program is distributed in the hope that it will be useful,
// but WITHOUT ANY WARRANTY; without even the implied warranty of
// MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
// GNU General Public License for more details.

// You should have received a copy of the GNU General Public License
// along with this program.  If not, see <https://www.gnu.org/licenses/>.

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Net;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.LiveTv;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.IO;
using MediaBrowser.Model.LiveTv;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Xtream.Service;

/// <summary>
/// A minimal DVR implementation for the Xtream Live TV service.
/// Jellyfin only performs recordings for its internal EmbyTV service, so a third-party
/// <see cref="ILiveTvService"/> has to persist its own timers, schedule them, capture the
/// stream to a file and hand the result to the library. This service does exactly that for
/// single (one-off) recordings, reusing Jellyfin's configured Live TV recording folder.
/// </summary>
public sealed class RecordingService : IDisposable
{
    private const int BufferSize = 81920;

    private static readonly HttpStatusCode[] _redirects =
    [
        HttpStatusCode.Moved,
        HttpStatusCode.MovedPermanently,
        HttpStatusCode.PermanentRedirect,
        HttpStatusCode.Redirect,
    ];

    private readonly ILogger<RecordingService> _logger;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IConfigurationManager _configurationManager;
    private readonly ILibraryManager _libraryManager;
    private readonly ILibraryMonitor _libraryMonitor;
    private readonly IProviderManager _providerManager;
    private readonly IFileSystem _fileSystem;

    private readonly object _timersLock = new();
    private readonly ConcurrentDictionary<string, Timer> _systemTimers = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, CancellationTokenSource> _activeRecordings = new(StringComparer.OrdinalIgnoreCase);
    private readonly string _dataPath;
    private readonly JsonSerializerOptions _jsonOptions = new() { WriteIndented = true };

    private List<TimerInfo>? _timers;

    /// <summary>
    /// Initializes a new instance of the <see cref="RecordingService"/> class.
    /// </summary>
    /// <param name="logger">Instance of the <see cref="ILogger{RecordingService}"/> interface.</param>
    /// <param name="httpClientFactory">Instance of the <see cref="IHttpClientFactory"/> interface.</param>
    /// <param name="configurationManager">Instance of the <see cref="IConfigurationManager"/> interface.</param>
    /// <param name="libraryManager">Instance of the <see cref="ILibraryManager"/> interface.</param>
    /// <param name="libraryMonitor">Instance of the <see cref="ILibraryMonitor"/> interface.</param>
    /// <param name="providerManager">Instance of the <see cref="IProviderManager"/> interface.</param>
    /// <param name="fileSystem">Instance of the <see cref="IFileSystem"/> interface.</param>
    public RecordingService(
        ILogger<RecordingService> logger,
        IHttpClientFactory httpClientFactory,
        IConfigurationManager configurationManager,
        ILibraryManager libraryManager,
        ILibraryMonitor libraryMonitor,
        IProviderManager providerManager,
        IFileSystem fileSystem)
    {
        _logger = logger;
        _httpClientFactory = httpClientFactory;
        _configurationManager = configurationManager;
        _libraryManager = libraryManager;
        _libraryMonitor = libraryMonitor;
        _providerManager = providerManager;
        _fileSystem = fileSystem;
        _dataPath = Path.Combine(configurationManager.CommonApplicationPaths.DataPath, "xtream", "timers.json");
    }

    private string DefaultRecordingPath
    {
        get
        {
            string? path = null;
            try
            {
                path = _configurationManager.GetConfiguration<LiveTvOptions>("livetv").RecordingPath;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Unable to read the Live TV configuration; falling back to the default recording path.");
            }

            return string.IsNullOrWhiteSpace(path)
                ? Path.Combine(_configurationManager.CommonApplicationPaths.DataPath, "livetv", "recordings")
                : path;
        }
    }

    /// <summary>
    /// Gets all persisted timers.
    /// </summary>
    /// <returns>A snapshot of the currently scheduled timers.</returns>
    public IReadOnlyList<TimerInfo> GetAllTimers()
    {
        lock (_timersLock)
        {
            EnsureLoaded();
            return _timers.ToList();
        }
    }

    /// <summary>
    /// Creates and schedules a new timer, returning its generated identifier.
    /// </summary>
    /// <param name="info">The timer to create.</param>
    /// <returns>The identifier assigned to the timer.</returns>
    public string CreateTimer(TimerInfo info)
    {
        NormalizeCollections(info);

        if (string.IsNullOrEmpty(info.Id))
        {
            info.Id = Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture);
        }

        info.Status = RecordingStatus.New;

        lock (_timersLock)
        {
            EnsureLoaded();
            _timers.RemoveAll(t => string.Equals(t.Id, info.Id, StringComparison.OrdinalIgnoreCase));
            _timers.Add(info);
            Save();
        }

        _logger.LogInformation(
            "Created recording timer {Id} for channel {ChannelId} from {StartDate} to {EndDate}",
            info.Id,
            info.ChannelId,
            info.StartDate,
            info.EndDate);

        AddOrUpdateSystemTimer(info);
        return info.Id;
    }

    /// <summary>
    /// Updates an existing timer and reschedules it.
    /// </summary>
    /// <param name="info">The updated timer.</param>
    public void UpdateTimer(TimerInfo info)
    {
        NormalizeCollections(info);

        bool updated;
        lock (_timersLock)
        {
            EnsureLoaded();
            int index = _timers.FindIndex(t => string.Equals(t.Id, info.Id, StringComparison.OrdinalIgnoreCase));
            updated = index != -1;
            if (updated)
            {
                _timers[index] = info;
                Save();
            }
        }

        if (updated)
        {
            AddOrUpdateSystemTimer(info);
        }
        else
        {
            _logger.LogWarning("Tried to update unknown timer {Id}", info.Id);
        }
    }

    /// <summary>
    /// Cancels a timer and stops the associated recording if it is currently running.
    /// </summary>
    /// <param name="timerId">The identifier of the timer to cancel.</param>
    public void CancelTimer(string timerId)
    {
        RemoveTimer(timerId);

        if (_activeRecordings.TryGetValue(timerId, out CancellationTokenSource? cts))
        {
            _logger.LogInformation("Cancelling active recording for timer {Id}", timerId);
            try
            {
                cts.Cancel();
            }
            catch (ObjectDisposedException)
            {
                // The recording already finished; nothing to cancel.
            }
        }
    }

    /// <summary>
    /// Loads the persisted timers and (re)arms a system timer for each of them.
    /// Intended to be called once on startup.
    /// </summary>
    public void Initialize()
    {
        try
        {
            Directory.CreateDirectory(DefaultRecordingPath);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Unable to create the recording directory.");
        }

        List<TimerInfo> all;
        lock (_timersLock)
        {
            EnsureLoaded();
            all = _timers.ToList();
        }

        _logger.LogInformation("Restarting {Count} Xtream recording timer(s)", all.Count);
        foreach (TimerInfo timer in all)
        {
            AddOrUpdateSystemTimer(timer);
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        // Stop any in-flight recordings so their files, sockets and tasks are released.
        foreach (CancellationTokenSource cts in _activeRecordings.Values)
        {
            try
            {
                cts.Cancel();
            }
            catch (ObjectDisposedException)
            {
                // The recording already finished.
            }
        }

        foreach (Timer timer in _systemTimers.Values)
        {
            timer.Dispose();
        }

        _systemTimers.Clear();
    }

    private static void NormalizeCollections(TimerInfo info)
    {
        // TimerInfo exposes computed getters (IsKids/IsSports/IsNews) that read Tags without a
        // null guard, so a timer arriving with null collections would throw on serialization.
        info.Tags ??= [];
        info.Genres ??= [];
    }

    [MemberNotNull(nameof(_timers))]
    private void EnsureLoaded()
    {
        if (_timers is not null)
        {
            return;
        }

        if (File.Exists(_dataPath))
        {
            try
            {
                byte[] bytes = File.ReadAllBytes(_dataPath);
                _timers = JsonSerializer.Deserialize<List<TimerInfo>>(bytes, _jsonOptions);
                if (_timers is not null)
                {
                    return;
                }

                _logger.LogError("Error deserializing {Path}, data was null", _dataPath);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error deserializing {Path}", _dataPath);
            }
        }

        _timers = [];
    }

    private void Save()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_dataPath) ?? throw new InvalidOperationException("Invalid data path."));
        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(_timers, _jsonOptions);
        File.WriteAllBytes(_dataPath, bytes);
    }

    private void UpdateTimerInternal(TimerInfo info)
    {
        lock (_timersLock)
        {
            EnsureLoaded();
            int index = _timers.FindIndex(t => string.Equals(t.Id, info.Id, StringComparison.OrdinalIgnoreCase));
            if (index == -1)
            {
                // The timer was cancelled while the recording was running; do not resurrect it.
                return;
            }

            _timers[index] = info;
            Save();
        }
    }

    private void RemoveTimer(string id)
    {
        lock (_timersLock)
        {
            EnsureLoaded();
            if (_timers.RemoveAll(t => string.Equals(t.Id, id, StringComparison.OrdinalIgnoreCase)) > 0)
            {
                Save();
            }
        }

        StopSystemTimer(id);
    }

    private void AddOrUpdateSystemTimer(TimerInfo item)
    {
        StopSystemTimer(item.Id);

        if (item.Status is RecordingStatus.Completed or RecordingStatus.Cancelled or RecordingStatus.Error)
        {
            return;
        }

        DateTime startUtc = item.StartDate.AddSeconds(-item.PrePaddingSeconds);
        DateTime now = DateTime.UtcNow;

        if (item.EndDate.AddSeconds(item.PostPaddingSeconds) <= now)
        {
            _logger.LogWarning("Recording timer {Id} is in the past and will be removed.", item.Id);
            RemoveTimer(item.Id);
            return;
        }

        if (startUtc <= now)
        {
            // Fire on the thread pool so we never block the caller (timer creation or startup re-arm).
            _ = Task.Run(() => OnTimerFiredAsync(item.Id));
            return;
        }

        TimeSpan dueTime = startUtc - now;
        try
        {
            Timer timer = new(OnSystemTimerCallback, item.Id, dueTime, Timeout.InfiniteTimeSpan);
            if (!_systemTimers.TryAdd(item.Id, timer))
            {
                timer.Dispose();
            }
            else
            {
                _logger.LogInformation(
                    "Recording timer {Id} ({Name}) will fire in {Minutes} minute(s).",
                    item.Id,
                    item.Name,
                    dueTime.TotalMinutes.ToString("F1", CultureInfo.InvariantCulture));
            }
        }
        catch (ArgumentOutOfRangeException ex)
        {
            _logger.LogError(ex, "Recording timer {Id} is scheduled too far in the future to be armed.", item.Id);
        }
    }

    private void StopSystemTimer(string id)
    {
        if (_systemTimers.TryRemove(id, out Timer? timer))
        {
            timer.Dispose();
        }
    }

    private void OnSystemTimerCallback(object? state)
    {
        string timerId = (string)state!;
        _ = OnTimerFiredAsync(timerId);
    }

    private async Task OnTimerFiredAsync(string timerId)
    {
        StopSystemTimer(timerId);

        if (_activeRecordings.ContainsKey(timerId))
        {
            return;
        }

        TimerInfo? timer;
        lock (_timersLock)
        {
            EnsureLoaded();
            timer = _timers.FirstOrDefault(t => string.Equals(t.Id, timerId, StringComparison.OrdinalIgnoreCase));
        }

        if (timer is null)
        {
            return;
        }

        try
        {
            await RecordAsync(timer).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unhandled error while recording timer {Id}", timerId);
        }
    }

    private async Task RecordAsync(TimerInfo timer)
    {
        DateTime recordingEndDate = timer.EndDate.AddSeconds(timer.PostPaddingSeconds);
        if (recordingEndDate <= DateTime.UtcNow)
        {
            _logger.LogWarning("Recording timer {Id} expired before it could start.", timer.Id);
            RemoveTimer(timer.Id);
            return;
        }

        if (!TryGetStreamId(timer.ChannelId, out int streamId))
        {
            _logger.LogError("Recording timer {Id} has an unsupported channel id {ChannelId}.", timer.Id, timer.ChannelId);
            RemoveTimer(timer.Id);
            return;
        }

        MediaSourceInfo mediaSource = Plugin.Instance.StreamService.GetMediaSourceInfo(StreamType.Live, streamId);
        string url = mediaSource.Path;
        string recordingPath = EnsureFileUnique(GetRecordingPath(timer));

        using CancellationTokenSource cts = new();
        if (!_activeRecordings.TryAdd(timer.Id, cts))
        {
            _logger.LogInformation("Recording for timer {Id} is already running.", timer.Id);
            return;
        }

        _libraryMonitor.ReportFileSystemChangeBeginning(recordingPath);
        try
        {
            TimeSpan duration = recordingEndDate - DateTime.UtcNow;
            if (duration <= TimeSpan.Zero)
            {
                // The window elapsed while we were setting up; nothing left to capture.
                throw new OperationCanceledException("The recording window has already elapsed.");
            }

            timer.Status = RecordingStatus.InProgress;
            UpdateTimerInternal(timer);

            _logger.LogInformation(
                "Beginning recording of timer {Id} for {Minutes} minute(s) to {Path}",
                timer.Id,
                duration.TotalMinutes.ToString("F1", CultureInfo.InvariantCulture),
                recordingPath);

            await RecordStreamToFileAsync(url, recordingPath, duration, cts.Token).ConfigureAwait(false);
            timer.Status = RecordingStatus.Completed;
            _logger.LogInformation("Recording of timer {Id} completed: {Path}", timer.Id, recordingPath);
        }
        catch (OperationCanceledException)
        {
            // Reaching the requested duration or a manual cancellation is the normal way for an
            // infinite live recording to stop; any captured bytes are kept.
            timer.Status = RecordingStatus.Completed;
            _logger.LogInformation("Recording of timer {Id} stopped: {Path}", timer.Id, recordingPath);
        }
        catch (Exception ex)
        {
            timer.Status = RecordingStatus.Error;
            _logger.LogError(ex, "Error recording timer {Id} to {Path}", timer.Id, recordingPath);
        }
        finally
        {
            _activeRecordings.TryRemove(timer.Id, out _);
        }

        DeleteFileIfEmpty(recordingPath);
        TriggerRefresh(recordingPath);
        _libraryMonitor.ReportFileSystemChangeComplete(recordingPath, false);

        if (File.Exists(recordingPath))
        {
            timer.RecordingPath = recordingPath;
            timer.Status = RecordingStatus.Completed;
            UpdateTimerInternal(timer);
        }
        else
        {
            RemoveTimer(timer.Id);
        }
    }

    private async Task RecordStreamToFileAsync(string url, string targetFile, TimeSpan duration, CancellationToken cancellationToken)
    {
        HttpClient client = _httpClientFactory.CreateClient(NamedClient.Default);
        HttpResponseMessage response = await client
            .GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
            .ConfigureAwait(false);

        // Handle a manual redirect in the case of a HTTPS to HTTP downgrade (same as Restream).
        if (_redirects.Contains(response.StatusCode) && response.Headers.Location is not null)
        {
            Uri location = response.Headers.Location;
            response.Dispose();
            response = await client
                .GetAsync(location, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                .ConfigureAwait(false);
        }

        using (response)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(targetFile) ?? throw new InvalidOperationException("Invalid recording path."));

            FileStream output = new(targetFile, FileMode.CreateNew, FileAccess.Write, FileShare.Read, BufferSize, FileOptions.Asynchronous);
            await using (output.ConfigureAwait(false))
            {
                // The live stream is infinite, so the recording is bounded by its duration.
                using CancellationTokenSource durationSource = new(duration);
                using CancellationTokenSource linkedSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, durationSource.Token);

                Stream input = await response.Content.ReadAsStreamAsync(linkedSource.Token).ConfigureAwait(false);
                await using (input.ConfigureAwait(false))
                {
                    // The infinite live stream is bounded by the linked duration/cancellation token.
                    // The resulting OperationCanceledException is handled by the caller as a normal stop.
                    await input.CopyToAsync(output, BufferSize, linkedSource.Token).ConfigureAwait(false);
                }
            }
        }
    }

    private static bool TryGetStreamId(string channelId, out int streamId)
    {
        streamId = 0;
        if (!Guid.TryParse(channelId, out Guid guid))
        {
            return false;
        }

        StreamService.FromGuid(guid, out int prefix, out int id, out int _, out int _);
        if (prefix != StreamService.LiveTvPrefix)
        {
            return false;
        }

        streamId = id;
        return true;
    }

    private string GetRecordingPath(TimerInfo timer)
    {
        string fileName = _fileSystem.GetValidFilename(GetRecordingName(timer)).Trim();
        return Path.Combine(DefaultRecordingPath, fileName + ".ts");
    }

    private static string GetRecordingName(TimerInfo info)
    {
        string name = info.Name;

        if (info.IsProgramSeries)
        {
            if (info.SeasonNumber.HasValue && info.EpisodeNumber.HasValue)
            {
                name += string.Format(
                    CultureInfo.InvariantCulture,
                    " S{0}E{1}",
                    info.SeasonNumber.Value.ToString("00", CultureInfo.InvariantCulture),
                    info.EpisodeNumber.Value.ToString("00", CultureInfo.InvariantCulture));
            }
            else
            {
                name += " " + GetDateString(info.StartDate);
            }

            if (!string.IsNullOrWhiteSpace(info.EpisodeTitle))
            {
                name += " - " + info.EpisodeTitle;
            }
        }
        else if (info.IsMovie && info.ProductionYear is not null)
        {
            name += " (" + info.ProductionYear.Value.ToString(CultureInfo.InvariantCulture) + ")";
        }
        else
        {
            name += " " + GetDateString(info.StartDate);
        }

        return name;
    }

    private static string GetDateString(DateTime date)
        => date.ToLocalTime().ToString("yyyy_MM_dd_HH_mm_ss", CultureInfo.InvariantCulture);

    private string EnsureFileUnique(string path)
    {
        string parent = Path.GetDirectoryName(path) ?? throw new InvalidOperationException("Invalid recording path.");
        string name = Path.GetFileNameWithoutExtension(path);
        string extension = Path.GetExtension(path);

        string result = path;
        int index = 1;
        while (File.Exists(result))
        {
            result = Path.Combine(parent, name + " - " + index.ToString(CultureInfo.InvariantCulture) + extension);
            index++;
        }

        return result;
    }

    private void TriggerRefresh(string path)
    {
        BaseItem? item = GetAffectedBaseItem(Path.GetDirectoryName(path));
        if (item is null)
        {
            _logger.LogWarning(
                "Recorded file {Path} is not inside a Jellyfin library. Make sure Live TV DVR is enabled; the file will be imported once the recordings library exists and is scanned.",
                path);
            return;
        }

        _logger.LogInformation("Refreshing recording parent {Path}", item.Path);
        _providerManager.QueueRefresh(
            item.Id,
            new MetadataRefreshOptions(new DirectoryService(_fileSystem))
            {
                RefreshPaths =
                [
                    path,
                    Path.GetDirectoryName(path)!,
                    Path.GetDirectoryName(Path.GetDirectoryName(path))!,
                ],
            },
            RefreshPriority.High);
    }

    private BaseItem? GetAffectedBaseItem(string? path)
    {
        BaseItem? item = null;
        string? parentPath = Path.GetDirectoryName(path);
        while (item is null && !string.IsNullOrEmpty(path))
        {
            item = _libraryManager.FindByPath(path, null);
            path = Path.GetDirectoryName(path);
        }

        if (item is not null
            && item.GetType() == typeof(Folder)
            && string.Equals(item.Path, parentPath, StringComparison.OrdinalIgnoreCase))
        {
            BaseItem parentItem = item.GetParent();
            if (parentItem is not null and not AggregateFolder)
            {
                item = parentItem;
            }
        }

        return item;
    }

    private void DeleteFileIfEmpty(string path)
    {
        try
        {
            FileInfo file = new(path);
            if (file.Exists && file.Length == 0)
            {
                _logger.LogWarning("Recording produced an empty file; deleting {Path}", path);
                file.Delete();
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unable to delete empty recording {Path}", path);
        }
    }
}
