using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.DependencyInjection;
using System;
using System.Collections.ObjectModel;
using System.IO;
using System.Threading.Tasks;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using MdModManager.Services;
using MdModManager.Models;

namespace MdModManager.ViewModels;

// 赞助打赏视图模型
public partial class SponsorViewModel : ViewModelBase
{
    private readonly ISponsorService? _sponsorService;

    [ObservableProperty]
    private ObservableCollection<SponsorInfo> _sponsors = new();

    [ObservableProperty]
    private string _letterText = "";

    [ObservableProperty]
    private Bitmap? _qrCodeImage;

    [ObservableProperty]
    private bool _isQRCodeVisible;

    [ObservableProperty]
    private string _selectedPaymentMethod = "";

    [ObservableProperty]
    private bool _isLetterPopupOpen;

    [ObservableProperty]
    private bool _isBackButtonVisible = true;

    public bool IsAlipaySelected => SelectedPaymentMethod == "Alipay";
    public bool IsWeChatSelected => SelectedPaymentMethod == "WeChat";

    [RelayCommand]
    private void OpenLetterPopup()
    {
        IsLetterPopupOpen = true;
    }

    [RelayCommand]
    private void CloseLetterPopup()
    {
        IsLetterPopupOpen = false;
    }

    public SponsorViewModel()
    {
        _sponsorService = Ioc.Default.GetService<ISponsorService>();
    }

    public async Task InitializeAsync()
    {
        InitLetter();
        await LoadSponsorsAsync();
        // 初始不显示收款码，由用户点击后展示
        SelectedPaymentMethod = "";
        IsQRCodeVisible = false;
        QrCodeImage = null;
    }

    // 初始化感谢信
    private void InitLetter()
    {
        if (I18nService.Instance.CurrentLanguage == "en-US")
        {
            LetterText = "To the friend reading this letter: hello! First of all, I want to thank you from the bottom of my heart for using MuseDashTOOL. Thank you for being here, and for supporting this project.\n" +
                "When I first started building MuseDashTOOL, it was simply because the original MuseDashModTool (the predecessor to Euterpe) hadn't been updated in a long time. I thought, 'Why not try making one myself?' So, to be honest, a lot of the early features were heavily borrowed from MDMT - anyone who used the beta versions probably noticed that! (laughs) But as time went on, I found myself flooded with new ideas, and MuseDashTOOL gradually grew into something much bigger than I ever anticipated.\n" +
                "As a relatively new player to Muse Dash myself back then, I knew exactly how frustrating it was for a beginner to install custom charts. You had to dig through endless tutorials and jump between different websites - so I built the one-click installer. I also knew the struggle of trying to find specific charts: coping with high latency on the MDMC website, or constantly asking around in various chat groups. It was such a hassle, so I integrated the entire chart database directly into MuseDashTOOL. I could go on forever about the bugs I fought and the hurdles I faced, but what matters most is this: I wanted to make sure that future players wouldn't have to go through any of those headaches. That was my primary motivation from day one.\n" +
                "Of course, MuseDashTOOL didn't become what it is overnight. In the beginning, the interface was crude and the features were bare. I spent countless hours debugging, tweaking, and refining. Sometimes, a tiny bug or a simple feature request would take hours of rewriting (partly due to my perfectionism). Whenever I felt like giving up, I would open Bilibili or check our chat groups to read your discussions. Whether it was praise or constructive criticism, it made me realize that people were actually watching, and that this tool was genuinely helping someone. I wasn't just working in a vacuum. It is safe to say that without all of you, MuseDashTOOL would not exist today. Thank you, once again. For a developer, there is no greater joy than knowing your creation is being loved and used.\n" +
                "While I developed the software itself on my own, this project would be nothing without our incredible community of chart designers. If there's one thing I've learned from the Muse Dash community, it's that people here are incredibly kind and passionate. We have so many talented creators who dedicate their own time to map beautiful charts completely for free, and who worked with me to bring scattered, hard-to-find charts into one place. There are also many non-creators who volunteer to moderate the chat groups and help out new players every day. This amazing atmosphere is what keeps me going. I'm honored to contribute my own small part to such a warm and friendly community.\n" +
                "Lastly, even though I've poured hundreds of hours into MuseDashTOOL, this is by no means the end of the road. I will keep updating it. It's far from perfect, and you might still run into occasional issues - after all, this is the very first software I've ever developed entirely by myself. If you do encounter any bugs, I ask for your understanding, and please know that I am always listening to your feedback.\n" +
                "That's about all I wanted to share regarding my journey with MuseDashTOOL. As for why this letter is tucked away here - well, first of all, I didn't know where else to put it! Secondly, since you've opened the support page, it means MuseDashTOOL has probably helped you in some way, so I thought you might actually be interested in reading my rambling. Think of it as a targeted delivery (after all, dumping this emotional wall of text on someone who just opened the app for the first time would be pretty awkward!) (\u0e51\u25e1\u0e51)";
        }
        else
        {
                    LetterText = "正在看这封信的朋友，你们好，首先感谢你们使用喵斯兔！同时也感谢你们对喵斯兔的支持！\n" +
            "我一开始制作喵斯兔的时候，只是因为MuseDashModTool（Euterpe的前身）迟迟不更新，于是萌生了自己做一个的想法，所以前期的很多功能都是抄的MDMT，如果用过beta版本的朋友应该能发现。（笑）不过越做到后面，我发现自己的想法越来越多，喵斯兔也慢慢的越来越庞大。\n" +
                     "因为我是一个接触喵斯快跑不久的新玩家，所以我明白，一个小白想要安装自制谱，需要翻多少个教程，需要上多少个网站，于是我做了一键安装自制谱；我也明白，想要去找到自己想玩的谱面，需要顶着高延迟在mdmc去找有多么痛苦，需要游走于各个qq群之间，又有多么麻烦，于是我把所有的谱面都整合在喵斯兔进行下载；如果让我说我踩了多少坑，经历了多少麻烦事，我可以一直喋喋不休，但我在这里最想说的是，我希望以后的所有喵斯玩家，在喵斯兔里不再有这些类似的烦恼。这也是我制作喵斯兔的初衷。\n" +
                     "当然，如今的喵斯兔绝不是一蹴而就的，最开始的喵斯兔，UI简陋，功能又少，我一遍遍的调试，一遍遍的修改，甚至有时一个小小的bug或者想加一个小小的功能我需要花大量的时间去改（可能也是因为我有强迫症）。每当我想要放弃的时候，我就会打开b站，或者打开qq群，看看你们的意见与讨论，不管褒扬还是批评，我都会觉得，我是有人在注视的，我做的软件对一些人是有帮助的，我并不是在沉浸在自己的世界里埋头苦干。可以说，没有你们，就绝对没有现在的喵斯兔。再一次感谢你们！对于一个开发者，最大的欢欣莫过于你们的焦点。\n" +
                     "虽然说，喵斯兔的软件本体开发是我一个人完成的，不过还有一些其他的工作，是离不开各位谱师的帮助。我想说，喵斯快跑这个圈子给我最大的感受就是大家都是有爱的。这里有这么多无偿奉献的谱师，愿意无偿花时间写这么多谱子；愿意花时间配合喵斯兔，将来源于各处零散的谱面收集起来；更有很多非谱师，在自发的管理各个qq群，为群友解决各种问题；这种氛围也让我有了继续下去的动力，我愿意为这群可爱友善的人们也贡献一点我微不足道的力量。\n" +
                     "最后我想说，喵斯兔开发虽然已经消耗了我成百上千个小时，但是这绝不会是它的终点，未来还会继续更新，不过它确实不是完美的，可能会有这样那样的问题，毕竟这也是我第一款自己独立开发的软件，如果你不幸遇到了问题，请多多海涵，我也随时在倾听你的反馈。\n" +
                     "关于我开发喵斯兔的心路历程以及一些想说的话到这里就结束了。至于为什么这封信放在这里，首先是我也不知道除了这里还能放在哪里了；其次，你既然点开了这个页面，说明喵斯兔应该或多或少地帮助到了你，那么你也许会有兴趣看完我的长篇大论，也算是一个精准投放吧（毕竟对着一个刚打开喵斯兔的陌生用户说这么一堆煽情的话也怪尴尬的） (๑>◡<๑)";
        }
        LetterText = LetterText.Replace("\n", "\n\n");
    }

