// Copyright 2013-2017 Serilog Contributors
//
// Licensed under the Apache License, Version 2.0 (the "License");
// you may not use this file except in compliance with the License.
// You may obtain a copy of the License at
//
//     http://www.apache.org/licenses/LICENSE-2.0
//
// Unless required by applicable law or agreed to in writing, software
// distributed under the License is distributed on an "AS IS" BASIS,
// WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
// See the License for the specific language governing permissions and
// limitations under the License.

using System.Text;
using Serilog.Core;
using Serilog.Debugging;
using Serilog.Events;
using Serilog.Formatting;

namespace Serilog.Sinks.File;

sealed class RollingFileSink : ILogEventSink, IFlushableFileSink, IDisposable
{
    readonly PathRoller _roller;
    readonly ITextFormatter _textFormatter;
    readonly long? _fileSizeLimitBytes;
    readonly int? _retainedFileCountLimit;
    readonly TimeSpan? _retainedFileTimeLimit;
    readonly Encoding? _encoding;
    readonly bool _buffered;
    readonly bool _shared;
    readonly bool _rollOnFileSizeLimit;
    readonly FileLifecycleHooks? _hooks;

    readonly object _syncRoot = new();
    bool _isDisposed;
    DateTime? _nextCheckpoint;
    IFileSink? _currentFile;
    int? _currentFileSequence;

    public RollingFileSink(string path,
                          ITextFormatter textFormatter,
                          long? fileSizeLimitBytes,
                          int? retainedFileCountLimit,
                          Encoding? encoding,
                          bool buffered,
                          bool shared,
                          RollingInterval rollingInterval,
                          bool rollOnFileSizeLimit,
                          FileLifecycleHooks? hooks,
                          TimeSpan? retainedFileTimeLimit,
                          int? rollingIntervalDuration)
    {
        if (path == null) throw new ArgumentNullException(nameof(path));
        if (fileSizeLimitBytes is < 1) throw new ArgumentException("Invalid value provided; file size limit must be at least 1 byte, or null.");
        if (retainedFileCountLimit is < 1) throw new ArgumentException("Zero or negative value provided; retained file count limit must be at least 1.");
        if (retainedFileTimeLimit.HasValue && retainedFileTimeLimit < TimeSpan.Zero) throw new ArgumentException("Negative value provided; retained file time limit must be non-negative.", nameof(retainedFileTimeLimit));
        if (rollingInterval != RollingInterval.Infinite && rollingIntervalDuration.HasValue && rollingIntervalDuration < 1) throw new ArgumentException("Zero or negative value provided; rolling interval duration must be at least 1.", nameof(rollingIntervalDuration));

        _roller = new PathRoller(path, rollingInterval, rollingIntervalDuration ?? 1);
        _textFormatter = textFormatter;
        _fileSizeLimitBytes = fileSizeLimitBytes;
        _retainedFileCountLimit = retainedFileCountLimit;
        _retainedFileTimeLimit = retainedFileTimeLimit;
        _encoding = encoding;
        _buffered = buffered;
        _shared = shared;
        _rollOnFileSizeLimit = rollOnFileSizeLimit;
        _hooks = hooks;
    }

    public void Emit(LogEvent logEvent)
    {
        if (logEvent == null) throw new ArgumentNullException(nameof(logEvent));

        lock (_syncRoot)
        {
            if (_isDisposed) throw new ObjectDisposedException("The log file has been disposed.");

            var now = Clock.DateTimeNow;
            AlignCurrentFileTo(now);

            while (_currentFile?.EmitOrOverflow(logEvent) == false && _rollOnFileSizeLimit)
            {
                AlignCurrentFileTo(now, nextSequence: true);
            }
        }
    }

    void AlignCurrentFileTo(DateTime now, bool nextSequence = false)
    {
        if (!_nextCheckpoint.HasValue)
        {
            OpenFile(now);
        }
        else if (nextSequence || now >= _nextCheckpoint.Value)
        {
            int? minSequence = null;
            if (nextSequence)
            {
                if (_currentFileSequence == null)
                    minSequence = 1;
                else
                    minSequence = _currentFileSequence.Value + 1;
            }

            CloseFile();
            OpenFile(now, minSequence);
        }
    }

