using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;

namespace FlashbackStudio
{
    // Flashback Studio for Windows: the 3DS app's two screens side by side. The
    // top screen (left) shows the selected clip's first frame, or the clip
    // playing; the bottom screen (right) has the clip grid and details, or the
    // player's controls, in the 3DS app's style. Everything it does, it does as
    // the 3DS app does (see 3ds-app/source/main.c), with the keyboard instead of
    // the buttons (and the mouse instead of the touch screen), plus exporting
    // clips as MP4 videos.
    sealed class MainForm : Form
    {
        enum Mode { List, Rename, ConfirmDelete, ConfirmCompact, ConfirmExport, ConfirmExportAll, Job, Player }

        const int GridCols = 3;
        const int DetailRows = 8;
        const int HintRows = 4;

        readonly TopScreen top = new TopScreen();
        readonly BottomScreen bottom = new BottomScreen();
        readonly TextBox renameBox, exportNameBox;     // (a new name being typed for a clip, or an export)
        readonly System.Windows.Forms.Timer timer = new System.Windows.Forms.Timer { Interval = 1 };
        readonly System.Windows.Forms.Timer previewTimer = new System.Windows.Forms.Timer { Interval = 120 };
        readonly ThumbLoader thumbs;

        string folder;                  // the clip folder (null until one is opened)
        List<ClipInfo> clips = new List<ClipInfo>();
        int selected, scroll;           // (scroll counts grid rows)
        Mode mode = Mode.List;
        string message;                 // shown in the details until the selection changes...
        bool messageOk;                 // ...(rather than an error)
        string renameError;

        // Exporting
        bool exportInputs = true, exportReplace;
        ExportSize exportSize;          // the video's size...
        ExportScaling exportScaling;    // ...how it's scaled up to it...
        ExportEncoder exportEncoder;    // ...and what encodes it, remembered
        bool? gpuFound;                 // the GPU can encode here (null until that's been checked...)
        bool gpuChecking;               // ...which it's being)
        string exportFolder;            // where exports go (null: next to their clips), remembered
        int exportFirst, exportCount;   // the frames to export (all of them, or a trimmed part)...
        string exportPath;              // ...of this clip

        // The player, and its settings (kept until the app closes, as on the 3DS)
        Player player;
        bool loop = true, showInputs = true, showStats;
        int volume = 100;               // playback's (0 to 100; exports are as recorded), remembered
        bool askSave;                   // asking whether to save the selection
        long panelShown;                // when the player's bottom screen was last drawn (Stopwatch ticks)
        bool fineTimer;                 // timeBeginPeriod(1) is on

        // A job on a thread of its own: compacting, trimming or exporting
        Thread job;
        CancellationTokenSource jobCancel;
        volatile string jobTitle, jobName, jobDetail;
        volatile string jobShow;        // what the job made, to show in Explorer (an MP4, or the folder of them)
        string showPath, showFor;       // that, and the message it goes with (Show in folder, under it)
        double jobDone;                 // (0 to 1)
        bool closing;                   // close once the job has stopped

        ClipInfo Selected => selected < clips.Count ? clips[selected] : null;

        // This build's version ("v3.1"), from FlashbackStudio.csproj's <Version>.
        static string AppVersion
        {
            get
            {
                System.Version v = typeof(MainForm).Assembly.GetName().Version;
                return $"v{v.Major}.{v.Minor}" + (v.Build > 0 ? $".{v.Build}" : "");
            }
        }
        int S(int px) => px * DeviceDpi / 96;

        public MainForm(string[] paths)
        {
            Text = "Flashback Studio " + AppVersion;
            using (Stream icon = typeof(MainForm).Assembly.GetManifestResourceStream("icon.ico"))
                Icon = new Icon(icon);
            BackColor = Color.FromArgb(24, 24, 24);
            KeyPreview = true;
            AllowDrop = true;
            AutoScaleMode = AutoScaleMode.None;

            bottom.Dock = DockStyle.Right;
            bottom.Width = bottom.PanelWidth;
            top.Dock = DockStyle.Fill;
            Panel hinge = new Panel { Dock = DockStyle.Right, Width = S(3), BackColor = BackColor };
            Controls.Add(top);
            Controls.Add(hinge);
            Controls.Add(bottom);
            ClientSize = new Size(S(800) + hinge.Width + bottom.PanelWidth, Math.Max(S(480), 30 * bottom.RowH));
            MinimumSize = SizeFromClientSize(new Size(S(400) + hinge.Width + bottom.PanelWidth, 24 * bottom.RowH));
            StartPosition = FormStartPosition.CenterScreen;

            renameBox = NameBox();
            exportNameBox = NameBox();

            bottom.PaintScreen += PaintScreen;
            bottom.Resize += (s, e) =>
            {
                // (More rows of the grid, or fewer: the selected clip still in view.)
                if (thumbs != null)
                {
                    ClampList();
                    WantThumbs();
                }
            };
            bottom.Wheel += delta =>
            {
                if (mode == Mode.List && clips.Count > 0)
                {
                    scroll -= Math.Sign(delta);
                    ClampScroll();
                    WantThumbs();
                    bottom.Invalidate();
                }
            };
            top.Clicked += () =>
            {
                if (mode == Mode.Player && !askSave)
                {
                    player.PlayPause();
                    PlayerChanged();
                }
            };
            timer.Tick += (s, e) => Tick();
            previewTimer.Tick += (s, e) =>
            {
                previewTimer.Stop();
                ShowPreview();
            };

            thumbs = new ThumbLoader((clip, bmp) =>
            {
                try
                {
                    BeginInvoke((Action)(() =>
                    {
                        clip.Thumb = bmp;
                        clip.ThumbFailed = bmp == null;
                        if (mode != Mode.Player)
                            bottom.Invalidate();
                    }));
                }
                catch (InvalidOperationException)
                {
                    bmp?.Dispose();     // (the window is gone)
                }
            });
            int thumbW = TileCols * bottom.CellW - 2 * ThumbX;
            thumbs.Width = thumbW;
            thumbs.Height = thumbW * 3 / 5;

            DragEnter += (s, e) => e.Effect = CanOpen && e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None;
            DragDrop += (s, e) =>
            {
                if (CanOpen && e.Data.GetData(DataFormats.FileDrop) is string[] dropped && dropped.Length > 0)
                    OpenPath(dropped[0]);
            };

            startPath = paths.Length > 0 ? paths[0] : null;
        }

        readonly string startPath;

        // A box on the bottom screen to type a name in.
        TextBox NameBox()
        {
            TextBox box = new TextBox
            {
                Visible = false,
                BorderStyle = BorderStyle.None,
                BackColor = Color.FromArgb(45, 45, 45),
                ForeColor = Color.White,
                Font = bottom.Regular,
            };
            bottom.Controls.Add(box);
            box.TextChanged += (s, e) =>
            {
                renameError = null;
                bottom.Invalidate();
            };
            return box;
        }

        // (Once the window exists, so the thumbnails read meanwhile can be handed to it.)
        protected override void OnLoad(EventArgs e)
        {
            base.OnLoad(e);
            LoadSettings(out string last, out Rectangle? window, out bool maximized);
            // Where the window was, if that's still on a screen (most of its title bar, at least).
            if (window.HasValue && Screen.AllScreens.Any(screen =>
                    Rectangle.Intersect(screen.WorkingArea, new Rectangle(window.Value.X, window.Value.Y, window.Value.Width, S(30))).Width >= S(100)))
            {
                StartPosition = FormStartPosition.Manual;
                Bounds = new Rectangle(window.Value.Location, new Size(Math.Max(window.Value.Width, MinimumSize.Width),
                                                                       Math.Max(window.Value.Height, MinimumSize.Height)));
            }
            if (maximized)
                WindowState = FormWindowState.Maximized;
            if (startPath != null && (File.Exists(startPath) || Directory.Exists(startPath)))
                OpenPath(startPath);
            else
                OpenFolder(StartFolder(last), null);
        }

        // ---- What's remembered: the folder opened last, the volume, where exports go, their size,
        // scaling and encoder, and the window

        static string SettingsFile =>
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "FlashbackStudio", "settings.txt");

