using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using SkiaSharp;
using Matdol.Controls;
using Matdol.Core;

namespace Matdol.Views;

public enum ViewMode { Normal, Diff, Avg }

public partial class MainWindow : Window
{
    public string? OpenFileOnStartup { get; set; }

    private sealed class Slot
    {
        public int Index;
        public IFrameSource? Src;
        public FrameData? Frame;
        public RawPixelFormat Fmt = RawPixelFormat.Yuv422p;
        public int Depth = 8;
        public FrameLayout Layout = FrameLayout.Progressive;
        public YuvMatrix Matrix = YuvMatrix.Bt709;
        public bool LimitedRange = true;
        public Border Box = null!;
        public ImageView View = null!;
        public OutlineText Label = null!;
        public Border BadgeB = null!, TagB = null!;
        public Button Btn = null!;
        public int LoadTicket;
        public int RenderGen;
        public bool Rendering;
        public RenderJob? Pending;
        public byte[]? RenderBuf;
        public bool Loaded => Src != null;
    }
    private sealed record RenderJob(int W, int H, FillBgraDelegate Fill);
    private const long HeavyPixels = 4_000_000;
    private static readonly TimeSpan SpinDelay = TimeSpan.FromMilliseconds(120);
    private readonly Slot[] _slots = new Slot[4];
    private int _b = 0;
    private int _focus = 0;
    private Slot BSlot => _slots[_b];
    private Slot Shown => _slots[_focus];

    private int _cur;
    private bool _split = false;
    private ViewMode _mode = ViewMode.Normal;
    private ChannelView _channel = ChannelView.All;
    private bool _tint, _showTitle, _pixelInfoOn = false, _loop, _sync = true, _syncing;
    private readonly DiffSettings _diff = new();
    private readonly DispatcherTimer _playTimer = new();
    private readonly DispatcherTimer _hideTimer = new() { Interval = TimeSpan.FromSeconds(2.5) };
    private bool _pointerOnPanel, _shiftDown;
    private WindowState _stateBeforeFull = WindowState.Normal;

    private static readonly double[] FpsTable = { 1, 7.5, 10, 12.5, 15, 25, 30, 60, 999 };
    private static readonly int[] DepthTable = { 8, 10, 12, 16 };

    public MainWindow()
    {
        InitializeComponent();
        Icon = AppInfo.Icon;
        Title = AppInfo.Name;
        ImageView.EmptyFrames ??= AppInfo.EmptySlotFrames;
        ImageView.EmptyTurn = AppInfo.SlowTurn;
        ImageView.EmptyIntroTime = AppInfo.IntroTime;
        ImageView.ViewBackground = AppInfo.ViewBackgroundBrush;
        InitSlots();
        InitToolbar();
        InitHover();
        InitDrop();
        PixelInfo.SizeChanged += (_, _) => { if (PixelInfo.IsVisible) PlacePixelInfo(_pixelInfoCursor); };

        _playTimer.Tick += (_, _) => StepFrame(+1, wrap: _loop, stopAtEnd: !_loop);
        Opened += (_, _) =>
        {
            if (OpenFileOnStartup != null) OpenPath(OpenFileOnStartup);
            else Shown.View.PlayEmptyIntro();
        };
        ApplyLayout();
        UpdateFocusVisuals();
        UpdateSeekRange();
    }

    private void InitSlots()
    {
        Border[] boxes = { Slot1, Slot2, Slot3, Slot4 };
        ImageView[] views = { View1, View2, View3, View4 };
        OutlineText[] labels = { Label1, Label2, Label3, Label4 };
        Border[] badgeB = { BadgeB1, BadgeB2, BadgeB3, BadgeB4 };
        Border[] tagB = { TagB1, TagB2, TagB3, TagB4 };
        Button[] btns = { BtnSlot1, BtnSlot2, BtnSlot3, BtnSlot4 };
        for (int i = 0; i < 4; i++)
        {
            var s = new Slot { Index = i, Box = boxes[i], View = views[i], Label = labels[i], BadgeB = badgeB[i], TagB = tagB[i], Btn = btns[i] };
            _slots[i] = s;
            int idx = i;
            s.Btn.Click += (_, _) => FocusSlot(idx);
            s.View.OpenRequested += v => { FocusSlot(idx); _ = PickAndOpen(); };
            s.View.Clicked += _ => FocusSlot(idx);
            s.View.SelectB += _ => SetB(idx);
            var miB = new MenuItem { Header = "Compare against this (set as B)" };
            miB.Click += (_, _) => SetB(idx);
            var miOpen = new MenuItem { Header = "Open file here…" };
            miOpen.Click += (_, _) => { FocusSlot(idx); _ = PickAndOpen(); };
            var miClose = new MenuItem { Header = "Close image" };
            miClose.Click += (_, _) => CloseSlot(idx);
            var menu = new ContextMenu { ItemsSource = new Control[] { miB, miOpen, new Separator(), miClose } };
            s.View.RightClicked += _ => { miClose.IsEnabled = s.Loaded; menu.Open(s.Box); };
            s.View.HoverPixelChanged += (p, pt) => OnHoverPixel(s, p, pt);
            s.View.ViewChanged += v => SyncFrom(v);
            s.View.ZoomChanged += () => { if (idx == _focus) { UpdateZoomLabel(); UpdateTitle(); } if (_showTitle) UpdateSlotLabels(); };
        }
    }

    private void SetB(int idx)
    {
        if (idx == _b) return;
        _b = idx;
        UpdateFocusVisuals(); RenderAll(); UpdateTitle();
    }

    private void FocusSlot(int idx)
    {
        if (idx == _focus) return;
        _focus = idx;
        if (!_split) { ApplyLayout(); RenderAll(); }
        UpdateFocusVisuals(); SyncFormatPanelFromFocus(); UpdateSeekRange(); UpdateTitle(); UpdateZoomLabel();
    }

    private void UpdateFocusVisuals()
    {
        foreach (var s in _slots)
        {
            bool isA = s.Index == _focus, isB = s.Index == _b;
            s.Box.Classes.Set("a", _split && isA && !isB);
            s.Box.Classes.Set("b", _split && isB && !isA);
            s.Box.Classes.Set("ab", _split && isA && isB);
            s.Btn.Classes.Set("on", isA);
            s.BadgeB.IsVisible = isB;
            s.TagB.IsVisible = _showTitle && isB;
        }
        UpdateSlotLabels();
        DiffPair.Text = $"A: slot {_focus + 1}   B: slot {_b + 1}";
    }

    private static readonly IBrush LabelInfoBrush = new SolidColorBrush(Color.Parse("#D2E2EA"));
    private static readonly IBrush LabelModeBrush = new SolidColorBrush(Color.Parse("#FFC06A"));

    private void UpdateSlotLabels()
    {
        foreach (var s in _slots)
        {
            s.Label.IsVisible = _showTitle;
            if (!_showTitle) continue;
            var segs = new List<(string, IBrush?)>();
            if (s.Src == null) { segs.Add(($"{s.Index + 1}  (empty)", null)); s.Label.SetSegments(segs); continue; }
            string fr = s.Src.FrameCount > 1 ? $"  [{Math.Min(_cur, s.Src.FrameCount - 1) + 1}/{s.Src.FrameCount}]" : "";
            segs.Add(($"{s.Index + 1}  {Path.GetFileName(s.Src.Path)}{fr}", null));
            int w = s.Frame?.Width ?? s.Src.Width, h = s.Frame?.Height ?? s.Src.Height;
            string lay = s.Layout == FrameLayout.Progressive ? "" : " · " + s.Layout.ShortName();
            segs.Add(($"   {w}x{h} · {s.Fmt.ShortName()} {s.Depth}bit{lay} · {ZoomText(s.View.Zoom)}", LabelInfoBrush));
            if (_mode != ViewMode.Normal && s.Index != _b && BSlot.Frame != null)
                segs.Add((_mode != ViewMode.Diff ? "  (·+B)/2" : _diff.MidCenter && !_diff.BlackWhite ? "  ·−B+mid" : "  |·−B|", LabelModeBrush));
            s.Label.SetSegments(segs);
        }
    }

    private void ApplyLayout()
    {
        foreach (var s in _slots)
        {
            if (_split)
            {
                s.Box.IsVisible = true;
                Grid.SetRow(s.Box, s.Index / 2); Grid.SetColumn(s.Box, s.Index % 2);
                Grid.SetRowSpan(s.Box, 1); Grid.SetColumnSpan(s.Box, 1);
                s.View.Backdrop = AppInfo.SplitBackgroundBrushes[s.Index];
            }
            else
            {
                s.Box.IsVisible = s.Index == _focus;
                s.View.Backdrop = null;
                Grid.SetRow(s.Box, 0); Grid.SetColumn(s.Box, 0);
                Grid.SetRowSpan(s.Box, 2); Grid.SetColumnSpan(s.Box, 2);
            }
        }
        foreach (var s in _slots) s.View.InvalidateVisual();
        LayoutIcon.Data = (Geometry)Resources[_split ? "IcSolo" : "IcSplit"]!;
        UpdateFocusVisuals();
    }

    private void ToggleLayout() { _split = !_split; ApplyLayout(); RenderAll(); UpdateSeekRange(); UpdateTitle(); UpdateZoomLabel(); }

