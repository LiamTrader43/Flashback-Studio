using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Threading;
using System.Windows.Forms;

namespace FlashbackConverter
{
    // The window: a list of clips, which Convert turns into MP4s, each next to
    // its clip.
    sealed class MainForm : Form
    {
        enum State { Idle, Waiting, Converting, Done, Failed, Stopped }

        // A clip in the list.
        sealed class Clip
        {
            public string Path;
            public string Error;        // why it can't be converted (null if it can)
            public int FrameCount;
            public State State;
            public int Percent;         // (while converting)
            public string Failure;      // (once failed)
            public ListViewItem Row;

            public string Mp4 => System.IO.Path.ChangeExtension(Path, ".mp4");
        }

        const string FfmpegMissing = "ffmpeg.exe wasn't found. Put it next to FlashbackConverter.exe (it comes with " +
                                     "it in the release), or install ffmpeg and add its bin folder to PATH.";

        readonly List<Clip> clips = new List<Clip>();
        readonly ListView list;
        readonly Label hint, status;
        readonly CheckBox showInputs, replace;
        readonly ProgressBar bar;
        readonly Button addClips, addFolder, remove, openFolder, convert;
        Thread worker;
        CancellationTokenSource cancel;
        bool closeWhenStopped;

        // A size in pixels at 100% display scaling, at this display's.
        int S(int px) => px * DeviceDpi / 96;

        public MainForm(string[] paths)
        {
            Text = "Flashback Converter";
            Font = SystemFonts.MessageBoxFont;
            AutoScaleMode = AutoScaleMode.None;
            ClientSize = new Size(S(820), S(440));
            MinimumSize = new Size(S(600), S(320));
            StartPosition = FormStartPosition.CenterScreen;
            AllowDrop = true;

            addClips = MakeButton("Add clips…");
            addFolder = MakeButton("Add folder…");
            remove = MakeButton("Remove");
            openFolder = MakeButton("Open folder");
            convert = MakeButton("Convert");

            list = new ListView
            {
                Dock = DockStyle.Fill,
                View = View.Details,
                FullRowSelect = true,
                HideSelection = false,
                ShowItemToolTips = true,
                AllowDrop = true,
            };
            list.Columns.Add("Clip", S(250));
            list.Columns.Add("Length", S(60));
            list.Columns.Add("Video", S(130));
            list.Columns.Add("Sound", S(55));
            list.Columns.Add("Inputs", S(55));
            list.Columns.Add("Status", S(200));
            hint = new Label
            {
                Dock = DockStyle.Fill,
                Text = "Drag clips (.flb) or folders of clips here,\nor use Add clips… or Add folder…",
                TextAlign = ContentAlignment.MiddleCenter,
                BackColor = SystemColors.Window,
                ForeColor = SystemColors.GrayText,
                AllowDrop = true,
            };
            Panel listPanel = new Panel { Dock = DockStyle.Fill, Margin = new Padding(0, S(6), 0, S(6)), BorderStyle = BorderStyle.None };
            listPanel.Controls.Add(list);
            listPanel.Controls.Add(hint);
            hint.BringToFront();

            showInputs = new CheckBox
            {
                Text = "Show the input display in the videos (for clips recorded with their inputs)",
                AutoSize = true,
                Checked = true,
                Margin = new Padding(0, 0, 0, S(2)),
            };
            replace = new CheckBox { Text = "Replace MP4s that already exist", AutoSize = true, Margin = new Padding(0, 0, 0, S(6)) };
            bar = new ProgressBar { Dock = DockStyle.Fill, Height = S(18), Maximum = 1000, Margin = new Padding(0, 0, 0, S(6)) };
            status = new Label { AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(0, 0, S(8), 0), MaximumSize = new Size(S(560), 0) };

            FlowLayoutPanel top = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = Padding.Empty };
            top.Controls.AddRange(new Control[] { addClips, addFolder, remove });
            TableLayoutPanel bottom = new TableLayoutPanel { AutoSize = true, Dock = DockStyle.Fill, ColumnCount = 3, Margin = Padding.Empty };
            bottom.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            bottom.ColumnStyles.Add(new ColumnStyle());
            bottom.ColumnStyles.Add(new ColumnStyle());
            bottom.Controls.Add(status, 0, 0);
            bottom.Controls.Add(openFolder, 1, 0);
            bottom.Controls.Add(convert, 2, 0);
            convert.Margin = Padding.Empty;

            TableLayoutPanel layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, Padding = new Padding(S(10)) };
            layout.RowStyles.Add(new RowStyle());
            layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            layout.RowStyles.Add(new RowStyle());
            layout.RowStyles.Add(new RowStyle());
            layout.RowStyles.Add(new RowStyle());
            layout.RowStyles.Add(new RowStyle());
            layout.Controls.Add(top, 0, 0);
            layout.Controls.Add(listPanel, 0, 1);
            layout.Controls.Add(showInputs, 0, 2);
            layout.Controls.Add(replace, 0, 3);
            layout.Controls.Add(bar, 0, 4);
            layout.Controls.Add(bottom, 0, 5);
            Controls.Add(layout);
            AcceptButton = convert;