        // (A line "name=value" each.)
        void LoadSettings(out string lastFolder, out Rectangle? window, out bool maximized)
        {
            lastFolder = null;
            window = null;
            maximized = false;
            try
            {
                foreach (string line in File.ReadAllLines(SettingsFile))
                {
                    int eq = line.IndexOf('=');
                    string name = eq > 0 ? line.Substring(0, eq) : "", value = eq > 0 ? line.Substring(eq + 1).Trim() : "";
                    if (name == "folder")
                        lastFolder = value;
                    else if (name == "export")
                        exportFolder = value.Length > 0 ? value : null;
                    else if (name == "volume" && int.TryParse(value, out int v))
                        volume = Math.Max(0, Math.Min(100, v));
                    else if (name == "size" && Enum.TryParse(value, out ExportSize size) && Enum.IsDefined(typeof(ExportSize), size))
                        exportSize = size;
                    else if (name == "scaling" && Enum.TryParse(value, out ExportScaling scaling) &&
                             Enum.IsDefined(typeof(ExportScaling), scaling))
                        exportScaling = scaling;
                    else if (name == "encoder" && Enum.TryParse(value, out ExportEncoder encoder) &&
                             Enum.IsDefined(typeof(ExportEncoder), encoder))
                        exportEncoder = encoder;
                    else if (name == "window")
                    {
                        // x, y, width, height, and 1 if it was maximized
                        int[] n = value.Split(',').Select(part => int.TryParse(part, out int k) ? k : -1).ToArray();
                        if (n.Length == 5 && n[2] > 0 && n[3] > 0)
                        {
                            window = new Rectangle(n[0], n[1], n[2], n[3]);
                            maximized = n[4] == 1;
                        }
                    }
                }
            }
            catch (Exception e) when (e is IOException || e is UnauthorizedAccessException)
            {
            }
        }

