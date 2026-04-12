using System.IO;
using System.Net.Http;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using WpfEllipse = System.Windows.Shapes.Ellipse;
using HandshakeDVPN.Services;

namespace HandshakeDVPN;

public partial class MainWindow : Window
{
    // ─── State ───
    private string? _userAddr;
    private string? _selectedNode;
    private string _connState = "off"; // off | ing | on
    private string _activeFilter = "";
    private List<HnsNodeInfo> _allNodes = new();
    private string? _generatedMnemonic;
    private int _balFailCount;
    private string _payMode = "gb"; // "gb" or "hr"
    private HnsNodeInfo? _selectedNodeInfo;
    private bool _isClosing;
    private DateTime _lastAllocCheck;
    private string? _planSubId;
    private int _planId;
    private CancellationTokenSource? _connectCts;
    private CancellationTokenSource? _refreshCts;
    private string? _lastSessionId;
    private List<ActiveSession> _activeSessions = new();

    private readonly DispatcherTimer _statusPoll;
    private readonly DispatcherTimer _ipPoll;
    private readonly DispatcherTimer _balPoll;

    // ─── User persistence ───
    private static readonly string UserFile = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "HandshakeDVPN", "user.json");

    public MainWindow()
    {
        InitializeComponent();
        _statusPoll = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
        _statusPoll.Tick += async (_, _) => await PollStatus();
        _ipPoll = new DispatcherTimer { Interval = TimeSpan.FromSeconds(60) };
        _ipPoll.Tick += async (_, _) => await PollIp();
        _balPoll = new DispatcherTimer { Interval = TimeSpan.FromMinutes(5) };
        _balPoll.Tick += async (_, _) => await LoadBalance();
        Loaded += async (_, _) => await Init();
        SetHnsIcon();
    }

    private void SetHnsIcon()
    {
        // Render HNS crystalline slash as window icon
        var geo = Geometry.Parse("M38.848,15.331 L35.927,10.142 L41.586,10.144 C41.737,10.144 41.913,10.245 41.996,10.38 C42.086,10.529 42.545,11.286 43.097,12.196 C43.758,13.286 44.548,14.588 45,15.331 L38.848,15.331 Z M28.382,46.754 C28.245,47 28.067,47 28.01,47 L25.698,47 C24.474,46.999 23.063,46.998 22.223,46.998 L31.214,30.857 C31.363,30.589 31.36,30.262 31.204,29.997 C31.049,29.733 30.767,29.57 30.461,29.57 L15.137,29.59 L12.09,24.314 L34.409,24.314 C34.433,24.314 34.457,24.31 34.457,24.309 C34.508,24.306 34.558,24.301 34.605,24.289 C34.642,24.281 34.678,24.269 34.714,24.256 C34.733,24.248 34.751,24.241 34.77,24.232 C34.937,24.158 35.085,24.035 35.179,23.862 L38.858,17.06 L44.932,17.06 L28.382,46.754 Z M20.725,46.141 C20.53,45.819 20.276,45.401 19.999,44.946 C19.07,43.412 17.879,41.448 17.744,41.232 C17.699,41.16 17.678,40.982 17.773,40.813 C17.988,40.426 22.057,33.096 23.049,31.31 L28.99,31.302 L20.725,46.141 Z M10.593,36.04 L7.639,30.793 L10.616,25.213 L13.602,30.382 C12.674,32.129 11.226,34.854 10.593,36.04 Z M6.629,36.857 C5.126,36.857 3.631,36.856 3.411,36.856 C3.263,36.856 3.085,36.753 3.004,36.62 L2.043,35.036 C1.358,33.906 0.484,32.466 0,31.669 L6.152,31.669 L9.072,36.858 C8.435,36.858 7.533,36.857 6.629,36.857 Z M16.618,0.246 C16.755,0 16.932,0 16.989,0 L22.802,0.001 L13.786,16.142 C13.779,16.155 13.775,16.17 13.768,16.184 C13.755,16.211 13.743,16.238 13.732,16.267 C13.723,16.295 13.715,16.321 13.707,16.349 C13.7,16.375 13.695,16.401 13.69,16.427 C13.685,16.46 13.682,16.491 13.681,16.523 C13.68,16.538 13.677,16.551 13.677,16.566 C13.677,16.577 13.68,16.587 13.68,16.598 C13.681,16.63 13.685,16.661 13.69,16.692 C13.693,16.718 13.697,16.744 13.704,16.77 C13.71,16.797 13.72,16.823 13.729,16.85 C13.738,16.876 13.747,16.903 13.759,16.928 C13.771,16.953 13.784,16.977 13.798,17.001 C13.812,17.024 13.826,17.048 13.842,17.07 C13.858,17.093 13.877,17.114 13.896,17.136 C13.914,17.156 13.933,17.177 13.953,17.196 C13.973,17.215 13.994,17.231 14.015,17.247 C14.04,17.267 14.064,17.284 14.09,17.3 C14.1,17.306 14.107,17.313 14.117,17.319 C14.127,17.325 14.138,17.327 14.149,17.332 C14.193,17.355 14.24,17.374 14.289,17.389 C14.307,17.394 14.323,17.401 14.342,17.405 C14.405,17.42 14.469,17.429 14.536,17.429 L14.921,17.429 L29.866,17.41 C30.261,18.097 30.999,19.39 31.628,20.491 C32.111,21.337 32.51,22.035 32.824,22.584 L10.594,22.584 C10.584,22.584 10.575,22.587 10.566,22.588 C10.264,22.593 9.972,22.753 9.818,23.041 L6.139,29.939 L0.069,29.939 C2.719,25.184 16.441,0.563 16.618,0.246 Z M24.287,0.879 L24.827,1.767 C25.775,3.331 27.114,5.538 27.256,5.768 C27.301,5.84 27.322,6.017 27.226,6.187 L21.95,15.69 L16.011,15.698 L24.287,0.879 Z M34.405,10.954 L37.36,16.204 L34.347,21.772 C33.988,21.145 33.542,20.364 33.124,19.631 C32.297,18.182 31.694,17.127 31.354,16.538 C31.996,15.362 33.694,12.252 34.405,10.954 Z");
        var drawing = new GeometryDrawing(Brushes.Black, null, geo);
        var drawingImage = new DrawingImage(drawing);
        drawingImage.Freeze();
        Icon = drawingImage;
    }

    // ═══ INIT ═══

    private bool _initDone;
    private Task Init()
    {
        if (_initDone) return Task.CompletedTask;
        _initDone = true;
        App.Backend.OnProgress += (step, detail) =>
        {
            Dispatcher.Invoke(() =>
            {
                if (_connState == "ing") StatusSub.Text = detail ?? step;
                AddLog(detail ?? step);
            });
        };
        App.Backend.OnLog += (msg) =>
        {
            if (msg.StartsWith("WARN:")) return; // suppress SDK warnings (pagination, etc)
            Dispatcher.Invoke(() => AddLog(msg));
        };

        return Task.CompletedTask;
    }

    // ═══ AUTH ═══

    private void AuthTab_Click(object sender, RoutedEventArgs e)
    {
        var tag = (string)((Button)sender).Tag;
        ImportPanel.Visibility = tag == "import" ? Visibility.Visible : Visibility.Collapsed;
        GenPanel.Visibility = tag == "new" ? Visibility.Visible : Visibility.Collapsed;
        AuthTabImport.Foreground = FindBrush(tag == "import" ? "T1" : "T3");
        AuthTabImport.Background = tag == "import" ? FindBrush("Bg0") : Brushes.Transparent;
        AuthTabNew.Foreground = FindBrush(tag == "new" ? "T1" : "T3");
        AuthTabNew.Background = tag == "new" ? FindBrush("Bg0") : Brushes.Transparent;
    }

    private async void BtnImport_Click(object sender, RoutedEventArgs e)
    {
        var mn = TbMnemonic.Text.Trim();
        var words = mn.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (words.Length != 12 && words.Length != 24)
        {
            AuthErr.Text = "Mnemonic must be 12 or 24 words";
            return;
        }
        try
        {
            BtnImport.IsEnabled = false;
            AuthErr.Text = "";
            var r = await App.Backend.ImportWalletAsync(mn);
            if (r is { Valid: true })
            {
                _userAddr = r.Address;
                SaveUser(_userAddr, mn);
                AuthOverlay.Visibility = Visibility.Collapsed;
                await EnterApp();
            }
        }
        catch (Exception ex) { AuthErr.Text = ex.Message; }
        finally { BtnImport.IsEnabled = true; }
    }

    private async void BtnGen_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            BtnGen.IsEnabled = false;
            var w = await App.Backend.CreateWalletAsync();
            if (w != null)
            {
                _generatedMnemonic = w.Mnemonic;
                GenMnemonic.Text = w.Mnemonic;
                GenAddr.Text = w.Address;
                GenResult.Visibility = Visibility.Visible;
            }
        }
        catch (Exception ex) { AuthErr.Text = ex.Message; }
        finally { BtnGen.IsEnabled = true; }
    }

    private void BtnUseGen_Click(object sender, RoutedEventArgs e)
    {
        if (_generatedMnemonic != null)
        {
            TbMnemonic.Text = _generatedMnemonic;
            AuthTab_Click(AuthTabImport, e);
        }
    }

    private void BtnTestWallet_Click(object sender, RoutedEventArgs e)
    {
        // Generate a fresh test wallet — fund it with P2P tokens before connecting
        BtnGen_Click(sender, e);
    }

    // ═══ WALLET ═══

    private void BtnCloseWallet_Click(object sender, RoutedEventArgs e) => WalletOverlay.Visibility = Visibility.Collapsed;

    private async void BtnWallet_Click(object sender, RoutedEventArgs e)
    {
        var panel = WalletPanel;
        panel.Children.Clear();

        var addr = _userAddr ?? "—";

        // Balance (centered)
        var balStack = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 0, 0, 4) };
        var balText = MakeText("Loading...", 32, "T1", HorizontalAlignment.Center, fontWeight: FontWeights.Bold);
        balStack.Children.Add(balText);
        // P2P label with Sentinel shield
        var p2pRow = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 6, 0, 0) };
        var sentLogo = new Viewbox { Width = 20, Height = 22, Margin = new Thickness(0, 0, 6, 0), VerticalAlignment = VerticalAlignment.Center };
        var sentCanvas = new Canvas { Width = 241, Height = 241 };
        sentCanvas.Children.Add(new System.Windows.Shapes.Path { Fill = new SolidColorBrush(Color.FromRgb(0x01, 0x56, 0xFC)), Data = Geometry.Parse("M178.171 60.8573C179.801 60.8573 181.123 62.1775 181.123 63.8045V110.662V113.572C181.161 119.355 181.207 126.551 180.202 134.203C179.028 143.119 176.7 150.371 173.115 156.367C161.437 175.91 140.465 190.218 120.788 192.134C120.695 192.143 120.593 192.152 120.5 192.152C120.407 192.152 120.304 192.152 120.211 192.134C100.506 190.209 79.5436 175.901 67.8844 156.367C64.2898 150.361 61.9804 143.11 60.7977 134.203C59.7919 126.561 59.8385 119.355 59.8851 113.572V110.662L59.8944 63.8045C59.8944 62.1775 61.2167 60.8573 62.8464 60.8573H178.171ZM178.171 55.0001H62.8464C57.9853 55.0001 54.0276 58.9514 54.0276 63.8045V113.554C53.9717 119.16 53.9065 126.867 54.9774 134.965C56.2625 144.69 58.8328 152.667 62.8371 159.361C68.9833 169.662 77.588 178.755 87.7106 185.672C97.917 192.645 108.934 196.894 119.568 197.944C119.876 197.981 120.183 198 120.49 198C120.798 198 121.105 197.981 121.412 197.944C132.038 196.903 143.045 192.664 153.261 185.681C163.383 178.764 171.988 169.662 178.144 159.361C182.157 152.648 184.727 144.671 186.003 134.965C187.065 126.932 187.018 119.513 186.981 113.563V110.644V63.8045C186.981 58.9514 183.023 55.0001 178.162 55.0001L178.171 55.0001Z") });
        sentCanvas.Children.Add(new System.Windows.Shapes.Path { Fill = new SolidColorBrush(Color.FromRgb(0x01, 0x56, 0xFC)), Data = Geometry.Parse("M170.145 120.192C171.011 120.843 172.24 120.229 172.24 119.151L172.213 113.228V71.0006C172.222 70.2754 171.635 69.6804 170.909 69.6804H70.0832C69.3569 69.6804 68.7609 70.2661 68.7609 71.0006V96.9769C68.7609 98.0926 69.0216 99.1896 69.5152 100.184C70.018 101.179 70.7444 102.044 71.6291 102.713L147.479 159.333C148.14 159.826 148.186 160.783 147.591 161.341C146.184 162.662 144.713 163.907 143.176 165.079C142.711 165.432 142.059 165.432 141.593 165.088L70.8655 112.168C69.9994 111.518 68.7702 112.131 68.7702 113.219C68.7702 122.293 67.9693 139.223 75.6055 152.193C84.1357 166.687 98.7841 178.123 113.852 181.823C116.031 182.353 118.201 182.734 120.38 182.92C120.463 182.929 120.547 182.929 120.631 182.92C130.493 182.037 140.532 177.537 149.146 170.741C151.306 169.03 153.374 167.189 155.338 165.209C159.222 161.304 162.602 156.934 165.405 152.202C167.938 147.916 169.531 143.202 170.546 138.489C170.769 137.466 170.965 136.434 171.132 135.402C171.579 132.697 170.48 129.973 168.292 128.327L104.083 80.3814C103.077 79.6283 103.599 78.0199 104.874 78.0199H113.367C113.656 78.0199 113.926 78.1129 114.159 78.2803L170.145 120.183V120.192Z") });
        sentLogo.Child = sentCanvas;
        p2pRow.Children.Add(sentLogo);
        p2pRow.Children.Add(MakeText("P2P", 16, "T2", fontWeight: FontWeights.SemiBold));
        balStack.Children.Add(p2pRow);
        panel.Children.Add(balStack);

        // Deposit section
        panel.Children.Add(SettingsDivider());
        panel.Children.Add(SettingsSection("DEPOSIT"));
        panel.Children.Add(MakeText("Send P2P tokens to this address:", 11, "T3", margin: new Thickness(0, 0, 0, 8)));

        // Address with copy
        var addrBorder = new Border
        {
            Background = FindBrush("Bg2"), CornerRadius = new CornerRadius(8),
            Padding = new Thickness(14, 10, 14, 10), Margin = new Thickness(0, 0, 0, 8),
        };
        var addrGrid = new Grid();
        addrGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        addrGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var addrTb = new TextBox
        {
            Text = addr, IsReadOnly = true, BorderThickness = new Thickness(0),
            Background = Brushes.Transparent, FontSize = 11.5,
            FontFamily = (FontFamily)Application.Current.FindResource("Mono"),
            Foreground = FindBrush("T1"), TextWrapping = TextWrapping.Wrap,
        };
        Grid.SetColumn(addrTb, 0);
        var copyBtn = new Button
        {
            Content = "Copy", FontSize = 10, Padding = new Thickness(10, 4, 10, 4),
            Cursor = Cursors.Hand, Background = FindBrush("Acc"), Foreground = Brushes.White,
            VerticalAlignment = VerticalAlignment.Center,
        };
        copyBtn.Template = CreateBtnTemplate();
        copyBtn.Click += (_, _) => { Clipboard.SetText(addr); AddLog("Address copied"); copyBtn.Content = "Copied"; };
        Grid.SetColumn(copyBtn, 1);
        addrGrid.Children.Add(addrTb);
        addrGrid.Children.Add(copyBtn);
        addrBorder.Child = addrGrid;
        panel.Children.Add(addrBorder);

        // QR code
        var qrImg = new Image { Width = 180, Height = 180, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 4, 0, 0) };
        panel.Children.Add(qrImg);
        _ = Task.Run(async () =>
        {
            try
            {
                using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
                var bytes = await http.GetByteArrayAsync($"https://api.qrserver.com/v1/create-qr-code/?size=200x200&data={Uri.EscapeDataString(addr)}");
                var bmp = new BitmapImage();
                bmp.BeginInit();
                bmp.StreamSource = new MemoryStream(bytes);
                bmp.CacheOption = BitmapCacheOption.OnLoad;
                bmp.EndInit();
                bmp.Freeze();
                Dispatcher.Invoke(() => qrImg.Source = bmp);
            }
            catch { }
        });

        // Withdraw section
        panel.Children.Add(SettingsDivider());
        panel.Children.Add(SettingsSection("WITHDRAW"));

        var tbToAddr = new TextBox
        {
            FontSize = 11.5, FontFamily = (FontFamily)Application.Current.FindResource("Mono"),
            Background = FindBrush("Bg2"), Foreground = FindBrush("T1"),
            BorderBrush = FindBrush("Bdr"), BorderThickness = new Thickness(1),
            Padding = new Thickness(10, 8, 10, 8), Margin = new Thickness(0, 0, 0, 6),
        };
        panel.Children.Add(MakeText("Recipient address:", 11, "T3", margin: new Thickness(0, 0, 0, 4)));
        panel.Children.Add(tbToAddr);

        var amountRow = new Grid { Margin = new Thickness(0, 0, 0, 8) };
        amountRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        amountRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var tbSendAmt = new TextBox
        {
            FontSize = 12, FontFamily = (FontFamily)Application.Current.FindResource("Mono"),
            Background = FindBrush("Bg2"), Foreground = FindBrush("T1"),
            BorderBrush = FindBrush("Bdr"), BorderThickness = new Thickness(1),
            Padding = new Thickness(10, 8, 10, 8),
        };
        Grid.SetColumn(tbSendAmt, 0);
        var unitLabel = MakeText("P2P", 12, "T3");
        unitLabel.VerticalAlignment = VerticalAlignment.Center;
        unitLabel.Margin = new Thickness(8, 0, 0, 0);
        Grid.SetColumn(unitLabel, 1);
        amountRow.Children.Add(tbSendAmt);
        amountRow.Children.Add(unitLabel);
        panel.Children.Add(MakeText("Amount:", 11, "T3", margin: new Thickness(0, 0, 0, 4)));
        panel.Children.Add(amountRow);

        var sendStatus = MakeText("", 11, "T3", margin: new Thickness(0, 4, 0, 0));

        var sendBtn = new Button
        {
            Content = "Send", FontSize = 13, FontWeight = FontWeights.SemiBold,
            Padding = new Thickness(0, 10, 0, 10), Cursor = Cursors.Hand,
            Background = FindBrush("Acc"), Foreground = Brushes.White,
            HorizontalAlignment = HorizontalAlignment.Stretch,
        };
        sendBtn.Style = (Style)Application.Current.FindResource("BtnAcc");
        sendBtn.Click += async (_, _) =>
        {
            var to = tbToAddr.Text.Trim();
            var amtText = tbSendAmt.Text.Trim();
            if (string.IsNullOrEmpty(to) || !to.StartsWith("sent1"))
            {
                sendStatus.Text = "Invalid address — must start with sent1";
                sendStatus.Foreground = FindBrush("Red");
                return;
            }
            if (!double.TryParse(amtText, out var amt) || amt <= 0)
            {
                sendStatus.Text = "Invalid amount";
                sendStatus.Foreground = FindBrush("Red");
                return;
            }
            var udvpn = (long)(amt * 1_000_000);
            sendBtn.IsEnabled = false;
            sendStatus.Text = "Broadcasting...";
            sendStatus.Foreground = FindBrush("T3");
            try
            {
                var backend = (NativeVpnClient)App.Backend;
                await backend.EnsureChainPublicAsync();
                var chain = backend.GetChain()!;
                var wallet = backend.GetWallet()!;
                var msg = Sentinel.SDK.Core.MessageBuilder.Send(wallet.Address, to, udvpn);
                var txBuilder = new Sentinel.SDK.Core.TransactionBuilder(wallet, chain);
                var tx = await txBuilder.BroadcastAsync(msg);
                if (tx.Success)
                {
                    sendStatus.Text = $"Sent! TX: {tx.TxHash?[..16]}...";
                    sendStatus.Foreground = FindBrush("Green");
                    AddLog($"Sent {amt} P2P to {Trunc(to, 12, 6)}");
                    await LoadBalance();
                }
                else
                {
                    sendStatus.Text = $"Failed: {tx.RawLog}";
                    sendStatus.Foreground = FindBrush("Red");
                }
            }
            catch (Exception ex)
            {
                sendStatus.Text = $"Error: {ex.Message}";
                sendStatus.Foreground = FindBrush("Red");
            }
            finally { sendBtn.IsEnabled = true; }
        };
        panel.Children.Add(sendBtn);
        panel.Children.Add(sendStatus);

        // Export section
        panel.Children.Add(SettingsDivider());
        panel.Children.Add(SettingsSection("EXPORT"));

        // Recovery Phrase
        var mnResult = new Border
        {
            Visibility = Visibility.Collapsed, Background = FindBrush("RedDim"),
            CornerRadius = new CornerRadius(8), Padding = new Thickness(12, 10, 12, 10),
            Margin = new Thickness(0, 6, 0, 0),
        };
        var exportMnBtn = MakeExportButton("Recovery Phrase", mnResult, () =>
        {
            var w = ((NativeVpnClient)App.Backend).GetWallet();
            return w?.ExportMnemonicString() ?? "No mnemonic available";
        });
        panel.Children.Add(exportMnBtn);
        panel.Children.Add(mnResult);

        // Private Key
        var pkResult = new Border
        {
            Visibility = Visibility.Collapsed, Background = FindBrush("RedDim"),
            CornerRadius = new CornerRadius(8), Padding = new Thickness(12, 10, 12, 10),
            Margin = new Thickness(0, 6, 0, 0),
        };
        var exportPkBtn = MakeExportButton("Public Key", pkResult, () =>
        {
            var w = ((NativeVpnClient)App.Backend).GetWallet();
            if (w == null) return "No wallet";
            return Convert.ToHexString(w.GetPublicKeyCompressed()).ToLower();
        });
        panel.Children.Add(exportPkBtn);
        panel.Children.Add(pkResult);

        WalletOverlay.Visibility = Visibility.Visible;

        // Load balance
        try
        {
            var bal = await App.Backend.GetBalanceAsync();
            if (bal != null) balText.Text = bal.P2P.ToString("F2");
            else balText.Text = "—";
        }
        catch { balText.Text = "—"; }
    }

    private void BtnCloseSettings_Click(object sender, RoutedEventArgs e)
    {
        SettingsOverlay.Visibility = Visibility.Collapsed;
    }

    private void BtnSettings_Click(object sender, RoutedEventArgs e)
    {
        var settings = ((NativeVpnClient)App.Backend).Settings;
        var stack = SettingsPanel;
        stack.Children.Clear();

        // ─── DNS ───
        stack.Children.Add(SettingsSection("DNS RESOLUTION"));

        var dnsRadios = new StackPanel { Margin = new Thickness(0, 0, 0, 8) };
        var rbHns = new RadioButton { Content = "  Handshake  (103.196.38.38, 103.196.38.39)", IsChecked = settings.DnsPreset == "handshake", Tag = "handshake", Margin = new Thickness(0, 4, 0, 4), FontSize = 12, Foreground = FindBrush("T1") };
        var rbGoogle = new RadioButton { Content = "  Google  (8.8.8.8, 8.8.4.4)", IsChecked = settings.DnsPreset == "google", Tag = "google", Margin = new Thickness(0, 4, 0, 4), FontSize = 12, Foreground = FindBrush("T1") };
        var rbCf = new RadioButton { Content = "  Cloudflare  (1.1.1.1, 1.0.0.1)", IsChecked = settings.DnsPreset == "cloudflare", Tag = "cloudflare", Margin = new Thickness(0, 4, 0, 4), FontSize = 12, Foreground = FindBrush("T1") };
        var rbCustomDns = new RadioButton { Content = "  Custom DNS", IsChecked = settings.DnsPreset == "custom", Tag = "custom", Margin = new Thickness(0, 4, 0, 4), FontSize = 12, Foreground = FindBrush("T1") };
        dnsRadios.Children.Add(rbHns);
        dnsRadios.Children.Add(rbGoogle);
        dnsRadios.Children.Add(rbCf);
        dnsRadios.Children.Add(rbCustomDns);
        stack.Children.Add(dnsRadios);

        var tbCustomDns = new TextBox
        {
            Text = settings.CustomDns,
            FontSize = 11.5,
            FontFamily = (FontFamily)Application.Current.FindResource("Mono"),
            Background = FindBrush("Bg2"),
            Foreground = FindBrush("T1"),
            BorderBrush = FindBrush("Bdr"),
            BorderThickness = new Thickness(1),
            Padding = new Thickness(10, 7, 10, 7),
            IsEnabled = settings.DnsPreset == "custom",
        };
        tbCustomDns.SetValue(TagProperty, "customDns");
        // placeholder via watermark
        if (string.IsNullOrEmpty(tbCustomDns.Text)) tbCustomDns.Text = "9.9.9.9, 149.112.112.112";
        rbCustomDns.Checked += (_, _) => tbCustomDns.IsEnabled = true;
        rbHns.Checked += (_, _) => tbCustomDns.IsEnabled = false;
        rbGoogle.Checked += (_, _) => tbCustomDns.IsEnabled = false;
        rbCf.Checked += (_, _) => tbCustomDns.IsEnabled = false;
        stack.Children.Add(tbCustomDns);

        stack.Children.Add(SettingsNote("Handshake DNS resolves both ICANN domains and Handshake TLDs (.forever, .badass, etc)."));
        stack.Children.Add(SettingsDivider());

        // ─── LCD ───
        stack.Children.Add(SettingsSection("LCD ENDPOINTS (REST API)"));
        stack.Children.Add(SettingsEndpoint("https://lcd.sentinel.co", true));
        stack.Children.Add(SettingsEndpoint("https://api.sentinel.quokkastake.io"));
        stack.Children.Add(SettingsEndpoint("https://sentinel-api.polkachu.com"));
        stack.Children.Add(SettingsEndpoint("https://sentinel.api.trivium.network:1317"));
        stack.Children.Add(SettingsNote("Custom LCD (overrides defaults):"));
        var tbCustomLcd = new TextBox
        {
            Text = settings.CustomLcd,
            FontSize = 11,
            FontFamily = (FontFamily)Application.Current.FindResource("Mono"),
            Background = FindBrush("Bg2"),
            Foreground = FindBrush("T1"),
            BorderBrush = FindBrush("Bdr"),
            BorderThickness = new Thickness(1),
            Padding = new Thickness(10, 7, 10, 7),
            Margin = new Thickness(0, 4, 0, 0),
        };
        if (string.IsNullOrEmpty(tbCustomLcd.Text)) tbCustomLcd.Foreground = FindBrush("T3");
        stack.Children.Add(tbCustomLcd);
        stack.Children.Add(SettingsDivider());

        // ─── RPC ───
        stack.Children.Add(SettingsSection("RPC ENDPOINTS"));
        stack.Children.Add(SettingsEndpoint("https://rpc.sentinel.co:443", true));
        stack.Children.Add(SettingsEndpoint("https://sentinel-rpc.polkachu.com"));
        stack.Children.Add(SettingsEndpoint("https://rpc.mathnodes.com"));
        stack.Children.Add(SettingsNote("Custom RPC (overrides defaults):"));
        var tbCustomRpc = new TextBox
        {
            Text = settings.CustomRpc,
            FontSize = 11,
            FontFamily = (FontFamily)Application.Current.FindResource("Mono"),
            Background = FindBrush("Bg2"),
            Foreground = FindBrush("T1"),
            BorderBrush = FindBrush("Bdr"),
            BorderThickness = new Thickness(1),
            Padding = new Thickness(10, 7, 10, 7),
            Margin = new Thickness(0, 4, 0, 0),
        };
        stack.Children.Add(tbCustomRpc);
        stack.Children.Add(SettingsDivider());

        // ─── Tunnel ───
        stack.Children.Add(SettingsSection("TUNNEL"));
        var cbFullTunnel = new CheckBox { Content = "  Full Tunnel — route all traffic through VPN", IsChecked = settings.FullTunnel, FontSize = 12, Foreground = FindBrush("T1"), Margin = new Thickness(0, 4, 0, 4) };
        var cbSystemProxy = new CheckBox { Content = "  System Proxy — OS SOCKS5 proxy for V2Ray", IsChecked = settings.SystemProxy, FontSize = 12, Foreground = FindBrush("T1"), Margin = new Thickness(0, 4, 0, 4) };
        stack.Children.Add(cbFullTunnel);
        stack.Children.Add(cbSystemProxy);
        stack.Children.Add(SettingsDivider());

        // ─── Protocols ───
        stack.Children.Add(SettingsSection("PROTOCOLS"));
        var tbMtu = SettingsInput("WireGuard MTU", settings.WgMtu.ToString(), "1280-1500");
        var tbKeepalive = SettingsInput("Keepalive (sec)", settings.WgKeepalive.ToString(), "15-60");
        var tbSocksPort = SettingsInput("V2Ray SOCKS Port", settings.V2RaySocksPort.ToString(), "1024-65535");
        stack.Children.Add(tbMtu.row);
        stack.Children.Add(tbKeepalive.row);
        stack.Children.Add(tbSocksPort.row);
        stack.Children.Add(SettingsDivider());

        // ─── Session ───
        stack.Children.Add(SettingsSection("SESSION DEFAULTS"));
        var tbDefaultGb = SettingsInput("Default GB", settings.DefaultGb.ToString(), "1-100");
        var cbPreferHourly = new CheckBox { Content = "  Prefer hourly pricing when cheaper", IsChecked = settings.PreferHourly, FontSize = 12, Foreground = FindBrush("T1"), Margin = new Thickness(0, 4, 0, 4) };
        stack.Children.Add(tbDefaultGb.row);
        stack.Children.Add(cbPreferHourly);
        stack.Children.Add(SettingsDivider());

        // ─── Polling ───
        stack.Children.Add(SettingsSection("POLLING INTERVALS"));
        var tbStatusPoll = SettingsInput("Status check (sec)", settings.StatusPollSec.ToString(), "1-30");
        var tbIpPoll = SettingsInput("IP check (sec)", settings.IpCheckSec.ToString(), "30-300");
        var tbBalPoll = SettingsInput("Balance check (sec)", settings.BalanceCheckSec.ToString(), "60-600");
        var tbAllocPoll = SettingsInput("Allocation check (sec)", settings.AllocationCheckSec.ToString(), "30-600");
        stack.Children.Add(tbStatusPoll.row);
        stack.Children.Add(tbIpPoll.row);
        stack.Children.Add(tbBalPoll.row);
        stack.Children.Add(tbAllocPoll.row);
        stack.Children.Add(SettingsDivider());

        // ─── Discovery ───
        stack.Children.Add(SettingsSection("PLAN DISCOVERY"));
        var tbPlanMax = SettingsInput("Max plan ID to probe", settings.PlanProbeMax.ToString(), "100-1000");
        stack.Children.Add(tbPlanMax.row);
        stack.Children.Add(SettingsDivider());

        // ─── Chain (read-only) ───
        stack.Children.Add(SettingsSection("CHAIN"));
        stack.Children.Add(SettingsRow("Chain ID", "sentinelhub-2", "Sentinel mainnet"));
        stack.Children.Add(SettingsRow("Denom", "udvpn", "1 P2P = 1,000,000 udvpn"));
        stack.Children.Add(SettingsRow("Gas Price", "0.2 udvpn", "Per gas unit"));

        // ─── Save Button ───
        var btnSave = new Button
        {
            Content = "Save Settings",
            Margin = new Thickness(0, 20, 0, 0),
            Padding = new Thickness(20, 11, 20, 11),
            FontSize = 13,
            FontWeight = FontWeights.SemiBold,
            Cursor = Cursors.Hand,
            Background = FindBrush("Acc"),
            Foreground = Brushes.White,
            HorizontalAlignment = HorizontalAlignment.Stretch,
        };
        btnSave.Style = (Style)Application.Current.FindResource("BtnAcc");
        btnSave.Click += (_, _) =>
        {
            // DNS
            if (rbHns.IsChecked == true) settings.DnsPreset = "handshake";
            else if (rbGoogle.IsChecked == true) settings.DnsPreset = "google";
            else if (rbCf.IsChecked == true) settings.DnsPreset = "cloudflare";
            else if (rbCustomDns.IsChecked == true) { settings.DnsPreset = "custom"; settings.CustomDns = tbCustomDns.Text.Trim(); }
            settings.CustomLcd = tbCustomLcd.Text.Trim();
            settings.CustomRpc = tbCustomRpc.Text.Trim();

            // Tunnel
            settings.FullTunnel = cbFullTunnel.IsChecked == true;
            settings.SystemProxy = cbSystemProxy.IsChecked == true;

            // Protocols
            if (int.TryParse(tbMtu.input.Text, out var mtu) && mtu >= 1280 && mtu <= 1500) settings.WgMtu = mtu;
            if (int.TryParse(tbKeepalive.input.Text, out var ka) && ka >= 15 && ka <= 60) settings.WgKeepalive = ka;
            if (int.TryParse(tbSocksPort.input.Text, out var sp) && sp >= 1024 && sp <= 65535) settings.V2RaySocksPort = sp;

            // Session
            if (int.TryParse(tbDefaultGb.input.Text, out var gb) && gb >= 1 && gb <= 100) settings.DefaultGb = gb;
            settings.PreferHourly = cbPreferHourly.IsChecked == true;

            // Polling
            if (int.TryParse(tbStatusPoll.input.Text, out var v1) && v1 >= 1) { settings.StatusPollSec = v1; _statusPoll.Interval = TimeSpan.FromSeconds(v1); }
            if (int.TryParse(tbIpPoll.input.Text, out var v2) && v2 >= 30) { settings.IpCheckSec = v2; _ipPoll.Interval = TimeSpan.FromSeconds(v2); }
            if (int.TryParse(tbBalPoll.input.Text, out var v3) && v3 >= 60) { settings.BalanceCheckSec = v3; _balPoll.Interval = TimeSpan.FromSeconds(v3); }
            if (int.TryParse(tbAllocPoll.input.Text, out var v4) && v4 >= 30) settings.AllocationCheckSec = v4;

            // Discovery
            if (int.TryParse(tbPlanMax.input.Text, out var pm) && pm >= 100) settings.PlanProbeMax = pm;

            settings.Save();
            AddLog($"Settings saved — DNS: {settings.GetDnsDisplay()}");
            SettingsOverlay.Visibility = Visibility.Collapsed;
        };
        stack.Children.Add(btnSave);

        SettingsOverlay.Visibility = Visibility.Visible;
    }

    private static StackPanel SettingsSection(string title)
    {
        var sp = new StackPanel { Margin = new Thickness(0, 0, 0, 10) };
        sp.Children.Add(new TextBlock
        {
            Text = title,
            FontSize = 10,
            FontWeight = FontWeights.Bold,
            Foreground = (Brush)Application.Current.FindResource("T3"),
            FontFamily = (FontFamily)Application.Current.FindResource("Mono"),
            Margin = new Thickness(0, 0, 0, 8),
        });
        return sp;
    }

    private static Grid SettingsRow(string label, string value, string? hint = null)
    {
        var grid = new Grid { Margin = new Thickness(0, 0, 0, 6) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var left = new StackPanel();
        left.Children.Add(new TextBlock
        {
            Text = label,
            FontSize = 12,
            Foreground = (Brush)Application.Current.FindResource("T1"),
        });
        if (hint != null)
            left.Children.Add(new TextBlock
            {
                Text = hint,
                FontSize = 10,
                Foreground = (Brush)Application.Current.FindResource("T3"),
                Margin = new Thickness(0, 1, 0, 0),
            });
        Grid.SetColumn(left, 0);

        var right = new TextBlock
        {
            Text = value,
            FontSize = 11.5,
            FontFamily = (FontFamily)Application.Current.FindResource("Mono"),
            Foreground = (Brush)Application.Current.FindResource("T2"),
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Right,
        };
        Grid.SetColumn(right, 1);

        grid.Children.Add(left);
        grid.Children.Add(right);
        return grid;
    }

    private static Border SettingsEndpoint(string url, bool primary = false)
    {
        var border = new Border
        {
            Background = primary
                ? (Brush)Application.Current.FindResource("GreenDim")
                : (Brush)Application.Current.FindResource("Bg2"),
            CornerRadius = new CornerRadius(5),
            Padding = new Thickness(10, 5, 10, 5),
            Margin = new Thickness(0, 0, 0, 4),
        };
        var grid = new Grid();
        grid.Children.Add(new TextBlock
        {
            Text = url,
            FontSize = 10.5,
            FontFamily = (FontFamily)Application.Current.FindResource("Mono"),
            Foreground = (Brush)Application.Current.FindResource(primary ? "Green" : "T2"),
        });
        if (primary)
        {
            var badge = new TextBlock
            {
                Text = "PRIMARY",
                FontSize = 8,
                FontWeight = FontWeights.Bold,
                FontFamily = (FontFamily)Application.Current.FindResource("Mono"),
                Foreground = (Brush)Application.Current.FindResource("Green"),
                HorizontalAlignment = HorizontalAlignment.Right,
                VerticalAlignment = VerticalAlignment.Center,
            };
            grid.Children.Add(badge);
        }
        border.Child = grid;
        return border;
    }

    private static TextBlock SettingsNote(string text)
    {
        return new TextBlock
        {
            Text = text,
            FontSize = 10,
            Foreground = (Brush)Application.Current.FindResource("T3"),
            TextWrapping = TextWrapping.Wrap,
            FontStyle = FontStyles.Italic,
            Margin = new Thickness(0, 4, 0, 0),
        };
    }

    private Button MakeExportButton(string label, Border resultBorder, Func<string> getValue)
    {
        var btn = new Button
        {
            Content = $"Show {label}", FontSize = 11, Padding = new Thickness(0, 7, 0, 7),
            Cursor = Cursors.Hand, Background = FindBrush("Bg2"), Foreground = FindBrush("T2"),
            HorizontalAlignment = HorizontalAlignment.Stretch, Margin = new Thickness(0, 6, 0, 0),
        };
        btn.Template = CreateBtnTemplate();

        btn.Click += (_, _) =>
        {
            if (resultBorder.Visibility == Visibility.Visible) { resultBorder.Visibility = Visibility.Collapsed; btn.Content = $"Show {label}"; return; }
            var value = getValue();
            var stack = new StackPanel();
            stack.Children.Add(MakeText("KEEP THIS SECRET — DO NOT SHARE", 9, "Red", fontWeight: FontWeights.Bold));
            var valueRow = new Grid { Margin = new Thickness(0, 6, 0, 0) };
            valueRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            valueRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var valueTb = new TextBox
            {
                Text = value, IsReadOnly = true, TextWrapping = TextWrapping.Wrap,
                BorderThickness = new Thickness(0), Background = Brushes.Transparent,
                FontSize = 11, FontFamily = (FontFamily)Application.Current.FindResource("Mono"),
                Foreground = FindBrush("T1"),
            };
            Grid.SetColumn(valueTb, 0);
            var copyBtn = new Button
            {
                Content = "Copy", FontSize = 9, Padding = new Thickness(8, 3, 8, 3),
                Cursor = Cursors.Hand, Background = FindBrush("Acc"), Foreground = Brushes.White,
                VerticalAlignment = VerticalAlignment.Top,
            };
            copyBtn.Template = CreateBtnTemplate();
            copyBtn.Click += (_, _) => { Clipboard.SetText(value); copyBtn.Content = "Copied"; };
            Grid.SetColumn(copyBtn, 1);
            valueRow.Children.Add(valueTb);
            valueRow.Children.Add(copyBtn);
            stack.Children.Add(valueRow);
            resultBorder.Child = stack;
            resultBorder.Visibility = Visibility.Visible;
            btn.Content = $"Hide {label}";
        };
        return btn;
    }

    private static (Grid row, TextBox input) SettingsInput(string label, string value, string hint)
    {
        var grid = new Grid { Margin = new Thickness(0, 4, 0, 4) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(100) });

        var left = new StackPanel();
        left.Children.Add(new TextBlock { Text = label, FontSize = 12, Foreground = (Brush)Application.Current.FindResource("T1") });
        left.Children.Add(new TextBlock { Text = hint, FontSize = 9, Foreground = (Brush)Application.Current.FindResource("T3") });
        Grid.SetColumn(left, 0);

        var input = new TextBox
        {
            Text = value,
            FontSize = 12,
            FontFamily = (FontFamily)Application.Current.FindResource("Mono"),
            Background = (Brush)Application.Current.FindResource("Bg2"),
            Foreground = (Brush)Application.Current.FindResource("T1"),
            BorderBrush = (Brush)Application.Current.FindResource("Bdr"),
            BorderThickness = new Thickness(1),
            Padding = new Thickness(8, 4, 8, 4),
            TextAlignment = TextAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };
        Grid.SetColumn(input, 1);

        grid.Children.Add(left);
        grid.Children.Add(input);
        return (grid, input);
    }

    private static Border SettingsDivider()
    {
        return new Border
        {
            Height = 1,
            Background = (Brush)Application.Current.FindResource("Bdr"),
            Margin = new Thickness(0, 12, 0, 12),
        };
    }


    // ═══ ENTER APP ═══

    private static bool _nodesLoaded;
    // ═══ ENTER APP ═══

    private async Task EnterApp()
    {
        AddLog($"Wallet: {Trunc(_userAddr ?? "", 12, 6)}");

        // Show cached data instantly
        var cachedNodes = DiskCache.Load<List<HnsNodeInfo>>("nodes", TimeSpan.FromMinutes(30));
        var cachedPlans = DiskCache.Load<List<PlanInfo>>("plans", TimeSpan.FromMinutes(30));
        var cachedTests = DiskCache.Load<List<NodeTestResult>>("test-results", TimeSpan.FromDays(7));
        if (cachedTests?.data != null) _testResults = cachedTests.Value.data;
        var cachedSessions = DiskCache.Load<List<ActiveSession>>("sessions", TimeSpan.FromMinutes(10));

        if (cachedNodes?.data != null && cachedNodes.Value.data.Count > 0)
        {
            _allNodes = cachedNodes.Value.data;
            _nodesLoaded = true;
            var online = _allNodes.Count(n => n.Moniker != null);
            TbNodeCount.Text = $"{online} nodes";
            TbOnlineCount.Text = cachedNodes.Value.isStale ? "updating..." : "online";
            RenderNodes();
            AddLog($"Cached: {online} nodes");
        }
        if (cachedPlans?.data != null) { _plans = cachedPlans.Value.data; AddLog($"Cached: {_plans.Count} plans"); }
        if (cachedSessions?.data != null) _activeSessions = cachedSessions.Value.data;

        // Refresh in background (single probe)
        if (!_nodesLoaded)
        {
            // No cache — must load fresh
            await LoadNodes();
            _nodesLoaded = true;
        }
        else
        {
            // Cache shown — refresh silently in background
            _refreshCts = new CancellationTokenSource();
            _ = RefreshAllAsync();
        }
        // Balance loads inside RefreshAllAsync — don't call separately (avoids double LCD init)
        await CheckExisting();

        _statusPoll.Start();
        _ipPoll.Start();
        _balFailCount = 0;
        _balPoll.Interval = TimeSpan.FromMinutes(5);
        _balPoll.Start();
    }

    private async Task LoadBalance()
    {
        try
        {
            var b = await App.Backend.GetBalanceAsync();
            if (b != null)
            {
                TbAddr.Text = Trunc(_userAddr ?? "", 10, 4);
                TbBalVal.Text = b.P2P.ToString("F2");
                _balFailCount = 0;
                _balPoll.Interval = TimeSpan.FromMinutes(5);
            }
            else
            {
                _balFailCount++;
                if (_balFailCount >= 10) _balPoll.Stop();
                else if (_balFailCount >= 3) _balPoll.Interval = TimeSpan.FromMinutes(5);
            }
        }
        catch { /* silent */ }
    }

    // ═══ NODES ═══

    private static bool _refreshing;
    private async Task RefreshAllAsync()
    {
        if (_refreshing) return;
        _refreshing = true;
        try
        {
            // Load balance first (uses same chain client, no extra LCD init)
            await LoadBalance();

            // Refresh nodes
            var data = await App.Backend.GetAllNodesAsync();
            if (data != null && data.Nodes.Count > 0)
            {
                DiskCache.Save("nodes", data.Nodes);
                Dispatcher.Invoke(() =>
                {
                    _allNodes = data.Nodes;
                    _nodesLoaded = true;
                    var online = _allNodes.Count(n => n.Moniker != null);
                    TbNodeCount.Text = $"{online} nodes";
                    AddLog($"Updated: {online} nodes");
                });
            }

            // Refresh sessions (only if wallet loaded)
            if (App.Backend.HasWallet)
            {
                var sessions = await App.Backend.GetActiveSessionsAsync();
                if (sessions != null) { _activeSessions = sessions; DiskCache.Save("sessions", sessions); }
            }

            // Refresh plans (only once — guard against double discovery)
            if (App.Backend.HasWallet)
            {
                var plans = await App.Backend.DiscoverPlansAsync();
                if (plans != null) { _plans = plans; DiskCache.Save("plans", plans); }
            }
        }
        catch { }
        finally { _refreshing = false; }
    }

    private async Task LoadNodes()
    {
        // Show loading state immediately
        NodeListPanel.Children.Clear();
        NodeListPanel.Children.Add(MakeLoadingIndicator());
        TbNodeCount.Text = "loading...";
        TbOnlineCount.Text = "";

        try
        {
            var data = await App.Backend.GetAllNodesAsync();
            if (data == null) { AddLog("Failed to load nodes", true); ShowEmptyState("Failed to load nodes"); return; }
            _allNodes = data.Nodes;
            var online = _allNodes.Count(n => n.Moniker != null);
            TbNodeCount.Text = $"{online} nodes";
            TbOnlineCount.Text = "online";
            RenderNodes();
            DiskCache.Save("nodes", _allNodes);
            if (_unknownCountries.Count > 0)
                AddLog($"Unknown countries (no flag): {string.Join(", ", _unknownCountries)}");
            _unknownCountries.Clear();
        }
        catch (Exception ex) { AddLog($"Node error: {ex.Message}", true); ShowEmptyState(ex.Message); }
    }

    private Border MakeLoadingIndicator()
    {
        var stack = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 40, 0, 0) };

        // Sentinel shield logo
        var sentinelLogo = new Viewbox { Width = 40, Height = 44, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 0, 0, 14) };
        var canvas = new Canvas { Width = 241, Height = 241 };
        var path1 = new System.Windows.Shapes.Path { Fill = new SolidColorBrush(Color.FromRgb(0x01, 0x56, 0xFC)), Data = Geometry.Parse("M178.171 60.8573C179.801 60.8573 181.123 62.1775 181.123 63.8045V110.662V113.572C181.161 119.355 181.207 126.551 180.202 134.203C179.028 143.119 176.7 150.371 173.115 156.367C161.437 175.91 140.465 190.218 120.788 192.134C120.695 192.143 120.593 192.152 120.5 192.152C120.407 192.152 120.304 192.152 120.211 192.134C100.506 190.209 79.5436 175.901 67.8844 156.367C64.2898 150.361 61.9804 143.11 60.7977 134.203C59.7919 126.561 59.8385 119.355 59.8851 113.572V110.662L59.8944 63.8045C59.8944 62.1775 61.2167 60.8573 62.8464 60.8573H178.171ZM178.171 55.0001H62.8464C57.9853 55.0001 54.0276 58.9514 54.0276 63.8045V113.554C53.9717 119.16 53.9065 126.867 54.9774 134.965C56.2625 144.69 58.8328 152.667 62.8371 159.361C68.9833 169.662 77.588 178.755 87.7106 185.672C97.917 192.645 108.934 196.894 119.568 197.944C119.876 197.981 120.183 198 120.49 198C120.798 198 121.105 197.981 121.412 197.944C132.038 196.903 143.045 192.664 153.261 185.681C163.383 178.764 171.988 169.662 178.144 159.361C182.157 152.648 184.727 144.671 186.003 134.965C187.065 126.932 187.018 119.513 186.981 113.563V110.644V63.8045C186.981 58.9514 183.023 55.0001 178.162 55.0001L178.171 55.0001Z") };
        var path2 = new System.Windows.Shapes.Path { Fill = new SolidColorBrush(Color.FromRgb(0x01, 0x56, 0xFC)), Data = Geometry.Parse("M170.145 120.192C171.011 120.843 172.24 120.229 172.24 119.151L172.213 113.228V71.0006C172.222 70.2754 171.635 69.6804 170.909 69.6804H70.0832C69.3569 69.6804 68.7609 70.2661 68.7609 71.0006V96.9769C68.7609 98.0926 69.0216 99.1896 69.5152 100.184C70.018 101.179 70.7444 102.044 71.6291 102.713L147.479 159.333C148.14 159.826 148.186 160.783 147.591 161.341C146.184 162.662 144.713 163.907 143.176 165.079C142.711 165.432 142.059 165.432 141.593 165.088L70.8655 112.168C69.9994 111.518 68.7702 112.131 68.7702 113.219C68.7702 122.293 67.9693 139.223 75.6055 152.193C84.1357 166.687 98.7841 178.123 113.852 181.823C116.031 182.353 118.201 182.734 120.38 182.92C120.463 182.929 120.547 182.929 120.631 182.92C130.493 182.037 140.532 177.537 149.146 170.741C151.306 169.03 153.374 167.189 155.338 165.209C159.222 161.304 162.602 156.934 165.405 152.202C167.938 147.916 169.531 143.202 170.546 138.489C170.769 137.466 170.965 136.434 171.132 135.402C171.579 132.697 170.48 129.973 168.292 128.327L104.083 80.3814C103.077 79.6283 103.599 78.0199 104.874 78.0199H113.367C113.656 78.0199 113.926 78.1129 114.159 78.2803L170.145 120.183V120.192Z") };
        canvas.Children.Add(path1);
        canvas.Children.Add(path2);
        sentinelLogo.Child = canvas;
        stack.Children.Add(sentinelLogo);

        stack.Children.Add(MakeText("Querying Sentinel chain...", 13, "T2", HorizontalAlignment.Center));
        stack.Children.Add(MakeText("Fetching active nodes", 11, "T3", HorizontalAlignment.Center, new Thickness(0, 4, 0, 0)));
        return new Border { Child = stack };
    }

    private void ShowEmptyState(string message)
    {
        NodeListPanel.Children.Clear();
        var stack = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 40, 0, 0) };
        stack.Children.Add(MakeText("No nodes loaded", 13, "T3", HorizontalAlignment.Center));
        stack.Children.Add(MakeText(message, 11, "Red", HorizontalAlignment.Center, new Thickness(0, 6, 0, 0)));
        NodeListPanel.Children.Add(new Border { Child = stack });
    }

    private void RenderNodes()
    {
        NodeListPanel.Children.Clear();
        var filtered = FilterNodes();
        if (filtered.Count == 0)
        {
            NodeListPanel.Children.Add(MakeText("No nodes found", 13, "T3", HorizontalAlignment.Center, new Thickness(0, 30, 0, 0)));
            return;
        }

        // Group by country
        var groups = filtered
            .GroupBy(n => string.IsNullOrWhiteSpace(n.Country) ? "Unknown" : n.Country)
            .OrderBy(g => g.Key);

        foreach (var g in groups)
        {
            var country = g.Key;
            var nodes = g.ToList();
            var code = CountryCode(country);
            var onlineCount = nodes.Count(n => n.Moniker != null);

            // Country header with proper vertical alignment
            var headerGrid = new Grid();
            headerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            headerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var leftPanel = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            var chevron = new TextBlock { Text = "\u25B6", FontSize = 8, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0) };
            try { chevron.Foreground = (Brush)Application.Current.FindResource("T3"); } catch { }
            leftPanel.Children.Add(chevron);
            var flagImg = MakeFlagImage(code);
            if (flagImg is FrameworkElement fe) fe.VerticalAlignment = VerticalAlignment.Center;
            leftPanel.Children.Add(flagImg);
            var countryTb = MakeText(country, 11.5, "T1", fontWeight: FontWeights.Medium);
            countryTb.VerticalAlignment = VerticalAlignment.Center;
            countryTb.Margin = new Thickness(6, 0, 0, 0);
            leftPanel.Children.Add(countryTb);
            Grid.SetColumn(leftPanel, 0);

            var countText = MakeText($"{onlineCount}/{nodes.Count}", 9.5, "T3", fontFamily: "Mono");
            countText.VerticalAlignment = VerticalAlignment.Center;
            Grid.SetColumn(countText, 1);

            headerGrid.Children.Add(leftPanel);
            headerGrid.Children.Add(countText);

            var header = new Border
            {
                Padding = new Thickness(8, 9, 8, 9),
                CornerRadius = new CornerRadius(8),
                Cursor = Cursors.Hand,
                Child = headerGrid,
            };
            header.MouseEnter += (s, _) => ((Border)s).Background = FindBrush("Bg2");
            header.MouseLeave += (s, _) => ((Border)s).Background = Brushes.Transparent;

            var nodesPanel = new StackPanel { Visibility = Visibility.Collapsed }; // start collapsed
            var isExpanded = false;
            header.MouseLeftButtonUp += (_, _) =>
            {
                isExpanded = !isExpanded;
                nodesPanel.Visibility = isExpanded ? Visibility.Visible : Visibility.Collapsed;
                chevron.Text = isExpanded ? "\u25BC" : "\u25B6";
            };

            // Group nodes by city within the country — collapsed with counts
            var cities = nodes.GroupBy(n => string.IsNullOrWhiteSpace(n.City) ? "Other" : n.City).OrderBy(c => c.Key);
            if (cities.Count() > 1)
            {
                foreach (var city in cities)
                {
                    var cityHeader = new Border { Padding = new Thickness(16, 5, 8, 5), Cursor = Cursors.Hand };
                    var cityGrid = new Grid();
                    cityGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                    cityGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                    var cityChevron = MakeText("\u25B6", 7, "T3");
                    cityChevron.VerticalAlignment = VerticalAlignment.Center;
                    cityChevron.Margin = new Thickness(0, 0, 6, 0);
                    var cityName = MakeText(city.Key, 10.5, "T2", fontWeight: FontWeights.Medium);
                    cityName.VerticalAlignment = VerticalAlignment.Center;
                    var cityLeft = new StackPanel { Orientation = Orientation.Horizontal };
                    cityLeft.Children.Add(cityChevron);
                    cityLeft.Children.Add(cityName);
                    Grid.SetColumn(cityLeft, 0);
                    var cityCount = MakeText($"{city.Count()}", 9, "T3", fontFamily: "Mono");
                    cityCount.VerticalAlignment = VerticalAlignment.Center;
                    Grid.SetColumn(cityCount, 1);
                    cityGrid.Children.Add(cityLeft);
                    cityGrid.Children.Add(cityCount);
                    cityHeader.Child = cityGrid;
                    cityHeader.MouseEnter += (s, _) => ((Border)s).Background = FindBrush("Bg2");
                    cityHeader.MouseLeave += (s, _) => ((Border)s).Background = Brushes.Transparent;

                    var cityNodes = new StackPanel { Visibility = Visibility.Collapsed };
                    var cityExpanded = false;
                    cityHeader.MouseLeftButtonUp += (_, _) =>
                    {
                        cityExpanded = !cityExpanded;
                        cityNodes.Visibility = cityExpanded ? Visibility.Visible : Visibility.Collapsed;
                        cityChevron.Text = cityExpanded ? "\u25BC" : "\u25B6";
                    };
                    foreach (var node in city)
                        cityNodes.Children.Add(MakeNodeRow(node));

                    nodesPanel.Children.Add(cityHeader);
                    nodesPanel.Children.Add(cityNodes);
                }
            }
            else
            {
                foreach (var node in nodes)
                    nodesPanel.Children.Add(MakeNodeRow(node));
            }

            NodeListPanel.Children.Add(header);
            NodeListPanel.Children.Add(nodesPanel);
        }
    }

    private Border MakeNodeRow(HnsNodeInfo node)
    {
        var isSelected = node.Address == _selectedNode;
        var isOnline = node.Moniker != null;
        var typeStr = (node.ServiceType ?? "").ToUpperInvariant();
        var isWg = typeStr.Contains("WG") || typeStr.Contains("WIRE");
        var gbStr = node.HasGbPrice ? $"{node.GbPriceDisplay}/GB" : "";
        var hrStr = node.HasHourlyPrice ? $"{node.HourlyPriceDisplay}/hr" : "";

        var label = !string.IsNullOrEmpty(node.Moniker) ? node.Moniker : Trunc(node.Address, 14, 0);

        var row = new Border
        {
            Padding = new Thickness(12, 9, 12, 9),
            CornerRadius = new CornerRadius(8),
            Margin = new Thickness(0, 1, 0, 1),
            BorderThickness = new Thickness(1),
            BorderBrush = isSelected ? FindBrush("Acc") : Brushes.Transparent,
            Background = isSelected ? FindBrush("AccLight") : Brushes.Transparent,
            Cursor = Cursors.Hand,
            Tag = node.Address,
            Opacity = isOnline ? 1.0 : 0.5,
        };

        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(8) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        // Online dot
        var dot = new WpfEllipse
        {
            Width = 6, Height = 6,
            Fill = FindBrush(isOnline ? "Green" : "Bg4"),
            VerticalAlignment = VerticalAlignment.Center
        };
        Grid.SetColumn(dot, 0);

        // Name + location
        var nameStack = new StackPanel { Margin = new Thickness(10, 0, 0, 0) };
        nameStack.Children.Add(MakeText(label, 12, isSelected ? "Acc" : "T1", fontWeight: FontWeights.Medium));
        if (!string.IsNullOrEmpty(node.City))
            nameStack.Children.Add(MakeText(node.City, 10, "T3"));
        nameStack.VerticalAlignment = VerticalAlignment.Center;
        Grid.SetColumn(nameStack, 1);

        // Meta: type badge + price
        var meta = new StackPanel { Orientation = Orientation.Vertical, VerticalAlignment = VerticalAlignment.Center };
        if (isOnline)
        {
            var badge = new Border
            {
                Background = FindBrush(isWg ? "GreenDim" : "BlueDim"),
                CornerRadius = new CornerRadius(4),
                Padding = new Thickness(5, 2, 5, 2),
                HorizontalAlignment = HorizontalAlignment.Right,
                Child = MakeText(isWg ? "WG" : "V2", 8.5, isWg ? "Green" : "Blue", fontWeight: FontWeights.SemiBold, fontFamily: "Mono")
            };
            meta.Children.Add(badge);
        }
        if (!string.IsNullOrEmpty(gbStr))
            meta.Children.Add(MakeText(gbStr, 9, "T2", HorizontalAlignment.Right, new Thickness(0, 1, 0, 0), fontFamily: "Mono"));
        if (!string.IsNullOrEmpty(hrStr))
            meta.Children.Add(MakeText(hrStr, 9, "T3", HorizontalAlignment.Right, new Thickness(0, 1, 0, 0), fontFamily: "Mono"));
        Grid.SetColumn(meta, 2);

        grid.Children.Add(dot);
        grid.Children.Add(nameStack);
        grid.Children.Add(meta);
        row.Child = grid;

        row.MouseEnter += (s, _) => { if (row.Tag?.ToString() != _selectedNode) ((Border)s).Background = FindBrush("Bg2"); };
        row.MouseLeave += (s, _) => { if (row.Tag?.ToString() != _selectedNode) ((Border)s).Background = Brushes.Transparent; };
        row.MouseLeftButtonUp += (_, _) => SelectNode(node);

        return row;
    }

    private void SelectNode(HnsNodeInfo node)
    {
        _selectedNode = node.Address;
        _selectedNodeInfo = node;
        StatusSub.Text = node.Moniker ?? Trunc(node.Address, 16, 6);

        // Update info card
        NodeInfoCard.Visibility = Visibility.Visible;
        NodeCardName.Text = node.Moniker ?? Trunc(node.Address, 20, 6);
        NodeCardLocation.Text = $"{node.Country ?? "Unknown"}{(node.City != null ? $", {node.City}" : "")}";
        NodeCardType.Text = (node.ServiceType ?? "unknown").ToUpperInvariant();
        NodeCardPeers.Text = node.Peers.HasValue ? $"{node.Peers} peers" : "\u2014";

        // Check if we have an existing session with this node
        CheckExistingSessionForNode(node.Address);

        // Flag in info card
        var code = CountryCode(node.Country ?? "");
        if (code != "??" && _flagCache.TryGetValue(code, out var bmp) && bmp != null)
        {
            NodeCardFlagImg.Source = bmp;
            NodeCardFlag.Background = Brushes.Transparent;
        }
        else
        {
            NodeCardFlagImg.Source = null;
            NodeCardFlag.Background = FindBrush("Bg3");
        }

        // Payment options (compact) — PayGbPrice/PayHrPrice are Run elements
        PayGbPrice.Text = node.HasGbPrice ? $"{node.GbPriceDisplay} P2P" : "N/A";
        PayHrPrice.Text = node.HasHourlyPrice ? $"{node.HourlyPriceDisplay} P2P" : "N/A";

        // Default to GB if available, else hourly
        if (node.HasGbPrice)
            SetPayMode("gb");
        else if (node.HasHourlyPrice)
            SetPayMode("hr");

        UpdateTotalCost();
        // Don't re-render entire list — selection is visual only in info card
    }

    private void CheckExistingSessionForNode(string nodeAddress)
    {
        ExistingSessionBar.Visibility = Visibility.Collapsed;

        // Check cached sessions first — no chain query, no flicker
        var match = _activeSessions.FirstOrDefault(s => s.NodeAddress == nodeAddress);
        if (match == null) return;

        ExistingSessionBar.Visibility = Visibility.Visible;
        ExistingSessionText.Text = $"Active session #{match.SessionId}";
        if (match.PayMode == "hr" && match.InactiveAt != null && DateTime.TryParse(match.InactiveAt, out var exp))
        {
            var left = exp - DateTime.UtcNow;
            ExistingSessionDetail.Text = left.TotalDays > 1 ? $"{(int)left.TotalDays}d {left.Hours}h left" : left.TotalHours > 1 ? $"{(int)left.TotalHours}h left" : "expiring";
        }
        else
            ExistingSessionDetail.Text = $"{match.RemainingDisplay} remaining";
    }

    private void PayGb_Click(object sender, MouseButtonEventArgs e) => SetPayMode("gb");
    private void PayHr_Click(object sender, MouseButtonEventArgs e) => SetPayMode("hr");

    private void SetPayMode(string mode)
    {
        _payMode = mode;
        PayGbBtn.Background = FindBrush(mode == "gb" ? "AccLight" : "Bg2");
        PayGbBtn.BorderBrush = FindBrush(mode == "gb" ? "Acc" : "Bdr");
        PayHrBtn.Background = FindBrush(mode == "hr" ? "AccLight" : "Bg2");
        PayHrBtn.BorderBrush = FindBrush(mode == "hr" ? "Acc" : "Bdr");
        AmountLabel.Text = mode == "gb" ? "Amount (GB):" : "Amount (hours):";
        TbAmount.Text = mode == "gb" ? "1" : "1";
        UpdateTotalCost();
    }

    private void TbAmount_Changed(object sender, TextChangedEventArgs e) => UpdateTotalCost();

    private void UpdateTotalCost()
    {
        if (TotalCost == null || _selectedNodeInfo == null) { if (TotalCost != null) TotalCost.Text = ""; return; }
        if (!int.TryParse(TbAmount.Text.Trim(), out var amount) || amount <= 0) { TotalCost.Text = "= ? P2P"; return; }

        var priceStr = _payMode == "gb" ? _selectedNodeInfo.GbPriceUdvpn : _selectedNodeInfo.HourlyPriceUdvpn;
        if (string.IsNullOrEmpty(priceStr)) { TotalCost.Text = "= N/A"; return; }

        double raw;
        if (long.TryParse(priceStr, out var lv)) raw = lv;
        else if (double.TryParse(priceStr, System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out var dv)) raw = Math.Floor(dv);
        else { TotalCost.Text = "= ? P2P"; return; }

        var totalUdvpn = raw * amount;
        var totalP2p = totalUdvpn / 1_000_000.0;
        if (totalP2p >= 1) TotalCost.Text = $"= {totalP2p:F2} P2P";
        else if (totalP2p >= 0.01) TotalCost.Text = $"= {totalP2p:F2} P2P";
        else TotalCost.Text = $"= {totalP2p:F4} P2P";
    }

    private List<HnsNodeInfo> FilterNodes()
    {
        // Only show online nodes (responded during probe)
        var list = _allNodes.Where(n => n.Moniker != null).AsEnumerable();
        if (_activeFilter == "wireguard") list = list.Where(n => (n.ServiceType ?? "").Contains("wireguard", StringComparison.OrdinalIgnoreCase));
        else if (_activeFilter == "v2ray") list = list.Where(n => (n.ServiceType ?? "").Contains("v2ray", StringComparison.OrdinalIgnoreCase));

        var q = TbSearch.Text.Trim();
        if (!string.IsNullOrEmpty(q))
            list = list.Where(n =>
                (n.Country ?? "").Contains(q, StringComparison.OrdinalIgnoreCase) ||
                (n.City ?? "").Contains(q, StringComparison.OrdinalIgnoreCase) ||
                (n.Moniker ?? "").Contains(q, StringComparison.OrdinalIgnoreCase) ||
                n.Address.Contains(q, StringComparison.OrdinalIgnoreCase));

        return list.ToList();
    }

    private void Filter_Click(object sender, RoutedEventArgs e)
    {
        _activeFilter = (string)((Button)sender).Tag;
        FAll.Foreground = FindBrush(_activeFilter == "" ? "Acc" : "T3");
        FWg.Foreground = FindBrush(_activeFilter == "wireguard" ? "Acc" : "T3");
        FV2.Foreground = FindBrush(_activeFilter == "v2ray" ? "Acc" : "T3");
        FGb.Foreground = FindBrush(_activeFilter == "gb" ? "Acc" : "T3");
        FHr.Foreground = FindBrush(_activeFilter == "hr" ? "Acc" : "T3");

        if (_sidebarTab == "sessions")
            _ = RenderSubscribedAsync();
        else if (_sidebarTab == "plans")
            _ = RenderPlansAsync();
        else if (_sidebarTab == "test")
            RenderTestTab();
        else
            RenderNodes();
    }

    private string _sidebarTab = "nodes"; // "nodes" or "sessions"

    private void SidebarTab_Click(object sender, RoutedEventArgs e)
    {
        _sidebarTab = (string)((Button)sender).Tag;
        TabNodes.Foreground = FindBrush(_sidebarTab == "nodes" ? "T1" : "T2");
        TabNodes.FontWeight = _sidebarTab == "nodes" ? FontWeights.SemiBold : FontWeights.Medium;
        TabNodes.BorderBrush = FindBrush(_sidebarTab == "nodes" ? "Acc" : "Bg0");
        TabSessions.Foreground = FindBrush(_sidebarTab == "sessions" ? "T1" : "T2");
        TabSessions.FontWeight = _sidebarTab == "sessions" ? FontWeights.SemiBold : FontWeights.Medium;
        TabSessions.BorderBrush = FindBrush(_sidebarTab == "sessions" ? "Acc" : "Bg0");
        TabTest.Foreground = FindBrush(_sidebarTab == "test" ? "T1" : "T2");
        TabTest.FontWeight = _sidebarTab == "test" ? FontWeights.SemiBold : FontWeights.Medium;
        TabTest.BorderBrush = FindBrush(_sidebarTab == "test" ? "Acc" : "Bg0");
        TabPlans.Foreground = FindBrush(_sidebarTab == "plans" ? "T1" : "T2");
        TabPlans.FontWeight = _sidebarTab == "plans" ? FontWeights.SemiBold : FontWeights.Medium;
        TabPlans.BorderBrush = FindBrush(_sidebarTab == "plans" ? "Acc" : "Bg0");

        if (_sidebarTab == "sessions")
        {
            _activeSessions = new();
            _ = RenderSubscribedAsync();
        }
        else if (_sidebarTab == "plans")
            _ = RenderPlansAsync();
        else if (_sidebarTab == "test")
            RenderTestTab();
        else
            RenderNodes();

        // Show filters per tab: Nodes=All/WG/V2, Sessions=All/WG/V2/GB/Hr, Plans=none
        var isNodes = _sidebarTab == "nodes";
        var isSessions = _sidebarTab == "sessions";
        var isTest = _sidebarTab == "test";

        // Test dashboard takes over main area
        TestDashboard.Visibility = isTest ? Visibility.Visible : Visibility.Collapsed;

        // Hide search on plans/test tabs
        SearchPlaceholder.Visibility = (isTest || _sidebarTab == "plans") ? Visibility.Collapsed : Visibility.Visible;
        TbSearch.Visibility = (isTest || _sidebarTab == "plans") ? Visibility.Collapsed : Visibility.Visible;
        FAll.Visibility = (isNodes || isSessions) ? Visibility.Visible : Visibility.Collapsed;
        FWg.Visibility = (isNodes || isSessions) ? Visibility.Visible : Visibility.Collapsed;
        FV2.Visibility = (isNodes || isSessions) ? Visibility.Visible : Visibility.Collapsed;
        FGb.Visibility = isSessions ? Visibility.Visible : Visibility.Collapsed;
        FHr.Visibility = isSessions ? Visibility.Visible : Visibility.Collapsed;
    }

    private void TbSearch_Changed(object sender, TextChangedEventArgs e) { if (_sidebarTab == "nodes") RenderNodes(); }

    private void TbSearch_Focus(object sender, RoutedEventArgs e)
    {
        SearchPlaceholder.Visibility = Visibility.Collapsed;
    }

    private void TbSearch_LostFocus(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrEmpty(TbSearch.Text))
            SearchPlaceholder.Visibility = Visibility.Visible;
    }

    private async Task RenderSubscribedAsync()
    {
        NodeListPanel.Children.Clear();
        NodeListPanel.Children.Add(MakeText("Loading sessions...", 12, "T3", HorizontalAlignment.Center, new Thickness(0, 20, 0, 0)));

        if (!App.Backend.HasWallet)
        {
            NodeListPanel.Children.Clear();
            NodeListPanel.Children.Add(MakeText("Import a wallet first", 12, "T3", HorizontalAlignment.Center, new Thickness(0, 20, 0, 0)));
            return;
        }

        // Show cached sessions instantly, then refresh
        var cachedSessions = DiskCache.Load<List<ActiveSession>>("sessions", TimeSpan.FromMinutes(2));
        if (cachedSessions?.data != null && cachedSessions.Value.data.Count > 0 && !cachedSessions.Value.isStale)
        {
            _activeSessions = cachedSessions.Value.data;
        }
        else
        {
            AddLog("Querying active sessions...");
            var sessions = await App.Backend.GetActiveSessionsAsync();
            _activeSessions = sessions ?? new();
            AddLog($"Found {_activeSessions.Count} active sessions");
            if (_activeSessions.Count > 0) DiskCache.Save("sessions", _activeSessions);
        }
        NodeListPanel.Children.Clear();

        if (_activeSessions.Count == 0)
        {
            NodeListPanel.Children.Add(MakeText("No active sessions", 13, "T3", HorizontalAlignment.Center, new Thickness(0, 30, 0, 0)));
            NodeListPanel.Children.Add(MakeText("Connect to a node to create a session", 11, "T3", HorizontalAlignment.Center, new Thickness(0, 6, 0, 0)));
            return;
        }

        // Only show direct P2P sessions — exclude plan sessions
        var filtered = _activeSessions
            .Where(s => SessionTracker.GetMode(s.SessionId) != "plan")
            .AsEnumerable();
        if (_activeFilter == "gb") filtered = filtered.Where(s => s.PayMode == "gb");
        else if (_activeFilter == "hr") filtered = filtered.Where(s => s.PayMode == "hr");
        else if (_activeFilter == "wireguard" || _activeFilter == "v2ray")
        {
            // Match session's node type
            filtered = filtered.Where(s =>
            {
                var node = _allNodes.FirstOrDefault(n => n.Address == s.NodeAddress);
                var type = (node?.ServiceType ?? "").ToLowerInvariant();
                return _activeFilter == "wireguard" ? type.Contains("wireguard") : type.Contains("v2ray");
            });
        }
        var list = filtered.ToList();

        if (list.Count == 0 && _activeFilter != "")
        {
            var filterName = _activeFilter == "gb" ? "Per GB" : _activeFilter == "hr" ? "Per Hour" : _activeFilter == "wireguard" ? "WireGuard" : "V2Ray";
            NodeListPanel.Children.Add(MakeText($"No {filterName} sessions", 13, "T3", HorizontalAlignment.Center, new Thickness(0, 30, 0, 0)));
            NodeListPanel.Children.Add(MakeText($"Connect to a node using {filterName} to see it here", 11, "T3", HorizontalAlignment.Center, new Thickness(0, 6, 0, 0)));
        }
        else
        {
            foreach (var s in list)
                NodeListPanel.Children.Add(MakeSessionRow(s));
        }

        TbNodeCount.Text = $"{list.Count} sessions";
        TbOnlineCount.Text = "";
    }

    private Border MakeSessionRow(ActiveSession s)
    {
        var node = _allNodes.FirstOrDefault(n => n.Address == s.NodeAddress);
        var label = node?.Moniker ?? Trunc(s.NodeAddress, 16, 6);
        var country = node?.Country ?? "";
        var code = CountryCode(country);

        var row = new Border
        {
            Padding = new Thickness(12, 10, 12, 10),
            CornerRadius = new CornerRadius(8),
            Margin = new Thickness(4, 3, 4, 3),
            Background = FindBrush("Bg0"),
            BorderBrush = FindBrush("Bdr"),
            BorderThickness = new Thickness(1),
        };

        var outer = new StackPanel();

        // Row 1: flag + name | session ID
        var r1 = new Grid();
        r1.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        r1.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var namePanel = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        if (code != "??" && _flagCache.TryGetValue(code, out var bmp) && bmp != null)
            namePanel.Children.Add(new Image { Width = 18, Height = 12, Stretch = System.Windows.Media.Stretch.Uniform, Source = bmp, Margin = new Thickness(0, 0, 6, 0), VerticalAlignment = VerticalAlignment.Center });
        namePanel.Children.Add(MakeText(label, 11.5, "T1", fontWeight: FontWeights.SemiBold));
        Grid.SetColumn(namePanel, 0);
        var sidTb = MakeText($"#{s.SessionId}", 9, "T3", fontFamily: "Mono");
        sidTb.VerticalAlignment = VerticalAlignment.Center;
        Grid.SetColumn(sidTb, 1);
        r1.Children.Add(namePanel);
        r1.Children.Add(sidTb);
        outer.Children.Add(r1);

        // Row 2: usage info text
        string usageText;
        if (s.PayMode == "hr")
        {
            // Show time remaining for hourly sessions
            if (s.InactiveAt != null && DateTime.TryParse(s.InactiveAt, out var exp))
            {
                var left = exp - DateTime.UtcNow;
                if (left.TotalDays > 1) usageText = $"{(int)left.TotalDays}d {left.Hours}h remaining";
                else if (left.TotalHours > 1) usageText = $"{(int)left.TotalHours}h {left.Minutes}m remaining";
                else if (left.TotalMinutes > 0) usageText = $"{(int)left.TotalMinutes}m remaining";
                else usageText = "Expired";
            }
            else usageText = $"Time-based  |  {s.UsedDisplay} used";
        }
        else
        {
            usageText = $"{s.UsedDisplay} used  /  {s.RemainingDisplay}  ({(int)s.UsedPercent}%)";
        }
        outer.Children.Add(MakeText(usageText, 10, "T2", margin: new Thickness(0, 4, 0, 0)));

        // Row 3: usage bar
        double pct;
        if (s.PayMode == "hr" && s.InactiveAt != null && DateTime.TryParse(s.InactiveAt, out var expiry))
        {
            // Time-based: show how much time has elapsed
            var totalDuration = TimeSpan.FromHours(1); // default 1hr if we don't know total
            var remaining = expiry - DateTime.UtcNow;
            pct = remaining.TotalSeconds > 0 ? Math.Max(0.01, Math.Min(0.99, 1 - (remaining.TotalSeconds / totalDuration.TotalSeconds))) : 0.99;
        }
        else
        {
            pct = Math.Max(0.01, Math.Min(0.99, s.UsedPercent / 100.0));
        }
        var barGrid = new Grid { Margin = new Thickness(0, 6, 0, 0) };
        barGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(pct, GridUnitType.Star) });
        barGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1 - pct, GridUnitType.Star) });
        var barBg = new Border { Height = 5, Background = FindBrush("Bg3"), CornerRadius = new CornerRadius(2.5) };
        Grid.SetColumnSpan(barBg, 2);
        var barFill = new Border { Height = 5, Background = FindBrush("Acc"), CornerRadius = new CornerRadius(2.5) };
        Grid.SetColumn(barFill, 0);
        barGrid.Children.Add(barBg);
        barGrid.Children.Add(barFill);
        outer.Children.Add(barGrid);

        // Row 4: connect button
        var nodeAddr = s.NodeAddress;
        var connectBtn = new Button
        {
            Content = "Connect",
            FontSize = 10,
            FontWeight = FontWeights.SemiBold,
            Padding = new Thickness(0, 6, 0, 6),
            Cursor = Cursors.Hand,
            Background = FindBrush("Acc"),
            Foreground = Brushes.White,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            Margin = new Thickness(0, 8, 0, 0),
        };
        connectBtn.Template = CreateBtnTemplate();
        connectBtn.Click += async (_, _) =>
        {
            _selectedNode = nodeAddr;
            if (node != null) SelectNode(node);
            SidebarTab_Click(TabNodes, new RoutedEventArgs());
            await ToggleConnect();
        };
        outer.Children.Add(connectBtn);

        row.Child = outer;
        return row;
    }

    private static ControlTemplate CreateBtnTemplate()
    {
        var template = new ControlTemplate(typeof(Button));
        var border = new FrameworkElementFactory(typeof(Border));
        border.SetValue(Border.BackgroundProperty, new TemplateBindingExtension(Button.BackgroundProperty));
        border.SetValue(Border.CornerRadiusProperty, new CornerRadius(5));
        border.SetValue(Border.PaddingProperty, new TemplateBindingExtension(Button.PaddingProperty));
        var cp = new FrameworkElementFactory(typeof(ContentPresenter));
        cp.SetValue(ContentPresenter.HorizontalAlignmentProperty, HorizontalAlignment.Center);
        border.AppendChild(cp);
        template.VisualTree = border;
        return template;
    }

    // ═══ PLANS ═══

    private List<PlanInfo> _plans = new();

    private async Task RenderPlansAsync()
    {
        NodeListPanel.Children.Clear();
        NodeListPanel.Children.Add(MakeText("Discovering plans...", 12, "T3", HorizontalAlignment.Center, new Thickness(0, 20, 0, 0)));

        if (!App.Backend.HasWallet)
        {
            NodeListPanel.Children.Clear();
            NodeListPanel.Children.Add(MakeText("Import a wallet first", 12, "T3", HorizontalAlignment.Center, new Thickness(0, 20, 0, 0)));
            return;
        }

        // Show cached plans instantly, then refresh
        var cachedPlans = DiskCache.Load<List<PlanInfo>>("plans", TimeSpan.FromMinutes(5));
        if (cachedPlans?.data != null && cachedPlans.Value.data.Count > 0 && !cachedPlans.Value.isStale)
        {
            _plans = cachedPlans.Value.data;
        }
        else
        {
            var plans = await App.Backend.DiscoverPlansAsync();
            _plans = plans ?? new();
            if (_plans.Count > 0) DiskCache.Save("plans", _plans);
        }
        NodeListPanel.Children.Clear();

        if (_plans.Count == 0)
        {
            NodeListPanel.Children.Add(MakeText("No plans found", 13, "T3", HorizontalAlignment.Center, new Thickness(0, 30, 0, 0)));
            return;
        }

        // Sort: subscribed first, then by subscriber count desc
        var active = _plans
            .Where(p => p.IsSubscribed || p.Subscribers > 0)
            .OrderByDescending(p => p.IsSubscribed)
            .ThenByDescending(p => p.Subscribers)
            .ToList();
        var empty = _plans.Where(p => !p.IsSubscribed && p.Subscribers == 0).OrderBy(p => p.Id).ToList();

        foreach (var p in active)
            NodeListPanel.Children.Add(MakePlanRow(p));

        // "Show All" button for empty plans
        if (empty.Count > 0)
        {
            var showAllBtn = new Button
            {
                Content = $"Show All ({empty.Count} more with 0 subscribers)",
                FontSize = 11, Padding = new Thickness(0, 8, 0, 8),
                Cursor = Cursors.Hand, Margin = new Thickness(4, 12, 4, 4),
                Background = FindBrush("Bg2"), Foreground = FindBrush("T2"),
                HorizontalAlignment = HorizontalAlignment.Stretch,
            };
            showAllBtn.Template = CreateBtnTemplate();
            var emptyPanel = new StackPanel { Visibility = Visibility.Collapsed };
            foreach (var p in empty)
                emptyPanel.Children.Add(MakePlanRow(p));
            showAllBtn.Click += (_, _) =>
            {
                emptyPanel.Visibility = emptyPanel.Visibility == Visibility.Visible ? Visibility.Collapsed : Visibility.Visible;
                showAllBtn.Content = emptyPanel.Visibility == Visibility.Visible
                    ? $"Hide empty plans ({empty.Count})"
                    : $"Show All ({empty.Count} more with 0 subscribers)";
            };
            NodeListPanel.Children.Add(showAllBtn);
            NodeListPanel.Children.Add(emptyPanel);
        }

        TbNodeCount.Text = $"{_plans.Count} plans";
        TbOnlineCount.Text = $"{active.Count} active";
    }

    private Border MakePlanRow(PlanInfo p)
    {
        var row = new Border
        {
            Padding = new Thickness(12, 10, 12, 10),
            CornerRadius = new CornerRadius(8),
            Margin = new Thickness(4, 3, 4, 3),
            Background = p.IsSubscribed ? FindBrush("GreenDim") : FindBrush("Bg0"),
            BorderBrush = p.IsSubscribed ? FindBrush("Green") : FindBrush("Bdr"),
            BorderThickness = new Thickness(1),
        };

        var outer = new StackPanel();

        // Row 1: Plan #ID | price
        var r1 = new Grid();
        r1.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        r1.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var titleTb = MakeText($"Plan #{p.Id}", 12, "T1", fontWeight: FontWeights.SemiBold);
        Grid.SetColumn(titleTb, 0);
        var priceTb = MakeText(p.PriceDisplay, 11, "T1", fontWeight: FontWeights.SemiBold, fontFamily: "Mono");
        priceTb.HorizontalAlignment = HorizontalAlignment.Right;
        Grid.SetColumn(priceTb, 1);
        r1.Children.Add(titleTb);
        r1.Children.Add(priceTb);
        outer.Children.Add(r1);

        // Row 2: nodes + subscribers
        var r2 = MakeText($"{p.NodeCount} nodes  |  {p.Subscribers} subscribers", 10, "T3", margin: new Thickness(0, 4, 0, 0));
        outer.Children.Add(r2);

        // Row 3: status + action button
        if (p.IsSubscribed)
        {
            var statusRow = new Grid { Margin = new Thickness(0, 8, 0, 0) };
            statusRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            statusRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var statusPanel = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            statusPanel.Children.Add(new System.Windows.Shapes.Ellipse { Width = 6, Height = 6, Fill = FindBrush("Green"), Margin = new Thickness(0, 0, 5, 0), VerticalAlignment = VerticalAlignment.Center });
            statusPanel.Children.Add(MakeText("Subscribed", 10, "Green", fontWeight: FontWeights.SemiBold));
            if (p.ExpiresDisplay != null)
                statusPanel.Children.Add(MakeText($"  ({p.ExpiresDisplay})", 9, "T3"));
            if (p.HasFeeGrant)
                statusPanel.Children.Add(MakeText("  gas-free", 9, "T3"));
            Grid.SetColumn(statusPanel, 0);

            var viewBtn = new Button
            {
                Content = "View Nodes",
                FontSize = 10, FontWeight = FontWeights.SemiBold,
                Padding = new Thickness(12, 4, 12, 4),
                Cursor = Cursors.Hand,
                Background = FindBrush("Acc"), Foreground = Brushes.White,
            };
            viewBtn.Template = CreateBtnTemplate();
            var planId = p.Id;
            var subId = p.SubscriptionId;
            viewBtn.Click += async (_, _) =>
            {
                AddLog($"Loading Plan #{planId} nodes...");
                await ShowPlanNodesAsync(planId, subId);
            };
            Grid.SetColumn(viewBtn, 1);

            statusRow.Children.Add(statusPanel);
            statusRow.Children.Add(viewBtn);
            outer.Children.Add(statusRow);
        }
        else
        {
            var subBtn = new Button
            {
                Content = "Subscribe",
                FontSize = 10, FontWeight = FontWeights.SemiBold,
                Padding = new Thickness(0, 6, 0, 6),
                Cursor = Cursors.Hand,
                Background = FindBrush("Acc"), Foreground = Brushes.White,
                HorizontalAlignment = HorizontalAlignment.Stretch,
                Margin = new Thickness(0, 8, 0, 0),
            };
            subBtn.Template = CreateBtnTemplate();
            var planId = p.Id;
            subBtn.Click += async (_, _) =>
            {
                subBtn.IsEnabled = false;
                subBtn.Content = "Subscribing...";
                var subId = await App.Backend.SubscribeToPlanAsync(planId);
                if (subId != null)
                {
                    AddLog($"Subscribed to Plan #{planId}, subscription #{subId}");
                    _ = RenderPlansAsync(); // refresh
                }
                else
                {
                    subBtn.Content = "Failed — try again";
                    subBtn.IsEnabled = true;
                }
            };
            outer.Children.Add(subBtn);
        }

        row.Child = outer;
        return row;
    }

    private async Task ShowPlanNodesAsync(int planId, string? subscriptionId)
    {
        NodeListPanel.Children.Clear();
        NodeListPanel.Children.Add(MakeText($"Loading Plan #{planId} nodes...", 12, "T3", HorizontalAlignment.Center, new Thickness(0, 20, 0, 0)));

        try
        {
            var backend = (NativeVpnClient)App.Backend;
            await backend.EnsureChainPublicAsync();
            var chain = backend.GetChain();
            var planNodes = await chain!.GetPlanNodesAsync(planId);

            NodeListPanel.Children.Clear();
            NodeListPanel.Children.Add(MakeText($"PLAN #{planId}  —  {planNodes.Count} nodes", 10, "T3", margin: new Thickness(8, 8, 0, 8), fontWeight: FontWeights.Bold));

            // Back button
            var backBtn = new Button
            {
                Content = "Back to Plans",
                FontSize = 10, Padding = new Thickness(10, 4, 10, 4),
                Cursor = Cursors.Hand, Margin = new Thickness(4, 0, 4, 8),
                Background = FindBrush("Bg2"), Foreground = FindBrush("T2"),
                HorizontalAlignment = HorizontalAlignment.Left,
            };
            backBtn.Template = CreateBtnTemplate();
            backBtn.Click += (_, _) => _ = RenderPlansAsync();
            NodeListPanel.Children.Add(backBtn);

            // Match enriched nodes with plan nodes for country/flag data
            var enrichedPlanNodes = planNodes.Select(n =>
                _allNodes.FirstOrDefault(a => a.Address == n.Address) ?? new HnsNodeInfo { Address = n.Address }
            ).Where(n => n.Moniker != null).ToList(); // online only

            // Group by country — same format as Nodes tab
            var groups = enrichedPlanNodes
                .GroupBy(n => string.IsNullOrWhiteSpace(n.Country) ? "Unknown" : n.Country)
                .OrderBy(g => g.Key);

            foreach (var g in groups)
            {
                var country = g.Key;
                var nodes = g.ToList();
                var code = CountryCode(country);
                var headerGrid = new Grid();
                headerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                headerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                var leftPanel = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
                var chevron = new TextBlock { Text = "\u25B6", FontSize = 8, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0) };
                try { chevron.Foreground = (Brush)Application.Current.FindResource("T3"); } catch { }
                leftPanel.Children.Add(chevron);
                var fImg = MakeFlagImage(code);
                if (fImg is FrameworkElement fe2) fe2.VerticalAlignment = VerticalAlignment.Center;
                leftPanel.Children.Add(fImg);
                var cTb = MakeText(country, 11.5, "T1", fontWeight: FontWeights.Medium);
                cTb.VerticalAlignment = VerticalAlignment.Center;
                cTb.Margin = new Thickness(6, 0, 0, 0);
                leftPanel.Children.Add(cTb);
                Grid.SetColumn(leftPanel, 0);
                var countText = MakeText($"{nodes.Count}", 9.5, "T3", fontFamily: "Mono");
                countText.VerticalAlignment = VerticalAlignment.Center;
                Grid.SetColumn(countText, 1);
                headerGrid.Children.Add(leftPanel);
                headerGrid.Children.Add(countText);

                var header = new Border { Padding = new Thickness(8, 9, 8, 9), CornerRadius = new CornerRadius(8), Cursor = Cursors.Hand, Child = headerGrid };
                header.MouseEnter += (s, _) => ((Border)s).Background = FindBrush("Bg2");
                header.MouseLeave += (s, _) => ((Border)s).Background = Brushes.Transparent;

                var nodesPanel = new StackPanel { Visibility = Visibility.Collapsed };
                var isExpanded = false;
                header.MouseLeftButtonUp += (_, _) =>
                {
                    isExpanded = !isExpanded;
                    nodesPanel.Visibility = isExpanded ? Visibility.Visible : Visibility.Collapsed;
                    chevron.Text = isExpanded ? "\u25BC" : "\u25B6";
                };

                foreach (var node in nodes)
                {
                    nodesPanel.Children.Add(MakePlanNodeRow(node, planId, subscriptionId));
                }

                NodeListPanel.Children.Add(header);
                NodeListPanel.Children.Add(nodesPanel);
            }

            TbNodeCount.Text = $"{planNodes.Count} plan nodes";
        }
        catch (Exception ex) { AddLog($"Plan nodes error: {ex.Message}", true); }
    }

    private Border MakePlanNodeRow(HnsNodeInfo node, int planId, string? subscriptionId)
    {
        var typeStr = (node.ServiceType ?? "").ToUpperInvariant();
        var isWg = typeStr.Contains("WG") || typeStr.Contains("WIRE");

        var row = new Border
        {
            Padding = new Thickness(10, 7, 10, 7),
            CornerRadius = new CornerRadius(6),
            Margin = new Thickness(0, 1, 0, 1),
            Background = Brushes.Transparent,
            Cursor = Cursors.Hand,
        };

        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(6) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        // Dot
        var dot = new System.Windows.Shapes.Ellipse { Width = 5, Height = 5, Fill = FindBrush("Green"), VerticalAlignment = VerticalAlignment.Center };
        Grid.SetColumn(dot, 0);

        // Name + city
        var nameStack = new StackPanel { Margin = new Thickness(8, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
        nameStack.Children.Add(MakeText(node.Moniker ?? Trunc(node.Address, 14, 0), 11.5, "T1", fontWeight: FontWeights.Medium));
        if (!string.IsNullOrEmpty(node.City))
            nameStack.Children.Add(MakeText(node.City, 9.5, "T3"));
        Grid.SetColumn(nameStack, 1);

        // Connect button
        var meta = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        var badge = new Border
        {
            Background = FindBrush(isWg ? "GreenDim" : "BlueDim"),
            CornerRadius = new CornerRadius(3),
            Padding = new Thickness(4, 1.5, 4, 1.5),
            Margin = new Thickness(0, 0, 6, 0),
            Child = MakeText(isWg ? "WG" : "V2", 8.5, isWg ? "Green" : "Blue", fontWeight: FontWeights.SemiBold, fontFamily: "Mono")
        };
        meta.Children.Add(badge);

        if (subscriptionId != null)
        {
            var connectBtn = new Button
            {
                Content = "Connect",
                FontSize = 9, FontWeight = FontWeights.SemiBold,
                Padding = new Thickness(10, 3, 10, 3),
                Cursor = Cursors.Hand,
                Background = FindBrush("Acc"), Foreground = Brushes.White,
            };
            connectBtn.Template = CreateBtnTemplate();
            var addr = node.Address;
            var sid = subscriptionId;
            connectBtn.Click += async (_, _) =>
            {
                _selectedNode = addr;
                _planSubId = sid;
                _planId = planId;
                StatusSub.Text = node.Moniker ?? Trunc(addr, 16, 6);
                NodeInfoCard.Visibility = Visibility.Collapsed;
                SetState("ing");
                AddLog($"Connecting via Plan #{planId}...");
                try
                {
                    var r = await App.Backend.ConnectViaPlanAsync(ulong.Parse(sid), addr);
                    if (_connState != "ing") return; // cancelled
                    if (r != null)
                    {
                        SetState("on");
                        StatProto.Text = r.Protocol?.ToUpperInvariant() ?? "WG";
                        StatSession.Text = Trunc(r.SessionId ?? "\u2014", 8, 0);
                        StatIp.Text = r.VpnIp ?? "checking...";
                        StatusSub.Text = Trunc(addr, 16, 6);
                        AddLog($"Connected via plan (gas-free)");
                        if (r.SessionId != null)
                        {
                            _lastSessionId = r.SessionId;
                            SessionTracker.Track(r.SessionId, "plan");
                            _ = UpdateAllocationAsync(r.SessionId);
                        }
                    }
                    else SetState("off");
                }
                catch (Exception ex) { SetState("off"); AddLog($"Error: {ex.Message}", true); }
            };
            meta.Children.Add(connectBtn);
        }
        Grid.SetColumn(meta, 2);

        grid.Children.Add(dot);
        grid.Children.Add(nameStack);
        grid.Children.Add(meta);
        row.Child = grid;

        row.MouseEnter += (s, _) => ((Border)s).Background = FindBrush("Bg2");
        row.MouseLeave += (s, _) => ((Border)s).Background = Brushes.Transparent;

        return row;
    }

    private async void BtnRefresh_Click(object sender, RoutedEventArgs e)
    {
        BtnRefresh.IsEnabled = false;
        await LoadNodes();
        BtnRefresh.IsEnabled = true;
    }

    // ═══ NODE TESTING ═══

    private List<NodeTestResult> _testResults = new();
    private bool _testRunning;
    private CancellationTokenSource? _testCts;
    private volatile bool _testStopRequested;
    private int _testTotal, _testDone, _testPassed, _testFailed;
    private bool _testRetestMode;

    // ─── Run History ───
    private List<TestRunSummary> _availableRuns = new();
    private TestRunSummary? _viewingHistoryRun;
    private List<NodeTestResult>? _historyResults;

    // ─── Test UI refs for live updates ───
    private TextBlock? _testStatusTb;
    private TextBlock? _testProgressTb;

    // ─── Test table sort/filter state ───
    private string _testFilter = "all"; // all | wg | v2 | pass | fail
    private string _testSortCol = ""; // "", "speed", "peers", "country", "result"
    private bool _testSortAsc;

    private void RenderTestTab()
    {
        NodeListPanel.Children.Clear();
        RefreshRunHistory();
        RenderTestDashboard();
    }

    private void RenderTestDashboard()
    {
        // Populate XAML-defined panels
        RenderTestControls();
        RenderTestStats();
        RenderTestProgress();
        RenderTestTable();
    }

    private void RenderTestControls()
    {
        TestControls.Children.Clear();

        // ─── History view mode ───
        if (_viewingHistoryRun != null)
        {
            var backBtn = new Button { Content = "\u25C0 Back to Current", FontSize = 11, FontWeight = FontWeights.SemiBold, Padding = new Thickness(16, 8, 16, 8), Cursor = Cursors.Hand, Background = FindBrush("Acc"), Foreground = Brushes.White, Margin = new Thickness(0, 0, 8, 0) };
            backBtn.Template = CreateBtnTemplate();
            backBtn.Click += (_, _) => ExitHistoryView();
            TestControls.Children.Add(backBtn);

            var infoTb = MakeText($"Viewing: {_viewingHistoryRun.FolderName}  |  {_viewingHistoryRun.Total} nodes  |  {_viewingHistoryRun.Passed} pass  |  {_viewingHistoryRun.AvgSpeed:F1} Mbps avg  |  {_viewingHistoryRun.Duration}", 11, "T2");
            infoTb.VerticalAlignment = VerticalAlignment.Center;
            TestControls.Children.Add(infoTb);

            // Comparison with current results
            if (_testResults.Count > 0)
            {
                var curPassed = _testResults.Count(r => r.Pass);
                var diffPassed = curPassed - _viewingHistoryRun.Passed;
                var diffLabel = diffPassed > 0 ? $"+{diffPassed}" : $"{diffPassed}";
                var diffColor = diffPassed > 0 ? "Green" : diffPassed < 0 ? "Red" : "T3";
                var sep = new Border { Width = 1, Height = 20, Background = FindBrush("Bg3"), Margin = new Thickness(8, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center };
                TestControls.Children.Add(sep);
                var compTb = MakeText($"vs Current: {diffLabel} passed", 10, diffColor, fontWeight: FontWeights.SemiBold);
                compTb.VerticalAlignment = VerticalAlignment.Center;
                TestControls.Children.Add(compTb);
            }

            // Filter buttons for history view
            var filterSep = new Border { Width = 1, Height = 20, Background = FindBrush("Bg3"), Margin = new Thickness(8, 0, 6, 0), VerticalAlignment = VerticalAlignment.Center };
            TestControls.Children.Add(filterSep);
            foreach (var (label, filter) in new[] { ("All", "all"), ("WG", "wg"), ("V2", "v2"), ("Pass", "pass"), ("Fail", "fail") })
            {
                var isActive = _testFilter == filter;
                var fb = new Button { Content = label, FontSize = 10, Padding = new Thickness(10, 6, 10, 6), Cursor = Cursors.Hand, Margin = new Thickness(0, 0, 3, 0) };
                fb.Background = isActive ? FindBrush("Acc") : FindBrush("Bg2");
                fb.Foreground = isActive ? Brushes.White : FindBrush("T2");
                fb.Template = CreateBtnTemplate();
                var f = filter;
                fb.Click += (_, _) => { _testFilter = f; RenderTestControls(); RenderTestTable(); };
                TestControls.Children.Add(fb);
            }
            return;
        }

        if (!_testRunning)
        {
            var startBtn = new Button { Content = "New Test", FontSize = 11, FontWeight = FontWeights.SemiBold, Padding = new Thickness(16, 8, 16, 8), Cursor = Cursors.Hand, Background = FindBrush("Acc"), Foreground = Brushes.White, Margin = new Thickness(0, 0, 6, 0) };
            startBtn.Template = CreateBtnTemplate();
            startBtn.Click += async (_, _) =>
            {
                if (_testRunning) return;
                _refreshCts?.Cancel();
                AddLog("[TEST] Starting scan...");
                try
                {
                    await StartBatchTestAsync();
                }
                catch (Exception ex)
                {
                    AddLog($"[TEST] CRASH: {ex.Message}");
                    AddLog($"[TEST] Stack: {ex.StackTrace?[..Math.Min(200, ex.StackTrace?.Length ?? 0)]}");
                    _testRunning = false;
                }
            };
            TestControls.Children.Add(startBtn);

            var resumeBtn = new Button { Content = "Resume", FontSize = 11, FontWeight = FontWeights.SemiBold, Padding = new Thickness(16, 8, 16, 8), Cursor = Cursors.Hand, Background = FindBrush("Acc"), Foreground = Brushes.White, Margin = new Thickness(0, 0, 6, 0), Opacity = 0.85 };
            resumeBtn.Template = CreateBtnTemplate();
            resumeBtn.Click += async (_, _) =>
            {
                if (_testRunning) return;
                // Load cached results and nodes, find untested, continue testing
                var cachedTests = DiskCache.Load<List<NodeTestResult>>("test-results", TimeSpan.FromDays(7));
                if (cachedTests?.data != null) _testResults = cachedTests.Value.data;
                var testedAddresses = new HashSet<string>(_testResults.Select(r => r.Address));
                var untested = _allNodes.Where(n => n.Moniker != null && !testedAddresses.Contains(n.Address)).ToList();
                if (untested.Count == 0) { AddLog("[TEST] Nothing to resume — all nodes tested"); return; }
                AddLog($"[TEST] Resuming — {untested.Count} untested nodes remaining");
                _testRunning = true;
                _testCts = new CancellationTokenSource();
                _testStopRequested = false;
                _testStartTime = DateTime.UtcNow;
                _testTotal = untested.Count;
                _testDone = 0;
                _testPassed = _testResults.Count(r => r.Pass);
                _testFailed = _testResults.Count(r => !r.Pass);
                RenderTestDashboard();
                var backend = (NativeVpnClient)App.Backend;
                foreach (var node in untested)
                {
                    if (_testStopRequested || _testCts.IsCancellationRequested) break;
                    Dispatcher.Invoke(() =>
                    {
                        if (_testStatusTb != null) _testStatusTb.Text = $"Testing {node.Moniker ?? Trunc(node.Address, 16, 0)}...";
                        AddLog($"[TEST] Testing {node.Moniker ?? Trunc(node.Address, 16, 0)}...");
                    });
                    try
                    {
                        var result = await backend.TestNodeAsync(node.Address, node, _testCts.Token);
                        _testResults.Insert(0, result);
                        _testDone++;
                        if (result.Pass) _testPassed++; else _testFailed++;
                        Dispatcher.Invoke(() =>
                        {
                            try { RenderTestStats(); RenderTestProgress(); RenderTestTable(); } catch (Exception rx) { AddLog($"[TEST] Render: {rx.Message}"); }
                        });
                        if (_testDone % 5 == 0) DiskCache.Save("test-results", _testResults);
                    }
                    catch (OperationCanceledException) { break; }
                    catch (Exception ex) { AddLog($"[TEST] Error on {Trunc(node.Address, 16, 0)}: {ex.Message}"); _testDone++; }
                }
                DiskCache.Save("test-results", _testResults);
                SaveRunArchive("Resume");
                _testRunning = false;
                Dispatcher.Invoke(RenderTestDashboard);
            };
            TestControls.Children.Add(resumeBtn);

            var failCount = _testResults.Count(r => !r.Pass);
            var retestBtn = new Button { Content = $"Retest Failed ({failCount})", FontSize = 11, FontWeight = FontWeights.SemiBold, Padding = new Thickness(16, 8, 16, 8), Cursor = Cursors.Hand, Background = FindBrush("RedDim"), Foreground = FindBrush("Red"), Margin = new Thickness(0, 0, 6, 0) };
            retestBtn.Template = CreateBtnTemplate();
            retestBtn.Click += async (_, _) => await RetestFailedAsync();
            TestControls.Children.Add(retestBtn);

            var exportBtn = new Button { Content = "Export", FontSize = 11, Padding = new Thickness(16, 8, 16, 8), Cursor = Cursors.Hand, Background = FindBrush("Bg2"), Foreground = FindBrush("T1"), Margin = new Thickness(0, 0, 6, 0) };
            exportBtn.Template = CreateBtnTemplate();
            exportBtn.Click += (_, _) => ExportTestResults();
            TestControls.Children.Add(exportBtn);

            var rescanBtn = new Button { Content = "Rescan", FontSize = 11, Padding = new Thickness(16, 8, 16, 8), Cursor = Cursors.Hand, Background = FindBrush("Bg2"), Foreground = FindBrush("T1"), Margin = new Thickness(0, 0, 6, 0) };
            rescanBtn.Template = CreateBtnTemplate();
            rescanBtn.Click += async (_, _) =>
            {
                AddLog("[TEST] Rescanning nodes from chain...");
                rescanBtn.IsEnabled = false;
                try
                {
                    var data = await App.Backend.GetAllNodesAsync();
                    if (data?.Nodes != null) { _allNodes = data.Nodes; DiskCache.Save("nodes", _allNodes); AddLog($"[TEST] Rescan: {_allNodes.Count} nodes"); }
                }
                catch (Exception ex) { AddLog($"[TEST] Rescan failed: {ex.Message}"); }
                rescanBtn.IsEnabled = true;
                RenderTestStats();
            };
            TestControls.Children.Add(rescanBtn);

            var resetBtn = new Button { Content = "Reset", FontSize = 11, Padding = new Thickness(16, 8, 16, 8), Cursor = Cursors.Hand, Background = FindBrush("Bg2"), Foreground = FindBrush("T2"), Margin = new Thickness(0, 0, 6, 0) };
            resetBtn.Template = CreateBtnTemplate();
            resetBtn.Click += (_, _) => { _testResults.Clear(); DiskCache.Clear("test-results"); _testRunNumber = 0; RenderTestDashboard(); };
            TestControls.Children.Add(resetBtn);

            // ─── Filter buttons ───
            var filterSep = new Border { Width = 1, Height = 20, Background = FindBrush("Bg3"), Margin = new Thickness(6, 0, 6, 0), VerticalAlignment = VerticalAlignment.Center };
            TestControls.Children.Add(filterSep);
            foreach (var (label, filter) in new[] { ("All", "all"), ("WG", "wg"), ("V2", "v2"), ("Pass", "pass"), ("Fail", "fail") })
            {
                var isActive = _testFilter == filter;
                var fb = new Button { Content = label, FontSize = 10, Padding = new Thickness(10, 6, 10, 6), Cursor = Cursors.Hand, Margin = new Thickness(0, 0, 3, 0) };
                fb.Background = isActive ? FindBrush("Acc") : FindBrush("Bg2");
                fb.Foreground = isActive ? Brushes.White : FindBrush("T2");
                fb.Template = CreateBtnTemplate();
                var f = filter;
                fb.Click += (_, _) => { _testFilter = f; RenderTestControls(); RenderTestTable(); };
                TestControls.Children.Add(fb);
            }
        }
        else
        {
            var stopBtn = new Button { Content = "Stop", FontSize = 11, FontWeight = FontWeights.SemiBold, Padding = new Thickness(16, 8, 16, 8), Cursor = Cursors.Hand, Background = FindBrush("Red"), Foreground = Brushes.White, Margin = new Thickness(0, 0, 6, 0) };
            stopBtn.Template = CreateBtnTemplate();
            stopBtn.Click += (_, _) =>
            {
                _testStopRequested = true;
                _testCts?.Cancel();
                DiskCache.Save("test-results", _testResults); // save before cleanup
                AddLog("[TEST] Stop requested — saving results & cleaning up...");
                _ = Task.Run(async () =>
                {
                    try { await App.Backend.DisconnectAsync(); } catch { }
                    try
                    {
                        var wg = NativeVpnClient.FindBinaryPublic("wireguard.exe");
                        if (wg != null) System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(wg, "/uninstalltunnelservice wgsent0") { CreateNoWindow = true, UseShellExecute = false })?.WaitForExit(3000);
                    }
                    catch { }
                    Dispatcher.Invoke(() =>
                    {
                        _testRunning = false;
                        AddLog("[TEST] Stopped");
                        RenderTestDashboard();
                    });
                });
            };
            TestControls.Children.Add(stopBtn);

            _testStatusTb = MakeText("Scanning...", 11, "T2");
            _testStatusTb.VerticalAlignment = VerticalAlignment.Center;
            _testStatusTb.Margin = new Thickness(8, 0, 0, 0);
            TestControls.Children.Add(_testStatusTb);
        }

        // ─── Single node test input (always visible) ───
        if (!_testRunning)
        {
            var sep2 = new Border { Width = 1, Height = 20, Background = FindBrush("Bg3"), Margin = new Thickness(6, 0, 6, 0), VerticalAlignment = VerticalAlignment.Center };
            TestControls.Children.Add(sep2);
            var nodeInput = new TextBox
            {
                Width = 180, FontSize = 10, Padding = new Thickness(6, 5, 6, 5),
                Background = FindBrush("Bg1"), Foreground = FindBrush("T1"),
                BorderBrush = FindBrush("Bdr"), BorderThickness = new Thickness(1),
                VerticalAlignment = VerticalAlignment.Center,
            };
            nodeInput.GotFocus += (s, _) => { if (((TextBox)s).Text == "sentnode1...") ((TextBox)s).Text = ""; };
            nodeInput.Text = "sentnode1...";
            nodeInput.Foreground = FindBrush("T3");
            nodeInput.GotFocus += (s, _) => ((TextBox)s).Foreground = FindBrush("T1");
            TestControls.Children.Add(nodeInput);

            var goBtn = new Button { Content = "Go", FontSize = 10, FontWeight = FontWeights.SemiBold, Padding = new Thickness(10, 5, 10, 5), Cursor = Cursors.Hand, Background = FindBrush("Acc"), Foreground = Brushes.White, Margin = new Thickness(3, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
            goBtn.Template = CreateBtnTemplate();
            goBtn.Click += async (_, _) =>
            {
                var addr = nodeInput.Text.Trim();
                if (!addr.StartsWith("sentnode1") || addr.Length < 20) { AddLog("[TEST] Invalid node address"); return; }
                goBtn.IsEnabled = false;
                _testResults.RemoveAll(r => r.Address == addr);
                AddLog($"[TEST] Testing single node {Trunc(addr, 16, 6)}...");
                try
                {
                    var nodeInfo = _allNodes.FirstOrDefault(n => n.Address == addr);
                    var result = await ((NativeVpnClient)App.Backend).TestNodeAsync(addr, nodeInfo, CancellationToken.None);
                    _testResults.Insert(0, result);
                    DiskCache.Save("test-results", _testResults);
                    AddLog($"[TEST] {(result.Pass ? "PASS" : "FAIL")} — {result.SpeedMbps:F1} Mbps");
                }
                catch (Exception ex) { AddLog($"[TEST] Single test failed: {ex.Message}"); }
                goBtn.IsEnabled = true;
                RenderTestDashboard();
            };
            TestControls.Children.Add(goBtn);

            // ─── Run History dropdown ───
            RefreshRunHistory();
            if (_availableRuns.Count > 0)
            {
                var historySep = new Border { Width = 1, Height = 20, Background = FindBrush("Bg3"), Margin = new Thickness(6, 0, 6, 0), VerticalAlignment = VerticalAlignment.Center };
                TestControls.Children.Add(historySep);

                var historyLabel = MakeText("History:", 10, "T3");
                historyLabel.VerticalAlignment = VerticalAlignment.Center;
                historyLabel.Margin = new Thickness(0, 0, 4, 0);
                TestControls.Children.Add(historyLabel);

                var combo = new ComboBox
                {
                    FontSize = 10, MinWidth = 220, MaxWidth = 400,
                    Background = FindBrush("Bg1"), Foreground = FindBrush("T1"),
                    BorderBrush = FindBrush("Bdr"), BorderThickness = new Thickness(1),
                    VerticalAlignment = VerticalAlignment.Center,
                    Padding = new Thickness(6, 4, 6, 4),
                };
                combo.Items.Add(new ComboBoxItem { Content = $"({_availableRuns.Count} saved runs)", IsEnabled = false, FontStyle = FontStyles.Italic });
                foreach (var run in _availableRuns.Take(20))
                {
                    var item = new ComboBoxItem { Content = run.DisplayLabel, Tag = run };
                    combo.Items.Add(item);
                }
                combo.SelectionChanged += (_, _) =>
                {
                    if (combo.SelectedItem is ComboBoxItem sel && sel.Tag is TestRunSummary run)
                        LoadHistoricalRun(run);
                };
                TestControls.Children.Add(combo);
            }
        }
    }

    private void RenderTestStats()
    {
        TestStatsGrid.Children.Clear();
        TestStatsGrid.ColumnDefinitions.Clear();
        TestStatsGrid.RowDefinitions.Clear();
        TestStatsGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        TestStatsGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        for (int i = 0; i < 6; i++)
            TestStatsGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        // Use history data when viewing a historical run, otherwise current results
        var source = _viewingHistoryRun != null && _historyResults != null ? _historyResults : _testResults;

        var totalOnline = _allNodes.Count(n => n.Moniker != null);
        var tested = source.Count;
        var passed = source.Count(r => r.Pass);
        var failed = tested - passed;
        var pass10 = source.Count(r => r.SpeedMbps >= 10);
        var avgSpeed = source.Where(r => r.SpeedMbps > 0).Select(r => r.SpeedMbps!.Value).DefaultIfEmpty(0).Average();
        var passRate = tested > 0 ? $"{passed * 100 / tested}%" : "--";
        // Not Online = tested nodes with no speed AND (peers=0 or null) — truly dead nodes
        var notOnline = source.Count(r => (r.SpeedMbps == null || r.SpeedMbps == 0) && (r.Peers == null || r.Peers == 0));
        var deadPlan = source.Count(r => r.InPlan && !r.Pass);
        var connected = source.Count(r => r.Connected);

        void AddStat(int col, string label, string value, string color, string sub)
        {
            var card = new Border { Background = FindBrush("Bg1"), CornerRadius = new CornerRadius(8), Padding = new Thickness(10, 8, 10, 8), Margin = new Thickness(3) };
            var s = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center };
            s.Children.Add(MakeText(label, 9, "T3", HorizontalAlignment.Center));
            var valTb = MakeText(value, 20, color, HorizontalAlignment.Center, new Thickness(0, 2, 0, 2), fontWeight: FontWeights.Bold);
            s.Children.Add(valTb);
            s.Children.Add(MakeText(sub, 9, "T3", HorizontalAlignment.Center));
            card.Child = s;
            Grid.SetColumn(card, col);
            TestStatsGrid.Children.Add(card);
            if (col == 0) _testProgressTb = valTb;
        }

        // When viewing history, show comparison deltas vs current
        var isHistory = _viewingHistoryRun != null;
        var histLabel = isHistory ? $"({_viewingHistoryRun!.FolderName})" : "";

        AddStat(0, isHistory ? "Tested" : "Tested", _testRunning ? $"{_testDone}/{_testTotal}" : $"{tested}", "T1", isHistory ? histLabel : $"of {totalOnline} online");
        AddStat(1, "Total Failed", $"{failed}", failed > 0 ? "Red" : "T3", tested > 0 ? $"{failed * 100 / tested}% failure rate" : "--");
        AddStat(2, "Pass 10 Mbps", $"{pass10}", "Green", connected > 0 ? $"{pass10 * 100 / connected}% of connected" : "--");
        AddStat(3, "Dead Plan", $"{deadPlan}", deadPlan > 0 ? "Red" : "T3", tested > 0 ? $"in-plan but failed" : "--");
        var baseline = isHistory ? _viewingHistoryRun!.Baseline : ((NativeVpnClient)App.Backend).LastBaseline;
        var baselineStr = baseline.HasValue ? $"{baseline:F1}" : "--";
        var baselineColor = baseline >= 30 ? "Green" : baseline >= 10 ? "Amber" : baseline.HasValue ? "Red" : "T3";
        AddStat(4, "Baseline", baselineStr, baselineColor, baseline.HasValue ? "Mbps (direct)" : "not measured");
        AddStat(5, "Pass Rate", passRate, "T1", $"{passed} of {tested} | {notOnline} dead");

        // ─── Speed history pills (last 10 node speeds) ───
        var recentSpeeds = source.Where(r => r.SpeedMbps > 0).Take(10).ToList();
        if (recentSpeeds.Count > 0)
        {
            var pillRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(6, 6, 6, 0) };
            pillRow.Children.Add(MakeText("Recent: ", 9, "T3", margin: new Thickness(0, 0, 4, 0)));
            foreach (var s in recentSpeeds)
            {
                var pillColor = s.SpeedMbps >= 10 ? "Green" : s.SpeedMbps >= 3 ? "Amber" : "Red";
                var pill = new Border
                {
                    Background = FindBrush(pillColor), CornerRadius = new CornerRadius(3),
                    Padding = new Thickness(4, 1, 4, 1), Margin = new Thickness(1, 0, 1, 0),
                    Opacity = 0.8, ToolTip = $"{s.Moniker}: {s.SpeedMbps:F1} Mbps",
                };
                pill.Child = MakeText($"{s.SpeedMbps:F0}", 8, "Bg0", fontWeight: FontWeights.SemiBold);
                pillRow.Children.Add(pill);
            }
            Grid.SetRow(pillRow, 1);
            Grid.SetColumnSpan(pillRow, 6);
            TestStatsGrid.Children.Add(pillRow);
        }
    }

    private void RenderTestProgress()
    {
        TestProgressPanel.Children.Clear();

        // ─── History view: show run info instead of progress ───
        if (_viewingHistoryRun != null)
        {
            var histPct = _viewingHistoryRun.Total > 0 ? (double)_viewingHistoryRun.Passed / _viewingHistoryRun.Total : 0;
            var header = new Grid();
            header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var left = new StackPanel();
            left.Children.Add(MakeText($"Historical Run: {_viewingHistoryRun.FolderName}", 11, "T1", fontWeight: FontWeights.SemiBold));
            left.Children.Add(MakeText($"{_viewingHistoryRun.Passed} of {_viewingHistoryRun.Total} passed ({(int)(histPct * 100)}%)", 10, "T2"));
            Grid.SetColumn(left, 0);
            var rightStack = new StackPanel { HorizontalAlignment = HorizontalAlignment.Right };
            rightStack.Children.Add(MakeText("Duration", 9, "T3"));
            rightStack.Children.Add(MakeText(_viewingHistoryRun.Duration, 12, "T1", fontWeight: FontWeights.SemiBold));
            Grid.SetColumn(rightStack, 1);
            header.Children.Add(left);
            header.Children.Add(rightStack);
            TestProgressPanel.Children.Add(header);

            // Pass rate bar
            var barGrid = new Grid { Margin = new Thickness(0, 6, 0, 2), Height = 8 };
            var clampedHistPct = Math.Max(0.01, Math.Min(0.99, histPct));
            barGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(clampedHistPct, GridUnitType.Star) });
            barGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1 - clampedHistPct, GridUnitType.Star) });
            barGrid.Children.Add(new Border { Height = 8, Background = FindBrush("Bg3"), CornerRadius = new CornerRadius(4) });
            Grid.SetColumnSpan((UIElement)barGrid.Children[0], 2);
            var fill = new Border { Height = 8, Background = FindBrush("Green"), CornerRadius = new CornerRadius(4) };
            Grid.SetColumn(fill, 0);
            barGrid.Children.Add(fill);
            TestProgressPanel.Children.Add(barGrid);

            // Comparison with current
            var meta = new Grid();
            var metaLeft = $"{_viewingHistoryRun.Fast} fast (>=10 Mbps) | Avg: {_viewingHistoryRun.AvgSpeed:F1} Mbps";
            meta.Children.Add(MakeText(metaLeft, 10, "T3"));
            if (_testResults.Count > 0)
            {
                var curPassed = _testResults.Count(r => r.Pass);
                var curFailed = _testResults.Count(r => !r.Pass);
                var diffP = curPassed - _viewingHistoryRun.Passed;
                var diffF = curFailed - _viewingHistoryRun.Failed;
                var compText = $"Current: {diffP:+0;-0;0} passed, {diffF:+0;-0;0} failed";
                var compTb = MakeText(compText, 10, diffP >= 0 ? "Green" : "Red");
                compTb.HorizontalAlignment = HorizontalAlignment.Right;
                meta.Children.Add(compTb);
            }
            else
            {
                var noComp = MakeText("No current results to compare", 10, "T3");
                noComp.HorizontalAlignment = HorizontalAlignment.Right;
                meta.Children.Add(noComp);
            }
            TestProgressPanel.Children.Add(meta);
            return;
        }

        var pct = _testTotal > 0 ? (double)_testDone / _testTotal : 0;

        // Progress header
        var hdr = new Grid();
        hdr.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        hdr.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var leftS = new StackPanel();
        var progressTitle = _testRetestMode ? $"Retesting Failed #{_testDone}/{_testTotal}" : _testRunNumber > 0 ? $"Test #{_testRunNumber}" : "Audit Progress";
        leftS.Children.Add(MakeText(progressTitle, 11, "T1", fontWeight: FontWeights.SemiBold));
        leftS.Children.Add(MakeText($"{(int)(pct * 100)}% Complete", 10, "T2"));
        Grid.SetColumn(leftS, 0);
        var etaStack = new StackPanel { HorizontalAlignment = HorizontalAlignment.Right };
        etaStack.Children.Add(MakeText("Est. Remaining", 9, "T3"));
        etaStack.Children.Add(MakeText(_testRunning && _testDone > 0 ? EstimateEta() : "--:--", 12, "T1", fontWeight: FontWeights.SemiBold));
        Grid.SetColumn(etaStack, 1);
        hdr.Children.Add(leftS);
        hdr.Children.Add(etaStack);
        TestProgressPanel.Children.Add(hdr);

        // Progress bar
        var pBarGrid = new Grid { Margin = new Thickness(0, 6, 0, 2), Height = 8 };
        var clampedPct = Math.Max(0.01, Math.Min(0.99, pct));
        pBarGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(clampedPct, GridUnitType.Star) });
        pBarGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1 - clampedPct, GridUnitType.Star) });
        pBarGrid.Children.Add(new Border { Height = 8, Background = FindBrush("Bg3"), CornerRadius = new CornerRadius(4) });
        Grid.SetColumnSpan((UIElement)pBarGrid.Children[0], 2);
        if (_testDone > 0)
        {
            var pFill = new Border { Height = 8, Background = FindBrush("Acc"), CornerRadius = new CornerRadius(4) };
            Grid.SetColumn(pFill, 0);
            pBarGrid.Children.Add(pFill);
        }
        TestProgressPanel.Children.Add(pBarGrid);

        // Meta
        var pMeta = new Grid();
        pMeta.Children.Add(MakeText($"{_testDone} / {(_testRunning ? _testTotal : totalOnline())} Available Nodes", 10, "T3"));
        _testStatusTb = MakeText(_testRunning ? "Scanning..." : "Standby", 10, "T2");
        _testStatusTb.HorizontalAlignment = HorizontalAlignment.Right;
        pMeta.Children.Add(_testStatusTb);
        TestProgressPanel.Children.Add(pMeta);

        int totalOnline() => _allNodes.Count(n => n.Moniker != null);
    }

    private string EstimateEta()
    {
        if (_testDone == 0 || !_testRunning) return "--:--";
        var elapsed = (DateTime.UtcNow - _testStartTime).TotalSeconds;
        var perNode = elapsed / _testDone;
        var remaining = (_testTotal - _testDone) * perNode;
        var ts = TimeSpan.FromSeconds(remaining);
        return ts.TotalHours >= 1 ? $"{(int)ts.TotalHours}:{ts.Minutes:D2}:{ts.Seconds:D2}" : $"{ts.Minutes:D2}:{ts.Seconds:D2}";
    }

    private DateTime _testStartTime;

    private void RenderTestTable()
    {
        TestTableBody.Children.Clear();
        var source = _viewingHistoryRun != null && _historyResults != null ? _historyResults : _testResults;
        var results = GetFilteredSortedResults(source);
        var headerLabel = _viewingHistoryRun != null ? $"History: {_viewingHistoryRun.FolderName}" : "Node Performance Matrix";
        TestTableTitle.Text = headerLabel;
        TestResultsCount.Text = _testFilter == "all" ? $"{source.Count} entries" : $"{results.Count} of {source.Count}";

        if (source.Count == 0)
        {
            TestTableBody.Children.Add(MakeText("No results yet — click New Test to start", 12, "T3", HorizontalAlignment.Center, new Thickness(0, 30, 0, 0)));
            return;
        }

        // Table header (clickable for sort)
        var headerRow = new Grid { Background = FindBrush("Bg2"), Margin = new Thickness(0, 0, 0, 2) };
        AddTableColumns(headerRow);
        AddTableCell(headerRow, 0, "Transport", "T3", FontWeights.Bold, 9);
        AddTableCell(headerRow, 1, "Node", "T3", FontWeights.Bold, 9);
        AddSortableHeader(headerRow, 2, "Country", "country");
        AddTableCell(headerRow, 3, "City", "T3", FontWeights.Bold, 9);
        AddSortableHeader(headerRow, 4, "Peers", "peers");
        AddSortableHeader(headerRow, 5, "Speed", "speed");
        AddSortableHeader(headerRow, 6, "BW", "bw");
        AddSortableHeader(headerRow, 7, "Result", "result");
        TestTableBody.Children.Add(headerRow);

        foreach (var r in results.Take(200))
        {
            var row = new Grid { Margin = new Thickness(0, 0, 0, 1) };
            row.Background = Brushes.Transparent;
            row.MouseEnter += (s, _) => ((Grid)s).Background = FindBrush("Bg1");
            row.MouseLeave += (s, _) => ((Grid)s).Background = Brushes.Transparent;
            AddTableColumns(row);

            // Transport with detail
            var transport = r.Transport ?? (r.Protocol?.ToUpperInvariant() == "WIREGUARD" ? "WG" : "V2");
            AddTableCell(row, 0, transport, transport.StartsWith("WG") ? "Green" : "Blue", FontWeights.SemiBold, 9);

            // Node (click to copy)
            var nodeCell = MakeText(r.Moniker ?? Trunc(r.Address, 16, 0), 10, "T1", fontWeight: FontWeights.Normal);
            nodeCell.Cursor = Cursors.Hand;
            nodeCell.ToolTip = r.Address;
            nodeCell.Padding = new Thickness(4, 5, 4, 5);
            nodeCell.MouseLeftButtonUp += (_, _) => { Clipboard.SetText(r.Address); AddLog($"Copied: {r.Address}"); };
            Grid.SetColumn(nodeCell, 1);
            row.Children.Add(nodeCell);

            // Country with flag
            var countryCell = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            var cc = CountryCode(r.Country ?? "");
            if (cc != "??" && _flagCache.TryGetValue(cc, out var flagBmp) && flagBmp != null)
                countryCell.Children.Add(new Image { Width = 16, Height = 11, Stretch = System.Windows.Media.Stretch.Uniform, Source = flagBmp, Margin = new Thickness(4, 0, 3, 0), VerticalAlignment = VerticalAlignment.Center });
            countryCell.Children.Add(MakeText(cc != "??" ? cc : "\u2014", 9, "T2"));
            Grid.SetColumn(countryCell, 2);
            row.Children.Add(countryCell);

            // City
            AddTableCell(row, 3, r.City ?? "\u2014", "T3", FontWeights.Normal, 9);

            // Peers
            AddTableCell(row, 4, r.Peers?.ToString() ?? "\u2014", "T3", FontWeights.Normal, 10);

            // Speed (colored)
            var speedColor = r.SpeedMbps >= 10 ? "Green" : r.SpeedMbps > 0 ? "Amber" : "Red";
            AddTableCell(row, 5, r.SpeedMbps > 0 ? $"{r.SpeedMbps:F1}" : "\u2014", speedColor, FontWeights.SemiBold, 10);

            // Total BW = speed × max(peers, 1)
            var effectivePeers = Math.Max(r.Peers ?? 1, 1);
            var totalBw = (r.SpeedMbps ?? 0) * effectivePeers;
            AddTableCell(row, 6, totalBw > 0 ? $"{totalBw:F0}" : "\u2014", "T3", FontWeights.Normal, 9);

            // Result badge: FAST / SLOW / FAIL
            string badge; string badgeColor;
            if (!r.Pass) { badge = "FAIL"; badgeColor = "Red"; }
            else if ((r.SpeedMbps ?? 0) >= 10) { badge = "FAST"; badgeColor = "Green"; }
            else { badge = "SLOW"; badgeColor = "Amber"; }
            AddTableCell(row, 7, badge, badgeColor, FontWeights.Bold, 10);

            // Click row to expand diagnostics
            row.Cursor = Cursors.Hand;
            row.MouseLeftButtonUp += (_, _) => ToggleRowDiag(r, row);

            TestTableBody.Children.Add(row);
        }
    }

    private void AddSortableHeader(Grid headerRow, int col, string label, string sortKey)
    {
        var indicator = _testSortCol == sortKey ? (_testSortAsc ? " \u25B2" : " \u25BC") : "";
        var tb = MakeText(label + indicator, 9, _testSortCol == sortKey ? "T1" : "T3", fontWeight: FontWeights.Bold);
        tb.Padding = new Thickness(6, 5, 6, 5);
        tb.Cursor = Cursors.Hand;
        tb.MouseLeftButtonUp += (_, _) =>
        {
            if (_testSortCol == sortKey) _testSortAsc = !_testSortAsc;
            else { _testSortCol = sortKey; _testSortAsc = false; }
            RenderTestTable();
        };
        Grid.SetColumn(tb, col);
        headerRow.Children.Add(tb);
    }

    private void ToggleRowDiag(NodeTestResult r, Grid row)
    {
        // Check if diag panel already exists (toggle off)
        var idx = TestTableBody.Children.IndexOf(row);
        if (idx + 1 < TestTableBody.Children.Count && TestTableBody.Children[idx + 1] is Border b && b.Tag as string == "diag")
        {
            TestTableBody.Children.RemoveAt(idx + 1);
            return;
        }

        // Create expandable diagnostics panel
        var diag = new Border
        {
            Background = FindBrush("Bg1"), Padding = new Thickness(12, 8, 12, 8),
            Margin = new Thickness(0, 0, 0, 2), CornerRadius = new CornerRadius(0, 0, 6, 6),
            Tag = "diag",
        };
        var stack = new StackPanel();

        void AddDiagLine(string label, string value, string color = "T2")
        {
            var line = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 1, 0, 1) };
            line.Children.Add(MakeText($"{label}: ", 9, "T3", fontWeight: FontWeights.SemiBold));
            line.Children.Add(MakeText(value, 9, color));
            stack.Children.Add(line);
        }

        AddDiagLine("Address", r.Address);
        AddDiagLine("Session", r.SessionId ?? "\u2014");
        AddDiagLine("Protocol", r.Protocol ?? "\u2014");
        AddDiagLine("Transport", r.Transport ?? "\u2014");
        AddDiagLine("Connect Time", $"{r.ConnectSeconds:F1}s");
        AddDiagLine("Speed", r.SpeedMbps > 0 ? $"{r.SpeedMbps:F2} Mbps ({r.SpeedMethod})" : "\u2014");
        AddDiagLine("Google", r.GoogleAccessible == true ? $"OK ({r.GoogleLatencyMs}ms)" : r.GoogleAccessible == false ? "Blocked" : "\u2014");
        AddDiagLine("Peers", r.Peers?.ToString() ?? "\u2014");
        AddDiagLine("Reported BW", r.ReportedBandwidth > 0 ? $"{r.ReportedBandwidth:F0} Mbps" : "\u2014");
        AddDiagLine("In Plan", r.InPlan ? "Yes" : "No");
        AddDiagLine("Tested", r.Timestamp.ToString("yyyy-MM-dd HH:mm:ss UTC"));
        if (!string.IsNullOrEmpty(r.Error)) AddDiagLine("Error", r.Error, "Red");

        // Copy + Retest buttons
        var btnRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 6, 0, 0) };
        var copyBtn = new Button { Content = "Copy Address", FontSize = 9, Padding = new Thickness(8, 3, 8, 3), Cursor = Cursors.Hand, Background = FindBrush("Bg2"), Foreground = FindBrush("T1"), Margin = new Thickness(0, 0, 6, 0) };
        copyBtn.Template = CreateBtnTemplate();
        copyBtn.Click += (_, _) => { Clipboard.SetText(r.Address); AddLog($"Copied: {r.Address}"); };
        btnRow.Children.Add(copyBtn);
        var retestOneBtn = new Button { Content = "Retest This Node", FontSize = 9, Padding = new Thickness(8, 3, 8, 3), Cursor = Cursors.Hand, Background = FindBrush("Acc"), Foreground = Brushes.White };
        retestOneBtn.Template = CreateBtnTemplate();
        retestOneBtn.Click += async (_, _) =>
        {
            retestOneBtn.IsEnabled = false;
            _testResults.RemoveAll(x => x.Address == r.Address);
            AddLog($"[TEST] Retesting {r.Moniker ?? Trunc(r.Address, 16, 0)}...");
            try
            {
                var result = await ((NativeVpnClient)App.Backend).TestNodeAsync(r.Address, null, CancellationToken.None);
                _testResults.Insert(0, result);
                DiskCache.Save("test-results", _testResults);
                AddLog($"[TEST] {(result.Pass ? "PASS" : "FAIL")} — {result.SpeedMbps:F1} Mbps");
            }
            catch (Exception ex) { AddLog($"[TEST] Retest failed: {ex.Message}"); }
            RenderTestDashboard();
        };
        btnRow.Children.Add(retestOneBtn);
        stack.Children.Add(btnRow);

        diag.Child = stack;
        TestTableBody.Children.Insert(idx + 1, diag);
    }

    private static void AddTableColumns(Grid g)
    {
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(70) });   // Transport
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) }); // Node
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(70) });   // Country
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(80) });   // City
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(45) });   // Peers
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(65) });   // Speed
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(60) });   // BW
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(55) });   // Result
    }

    private void AddTableCell(Grid row, int col, string text, string color, FontWeight weight, double size)
    {
        var tb = MakeText(text, size, color, fontWeight: weight);
        tb.Padding = new Thickness(6, 5, 6, 5);
        tb.VerticalAlignment = VerticalAlignment.Center;
        Grid.SetColumn(tb, col);
        row.Children.Add(tb);
    }

    private async Task RetestFailedAsync()
    {
        var failedNodes = _testResults.Where(r => !r.Pass).Select(r => r.Address).ToList();
        if (failedNodes.Count == 0) { AddLog("No failed nodes to retest"); return; }
        // Remove old results for these nodes
        _testResults.RemoveAll(r => failedNodes.Contains(r.Address));
        // Re-run test on failed nodes only
        _testRunning = true;
        _testRetestMode = true;
        _testCts = new CancellationTokenSource();
        _testStopRequested = false;
        _testTotal = failedNodes.Count;
        _testDone = 0;
        _testStartTime = DateTime.UtcNow;
        AddLog($"[TEST] Retesting {failedNodes.Count} failed nodes...");
        RenderTestDashboard();

        var backend = (NativeVpnClient)App.Backend;
        foreach (var addr in failedNodes)
        {
            if (_testStopRequested || _testCts.IsCancellationRequested) break;
            var nodeInfo = _allNodes.FirstOrDefault(n => n.Address == addr);
            try
            {
                var result = await backend.TestNodeAsync(addr, nodeInfo, _testCts.Token);
                _testResults.Insert(0, result);
                _testDone++;
                Dispatcher.Invoke(() => { RenderTestStats(); RenderTestProgress(); RenderTestTable(); });
                if (_testDone % 5 == 0) DiskCache.Save("test-results", _testResults);
            }
            catch (OperationCanceledException) { break; }
            catch { _testDone++; }
        }
        DiskCache.Save("test-results", _testResults);
        SaveRunArchive("Retest Failed");
        _testRunning = false;
        _testRetestMode = false;
        Dispatcher.Invoke(RenderTestDashboard);
    }

    // ─── Export + Filter/Sort Helpers ───

    private void ExportTestResults()
    {
        var source = _viewingHistoryRun != null && _historyResults != null ? _historyResults : _testResults;
        if (source.Count == 0) { AddLog("No results to export"); return; }
        var dlg = new Microsoft.Win32.SaveFileDialog
        {
            FileName = _viewingHistoryRun != null ? $"history-{_viewingHistoryRun.FolderName}" : $"test-results-{DateTime.Now:yyyy-MM-dd}",
            DefaultExt = ".json",
            Filter = "JSON|*.json|CSV|*.csv",
        };
        if (dlg.ShowDialog() != true) return;
        try
        {
            if (dlg.FileName.EndsWith(".csv", StringComparison.OrdinalIgnoreCase))
            {
                var lines = new List<string> { "Address,Moniker,Country,City,Protocol,Transport,Peers,SpeedMbps,GoogleOK,GoogleMs,Pass,Error,Timestamp" };
                foreach (var r in source)
                    lines.Add($"\"{r.Address}\",\"{r.Moniker}\",\"{r.Country}\",\"{r.City}\",\"{r.Protocol}\",\"{r.Transport}\",{r.Peers},{r.SpeedMbps:F1},{r.GoogleAccessible},{r.GoogleLatencyMs},{r.Pass},\"{r.Error}\",{r.Timestamp:o}");
                File.WriteAllLines(dlg.FileName, lines);
            }
            else
            {
                var json = JsonSerializer.Serialize(source, new JsonSerializerOptions { WriteIndented = true });
                File.WriteAllText(dlg.FileName, json);
            }
            AddLog($"Exported {source.Count} results to {Path.GetFileName(dlg.FileName)}");
        }
        catch (Exception ex) { AddLog($"Export failed: {ex.Message}", true); }
    }

    private List<NodeTestResult> GetFilteredSortedResults(List<NodeTestResult>? source = null)
    {
        source ??= _testResults;
        IEnumerable<NodeTestResult> filtered = _testFilter switch
        {
            "wg" => source.Where(r => r.Transport == "WG" || r.Protocol?.Contains("wireguard", StringComparison.OrdinalIgnoreCase) == true),
            "v2" => source.Where(r => r.Transport?.StartsWith("V2") == true || r.Protocol?.Contains("v2ray", StringComparison.OrdinalIgnoreCase) == true),
            "pass" => source.Where(r => r.Pass),
            "fail" => source.Where(r => !r.Pass),
            _ => source,
        };

        if (!string.IsNullOrEmpty(_testSortCol))
        {
            filtered = _testSortCol switch
            {
                "speed" => _testSortAsc ? filtered.OrderBy(r => r.SpeedMbps ?? 0) : filtered.OrderByDescending(r => r.SpeedMbps ?? 0),
                "peers" => _testSortAsc ? filtered.OrderBy(r => r.Peers ?? 0) : filtered.OrderByDescending(r => r.Peers ?? 0),
                "country" => _testSortAsc ? filtered.OrderBy(r => r.Country ?? "") : filtered.OrderByDescending(r => r.Country ?? ""),
                "result" => _testSortAsc ? filtered.OrderBy(r => r.Pass) : filtered.OrderByDescending(r => r.Pass),
                "bw" => _testSortAsc ? filtered.OrderBy(r => r.ReportedBandwidth ?? 0) : filtered.OrderByDescending(r => r.ReportedBandwidth ?? 0),
                _ => filtered,
            };
        }

        return filtered.ToList();
    }

    private int _testRunNumber;

    private async Task StartBatchTestAsync()
    {
        if (_testRunning) return;
        _testRunning = true;
        _testCts = new CancellationTokenSource();
        _testStopRequested = false;
        _testRetestMode = false;
        _testStartTime = DateTime.UtcNow;
        _testRunNumber++;

        var backend = (NativeVpnClient)App.Backend;

        // Baseline measurement before scan
        try
        {
            AddLog($"[TEST] Run #{_testRunNumber} — measuring baseline...");
            await backend.MeasureBaselineAsync();
        }
        catch (Exception ex) { AddLog($"[TEST] Baseline failed: {ex.Message}"); }

        // Build plan node set for InPlan marking
        var planNodeAddresses = new HashSet<string>();
        try
        {
            var subscribedPlans = _plans.Where(p => p.IsSubscribed).ToList();
            if (subscribedPlans.Count > 0)
            {
                var chain = backend.GetChain();
                if (chain != null)
                {
                    foreach (var plan in subscribedPlans)
                    {
                        try
                        {
                            var nodes = await chain.GetPlanNodesAsync(plan.Id);
                            foreach (var n in nodes) planNodeAddresses.Add(n.Address);
                        }
                        catch { }
                    }
                    if (planNodeAddresses.Count > 0)
                        AddLog($"[TEST] {planNodeAddresses.Count} nodes in subscribed plans");
                }
            }
        }
        catch { }

        var nodesToTest = _allNodes.Where(n => n.Moniker != null).ToList();
        _testTotal = nodesToTest.Count;
        _testDone = 0;
        _testPassed = _testResults.Count(r => r.Pass);
        _testFailed = _testResults.Count(r => !r.Pass);

        RenderTestDashboard();

        foreach (var node in nodesToTest)
        {
            if (_testStopRequested || _testCts.IsCancellationRequested) break;

            // Skip already tested nodes (in this session)
            if (_testResults.Any(r => r.Address == node.Address)) { _testDone++; continue; }

            Dispatcher.Invoke(() =>
            {
                if (_testStatusTb != null) _testStatusTb.Text = $"Testing {node.Moniker ?? Trunc(node.Address, 16, 0)}...";
                if (_testProgressTb != null) _testProgressTb.Text = $"{_testDone}/{_testTotal}";
                AddLog($"[TEST] Testing {node.Moniker ?? Trunc(node.Address, 16, 0)}...");
            });

            try
            {
                var result = await backend.TestNodeAsync(node.Address, node, _testCts.Token);
                result.InPlan = planNodeAddresses.Contains(node.Address);
                _testResults.Insert(0, result);
                _testDone++;
                if (result.Pass) _testPassed++; else _testFailed++;

                Dispatcher.Invoke(() =>
                {
                    try { RenderTestStats(); RenderTestProgress(); RenderTestTable(); } catch (Exception rx) { AddLog($"[TEST] Render: {rx.Message}"); }
                });

                // Save every 5 results
                if (_testDone % 5 == 0) DiskCache.Save("test-results", _testResults);
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                _testDone++;
                _testFailed++;
                AddLog($"[TEST] Error on {Trunc(node.Address, 16, 0)}: {ex.Message}");
            }
        }

        DiskCache.Save("test-results", _testResults);

        // Auto-save run archive with timestamp
        SaveRunArchive($"Run #{_testRunNumber}");

        _testRunning = false;
        _testRetestMode = false;
        Dispatcher.Invoke(() =>
        {
            _testRunning = false;
            RenderTestDashboard();
        });
    }

    // ─── Run History Methods ───

    private void SaveRunArchive(string label)
    {
        try
        {
            var ts = DateTime.Now.ToString("yyyy-MM-dd_HH-mm");
            var runDir = Path.Combine(TestRunSummary.RunsDir, ts);
            if (!Directory.Exists(runDir)) Directory.CreateDirectory(runDir);
            File.WriteAllText(Path.Combine(runDir, "results.json"),
                JsonSerializer.Serialize(_testResults, new JsonSerializerOptions { WriteIndented = true }));
            File.WriteAllText(Path.Combine(runDir, "summary.json"),
                JsonSerializer.Serialize(new
                {
                    run = _testRunNumber,
                    label,
                    total = _testResults.Count,
                    passed = _testResults.Count(r => r.Pass),
                    failed = _testResults.Count(r => !r.Pass),
                    fast = _testResults.Count(r => (r.SpeedMbps ?? 0) >= 10),
                    avgSpeed = _testResults.Where(r => r.SpeedMbps > 0).Select(r => r.SpeedMbps!.Value).DefaultIfEmpty(0).Average(),
                    baseline = ((NativeVpnClient)App.Backend).LastBaseline,
                    startTime = _testStartTime.ToString("o"),
                    endTime = DateTime.UtcNow.ToString("o"),
                    duration = (DateTime.UtcNow - _testStartTime).ToString(@"hh\:mm\:ss"),
                }));
            AddLog($"[TEST] Run saved: {ts} ({label})");
            RefreshRunHistory();
        }
        catch (Exception ex) { AddLog($"[TEST] Run archive failed: {ex.Message}"); }
    }

    private void RefreshRunHistory()
    {
        _availableRuns = TestRunSummary.ScanAll();
    }

    private void LoadHistoricalRun(TestRunSummary run)
    {
        var results = TestRunSummary.LoadResults(run.FolderPath);
        if (results == null || results.Count == 0)
        {
            AddLog($"[HISTORY] No results in {run.FolderName}");
            return;
        }
        _viewingHistoryRun = run;
        _historyResults = results;
        AddLog($"[HISTORY] Loaded {run.FolderName} — {results.Count} results");
        RenderTestDashboard();
    }

    private void ExitHistoryView()
    {
        _viewingHistoryRun = null;
        _historyResults = null;
        RenderTestDashboard();
    }

    private Border MakeTestResultRow(NodeTestResult r)
    {
        var row = new Border
        {
            Padding = new Thickness(10, 8, 10, 8),
            CornerRadius = new CornerRadius(6),
            Margin = new Thickness(4, 2, 4, 2),
            Background = FindBrush("Bg0"),
            BorderBrush = FindBrush(r.Pass ? "Green" : "Red"),
            BorderThickness = new Thickness(1),
        };

        var outer = new StackPanel();

        // Row 1: name + verdict
        var r1 = new Grid();
        r1.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        r1.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var namePanel = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        var code = CountryCode(r.Country ?? "");
        if (code != "??" && _flagCache.TryGetValue(code, out var bmp) && bmp != null)
            namePanel.Children.Add(new Image { Width = 16, Height = 11, Stretch = System.Windows.Media.Stretch.Uniform, Source = bmp, Margin = new Thickness(0, 0, 6, 0), VerticalAlignment = VerticalAlignment.Center });
        namePanel.Children.Add(MakeText(r.Moniker ?? Trunc(r.Address, 16, 0), 11, "T1", fontWeight: FontWeights.SemiBold));
        Grid.SetColumn(namePanel, 0);

        string vLabel; string vColor;
        if (!r.Pass) { vLabel = "FAIL"; vColor = "Red"; }
        else if ((r.SpeedMbps ?? 0) >= 10) { vLabel = "FAST"; vColor = "Green"; }
        else { vLabel = "SLOW"; vColor = "Amber"; }
        var verdict = MakeText(vLabel, 10, vColor, fontWeight: FontWeights.Bold, fontFamily: "Mono");
        verdict.VerticalAlignment = VerticalAlignment.Center;
        Grid.SetColumn(verdict, 1);

        r1.Children.Add(namePanel);
        r1.Children.Add(verdict);
        outer.Children.Add(r1);

        // Row 2: details
        var details = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 4, 0, 0) };
        details.Children.Add(MakeText(r.Protocol?.ToUpperInvariant() ?? "?", 9, "T2", margin: new Thickness(0, 0, 8, 0)));
        if (r.SpeedMbps > 0)
            details.Children.Add(MakeText($"{r.SpeedMbps:F1} Mbps", 9, "T1", fontWeight: FontWeights.Medium, margin: new Thickness(0, 0, 8, 0)));
        if (r.GoogleAccessible == true)
            details.Children.Add(MakeText($"Google {r.GoogleLatencyMs}ms", 9, "Green", margin: new Thickness(0, 0, 8, 0)));
        else if (r.GoogleAccessible == false)
            details.Children.Add(MakeText("Google blocked", 9, "Red", margin: new Thickness(0, 0, 8, 0)));
        details.Children.Add(MakeText($"{r.ConnectSeconds:F0}s", 9, "T3"));
        outer.Children.Add(details);

        // Row 3: error if failed
        if (!r.Pass && r.Error != null)
            outer.Children.Add(MakeText(r.Error, 9, "Red", margin: new Thickness(0, 4, 0, 0)));

        row.Child = outer;
        return row;
    }

    // ═══ CONNECTION ═══

    private async void Orb_Click(object sender, MouseButtonEventArgs e) => await ToggleConnect();
    private async void BtnConnect_Click(object sender, RoutedEventArgs e) => await ToggleConnect();

    private async Task ToggleConnect()
    {
        // Cancel if connecting
        if (_connState == "ing")
        {
            _connectCts?.Cancel();
            AddLog("Connection cancelled");
            SetState("off");
            StatusSub.Text = "Cancelled";
            try { await App.Backend.DisconnectAsync(); } catch { }
            return;
        }
        if (_connState == "on") { await DoDisconnect(); return; }

        _connState = "ing";
        if (_selectedNode == null)
        {
            _connState = "off";
            StatusSub.Text = "Select a node first";
            return;
        }
        // Cancel background refresh to free up chain client for connection
        _refreshCts?.Cancel();
        _connectCts = new CancellationTokenSource();
        await DoConnect(_selectedNode);
    }

    private async Task DoConnect(string nodeAddr)
    {
        _statusPoll.Stop();
        _ipPoll.Stop();
        _balPoll.Stop();

        SetState("ing");
        AddLog($"Connecting to {Trunc(nodeAddr, 12, 6)}...");

        // Pre-connect liveness check — verify node is still online before paying
        try
        {
            var node = _allNodes.FirstOrDefault(n => n.Address == nodeAddr);
            if (node != null)
            {
                StatusSub.Text = "Verifying node is online...";
                var url = node.Address; // SDK resolves this
                AddLog("Checking node status...");
            }
        }
        catch { /* liveness check failed — try anyway */ }

        try
        {
            var amount = int.TryParse(TbAmount.Text.Trim(), out var a) && a > 0 ? a : 1;
            var preferHourly = _payMode == "hr";
            AddLog($"Payment: {amount} {(preferHourly ? "hour(s)" : "GB")}");
            var r = await App.Backend.ConnectDirectAsync(nodeAddr, amount, preferHourly);
            if (_connState != "ing") return; // cancelled while connecting
            if (r != null && !string.IsNullOrEmpty(r.NodeAddress))
            {
                SetState("on");
                StatProto.Text = r.Protocol?.ToUpperInvariant() ?? "WG";
                StatSession.Text = Trunc(r.SessionId ?? "\u2014", 8, 0);
                StatIp.Text = r.VpnIp ?? "checking...";
                StatusSub.Text = r.NodeAddress != null ? Trunc(r.NodeAddress, 16, 6) : "Connected";
                AddLog($"Connected via {r.Protocol?.ToUpperInvariant() ?? "WG"} with Handshake DNS");
                if (r.SessionId != null)
                {
                    _lastSessionId = r.SessionId;
                    SessionTracker.Track(r.SessionId, _payMode);
                    _ = UpdateAllocationAsync(r.SessionId);
                }
                return;
            }
            SetState("off");
            StatusSub.Text = "Connection failed \u2014 try again";
            AddLog("No response", true);
        }
        catch (Exception ex)
        {
            AddLog($"Error: {ex.Message}", true);

            for (int i = 0; i < 3; i++)
            {
                await Task.Delay(2000);
                try
                {
                    var s = await App.Backend.GetStatusAsync();
                    if (s is { Connected: true })
                    {
                        SetState("on");
                        StatProto.Text = s.Protocol?.ToUpperInvariant() ?? "\u2014";
                        StatSession.Text = Trunc(s.SessionId ?? "\u2014", 8, 0);
                        if (s.VpnIp != null) StatIp.Text = s.VpnIp;
                        StatusSub.Text = s.NodeAddress != null ? Trunc(s.NodeAddress, 16, 6) : "Connected";
                        AddLog("Connected with Handshake DNS");
                        return;
                    }
                }
                catch { /* status check failed */ }
            }

            SetState("off");
            StatusSub.Text = "Connection failed \u2014 try again";
        }
        finally
        {
            _statusPoll.Start();
            _ipPoll.Start();
            _balFailCount = 0;
            _balPoll.Interval = TimeSpan.FromMinutes(5);
            _balPoll.Start();
        }
    }

    private async Task DoDisconnect()
    {
        SetState("ing");
        AddLog("Disconnecting...");
        try
        {
            await App.Backend.DisconnectAsync();
            SetState("off");
            ClearStats();
            AddLog("Disconnected — session still active");

            // Add session to local cache — only for direct P2P sessions, NOT plan connections
            var mode = SessionTracker.GetMode(_lastSessionId ?? "");
            if (_lastSessionId != null && _selectedNode != null && mode != "plan")
            {
                var session = new ActiveSession
                {
                    SessionId = _lastSessionId,
                    NodeAddress = _selectedNode,
                    PayMode = _payMode,
                    Status = "active",
                    MaxBytes = 1000000000,
                };
                if (!_activeSessions.Any(s => s.SessionId == _lastSessionId))
                    _activeSessions.Insert(0, session);
                DiskCache.Save("sessions", _activeSessions);
            }

            // Show session bar instantly
            if (_selectedNodeInfo != null) CheckExistingSessionForNode(_selectedNodeInfo.Address);
        }
        catch (Exception ex)
        {
            SetState("off");
            AddLog($"Disconnect error: {ex.Message}", true);
        }
    }

    private void SetState(string state)
    {
        _connState = state;
        var accColor = Color.FromRgb(0x00, 0x00, 0x00);
        var bdrColor = Color.FromRgb(0xE0, 0xE0, 0xE0);
        var t3Color = Color.FromRgb(0x99, 0x99, 0x99);
        var greenColor = Color.FromRgb(0x22, 0xC5, 0x5E);

        switch (state)
        {
            case "off":
                OrbBorderBrush.Color = bdrColor;
                Ring1Brush.Color = bdrColor;
                Ring2Brush.Color = bdrColor;
                OrbLogoPath.Fill = new SolidColorBrush(t3Color);
                OrbBorder.Background = FindBrush("Bg2");
                StatusTxt.Text = "DISCONNECTED";
                StatusTxt.Foreground = FindBrush("T3");
                StatusSub.Foreground = FindBrush("T3");
                BtnConnectText.Text = "Connect";
                BtnConnect.IsEnabled = true;
                BtnConnect.Background = FindBrush("Acc");
                BtnConnect.Foreground = Brushes.White;
                MainBg.Opacity = 0.03;
                break;
            case "ing":
                OrbBorderBrush.Color = accColor;
                Ring1Brush.Color = accColor;
                Ring2Brush.Color = accColor;
                OrbLogoPath.Fill = new SolidColorBrush(accColor);
                StatusTxt.Text = "CONNECTING...";
                StatusTxt.Foreground = FindBrush("Acc");
                BtnConnectText.Text = "Cancel";
                BtnConnect.IsEnabled = true;
                BtnConnect.Background = Brushes.White;
                BtnConnect.Foreground = FindBrush("T1");
                MainBg.Opacity = 0.06;
                break;
            case "on":
                OrbBorderBrush.Color = accColor;
                Ring1Brush.Color = accColor;
                Ring2Brush.Color = accColor;
                OrbLogoPath.Fill = new SolidColorBrush(accColor);
                OrbBorder.Background = FindBrush("AccLight");
                StatusTxt.Text = "PROTECTED";
                StatusTxt.Foreground = FindBrush("Green");
                BtnConnectText.Text = "Disconnect";
                BtnConnect.IsEnabled = true;
                BtnConnect.Background = Brushes.White;
                BtnConnect.Foreground = FindBrush("T1");
                MainBg.Opacity = 0.08;
                break;
        }
    }

    private void ClearStats()
    {
        StatIp.Text = "\u2014"; StatUptime.Text = "\u2014"; StatProto.Text = "\u2014"; StatSession.Text = "\u2014";
        AllocationBar.Visibility = Visibility.Collapsed;
    }

    // ═══ POLLING ═══

    private async Task PollStatus()
    {
        try
        {
            var s = await App.Backend.GetStatusAsync();
            if (s is { Connected: true })
            {
                if (_connState != "on") SetState("on");
                StatUptime.Text = s.UptimeFormatted ?? "\u2014";
                StatProto.Text = s.Protocol?.ToUpperInvariant() ?? "\u2014";
                if (s.VpnIp != null) StatIp.Text = s.VpnIp;

                // Update allocation every 30s (not every poll)
                if (s.SessionId != null && (DateTime.UtcNow - _lastAllocCheck).TotalSeconds > 120)
                {
                    _lastAllocCheck = DateTime.UtcNow;
                    _ = UpdateAllocationAsync(s.SessionId);
                }
            }
            else if (_connState == "on" && !_isClosing)
            {
                SetState("off");
                ClearStats();
                AddLog("Connection dropped", true);
            }
        }
        catch { /* busy */ }
    }

    private async Task PollIp()
    {
        if (_connState != "on") return;
        var ip = await App.Backend.GetPublicIpAsync();
        if (ip != null) StatIp.Text = ip;
    }

    private async Task UpdateAllocationAsync(string sessionId)
    {
        try
        {
            if (!ulong.TryParse(sessionId, out var sid)) return;
            var backend = (NativeVpnClient)App.Backend;
            var alloc = await backend.QueryAllocationAsync(sid);
            if (alloc == null) return;

            var maxBytes = alloc.Value.max;
            var usedBytes = alloc.Value.used;
            var isGb = maxBytes > 0;

            Dispatcher.Invoke(() =>
            {
                AllocationBar.Visibility = Visibility.Visible;
                if (isGb)
                {
                    var remaining = Math.Max(0, maxBytes - usedBytes);
                    var pct = maxBytes > 0 ? (double)usedBytes / maxBytes : 0;
                    AllocLabel.Text = "DATA REMAINING";
                    AllocValue.Text = $"{FormatBytesStatic(remaining)} / {FormatBytesStatic(maxBytes)}  ({(int)(pct * 100)}% used)";
                    AllocUsedCol.Width = new GridLength(Math.Max(0.01, pct), GridUnitType.Star);
                    AllocRemainCol.Width = new GridLength(Math.Max(0.01, 1 - pct), GridUnitType.Star);
                }
                else
                {
                    AllocLabel.Text = "SESSION DATA USED";
                    AllocValue.Text = FormatBytesStatic(usedBytes);
                    AllocUsedCol.Width = new GridLength(0.1, GridUnitType.Star);
                    AllocRemainCol.Width = new GridLength(0.9, GridUnitType.Star);
                }
            });
        }
        catch { /* allocation query failed */ }
    }

    private static double ParsePrice(string? s)
    {
        if (string.IsNullOrEmpty(s)) return double.MaxValue;
        if (double.TryParse(s, System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out var v)) return v;
        return double.MaxValue;
    }

    private static string FormatBytesStatic(long b)
    {
        if (b >= 1_073_741_824) return $"{b / 1_073_741_824.0:F2} GB";
        if (b >= 1_048_576) return $"{b / 1_048_576.0:F1} MB";
        if (b >= 1024) return $"{b / 1024.0:F0} KB";
        return $"{b} B";
    }

    private async Task CheckExisting()
    {
        try
        {
            var s = await App.Backend.GetStatusAsync();
            if (s is { Connected: true })
            {
                SetState("on");
                StatProto.Text = s.Protocol?.ToUpperInvariant() ?? "\u2014";
                StatSession.Text = Trunc(s.SessionId ?? "\u2014", 8, 0);
                StatUptime.Text = s.UptimeFormatted ?? "\u2014";
                if (s.VpnIp != null) StatIp.Text = s.VpnIp;
                if (s.NodeAddress != null)
                {
                    _selectedNode = s.NodeAddress;
                    StatusSub.Text = Trunc(s.NodeAddress, 16, 6);
                }
                AddLog("Restored existing connection");
            }
        }
        catch { /* no existing connection */ }
    }

    // ═══ APP LIFECYCLE ═══

    private void Window_Closing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        _isClosing = true;
        _testCts?.Cancel();
        _statusPoll.Stop();
        _ipPoll.Stop();
        _balPoll.Stop();
        if (_connState == "on" || _connState == "ing")
        {
            try
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                App.Backend.DisconnectAsync().Wait(cts.Token);
            }
            catch { /* best effort */ }
        }
    }

    private async void BtnLogout_Click(object sender, RoutedEventArgs e)
    {
        if (MessageBox.Show("Disconnect and logout?", "Logout", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        _statusPoll.Stop(); _ipPoll.Stop(); _balPoll.Stop();
        try { await App.Backend.DisconnectAsync(); } catch { }
        ClearUser();
        _userAddr = null; _selectedNode = null; _connState = "off";
        _allNodes.Clear(); NodeListPanel.Children.Clear();
        ClearStats(); SetState("off");
        NodeInfoCard.Visibility = Visibility.Collapsed;
        TbMnemonic.Text = ""; AuthErr.Text = "";
        AuthOverlay.Visibility = Visibility.Visible;
    }

    // ═══ LOGGING ═══

    private readonly List<string> _logLines = new();

    private void BtnCopyLogs_Click(object sender, RoutedEventArgs e)
    {
        if (_logLines.Count == 0) return;
        Clipboard.SetText(string.Join("\n", _logLines));
    }

    private void TbAddr_Click(object sender, MouseButtonEventArgs e)
    {
        if (_userAddr != null)
        {
            Clipboard.SetText(_userAddr);
            AddLog($"Copied: {_userAddr}");
        }
    }

    private static readonly string _logFile = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "HandshakeDVPN", "app.log");

    private void AddLog(string msg, bool err = false)
    {
        var ts = DateTime.Now.ToString("HH:mm:ss");
        try
        {
            var dir = Path.GetDirectoryName(_logFile)!;
            if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
            File.AppendAllText(_logFile, $"[{ts}]{(err ? " ERROR" : "")} {msg}\n");
        }
        catch { }

        _logLines.Add($"[{ts}]{(err ? " ERROR" : "")} {msg}");

        var line = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 1, 0, 1) };
        line.Children.Add(MakeText($"[{ts}]", 10, "Acc", fontFamily: "Mono"));
        line.Children.Add(MakeText($" {msg}", 10, err ? "Red" : "T2", fontFamily: "Mono"));
        LogPanel.Children.Add(line);
        if (LogPanel.Children.Count > 20) LogPanel.Children.RemoveAt(0);
        LogScroll.ScrollToEnd();

        // Also add to test log panel if it's a test message
        if (msg.Contains("[TEST]") && TestLogPanel != null)
        {
            var testLine = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 1, 0, 1) };
            testLine.Children.Add(MakeText($"[{ts}]", 9, "T3", fontFamily: "Mono"));
            testLine.Children.Add(MakeText($" {msg.Replace("[TEST] ", "")}", 9, err ? "Red" : "T1", fontFamily: "Mono"));
            TestLogPanel.Children.Add(testLine);
            if (TestLogPanel.Children.Count > 100) TestLogPanel.Children.RemoveAt(0);
            TestLogScroll.ScrollToEnd();
        }
    }

    // ═══ PERSISTENCE ═══

    private static void SaveUser(string addr, string mn)
    {
        var dir = Path.GetDirectoryName(UserFile)!;
        if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
        File.WriteAllText(UserFile, JsonSerializer.Serialize(new { address = addr, mnemonic = mn }));
    }

    private static (string?, string?) LoadUser()
    {
        try
        {
            if (!File.Exists(UserFile)) return (null, null);
            using var doc = JsonDocument.Parse(File.ReadAllText(UserFile));
            return (doc.RootElement.GetProperty("address").GetString(), doc.RootElement.GetProperty("mnemonic").GetString());
        }
        catch { return (null, null); }
    }

    private static void ClearUser() { try { if (File.Exists(UserFile)) File.Delete(UserFile); } catch { } }

    // ═══ UI HELPERS ═══

    private static TextBlock MakeText(string text, double size, string brushKey,
        HorizontalAlignment halign = HorizontalAlignment.Left, Thickness? margin = null,
        FontWeight? fontWeight = null, string? fontFamily = null)
    {
        var tb = new TextBlock
        {
            Text = text,
            FontSize = size,
            HorizontalAlignment = halign,
        };
        if (fontWeight.HasValue) tb.FontWeight = fontWeight.Value;
        if (margin.HasValue) tb.Margin = margin.Value;
        if (fontFamily == "Mono")
            tb.FontFamily = (FontFamily)Application.Current.FindResource("Mono");
        try { tb.Foreground = (Brush)Application.Current.FindResource(brushKey); } catch { }
        return tb;
    }

    private static StackPanel MakePanel(Orientation orient, double gap, params UIElement[] children)
    {
        var sp = new StackPanel { Orientation = orient };
        foreach (var c in children)
        {
            if (c is FrameworkElement fe && orient == Orientation.Horizontal)
                fe.Margin = new Thickness(0, 0, gap, 0);
            sp.Children.Add(c);
        }
        return sp;
    }

    private static Brush FindBrush(string key)
    {
        try { return (Brush)Application.Current.FindResource(key); }
        catch { return Brushes.Gray; }
    }

    private static string Trunc(string s, int pre, int suf)
    {
        if (string.IsNullOrEmpty(s)) return "\u2014";
        if (suf == 0) return s.Length <= pre ? s : s[..pre] + "...";
        if (s.Length <= pre + suf + 3) return s;
        return $"{s[..pre]}...{s[^suf..]}";
    }

    // ─── Flag Images ───
    // WPF cannot render emoji flags (Windows excludes them from Segoe UI Emoji).
    // Load real flag PNGs from flagcdn.com, cache to %LocalAppData%/HandshakeDVPN/flags/

    private static readonly Dictionary<string, BitmapImage?> _flagCache = new();
    private static readonly string _flagDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "HandshakeDVPN", "flags");

    private static FrameworkElement MakeFlagImage(string code)
    {
        if (code == "??")
            return MakeText("\u2014", 12, "T3");

        var img = new Image
        {
            Width = 24,
            Height = 16,
            Stretch = System.Windows.Media.Stretch.Uniform,
            VerticalAlignment = VerticalAlignment.Center,
        };

        var border = new Border
        {
            Width = 26,
            Height = 18,
            CornerRadius = new CornerRadius(2),
            ClipToBounds = true,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 2, 0),
            Background = FindBrush("Bg3"),
            Child = img,
        };

        // Try memory cache
        if (_flagCache.TryGetValue(code, out var cached) && cached != null)
        {
            img.Source = cached;
            border.Background = Brushes.Transparent;
            return border;
        }

        // Try disk cache
        var diskPath = Path.Combine(_flagDir, $"{code.ToLower()}.png");
        if (File.Exists(diskPath))
        {
            var bmp = LoadFlagFromDisk(diskPath);
            if (bmp != null)
            {
                _flagCache[code] = bmp;
                img.Source = bmp;
                border.Background = Brushes.Transparent;
                return border;
            }
        }

        // Download in background — gray placeholder shows until loaded
        _ = LoadFlagAsync(code, img, border);
        return border;
    }

    private static readonly SemaphoreSlim _flagSem = new(10);

    private static async Task LoadFlagAsync(string code, Image img, Border border)
    {
        await _flagSem.WaitAsync();
        try
        {
            if (!Directory.Exists(_flagDir)) Directory.CreateDirectory(_flagDir);
            var url = $"https://flagcdn.com/w40/{code.ToLower()}.png";
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
            byte[]? bytes = null;

            // Try twice
            for (int attempt = 0; attempt < 2; attempt++)
            {
                try { bytes = await http.GetByteArrayAsync(url); break; }
                catch { if (attempt == 0) await Task.Delay(500); }
            }
            if (bytes == null || bytes.Length < 100) return;

            var diskPath = Path.Combine(_flagDir, $"{code.ToLower()}.png");
            await File.WriteAllBytesAsync(diskPath, bytes);

            var bmp = new BitmapImage();
            bmp.BeginInit();
            bmp.StreamSource = new MemoryStream(bytes);
            bmp.CacheOption = BitmapCacheOption.OnLoad;
            bmp.DecodePixelWidth = 40;
            bmp.EndInit();
            bmp.Freeze();
            _flagCache[code] = bmp;

            img.Dispatcher.Invoke(() =>
            {
                img.Source = bmp;
                border.Background = Brushes.Transparent;
            });
        }
        catch { /* flag download failed */ }
        finally { _flagSem.Release(); }
    }

    private static BitmapImage? LoadFlagFromDisk(string path)
    {
        try
        {
            var bytes = File.ReadAllBytes(path);
            var bmp = new BitmapImage();
            bmp.BeginInit();
            bmp.StreamSource = new MemoryStream(bytes);
            bmp.CacheOption = BitmapCacheOption.OnLoad;
            bmp.DecodePixelWidth = 40;
            bmp.EndInit();
            bmp.Freeze();
            return bmp;
        }
        catch { return null; }
    }

    private static string CountryCode(string country)
    {
        // Comprehensive map matching TEST2's flagMap — covers all Sentinel network countries + variants
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            // North America
            ["united states"] = "US", ["us"] = "US", ["usa"] = "US",
            ["canada"] = "CA", ["ca"] = "CA",
            ["mexico"] = "MX",
            ["puerto rico"] = "PR",
            ["dominican republic"] = "DO", ["jamaica"] = "JM",
            ["guatemala"] = "GT", ["honduras"] = "HN", ["el salvador"] = "SV",
            ["nicaragua"] = "NI", ["costa rica"] = "CR", ["panama"] = "PA",
            ["cuba"] = "CU", ["trinidad and tobago"] = "TT", ["bahamas"] = "BS", ["barbados"] = "BB",
            // Europe — Western
            ["germany"] = "DE", ["de"] = "DE",
            ["france"] = "FR", ["fr"] = "FR",
            ["united kingdom"] = "GB", ["uk"] = "GB", ["gb"] = "GB",
            ["netherlands"] = "NL", ["the netherlands"] = "NL", ["nl"] = "NL", ["holland"] = "NL",
            ["belgium"] = "BE", ["luxembourg"] = "LU",
            ["switzerland"] = "CH", ["austria"] = "AT",
            ["ireland"] = "IE",
            ["liechtenstein"] = "LI", ["monaco"] = "MC", ["andorra"] = "AD", ["san marino"] = "SM",
            // Europe — Northern
            ["sweden"] = "SE", ["norway"] = "NO", ["finland"] = "FI",
            ["denmark"] = "DK", ["iceland"] = "IS",
            ["estonia"] = "EE", ["latvia"] = "LV", ["lithuania"] = "LT",
            // Europe — Southern
            ["spain"] = "ES", ["italy"] = "IT", ["portugal"] = "PT",
            ["greece"] = "GR", ["malta"] = "MT", ["cyprus"] = "CY", ["croatia"] = "HR",
            ["slovenia"] = "SI", ["albania"] = "AL",
            ["north macedonia"] = "MK", ["macedonia"] = "MK",
            ["montenegro"] = "ME", ["kosovo"] = "XK",
            ["bosnia and herzegovina"] = "BA", ["bosnia"] = "BA",
            // Europe — Eastern
            ["poland"] = "PL", ["romania"] = "RO",
            ["czech republic"] = "CZ", ["czechia"] = "CZ", ["cz"] = "CZ",
            ["hungary"] = "HU", ["bulgaria"] = "BG",
            ["slovakia"] = "SK", ["serbia"] = "RS",
            ["ukraine"] = "UA", ["moldova"] = "MD", ["belarus"] = "BY",
            ["russia"] = "RU", ["russian federation"] = "RU",
            // Europe — Caucasus
            ["georgia"] = "GE", ["armenia"] = "AM", ["azerbaijan"] = "AZ",
            // Turkey
            ["turkey"] = "TR", ["\u00fcrkiye"] = "TR", ["t\u00fcrkiye"] = "TR",
            // East Asia
            ["china"] = "CN",
            ["japan"] = "JP", ["south korea"] = "KR", ["korea"] = "KR",
            ["taiwan"] = "TW", ["hong kong"] = "HK", ["mongolia"] = "MN",
            // Southeast Asia
            ["singapore"] = "SG", ["thailand"] = "TH",
            ["vietnam"] = "VN", ["viet nam"] = "VN",
            ["indonesia"] = "ID", ["malaysia"] = "MY",
            ["philippines"] = "PH", ["cambodia"] = "KH", ["myanmar"] = "MM",
            // South Asia
            ["india"] = "IN", ["pakistan"] = "PK", ["bangladesh"] = "BD",
            ["nepal"] = "NP", ["sri lanka"] = "LK",
            // Central Asia
            ["kazakhstan"] = "KZ", ["uzbekistan"] = "UZ", ["kyrgyzstan"] = "KG",
            // Middle East
            ["israel"] = "IL", ["uae"] = "AE", ["united arab emirates"] = "AE",
            ["saudi arabia"] = "SA", ["qatar"] = "QA", ["kuwait"] = "KW",
            ["bahrain"] = "BH", ["oman"] = "OM",
            ["jordan"] = "JO", ["lebanon"] = "LB", ["iraq"] = "IQ", ["iran"] = "IR",
            // Oceania
            ["australia"] = "AU", ["new zealand"] = "NZ",
            // South America
            ["brazil"] = "BR", ["argentina"] = "AR", ["chile"] = "CL",
            ["colombia"] = "CO", ["peru"] = "PE", ["venezuela"] = "VE",
            ["ecuador"] = "EC", ["uruguay"] = "UY", ["paraguay"] = "PY", ["bolivia"] = "BO",
            // Africa
            ["south africa"] = "ZA", ["nigeria"] = "NG", ["kenya"] = "KE",
            ["egypt"] = "EG", ["morocco"] = "MA", ["tunisia"] = "TN",
            ["ghana"] = "GH", ["ethiopia"] = "ET", ["tanzania"] = "TZ",
            ["uganda"] = "UG", ["angola"] = "AO", ["mozambique"] = "MZ",
            ["zimbabwe"] = "ZW", ["botswana"] = "BW", ["namibia"] = "NA",
            ["senegal"] = "SN", ["cameroon"] = "CM", ["madagascar"] = "MG",
            ["ivory coast"] = "CI", ["cote d'ivoire"] = "CI",
            ["mauritius"] = "MU", ["seychelles"] = "SC",
            ["dr congo"] = "CD", ["democratic republic of the congo"] = "CD", ["congo"] = "CG",
        };
        // Exact match
        if (map.TryGetValue(country, out var code)) return code;

        // Fuzzy: try partial match (handles "Republic of Korea", "Viet Nam", etc.)
        var lower = country.ToLowerInvariant().Trim();
        foreach (var kvp in map)
        {
            if (kvp.Key.Length < 3) continue; // skip 2-letter codes
            if (lower.Contains(kvp.Key.ToLowerInvariant()) || kvp.Key.ToLowerInvariant().Contains(lower))
                return kvp.Value;
        }

        // Last resort: try first word (handles "Korean Republic" → korea → KR)
        var firstWord = lower.Split(' ')[0];
        if (firstWord.Length >= 4)
        {
            foreach (var kvp in map)
            {
                if (kvp.Key.Length < 3) continue;
                if (kvp.Key.ToLowerInvariant().Contains(firstWord))
                    return kvp.Value;
            }
        }

        // Log unknown for debugging
        _unknownCountries.Add(country);
        return "??";
    }

    private static readonly HashSet<string> _unknownCountries = new();
}
