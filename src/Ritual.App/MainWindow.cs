using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Microsoft.Win32;
using OpenCvSharp;
using Ritual.Core;
using Window = System.Windows.Window;
using WpfImage = System.Windows.Controls.Image;

namespace Ritual.App;

public sealed class MainWindow : Window
{
    private readonly string root,
        data,
        settingsPath;
    private readonly Settings settings;
    private VisionEngine? vision;
    private readonly TextReaderEngine ocr = new();
    private readonly ScoutMarket scout = new();
    private readonly TradeMarket trade = new();
    private readonly PriceCache cache;
    private InputService? input;
    private WgcCapture? capture;
    private readonly GenerationGate generations = new();
    private readonly SemaphoreSlim operations = new(1);
    private readonly SemaphoreSlim priceOperations = new(1);
    private readonly HashSet<string> priceJobs = [];
    private readonly Dictionary<string, DateTimeOffset> marketCooldowns = [];
    private readonly HashSet<string> scheduledRetries = [];
    private CancellationTokenSource cancellation = new();
    private readonly Overlay overlay = new();
    private readonly DispatcherTimer watcher;
    private readonly ObservableCollection<Row> rows = [];
    private readonly Dictionary<string, PriceQuote> quotes = [];
    private readonly ComboBox leagues = new() { MinWidth = 190 };
    private bool loadingLeagues;
    private readonly FocusRecovery focusRecovery = new();
    private readonly ComboBox correction = new()
    {
        IsEditable = true,
        IsTextSearchEnabled = false,
        DisplayMemberPath = "NameKo",
        SelectedValuePath = "Id",
        MinWidth = 180,
    };
    private string? selectedInstance;
    private bool bindingCorrection;
    private Box? previewBox;
    private readonly Dictionary<string, string> priceStates = [];
    private readonly Dictionary<string, TooltipInfo> tooltips = [];
    private readonly string captureSession = DateTimeOffset.Now.ToString("yyyyMMdd-HHmmss");
    private string? lastCaptureHash;
    private readonly TextBlock status = new()
    {
        TextWrapping = TextWrapping.Wrap,
        Foreground = Brushes.LightSteelBlue,
    };
    private readonly TextBlock details = new()
    {
        TextWrapping = TextWrapping.Wrap,
        Foreground = Brushes.Gainsboro,
        Margin = new Thickness(0, 8, 0, 0),
    };
    private readonly WpfImage image = new() { Stretch = Stretch.Uniform };
    private readonly LabelsLayer labels = new() { DrawBoxes = true };
    private readonly Grid preview = new()
    {
        Background = new SolidColorBrush(Color.FromRgb(11, 14, 18)),
    };
    private Mat? frame;
    private Analysis? analysis;
    private nint liveWindow;
    private Box? liveBounds;
    private bool watching,
        watchBusy,
        closing;
    private string? lastReadTooltip;
    private byte[]? tooltipPixels;
    private Box? previousTooltip;
    private int tooltipStableFrames;
    private Rate? rate;
    private readonly string[] launchArgs;

    private sealed record Row(
        string InstanceId,
        string Item,
        string Quantity,
        string Price,
        string Confidence,
        string Basis,
        string Detail
    );