    private void InitToolbar()
    {
        BtnOpen.Click += async (_, _) => await PickAndOpen();
        BtnCapture.Click += async (_, _) => await Capture(toFile: _shiftDown);
        BtnLayout.Click += (_, _) => ToggleLayout();

        CbFormat.ItemsSource = Enum.GetValues<RawPixelFormat>().Select(f => f.DisplayName()).ToList();
        CbFormat.SelectedIndex = (int)Shown.Fmt;
        CbDepth.ItemsSource = DepthTable.Select(d => $"{d} bit" + (d > 8 ? " (16-bit LE)" : "")).ToList();
        CbDepth.SelectedIndex = 0;
        var sizes = new List<string> { "Auto" };
        sizes.AddRange(SizePreset.Table.Select(p => p.ToString()));
        sizes.Add("User");
        CbSize.ItemsSource = sizes;
        CbSize.SelectedIndex = 9;
        CbSize.SelectionChanged += (_, _) => SyncSizeBoxes();
        SyncSizeBoxes();
        CbLayout.SelectedIndex = (int)FrameLayout.Progressive;
        CbLayout.SelectionChanged += (_, _) =>
        {
            SizeViewNote.IsVisible = SelectedLayout.RawFrameMultiplier() != (1, 1);
            if (!_panelSync) ApplyLayoutChoice();
        };
        CbMatrix.ItemsSource = Enum.GetValues<YuvMatrix>().Select(m => m.DisplayName()).ToList();
        CbMatrix.SelectedIndex = (int)YuvMatrix.Bt709;
        CbRange.ItemsSource = new[] { "Full range", "Limited (TV)" };
        CbRange.SelectedIndex = 1;
        CbMatrix.SelectionChanged += (_, _) => { if (!_panelSync) ApplyMatrixChoice(); };
        CbRange.SelectionChanged += (_, _) => { if (!_panelSync) ApplyMatrixChoice(); };
        CbFormat.SelectionChanged += (_, _) => UpdateCscEnabled();
        BtnApplyFormat.Click += (_, _) => ReloadFocus();
        InitRawOptions();

        BtnChAll.Click += (_, _) => SetChannel(ChannelView.All);
        BtnCh0.Click += (_, _) => ToggleChannel(ChannelView.Ch0);
        BtnCh1.Click += (_, _) => ToggleChannel(ChannelView.Ch1);
        BtnCh2.Click += (_, _) => ToggleChannel(ChannelView.Ch2);
        BtnColorCh.Click += (_, _) => ToggleTint();

        BtnGrid.Click += (_, _) => ToggleGrid();
        NumGridW.ValueChanged += (_, _) => OnGridSizeEdited(fromW: true);
        NumGridH.ValueChanged += (_, _) => OnGridSizeEdited(fromW: false);
        ChkGridSquare.IsCheckedChanged += (_, _) => OnGridSizeEdited(fromW: true);
        BtnPixelInfo.Click += (_, _) => TogglePixelInfo();
        BtnPixelChip.Click += (_, _) => SetPixelInfo(false);
        BtnTitle.Click += (_, _) => ToggleTitle();

        BtnDiff.Click += (_, _) => SetMode(ViewMode.Diff);
        BtnAvg.Click += (_, _) => SetMode(ViewMode.Avg);
        NumDiffTh.ValueChanged += (_, e) => { _diff.Threshold = (int)(e.NewValue ?? 0); RenderAll(); };
        NumDiffGain.ValueChanged += (_, e) => { _diff.Gain = (int)(e.NewValue ?? 1); RenderAll(); };
        ChkDiffBw.IsCheckedChanged += (_, _) => { _diff.BlackWhite = ChkDiffBw.IsChecked == true; RenderAll(); };
        RadDiffMid.IsCheckedChanged += (_, _) => { _diff.MidCenter = RadDiffMid.IsChecked == true; RenderAll(); UpdateSlotLabels(); };
        ChkSync.IsCheckedChanged += (_, _) => { _sync = ChkSync.IsChecked == true; if (_sync) SyncFrom(Shown.View); };

        BtnFirst.Click += (_, _) => GoTo(0);
        BtnPrev.Click += (_, _) => StepFrame(-1);
        BtnNext.Click += (_, _) => StepFrame(+1);
        BtnLast.Click += (_, _) => GoTo(int.MaxValue);
        BtnPlay.Click += (_, _) => TogglePlay();
        BtnLoop.Click += (_, _) => { _loop = !_loop; BtnLoop.Classes.Set("on", _loop); };
        CbFps.ItemsSource = FpsTable.Select(f => f >= 999 ? "max" : f.ToString("0.#")).ToList();
        CbFps.SelectedIndex = 6;
        CbFps.SelectionChanged += (_, _) => ApplyFps();
        Seek.ValueChanged += (_, e) => { if ((int)e.NewValue != _cur) GoTo((int)e.NewValue, fromSlider: true); };

        BtnZoom.AddHandler(PointerPressedEvent, (_, e) => { if (e.ClickCount == 2) { Shown.View.ResetZoom(); e.Handled = true; } }, Avalonia.Interactivity.RoutingStrategies.Tunnel);
        ZoomSlider.ValueChanged += (_, e) => { if (!_zoomSliderBusy) Shown.View.SetZoom(Math.Pow(2, Math.Round(e.NewValue))); };
        BtnFull.Click += (_, _) => ToggleFullScreen();
        BtnHelp.Click += (_, _) => ToggleHelp();
        InitHelp();
    }

    private bool _rawUiSync;
    private void InitRawOptions()
    {
        CbRawMode.SelectionChanged += (_, _) =>
        {
            if (_rawUiSync || CbRawMode.SelectedIndex < 0) return;
            var mode = (Matdol.Codecs.Raw.CameraRawMode)CbRawMode.SelectedIndex;
            AppSettings.Current.RawMode = mode; AppSettings.Save();
            ReloadRawSlot(Shown, mode);
        };
    }

    private void SyncRawGroup()
    {
        _rawUiSync = true;
        try
        {
            if (Shown.Src is CodecFileSource { FileKind: CodecFileSource.Kind.CameraRaw } cs)
            {
                RawGroup.IsEnabled = true;
                RawModeSensor.IsEnabled = RawModeHalf.IsEnabled = cs.RawSensorSupported;
                CbRawMode.SelectedIndex = (int)cs.RawMode;
                string ext = Path.GetExtension(cs.Path).ToLowerInvariant();
                RawInfo.Text = cs.RawSensorSupported
                    ? $"{cs.RawFamilyName} ({ext}): preview · sensor data · half-size RGB"
                    : $"{cs.RawFamilyName} ({ext}): embedded preview only — sensor decoding is available for Adobe DNG, Canon CR2, Nikon NEF";
            }
            else
            {
                RawGroup.IsEnabled = false;
                CbRawMode.SelectedIndex = (int)AppSettings.Current.RawMode;
                RawInfo.Text = "open a camera raw file (.cr2 .nef .arw .dng .raf .orf .rw2 .pef …) in the selected slot to change this";
            }
        }
        finally { _rawUiSync = false; }
    }

    private async void ReloadRawSlot(Slot s, Matdol.Codecs.Raw.CameraRawMode mode)
    {
        if (s.Src is not CodecFileSource { FileKind: CodecFileSource.Kind.CameraRaw } cs || cs.RawMode == mode) return;
        int ticket = ++s.LoadTicket;
        s.View.Busy = true;
        CodecFileSource fresh;
        try { fresh = await System.Threading.Tasks.Task.Run(() => cs.ReopenAs(mode)); }
        catch (Exception ex) { if (ticket == s.LoadTicket) s.View.Busy = false; FlashTitle($"{Path.GetFileName(cs.Path)}: {ex.Message}"); return; }
        if (ticket != s.LoadTicket) return;
        s.View.Busy = false;
        if (!ReferenceEquals(s.Src, cs)) return;
        cs.Dispose(); s.Src = fresh; s.Fmt = fresh.Format; s.Depth = fresh.BitDepth;
        if (fresh.Note != null) FlashTitle(fresh.Note);
        UpdateSeekRange();
        GoTo(Math.Min(_cur, MaxFrames - 1));
        SyncFormatPanelFromFocus();
        UpdateFocusVisuals();
    }

    private FrameLayout SelectedLayout => (FrameLayout)Math.Max(0, CbLayout.SelectedIndex);
    private YuvMatrix SelectedMatrix => (YuvMatrix)Math.Max(0, CbMatrix.SelectedIndex);
    private bool SelectedLimited => CbRange.SelectedIndex == 1;
    private bool _panelSync;

    private static bool IsRawOrEmpty(Slot s) => s.Src == null || s.Src is RawFrameSource;

    private void ApplyLayoutChoice()
    {
        var s = Shown; var l = SelectedLayout;
        if (s.Src == null || l == s.Layout) return;
        if (!IsRawOrEmpty(s) && l.RawFrameMultiplier() != (1, 1)) { SyncFormatPanelFromFocus(); return; }
        s.Layout = l;
        GoTo(_cur);
        SyncFormatPanelFromFocus();
    }

    private void ApplyMatrixChoice()
    {
        var s = Shown;
        if (s.Src == null || !s.Fmt.IsYuv()) return;
        s.Matrix = SelectedMatrix; s.LimitedRange = SelectedLimited;
        GoTo(_cur);
    }

    private void UpdateCscEnabled()
    {
        var s = Shown;
        bool yuv = IsRawOrEmpty(s) ? ((RawPixelFormat)Math.Max(0, CbFormat.SelectedIndex)).IsYuv() : s.Fmt.IsYuv();
        CscGroup.IsEnabled = yuv;
    }

