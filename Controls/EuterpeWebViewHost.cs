using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Platform;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.DependencyInjection;
using MdModManager.Helpers;
using MdModManager.Services;
using Microsoft.Web.WebView2.Core;
using DrawingRectangle = System.Drawing.Rectangle;

namespace MdModManager.Controls;

public sealed class EuterpeWebViewHost : NativeControlHost
{
    private const string DownloadMenuScript = """
        (() => {
            if (window.location.hostname !== 'euterpe-org.com' || window.__musedashToolDownloadMenuHook) {
                return JSON.stringify({ installed: false });
            }

            window.__musedashToolDownloadMenuHook = true;
            const isDownloadButton = (element) => /^(下载|download)$/i.test((element.innerText || element.textContent || '').trim());
            const getDownloadButton = (event) => event.composedPath().find((node) =>
                node instanceof Element &&
                node.matches('button.rounded-r-none') &&
                isDownloadButton(node));
            const getMenuButton = (button) => {
                return button.parentElement?.querySelector('button.rounded-l-none') || null;
            };

            const redirectMainDownloadToMenu = (event) => {
                const button = getDownloadButton(event);
                if (!button) return;
                const menuButton = getMenuButton(button);
                if (!menuButton) return;

                event.preventDefault();
                event.stopImmediatePropagation();
                menuButton.click();
            };

            // The main half dispatches euterpe:// on click; the ZIP item has different text and is not intercepted.
            window.addEventListener('click', redirectMainDownloadToMenu, true);

            return JSON.stringify({ installed: true });
        })();
        """;

    private const string RestoreScrollPositionScript = """
        (() => {
            if (window.location.hostname !== 'euterpe-org.com' || window.__musedashToolScrollRestoreHook) {
                return JSON.stringify({ installed: false });
            }

            window.__musedashToolScrollRestoreHook = true;
            const storagePrefix = 'musedash-tool-euterpe-scroll:';
            const pageKey = () => storagePrefix + window.location.pathname + window.location.search;
            const currentScrollTop = () => Math.max(window.scrollY || 0, document.scrollingElement?.scrollTop || 0);
            const savePosition = () => sessionStorage.setItem(pageKey(), String(currentScrollTop()));
            const restorePosition = () => {
                const value = Number(sessionStorage.getItem(pageKey()));
                if (!Number.isFinite(value) || value <= 0) return;

                // The site renders the list asynchronously after browser history changes.
                for (const delay of [0, 80, 240, 600, 1200]) {
                    window.setTimeout(() => window.scrollTo(0, value), delay);
                }
            };

            for (const method of ['pushState', 'replaceState']) {
                const original = history[method];
                history[method] = function (...args) {
                    savePosition();
                    return original.apply(this, args);
                };
            }

            window.addEventListener('scroll', savePosition, { passive: true });
            window.addEventListener('pagehide', savePosition);
            window.addEventListener('popstate', restorePosition);
            window.addEventListener('hashchange', restorePosition);
            restorePosition();
            return JSON.stringify({ installed: true });
        })();
        """;

    private const uint WsChild = 0x40000000;
    private const uint WsVisible = 0x10000000;
    private const uint WsClipChildren = 0x02000000;
    private const uint WsClipSiblings = 0x04000000;

    public static readonly StyledProperty<Uri?> SourceProperty =
        AvaloniaProperty.Register<EuterpeWebViewHost, Uri?>(nameof(Source));

    private CoreWebView2Controller? _controller;
    private IntPtr _hostHandle;
    private bool _isDisposed;

    public Uri? Source
    {
        get => GetValue(SourceProperty);
        set => SetValue(SourceProperty, value);
    }

    public EuterpeWebViewHost()
    {
        SizeChanged += (_, _) => Dispatcher.UIThread.Post(SyncNativeLayout, DispatcherPriority.Render);
    }

    public void GoBack()
    {
        if (_controller?.CoreWebView2?.CanGoBack == true)
            _controller.CoreWebView2.GoBack();
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);

