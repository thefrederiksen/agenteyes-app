using System.Text.Json;

namespace AgentEyes.Setup.Engine;

/// <summary>
/// What the last FAILED update attempt left for the app to read (issue #86 review, B1c). The file
/// exists only after a failure; a successful update clears it. The relaunched app's AutoUpdate reads
/// it at start and does not hand over again for the same target version - it logs and shows the
/// reason instead - so a failing update cannot become a stop -> fail -> relaunch -> stop loop.
/// </summary>
public sealed class UpdateAttemptRecord
{
    /// <summary>The format, for a future reader that has to tell an old file apart.</summary>
    public int Version { get; set; } = 1;

    /// <summary>When the attempt was made.</summary>
    public DateTime AttemptedUtc { get; set; }

    /// <summary>The release version the attempt was updating to.</summary>
    public string TargetVersion { get; set; } = "";

    /// <summary>Always "failed": a successful attempt deletes the file instead of writing one.</summary>
    public string Outcome { get; set; } = UpdateAttemptMarker.OutcomeFailed;

    /// <summary>Where it failed: "download", "stop", "swap", "relaunch" or "unknown".</summary>
    public string Stage { get; set; } = "";

    /// <summary>The exception message, as the person saw it.</summary>
    public string Reason { get; set; } = "";

    /// <summary>Who ran the attempt: "cli" or "wizard".</summary>
    public string By { get; set; } = "";

    /// <summary>One line for a log or a balloon.</summary>
    public string Describe() =>
        $"the update to v{TargetVersion} failed at {AttemptedUtc.ToLocalTime():yyyy-MM-dd HH:mm:ss} ({Stage}: {Reason})";
}

/// <summary>Reads, writes and clears the <see cref="UpdateAttemptRecord"/> under the install root
/// (<see cref="InstallLayout.UpdateAttemptMarkerPath"/>).</summary>
public static class UpdateAttemptMarker
{
    public const string OutcomeFailed = "failed";

    public const string StageDownload = "download";
    public const string StageStop = "stop";
    public const string StageSwap = "swap";
    public const string StageRelaunch = "relaunch";
    public const string StageUnknown = "unknown";

    /// <summary>The stage an exception from <see cref="UpdateRestartCycle.RunAsync"/> belongs to.</summary>
    public static string StageOf(Exception ex)
    {
        ArgumentNullException.ThrowIfNull(ex);
        return ex switch
        {
            UpdateStageException => StageDownload,
            AppStopFailedException => StageStop,
            UpdateSwapException => StageSwap,
            AppRelaunchFailedException => StageRelaunch,
            AggregateException agg when agg.InnerExceptions.Any(e => e is AppRelaunchFailedException) => StageRelaunch,
            _ => StageUnknown,
        };
    }

    /// <summary>Record a failed attempt (overwrites an earlier one). Written atomically.</summary>
    public static UpdateAttemptRecord WriteFailed(InstallLayout layout, string targetVersion, string stage, string reason, string by)
    {
        ArgumentNullException.ThrowIfNull(layout);
        if (string.IsNullOrWhiteSpace(targetVersion)) throw new ArgumentException("targetVersion required", nameof(targetVersion));
        var record = new UpdateAttemptRecord
        {
            AttemptedUtc = DateTime.UtcNow,
            TargetVersion = targetVersion,
            Stage = stage ?? StageUnknown,
            Reason = reason ?? "",
            By = by ?? "",
        };
        string path = layout.UpdateAttemptMarkerPath;
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        string tmp = path + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(record, new JsonSerializerOptions { WriteIndented = true }));
        File.Move(tmp, path, overwrite: true);
        EngineLog.Write($"[UpdateAttemptMarker] WriteFailed: {path} - {record.Describe()}");
        return record;
    }

    /// <summary>The recorded failure, or null when there is none. A file that is there but cannot be
    /// read throws with the path: a reader must not silently treat it as "no failure".</summary>
    public static UpdateAttemptRecord? Read(InstallLayout layout)
    {
        ArgumentNullException.ThrowIfNull(layout);
        string path = layout.UpdateAttemptMarkerPath;
        if (!File.Exists(path)) return null;
        try
        {
            var record = JsonSerializer.Deserialize<UpdateAttemptRecord>(File.ReadAllText(path));
            if (record == null || string.IsNullOrWhiteSpace(record.TargetVersion))
                throw new JsonException("the file holds no update attempt");
            return record;
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            throw new InvalidDataException(
                $"the update attempt record {path} cannot be read ({ex.Message}). Delete it, or run 'agenteyes-setup update' "
                + "by hand - a successful update removes it.", ex);
        }
    }

    /// <summary>A successful update: the record is gone. True when there was one.</summary>
    public static bool Clear(InstallLayout layout)
    {
        ArgumentNullException.ThrowIfNull(layout);
        string path = layout.UpdateAttemptMarkerPath;
        if (!File.Exists(path)) return false;
        File.Delete(path);
        EngineLog.Write($"[UpdateAttemptMarker] Clear: removed {path}");
        return true;
    }
}