        void SaveSettings()
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(SettingsFile));
                // (The window's size when it isn't maximized or minimized, and whether it's maximized.)
                Rectangle w = WindowState == FormWindowState.Normal ? Bounds : RestoreBounds;
                File.WriteAllLines(SettingsFile, new[] { "folder=" + folder, "volume=" + volume, "export=" + exportFolder,
                                                         "size=" + exportSize, "scaling=" + exportScaling, "encoder=" + exportEncoder,
                                                         $"window={w.X},{w.Y},{w.Width},{w.Height},{(WindowState == FormWindowState.Maximized ? 1 : 0)}" });
            }
            catch (Exception e) when (e is IOException || e is UnauthorizedAccessException)
            {
            }
        }

        // ---- The clip folder

        // The folder to start with: the last one opened, or else an SD card's
        // /luma/flashback/ (a 3DS SD card in the PC).
        static string StartFolder(string last)
        {
            if (last != null && Directory.Exists(last))
                return last;
            foreach (DriveInfo d in DriveInfo.GetDrives())
            {
                try
                {
                    string sd = Path.Combine(d.RootDirectory.FullName, "luma", "flashback");
                    if (d.DriveType == DriveType.Removable && d.IsReady && Directory.Exists(sd))
                        return sd;
                }
                catch (IOException)
                {
                }
            }
            return null;
        }

        bool CanOpen => mode == Mode.List || mode == Mode.ConfirmDelete || mode == Mode.ConfirmCompact ||
                        mode == Mode.ConfirmExport || mode == Mode.ConfirmExportAll;

        // Opens a folder, or a clip's folder with the clip selected.
        void OpenPath(string path)
        {
            try
            {
                path = Path.GetFullPath(path);
            }
            catch (Exception e) when (e is ArgumentException || e is NotSupportedException || e is IOException)
            {
                return;
            }
            if (Directory.Exists(path))
                OpenFolder(path, null);
            else if (File.Exists(path))
                OpenFolder(Path.GetDirectoryName(path), Path.GetFileName(path));
        }

        void OpenFolder(string path, string select)
        {
            mode = Mode.List;
            folder = path;
            message = null;
            selected = scroll = 0;
            Rescan(false, select);
            if (folder != null)
                SaveSettings();
        }

        void ChooseFolder()
        {
            string chosen = FolderPicker.Pick(this, "Open the folder with your clips (on a 3DS SD card: luma\\flashback)", folder);
            if (chosen != null)
                OpenFolder(chosen, null);
        }

        // Reads the folder again. Clips that are still there keep their
        // thumbnails, unless 'keepThumbs' is false; 'select' is a clip to select
        // (otherwise the selected one stays selected).
        void Rescan(bool keepThumbs, string select)
        {
            string before = Selected?.Name;
            Dictionary<string, ClipInfo> old = clips.ToDictionary(c => c.Path, StringComparer.OrdinalIgnoreCase);
            clips = folder != null ? Library.Scan(folder) : new List<ClipInfo>();
            foreach (ClipInfo c in clips)
                if (keepThumbs && old.TryGetValue(c.Path, out ClipInfo o) && o.FileSize == c.FileSize)
                {
                    c.Thumb = o.Thumb;
                    c.ThumbFailed = o.ThumbFailed;
                    o.Thumb = null;
                }
            foreach (ClipInfo o in old.Values)
                o.Thumb?.Dispose();
            int i = clips.FindIndex(c => string.Equals(c.Name, select ?? before, StringComparison.OrdinalIgnoreCase));
            if (i >= 0)
                selected = i;
            ClampList();
            WantThumbs();
            ShowPreview();
            bottom.Invalidate();
        }

        // ---- The grid

        int TileCols => BottomScreen.Cols / GridCols;
        int ThumbX => bottom.CellW / 2 + S(2);
        int ThumbRows => (thumbs.Height + S(8) + bottom.RowH - 1) / bottom.RowH;
        int TileRows => 1 + ThumbRows + 2;      // a blank row, the thumbnail, two rows of text
        int GridRows => Math.Max(1, (bottom.Rows - 1 - 1 - DetailRows - HintRows) / TileRows);

        void ClampScroll()
        {
            int rows = (clips.Count + GridCols - 1) / GridCols;
            scroll = Math.Max(0, Math.Min(scroll, rows - GridRows));
        }

        void ClampList()
        {
            selected = Math.Max(0, Math.Min(selected, clips.Count - 1));
            int sel = selected / GridCols;
            if (scroll > sel)
                scroll = sel;
            if (scroll < sel - (GridRows - 1))
                scroll = sel - (GridRows - 1);
            ClampScroll();
        }

        // The thumbnails on screen first, then the rest.
        void WantThumbs()
        {
            int first = scroll * GridCols, count = GridRows * GridCols;
            thumbs.Want(clips.Skip(first).Take(count).Concat(clips.Take(first)).Concat(clips.Skip(first + count)));
        }

        void Select(int index)
        {
            if (clips.Count == 0)
                return;
            int before = selected;
            selected = index;
            ClampList();
            if (selected != before)
            {
                message = null;
                WantThumbs();
                // (The top screen once the selection stops moving.)
                previewTimer.Stop();
                previewTimer.Start();
            }
            bottom.Invalidate();
        }

        // The selected clip's first frame on the top screen.
        void ShowPreview()
        {
            ClipInfo c = Selected;
            if (c == null || c.Error != null)
            {
                top.Show(null, 0, 0, null);
                return;
            }
            try
            {
                using (FlbClip clip = FlbClip.Open(c.Path))
                {
                    ushort[] frame = new ushort[clip.Width * clip.Height];
                    clip.DecodeEntry(0, frame);
                    top.Show(frame, clip.Width, clip.Height, null);
                }
            }
            catch (InvalidDataException)
            {
                top.Show(null, 0, 0, null);
            }
        }

        // ---- Drawing the bottom screen

        void PaintScreen(Painter p)
        {
            renameBox.Visible = mode == Mode.Rename;
            exportNameBox.Visible = mode == Mode.ConfirmExport;
            if (mode == Mode.Player)
                PaintPlayer(p);
            else
                PaintList(p);
        }

        static string Sentence(string s) => s.Length > 0 ? char.ToUpperInvariant(s[0]) + s.Substring(1) : s;

        // A row of buttons, in four columns across the screen: each what it does
        // and its action. The first is red if 'main' (the screen's main button).
        // 'line' is which of the bottom screen's rows of buttons (0 to 2).
        void Buttons(Painter p, int line, bool main, params (string Label, Action Click)[] buttons)
        {
            int h = (HintRows * p.RowH - S(4)) / 3;
            Rectangle band = p.Band((p.Rows - HintRows) * p.RowH + S(2) + line * h, h - S(4));
            Rectangle[] boxes = p.Split(band, 4, S(5));
            for (int i = 0; i < buttons.Length; i++)
                p.Button(boxes[i], buttons[i].Label, buttons[i].Click, i == 0 && main ? Look.Red : Look.Normal);
        }

        // A question's answers: the main one (red), and Cancel.
        void Answers(Painter p, string answer, Action yes, Action no)
        {
            int h = (HintRows * p.RowH - S(4)) / 3;
            Rectangle[] boxes = p.Split(p.Band((p.Rows - HintRows) * p.RowH + S(2), h - S(4)), 3, S(5));
            if (answer != null)
                p.Button(boxes[0], answer, yes, Look.Red);
            p.Button(boxes[answer != null ? 1 : 0], "Cancel", no);
        }

        // A small button in a line of text (to change a choice), at row r, column col.
        int Choice(Painter p, int r, int col, string label, Action click, bool on)
        {
            Rectangle at = p.Cells(r, col, label.Length + 2);
            p.Button(new Rectangle(at.X, at.Y + S(1), at.Width, at.Height - S(2)), label, click, on ? Look.Red : Look.Normal);
            return col + label.Length + 2;
        }

        // A dropdown in a line of text, at row r, column col: a button 'width'
        // columns wide saying 'label' (what's chosen), which opens a list of
        // 'options' under it (the chosen one red) to pick another (the ones
        // 'enabled' says, if it's given). Returns the column after it.
        int Dropdown(Painter p, int r, int col, int width, string label, string[] options, int chosen, Action<int> choose,
                     bool[] enabled = null)
        {
            Rectangle at = p.Cells(r, col, width);
            Rectangle box = new Rectangle(at.X, at.Y + S(1), at.Width, at.Height - S(2));
            p.Button(box, label + " ▾", () => ShowDropdown(box, options, chosen, choose, enabled));
            return col + width;
        }

        void ShowDropdown(Rectangle under, string[] options, int chosen, Action<int> choose, bool[] enabled)
        {
            ContextMenuStrip menu = new ContextMenuStrip
            {
                Renderer = new DropdownRenderer(),
                ShowImageMargin = false,
                Font = bottom.Label,
                MinimumSize = new Size(under.Width, 0),
            };
            for (int i = 0; i < options.Length; i++)
            {
                int pick = i;
                ToolStripMenuItem item = new ToolStripMenuItem(options[i]) { Checked = i == chosen, Enabled = enabled?[i] ?? true };
                item.Click += (s, e) =>
                {
                    choose(pick);
                    bottom.Invalidate();
                };
                menu.Items.Add(item);
            }
            menu.Closed += (s, e) => BeginInvoke((Action)menu.Dispose);     // (after its item's Click)
            menu.Show(bottom, new Point(under.Left, under.Bottom));
        }

        void PaintList(Painter p)
        {
            int first = scroll * GridCols, last = Math.Min(clips.Count, first + GridRows * GridCols);
            string counts = clips.Count > GridRows * GridCols ? $"{first + 1}-{last} of {clips.Count} " :
                            clips.Count > 0 ? $"{clips.Count} clip{(clips.Count == 1 ? "" : "s")} " : "";
            p.Row(0, Style.Title, " Flashback Studio");
            p.Right(0, Style.Title, counts);

            int sep = 1 + GridRows * TileRows;
            if (clips.Count == 0)
            {
                p.Wrap(3, sep - 1, Style.Normal, folder == null
                    ? "Open the folder with your clips (O), or drag clips or a folder here. On the 3DS's SD card, " +
                      "Flashback saves them in /luma/flashback/."
                    : $"No clips in {folder}. Save one with Luma3DS-Flashback (Rosalina menu > Flashback), copy it " +
                      "from the SD card's /luma/flashback/, or open another folder (O).", 1);
            }
            else
            {
                p.Text(1, 1, Style.Dim, folder.Length > BottomScreen.Cols - 2 ? "…" + folder.Substring(folder.Length - (BottomScreen.Cols - 3)) : folder);
                for (int r = 0; r < GridRows; r++)
                    for (int i = 0; i < GridCols; i++)
                    {
                        int idx = first + r * GridCols + i;
                        if (idx < clips.Count)
                            PaintTile(p, 1 + r * TileRows, i * TileCols, clips[idx], idx);
                    }
            }

            p.Text(sep, 0, Style.Dim, new string('-', BottomScreen.Cols));
            int d = sep + 1, hints = p.Rows - HintRows;
            ClipInfo c = Selected;
            switch (mode)
            {
                case Mode.Job:
                    p.Text(d, 1, Style.Info, jobTitle);
                    p.Text(d + 1, 1, Style.Normal, jobName ?? "");
                    PaintProgress(p, d + 2, jobDone);
                    p.Text(d + 3, 1, Style.Normal, jobDetail ?? "");
                    Answers(p, null, null, CancelJob);
                    return;
                case Mode.ConfirmDelete:
                    p.Text(d, 1, Style.Warn, "Delete this clip?");
                    p.Text(d + 1, 1, Style.Normal, c.Name);
                    p.Text(d + 2, 1, Style.Normal, IsRemovable(c.Path) ? "It can't be undone (it's on a removable drive)." : "It goes to the Recycle Bin.");
                    Answers(p, "Delete", Delete, Cancel);
                    return;
                case Mode.ConfirmCompact:
                    p.Text(d, 1, Style.Info, "Compact this clip?");
                    p.Text(d + 1, 1, Style.Normal, c.Name);
                    p.Text(d + 2, 1, Style.Normal, $"{Names.Size(c.FileSize)} -> {Names.Size(c.CompactSize)}");
                    p.Wrap(d + 3, d + 5, Style.Normal, "Remove empty space between frames. The same video but smaller.", 1);
                    Answers(p, "Compact", Compact, Cancel);
                    return;
                case Mode.ConfirmExport:
                {
                    bool part = exportCount < c.FrameCount;
                    p.Text(d, 1, Style.Info, part ? "Export the selection as an MP4?" : "Export this clip as an MP4?");
                    p.Text(d + 1, 1, Style.Normal, "Name");
                    exportNameBox.Bounds = Rectangle.Inflate(p.Cells(d + 1, 8, BottomScreen.Cols - 13), 0, -1);
                    p.Text(d + 1, BottomScreen.Cols - 4, Style.Dim, ".mp4");
                    PaintExportFolder(p, d + 2, "next to the clip");
                    PaintVideoChoices(p, d + 3, c);
                    PaintInputsChoice(p, d + 4, c.HasInputs);
                    p.Right(d + 4, Style.Normal, $"{Names.Length(exportCount, c.Fps)}{(c.HasAudio ? ", with sound" : "")} ");
                    PaintEncoderChoice(p, d + 5);
                    string problem = ExportProblem();
                    if (problem != null)
                        p.Wrap(d + 6, d + 7, Style.Warn, problem, 1);
                    else if (File.Exists(ExportMp4()))
                        p.Wrap(d + 6, d + 7, Style.Warn, "There's already an MP4 with that name there: it will be replaced.", 1);
                    Answers(p, "Export", problem == null ? Export : (Action)null, Cancel);
                    return;
                }
                case Mode.ConfirmExportAll:
                {
                    int ok = clips.Count(x => x.Error == null), have = clips.Count(x => x.Error == null && File.Exists(TargetMp4(x)));
                    p.Text(d, 1, Style.Info, $"Export all {ok} clip{(ok == 1 ? "" : "s")} as MP4s?");
                    PaintExportFolder(p, d + 1, "next to each clip");
                    p.Text(d + 2, 1, Style.Dim, "Each MP4 is named after its clip.");
                    PaintVideoChoices(p, d + 3, null);
                    PaintInputsChoice(p, d + 4, true);
                    PaintEncoderChoice(p, d + 5);
                    string problem = ExportFolderProblem();
                    if (problem != null)
                        p.Wrap(d + 6, d + 7, Style.Warn, problem, 1);
                    else if (have > 0)
                    {
                        string text = $"{have} already ha{(have == 1 ? "s" : "ve")} an MP4 there: ";
                        p.Text(d + 6, 1, Style.Normal, text);
                        Choice(p, d + 6, 1 + text.Length, exportReplace ? "replace" : "skip", ToggleExportReplace, false);
                    }
                    Answers(p, "Export", problem == null ? Export : (Action)null, Cancel);
                    return;
                }
                case Mode.Rename:
                    p.Text(d, 1, Style.Info, "Rename this clip");
                    renameBox.Bounds = Rectangle.Inflate(p.Cells(d + 1, 1, BottomScreen.Cols - 2), 0, -1);
                    if (renameError != null)
                        p.Wrap(d + 2, d + 4, Style.Warn, renameError, 1);
                    else
                        p.Text(d + 2, 1, Style.Dim, "Spaces become dashes.");
                    Answers(p, "Rename", Rename, Cancel);
                    return;
            }

            if (c != null)
            {
                p.Text(d, 1, Style.Info, c.Name);
                if (c.Error != null)
                {
                    p.Text(d + 1, 1, Style.Warn, "Can't play this clip:");
                    p.Wrap(d + 2, d + 4, Style.Normal, Sentence(c.Error) + ".", 1);
                }
                else
                {
                    p.Text(d + 1, 1, Style.Normal, Names.Resolution(c.Width, c.Height, c.Fps, c.HasAudio));
                    p.Text(d + 2, 1, Style.Normal, $"Length {Names.Length(c.FrameCount, c.Fps)} ({c.FrameCount} frames), {Names.Size(c.FileSize)}");
                    p.Text(d + 3, 1, Style.Normal, c.MissedFrames == 0 ? "No missed frames" : $"Missed frames: {c.MissedFrames} (shown as copies)");
                    p.Text(d + 4, 1, Style.Dim, c.IsRaw ? "From an older Flashback build (.raw)" : c.IsCompacted ? "Compacted" :
                                                 $"Not compacted ({Names.Size(c.CompactSize)} once compacted)");
                    if (message == null)
                        p.Text(d + 5, 1, Style.Dim, (c.HasInputs ? "Inputs recorded" : "No inputs recorded") +
                                                    (HasMp4(c) ? "; exported (has an MP4)" : ""));
                }
            }
            if (message != null)
            {
                // (After an export: a button to show it, under the message.)
                bool show = showPath != null && ReferenceEquals(message, showFor);
                p.Wrap(d + 5, show ? d + 5 : d + DetailRows - 1, messageOk ? Style.Info : Style.Warn, message, 1);
                if (show)
                    Choice(p, d + 6, 1, "Show in folder", ShowExported, false);
            }

            if (clips.Count > 0)
            {
                bool can = c != null && c.Error == null;
                Buttons(p, 0, true, ("Play", can ? Play : (Action)null), ("Trim", can && !c.IsRaw ? Trim : (Action)null),
                        ("Export", can ? AskExport : (Action)null), ("Compact", can && !c.IsRaw && !c.IsCompacted ? AskCompact : (Action)null));
                Buttons(p, 1, false, ("Rename", c != null && !c.IsRaw ? StartRename : (Action)null), ("Delete", AskDelete),
                        ("Refresh", RefreshList), ("Open folder", ChooseFolder));
                Buttons(p, 2, false, ("Export all", AskExportAll));
            }
            else
                Buttons(p, 0, false, ("Open folder", ChooseFolder), ("Refresh", RefreshList));
        }

        static readonly string[] ScalingNames = { "Nearest", "Bilinear", "Bicubic", "Lanczos", "Spline" };

        // The video's size and how it's scaled up: two dropdowns. (Twice the size
        // is clip 'c''s, or if it's null, any clip's.)
        void PaintVideoChoices(Painter p, int r, ClipInfo c)
        {
            string twice = c != null ? $"{c.Width * 2}x{c.Height * 2}" : "2x";
            string[] sizes = { twice, "1080p", "1440p", "4K" };
            string[] items = new string[sizes.Length];
            items[0] = c != null ? twice + " (2x)" : "2x the clip's size";
            for (int i = 1; i < sizes.Length; i++)
            {
                (int w, int h) = Converter.VideoSize(400, 240, (ExportSize)i);
                items[i] = $"{sizes[i]} ({w}x{h})";
            }
            p.Text(r, 1, Style.Normal, "Size:");
            int col = Dropdown(p, r, 7, 11, sizes[(int)exportSize], items, (int)exportSize, i =>
            {
                exportSize = (ExportSize)i;
                SaveSettings();
            });
            p.Text(r, col + 2, Style.Normal, "Scaling:");
            Dropdown(p, r, col + 11, 12, ScalingNames[(int)exportScaling], ScalingNames, (int)exportScaling, i =>
            {
                exportScaling = (ExportScaling)i;
                SaveSettings();
            });
        }

        // What encodes the video: a dropdown. If the GPU can't here (no NVIDIA
        // card, or no driver for it), it's the CPU, and the GPU can't be chosen.
        void PaintEncoderChoice(Painter p, int r)
        {
            bool gpu = exportEncoder == ExportEncoder.Gpu && gpuFound != false;
            p.Text(r, 1, Style.Normal, "Encoder:");
            string[] items = { "CPU (x264)", gpuFound == false ? "GPU (no NVIDIA card found)" : "GPU (NVIDIA NVENC)" };
            int col = Dropdown(p, r, 10, 7, gpu ? "GPU" : "CPU", items, gpu ? 1 : 0, i =>
            {
                exportEncoder = (ExportEncoder)i;
                SaveSettings();
            }, new[] { true, gpuFound != false });
            if (gpuFound == false)
                p.Text(r, col + 1, Style.Dim, "(no NVIDIA card found)");
        }

        void PaintInputsChoice(Painter p, int r, bool any)
        {
            const string label = "Input display: ";
            p.Text(r, 1, Style.Normal, label);
            int col = Choice(p, r, 1 + label.Length, exportInputs ? "on" : "off", ToggleExportInputs, exportInputs);
            if (!any)
                p.Text(r, col + 1, Style.Dim, "(no inputs in this clip)");
        }

        // A tile: a thumbnail of the clip's first frame in a thin frame, or on a
        // white card (which its text continues) if it's selected, and two lines
        // of text: when it was saved, and how long it is. Clicking it selects it,
        // and clicking it again plays it.
        void PaintTile(Painter p, int row, int col, ClipInfo c, int idx)
        {
            bool sel = idx == selected;
            Rectangle area = p.Cells(row + 1, col, TileCols, ThumbRows), tile = p.Cells(row, col, TileCols, TileRows);
            Rectangle thumb = new Rectangle(area.X + ThumbX, area.Y + (area.Height - thumbs.Height) / 2, thumbs.Width, thumbs.Height);
            if (sel)
                p.Fill(area, Theme.Head);
            else
                p.Fill(Rectangle.Inflate(thumb, S(2), S(2)), p.Hovered(tile) ? Color.FromArgb(130, 130, 130) : Theme.Tile);
            if (c.Thumb != null)
                p.G.DrawImageUnscaled(c.Thumb, thumb.X, thumb.Y);
            else
                p.Fill(thumb, c.Error != null || c.ThumbFailed ? Theme.Damaged : Theme.Loading);

            string saved = Names.TileTime(c.Name), length = c.Error != null ? "damaged" : Names.Length(c.FrameCount, c.Fps);
            Style style = sel ? Style.Selected : c.Error != null ? Style.Warn : Style.Normal;
            p.Text(row + 1 + ThumbRows, col, style, Center(saved, TileCols), TileCols);
            p.Text(row + 2 + ThumbRows, col, style, Center(length, TileCols), TileCols);
            p.Clickable(tile, () =>
            {
                if (mode != Mode.List)
                    return;
                if (idx == selected)
                    Play();
                else
                    Select(idx);
            });
        }

        static string Center(string text, int width)
        {
            if (text.Length >= width)
                return text;
            int left = (width - text.Length) / 2;
            return new string(' ', left) + text + new string(' ', width - left - text.Length);
        }

        // A progress bar across row r, in the player bar's colours.
        static void PaintProgress(Painter p, int r, double done)
        {
            Rectangle bar = p.Cells(r, 1, BottomScreen.Cols - 2);
            bar = new Rectangle(bar.X, bar.Y + bar.Height / 4, bar.Width, bar.Height / 2);
            p.Fill(bar, Theme.Rest);
            p.Fill(new Rectangle(bar.X, bar.Y, (int)(bar.Width * Math.Max(0, Math.Min(1, done))), bar.Height), Theme.Red);
        }

        // ---- Keys

        protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
        {
            if (HandleKey(keyData))
                return true;
            return base.ProcessCmdKey(ref msg, keyData);
        }

        // Does what 'key' does now; false if it does nothing. Everything has a
        // button; only playing (Space), a frame back and on (, and .) and a
        // second back and on (Left/Right) have keys too, while a clip is open.
        // (And a name being typed is used with Enter, as in any text box.)
        bool HandleKey(Keys key)
        {
            if (mode == Mode.Rename || mode == Mode.ConfirmExport)
            {
                if (key == Keys.Enter && mode == Mode.Rename)
                    Rename();
                else if (key == Keys.Enter)
                {
                    if (ExportProblem() == null)
                        Export();
                }
                else if (key == Keys.Escape)
                    Cancel();
                else
                    return false;
                return true;
            }
            if (mode != Mode.Player || askSave)
                return false;
            switch (key)
            {
                case Keys.Space: player.PlayPause(); break;
                case Keys.Oemcomma: player.Step(-1); break;     // (",": a frame back, "." a frame on, like video editors)
                case Keys.OemPeriod: player.Step(1); break;
                case Keys.Left: player.Jump(-1); break;
                case Keys.Right: player.Jump(1); break;
                default: return false;
            }
            PlayerChanged();
            return true;
        }

        void Cancel()
        {
            mode = Mode.List;
            bottom.Invalidate();
            bottom.Focus();
        }

        void SetMessage(string text, bool ok)
        {
            message = text;
            messageOk = ok;
            bottom.Invalidate();
        }

        void RefreshList()
        {
            message = null;
            Rescan(false, null);
        }

        // ---- Deleting, compacting, renaming

        static bool IsRemovable(string path)
        {
            try
            {
                return new DriveInfo(Path.GetPathRoot(path)).DriveType == DriveType.Removable;
            }
            catch (ArgumentException)
            {
                return false;   // (a network path)
            }
        }

        static long FreeSpace(string path)
        {
            try
            {
                return new DriveInfo(Path.GetPathRoot(path)).AvailableFreeSpace;
            }
            catch (Exception e) when (e is ArgumentException || e is IOException || e is UnauthorizedAccessException)
            {
                return long.MaxValue;
            }
        }

        void AskDelete()
        {
            if (Selected == null)
                return;
            mode = Mode.ConfirmDelete;
            bottom.Invalidate();
        }

        // (A clip on a removable drive, like an SD card, can't go to the Recycle Bin.)
        void Delete()
        {
            ClipInfo c = Selected;
            mode = Mode.List;
            message = null;
            try
            {
                if (IsRemovable(c.Path))
                    File.Delete(c.Path);
                else
                    Microsoft.VisualBasic.FileIO.FileSystem.DeleteFile(c.Path, Microsoft.VisualBasic.FileIO.UIOption.OnlyErrorDialogs,
                                                                       Microsoft.VisualBasic.FileIO.RecycleOption.SendToRecycleBin);
                if (c.IsRaw)
                    File.Delete(Path.ChangeExtension(c.Path, ".txt"));
            }
            catch (Exception e) when (e is IOException || e is UnauthorizedAccessException || e is OperationCanceledException)
            {
                message = "The clip couldn't be deleted.";
                messageOk = false;
            }
            Rescan(true, null);
        }

        void AskCompact()
        {
            ClipInfo c = Selected;
            if (c == null || c.Error != null)
                return;     // (a damaged clip's details already say what's wrong with it)
            if (c.IsRaw)
                SetMessage("Clips from older Flashback builds (.raw) can't be compacted.", false);
            else if (c.IsCompacted)
                SetMessage("This clip is already compacted.", true);
            else
            {
                mode = Mode.ConfirmCompact;
                bottom.Invalidate();
            }
        }

        // The copy is made next to the clip, which is only replaced at the end.
        void Compact()
        {
            ClipInfo c = Selected;
            mode = Mode.List;
            long needed = c.CompactSize + 1000000;
            if (FreeSpace(c.Path) < needed)
            {
                SetMessage($"Not enough free space: compacting needs {Names.Size(needed)} free.", false);
                return;
            }
            long from = c.FileSize;
            RunJob("Compacting...", c.Name, token =>
            {
                Stopwatch sw = Stopwatch.StartNew();
                FlbCompactor work = FlbCompactor.Compact(c.Path);
                while (!work.Step())
                {
                    if (token.IsCancellationRequested)
                    {
                        work.Cancel();
                        return ("Compacting cancelled; the clip wasn't changed.", true, c.Name);
                    }
                    Progress((double)work.Copied / Math.Max(1, work.Total), $"{Names.Size(work.Copied)} of {Names.Size(work.Total)} copied");
                }
                long to = new FileInfo(c.Path).Length;
                return ($"Compacted: {Names.Size(from)} -> {Names.Size(to)} in {(int)sw.Elapsed.TotalSeconds} s.", true, c.Name);
            });
        }

        void StartRename()
        {
            ClipInfo c = Selected;
            if (c == null)
                return;
            if (c.IsRaw)
            {
                SetMessage("Clips from older Flashback builds (.raw) can't be renamed here.", false);
                return;
            }
            mode = Mode.Rename;
            renameError = null;
            renameBox.Text = Path.GetFileNameWithoutExtension(c.Name);
            bottom.Invalidate();
            bottom.Update();    // (places the box)
            renameBox.Visible = true;
            renameBox.Focus();
            renameBox.SelectAll();
        }

        void Rename()
        {
            ClipInfo c = Selected;
            string error = Names.CheckRename(renameBox.Text, folder, c.Name);
            if (error != null)
            {
                renameError = error;
                bottom.Invalidate();
                return;
            }
            string name = Names.ClipFileName(renameBox.Text), to = Path.Combine(folder, name);
            mode = Mode.List;
            bottom.Focus();
            if (name == c.Name)
            {
                bottom.Invalidate();
                return;
            }
            string text;
            bool ok;
            try
            {
                if (string.Equals(name, c.Name, StringComparison.OrdinalIgnoreCase))
                {
                    // Only capitals change, which Windows (and the SD card) see as
                    // the same name: go through "<clip>.old", which is put back if
                    // this is cut off (see FlbCompactor.CleanUpLeftover).
                    File.Move(c.Path, c.Path + ".old");
                    File.Move(c.Path + ".old", to);
                }
                else
                    File.Move(c.Path, to);
                text = $"Renamed to {name}.";
                ok = true;
            }
            catch (Exception e) when (e is IOException || e is UnauthorizedAccessException)
            {
                text = "The clip couldn't be renamed.";
                ok = false;
            }
            Rescan(true, name);
            SetMessage(text, ok);
        }

        // ---- Exporting

        // The folder exports go to: the one chosen, or the clips' own.
        string ExportDir() => exportFolder ?? folder;

        // Where the export being asked about goes: the name typed, in ExportDir.
        string ExportMp4()
        {
            string name = exportNameBox.Text.Trim();
            if (name.EndsWith(".mp4", StringComparison.OrdinalIgnoreCase))
                name = name.Substring(0, name.Length - 4);
            return Path.Combine(ExportDir(), name + ".mp4");
        }

        // Where clip c's MP4 goes when every clip is exported.
        string TargetMp4(ClipInfo c) => Path.Combine(ExportDir(), Path.GetFileNameWithoutExtension(c.Name) + ".mp4");

        // Whether clip c has been exported (with its own name), next to it or where exports go.
        bool HasMp4(ClipInfo c) => File.Exists(c.Mp4) || (exportFolder != null && File.Exists(TargetMp4(c)));

        // Why the export folder can't be used, or null.
        string ExportFolderProblem() =>
            Directory.Exists(ExportDir()) ? null : "That folder isn't there any more: choose another (Change…).";

        // Why the name typed can't be used, or null.
        string ExportProblem()
        {
            string name = exportNameBox.Text.Trim();
            if (name.EndsWith(".mp4", StringComparison.OrdinalIgnoreCase))
                name = name.Substring(0, name.Length - 4);
            if (name.Length == 0)
                return "Type a name for the MP4.";
            if (name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
                return "A name can't have \\ / : * ? \" < > | in it.";
            if (name.EndsWith("."))
                return "The name can't end with a dot.";
            if (Names.IsReserved(name))
                return "Windows can't use that name (CON, PRN, AUX, NUL, COM0-9 and LPT0-9 are taken). Try another.";
            return ExportFolderProblem();
        }

        // The export folder's row: where exports go, and buttons to change it
        // (and to go back to next to the clips).
        void PaintExportFolder(Painter p, int r, string standard)
        {
            p.Text(r, 1, Style.Normal, "Folder");
            int right = BottomScreen.Cols - (exportFolder != null ? 17 : 9);     // (room for the buttons: 9 columns, and 7)
            string shown = exportFolder ?? standard;
            int room = right - 8 - 1;
            if (shown.Length > room)
                shown = "…" + shown.Substring(shown.Length - (room - 1));
            p.Text(r, 8, exportFolder != null ? Style.Normal : Style.Dim, shown);
            int col = Choice(p, r, right, "Change…", ChooseExportFolder, false);
            if (exportFolder != null)
                Choice(p, r, col + 1, "Reset", () => SetExportFolder(null), false);
        }

        void ChooseExportFolder()
        {
            string chosen = FolderPicker.Pick(this, "Export the MP4s to this folder", ExportDir());
            if (chosen != null)
                SetExportFolder(chosen);
            if (mode == Mode.ConfirmExport)
                exportNameBox.Focus();
        }

        // Where exports go from now on (null: next to their clips), remembered.
        void SetExportFolder(string dir)
        {
            exportFolder = dir;
            SaveSettings();
            bottom.Invalidate();
        }

        // Asks about exporting frames exportFirst .. exportFirst + exportCount - 1
        // of exportPath, with 'name' to start with, in the box to type another.
        void AskExportName(string name)
        {
            CheckGpu();
            exportNameBox.Text = name;
            mode = Mode.ConfirmExport;
            bottom.Invalidate();
            bottom.Update();    // (places the box)
            exportNameBox.Visible = true;
            exportNameBox.Focus();
            exportNameBox.SelectAll();
        }

        void ToggleExportInputs()
        {
            exportInputs = !exportInputs;
            bottom.Invalidate();
        }

        void ToggleExportReplace()
        {
            exportReplace = !exportReplace;
            bottom.Invalidate();
        }

        bool CheckFfmpeg()
        {
            if (Converter.FindFfmpeg() != null)
                return true;
            SetMessage("Exporting needs ffmpeg.exe next to FlashbackStudio.exe (it comes with it), or ffmpeg on PATH.", false);
            return false;
        }

        // Finds out whether the GPU can encode here, once, on a thread of its own
        // (it takes a second or so): the first time an export is asked about.
        void CheckGpu()
        {
            string ffmpeg = Converter.FindFfmpeg();
            if (gpuFound != null || gpuChecking || ffmpeg == null)
                return;
            gpuChecking = true;
            new Thread(() =>
            {
                bool found = Converter.GpuAvailable(ffmpeg);
                try
                {
                    BeginInvoke((Action)(() =>
                    {
                        gpuFound = found;
                        bottom.Invalidate();
                    }));
                }
                catch (InvalidOperationException)
                {
                    // (the window is gone)
                }
            }) { IsBackground = true, Name = "GPU check" }.Start();
        }

        void AskExport()
        {
            ClipInfo c = Selected;
            if (c == null || c.Error != null || !CheckFfmpeg())
                return;
            exportPath = c.Path;
            exportFirst = 0;
            exportCount = c.FrameCount;
            AskExportName(Path.GetFileNameWithoutExtension(c.Name));
        }

        void AskExportAll()
        {
            if (!clips.Any(c => c.Error == null) || !CheckFfmpeg())
                return;
            CheckGpu();
            mode = Mode.ConfirmExportAll;
            bottom.Invalidate();
        }

        void Export()
        {
            string ffmpeg = Converter.FindFfmpeg();
            bool inputs = exportInputs;
            ExportSize size = exportSize;
            ExportScaling scaling = exportScaling;
            // (The GPU if it can encode here: if that isn't known yet, the job finds out first.)
            bool gpu = exportEncoder == ExportEncoder.Gpu;
            ExportEncoder Encoder() => gpu && Converter.GpuAvailable(ffmpeg) ? ExportEncoder.Gpu : ExportEncoder.Cpu;
            if (mode == Mode.ConfirmExport)
            {
                string path = exportPath, mp4 = ExportMp4(), name = Path.GetFileName(mp4);
                int firstFrame = exportFirst, count = exportCount;
                mode = Mode.List;
                RunJob("Exporting...", name, token =>
                {
                    Stopwatch sw = Stopwatch.StartNew();
                    try
                    {
                        ExportEncoder encoder = Encoder();
                        using (FlbClip clip = FlbClip.Open(path))
                            Converter.ToMp4(clip, firstFrame, count, mp4, inputs, size, scaling, encoder, ffmpeg,
                                            done => Progress((double)done / count, $"Frame {done} of {count}"), token);
                    }
                    catch (OperationCanceledException)
                    {
                        return ("Exporting stopped; no MP4 was saved.", true, null);
                    }
                    jobShow = mp4;
                    return ($"Exported {name} in {(int)sw.Elapsed.TotalSeconds} s.", true, null);
                });
                return;
            }

            List<ClipInfo> all = clips.Where(c => c.Error == null).ToList();
            if (ExportFolderProblem() != null)
                return;
            List<ClipInfo> todo = all.Where(c => exportReplace || !File.Exists(TargetMp4(c))).ToList();
            int skipped = all.Count - todo.Count;
            mode = Mode.List;
            if (todo.Count == 0)
            {
                SetMessage("Every clip has its MP4 already (R replaces them).", true);
                return;
            }
            string dir = ExportDir();
            RunJob("Exporting...", "", token =>
            {
                long total = Math.Max(1, todo.Sum(c => (long)c.FrameCount)), before = 0;
                int exported = 0, failures = 0;
                string failed = null;
                ExportEncoder encoder = Encoder();
                for (int i = 0; i < todo.Count; i++)
                {
                    ClipInfo c = todo[i];
                    jobName = $"Clip {i + 1} of {todo.Count}: {c.Name}";
                    try
                    {
                        using (FlbClip clip = FlbClip.Open(c.Path))
                            Converter.ToMp4(clip, TargetMp4(c), inputs, size, scaling, encoder, ffmpeg,
                                            done => Progress((before + done) / (double)total, $"Frame {done} of {c.FrameCount}"), token);
                        exported++;
                    }
                    catch (OperationCanceledException)
                    {
                        return ($"Exporting stopped: {exported} exported.", true, null);
                    }
                    catch (Exception e)
                    {
                        failures++;
                        failed = failed ?? $"{c.Name}: {e.Message}";
                    }
                    before += c.FrameCount;
                }
                string result = $"Exported {exported} clip{(exported == 1 ? "" : "s")}";
                if (skipped > 0)
                    result += $"; {skipped} had an MP4";
                if (failures > 0)
                    result += $"; {failures} failed: {failed}";
                if (exported > 0)
                    jobShow = dir;
                return (result + ".", failures == 0, null);
            });
        }

        // ---- Jobs

        void Progress(double done, string detail)
        {
            jobDone = done;
            jobDetail = detail;
        }

        // Runs 'work' on a thread of its own, the bottom screen showing its
        // progress (Esc stops it). It returns its message, whether that's good
        // news, and a clip to select (or null).
        void RunJob(string title, string name, Func<CancellationToken, (string Message, bool Ok, string Select)> work)
        {
            jobTitle = title;
            jobName = name;
            jobDetail = "";
            jobShow = null;
            jobDone = 0;
            jobCancel = new CancellationTokenSource();
            CancellationToken token = jobCancel.Token;
            mode = Mode.Job;
            message = null;
            previewTimer.Stop();
            job = new Thread(() =>
            {
                (string Message, bool Ok, string Select) result;
                try
                {
                    result = work(token);
                }
                catch (Exception e)
                {
                    result = (Sentence(e.Message.TrimEnd('.')) + ".", false, null);
                }
                try
                {
                    BeginInvoke((Action)(() => JobDone(result.Message, result.Ok, result.Select)));
                }
                catch (InvalidOperationException)
                {
                    // (the window is gone)
                }
            }) { IsBackground = true, Name = "job" };
            job.Start();
            timer.Interval = 100;
            timer.Start();
            bottom.Invalidate();
        }

        void JobDone(string text, bool ok, string select)
        {
            job = null;
            mode = Mode.List;
            timer.Stop();
            if (closing)
            {
                Close();
                return;
            }
            Rescan(true, select);
            SetMessage(text, ok);
            showPath = jobShow;
            showFor = text;
        }

        // Shows what the last export made in Explorer: the MP4 selected, or the folder of them.
        void ShowExported()
        {
            try
            {
                if (File.Exists(showPath))
                    Process.Start("explorer.exe", $"/select,\"{showPath}\"");
                else if (Directory.Exists(showPath))
                    Process.Start("explorer.exe", $"\"{showPath}\"");
                else
                    SetMessage("It isn't there any more.", false);
            }
            catch (Exception e) when (e is System.ComponentModel.Win32Exception || e is InvalidOperationException)
            {
                SetMessage("Explorer couldn't be opened.", false);
            }
        }

        void CancelJob()
        {
            jobCancel?.Cancel();
            jobDetail = "Stopping...";
            bottom.Invalidate();
        }

        // ---- The player

        [DllImport("winmm.dll")] static extern int timeBeginPeriod(int ms);
        [DllImport("winmm.dll")] static extern int timeEndPeriod(int ms);

        void Play() => OpenPlayer(false);

        void Trim()
        {
            if (Selected?.IsRaw == true)
            {
                SetMessage("Clips from older Flashback builds (.raw) can't be trimmed.", false);
                return;
            }
            OpenPlayer(true);
        }

        // Opens the selected clip in the player; for trimming, paused at the start.
        void OpenPlayer(bool trim)
        {
            ClipInfo c = Selected;
            if (c == null || c.Error != null)
                return;     // (a damaged clip's details already say why it can't be played)
            try
            {
                player = new Player(c.Path, loop, showInputs, showStats) { Volume = volume };
            }
            catch (InvalidDataException e)
            {
                SetMessage(Sentence(e.Message) + ".", false);
                return;
            }
            if (trim)
            {
                player.TrimMode = true;
                player.Playing = false;
            }
            mode = Mode.Player;
            askSave = false;
            previewTimer.Stop();
            // (Frames due every 33 ms are shown on time, not up to 15 ms late.)
            timeBeginPeriod(1);
            fineTimer = true;
            timer.Interval = 1;
            timer.Start();
            PlayerChanged();
        }

        void ClosePlayer()
        {
            loop = player.Loop;
            showInputs = player.ShowInputs;
            showStats = player.ShowStats;
            player.Dispose();
            player = null;
            timer.Stop();
            if (fineTimer)
                timeEndPeriod(1);
            fineTimer = false;
            mode = Mode.List;
            Rescan(true, null);
        }

        // The player's settings (S while a clip plays).
        static readonly string[] SettingNames = { "Loop", "Input display", "Timing details" };

        void ToggleSetting(int item)
        {
            if (item == 0)
                player.Loop = !player.Loop;
            else if (item == 1)
                player.ShowInputs = !player.ShowInputs;
            else
                player.ShowStats = !player.ShowStats;
        }

        // Shows the player's frame on the top screen, with the input display (if
        // it's on and the clip has inputs).
        void ShowFrame()
        {
            FlbClip clip = player.Clip;
            if (player.ShownEntry < 0)
                return;
            FlbInput? input = player.ShowInputs && clip.Inputs != null ? clip.Inputs[player.Frame] : (FlbInput?)null;
            player.DrawMs = top.Show(player.Pixels, clip.Width, clip.Height, input);
        }

        // After a key or a click: both screens up to date now.
        void PlayerChanged()
        {
            if (player == null)
                return;
            player.Tick();
            ShowFrame();
            bottom.Invalidate();
        }

        void Tick()
        {
            if (mode == Mode.Job)
            {
                bottom.Invalidate();
                return;
            }
            if (player == null)
                return;
            if (player.Tick())
                ShowFrame();
            // (The bottom screen, at most 25 times a second.)
            long now = Stopwatch.GetTimestamp();
            if (now - panelShown > Stopwatch.Frequency / 25)
            {
                panelShown = now;
                bottom.Invalidate();
            }
        }

        // Closes the player and saves the selection as a new clip next to it.
        void SaveSelection()
        {
            Player pl = player;
            int first = pl.TrimIn, count = pl.TrimOut + 1 - pl.TrimIn, fps = pl.Clip.Fps;
            long bytes = FlbCompactor.TrimSize(pl.Clip, first, count);
            string source = pl.Clip.Path;
            ClosePlayer();
            string name = Names.FreeName(folder, Path.GetFileName(source), ".flb");
            if (name == null)
            {
                SetMessage("There's no free name for the trimmed clip.", false);
                return;
            }
            long needed = bytes + 1000000;
            if (FreeSpace(source) < needed)
            {
                SetMessage($"Not enough free space: trimming needs {Names.Size(needed)} free.", false);
                return;
            }
            string outPath = Path.Combine(folder, name);
            RunJob("Trimming...", name, token =>
            {
                Stopwatch sw = Stopwatch.StartNew();
                FlbCompactor work = FlbCompactor.Trim(source, outPath, first, count);
                while (!work.Step())
                {
                    if (token.IsCancellationRequested)
                    {
                        work.Cancel();
                        return ("Trimming cancelled; nothing was saved.", true, null);
                    }
                    Progress((double)work.Copied / Math.Max(1, work.Total), $"{Names.Size(work.Copied)} of {Names.Size(work.Total)} copied");
                }
                long size = new FileInfo(outPath).Length;
                // (The new clip is selected: its name is just above.)
                return ($"Trimmed: saved as a new clip ({Names.Length(count, fps)}, {Names.Size(size)}) in {(int)sw.Elapsed.TotalSeconds} s.",
                        true, name);
            });
        }

        // Closes the player and asks to export the clip, or the trimmed part.
        void ExportFromPlayer()
        {
            if (!CheckFfmpeg())
                return;
            Player pl = player;
            bool part = pl.TrimMode && !pl.TrimIsWhole;
            exportPath = pl.Clip.Path;
            exportFirst = part ? pl.TrimIn : 0;
            exportCount = part ? pl.TrimOut + 1 - pl.TrimIn : pl.Count;
            ClosePlayer();
            int i = clips.FindIndex(c => string.Equals(c.Path, exportPath, StringComparison.OrdinalIgnoreCase));
            if (i < 0)
                return;
            selected = i;
            ClampList();
            string stem = Path.GetFileNameWithoutExtension(exportPath);
            AskExportName(part ? Path.GetFileNameWithoutExtension(Names.FreeName(ExportDir(), stem + ".mp4", ".mp4") ?? stem + "-trim") : stem);
            bottom.Invalidate();
        }

        void AskSave()
        {
            if (player.TrimIsWhole)
                player.TrimMessage = "Set a start (I) or an end (O) first.";
            else
            {
                askSave = true;
                player.Playing = false;
            }
        }

        void ToggleLoop() => player.Loop = !player.Loop;

        // Playback's volume (0 to 100): the player's sound only, as exports are
        // as recorded. Remembered for next time.
        void SetVolume(int percent, bool remember = true)
        {
            volume = Math.Max(0, Math.Min(100, percent));
            if (player != null)
                player.Volume = volume;
            if (remember)
                SaveSettings();
            bottom.Invalidate();
        }

        // The volume slider, in the progress bar's colours: dragging along it
        // (or clicking it) sets the volume.
        void PaintVolume(Painter p, Rectangle bar)
        {
            int at = (int)((long)volume * (bar.Width - 1) / 100), grow = Math.Max(3, S(3)), knob = Math.Max(3, S(3));
            p.Fill(new Rectangle(bar.X, bar.Y, at + 1, bar.Height), Theme.Red);
            p.Fill(new Rectangle(bar.X + at + 1, bar.Y, bar.Width - at - 1, bar.Height), Theme.Rest);
            p.Fill(new Rectangle(bar.X + at - knob / 2, bar.Y - grow, knob, bar.Height + 2 * grow), Theme.Head);
            p.Draggable(new Rectangle(bar.X - S(4), bar.Y - p.RowH / 2, bar.Width + S(8), bar.Height + p.RowH),
                        (along, done) => SetVolume((int)Math.Round(along * 100), done));
        }

        // The bar pixel showing clip frame 'frame', for a bar 'width' wide.
        int BarPixel(int frame, int width) => player.Count > 1 ? (int)((long)frame * (width - 1) / (player.Count - 1)) : 0;

        // The progress bar. While trimming, the parts outside the selection are
        // dark and its start and end are marked. Red marks under it show where
        // frames were missed. Clicking or dragging along it jumps there (with no
        // sound meanwhile).
        void PaintBar(Painter p, Rectangle bar)
        {
            Player pl = player;
            int w = bar.Width, head = BarPixel(pl.Frame, w), tin = BarPixel(pl.TrimIn, w), tout = BarPixel(pl.TrimOut, w);
            int grow = Math.Max(3, S(3)), tickY = bar.Bottom + grow + S(2), tickH = Math.Max(2, S(2));
            if (pl.TrimMode)
            {
                p.Fill(new Rectangle(bar.X, bar.Y, w, bar.Height), Theme.Cut);
                p.Fill(new Rectangle(bar.X + tin, bar.Y, Math.Max(0, Math.Min(head, tout) - tin + 1), bar.Height), Theme.Red);
                if (head < tout)
                    p.Fill(new Rectangle(bar.X + Math.Max(head + 1, tin), bar.Y, tout - Math.Max(head + 1, tin) + 1, bar.Height), Theme.Rest);
            }
            else
            {
                p.Fill(new Rectangle(bar.X, bar.Y, head + 1, bar.Height), Theme.Red);
                p.Fill(new Rectangle(bar.X + head + 1, bar.Y, w - head - 1, bar.Height), Theme.Rest);
            }
            FlbClip clip = pl.Clip;
            for (int e = 0; e < clip.EntryCount; e++)
                for (int k = 1; k < clip[e].Copies; k++)
                    p.Fill(new Rectangle(bar.X + BarPixel(clip[e].FirstFrame + k, w), tickY, Math.Max(1, S(1)), tickH), Theme.Missed);
            int mark = Math.Max(2, S(2)), playhead = Math.Max(3, S(3));
            if (pl.TrimMode)
            {
                p.Fill(new Rectangle(bar.X + tin, bar.Y - grow, mark, bar.Height + 2 * grow), Theme.Mark);
                p.Fill(new Rectangle(bar.X + tout - mark + 1, bar.Y - grow, mark, bar.Height + 2 * grow), Theme.Mark);
            }
            p.Fill(new Rectangle(bar.X + head - playhead / 2, bar.Y - grow, playhead, bar.Height + 2 * grow), Theme.Head);

            p.Draggable(new Rectangle(bar.X, bar.Y - p.RowH, bar.Width, bar.Height + 2 * p.RowH), (at, done) =>
            {
                if (player == null || askSave)
                    return;
                player.Scrubbing = !done;
                player.Seek((int)Math.Round(at * (player.Count - 1)));
                PlayerChanged();
            });
        }

        // An action on the player, then both screens brought up to date.
        Action WithPlayer(Action action) => action == null ? null : (Action)(() =>
        {
            if (player == null)
                return;
            action();
            if (mode == Mode.Player)
                PlayerChanged();
        });

        void PaintPlayer(Painter p)
        {
            Player pl = player;
            FlbClip clip = pl.Clip;
            p.Row(0, Style.Title, " " + pl.Title);
            p.Text(1, 1, Style.Dim, Names.Resolution(clip.Width, clip.Height, clip.Fps, clip.HasAudio));

            Rectangle bar = p.Cells(3, 1, BottomScreen.Cols - 2);
            PaintBar(p, new Rectangle(bar.X, bar.Y, bar.Width, Math.Max(6, p.RowH * 2 / 3)));

            p.Text(6, 1, Style.Normal, $"{Names.Time(pl.Frame, clip.Fps)} / {Names.Time(pl.Count, clip.Fps)}".PadRight(22) +
                                       $"Frame {pl.Frame + 1}/{pl.Count}");
            p.Text(7, 1, Style.Normal, $"{(pl.Playing ? "Playing" : "Paused"),-8} {Player.SpeedNames[pl.Speed],-6} Loop {(pl.Loop ? "on" : "off")}");

            if (pl.ShownEntry >= 0 && pl.IsMissed(pl.Frame))
                p.Text(8, 1, Style.Warn, "Missed frame (copy of the one before)");

            if (!clip.HasAudio)
                p.Text(9, 1, Style.Dim, "No sound (recorded without audio)");
            else if (pl.AudioError != null)
                p.Text(9, 1, Style.Warn, "No sound: " + pl.AudioError);
            else if (pl.AudioOn)
                p.Text(9, 1, Style.Normal, "Sound on");
            else if (!pl.Playing)
                p.Text(9, 1, Style.Dim, "Sound: muted while paused");
            else if (pl.Speed != Player.SpeedNormal)
                p.Text(9, 1, Style.Dim, $"Sound: muted at {Player.SpeedNames[pl.Speed]} speed");
            else
                p.Text(9, 1, Style.Dim, "Sound: muted");

            // The volume: drag along it, or +/-.
            Style sound = clip.HasAudio ? Style.Normal : Style.Dim;
            p.Text(10, 1, sound, "Volume");
            Rectangle slider = p.Cells(10, 9, 24);
            PaintVolume(p, new Rectangle(slider.X, slider.Y + slider.Height * 3 / 8, slider.Width, Math.Max(4, slider.Height / 4)));
            p.Text(10, 34, sound, $"{volume}%");

            if (pl.Error != null)
                p.Wrap(11, 13, Style.Warn, pl.Error, 1);
            else if (pl.ShowStats)
            {
                p.Text(11, 1, Style.Dim, $"Read+decode {pl.DecodeMs:0.0}  draw {pl.DrawMs:0.0} ms");
                p.Text(12, 1, Style.Dim, $"Frames skipped to keep up: {pl.Skipped}");
                if (pl.AudioOn)
                    p.Text(13, 1, Style.Dim, $"Sound vs picture: {pl.AudioOffsetMs:+0;-0;0} ms");
            }

            if (pl.TrimMode)
            {
                p.Text(14, 1, Style.Info, $"Keep {Names.Time(pl.TrimIn, clip.Fps)} to {Names.Time(pl.TrimOut + 1, clip.Fps)} " +
                                          $"({Names.Time(pl.TrimOut + 1 - pl.TrimIn, clip.Fps)})");
                if (askSave)
                {
                    const string ask = "Save it as a new clip? ";
                    p.Text(15, 1, Style.Key, ask);
                    int c2 = Choice(p, 15, 1 + ask.Length, "Save", SaveSelection, true) + 1;
                    Choice(p, 15, c2, "No", () => { askSave = false; bottom.Invalidate(); }, false);
                }
                else if (pl.TrimMessage != null)
                    p.Text(15, 1, Style.Warn, pl.TrimMessage);
            }

            int sep = 17;
            p.Text(sep, 0, Style.Dim, new string('-', BottomScreen.Cols));
            int y = (sep + 1) * p.RowH + S(4), gap = S(6);
            if (pl.SettingsOpen)
            {
                // The settings, over the buttons while they're open.
                p.Text(sep + 1, 1, Style.Info, "Settings");
                y += p.RowH;
                bool[] on = { pl.Loop, pl.ShowInputs, pl.ShowStats };
                for (int i = 0; i < SettingNames.Length; i++)
                {
                    int item = i;
                    p.Switch(p.Band(y, S(28)), SettingNames[i], on[i], WithPlayer(() => ToggleSetting(item)),
                             i == 1 && clip.Inputs == null ? "(none in this clip)" : null);
                    y += S(28) + S(4);
                }
                y += gap;
                p.Button(p.Split(p.Band(y, S(28)), 3, gap)[0], "Close", WithPlayer(() => player.SettingsOpen = false));
            }
            else
            {
                // Like a media player's: a second back, a frame back, play/pause, a
                // frame on, a second on; with their keys under them.
                Rectangle[] t = p.Split(p.Band(y, S(34)), 5, gap);
                string play = pl.Playing ? "Pause" : pl.TrimMode ? "Play the selection" : "Play";
                p.Button(t[0], null, WithPlayer(() => pl.Jump(-1)), glyph: Glyph.SecondBack, tip: "Back 1 second (Left)");
                p.Button(t[1], null, WithPlayer(() => pl.Step(-1)), glyph: Glyph.FrameBack, tip: "Previous frame (,)");
                p.Button(t[2], null, WithPlayer(pl.PlayPause), Look.Red, pl.Playing ? Glyph.Pause : Glyph.Play, play + " (Space)");
                p.Button(t[3], null, WithPlayer(() => pl.Step(1)), glyph: Glyph.FrameOn, tip: "Next frame (.)");
                p.Button(t[4], null, WithPlayer(() => pl.Jump(1)), glyph: Glyph.SecondOn, tip: "Forward 1 second (Right)");
                y += S(34) + S(2);
                string[] keys = { "Left", ",", "Space", ".", "Right" };
                for (int i = 0; i < 5; i++)
                    p.TextIn(new Rectangle(t[i].X, y, t[i].Width, p.RowH), keys[i], Theme.Fore(Style.Dim), bottom.Regular);
                y += p.RowH + gap;

                if (pl.TrimMode)
                {
                    Rectangle[] a = p.Split(p.Band(y, S(28)), 2, gap);
                    p.Button(a[0], "Start here", WithPlayer(pl.SetTrimIn), tip: "Start the selection at this frame");
                    p.Button(a[1], "End here", WithPlayer(pl.SetTrimOut), tip: "End the selection at this frame");
                    y += S(28) + gap;
                    a = p.Split(p.Band(y, S(28)), 2, gap);
                    p.Button(a[0], "Save as a new clip", WithPlayer(AskSave), Look.Red, tip: "Save the selection next to this clip, as <name>-trim.flb");
                    p.Button(a[1], pl.Loop ? "Loop: on" : "Loop: off", WithPlayer(ToggleLoop), tip: "Play the selection over and over, or once");
                    y += S(28) + gap;
                    a = p.Split(p.Band(y, S(28)), 2, gap);
                    p.Button(a[0], "Export the selection", WithPlayer(ExportFromPlayer), tip: "Export the selection as an MP4");
                    p.Button(a[1], "Stop trimming", WithPlayer(pl.ToggleTrim));
                }
                else
                {
                    // The speed: one of four.
                    Rectangle band = p.Band(y, S(26));
                    p.TextIn(new Rectangle(band.X, band.Y, 7 * p.CellW, band.Height), "Speed", Theme.Fore(Style.Normal), bottom.Regular, true);
                    Rectangle[] speeds = p.Split(new Rectangle(band.X + 7 * p.CellW, band.Y, band.Width - 7 * p.CellW, band.Height), 4, S(4));
                    for (int i = 0; i < speeds.Length; i++)
                    {
                        int speed = i;
                        p.Button(speeds[i], Player.SpeedNames[i], WithPlayer(() => pl.ChangeSpeed(speed - pl.Speed)),
                                 i == pl.Speed ? Look.Red : Look.Normal, tip: i == Player.SpeedNormal ? "Play at normal speed (with sound)" :
                                                                         $"Play at {Player.SpeedNames[i]} speed (without sound)");
                    }
                    y += S(26) + gap + S(4);
                    Rectangle[] a = p.Split(p.Band(y, S(28)), 3, gap);
                    p.Button(a[0], "Settings", WithPlayer(() => pl.SettingsOpen = true), tip: "Loop, input display, timing details");
                    p.Button(a[1], "Trim", pl.Clip.IsRaw ? null : WithPlayer(pl.ToggleTrim), tip: "Keep part of the clip");
                    p.Button(a[2], "Export as MP4", WithPlayer(ExportFromPlayer), tip: "Export the clip as an MP4 video");
                    y += S(28) + gap;
                    p.Button(p.Band(y, S(28)), "Back to the clip list", WithPlayer(ClosePlayer));
                }
            }
            p.Text(p.Rows - 1, 1, Style.Dim, p.Tip ?? "Click or drag along the bar to jump.");
        }

        // ---- Closing

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            if (job != null)
            {
                e.Cancel = true;
                if (!closing &&
                    MessageBox.Show(this, "Stop and close? " + (jobTitle == "Exporting..." ? "The MP4 being exported won't be saved."
                                                                                           : "The clip is left as it was."),
                                    Text, MessageBoxButtons.YesNo, MessageBoxIcon.Question) == DialogResult.Yes)
                {
                    closing = true;
                    CancelJob();
                }
            }
            base.OnFormClosing(e);
        }

        protected override void OnFormClosed(FormClosedEventArgs e)
        {
            SaveSettings();     // (the window, as it was)
            player?.Dispose();
            player = null;
            if (fineTimer)
                timeEndPeriod(1);
            thumbs.Dispose();
            base.OnFormClosed(e);
        }
    }
}