        if (change.Property == SourceProperty)
            _ = NavigateAsync(change.GetNewValue<Uri?>());
    }

    protected override IPlatformHandle CreateNativeControlCore(IPlatformHandle parent)
    {
        _hostHandle = CreateWindowEx(
            0,
            "STATIC",
            string.Empty,
            WsChild | WsVisible | WsClipChildren | WsClipSiblings,
            0,
            0,
            1,
            1,
            parent.Handle,
            IntPtr.Zero,
            IntPtr.Zero,
            IntPtr.Zero);

        if (_hostHandle == IntPtr.Zero)
            throw new InvalidOperationException($"Unable to create WebView host window ({Marshal.GetLastWin32Error()}).");

        _ = InitializeAsync();
        return new PlatformHandle(_hostHandle, "HWND");
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        base.ArrangeOverride(finalSize);
        return finalSize;
    }

    protected override void DestroyNativeControlCore(IPlatformHandle control)
    {
        _isDisposed = true;
        _controller?.Close();
        _controller = null;

        if (_hostHandle != IntPtr.Zero)
        {
            DestroyWindow(_hostHandle);
            _hostHandle = IntPtr.Zero;
        }

        base.DestroyNativeControlCore(control);
    }

    private async Task InitializeAsync()
    {
        try
        {
            var userDataFolder = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "MdModManager",
                "EuterpeWebView");

            Directory.CreateDirectory(userDataFolder);
            var environment = await CoreWebView2Environment.CreateAsync(null, userDataFolder);
            if (_isDisposed || _hostHandle == IntPtr.Zero)
                return;

            _controller = await environment.CreateCoreWebView2ControllerAsync(_hostHandle);
            _controller.CoreWebView2.Settings.AreDevToolsEnabled = false;
            _controller.CoreWebView2.Settings.AreDefaultContextMenusEnabled = true;
            _controller.CoreWebView2.NavigationStarting += (_, e) =>
            {
                if (e.Uri.StartsWith("euterpe:", StringComparison.OrdinalIgnoreCase))
                {
                    e.Cancel = true;
                    RuntimeLog.Write("EuterpeWebView", "Blocked Euterpe custom-protocol navigation.");
                }
            };
            _controller.CoreWebView2.DownloadStarting += OnDownloadStarting;
            _controller.CoreWebView2.NavigationCompleted += async (_, e) =>
            {
                RuntimeLog.Write("EuterpeWebView", $"Navigation completed: success={e.IsSuccess}, error={e.WebErrorStatus}");

                if (e.IsSuccess)
                    await ApplyWebsiteLayoutRulesAsync();
            };

            UpdateBounds();
            await NavigateAsync(Source);
        }
        catch (Exception ex)
        {
            RuntimeLog.Write("EuterpeWebView", $"WebView2 initialization failed: {ex}");
        }
    }

    private Task NavigateAsync(Uri? source)
    {
        if (_controller?.CoreWebView2 is not null && source is not null)
        {
            RuntimeLog.Write("EuterpeWebView", $"Navigation starting: {source}");
            _controller.CoreWebView2.Navigate(source.AbsoluteUri);
        }

        return Task.CompletedTask;
    }

    private async Task ApplyWebsiteLayoutRulesAsync()
    {
        try
        {
            if (_controller?.CoreWebView2 is null)
                return;

            var downloadMenuResult = await _controller.CoreWebView2.ExecuteScriptAsync(DownloadMenuScript);
            var scrollResult = await _controller.CoreWebView2.ExecuteScriptAsync(RestoreScrollPositionScript);
            RuntimeLog.Write("EuterpeWebView", $"Download menu rule: {downloadMenuResult}; scroll rule: {scrollResult}");
        }
        catch (Exception ex)
        {
            RuntimeLog.Write("EuterpeWebView", $"Unable to apply website layout rules: {ex.Message}");
        }
    }

    private void OnDownloadStarting(object? sender, CoreWebView2DownloadStartingEventArgs e)
    {
        var displayName = Path.GetFileName(e.ResultFilePath);
        var isZipDownload = Path.GetExtension(displayName).Equals(".zip", StringComparison.OrdinalIgnoreCase);
        RuntimeLog.Write(
            "EuterpeWebView",
            $"Download starting: uri={e.DownloadOperation.Uri}, file={displayName}, zip={isZipDownload}");
        if (!Uri.TryCreate(e.DownloadOperation.Uri, UriKind.Absolute, out var downloadUri) ||
            !IsEuterpeDownloadUri(downloadUri) ||
            !isZipDownload)
        {
            return;
        }

        var tempDirectory = Path.Combine(Path.GetTempPath(), "MuseDashTOOL", "EuterpeDownloads");
        Directory.CreateDirectory(tempDirectory);
        var temporaryZipPath = Path.Combine(tempDirectory, $"{Guid.NewGuid():N}.zip");
        var download = e.DownloadOperation;
        var completed = 0;

        e.Handled = true;
        e.ResultFilePath = temporaryZipPath;
        RuntimeLog.Write("EuterpeWebView", $"Taking over ZIP download: {downloadUri.GetLeftPart(UriPartial.Path)}");

        download.StateChanged += async (_, _) =>
        {
            if (download.State != CoreWebView2DownloadState.Completed || Interlocked.Exchange(ref completed, 1) != 0)
                return;

        await InstallDownloadedZipAsync(temporaryZipPath, displayName);
        };
    }

    private static bool IsEuterpeDownloadUri(Uri uri)
    {
        if (uri.Host.EndsWith("euterpe-org.com", StringComparison.OrdinalIgnoreCase))
            return true;

        return uri.OriginalString.StartsWith("blob:https://euterpe-org.com/", StringComparison.OrdinalIgnoreCase);
    }

    private static async Task InstallDownloadedZipAsync(string temporaryZipPath, string displayName)
    {
        try
        {
            await Task.Run(() =>
            {
                using (var archive = ZipFile.OpenRead(temporaryZipPath))
                {
                    var isChartPackage = archive.Entries.Any(entry =>
                        entry.Name.Equals("info.json", StringComparison.OrdinalIgnoreCase) ||
                        entry.Name.Equals("map.json", StringComparison.OrdinalIgnoreCase) ||
                        entry.Name.EndsWith(".epk", StringComparison.OrdinalIgnoreCase));
                    if (!isChartPackage)
                        throw new InvalidDataException("下载的 ZIP 中未找到可转换的谱面文件。");
                }

                var configService = Ioc.Default.GetRequiredService<IConfigService>();
                var targetDirectory = ChartDownloadPathHelper.GetDefaultDownloadDirectory(configService.Config);
                if (string.IsNullOrWhiteSpace(targetDirectory))
                    throw new InvalidOperationException("未设置有效的游戏目录。");

                var baseName = Path.GetFileNameWithoutExtension(displayName);
                if (string.IsNullOrWhiteSpace(baseName))
                    baseName = "Euterpe 谱面";

                var destinationPath = GetUniqueMdmPath(targetDirectory, baseName);
                File.Move(temporaryZipPath, destinationPath);
                ChartService.ConvertEpkToInfoJsonInPlace(destinationPath);

                var chartService = Ioc.Default.GetRequiredService<IChartService>();
                var chart = chartService.LoadSingleChart(destinationPath)
                    ?? throw new InvalidDataException("谱面转换后无法读取。");
                Ioc.Default.GetRequiredService<IChartIndexService>().AddToIndex(chart);
                Dispatcher.UIThread.Post(() =>
                    Ioc.Default.GetRequiredService<IDownloadManagerService>().SessionDownloadedFiles.Add(destinationPath));
            });

            Ioc.Default.GetRequiredService<INotificationService>().ShowSuccess("谱面已下载、转换并安装");
        }
        catch (Exception ex)
        {
            RuntimeLog.Write("EuterpeWebView", $"ZIP install failed: {ex}");
            Ioc.Default.GetRequiredService<INotificationService>().ShowFailure("谱面安装失败", ex.Message);
        }
        finally
        {
            try
            {
                if (File.Exists(temporaryZipPath))
                    File.Delete(temporaryZipPath);
            }
            catch
            {
            }
        }
    }

    private static string GetUniqueMdmPath(string directory, string baseName)
    {
        var invalidCharacters = Path.GetInvalidFileNameChars();
        var safeName = string.Concat(baseName.Select(character => invalidCharacters.Contains(character) ? '_' : character)).Trim();
        if (string.IsNullOrWhiteSpace(safeName))
            safeName = "Euterpe 谱面";

        var path = Path.Combine(directory, $"{safeName}.mdm");
        for (var suffix = 2; File.Exists(path); suffix++)
            path = Path.Combine(directory, $"{safeName} ({suffix}).mdm");
        return path;
    }

    private void UpdateBounds()
    {
        if (_controller is null || _hostHandle == IntPtr.Zero)
            return;

        GetClientRect(_hostHandle, out var rect);
        var width = rect.Right - rect.Left;
        var height = rect.Bottom - rect.Top;
        _controller.Bounds = new DrawingRectangle(0, 0, width, height);

        var scale = TopLevel.GetTopLevel(this)?.RenderScaling ?? 1;
        RuntimeLog.Write(
            "EuterpeWebView",
            $"Layout: Avalonia={Bounds.Width:F1}x{Bounds.Height:F1}, client={width}x{height}, scale={scale:F2}");
    }

    private void SyncNativeLayout()
    {
        TryUpdateNativeControlPosition();
        UpdateBounds();
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateWindowEx(
        uint extendedStyle,
        string className,
        string windowName,
        uint style,
        int x,
        int y,
        int width,
        int height,
        IntPtr parent,
        IntPtr menu,
        IntPtr instance,
        IntPtr parameter);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool DestroyWindow(IntPtr window);

    [DllImport("user32.dll")]
    private static extern bool GetClientRect(IntPtr window, out NativeRect rect);

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }
}
