using System.Collections.ObjectModel;
using System;
using System.Threading;
using System.Threading.Tasks;
using MdModManager.Models;

namespace MdModManager.Services;

public sealed record DownloadCompletionResult(bool Success, string? FilePath, string? ErrorMessage);

public interface IDownloadManagerService
{
    ObservableCollection<DownloadTaskItem> Tasks { get; }
    System.Collections.Generic.HashSet<string> SessionDownloadedFiles { get; }
    void EnqueueDownload(MdmcChart chart);
    Task<DownloadCompletionResult> EnqueueDownloadAndWaitAsync(
        MdmcChart chart,
        Func<string, CancellationToken, Task<string?>>? completedFileValidator = null);
    void PauseDownload(DownloadTaskItem item);
    void ResumeDownload(DownloadTaskItem item);
    void CancelDownload(DownloadTaskItem item);
    void TogglePauseResumeAll();
    void CancelAllDownloads();
    void ClearCompletedAndCanceled();
}