    private bool _gridEditing;
    private int GridW => (int)(NumGridW.Value ?? 8);
    private int GridH => (int)(NumGridH.Value ?? 8);
    private void OnGridSizeEdited(bool fromW)
    {
        if (_gridEditing) return;
        if (ChkGridSquare.IsChecked == true)
        {
            _gridEditing = true;
            if (fromW) NumGridH.Value = NumGridW.Value; else NumGridW.Value = NumGridH.Value;
            _gridEditing = false;
        }
        foreach (var s in _slots) { s.View.GridW = GridW; s.View.GridH = GridH; s.View.InvalidateVisual(); }
    }

    private void SyncSizeBoxes()
    {
        int i = CbSize.SelectedIndex;
        bool user = i == CbSize.ItemCount - 1;
        NumW.IsEnabled = NumH.IsEnabled = user;
        if (i >= 1 && !user) { NumW.Value = SizePreset.Table[i - 1].Width; NumH.Value = SizePreset.Table[i - 1].Height; }
    }

    private void SyncFormatPanelFromFocus()
    {
        _panelSync = true;
        try
        {
            bool raw = IsRawOrEmpty(Shown);
            CbFormat.SelectedIndex = (int)Shown.Fmt;
            CbDepth.SelectedIndex = Math.Max(0, Array.IndexOf(DepthTable, Shown.Depth));
            LayoutSbs.IsEnabled = LayoutTb.IsEnabled = raw;
            CbLayout.SelectedIndex = (int)Shown.Layout;
            CbMatrix.SelectedIndex = (int)Shown.Matrix;
            CbRange.SelectedIndex = Shown.LimitedRange ? 1 : 0;
            if (Shown.Src != null)
            {
                var (mw, mh) = Shown.Layout.RawFrameMultiplier();
                NumW.Value = Shown.Src.Width / mw; NumH.Value = Shown.Src.Height / mh;
            }
            RawFormatGroup.IsEnabled = raw;
            RawFormatNote.IsVisible = !raw;
            UpdateCscEnabled();
            UpdateChannelGlyphs();
        }
        finally { _panelSync = false; }
        SyncRawGroup();
    }

    private bool _pinTop, _pinBottom;

    private void InitHover()
    {
        Root.PointerMoved += (_, _) => ShowPanels();
        Root.PointerExited += (_, _) => HidePanels();
        foreach (var p in new[] { TopPanel, BottomPanel })
        {
            p.PointerEntered += (_, _) => { _pointerOnPanel = true; ShowPanels(); };
            p.PointerExited += (_, _) => { _pointerOnPanel = false; _hideTimer.Start(); };
        }
        _hideTimer.Tick += (_, _) => HidePanels();
        _pinTop = AppSettings.Current.PinTopPanel; _pinBottom = AppSettings.Current.PinBottomPanel;
        BtnPinTop.Click += (_, _) => SetPins(!_pinTop, _pinBottom);
        BtnPinBottom.Click += (_, _) => SetPins(_pinTop, !_pinBottom);
        SetPins(_pinTop, _pinBottom, save: false);
    }

    private void SetPins(bool top, bool bottom, bool save = true)
    {
        _pinTop = top; _pinBottom = bottom;
        BtnPinTop.Classes.Set("on", top); BtnPinBottom.Classes.Set("on", bottom);
        ToolTip.SetTip(BtnPinTop, top ? "pinned — click to auto-hide" : "pin: keep this panel shown");
        ToolTip.SetTip(BtnPinBottom, bottom ? "pinned — click to auto-hide" : "pin: keep this panel shown");
        if (save) { AppSettings.Current.PinTopPanel = top; AppSettings.Current.PinBottomPanel = bottom; AppSettings.Save(); }
        if (!HelpOverlay.IsVisible) { SetPanelShown(TopPanel, top || TopPanel.Opacity > 0); SetPanelShown(BottomPanel, bottom || BottomPanel.Opacity > 0); }
        UpdatePixelChip();
        if (!_pointerOnPanel) _hideTimer.Start();
    }

    private static void SetPanelShown(Border p, bool on) { p.Opacity = on ? 1 : 0; p.IsHitTestVisible = on; }

    private void ShowPanels()
    {
        if (HelpOverlay.IsVisible) return;
        if (_pixelInfoOn) { SetPanelShown(TopPanel, _pinTop); SetPanelShown(BottomPanel, _pinBottom); return; }
        foreach (var p in new[] { TopPanel, BottomPanel }) SetPanelShown(p, true);
        _hideTimer.Stop(); _hideTimer.Start();
    }

    private void HidePanels(bool all = false)
    {
        _hideTimer.Stop();
        if (_pointerOnPanel && !all) return;
        SetPanelShown(TopPanel, !all && _pinTop);
        SetPanelShown(BottomPanel, !all && _pinBottom);
    }

    private void InitDrop()
    {
        AddHandler(DragDrop.DragEnterEvent, (_, e) => e.DragEffects = DragDropEffects.Copy);
        AddHandler(DragDrop.DragOverEvent, (_, e) => e.DragEffects = DragDropEffects.Copy);
        AddHandler(DragDrop.DropEvent, (_, e) =>
        {
            var path = ExtractDroppedPath(e.DataTransfer);
            if (path == null) return;
            if (_split && SlotAt(e.GetPosition(SlotGrid)) is { } idx) FocusSlot(idx);
            OpenPath(path);
            e.Handled = true;
        });
    }

    private static readonly DataFormat<string> UriListFormat = DataFormat.CreateStringPlatformFormat("text/uri-list");

    private static string? ExtractDroppedPath(IDataTransfer data)
    {
        var p = data.TryGetFile()?.TryGetLocalPath();
        if (p != null) return p;

        foreach (var text in new[] { data.TryGetValue(UriListFormat), data.TryGetText() })
        {
            if (string.IsNullOrEmpty(text)) continue;
            foreach (var line in text.Split('\n', StringSplitOptions.RemoveEmptyEntries))
            {
                var t = line.Trim();
                if (t.Length == 0 || t.StartsWith('#')) continue;
                if (Uri.TryCreate(t, UriKind.Absolute, out var uri) && uri.IsFile) return uri.LocalPath;
                if (File.Exists(t)) return t;
            }
        }
        return null;
    }

    private int? SlotAt(Point p)
    {
        foreach (var s in _slots)
            if (s.Box.IsVisible && s.Box.Bounds.Contains(p)) return s.Index;
        return null;
    }

