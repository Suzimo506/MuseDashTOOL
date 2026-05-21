using System.Threading.Tasks;

namespace MdModManager.Services;

public interface ITelemetryService
{
    // 发送应用会话遥测请求
    Task TrackSessionAsync();

    // 绑定游戏账号
    Task BindVanillaAccountAsync();
}
