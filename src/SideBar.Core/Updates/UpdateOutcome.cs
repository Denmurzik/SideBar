using System.Net;

namespace SideBar.Core.Updates;

public enum UpdateKind { UpToDate, Available, NotFound, Failed }

/// <summary>Итог одной проверки. <see cref="Release"/> задан только при <see cref="UpdateKind.Available"/>.</summary>
public sealed record UpdateOutcome(UpdateKind Kind, ReleaseInfo? Release = null, string Detail = "")
{
    /// <summary>Строка для лога: только тип итога и код, без тела ответа.</summary>
    public string LogLine => Kind switch
    {
        UpdateKind.Available => "update: available",
        UpdateKind.UpToDate => "update: up to date",
        UpdateKind.NotFound => "update: not available (404)",
        _ => $"update: failed ({Detail})",
    };

    /// <summary>Ответ releases/latest → итог. Никогда не бросает.</summary>
    public static UpdateOutcome Evaluate(int httpStatus, ReadOnlySpan<byte> body, Version current)
    {
        if (httpStatus == (int)HttpStatusCode.NotFound) return new UpdateOutcome(UpdateKind.NotFound);
        if (httpStatus != (int)HttpStatusCode.OK) return new UpdateOutcome(UpdateKind.Failed, Detail: $"http {httpStatus}");
        if (!ReleaseInfo.TryParse(body, out var info)) return new UpdateOutcome(UpdateKind.Failed, Detail: "parse");
        return AppVersion.IsNewer(current, info.Version)
            ? new UpdateOutcome(UpdateKind.Available, info)
            : new UpdateOutcome(UpdateKind.UpToDate);
    }
}
