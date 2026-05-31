using System.Threading.Tasks;

namespace MdModManager.Services;

public sealed class AsyncManualResetEvent
{
    private volatile TaskCompletionSource<bool> _tcs = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public AsyncManualResetEvent(bool initialState)
    {
        if (initialState)
        {
            _tcs.TrySetResult(true);
        }
    }

    // 等待通知可用性
    public Task WaitAsync() => _tcs.Task;

    // 设置通知信号释放阻塞
    public void Set()
    {
        _tcs.TrySetResult(true);
    }

    // 重置通知信号恢复阻塞
    public void Reset()
    {
        if (_tcs.Task.IsCompleted)
        {
            _tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        }
    }
}

public interface IAuthService
{
    // 会话就绪通知句柄
    AsyncManualResetEvent Ready { get; }

    // 唤起浏览器登录页面
    Task LoginAsync();

    // 注销当前会话
    Task LogoutAsync();

    // 获取并自动刷新可用令牌
    Task<string> GetAccessTokenAsync();

    // 强制请求刷新会话令牌
    Task<string> RenewAccessTokenAsync();

    // 启动时自动恢复本地有效会话
    Task<bool> RestoreSessionAsync();
}