    public MainWindow(string[] args)
    {
        launchArgs = args;
        root = BundledAssets.Prepare() ?? FindRoot();
        data = Path.Combine(root, "data");
        settingsPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "RitualChecker",
            "settings.json"
        );
        try
        {
            settings = File.Exists(settingsPath) ? JsonFiles.Read<Settings>(settingsPath) : new();
        }
        catch (Exception e)
            when (e is IOException or System.Text.Json.JsonException or UnauthorizedAccessException)
        {
            settings = new();
        }
        cache = new(Path.Combine(data, "price-cache"));
        ocr.LoadDigitReference(Path.Combine(data, "ui", "quantity-one.png"));
        Title = "Ritual Checker · Controller Edition";
        Width = 1280;
        Height = 850;
        MinWidth = 980;
        MinHeight = 650;
        Background = new SolidColorBrush(Color.FromRgb(20, 24, 30));
        Foreground = Brushes.White;
        FontFamily = new FontFamily("Malgun Gothic");
        BuildUi();
        watcher = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(settings.WatchIntervalMs),
        };
        watcher.Tick += Watch;
        Loaded += OnLoaded;
        Closing += (_, _) =>
        {
            closing = true;
            watcher.Stop();
            Invalidate();
            input?.Dispose();
            overlay.Close();
        };
        Closed += (_, _) =>
        {
            frame?.Dispose();
        };
    }

    private static string FindRoot()
    {
        foreach (var start in new[] { AppContext.BaseDirectory, Directory.GetCurrentDirectory() })
        {
            var d = new DirectoryInfo(start);
            while (d is not null)
            {
                if (
                    Directory.Exists(Path.Combine(d.FullName, "data"))
                    && File.Exists(Path.Combine(d.FullName, "data", "catalog.json"))
                )
                    return d.FullName;
                d = d.Parent;
            }
        }
        return AppContext.BaseDirectory;
    }

    private Button Button(string text, RoutedEventHandler handler)
    {
        var b = new Button
        {
            Content = text,
            Padding = new Thickness(12, 7, 12, 7),
            Margin = new Thickness(0, 0, 8, 0),
        };
        b.Click += handler;
        return b;
    }

    private void BuildUi()
    {
        var shell = new DockPanel { Margin = new Thickness(22) };
        Content = shell;
        var header = new StackPanel();
        DockPanel.SetDock(header, Dock.Top);
        shell.Children.Add(header);
        header.Children.Add(
            new TextBlock
            {
                Text = "RITUAL CHECKER",
                FontSize = 26,
                FontWeight = FontWeights.Bold,
                Foreground = new SolidColorBrush(Color.FromRgb(230, 194, 119)),
            }
        );
        header.Children.Add(
            new TextBlock
            {
                Text = "게임에서 F8 또는 LB+RB 길게 누르기 · 원본 화면 자동 수집",
                Foreground = Brushes.LightGray,
                Margin = new Thickness(0, 4, 0, 14),
            }
        );
        var toolbar = new WrapPanel();
        header.Children.Add(toolbar);
        toolbar.Children.Add(
            Button(
                "화면 파일 열기",
                async (_, _) =>
                {
                    var picker = new OpenFileDialog
                    {
                        Filter = "PNG images|*.png|Images|*.jpg;*.jpeg;*.bmp",
                    };
                    if (picker.ShowDialog() == true)
                        await Replay(picker.FileName);
                }
            )
        );

        toolbar.Children.Add(
            Button(
                "분석 종료",
                (_, _) =>
                {
                    Invalidate();
                    SetStatus("분석 종료");
                }
            )
        );
        toolbar.Children.Add(Button("입력 설정", (_, _) => EditInput()));
        toolbar.Children.Add(
            new TextBlock { Text = "리그  ", VerticalAlignment = VerticalAlignment.Center }
        );
        toolbar.Children.Add(leagues);
        if (!string.IsNullOrWhiteSpace(settings.League))
        {
            leagues.ItemsSource = new[] { settings.League };
            leagues.SelectedItem = settings.League;
        }
        leagues.SelectionChanged += async (_, _) =>
        {
            if (
                loadingLeagues
                || leagues.SelectedItem is not string league
                || league == settings.League
            )
                return;
            settings.League = league;
            JsonFiles.Write(settingsPath, settings);
            var active = watching;
            var window = liveWindow;
            var bounds = liveBounds;
            var readTooltips = tooltips.ToArray();
            Invalidate();
            foreach (var tip in readTooltips)
                tooltips[tip.Key] = tip.Value;
            liveWindow = window;
            liveBounds = bounds;
            watching = active;
            if (active)
                watcher.Start();
            Render();
            if (analysis?.Grid is not null)
            {
                _ = RefreshPrices(generations.Current, null, cancellation.Token);
                foreach (var tip in tooltips.Values.Distinct())
                    _ = RefreshPrices(generations.Current, tip, cancellation.Token);
            }
            await Task.CompletedTask;
        };
        status.Margin = new Thickness(0, 12, 0, 12);
        header.Children.Add(status);
        var footer = new TextBlock
        {
            Text =
                "관측 오류 0건 검증 미완료 · 가격은 매물 호가/Scout 관측값 · GGG와 무관한 비공식 도구",
            Foreground = Brushes.Gray,
            Margin = new Thickness(0, 12, 0, 0),
        };
        DockPanel.SetDock(footer, Dock.Bottom);
        shell.Children.Add(footer);
        var body = new Grid();
        body.ColumnDefinitions.Add(new() { Width = new GridLength(3, GridUnitType.Star) });
        body.ColumnDefinitions.Add(new() { Width = new GridLength(2, GridUnitType.Star) });
        shell.Children.Add(body);
        preview.Children.Add(image);
        preview.Children.Add(labels);
        preview.SizeChanged += (_, _) => ResizeLabels();
        body.Children.Add(preview);
        var right = new DockPanel { Margin = new Thickness(18, 0, 0, 0) };
        Grid.SetColumn(right, 1);
        body.Children.Add(right);
        var fixPanel = new StackPanel { Margin = new Thickness(0, 10, 0, 0) };
        DockPanel.SetDock(fixPanel, Dock.Bottom);
        right.Children.Add(fixPanel);
        fixPanel.Children.Add(
            new TextBlock
            {
                Text = "이름 수정 · 항목 선택 후 정확한 이름 검색",
                Foreground = Brushes.LightSteelBlue,
                Margin = new Thickness(0, 0, 0, 5),
            }
        );
        fixPanel.Children.Add(correction);
        correction.SelectionChanged += async (_, _) =>
        {
            if (!bindingCorrection && correction.SelectedItem is CatalogItem known)
                await CorrectItem(known);
        };
        correction.KeyUp += (_, e) =>
        {
            if (
                vision is null
                || bindingCorrection
                || e.Key
                    is System.Windows.Input.Key.Up
                        or System.Windows.Input.Key.Down
                        or System.Windows.Input.Key.Enter
                        or System.Windows.Input.Key.Escape
            )
                return;
            string query = correction.Text;
            bindingCorrection = true;
            correction.ItemsSource = vision
                .Catalog.Items.Where(i =>
                    i.NameKo.Contains(query, StringComparison.OrdinalIgnoreCase)
                    || i.NameEn.Contains(query, StringComparison.OrdinalIgnoreCase)
                )
                .OrderBy(i => i.NameKo)
                .ToArray();
            correction.Text = query;
            bindingCorrection = false;
            correction.IsDropDownOpen = true;
        };
        var dscroll = new ScrollViewer
        {
            Content = details,
            MaxHeight = 160,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
        };
        DockPanel.SetDock(dscroll, Dock.Bottom);
        right.Children.Add(dscroll);
        var headerStyle = new Style(
            typeof(System.Windows.Controls.Primitives.DataGridColumnHeader)
        );
        headerStyle.Setters.Add(
            new Setter(Control.BackgroundProperty, new SolidColorBrush(Color.FromRgb(43, 51, 63)))
        );
        headerStyle.Setters.Add(new Setter(Control.ForegroundProperty, Brushes.Gainsboro));
        headerStyle.Setters.Add(new Setter(Control.PaddingProperty, new Thickness(5, 7, 5, 7)));
        var table = new DataGrid
        {
            ColumnHeaderStyle = headerStyle,
            ItemsSource = rows,
            AutoGenerateColumns = false,
            IsReadOnly = true,
            CanUserAddRows = false,
            Background = new SolidColorBrush(Color.FromRgb(30, 35, 43)),
            Foreground = Brushes.Gainsboro,
            RowBackground = new SolidColorBrush(Color.FromRgb(30, 35, 43)),
            AlternatingRowBackground = new SolidColorBrush(Color.FromRgb(36, 42, 51)),
            RowHeight = 30,
            HeadersVisibility = DataGridHeadersVisibility.Column,
            GridLinesVisibility = DataGridGridLinesVisibility.None,
        };
        foreach (
            var (field, title, width) in new[]
            {
                ("Item", "아이템", 165),
                ("Quantity", "수량", 45),
                ("Price", "묶음 가격", 90),
                ("Confidence", "유사도", 55),
            }
        )
            table.Columns.Add(
                new DataGridTextColumn
                {
                    Header = title,
                    Binding = new System.Windows.Data.Binding(field),
                    Width = width,
                }
            );
        table.SelectionChanged += (_, _) =>
        {
            if (table.SelectedItem is Row row)
            {
                selectedInstance = row.InstanceId;
                details.Text = row.Detail;
                bindingCorrection = true;
                correction.SelectedItem = null;
                correction.Text = "";
                bindingCorrection = false;
            }
        };
        right.Children.Add(table);
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        try
        {
            SetStatus("참조 카탈로그 준비 중…");
            vision = await Task.Run(() => new VisionEngine(data));
            ocr.NonStackable.UnionWith(
                vision.Catalog.Items.Where(i => i.MaxStackSize == 1).Select(i => i.Id)
            );
            correction.ItemsSource = vision.Catalog.Items.OrderBy(i => i.NameKo).ToArray();
            if (!launchArgs.Contains("--ui-shot"))
                try
                {
                    input = new(new WindowInteropHelper(this).Handle, settings, () => _ = Live());
                }
                catch (Exception error)
                {
                    MessageBox.Show(this, error.Message, "입력 설정 확인");
                }
            SetStatus(
                $"참조 {vision.Catalog.Items.Length}개 로드 · F8 / LB+RB · "
                    + (ocr.Available ? "한국어 OCR 준비됨" : "한국어 OCR 언어팩 없음")
            );
            if (!launchArgs.Contains("--ui-shot"))
                _ = LoadLeagues();
            var replay = Array.IndexOf(launchArgs, "--replay");
            if (replay >= 0 && replay + 1 < launchArgs.Length)
                await Replay(launchArgs[replay + 1]);
            var wgcshot = Array.IndexOf(launchArgs, "--wgc-shot");
            if (wgcshot >= 0 && wgcshot + 1 < launchArgs.Length)
            {
                using var testCapture = new WgcCapture(new WindowInteropHelper(this).Handle);
                using var captured = await testCapture.CaptureAsync(default);
                Cv2.ImWrite(launchArgs[wgcshot + 1], captured.Image);
                JsonFiles.Write(
                    launchArgs[wgcshot + 1] + ".json",
                    new
                    {
                        captured.Backend,
                        captured.ColorStatus,
                        captured.ScreenBounds,
                        width = captured.Image.Width,
                        height = captured.Image.Height,
                    }
                );
            }
            var screenshot = Array.IndexOf(launchArgs, "--ui-shot");
            if (screenshot >= 0 && screenshot + 1 < launchArgs.Length)
            {
                await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
                UpdateLayout();
                var bmp = new RenderTargetBitmap(
                    (int)ActualWidth,
                    (int)ActualHeight,
                    96,
                    96,
                    PixelFormats.Pbgra32
                );
                bmp.Render(this);
                var encoder = new PngBitmapEncoder();
                encoder.Frames.Add(BitmapFrame.Create(bmp));
                using var output = File.Create(launchArgs[screenshot + 1]);
                encoder.Save(output);
                Close();
            }
        }
        catch (Exception ex)
        {
            SetStatus("초기화 실패: " + ex.Message);
        }
    }

    private void EditInput()
    {
        var dialog = new Window
        {
            Owner = this,
            Title = "입력 설정",
            Width = 470,
            Height = 430,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
        };
        var panel = new StackPanel { Margin = new Thickness(20) };
        dialog.Content = panel;
        panel.Children.Add(new TextBlock { Text = "키보드 키를 아래 입력란에서 누르세요." });
        var keyBox = new TextBox
        {
            Text = (
                (System.Windows.Input.Key)
                    System.Windows.Input.KeyInterop.KeyFromVirtualKey(settings.KeyboardVirtualKey)
            ).ToString(),
            Margin = new Thickness(0, 8, 0, 10),
        };
        int vk = settings.KeyboardVirtualKey;
        uint modifiers = settings.KeyboardModifiers;
        keyBox.PreviewKeyDown += (_, e) =>
        {
            e.Handled = true;
            var key = e.Key == System.Windows.Input.Key.System ? e.SystemKey : e.Key;
            if (
                key
                is System.Windows.Input.Key.LeftCtrl
                    or System.Windows.Input.Key.RightCtrl
                    or System.Windows.Input.Key.LeftAlt
                    or System.Windows.Input.Key.RightAlt
                    or System.Windows.Input.Key.LeftShift
                    or System.Windows.Input.Key.RightShift
            )
                return;
            vk = System.Windows.Input.KeyInterop.VirtualKeyFromKey(key);
            var m = System.Windows.Input.Keyboard.Modifiers;
            modifiers = (uint)(
                ((m & System.Windows.Input.ModifierKeys.Alt) != 0 ? 1 : 0)
                | ((m & System.Windows.Input.ModifierKeys.Control) != 0 ? 2 : 0)
                | ((m & System.Windows.Input.ModifierKeys.Shift) != 0 ? 4 : 0)
            );
            keyBox.Text = $"{m} + {key}";
        };
        panel.Children.Add(keyBox);
        panel.Children.Add(new TextBlock { Text = "분석할 때 함께 누를 컨트롤러 버튼" });
        var buttonChecks = new List<(CheckBox Check, ushort Value)>();
        var padPanel = new WrapPanel { Margin = new Thickness(0, 8, 0, 10) };
        panel.Children.Add(padPanel);
        foreach (
            var (name, value) in new (string, ushort)[]
            {
                ("LB", 0x100),
                ("RB", 0x200),
                ("보기 / Back", 0x20),
                ("메뉴 / Start", 0x10),
                ("왼쪽 스틱", 0x40),
                ("오른쪽 스틱", 0x80),
                ("A", 0x1000),
                ("B", 0x2000),
                ("X", 0x4000),
                ("Y", 0x8000),
            }
        )
        {
            var check = new CheckBox
            {
                Content = name,
                IsChecked = (settings.ControllerButtons & value) != 0,
                Margin = new Thickness(0, 4, 12, 4),
            };
            padPanel.Children.Add(check);
            buttonChecks.Add((check, value));
        }
        panel.Children.Add(
            new TextBlock
            {
                Text = "분석 화면은 프로그램의 captures 폴더에 자동 저장됩니다.",
                TextWrapping = TextWrapping.Wrap,
            }
        );
        panel.Children.Add(
            Button(
                "저장",
                (_, _) =>
                {
                    ushort buttons = (ushort)
                        buttonChecks
                            .Where(b => b.Check.IsChecked == true)
                            .Aggregate(0, (sum, b) => sum | b.Value);
                    if (buttons == 0)
                    {
                        MessageBox.Show("버튼 조합을 확인하세요.");
                        return;
                    }
                    var old = (
                        settings.KeyboardVirtualKey,
                        settings.KeyboardModifiers,
                        settings.ControllerButtons
                    );
                    try
                    {
                        settings.KeyboardVirtualKey = vk;
                        settings.KeyboardModifiers = modifiers;
                        settings.ControllerButtons = buttons;
                        if (input is null)
                            input = new(
                                new WindowInteropHelper(this).Handle,
                                settings,
                                () => _ = Live()
                            );
                        else
                            input.Configure();
                        JsonFiles.Write(settingsPath, settings);
                        dialog.Close();
                    }
                    catch (Exception ex)
                    {
                        (
                            settings.KeyboardVirtualKey,
                            settings.KeyboardModifiers,
                            settings.ControllerButtons
                        ) = old;
                        input?.Configure();
                        MessageBox.Show(ex.Message);
                    }
                }
            )
        );
        dialog.ShowDialog();
    }

    private void Invalidate()
    {
        generations.Next();
        cancellation.Cancel();
        cancellation.Dispose();
        cancellation = new();
        watching = false;
        watcher?.Stop();
        overlay.Hide();
        quotes.Clear();
        priceStates.Clear();
        tooltips.Clear();
        focusRecovery.FrameValidated();
        rate = null;
        lastReadTooltip = null;
        tooltipPixels = null;
        previousTooltip = null;
        tooltipStableFrames = 0;
        capture?.Dispose();
        capture = null;
    }

    private async Task Replay(string path)
    {
        if (vision is null)
            return;
        Invalidate();
        liveWindow = 0;
        liveBounds = null;
        await AnalyzeFrame(Cv2.ImRead(path), null);
    }

    private async Task Live()
    {
        if (vision is null || !GameWindow.IsGame(GameWindow.GetForegroundWindow()))
            return;
        Invalidate();
        var window = GameWindow.GetForegroundWindow();
        try
        {
            capture = new WgcCapture(window);
            var generation = generations.Current;
            using var captured = await capture.CaptureAsync(cancellation.Token);
            if (!generations.Accept(generation))
                return;
            liveWindow = window;
            liveBounds = captured.ScreenBounds;
            await AnalyzeFrame(captured.Image.Clone(), captured.ColorStatus);
        }
        catch (Exception e)
        {
            SetStatus(e.Message);
        }
    }

    private async Task AnalyzeFrame(Mat captured, string? captureStatus)
    {
        var generation = generations.Current;
        var token = cancellation.Token;
        try
        {
            await operations.WaitAsync(token);
            try
            {
                SetStatus("의식 창과 아이템 분석 중…");
                var watch = Stopwatch.StartNew();
                var result = await Task.Run(() => vision!.Analyze(captured, generation), token);
                if (ocr.Available)
                    result = await ocr.ReadQuantitiesAsync(captured, result, token);
                if (!generations.Accept(generation) || closing)
                    return;
                frame?.Dispose();
                frame = captured.Clone();
                analysis = result;
                focusRecovery.FrameValidated();
                selectedInstance = null;
                bindingCorrection = true;
                correction.SelectedItem = null;
                correction.Text = "";
                bindingCorrection = false;
                quotes.Clear();
                priceStates.Clear();
                tooltips.Clear();
                DisplayFrame();
                Render();
                SetStatus(
                    $"{result.Items.Length}개 영역 · 로컬 처리 {watch.Elapsed.TotalMilliseconds:0}ms · {captureStatus ?? "저장 화면"}\n"
                        + string.Join(" / ", result.Warnings)
                );
                if (result.Grid is null)
                {
                    if (liveWindow != 0)
                        await SaveCapture(captured, result);
                    return;
                }
                if (ocr.Available && result.TooltipBounds is not null)
                {
                    var tooltip = await ocr.ReadTooltipAsync(
                        captured,
                        result,
                        vision!.Catalog,
                        token
                    );
                    if (tooltip is not null && generations.Accept(generation))
                    {
                        ApplyTooltip(captured, tooltip);
                        details.Text =
                            $"{tooltip.Name}\n구매 공물: {tooltip.PurchaseTribute?.ToString() ?? "미확인"} / 보류 공물: {tooltip.DeferTribute?.ToString() ?? "해당 없음"}\n{(tooltip.Complete ? "옵션 판독 완료" : "옵션 일부 미판독")}\n{TooltipDetails(tooltip)}";
                    }
                }
                if (liveWindow != 0)
                {
                    watching = true;
                    watcher.Start();
                }
                if (liveWindow != 0)
                    await SaveCapture(captured, result);
            }
            finally
            {
                operations.Release();
            }
            if (analysis?.Grid is not null && !string.IsNullOrWhiteSpace(settings.League))
            {
                _ = RefreshPrices(generation, null, token);
                foreach (var tip in tooltips.Values.Distinct())
                    _ = RefreshPrices(generation, tip, token);
            }
            else if (string.IsNullOrWhiteSpace(settings.League))
                SetStatus(status.Text + "\n리그를 선택하면 시세를 자동 조회합니다.");
        }
        catch (OperationCanceledException) { }
        catch (Exception e)
        {
            if (generations.Accept(generation))
                SetStatus("분석 오류: " + e.Message);
        }
        finally
        {
            captured.Dispose();
        }
    }

    private async Task RefreshPrices(long generation, TooltipInfo? tooltip, CancellationToken token)
    {
        if (launchArgs.Contains("--ui-shot"))
            return;
        string job =
            generation
            + "|"
            + (
                tooltip is null
                    ? "base"
                    : PriceCache.Key(settings.League, tooltip.CatalogId ?? "", tooltip)
            );
        if (!priceJobs.Add(job))
            return;
        try
        {
            await priceOperations.WaitAsync(token);
            try
            {
                if (generations.Accept(generation))
                    await Prices(generation, tooltip, token);
            }
            finally
            {
                priceOperations.Release();
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            if (generations.Accept(generation))
                SetStatus("시세 갱신: " + ex.Message);
        }
        finally
        {
            priceJobs.Remove(job);
        }
    }

    private async Task RetryMarket(
        long generation,
        string kind,
        DateTimeOffset due,
        CancellationToken token
    )
    {
        string key = generation + "|" + kind;
        if (!scheduledRetries.Add(key))
            return;
        try
        {
            while (due > DateTimeOffset.UtcNow)
            {
                await Task.Delay(
                    TimeSpan.FromSeconds(
                        Math.Max(.01, Math.Min(1, (due - DateTimeOffset.UtcNow).TotalSeconds))
                    ),
                    token
                );
                if (generations.Accept(generation))
                    Render();
            }
            if (!generations.Accept(generation) || closing)
                return;
            marketCooldowns.Remove(kind);
            scheduledRetries.Remove(key);
            _ = RefreshPrices(generation, null, token);
            foreach (var info in tooltips.Values.Distinct())
                _ = RefreshPrices(generation, info, token);
        }
        catch (OperationCanceledException) { }
        finally
        {
            scheduledRetries.Remove(key);
        }
    }

    private async Task Prices(long generation, TooltipInfo? tooltip, CancellationToken token)
    {
        if (analysis is null || vision is null)
            return;
        var targets = analysis
            .Items.Where(i =>
                i.CatalogId is not null
                && (
                    tooltip is null
                        ? !tooltips.ContainsKey(i.InstanceId)
                        : tooltips.TryGetValue(i.InstanceId, out var assigned)
                            && assigned == tooltip
                )
            )
            .ToArray();
        foreach (var item in targets)
        {
            var old =
                cache.Read(PriceCache.Key(settings.League, item.CatalogId!, tooltip))
                ?? (
                    tooltip is null
                        ? null
                        : cache.Read(PriceCache.Key(settings.League, item.CatalogId!))
                );
            if (old is not null)
                quotes[item.InstanceId] = old;
        }
        Render();
        try
        {
            var fetchedRate = await scout.RateAsync(settings.League, token);
            if (!generations.Accept(generation))
                return;
            rate = fetchedRate;
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            SetStatus("환율 조회: " + e.Message);
            rate = null;
        }
        foreach (var group in targets.GroupBy(i => i.CatalogId))
        {
            token.ThrowIfCancellationRequested();
            if (!generations.Accept(generation))
                return;
            var item = vision.Catalog.Items.Single(i => i.Id == group.Key);
            var key = PriceCache.Key(settings.League, item.Id, tooltip);
            var cached = cache.Read(key);
            if (cached is { Stale: false })
                continue;
            if (
                marketCooldowns.TryGetValue(item.Kind, out var until)
                && until > DateTimeOffset.UtcNow
            )
            {
                _ = RetryMarket(generation, item.Kind, until, token);
                continue;
            }
            foreach (var observed in group)
                priceStates[observed.InstanceId] = "조회 중…";
            Render();
            try
            {
                PriceQuote? quote;
                if (item.Kind == "currency")
                    quote = await scout.QuoteAsync(item, settings.League, token);
                else
                {
                    var result = await trade.QuoteAsync(
                        item,
                        settings.League,
                        rate,
                        tooltip,
                        token
                    );
                    quote = result.Quote;
                    JsonFiles.Write(
                        Path.Combine(
                            data,
                            "market-observations",
                            $"{DateTimeOffset.UtcNow:yyyyMMddTHHmmssfff}-{item.Id}.json"
                        ),
                        new
                        {
                            item.Id,
                            settings.League,
                            rate,
                            tooltip,
                            listings = result.Listings,
                            quote,
                        }
                    );
                }
                if (!generations.Accept(generation))
                    return;
                if (quote is not null)
                {
                    cache.Write(key, quote);
                    foreach (var observed in group)
                    {
                        if (
                            analysis?.Items.Any(i =>
                                i.InstanceId == observed.InstanceId && i.CatalogId == quote.ItemId
                            ) == true
                            && (tooltip is not null || !tooltips.ContainsKey(observed.InstanceId))
                        )
                        {
                            quotes[observed.InstanceId] = quote;
                            priceStates.Remove(observed.InstanceId);
                        }
                    }
                }
                else
                    foreach (var observed in group)
                        priceStates[observed.InstanceId] = "매물 없음";
                Render();
            }
            catch (MarketCooldownException e)
            {
                marketCooldowns[item.Kind] = e.RetryAt;
                if (!generations.Accept(generation))
                    return;
                foreach (var observed in group)
                    priceStates[observed.InstanceId] = "요청 대기";
                Render();
                _ = RetryMarket(generation, item.Kind, e.RetryAt, token);
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                if (!generations.Accept(generation))
                    return;
                foreach (var observed in group)
                    priceStates[observed.InstanceId] = "조회 실패";
                Render();
                SetStatus($"{VisionEngine.DisplayName(item)}: {e.Message}");
            }
        }
        if (generations.Accept(generation))
            Render();
    }

    private async Task LoadLeagues()
    {
        var path = Path.Combine(data, "leagues.json");
        void Bind(string[] values)
        {
            loadingLeagues = true;
            leagues.ItemsSource = values;
            leagues.SelectedItem = values.Contains(settings.League) ? settings.League : null;
            loadingLeagues = false;
        }
        try
        {
            if (File.Exists(path))
                Bind(JsonFiles.Read<string[]>(path));
            var values = await scout.LeaguesAsync(CancellationToken.None);
            if (closing)
                return;
            Bind(values);
            JsonFiles.Write(path, values);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            SetStatus("리그 목록: " + ex.Message);
        }
    }

    private async Task SaveCapture(Mat original, Analysis result, TooltipInfo? tooltip = null)
    {
        using var copy = original.Clone();
        var hdr = capture?.DisplayColorInfo.HdrEnabled;
        try
        {
            await Task.Run(() =>
            {
                Cv2.ImEncode(".png", copy, out var bytes);
                var hash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes));
                if (hash == lastCaptureHash)
                    return;
                lastCaptureHash = hash;
                var folder = Path.Combine(AppContext.BaseDirectory, "captures", captureSession);
                Directory.CreateDirectory(folder);
                var id = DateTimeOffset.UtcNow.ToString("yyyyMMddTHHmmssfff") + "-" + hash[..8];
                File.WriteAllBytes(Path.Combine(folder, id + ".png"), bytes);
                JsonFiles.Write(
                    Path.Combine(folder, id + ".json"),
                    new
                    {
                        session = captureSession,
                        split = "unassigned",
                        source = "real",
                        independent = false,
                        fullFrame = true,
                        resolution = new[] { copy.Width, copy.Height },
                        hdr,
                        ui = "controller-ko",
                        captureBackend = "Windows.Graphics.Capture/game-window",
                        sha256 = hash,
                        prediction = result,
                        tooltip,
                        groundTruth = (object?)null,
                    }
                );
            });
        }
        catch (Exception ex)
        {
            SetStatus("원본 저장 실패: " + ex.Message);
        }
    }

    private async void Watch(object? sender, EventArgs e)
    {
        if (!watching || frame is null || analysis?.Grid is null)
            return;
        if (GameWindow.GetForegroundWindow() != liveWindow)
        {
            overlay.Hide();
            focusRecovery.LostFocus();
            if (!watchBusy)
            {
                capture?.Dispose();
                capture = null;
            }
            return;
        }
        if (watchBusy)
            return;
        watchBusy = true;
        var gen = generations.Current;
        var token = cancellation.Token;
        try
        {
            capture ??= new WgcCapture(liveWindow);
            using var current = await capture.CaptureAsync(token);
            if (!generations.Accept(gen))
                return;
            if (current.ScreenBounds != liveBounds || focusRecovery.RequiresValidation)
            {
                overlay.Hide();
                liveBounds = current.ScreenBounds;
                await Reanalyze(current.Image, current.ColorStatus);
                return;
            }
            var grid = analysis.Grid;
            var tooltip = VisionEngine.DetectTooltip(current.Image, grid);
            var changed = VisionEngine.SceneDifference(frame, current.Image, grid);
            if (changed > .025 && tooltip is null)
            {
                overlay.Hide();
                await Reanalyze(current.Image, current.ColorStatus);
                return;
            }
            analysis = analysis with { TooltipBounds = tooltip };
            Render();
            if (tooltip is null)
            {
                lastReadTooltip = null;
                tooltipPixels = null;
                tooltipStableFrames = 0;
                return;
            }
            using var region = new Mat(current.Image, tooltip.Rect());
            using var small = new Mat();
            Cv2.Resize(region, small, new OpenCvSharp.Size(160, 120));
            using var gray = new Mat();
            Cv2.CvtColor(small, gray, ColorConversionCodes.BGR2GRAY);
            var bytes = new byte[160 * 120];
            for (int y = 0; y < 120; y++)
            for (int x = 0; x < 160; x++)
                bytes[y * 160 + x] = (byte)(gray.At<byte>(y, x) > 130 ? 255 : 0);
            double diff = tooltipPixels is null
                ? 1
                : bytes.Zip(tooltipPixels).Count(p => p.First != p.Second) / (double)bytes.Length;
            bool moved =
                previousTooltip is null
                || Math.Abs(previousTooltip.X - tooltip.X) > 4
                || Math.Abs(previousTooltip.Y - tooltip.Y) > 4;
            tooltipPixels = bytes;
            previousTooltip = tooltip;
            if (diff > .02 || moved)
            {
                tooltipStableFrames = 0;
                lastReadTooltip = null;
                return;
            }
            if (++tooltipStableFrames < 2 || lastReadTooltip is not null)
                return;
            lastReadTooltip = "pending";
            var info = await ocr.ReadTooltipAsync(current.Image, analysis, vision!.Catalog, token);
            if (!generations.Accept(gen))
                return;
            await SaveCapture(current.Image, analysis, info);
            if (info is null)
                return;
            lastReadTooltip = info.RawText;
            if (info.CatalogId is null)
                return;
            if (!ApplyTooltip(current.Image, info))
                return;
            details.Text =
                $"{info.Name}\n구매 공물: {info.PurchaseTribute?.ToString() ?? "미확인"}\n{(info.Complete ? "옵션 판독 완료" : "옵션 판독 일부 누락")}\n{TooltipDetails(info)}";
            if (!string.IsNullOrWhiteSpace(settings.League))
                _ = RefreshPrices(gen, info, token);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            if (generations.Accept(gen))
            {
                overlay.Hide();
                SetStatus("화면 감시: " + ex.Message);
            }
        }
        finally
        {
            watchBusy = false;
        }
    }

    private string TooltipDetails(TooltipInfo info)
    {
        var item = vision?.Catalog.Items.SingleOrDefault(i => i.Id == info.CatalogId);
        if (item is null)
            return "";
        return string.Join('\n', TooltipParser.FormatOptions(info, item))
            + (info.FlavorLines?.Length > 0 ? "\n(배경 설명문은 가격 판독에서 제외)" : "");
    }

    private bool ApplyTooltip(Mat pixels, TooltipInfo info)
    {
        if (analysis is null || vision is null || info.CatalogId is null)
            return false;
        var known = vision.Catalog.Items.SingleOrDefault(i => i.Id == info.CatalogId);
        if (known is null)
            return false;
        var current = analysis with
        {
            Items = analysis
                .Items.Select(i =>
                    i with
                    {
                        Selected = VisionEngine.Selected(pixels, i.Bounds, analysis.Grid!.CellSize),
                    }
                )
                .ToArray(),
        };
        var target = TooltipBinding.Resolve(current, info, vision.Catalog);
        if (target is null)
            return false;
        analysis = analysis with
        {
            Items = analysis
                .Items.Select(i =>
                    i.InstanceId == target.InstanceId
                        ? i with
                        {
                            CatalogId = known.Id,
                            Name = VisionEngine.DisplayName(known),
                            Kind = known.Kind,
                            Estimated = false,
                            Quantity = known.Kind == "unique" ? 1 : i.Quantity,
                        }
                        : i
                )
                .ToArray(),
        };
        quotes.Remove(target.InstanceId);
        priceStates.Remove(target.InstanceId);
        tooltips[target.InstanceId] = info;
        Render();
        return true;
    }

    private async Task CorrectItem(CatalogItem known)
    {
        if (analysis is null || frame is null || vision is null)
            return;
        var target = analysis.Items.SingleOrDefault(i => i.InstanceId == selectedInstance);
        if (target is null)
            return;
        if (!TooltipBinding.Fits(target, known))
        {
            SetStatus("아이템 크기가 선택한 영역과 다릅니다.");
            return;
        }
        using var crop = new Mat(frame, target.Bounds.Rect());
        vision.AddExample(crop, known.Id, Path.Combine(data, "user-examples"), captureSession);
        analysis = analysis with
        {
            Items = analysis
                .Items.Select(i =>
                    i.InstanceId == target.InstanceId
                        ? i with
                        {
                            CatalogId = known.Id,
                            Name = VisionEngine.DisplayName(known),
                            Kind = known.Kind,
                            Estimated = false,
                            Quantity = known.Kind == "unique" ? 1 : i.Quantity,
                        }
                        : i
                )
                .ToArray(),
        };
        // Cancel outstanding queries so the previous guess cannot overwrite the correction.
        generations.Next();
        cancellation.Cancel();
        cancellation.Dispose();
        cancellation = new();
        quotes.Remove(target.InstanceId);
        priceStates.Remove(target.InstanceId);
        tooltips.Remove(target.InstanceId);
        Render();
        SetStatus("이름 수정 저장 · 다음 분석부터 이 실물 참조도 사용합니다.");
        if (!string.IsNullOrWhiteSpace(settings.League))
            await RefreshPrices(generations.Current, null, cancellation.Token);
    }

    private async Task Reanalyze(Mat current, string colorStatus)
    {
        // A returned focus or changed screen starts a new generation before any price can render.
        generations.Next();
        cancellation.Cancel();
        cancellation.Dispose();
        cancellation = new();
        quotes.Clear();
        priceStates.Clear();
        tooltips.Clear();
        lastReadTooltip = null;
        tooltipPixels = null;
        await AnalyzeFrame(current.Clone(), colorStatus);
        if (analysis?.Grid is null)
        {
            watching = false;
            watcher.Stop();
            overlay.Hide();
        }
    }

    private void DisplayFrame()
    {
        if (frame is null)
            return;
        previewBox = Presentation.Preview(
            analysis?.Grid?.Bounds,
            analysis?.Grid?.CellSize ?? 0,
            frame.Width,
            frame.Height
        );
        using var region = new Mat(frame, previewBox.Rect());
        Cv2.ImEncode(".png", region, out var bytes);
        var bitmap = new BitmapImage();
        bitmap.BeginInit();
        bitmap.CacheOption = BitmapCacheOption.OnLoad;
        bitmap.StreamSource = new MemoryStream(bytes);
        bitmap.EndInit();
        bitmap.Freeze();
        image.Source = bitmap;
        ResizeLabels();
    }

    private void ResizeLabels()
    {
        if (previewBox is not { } box || preview.ActualWidth == 0)
            return;
        var s = Math.Min(preview.ActualWidth / box.Width, preview.ActualHeight / box.Height);
        labels.Width = box.Width * s;
        labels.Height = box.Height * s;
        labels.HorizontalAlignment = HorizontalAlignment.Center;
        labels.VerticalAlignment = VerticalAlignment.Center;
        labels.ScaleX = s;
        labels.ScaleY = s;
        labels.OffsetX = box.X;
        labels.OffsetY = box.Y;
        labels.InvalidateVisual();
    }

    private void Render()
    {
        if (analysis is null)
            return;
        rows.Clear();
        var tags = new List<PriceLabel>();
        foreach (var item in analysis.Items)
        {
            quotes.TryGetValue(item.InstanceId, out var quote);
            var formatted = quote is null ? null : Valuation.Format(quote, item.Quantity, rate);
            var state = priceStates.GetValueOrDefault(
                item.InstanceId,
                Presentation.PendingPrice(!string.IsNullOrWhiteSpace(settings.League))
            );
            if (
                marketCooldowns.TryGetValue(item.Kind, out var cooldown)
                && cooldown > DateTimeOffset.UtcNow
            )
                state =
                    $"요청 대기 · {Math.Ceiling((cooldown - DateTimeOffset.UtcNow).TotalSeconds)}초";
            if (quote is not null && item.Quantity is null)
                formatted = new(Valuation.Format(quote, 1, rate).Text + "/개 · 수량?", null, true);
            var price = formatted?.Text ?? state;
            if (quote is not null && priceStates.TryGetValue(item.InstanceId, out var refreshing))
                price += " · " + refreshing;
            var detail =
                $"{item.Name}\n{item.CatalogId}\n유사도 {item.Confidence:0.000} (확률 아님)\n"
                + (
                    quote is null
                        ? state
                        : $"{quote.Source} · {quote.Basis}\n단가 {quote.UnitPrice:0.####} {quote.Currency} · 표본 {quote.Samples}\n조회 {quote.RetrievedAt.ToLocalTime():g}\n{quote.Note}"
                );
            if (tooltips.TryGetValue(item.InstanceId, out var info))
            {
                var efficiency = Valuation.Efficiency(
                    formatted?.TotalExalted,
                    info.PurchaseTribute,
                    !item.Estimated && quote is { Stale: false }
                );
                detail +=
                    $"\n구매 공물 {info.PurchaseTribute?.ToString() ?? "미확인"} · 공물 1,000점당 {efficiency?.ToString("0.##") ?? "미확인"} ex";
            }
            rows.Add(
                new(
                    item.InstanceId,
                    (item.Estimated ? "~ " : "") + item.Name,
                    item.Quantity?.ToString() ?? "?",
                    price,
                    item.Confidence.ToString("0.00"),
                    quote?.Basis ?? "",
                    detail
                )
            );
            tags.Add(
                new(
                    item,
                    quote is null
                            ? state switch
                            {
                                "리그 선택" => "리그?",
                                "조회 대기…" => "대기",
                                "조회 중…" => "…",
                                "매물 없음" => "없음",
                                "조회 실패" => "실패",
                                _ => state,
                            }
                        : item.Quantity is null ? "?개"
                        : price,
                    detail,
                    formatted?.TotalExalted,
                    rate?.ExaltedPerDivine
                )
            );
        }
        labels.Labels = tags.ToArray();
        labels.Tooltip = analysis.TooltipBounds;
        labels.InvalidateVisual();
        if (
            watching
            && liveBounds is { } bounds
            && focusRecovery.CanDisplay(GameWindow.GetForegroundWindow() == liveWindow)
        )
            overlay.Update(bounds, tags.ToArray(), analysis.TooltipBounds);
    }

    private void SetStatus(string text)
    {
        if (!closing)
            status.Text = text;
    }
}
