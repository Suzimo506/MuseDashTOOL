using System.Collections.ObjectModel;
using MdModManager.Models;

namespace MdModManager.Services;

public interface IDownloadManagerService
{
    ObservableCollection<DownloadTaskItem> Tasks { get; }
    System.Collections.Generic.HashSet<string> SessionDownloadedFiles { get; }
    void EnqueueDownload(MdmcChart chart);
    void PauseDownload(DownloadTaskItem item);
    void ResumeDownload(DownloadTaskItem item);
    void CancelDownload(DownloadTaskItem item);
    void TogglePauseResumeAll();
    void CancelAllDownloads();
    void ClearCompletedAndCanceled();
}