    // 加载赞助者名单
    private async Task LoadSponsorsAsync()
    {
        if (_sponsorService == null) return;
        var list = await _sponsorService.GetSponsorsAsync();
        if (list != null)
        {
            Sponsors.Clear();
            foreach (var item in list)
            {
                Sponsors.Add(item);
            }
        }
    }

    // 选择付款方式
    [RelayCommand]
    private void SelectPaymentMethod(string method)
    {
        if (SelectedPaymentMethod == method)
        {
            // 再次点击已选中的方式，进行收起
            SelectedPaymentMethod = "";
            IsQRCodeVisible = false;
            QrCodeImage?.Dispose();
            QrCodeImage = null;
            OnPropertyChanged(nameof(IsAlipaySelected));
            OnPropertyChanged(nameof(IsWeChatSelected));
            return;
        }

        SelectedPaymentMethod = method;
        IsQRCodeVisible = true;

        OnPropertyChanged(nameof(IsAlipaySelected));
        OnPropertyChanged(nameof(IsWeChatSelected));

        try
        {
            var assetPath = method == "Alipay" ? "avares://MuseDashTOOL/Assets/zfb.jpg" : "avares://MuseDashTOOL/Assets/wx.jpg";
            using var stream = AssetLoader.Open(new Uri(assetPath));
            var newImage = new Bitmap(stream);
            
            QrCodeImage?.Dispose();
            QrCodeImage = newImage;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Failed to load QR code image: {ex.Message}");
            QrCodeImage?.Dispose();
            QrCodeImage = null;
        }
    }

    // 返回欢迎页
    [RelayCommand]
    private async Task GoBackAsync()
    {
        var desktop = Avalonia.Application.Current?.ApplicationLifetime
            as Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime;
        if (desktop?.MainWindow?.DataContext is MainWindowViewModel mainVm)
        {
            mainVm.CleanupCurrentPage();
            var welcomeVm = Ioc.Default.GetRequiredService<WelcomeViewModel>();
            mainVm.CurrentPage = welcomeVm;
            await welcomeVm.InitializeAsync();
        }
    }

    // 释放资源，防止 Bitmap 内存泄漏
    public void Cleanup()
    {
        QrCodeImage?.Dispose();
        QrCodeImage = null;
    }
}