            addClips.Click += (s, e) => ChooseClips();
            addFolder.Click += (s, e) => ChooseFolder();
            remove.Click += (s, e) => RemoveSelected();
            openFolder.Click += (s, e) => OpenFolder();
            convert.Click += (s, e) =>
            {
                if (worker != null)
                    cancel.Cancel();
                else
                    StartConverting();
            };
            replace.CheckedChanged += (s, e) => UpdateRows();
            list.SelectedIndexChanged += (s, e) => UpdateButtons();
            list.Resize += (s, e) => FitStatusColumn();
            list.KeyDown += (s, e) =>
            {
                if (e.KeyCode == Keys.Delete && worker == null)
                    RemoveSelected();
                else if (e.KeyCode == Keys.A && e.Control)
                    foreach (ListViewItem row in list.Items)
                        row.Selected = true;
            };
            foreach (Control c in new Control[] { this, list, hint })
            {
                c.DragEnter += (s, e) =>
                    e.Effect = worker == null && e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None;
                c.DragDrop += (s, e) =>
                {
                    if (worker == null && e.Data.GetData(DataFormats.FileDrop) is string[] dropped)
                        AddPaths(dropped);
                };
            }

            AddPaths(paths);
            if (Converter.FindFfmpeg() == null)
                ShowStatus(FfmpegMissing, true);
            UpdateButtons();
        }

        Button MakeButton(string text)
        {
            return new Button
            {
                Text = text,
                AutoSize = true,
                MinimumSize = new Size(S(96), S(28)),
                Padding = new Padding(S(6), 0, S(6), 0),
                Margin = new Padding(0, 0, S(6), 0),
                UseVisualStyleBackColor = true,
            };
        }

        void ShowStatus(string text, bool bad = false)
        {
            status.Text = text;
            status.ForeColor = bad ? Color.Firebrick : SystemColors.ControlText;
        }

        // Makes the Status column fill the rest of the list's width.
        void FitStatusColumn()
        {
            int others = 0;
            for (int i = 0; i < list.Columns.Count - 1; i++)
                others += list.Columns[i].Width;
            list.Columns[list.Columns.Count - 1].Width = Math.Max(S(120), list.ClientSize.Width - others);
        }

        protected override void OnShown(EventArgs e)
        {
            base.OnShown(e);
            FitStatusColumn();
        }

        // ---- Adding and removing clips

        static bool IsClipFile(string path)
        {
            string ext = Path.GetExtension(path);
            return string.Equals(ext, ".flb", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(ext, ".raw", StringComparison.OrdinalIgnoreCase);
        }

        // The clips in a folder (not its subfolders): .flb clips, and .raw clips
        // from older builds (which always have a .txt next to them).
        static IEnumerable<string> ClipsIn(string folder)
        {
            return Directory.EnumerateFiles(folder)
                .Where(f => string.Equals(Path.GetExtension(f), ".flb", StringComparison.OrdinalIgnoreCase) ||
                            (string.Equals(Path.GetExtension(f), ".raw", StringComparison.OrdinalIgnoreCase) &&
                             File.Exists(Path.ChangeExtension(f, ".txt"))))
                .OrderBy(f => f, StringComparer.OrdinalIgnoreCase);
        }

        void ChooseClips()
        {
            using (OpenFileDialog d = new OpenFileDialog
            {
                Title = "Add clips",
                Filter = "Flashback clips (*.flb, *.raw)|*.flb;*.raw|All files (*.*)|*.*",
                Multiselect = true,
            })
                if (d.ShowDialog(this) == DialogResult.OK)
                    AddPaths(d.FileNames);
        }

        void ChooseFolder()
        {
            using (FolderBrowserDialog d = new FolderBrowserDialog
            {
                Description = "Add the clips in this folder:",
                ShowNewFolderButton = false,
            })
                if (d.ShowDialog(this) == DialogResult.OK)
                    AddPaths(new[] { d.SelectedPath });
        }

        // Adds clips, and the clips in folders, that aren't in the list already.
        void AddPaths(IEnumerable<string> paths)
        {
            int added = 0;
            bool any = false;
            list.BeginUpdate();
            foreach (string given in paths)
            {
                string path;
                try
                {
                    path = Path.GetFullPath(given);
                }
                catch (Exception e) when (e is ArgumentException || e is NotSupportedException || e is IOException)
                {
                    continue;
                }
                IEnumerable<string> files;
                if (Directory.Exists(path))
                {
                    try
                    {
                        files = ClipsIn(path).ToList();
                    }
                    catch (Exception e) when (e is IOException || e is UnauthorizedAccessException)
                    {
                        continue;
                    }
                }
                else if (IsClipFile(path) && File.Exists(path))
                    files = new[] { path };
                else
                    continue;
                foreach (string file in files)
                {
                    any = true;
                    if (!clips.Any(c => string.Equals(c.Path, file, StringComparison.OrdinalIgnoreCase)))
                    {
                        AddClip(file);
                        added++;
                    }
                }
            }
            list.EndUpdate();
            if (paths.Any())
            {
                if (!any)
                    ShowStatus("There are no Flashback clips (.flb) in what was added.", true);
                else
                    ShowStatus(added == 1 ? "Added 1 clip." : added == 0 ? "Those clips are in the list already." : $"Added {added} clips.");
            }
            hint.Visible = clips.Count == 0;
            UpdateButtons();
        }

        void AddClip(string path)
        {
            Clip clip = new Clip { Path = path };
            ListViewItem row = new ListViewItem(Path.GetFileName(path)) { Tag = clip, UseItemStyleForSubItems = false };
            for (int i = 1; i < list.Columns.Count; i++)
                row.SubItems.Add("");
            try
            {
                using (FlbClip c = FlbClip.Open(path))
                {
                    clip.FrameCount = c.FrameCount;
                    int seconds = (int)Math.Round((double)c.FrameCount / c.Fps);
                    row.SubItems[1].Text = $"{seconds / 60}:{seconds % 60:00}";
                    row.SubItems[2].Text = $"{c.Width} × {c.Height}, {c.Fps} fps";
                    row.SubItems[3].Text = c.HasAudio ? "Yes" : "No";
                    row.SubItems[4].Text = c.Inputs != null ? "Yes" : "No";
                }
            }
            catch (InvalidDataException e)
            {
                clip.Error = e.Message;
            }
            clip.Row = row;
            clips.Add(clip);
            list.Items.Add(row);
            UpdateRow(clip);
        }

        void RemoveSelected()
        {
            list.BeginUpdate();
            foreach (ListViewItem row in list.SelectedItems.Cast<ListViewItem>().ToList())
            {
                clips.Remove((Clip)row.Tag);
                list.Items.Remove(row);
            }
            list.EndUpdate();
            hint.Visible = clips.Count == 0;
            UpdateButtons();
        }

        // Opens the folder of the selected clip (or the first one) in Explorer,
        // with its MP4 selected if it has one.
        void OpenFolder()
        {
            Clip clip = list.SelectedItems.Count > 0 ? (Clip)list.SelectedItems[0].Tag : clips.FirstOrDefault();
            if (clip == null)
                return;
            string show = File.Exists(clip.Mp4) ? clip.Mp4 : clip.Path;
            try
            {
                Process.Start("explorer.exe", "/select,\"" + show + "\"");
            }
            catch (Exception e) when (e is System.ComponentModel.Win32Exception || e is InvalidOperationException)
            {
                ShowStatus("Explorer couldn't be opened: " + e.Message, true);
            }
        }

        // ---- The list

        static string Sentence(string s) => s.Length > 0 ? char.ToUpperInvariant(s[0]) + s.Substring(1) : s;

        void UpdateRow(Clip clip)
        {
            string text;
            bool bad = false;
            if (clip.Error != null)
            {
                text = Sentence(clip.Error);
                bad = true;
            }
            else
            {
                switch (clip.State)
                {
                    case State.Waiting: text = "Waiting"; break;
                    case State.Converting: text = $"Converting… {clip.Percent}%"; break;
                    case State.Done: text = "Done"; break;
                    case State.Failed: text = "Failed: " + clip.Failure; bad = true; break;
                    case State.Stopped: text = "Stopped"; break;
                    default:
                        text = !File.Exists(clip.Mp4) ? "Ready" :
                               replace.Checked ? "Ready (replaces its MP4)" : "Its MP4 already exists";
                        break;
                }
            }
            ListViewItem.ListViewSubItem cell = clip.Row.SubItems[list.Columns.Count - 1];
            cell.Text = text;
            cell.ForeColor = bad ? Color.Firebrick : SystemColors.WindowText;
            clip.Row.ToolTipText = clip.Path + "\n" + text;     // (the whole message, if it doesn't fit)
        }

        void UpdateRows()
        {
            list.BeginUpdate();
            foreach (Clip clip in clips)
                UpdateRow(clip);
            list.EndUpdate();
        }

        void UpdateButtons()
        {
            bool idle = worker == null;
            addClips.Enabled = addFolder.Enabled = showInputs.Enabled = replace.Enabled = idle;
            remove.Enabled = idle && list.SelectedItems.Count > 0;
            openFolder.Enabled = clips.Count > 0;
            convert.Enabled = !idle || clips.Count > 0;
            convert.Text = idle ? "Convert" : "Stop";
        }

        // ---- Converting

        // Converts every clip that can be, and doesn't have an MP4 (or all of them,
        // with Replace), one after another, on a thread of its own.
        void StartConverting()
        {
            string ffmpeg = Converter.FindFfmpeg();
            if (ffmpeg == null)
            {
                ShowStatus(FfmpegMissing, true);
                MessageBox.Show(this, FfmpegMissing, Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            List<Clip> jobs = new List<Clip>();
            int skipped = 0;
            foreach (Clip clip in clips)
            {
                if (clip.Error != null)
                    continue;
                if (!replace.Checked && File.Exists(clip.Mp4))
                {
                    if (clip.State != State.Done)
                    {
                        clip.State = State.Idle;
                        skipped++;
                    }
                }
                else
                {
                    clip.State = State.Waiting;
                    jobs.Add(clip);
                }
            }
            UpdateRows();
            if (jobs.Count == 0)
            {
                ShowStatus(clips.Any(c => c.Error == null)
                    ? "Every clip has its MP4 already. To convert them again, tick Replace MP4s that already exist."
                    : "None of these clips can be converted (the list says why).", true);
                return;
            }

            bool inputs = showInputs.Checked;
            cancel = new CancellationTokenSource();
            CancellationToken token = cancel.Token;
            worker = new Thread(() => Convert(jobs, skipped, ffmpeg, inputs, token)) { IsBackground = true };
            bar.Value = 0;
            UpdateButtons();
            worker.Start();
        }

        // Runs 'update' on the window's thread.
        void Ui(Action update)
        {
            try
            {
                BeginInvoke(update);
            }
            catch (InvalidOperationException)
            {
                // (the window is gone)
            }
        }

        // (On the converting thread.)
        void Convert(List<Clip> jobs, int skipped, string ffmpeg, bool inputs, CancellationToken token)
        {
            long total = Math.Max(1, jobs.Sum(j => (long)j.FrameCount)), before = 0;
            int converted = 0, failed = 0;
            bool stopped = false;
            Stopwatch shown = Stopwatch.StartNew();
            for (int i = 0; i < jobs.Count && !stopped; i++)
            {
                Clip clip = jobs[i];
                string about = $"Converting clip {i + 1} of {jobs.Count}: {Path.GetFileName(clip.Path)}";
                Ui(() =>
                {
                    clip.State = State.Converting;
                    clip.Percent = 0;
                    UpdateRow(clip);
                    clip.Row.EnsureVisible();
                    ShowStatus(about);
                });

                State result;
                string failure = null;
                try
                {
                    using (FlbClip c = FlbClip.Open(clip.Path))
                    {
                        long start = before;
                        int frames = c.FrameCount;
                        Converter.ToMp4(c, clip.Mp4, inputs, ffmpeg, done =>
                        {
                            if (shown.ElapsedMilliseconds < 100)
                                return;
                            shown.Restart();
                            Ui(() =>
                            {
                                clip.Percent = (int)(100L * done / Math.Max(1, frames));
                                UpdateRow(clip);
                                bar.Value = (int)(1000 * (start + done) / total);
                            });
                        }, token);
                    }
                    result = State.Done;
                    converted++;
                }
                catch (OperationCanceledException)
                {
                    result = State.Stopped;
                    stopped = true;
                }
                catch (Exception e)
                {
                    result = State.Failed;
                    failure = e.Message;
                    failed++;
                }
                before += clip.FrameCount;
                long through = before;
                Ui(() =>
                {
                    clip.State = result;
                    clip.Failure = failure;
                    UpdateRow(clip);
                    bar.Value = (int)(1000 * through / total);
                });
            }

            Ui(() =>
            {
                foreach (Clip clip in jobs.Where(c => c.State == State.Waiting))
                    clip.State = State.Idle;
                UpdateRows();
                worker = null;
                UpdateButtons();
                string summary = (stopped ? "Stopped: " : "Done: ") + $"{converted} converted";
                if (skipped > 0)
                    summary += skipped == 1 ? ", 1 skipped (its MP4 already exists)" : $", {skipped} skipped (their MP4s already exist)";
                if (failed > 0)
                    summary += $", {failed} failed";
                ShowStatus(summary + ".", failed > 0);
                if (!stopped)
                    bar.Value = bar.Maximum;
                if (closeWhenStopped)
                    Close();
            });
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            if (worker != null)
            {
                e.Cancel = true;
                if (!closeWhenStopped &&
                    MessageBox.Show(this, "Stop converting and close? The clip being converted will need converting again.",
                                    Text, MessageBoxButtons.YesNo, MessageBoxIcon.Question) == DialogResult.Yes)
                {
                    closeWhenStopped = true;
                    cancel.Cancel();
                }
            }
            base.OnFormClosing(e);
        }
    }
}