    void OpenFile(DateTime now, int? minSequence = null)
    {
        var currentCheckpoint = _roller.GetCurrentCheckpoint(now);

        // We only try periodically because repeated failures
        // to open log files REALLY slow an app down.
        _nextCheckpoint = _roller.GetNextCheckpoint(now) ?? now.AddMinutes(30);

        var existingFiles = Enumerable.Empty<string>();
        try
        {
            if (Directory.Exists(_roller.LogFileDirectory))
            {
                // ReSharper disable once ConvertClosureToMethodGroup
                existingFiles = Directory.GetFiles(_roller.LogFileDirectory, _roller.DirectorySearchPattern)
                    .Select(f => Path.GetFileName(f));
            }
        }
        catch (DirectoryNotFoundException) { }

        var latestForThisCheckpoint = _roller
            .SelectMatches(existingFiles)
            .Where(m => m.DateTime == currentCheckpoint)
#if ENUMERABLE_MAXBY
            .MaxBy(m => m.SequenceNumber);
#else
            .OrderByDescending(m => m.SequenceNumber)
            .FirstOrDefault();
#endif

        var sequence = latestForThisCheckpoint?.SequenceNumber;
        if (minSequence != null)
        {
            if (sequence == null || sequence.Value < minSequence.Value)
                sequence = minSequence;
        }

        const int maxAttempts = 3;
        for (var attempt = 0; attempt < maxAttempts; attempt++)
        {
            _roller.GetLogFilePath(now, sequence, out var path);

            try
            {
                _currentFile = _shared ?
#pragma warning disable 618
                    new SharedFileSink(path, _textFormatter, _fileSizeLimitBytes, _encoding) :
#pragma warning restore 618
                    new FileSink(path, _textFormatter, _fileSizeLimitBytes, _encoding, _buffered, _hooks);

                _currentFileSequence = sequence;
            }
            catch (IOException ex)
            {
                if (IOErrors.IsLockedFile(ex))
                {
                    SelfLog.WriteLine("File target {0} was locked, attempting to open next in sequence (attempt {1})", path, attempt + 1);
                    sequence = (sequence ?? 0) + 1;
                    continue;
                }

                throw;
            }

            ApplyRetentionPolicy(path, now);
            return;
        }
    }

    void ApplyRetentionPolicy(string currentFilePath, DateTime now)
    {
        if (_retainedFileCountLimit == null && _retainedFileTimeLimit == null) return;

        var currentFileName = Path.GetFileName(currentFilePath);

        // We consider the current file to exist, even if nothing's been written yet,
        // because files are only opened on response to an event being processed.
        // ReSharper disable once ConvertClosureToMethodGroup
        var potentialMatches = Directory.GetFiles(_roller.LogFileDirectory, _roller.DirectorySearchPattern)
            .Select(f => Path.GetFileName(f))
            .Union(new[] { currentFileName });

        var newestFirst = _roller
            .SelectMatches(potentialMatches)
            .OrderByDescending(m => m.DateTime)
            .ThenByDescending(m => m.SequenceNumber);

        var toRemove = newestFirst
            .Where(n => StringComparer.OrdinalIgnoreCase.Compare(currentFileName, n.Filename) != 0)
            .SkipWhile((f, i) => ShouldRetainFile(f, i, now))
            .Select(x => x.Filename)
            .ToList();

        foreach (var obsolete in toRemove)
        {
            var fullPath = Path.Combine(_roller.LogFileDirectory, obsolete);
            try
            {
                _hooks?.OnFileDeleting(fullPath);
                System.IO.File.Delete(fullPath);
            }
            catch (Exception ex)
            {
                SelfLog.WriteLine("Error {0} while processing obsolete log file {1}", ex, fullPath);
            }
        }
    }

    bool ShouldRetainFile(RollingLogFile file, int index, DateTime now)
    {
        if (_retainedFileCountLimit.HasValue && index >= _retainedFileCountLimit.Value - 1)
            return false;

        if (_retainedFileTimeLimit.HasValue && file.DateTime.HasValue &&
            file.DateTime.Value < now.Subtract(_retainedFileTimeLimit.Value))
        {
            return false;
        }

        return true;
    }

    public void Dispose()
    {
        lock (_syncRoot)
        {
            if (_currentFile == null) return;
            CloseFile();
            _isDisposed = true;
        }
    }

    void CloseFile()
    {
        if (_currentFile != null)
        {
            (_currentFile as IDisposable)?.Dispose();
            _currentFile = null;
        }

        _nextCheckpoint = null;
    }

    public void FlushToDisk()
    {
        lock (_syncRoot)
        {
            _currentFile?.FlushToDisk();
        }
    }
}