    private async System.Threading.Tasks.Task PickAndOpen()
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = $"Open into slot {_focus + 1}",
            AllowMultiple = false,
            FileTypeFilter = new[]
            {
                FilePickerFileTypes.All,
                new FilePickerFileType("Known formats") { Patterns = KnownPatterns() },
                new FilePickerFileType("YUV raw") { Patterns = new[] { "*.yuv", "*.y" } },
                new FilePickerFileType("YUV4MPEG2 (y4m)") { Patterns = new[] { "*.y4m" } },
                new FilePickerFileType("RGB raw") { Patterns = new[] { "*.rgb", "*.bgr", "*.gbr", "*.raw" } },
                new FilePickerFileType("PNM (ppm/pgm/pbm)") { Patterns = new[] { "*.ppm", "*.pgm", "*.pbm", "*.pnm" } },
                new FilePickerFileType("Images") { Patterns = new[] { "*.bmp", "*.dib", "*.jpg", "*.jpeg", "*.png", "*.webp", "*.gif" } },
                new FilePickerFileType("DPX (SMPTE 268)") { Patterns = new[] { "*.dpx" } },
                new FilePickerFileType("TIFF") { Patterns = new[] { "*.tif", "*.tiff" } },
                new FilePickerFileType("HEIF / HEIC / AVIF (OS decoder)") { Patterns = new[] { "*.heic", "*.heif", "*.hif", "*.avif" } },
                new FilePickerFileType("Camera RAW") { Patterns = Matdol.Codecs.Raw.CameraRaw.Families.SelectMany(f => f.Extensions).Distinct().Select(e => "*" + e).ToArray() },
            }.Concat(DscPlugin.IsAvailable
                ? new[] { new FilePickerFileType("VESA DSC bitstream") { Patterns = new[] { "*.dsc" } } }
                : Array.Empty<FilePickerFileType>()).ToArray()
        });
        var path = files.FirstOrDefault()?.TryGetLocalPath();
        if (path != null) OpenPath(path);
    }

    private static string[] KnownPatterns()
    {
        var l = new List<string> { "*.yuv", "*.y", "*.y4m", "*.rgb", "*.bgr", "*.gbr", "*.raw", "*.bin", "*.ppm", "*.pgm", "*.pbm", "*.pnm", "*.bmp", "*.dib", "*.jpg", "*.jpeg", "*.png", "*.webp", "*.gif", "*.dpx",
                                   "*.tif", "*.tiff", "*.heic", "*.heif", "*.hif", "*.avif" };
        l.AddRange(Matdol.Codecs.Raw.CameraRaw.Families.SelectMany(f => f.Extensions).Select(e => "*" + e));
        if (DscPlugin.IsAvailable) l.Add("*.dsc");
        return l.Distinct().ToArray();
    }

    private sealed record OpenRequest(string Path, FrameLayout Layout, YuvMatrix Matrix, bool Limited,
                                      RawPixelFormat RawFmt, int RawDepth, int W, int H, bool AutoSize);
    private sealed class OpenResult
    {
        public IFrameSource? Src; public RawPixelFormat Fmt; public int Depth;
        public FrameLayout Layout; public YuvMatrix Matrix; public bool Limited; public string? Note;
    }

    private async void OpenPath(string path)
    {
        var s = Shown;
        StopPlay();
        var (mw, mh) = SelectedLayout.RawFrameMultiplier();
        var req = new OpenRequest(path, SelectedLayout, SelectedMatrix, SelectedLimited,
            (RawPixelFormat)Math.Max(0, CbFormat.SelectedIndex), DepthTable[Math.Max(0, CbDepth.SelectedIndex)],
            (int)(NumW.Value ?? 1920) * mw, (int)(NumH.Value ?? 1080) * mh, CbSize.SelectedIndex == 0);
        int ticket = ++s.LoadTicket;
        s.View.Busy = true;
        OpenResult r;
        try { r = await System.Threading.Tasks.Task.Run(() => LoadSource(req)); }
        catch (Exception ex) { r = new OpenResult { Note = "open failed: " + ex.Message }; }
        if (ticket != s.LoadTicket) { r.Src?.Dispose(); return; }
        s.View.Busy = false;
        if (r.Src == null) { FlashTitle(r.Note ?? "open failed"); return; }
        try
        {
            s.Src?.Dispose();
            s.Src = r.Src; s.Frame = null; s.Fmt = r.Fmt; s.Depth = r.Depth;
            s.Layout = r.Layout; s.Matrix = r.Matrix; s.LimitedRange = r.Limited;
            if (s.Index == _focus) SyncFormatPanelFromFocus();
            UpdateSeekRange();
            GoTo(Math.Min(_cur, s.Src.FrameCount - 1));
            if (!_firstImageShown)
            {
                _firstImageShown = true;
                if (AppSettings.Current.FitWindowToFirstImage && !_split) FitWindowToImage(s);
            }
            if (r.Note != null) FlashTitle(r.Note);
        }
        catch (Exception ex) { Title = AppInfo.Name + " — open failed: " + ex.Message; }
        UpdateFocusVisuals();
    }

    private bool _firstImageShown;
    private const double FitMinW = 320, FitMinH = 240, SlotBorder = 2;

    private void FitWindowToImage(Slot s)
    {
        var f = s.Frame;
        if (f == null || WindowState != WindowState.Normal) return;
        var screen = Screens.ScreenFromTopLevel(this) ?? Screens.Primary;
        if (screen == null) return;
        double rs = RenderScaling > 0 ? RenderScaling : 1, ss = screen.Scaling > 0 ? screen.Scaling : rs;
        var wa = screen.WorkingArea;
        var client = ClientSize;
        var frame = FrameSize ?? client;
        double dx = Math.Max(0, frame.Width - client.Width), dy = Math.Max(0, frame.Height - client.Height);
        double maxW = Math.Max(FitMinW, wa.Width / ss - dx), maxH = Math.Max(FitMinH, wa.Height / ss - dy);
        double w = Math.Clamp(Math.Ceiling(f.Width / rs) + 2 * SlotBorder, FitMinW, maxW);
        double h = Math.Clamp(Math.Ceiling(f.Height / rs) + 2 * SlotBorder, FitMinH, maxH);
        Width = w; Height = h;
        int pw = (int)Math.Ceiling((w + dx) * ss), ph = (int)Math.Ceiling((h + dy) * ss);
        var p = Position;
        int x = Math.Clamp(p.X, wa.X, Math.Max(wa.X, wa.Right - pw)), y = Math.Clamp(p.Y, wa.Y, Math.Max(wa.Y, wa.Bottom - ph));
        if (x != p.X || y != p.Y) Position = new PixelPoint(x, y);
    }

    private static OpenResult LoadSource(OpenRequest q)
    {
        var r = new OpenResult { Layout = q.Layout, Matrix = q.Matrix, Limited = q.Limited };
        string path = q.Path;
        void HeaderLayout() { if (r.Layout.RawFrameMultiplier() != (1, 1)) r.Layout = FrameLayout.Progressive; }
        if (Y4mFileSource.IsY4m(path))
        {
            try
            {
                var ys = new Y4mFileSource(path);
                r.Src = ys; r.Fmt = ys.Format; r.Depth = ys.BitDepth;
                if (ys.LimitedRangeHint is { } lim) r.Limited = lim;
                HeaderLayout();
            }
            catch (Exception ex) { r.Note = $"Y4M decode failed ({ex.Message}) → shown as raw"; }
        }
        else if (CodecFileSource.Detect(path) is { } kind)
        {
            try
            {
                var cs = new CodecFileSource(path, kind);
                r.Src = cs; r.Fmt = cs.Format; r.Depth = cs.BitDepth;
                if (cs.Format.IsYuv())
                {
                    if (cs.MatrixHint is { } m) r.Matrix = m;
                    if (cs.LimitedRangeHint is { } lim) r.Limited = lim;
                }
                HeaderLayout();
                r.Note = cs.Note;
            }
            catch (Exception ex)
            {
                r.Note = kind == CodecFileSource.Kind.Dsc && !DscPlugin.IsAvailable
                    ? DscPlugin.MissingReason + " → shown as raw (see help: F1)"
                    : $"{kind.ToString().ToUpperInvariant()} decode failed ({ex.Message}) → shown as raw";
            }
        }
        else if (RawFormatInfo.IsPnmExtension(path) || RawFormatInfo.IsImageExtension(path))
        {
            try
            {
                r.Src = RawFormatInfo.IsPnmExtension(path) ? new PnmFileSource(path) : new ImageFileSource(path);
                r.Fmt = r.Src.Format; r.Depth = r.Src.BitDepth;
                HeaderLayout();
            }
            catch { r.Note = "decode failed → shown as raw"; }
        }
        if (r.Src == null)
        {
            r.Fmt = RawFormatInfo.GuessFromExtension(path, q.RawFmt) ?? q.RawFmt;
            r.Depth = q.RawDepth;
            int w = q.W, h = q.H;
            if (q.AutoSize)
            {
                var (mw, mh) = r.Layout.RawFrameMultiplier();
                var est = SizeEstimator.Estimate(path, r.Fmt, r.Depth, mw, mh);
                if (est != null) (w, h) = est.Value;
                else r.Note = (r.Note != null ? r.Note + " · " : "") + $"Auto size estimation failed → shown as {w}x{h}";
            }
            r.Src = new RawFrameSource(path, r.Fmt, w, h, r.Depth);
        }
        return r;
    }

    private void CloseSlot(int idx)
    {
        var s = _slots[idx];
        if (s.Src == null) return;
        s.Src.Dispose(); s.Src = null; s.Frame = null;
        s.View.Clear();
        if (!_slots.Any(x => x.Loaded)) StopPlay();
        UpdateSeekRange();
        GoTo(Math.Min(_cur, MaxFrames - 1));
        if (idx == _focus) SyncFormatPanelFromFocus();
        PixelInfo.IsVisible = false;
        UpdateFocusVisuals(); UpdateTitle();
    }

    private int _refreshGen;
    private readonly HashSet<Slot> _refreshing = new();

    private async void RefreshFromDisk()
    {
        int gen = ++_refreshGen;
        var order = _slots.Where(x => x.Src != null && (!x.View.Busy || _refreshing.Contains(x)))
                          .OrderBy(x => _split ? x.Index : (x.Index == _focus ? -1 : x.Index))
                          .ToList();
        if (order.Count == 0) return;
        var tickets = new Dictionary<Slot, int>();
        foreach (var x in order) { tickets[x] = ++x.LoadTicket; x.View.Busy = true; _refreshing.Add(x); }

        var failed = new List<string>();
        foreach (var s in order)
        {
            if (gen != _refreshGen) return;
            if (tickets[s] != s.LoadTicket) { _refreshing.Remove(s); continue; }
            var src = s.Src;
            if (src == null) { s.View.Busy = false; _refreshing.Remove(s); continue; }
            int want = _cur;
            IFrameSource? fresh = null; FrameData? raw = null;
            try
            {
                (fresh, raw) = await System.Threading.Tasks.Task.Run(() =>
                {
                    var f = src.Reopen();
                    return (f, f.ReadFrame(Math.Min(want, f.FrameCount - 1)));
                });
            }
            catch { }
            if (tickets[s] != s.LoadTicket || !ReferenceEquals(s.Src, src)) { fresh?.Dispose(); if (gen == _refreshGen) _refreshing.Remove(s); continue; }
            s.View.Busy = false;
            _refreshing.Remove(s);
            if (fresh == null || raw == null) { failed.Add($"{s.Index + 1}: {Path.GetFileName(src.Path)}"); continue; }

            src.Dispose();
            s.Src = fresh;
            if (fresh is not RawFrameSource) { s.Fmt = fresh.Format; s.Depth = fresh.BitDepth; }
            raw.Matrix = s.Matrix; raw.LimitedRange = s.LimitedRange;
            s.Frame = FrameArrange.Arrange(raw, s.Layout);
            UpdateSeekRange();
            if (want != _cur || _cur > MaxFrames - 1) GoTo(Math.Min(_cur, MaxFrames - 1));
            else RenderAll();
            if (s.Index == _focus) SyncFormatPanelFromFocus();
            UpdateTitle();
        }
        if (gen != _refreshGen) return;
        FlashTitle(failed.Count == 0 ? "refreshed" : "refresh failed — " + string.Join(", ", failed));
    }

    private void ReloadFocus()
    {
        if (Shown.Src == null) return;
        if (Shown.Src is RawFrameSource) { Shown.Fmt = (RawPixelFormat)Math.Max(0, CbFormat.SelectedIndex); OpenPath(Shown.Src.Path); }
        else ApplyLayoutChoice();
    }

    private void UpdateSeekRange()
    {
        int max = _slots.Where(s => s.Src != null).Select(s => s.Src!.FrameCount).DefaultIfEmpty(1).Max();
        Seek.Maximum = Math.Max(0, max - 1);
        bool playable = Playable;
        SeekRow.IsVisible = playable;
        foreach (var c in new Control[] { BtnFirst, BtnPrev, BtnPlay, BtnNext, BtnLast, CbFps, BtnLoop }) c.IsEnabled = playable;
        if (!playable) StopPlay();
    }

    private bool Playable => _slots.Any(s => (_split || s.Index == _focus) && s.Src is RawFrameSource { FrameCount: > 1 } or CodecFileSource { FrameCount: > 1 } or Y4mFileSource { FrameCount: > 1 });

    private int MaxFrames => (int)Seek.Maximum + 1;

    private void GoTo(int index, bool fromSlider = false)
    {
        _cur = Math.Clamp(index, 0, Math.Max(0, MaxFrames - 1));
        foreach (var s in _slots)
        {
            if (s.Src == null) { s.Frame = null; continue; }
            var raw = s.Src.ReadFrame(Math.Min(_cur, s.Src.FrameCount - 1));
            raw.Matrix = s.Matrix; raw.LimitedRange = s.LimitedRange;
            s.Frame = FrameArrange.Arrange(raw, s.Layout);
        }
        RenderAll();
        if (!fromSlider) Seek.Value = _cur;
        FrameLabel.Text = $"{_cur} / {MaxFrames - 1}";
        UpdateTitle();
        if (_showTitle) UpdateFocusVisuals();
    }

    private void StepFrame(int delta, bool wrap = false, bool stopAtEnd = false)
    {
        int n = _cur + delta, cnt = MaxFrames;
        if (n >= cnt) { if (wrap) n = 0; else if (stopAtEnd) { StopPlay(); return; } else n = cnt - 1; }
        if (n < 0) n = wrap ? cnt - 1 : 0;
        GoTo(n);
    }

    private void RenderAll()
    {
        foreach (var s in _slots) if (_split || s.Index == _focus) RenderSlot(s);
    }

    private void RenderSlot(Slot s)
    {
        var self = s.Frame;
        if (self == null) { s.RenderGen++; s.Pending = null; s.View.Clear(); s.View.SplitX = s.View.SplitY = 0; return; }
        (s.View.SplitX, s.View.SplitY) = s.Layout.Splits(self.Width, self.Height);
        var r = _mode != ViewMode.Normal && s.Index != _b ? BSlot.Frame : null;
        var ch = _channel; var cc = _tint;
        FillBgraDelegate fill;
        if (r != null)
        {
            var ds = _diff.Clone();
            ds.Mode = _mode == ViewMode.Diff ? DiffMode.Diff : DiffMode.Avg;
            fill = dst => DiffRenderer.ToBgra(self, r, ds, ch, cc, dst);
        }
        else fill = dst => self.ToBgra(dst, ch, cc);
        if (r == null && (long)self.Width * self.Height < HeavyPixels)
        {
            s.RenderGen++; s.Pending = null;
            s.View.SetFrame(self.Width, self.Height, fill);
            return;
        }
        s.Pending = new RenderJob(self.Width, self.Height, fill);
        if (!s.Rendering) PumpRender(s);
    }

    private async void PumpRender(Slot s)
    {
        s.Rendering = true;
        var spin = new DispatcherTimer { Interval = SpinDelay };
        spin.Tick += (_, _) => { if (!_playTimer.IsEnabled) { spin.Stop(); s.View.Working = true; } };
        spin.Start();
        try
        {
            while (s.Pending is { } job)
            {
                s.Pending = null;
                int gen = ++s.RenderGen;
                int n = job.W * job.H * 4;
                var buf = s.RenderBuf is { } old && old.Length == n ? old : (s.RenderBuf = new byte[n]);
                try { await System.Threading.Tasks.Task.Run(() => job.Fill(buf)); }
                catch (Exception ex) { FlashTitle($"render failed — {ex.Message}"); continue; }
                if (gen != s.RenderGen) continue;
                s.View.SetFrame(job.W, job.H, dst => buf.AsSpan(0, Math.Min(n, dst.Length)).CopyTo(dst));
            }
        }
        finally { spin.Stop(); s.View.Working = false; s.Rendering = false; }
    }

    private void SetMode(ViewMode m)
    {
        _mode = _mode == m ? ViewMode.Normal : m;
        BtnDiff.Classes.Set("on", _mode == ViewMode.Diff);
        BtnAvg.Classes.Set("on", _mode == ViewMode.Avg);
        RenderAll(); UpdateTitle(); UpdateSlotLabels();
    }

    private void SetChannel(ChannelView ch)
    {
        _channel = ch;
        BtnChAll.Classes.Set("on", ch == ChannelView.All);
        BtnCh0.Classes.Set("on", ch == ChannelView.Ch0);
        BtnCh1.Classes.Set("on", ch == ChannelView.Ch1);
        BtnCh2.Classes.Set("on", ch == ChannelView.Ch2);
        BtnColorCh.IsEnabled = ch != ChannelView.All;
        RenderAll();
    }

    private void ToggleChannel(ChannelView ch) => SetChannel(_channel == ch ? ChannelView.All : ch);

    private void ToggleTint()
    {
        if (_channel == ChannelView.All) return;
        _tint = !_tint; BtnColorCh.Classes.Set("on", _tint); RenderAll();
    }

    private void UpdateChannelGlyphs()
    {
        bool yuv = Shown.Fmt.IsYuv() || Shown.Fmt.IsGray();
        GlyphCh0.Text = yuv ? "Y" : "R"; GlyphCh1.Text = yuv ? "U" : "G"; GlyphCh2.Text = yuv ? "V" : "B";
    }

    private void SyncFrom(ImageView src)
    {
        if (!_sync || _syncing) return;
        _syncing = true;
        var (z, ox, oy) = src.GetView();
        foreach (var s in _slots) if (s.View != src) s.View.ApplyView(z, ox, oy);
        _syncing = false;
    }

    private void UpdateTitle()
    {
        var s = Shown;
        string who = AbTag(s.Index) is { Length: > 0 } t ? $" ({t})" : "";
        if (s.Src == null) { Title = $"{AppInfo.Name} — slot {s.Index + 1}{who} empty"; return; }
        string mode = _mode == ViewMode.Normal ? "" : _mode == ViewMode.Diff ? "  [diff |A−B|]" : "  [avg (A+B)/2]";
        string lay = (s.Layout == FrameLayout.Progressive ? "" : ", " + s.Layout.ShortName())
                   + (s.Fmt.IsYuv() ? $", {s.Matrix.ShortName()} {(s.LimitedRange ? "limited" : "full")}" : "");
        string fmt = s.Src is CodecFileSource cs ? cs.Summary : s.Src is Y4mFileSource ys ? ys.Summary : $"{s.Fmt.DisplayName()}, {s.Depth}bit";
        Title = $"[{_cur}/{MaxFrames - 1}]  {s.Index + 1}{who}: {Path.GetFileName(s.Src.Path)}{mode}  (B=slot {_b + 1})  ({s.Src.Width}x{s.Src.Height}, {fmt}{lay})  {ZoomText()}";
    }

    private string AbTag(int idx) => (idx == _focus ? "A" : "") + (idx == _b ? "B" : "");

    private string ZoomText() => ZoomText(Shown.View.Zoom);
    private static string ZoomText(double z) => z >= 1 ? $"{z:0}X" : $"1/{1 / z:0}X";
    private bool _zoomSliderBusy;
    private void UpdateZoomLabel()
    {
        BtnZoom.Content = ZoomText(); ZoomLabel.Text = "ZOOM " + ZoomText();
        _zoomSliderBusy = true; ZoomSlider.Value = Math.Round(Math.Log2(Shown.View.Zoom)); _zoomSliderBusy = false;
    }

    private void ApplyFps()
    {
        double fps = FpsTable[Math.Max(0, CbFps.SelectedIndex)];
        _playTimer.Interval = TimeSpan.FromMilliseconds(fps >= 999 ? 1 : 1000.0 / fps);
    }

    private void TogglePlay(bool? loop = null)
    {
        if (MaxFrames <= 1) return;
        if (loop.HasValue) { _loop = loop.Value; BtnLoop.Classes.Set("on", _loop); }
        if (_playTimer.IsEnabled) StopPlay();
        else { ApplyFps(); _playTimer.Start(); PlayIcon.Data = (Geometry)Resources["IcPause"]!; }
    }

    private void StopPlay() { _playTimer.Stop(); PlayIcon.Data = (Geometry)Resources["IcPlay"]!; }

    private bool _grid;
    private void ToggleGrid() { _grid = !_grid; BtnGrid.Classes.Set("on", _grid); foreach (var s in _slots) { s.View.ShowGrid = _grid; s.View.InvalidateVisual(); } }
    private void ToggleTitle() { _showTitle = !_showTitle; BtnTitle.Classes.Set("on", _showTitle); UpdateFocusVisuals(); }
    private void ToggleHelp() => ShowHelp(!HelpOverlay.IsVisible);
    private void TogglePixelInfo() => SetPixelInfo(!_pixelInfoOn);
    private void SetPixelInfo(bool on)
    {
        _pixelInfoOn = on;
        BtnPixelInfo.Classes.Set("on", on);
        if (!on) PixelInfo.IsVisible = false;
        if (on)
        {
            _pointerOnPanel = false; _hideTimer.Stop();
            SetPanelShown(TopPanel, _pinTop); SetPanelShown(BottomPanel, _pinBottom);
        }
        UpdatePixelChip();
    }

    private void UpdatePixelChip() => BtnPixelChip.IsVisible = _pixelInfoOn && !_pinBottom && !HelpOverlay.IsVisible;

    private void ToggleFullScreen()
    {
        if (WindowState == WindowState.FullScreen) WindowState = _stateBeforeFull;
        else { _stateBeforeFull = WindowState; WindowState = WindowState.FullScreen; }
        BtnFull.Classes.Set("on", WindowState == WindowState.FullScreen);
    }

    private static readonly IBrush PixelNumBrush = new SolidColorBrush(Color.Parse("#FFB84D"));
    private static readonly IBrush PixelDecBrush = new SolidColorBrush(Color.Parse("#8FE3FF"));
    private static readonly IBrush PixelHexBrush = new SolidColorBrush(Color.Parse("#5FB3CF"));

    private void OnHoverPixel(Slot hover, (int x, int y)? p, Point pt)
    {
        var f = hover.Frame;
        if (!_pixelInfoOn || p == null || f == null) { PixelInfo.IsVisible = false; return; }
        var (x, y) = p.Value;
        var loc = FrameArrange.Locate(hover.Layout, f.Width, f.Height, x, y);
        var (bx, by, bn) = FrameArrange.Block(loc, hover.View.GridW, hover.View.GridH);
        const string Sep = ",  ";
        var head = new Avalonia.Controls.Documents.InlineCollection();
        void Txt(string t) => head.Add(new Avalonia.Controls.Documents.Run(t));
        void Num(int v) => head.Add(new Avalonia.Controls.Documents.Run(v.ToString()) { Foreground = PixelNumBrush });
        void Coord(string label, int a, int b) { Txt($"{label} = ("); Num(a); Txt(", "); Num(b); Txt(")"); }
        string size = $"{f.Width}x{f.Height}";
        switch (hover.Layout)
        {
            case FrameLayout.StereoSideBySide or FrameLayout.StereoTopBottom:
                Txt($"• Pel: [{f.BitDepth}bit | img_size = {loc.RegionW}x{loc.RegionH} ({loc.Region} of {size})]{Sep}");
                Coord("(px,py)", loc.RegionX, loc.RegionY);
                break;
            case FrameLayout.FieldSeparated:
                Txt($"• Pel: [{f.BitDepth}bit | img_size = {size} | {loc.Region} field]{Sep}");
                Coord("(px,py)", loc.StoredX, loc.StoredY); Txt(Sep);
                Coord("field (px,py)", loc.RegionX, loc.RegionY);
                break;
            default:
                Txt($"• Pel: [{f.BitDepth}bit | img_size = {size}]{Sep}");
                Coord("(px,py)", x, y);
                break;
        }
        head.Add(new Avalonia.Controls.Documents.LineBreak());
        Txt($"• Blk: [blk_size = {hover.View.GridW}x{hover.View.GridH}]{Sep}");
        Coord("(bx,by)", bx, by);
        Txt($"{Sep}#blk_index = {bn}");
        PixelHead.Inlines = head;

        var body = new Avalonia.Controls.Documents.InlineCollection();
        bool first = true;
        void Plain(string t) => body.Add(new Avalonia.Controls.Documents.Run(t));
        void Colored(string t, IBrush brush) => body.Add(new Avalonia.Controls.Documents.Run(t) { Foreground = brush });
        void NewLine() { if (!first) body.Add(new Avalonia.Controls.Documents.LineBreak()); first = false; }

        var bf = BSlot.Frame;
        foreach (var s in _slots)
        {
            var sf = s.Frame;
            if (sf == null) continue;
            string who = AbTag(s.Index).PadRight(2);
            if (x >= sf.Width || y >= sf.Height) { NewLine(); Plain($"• {s.Index + 1}{who} (outside {sf.Width}x{sf.Height})"); continue; }
            int hd = sf.HexDigits, wd = sf.MaxValue > 255 ? 5 : 3;
            string hex(int v) => v.ToString("x" + hd);
            var c = sf.GetPixel(x, y);
            string tag = sf.Format.IsGray() ? "Gray" : sf.Format.IsYuv() ? "YUV " : "RGB ";
            string dec = sf.Format.IsGray()
                ? $"[{c.c0.ToString().PadLeft(wd)}]"
                : $"[{c.c0.ToString().PadLeft(wd)},{c.c1.ToString().PadLeft(wd)},{c.c2.ToString().PadLeft(wd)}]";
            string hx = sf.Format.IsGray() ? $"(0x{hex(c.c0)})" : $"(0x{hex(c.c0)},{hex(c.c1)},{hex(c.c2)})";
            NewLine();
            Plain($"• {s.Index + 1}{who} {tag}");
            if (s.Index == hover.Index) { Colored(dec, PixelDecBrush); Plain(" "); Colored(hx, PixelHexBrush); }
            else Plain(dec + " " + hx);
            string line = "";
            if (bf != null && s.Index != _b)
            {
                var plan = new ComparePlan(sf, bf);
                if (plan.Contains(x, y))
                {
                    var a = plan.Sample(sf, x, y); var b = plan.Sample(bf, x, y);
                    line += _diff.MidCenter
                        ? $"  Δ[{a.c0 - b.c0:+0;-0;0},{a.c1 - b.c1:+0;-0;0},{a.c2 - b.c2:+0;-0;0}]"
                        : $"  Δ[{Math.Abs(a.c0 - b.c0)},{Math.Abs(a.c1 - b.c1)},{Math.Abs(a.c2 - b.c2)}]";
                    if (sf.Format != bf.Format || sf.MaxValue != bf.MaxValue) line += $" ({plan.Describe()})";
                }
            }
            if (line.Length > 0) Plain(line);
        }
        if (f.Format.IsYuv())
        {
            var (r, gg, b) = f.GetDisplayRgb(x, y);
            NewLine();
            Plain("• → RGB "); Colored($"[{r,3},{gg,3},{b,3}]", PixelDecBrush); Plain(" "); Colored($"(0x{r:x2},{gg:x2},{b:x2})", PixelHexBrush);
            Plain($"  8bit  {f.Matrix.ShortName()} {(f.LimitedRange ? "limited" : "full")}");
        }
        PixelText.Inlines = body;

        const int N = 5, H = N / 2;
        var zoom = new WriteableBitmap(new PixelSize(N, N), new Vector(96, 96), PixelFormat.Bgra8888, AlphaFormat.Opaque);
        using (var fb = zoom.Lock())
        {
            unsafe
            {
                byte* d = (byte*)fb.Address;
                for (int dy = -H; dy <= H; dy++)
                    for (int dx = -H; dx <= H; dx++)
                    {
                        var (r, gg, b) = f.GetDisplayRgb(x + dx, y + dy);
                        byte* q = d + (dy + H) * fb.RowBytes + (dx + H) * 4;
                        q[0] = b; q[1] = gg; q[2] = r; q[3] = 255;
                    }
            }
        }
        PixelZoom.Source = zoom;
        RenderOptions.SetBitmapInterpolationMode(PixelZoom, BitmapInterpolationMode.None);

        PixelInfo.IsVisible = true;
        PlacePixelInfo(hover.View.TranslatePoint(pt, Root) ?? pt);
    }

    private const double PixelInfoAnchor = 0.40, PixelInfoGapBelow = 24, PixelInfoGapAbove = 10, PixelInfoSlack = 2;
    private Point _pixelInfoCursor;
    private void PlacePixelInfo(Point cursor)
    {
        _pixelInfoCursor = cursor;
        foreach (Control t in new Control[] { PixelHead, PixelText, PixelZoom })
            for (Control? c = t; c != null && c != Root; c = c.Parent as Control) c.InvalidateMeasure();
        PixelInfo.Measure(Size.Infinity);
        var m = PixelInfo.Margin;
        double w = PixelInfo.DesiredSize.Width - m.Left - m.Right, h = PixelInfo.DesiredSize.Height - m.Top - m.Bottom + PixelInfoSlack;
        double maxX = Math.Max(0, Root.Bounds.Width - w), maxY = Math.Max(0, Root.Bounds.Height - h);
        double px = Math.Clamp(cursor.X - w * PixelInfoAnchor, 0, maxX);
        double py = cursor.Y + PixelInfoGapBelow;
        if (py > maxY) py = cursor.Y - h - PixelInfoGapAbove;
        bool Shown(Border p) => !HelpOverlay.IsVisible && (!_pixelInfoOn || (p == TopPanel ? _pinTop : _pinBottom));
        var bottom = BottomPanel.Bounds.Inflate(4); var top = TopPanel.Bounds.Inflate(4);
        if (Shown(BottomPanel) && new Rect(px, py, w, h).Intersects(bottom)) py = Math.Min(cursor.Y, bottom.Top) - h - PixelInfoGapAbove;
        if (Shown(TopPanel) && new Rect(px, py, w, h).Intersects(top)) py = Math.Max(py, top.Bottom + 2);
        PixelInfo.Margin = new Thickness(Math.Floor(px), Math.Floor(Math.Clamp(py, 0, maxY)), 0, 0);
    }

    private async System.Threading.Tasks.Task Capture(bool toFile = false)
    {
        var target = SlotGrid;
        double scale = RenderScaling;
        int fw = (int)Math.Round(target.Bounds.Width * scale), fh = (int)Math.Round(target.Bounds.Height * scale);
        if (fw <= 0 || fh <= 0) return;

        using var rtb = new RenderTargetBitmap(new PixelSize(fw, fh), new Vector(96 * scale, 96 * scale));
        rtb.Render(target);
        var full = new byte[fw * fh * 4];
        unsafe { fixed (byte* p = full) rtb.CopyPixels(new PixelRect(0, 0, fw, fh), (IntPtr)p, full.Length, fw * 4); }

        var tiles = new List<(int row, int col, PixelRect r)>();
        foreach (var s in _slots)
        {
            if (!s.Box.IsVisible || (!_split && s.Index != _focus)) continue;
            if (s.View.VisibleImageRect() is not { } vr || s.View.TranslatePoint(vr.TopLeft, target) is not { } tl) continue;
            int x0 = Math.Clamp((int)Math.Round(tl.X * scale), 0, fw), y0 = Math.Clamp((int)Math.Round(tl.Y * scale), 0, fh);
            int x1 = Math.Clamp((int)Math.Round((tl.X + vr.Width) * scale), 0, fw), y1 = Math.Clamp((int)Math.Round((tl.Y + vr.Height) * scale), 0, fh);
            if (x1 > x0 && y1 > y0) tiles.Add((_split ? s.Index / 2 : 0, _split ? s.Index % 2 : 0, new PixelRect(x0, y0, x1 - x0, y1 - y0)));
        }
        if (tiles.Count == 0) { FlashTitle("nothing to capture (no image)"); return; }

        var cols = tiles.Select(t => t.col).Distinct().OrderBy(c => c).ToList();
        var rows = tiles.Select(t => t.row).Distinct().OrderBy(r => r).ToList();
        int gap = Math.Max(1, (int)Math.Round(2 * scale));
        var colW = cols.ToDictionary(c => c, c => tiles.Where(t => t.col == c).Max(t => t.r.Width));
        var rowH = rows.ToDictionary(r => r, r => tiles.Where(t => t.row == r).Max(t => t.r.Height));
        var colX = new Dictionary<int, int>(); int acc = 0;
        foreach (var c in cols) { colX[c] = acc; acc += colW[c] + gap; }
        int w = acc - gap;
        var rowY = new Dictionary<int, int>(); acc = 0;
        foreach (var r in rows) { rowY[r] = acc; acc += rowH[r] + gap; }
        int h = acc - gap;

        var bgra = new byte[w * h * 4];
        var sep = Color.Parse("#141E21");
        for (int i = 0; i < bgra.Length; i += 4) { bgra[i] = sep.B; bgra[i + 1] = sep.G; bgra[i + 2] = sep.R; }
        foreach (var (row, col, r) in tiles)
            for (int y = 0; y < r.Height; y++)
                Buffer.BlockCopy(full, ((r.Y + y) * fw + r.X) * 4, bgra, ((rowY[row] + y) * w + colX[col]) * 4, r.Width * 4);
        for (int i = 3; i < bgra.Length; i += 4) bgra[i] = 255;

        if (toFile) { await SaveCapture(bgra, w, h); return; }

        if (OperatingSystem.IsWindows()) { Win32Clipboard.SetDib(bgra, w, h); FlashTitle("captured to clipboard"); return; }

        try
        {
            var cb = Clipboard ?? throw new InvalidOperationException();
            using var bmp = ToBitmap(bgra, w, h);
            var item = new DataTransferItem();
            item.Set(DataFormat.Bitmap, bmp);
            var transfer = new DataTransfer();
            transfer.Add(item);
            await cb.SetDataAsync(transfer);
            FlashTitle("captured to clipboard");
        }
        catch { await SaveCapture(bgra, w, h); }
    }

    private static Bitmap ToBitmap(byte[] bgra, int w, int h)
    {
        unsafe
        {
            fixed (byte* p = bgra)
                return new Bitmap(PixelFormat.Bgra8888, AlphaFormat.Opaque, (IntPtr)p, new PixelSize(w, h), new Vector(96, 96), w * 4);
        }
    }

    private static byte[] EncodePng(byte[] bgra, int w, int h)
    {
        using var bmp = new SKBitmap(new SKImageInfo(w, h, SKColorType.Bgra8888, SKAlphaType.Opaque));
        System.Runtime.InteropServices.Marshal.Copy(bgra, 0, bmp.GetPixels(), bgra.Length);
        using var data = bmp.Encode(SKEncodedImageFormat.Png, 100);
        return data.ToArray();
    }

    private async System.Threading.Tasks.Task SaveCapture(byte[] bgra, int w, int h)
    {
        var name = Shown.Src != null ? Path.GetFileNameWithoutExtension(Shown.Src.Path) + $"_{_cur}" : "capture";
        var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "Save capture", DefaultExtension = "png", SuggestedFileName = name + ".png",
            FileTypeChoices = new[] { new FilePickerFileType("PNG") { Patterns = new[] { "*.png" } }, new FilePickerFileType("BMP") { Patterns = new[] { "*.bmp" } } }
        });
        var path = file?.TryGetLocalPath(); if (path == null) return;
        using var bmp = new SKBitmap(new SKImageInfo(w, h, SKColorType.Bgra8888, SKAlphaType.Opaque));
        System.Runtime.InteropServices.Marshal.Copy(bgra, 0, bmp.GetPixels(), bgra.Length);
        var fmt = path.EndsWith(".bmp", StringComparison.OrdinalIgnoreCase) ? SKEncodedImageFormat.Bmp : SKEncodedImageFormat.Png;
        using var data = bmp.Encode(fmt, 100);
        await using var fs = File.Create(path);
        data.SaveTo(fs);
    }

    private void FlashTitle(string msg)
    {
        var t = new DispatcherTimer { Interval = TimeSpan.FromSeconds(Math.Clamp(msg.Length / 18.0, 1.5, 6)) };
        Title = AppInfo.Name + " — " + msg;
        t.Tick += (_, _) => { t.Stop(); UpdateTitle(); };
        t.Start();
    }

    protected override void OnKeyUp(KeyEventArgs e)
    {
        if (e.Key is Key.LeftShift or Key.RightShift) _shiftDown = false;
        base.OnKeyUp(e);
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.Key is Key.LeftShift or Key.RightShift) { _shiftDown = true; base.OnKeyDown(e); return; }
        bool ctrl = e.KeyModifiers.HasFlag(KeyModifiers.Control);
        var v = Shown.View; var vc = new Point(v.Bounds.Width / 2, v.Bounds.Height / 2);
        e.Handled = true;
        bool frameKey = e.Key is Key.Space or Key.Right or Key.Left or Key.PageDown or Key.PageUp or Key.Home or Key.End
                        || (e.Key == Key.Enter && !ctrl) || (ctrl && e.Key is Key.Up or Key.Down);
        if (frameKey && !Playable) { e.Handled = false; base.OnKeyDown(e); return; }
        switch (e.Key)
        {
            case Key.Enter when ctrl: ToggleFullScreen(); break;
            case Key.Enter: TogglePlay(loop: true); break;
            case Key.Space: TogglePlay(); break;
            case Key.Escape:
                if (HelpOverlay.IsVisible) ShowHelp(false);
                else if (_pixelInfoOn) SetPixelInfo(false);
                else if (WindowState == WindowState.FullScreen) ToggleFullScreen();
                break;
            case Key.F1: ToggleHelp(); break;
            case Key.F5: RefreshFromDisk(); break;
            case Key.O when ctrl: _ = PickAndOpen(); break;
            case Key.C when ctrl: _ = Capture(toFile: e.KeyModifiers.HasFlag(KeyModifiers.Shift)); break;
            case Key.Tab when ctrl: ToggleLayout(); break;
            case Key.D0 or Key.NumPad0 when ctrl: SetChannel(ChannelView.All); break;
            case Key.D1 or Key.NumPad1 when ctrl: ToggleChannel(ChannelView.Ch0); break;
            case Key.D2 or Key.NumPad2 when ctrl: ToggleChannel(ChannelView.Ch1); break;
            case Key.D3 or Key.NumPad3 when ctrl: ToggleChannel(ChannelView.Ch2); break;
            case Key.T when ctrl: ToggleTint(); break;
            case Key.D1 or Key.NumPad1: FocusSlot(0); break;
            case Key.D2 or Key.NumPad2: FocusSlot(1); break;
            case Key.D3 or Key.NumPad3: FocusSlot(2); break;
            case Key.D4 or Key.NumPad4: FocusSlot(3); break;
            case Key.OemMinus or Key.Subtract or Key.D0 or Key.NumPad0: SetMode(ViewMode.Diff); break;
            case Key.OemPlus or Key.Add: SetMode(ViewMode.Avg); break;
            case Key.W: ChkDiffBw.IsChecked = ChkDiffBw.IsChecked != true; break;
            case Key.I: ToggleTitle(); break;
            case Key.G: ToggleGrid(); break;
            case Key.P: TogglePixelInfo(); break;
            case Key.Right when ctrl: GoTo(int.MaxValue); break;
            case Key.Left when ctrl: GoTo(0); break;
            case Key.Right: StepFrame(+1); break;
            case Key.Left: StepFrame(-1); break;
            case Key.PageDown: StepFrame(+10); break;
            case Key.PageUp: StepFrame(-10); break;
            case Key.Down when ctrl: StepFrame(+10); break;
            case Key.Up when ctrl: StepFrame(-10); break;
            case Key.Home: GoTo(0); break;
            case Key.End: GoTo(int.MaxValue); break;
            case Key.Up: v.ZoomAt(vc, 2); break;
            case Key.Down: v.ZoomAt(vc, 0.5); break;
            default: e.Handled = false; break;
        }
        if (!e.Handled) base.OnKeyDown(e);
    }

    private static readonly (string key, string what)[] HelpItems =
    {
        ("F1", "help (show / hide)"),
        ("F5", "refresh"),
        ("Ctrl+O", "open file into the selected slot (A)"),
        ("double-click image", "open file into that slot"),
        ("drop a file", "open it (split: into the window you drop on)"),
        ("right-click image", "menu: set as B · open file here · close image"),
        ("click image", "select slot (A)"),
        ("1  2  3  4", "select slot (A)"),
        ("Ctrl+click image", "set as compare base (B)"),
        ("Ctrl+Tab", "solo ↔ 2x2 split"),
        ("−  or  0", "diff view |A − B|  (split: every slot vs B · again: normal)"),
        ("compare settings", "threshold · gain · B/W · diff center: 0 → |A − B|·gain,  mid → (A − B)·gain + mid (8bit: 128, clipped to 0 … max)"),
        ("+", "average view (A + B)/2  (different size / bit depth / color space: overlap from (0,0), lower bit depth, RGB if YUV↔RGB)"),
        ("W", "B/W binary diff"),
        ("Ctrl+0", "all channels (full color)"),
        ("Ctrl+1  2  3", "single channel: Y / U / V  or  R / G / B  (same key again: all)"),
        ("Ctrl+T", "tint single channel: its own color ↔ gray"),
        ("G", "block grid  (right-click the grid button: width × height)"),
        ("I", "info overlay: per window file name, frame, size, format, bit depth, zoom, A/B tags"),
        ("pin (panel corner)", "keep the top / bottom panel shown ↔ auto-hide (remembered)"),
        ("P", "pixel info on / off  (while on, unpinned panels stay hidden)"),
        ("Space", "play / pause"),
        ("Enter", "play & repeat / pause"),
        ("←  /  →", "previous / next frame"),
        ("PgUp  /  PgDn", "−10 / +10 frames"),
        ("Ctrl+↑  /  Ctrl+↓", "−10 / +10 frames"),
        ("Home  /  End", "first / last frame"),
        ("Ctrl+←  /  Ctrl+→", "first / last frame"),
        ("↑  /  ↓", "zoom in / out"),
        ("mouse wheel", "zoom in / out at the pointer"),
        ("drag", "pan (synced across slots)"),
        ("Ctrl+C", "capture the image area to clipboard"),
        ("Ctrl+Shift+C", "capture the image area to a file"),
        ("Ctrl+Enter", "fullscreen"),
        ("Esc", "close help · pixel info off · leave fullscreen"),
    };

    private const double DonateHoverOpacity = 0.4;

    private void InitHelp()
    {
        HelpSpinner.Frames = AppInfo.HelpFrames;
        HelpSpinner.Turn = AppInfo.SlowTurn;
        HelpLabel.Text = new MillText(AppInfo.LabelName, AppInfo.LabelTail, 0.8);
        HelpVersion.Text = AppInfo.VersionText;
        HelpTagline.Text = AppInfo.Tagline;

        var mono = new FontFamily("Consolas,Menlo,DejaVu Sans Mono,monospace");
        int row = 0;
        foreach (var (key, what) in HelpItems)
        {
            HelpGrid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
            var k = new TextBlock { Text = key, FontFamily = mono, FontSize = 12, Foreground = new SolidColorBrush(Color.Parse("#7CC8FF")), Margin = new Thickness(0, 2, 18, 2) };
            var w = new TextBlock { Text = what, FontSize = 12, Foreground = new SolidColorBrush(Color.Parse("#E6ECEF")), Margin = new Thickness(0, 2), TextWrapping = TextWrapping.Wrap };
            Grid.SetRow(k, row); Grid.SetColumn(k, 0);
            Grid.SetRow(w, row); Grid.SetColumn(w, 1);
            HelpGrid.Children.Add(k); HelpGrid.Children.Add(w);
            row++;
        }
        HelpNote.Text = "A = selected slot (files open here, shown in solo) · B = compare base";
        Link(HelpNotesLink, () => NotesWindow.ShowFor(this));
        HelpSplashOpt.IsChecked = AppSettings.Current.ShowSplash;
        HelpSplashOpt.IsCheckedChanged += (_, _) => { AppSettings.Current.ShowSplash = HelpSplashOpt.IsChecked == true; AppSettings.Save(); };
        HelpFitOpt.IsChecked = AppSettings.Current.FitWindowToFirstImage;
        HelpFitOpt.IsCheckedChanged += (_, _) => { AppSettings.Current.FitWindowToFirstImage = HelpFitOpt.IsChecked == true; AppSettings.Save(); };
        HelpDonate.IsVisible = AppInfo.ShowDonate;
        double donateIdle = HelpDonate.Opacity;
        HelpDonate.PointerEntered += (_, _) => HelpDonate.Opacity = DonateHoverOpacity;
        HelpDonate.PointerExited += (_, _) => HelpDonate.Opacity = donateIdle;
        HelpDonate.PointerPressed += (_, e) => { DonateWindow.ShowFor(this, OpenUrl); e.Handled = true; };

        var legalLinks = new (string Text, LegalWindow.Page Page)[]
        {
            ("license (MIT)", LegalWindow.Page.License),
            ("third-party licenses", LegalWindow.Page.Licenses),
            ("privacy", LegalWindow.Page.Privacy),
        };
        IBrush linkIdle = new SolidColorBrush(Color.Parse("#6F7C84")), linkHover = new SolidColorBrush(Color.Parse("#8FA9BA"));
        for (int i = 0; i < legalLinks.Length; i++)
        {
            if (i > 0) HelpLegal.Children.Add(new TextBlock { Text = "·", FontSize = 10.5, Foreground = linkIdle });
            var (text, page) = legalLinks[i];
            var link = new TextBlock { Text = text, FontSize = 10.5, Foreground = linkIdle, Cursor = new Cursor(StandardCursorType.Hand) };
            link.PointerEntered += (_, _) => { link.Foreground = linkHover; link.TextDecorations = TextDecorations.Underline; };
            link.PointerExited += (_, _) => { link.Foreground = linkIdle; link.TextDecorations = null; };
            link.PointerPressed += (_, e) => { LegalWindow.ShowPage(this, page); e.Handled = true; };
            HelpLegal.Children.Add(link);
        }

        HelpOverlay.PointerPressed += (_, e) =>
        {
            if (!HelpCard.IsPointerOver) { ShowHelp(false); e.Handled = true; }
        };
        Root.SizeChanged += (_, _) => HelpCard.MaxHeight = Math.Max(240, Root.Bounds.Height - 60);
    }

    private static void Link(TextBlock t, Action action)
    {
        t.PointerEntered += (_, _) => t.TextDecorations = TextDecorations.Underline;
        t.PointerExited += (_, _) => t.TextDecorations = null;
        t.PointerPressed += (_, e) => { action(); e.Handled = true; };
    }

    private async void OpenUrl(string url)
    {
        try { if (await Launcher.LaunchUriAsync(new Uri(url))) return; } catch { }
        try
        {
            var psi = OperatingSystem.IsWindows() ? new System.Diagnostics.ProcessStartInfo(url) { UseShellExecute = true }
                    : new System.Diagnostics.ProcessStartInfo(OperatingSystem.IsMacOS() ? "open" : "xdg-open", url);
            System.Diagnostics.Process.Start(psi);
        }
        catch { FlashTitle(url); }
    }

    private void ShowHelp(bool show)
    {
        HelpOverlay.IsVisible = show;
        if (show) { _pointerOnPanel = false; HidePanels(all: true); }
        else HidePanels();
        UpdatePixelChip();
        if (show) { HelpCard.MaxHeight = Math.Max(240, Root.Bounds.Height - 60); HelpSpinner.Start(); StartMilk(); }
        else { HelpSpinner.Stop(); _milkTimer?.Stop(); }
    }

    private readonly SoyMilkSim _milk = new();
    private DispatcherTimer? _milkTimer;
    private readonly System.Diagnostics.Stopwatch _milkClock = new();
    private double _milkLast;
    private static readonly Point[] MillTips = { new(79, 99.5), new(39.5, 95), new(65, 95) };

    private void StartMilk()
    {
        HelpMilkPool.Sim = HelpMilkDrops.Sim = _milk;
        _milk.Reset();
        _milkClock.Restart(); _milkLast = 0;
        _milkTimer ??= new DispatcherTimer(TimeSpan.FromMilliseconds(16), DispatcherPriority.Render, (_, _) => TickMilk());
        _milkTimer.Start();
    }

    private void TickMilk()
    {
        double now = _milkClock.Elapsed.TotalSeconds, dt = Math.Min(0.1, now - _milkLast);
        _milkLast = now;
        _milk.Box = HelpMilkPool.Bounds.Size;
        var tips = new Point[MillTips.Length];
        for (int i = 0; i < tips.Length; i++) tips[i] = HelpSpinner.TranslatePoint(MillTips[i], HelpMilkPool) ?? MillTips[i];
        _milk.Tips = tips;
        _milk.Step(dt);
        HelpMilkPool.InvalidateVisual(); HelpMilkDrops.InvalidateVisual();
    }
}
