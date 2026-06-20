using System.ComponentModel;
using System.Diagnostics;
using System.Drawing;

namespace UE5LibraryBrowser;

internal sealed class MainForm : Form
{
    private readonly ToolStrip _toolbar = new();
    private readonly ToolStripButton _openButton = new("打开素材库");
    private readonly ToolStripButton _refreshButton = new("刷新");
    private readonly ToolStripButton _openModelButton = new("打开模型");
    private readonly ToolStripButton _openFolderButton = new("打开目录");
    private readonly ToolStripLabel _statusLabel = new("请选择 UE5 素材库");
    private readonly TextBox _modelFilter = new();
    private readonly TextBox _animationFilter = new();
    private readonly DataGridView _modelGrid = new();
    private readonly DataGridView _animationGrid = new();
    private readonly Label _modelHeader = new();
    private readonly Label _animationHeader = new();
    private readonly TextBox _details = new();
    private readonly Image _placeholder = BuildPlaceholderImage();

    private UeLibraryIndex? _index;
    private ThumbnailService? _thumbnails;
    private PreviewComposer? _previewComposer;
    private CancellationTokenSource? _thumbnailCts;
    private string _root = "";
    private string? _initialRoot;

    public MainForm(string? initialRoot)
    {
        _initialRoot = initialRoot;
        Text = "UE5 Library Browser";
        Width = 1500;
        Height = 920;
        MinimumSize = new Size(1100, 700);

        BuildLayout();
        WireEvents();
    }

    protected override async void OnShown(EventArgs e)
    {
        base.OnShown(e);
        var root = !string.IsNullOrWhiteSpace(_initialRoot)
            ? _initialRoot
            : Directory.Exists(@"F:\UE-Assets\nte-reusable-library")
                ? @"F:\UE-Assets\nte-reusable-library"
                : Directory.Exists(@"F:\UE-Assets\nte-useful-assets")
                    ? @"F:\UE-Assets\nte-useful-assets"
                    : "";

        if (!string.IsNullOrWhiteSpace(root))
            await OpenLibraryAsync(root);
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        _thumbnailCts?.Cancel();
        base.OnClosing(e);
    }

    private void BuildLayout()
    {
        _toolbar.GripStyle = ToolStripGripStyle.Hidden;
        _toolbar.Items.AddRange([_openButton, _refreshButton, new ToolStripSeparator(), _openModelButton, _openFolderButton, new ToolStripSeparator(), _statusLabel]);
        Controls.Add(_toolbar);

        var split = new SplitContainer
        {
            Dock = DockStyle.Fill,
            SplitterDistance = 760,
            Orientation = Orientation.Vertical
        };
        Controls.Add(split);
        split.BringToFront();

        var left = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            RowCount = 3,
            ColumnCount = 1,
            Padding = new Padding(8)
        };
        left.RowStyles.Add(new RowStyle(SizeType.Absolute, 28));
        left.RowStyles.Add(new RowStyle(SizeType.Absolute, 32));
        left.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        split.Panel1.Controls.Add(left);

        _modelHeader.Dock = DockStyle.Fill;
        _modelHeader.TextAlign = ContentAlignment.MiddleLeft;
        _modelHeader.Font = new Font(Font, FontStyle.Bold);
        left.Controls.Add(_modelHeader, 0, 0);

        _modelFilter.Dock = DockStyle.Fill;
        _modelFilter.PlaceholderText = "筛选模型、路径、Skeleton...";
        left.Controls.Add(_modelFilter, 0, 1);

        ConfigureModelGrid();
        left.Controls.Add(_modelGrid, 0, 2);

        var right = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            RowCount = 4,
            ColumnCount = 1,
            Padding = new Padding(8)
        };
        right.RowStyles.Add(new RowStyle(SizeType.Absolute, 28));
        right.RowStyles.Add(new RowStyle(SizeType.Absolute, 32));
        right.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        right.RowStyles.Add(new RowStyle(SizeType.Absolute, 155));
        split.Panel2.Controls.Add(right);

        _animationHeader.Dock = DockStyle.Fill;
        _animationHeader.TextAlign = ContentAlignment.MiddleLeft;
        _animationHeader.Font = new Font(Font, FontStyle.Bold);
        right.Controls.Add(_animationHeader, 0, 0);

        _animationFilter.Dock = DockStyle.Fill;
        _animationFilter.PlaceholderText = "筛选动画、来源、验证状态...";
        right.Controls.Add(_animationFilter, 0, 1);

        ConfigureAnimationGrid();
        right.Controls.Add(_animationGrid, 0, 2);

        _details.Dock = DockStyle.Fill;
        _details.Multiline = true;
        _details.ReadOnly = true;
        _details.ScrollBars = ScrollBars.Vertical;
        _details.Font = new Font("Consolas", 9);
        right.Controls.Add(_details, 0, 3);
    }

    private void ConfigureModelGrid()
    {
        _modelGrid.Dock = DockStyle.Fill;
        _modelGrid.AllowUserToAddRows = false;
        _modelGrid.AllowUserToDeleteRows = false;
        _modelGrid.AllowUserToResizeRows = false;
        _modelGrid.MultiSelect = false;
        _modelGrid.ReadOnly = true;
        _modelGrid.RowHeadersVisible = false;
        _modelGrid.SelectionMode = DataGridViewSelectionMode.FullRowSelect;
        _modelGrid.AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill;
        _modelGrid.RowTemplate.Height = 92;
        _modelGrid.BackgroundColor = SystemColors.Window;

        _modelGrid.Columns.Add(new DataGridViewImageColumn
        {
            Name = "Preview",
            HeaderText = "",
            Width = 128,
            FillWeight = 18,
            ImageLayout = DataGridViewImageCellLayout.Zoom
        });
        _modelGrid.Columns.Add(new DataGridViewTextBoxColumn { Name = "Name", HeaderText = "模型", FillWeight = 32 });
        _modelGrid.Columns.Add(new DataGridViewTextBoxColumn { Name = "Animations", HeaderText = "动画", FillWeight = 10 });
        _modelGrid.Columns.Add(new DataGridViewTextBoxColumn { Name = "Kind", HeaderText = "类型", FillWeight = 12 });
        _modelGrid.Columns.Add(new DataGridViewTextBoxColumn { Name = "Skeleton", HeaderText = "Skeleton", FillWeight = 20 });
        _modelGrid.Columns.Add(new DataGridViewTextBoxColumn { Name = "Status", HeaderText = "状态", FillWeight = 8 });
    }

    private void ConfigureAnimationGrid()
    {
        _animationGrid.Dock = DockStyle.Fill;
        _animationGrid.AllowUserToAddRows = false;
        _animationGrid.AllowUserToDeleteRows = false;
        _animationGrid.AllowUserToResizeRows = false;
        _animationGrid.MultiSelect = false;
        _animationGrid.ReadOnly = true;
        _animationGrid.RowHeadersVisible = false;
        _animationGrid.SelectionMode = DataGridViewSelectionMode.FullRowSelect;
        _animationGrid.AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill;
        _animationGrid.BackgroundColor = SystemColors.Window;

        _animationGrid.Columns.Add(new DataGridViewTextBoxColumn { Name = "Name", HeaderText = "动画", FillWeight = 30 });
        _animationGrid.Columns.Add(new DataGridViewTextBoxColumn { Name = "Previewable", HeaderText = "可预览", FillWeight = 9 });
        _animationGrid.Columns.Add(new DataGridViewTextBoxColumn { Name = "Relation", HeaderText = "关系来源", FillWeight = 12 });
        _animationGrid.Columns.Add(new DataGridViewTextBoxColumn { Name = "Validation", HeaderText = "验证", FillWeight = 12 });
        _animationGrid.Columns.Add(new DataGridViewTextBoxColumn { Name = "Duration", HeaderText = "时长", FillWeight = 8 });
        _animationGrid.Columns.Add(new DataGridViewTextBoxColumn { Name = "Tracks", HeaderText = "Track", FillWeight = 8 });
        _animationGrid.Columns.Add(new DataGridViewTextBoxColumn { Name = "Output", HeaderText = "文件", FillWeight = 21 });
    }

    private void WireEvents()
    {
        _openButton.Click += async (_, _) => await ChooseAndOpenLibraryAsync();
        _refreshButton.Click += async (_, _) =>
        {
            if (!string.IsNullOrWhiteSpace(_root))
                await OpenLibraryAsync(_root);
        };
        _openModelButton.Click += (_, _) => OpenSelectedModel();
        _openFolderButton.Click += (_, _) => OpenSelectedModelFolder();
        _modelFilter.TextChanged += (_, _) => RebuildModelGrid();
        _animationFilter.TextChanged += (_, _) => RebuildAnimationGrid(GetSelectedModel());
        _modelGrid.SelectionChanged += (_, _) => RebuildAnimationGrid(GetSelectedModel());
        _modelGrid.CellDoubleClick += (_, _) => OpenSelectedModel();
        _animationGrid.SelectionChanged += (_, _) => ShowSelectedAnimationDetails();
        _animationGrid.CellDoubleClick += async (_, _) => await GenerateAndOpenSelectedAnimationAsync();
    }

    private async Task ChooseAndOpenLibraryAsync()
    {
        using var dialog = new FolderBrowserDialog
        {
            Description = "选择 UnrealExporter 生成的 UE5 素材库根目录",
            SelectedPath = Directory.Exists(_root) ? _root : @"F:\UE-Assets"
        };
        if (dialog.ShowDialog(this) == DialogResult.OK)
            await OpenLibraryAsync(dialog.SelectedPath);
    }

    private async Task OpenLibraryAsync(string root)
    {
        try
        {
            UseWaitCursor = true;
            _statusLabel.Text = "正在读取 library_index.db...";
            _modelGrid.Rows.Clear();
            _animationGrid.Rows.Clear();
            _details.Clear();
            _thumbnailCts?.Cancel();
            _thumbnailCts = new CancellationTokenSource();

            var index = await Task.Run(() => UeLibraryIndexReader.Load(root));
            _root = index.Root;
            _index = index;
            _thumbnails = new ThumbnailService(_root);
            _previewComposer = new PreviewComposer(_root);

            RebuildModelGrid();
            _statusLabel.Text = $"已打开: {_root}";
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "打开素材库失败", MessageBoxButtons.OK, MessageBoxIcon.Error);
            _statusLabel.Text = "打开失败";
        }
        finally
        {
            UseWaitCursor = false;
        }
    }

    private void RebuildModelGrid()
    {
        if (_index == null)
            return;

        var filter = _modelFilter.Text.Trim();
        var models = _index.Models
            .Where(x => MatchesModelFilter(x, filter))
            .OrderByDescending(x => x.UsableAnimationCount)
            .ThenByDescending(x => x.AnimationCount)
            .ThenBy(x => x.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();

        _modelGrid.Rows.Clear();
        foreach (var model in models)
        {
            var rowIndex = _modelGrid.Rows.Add(
                _placeholder,
                model.Name,
                $"{model.UsableAnimationCount}/{model.AnimationCount}",
                model.DisplayKind,
                ShortSkeleton(model),
                string.IsNullOrWhiteSpace(model.ValidationStatus) ? model.Confidence : model.ValidationStatus);
            var row = _modelGrid.Rows[rowIndex];
            row.Tag = model;
            SetRowTooltip(row, $"{model.Output}{Environment.NewLine}{model.Source}{Environment.NewLine}{model.SkeletonPath}");
        }

        _modelHeader.Text = $"模型 {models.Count}/{_index.Models.Count}";
        _animationHeader.Text = "动画";
        if (_modelGrid.Rows.Count > 0)
            _modelGrid.Rows[0].Selected = true;

        _ = LoadVisibleThumbnailsAsync(_thumbnailCts?.Token ?? CancellationToken.None);
    }

    private void RebuildAnimationGrid(UeLibraryModel? model)
    {
        _animationGrid.Rows.Clear();
        if (_index == null || model == null)
        {
            _animationHeader.Text = "动画";
            _details.Clear();
            return;
        }

        var key = UeLibraryIndexReader.MakeLibraryRelative(_root, model.Output);
        _index.AnimationsByModel.TryGetValue(key, out var animations);
        animations ??= [];

        var filter = _animationFilter.Text.Trim();
        var visible = animations
            .Where(x => MatchesAnimationFilter(x, filter))
            .OrderByDescending(x => x.IsPreviewable)
            .ThenBy(x => x.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();

        foreach (var animation in visible)
        {
            var rowIndex = _animationGrid.Rows.Add(
                animation.Name,
                animation.IsPreviewable ? "yes" : "no",
                animation.RelationSource,
                string.IsNullOrWhiteSpace(animation.ValidationStatus) ? animation.Status : animation.ValidationStatus,
                animation.Duration > 0 ? animation.Duration.ToString("0.###") : "",
                animation.TrackCount,
                animation.Output);
            var row = _animationGrid.Rows[rowIndex];
            row.Tag = animation;
            SetRowTooltip(row, BuildAnimationDetails(model, animation));
            if (!animation.IsPreviewable)
                row.DefaultCellStyle.ForeColor = Color.DimGray;
        }

        _animationHeader.Text = $"动画: {model.Name} ({model.UsableAnimationCount}/{model.AnimationCount})";
        if (_animationGrid.Rows.Count > 0)
            _animationGrid.Rows[0].Selected = true;
        ShowSelectedAnimationDetails();
    }

    private async Task LoadVisibleThumbnailsAsync(CancellationToken cancellationToken)
    {
        if (_thumbnails == null)
            return;

        foreach (DataGridViewRow row in _modelGrid.Rows)
        {
            if (cancellationToken.IsCancellationRequested)
                break;
            if (row.Tag is not UeLibraryModel model)
                continue;

            try
            {
                var image = await _thumbnails.GetThumbnailAsync(model, cancellationToken);
                if (!row.IsNewRow && !cancellationToken.IsCancellationRequested)
                    row.Cells["Preview"].Value = image;
            }
            catch
            {
                // Thumbnail failures should not block browsing.
            }
        }
    }

    private async Task GenerateAndOpenSelectedAnimationAsync()
    {
        var model = GetSelectedModel();
        var animation = GetSelectedAnimation();
        if (model == null || animation == null || _previewComposer == null)
            return;

        if (!animation.IsPreviewable)
        {
            MessageBox.Show(this, BuildAnimationDetails(model, animation), "动画不可直接预览", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        try
        {
            UseWaitCursor = true;
            _statusLabel.Text = $"正在合成 {model.Name} + {animation.Name}...";
            var result = await _previewComposer.EnsurePreviewAsync(model, animation, CancellationToken.None);
            _details.Text = BuildAnimationDetails(model, animation) + Environment.NewLine + Environment.NewLine + result.Message;
            if (!result.Success)
            {
                MessageBox.Show(this, result.Message, "合成 preview 失败", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return;
            }

            _statusLabel.Text = "preview 已生成，正在打开 F3D";
            PreviewComposer.OpenWithF3d(result.OutputPath);
        }
        finally
        {
            UseWaitCursor = false;
        }
    }

    private void ShowSelectedAnimationDetails()
    {
        var model = GetSelectedModel();
        var animation = GetSelectedAnimation();
        _details.Text = model == null
            ? ""
            : animation == null
                ? BuildModelDetails(model)
                : BuildAnimationDetails(model, animation);
    }

    private UeLibraryModel? GetSelectedModel()
        => _modelGrid.SelectedRows.Count == 0 ? null : _modelGrid.SelectedRows[0].Tag as UeLibraryModel;

    private UeLibraryAnimation? GetSelectedAnimation()
        => _animationGrid.SelectedRows.Count == 0 ? null : _animationGrid.SelectedRows[0].Tag as UeLibraryAnimation;

    private void OpenSelectedModel()
    {
        var model = GetSelectedModel();
        if (model?.Output is { Length: > 0 } path && File.Exists(path))
            PreviewComposer.OpenWithF3d(path);
    }

    private void OpenSelectedModelFolder()
    {
        var model = GetSelectedModel();
        if (model == null)
            return;
        var directory = Path.GetDirectoryName(model.Output);
        if (!string.IsNullOrWhiteSpace(directory) && Directory.Exists(directory))
            Process.Start(new ProcessStartInfo(directory) { UseShellExecute = true });
    }

    private static bool MatchesModelFilter(UeLibraryModel model, string filter)
    {
        if (string.IsNullOrWhiteSpace(filter))
            return true;

        return Contains(model.Name, filter)
            || Contains(model.Output, filter)
            || Contains(model.Source, filter)
            || Contains(model.SkeletonPath, filter)
            || Contains(model.ResourceKind, filter)
            || Contains(model.SourceType, filter);
    }

    private static bool MatchesAnimationFilter(UeLibraryAnimation animation, string filter)
    {
        if (string.IsNullOrWhiteSpace(filter))
            return true;

        return Contains(animation.Name, filter)
            || Contains(animation.Output, filter)
            || Contains(animation.Source, filter)
            || Contains(animation.RelationSource, filter)
            || Contains(animation.ValidationStatus, filter)
            || Contains(animation.ValidationReason, filter);
    }

    private static bool Contains(string value, string filter)
        => value?.IndexOf(filter, StringComparison.OrdinalIgnoreCase) >= 0;

    private static void SetRowTooltip(DataGridViewRow row, string text)
    {
        foreach (DataGridViewCell cell in row.Cells)
            cell.ToolTipText = text;
    }

    private static string ShortSkeleton(UeLibraryModel model)
    {
        var value = !string.IsNullOrWhiteSpace(model.SkeletonName)
            ? model.SkeletonName
            : model.SkeletonPath;
        if (string.IsNullOrWhiteSpace(value))
            return "";
        var name = value.Split('/', '\\', '.').LastOrDefault(x => !string.IsNullOrWhiteSpace(x)) ?? value;
        return name.Length <= 36 ? name : name[..33] + "...";
    }

    private static string BuildModelDetails(UeLibraryModel model)
        => $"""
           Model: {model.Name}
           File: {model.Output}
           Source: {model.Source}
           Kind: {model.DisplayKind}
           Skeleton: {model.SkeletonPath}
           Bones: {model.BoneCount}
           Materials: {model.MaterialCount}
           Animations: {model.UsableAnimationCount}/{model.AnimationCount}
           Validation: {model.ValidationStatus}
           """;

    private static string BuildAnimationDetails(UeLibraryModel model, UeLibraryAnimation animation)
        => $"""
           Model: {model.Name}
           Model file: {model.Output}

           Animation: {animation.Name}
           Animation file: {animation.Output}
           Source: {animation.Source}
           Relation: {animation.RelationSource}
           Status: {animation.Status}
           Validation: {animation.ValidationStatus} / {animation.ValidationCategory}
           Reason: {animation.ValidationReason}
           Duration: {animation.Duration:0.###}
           Frames: {animation.FrameCount}
           Tracks: {animation.TrackCount}
           Coverage: {animation.TrackCoverage:0.###}
           Hierarchy compatible: {animation.HierarchyCompatible}
           Container animation: {animation.IsContainerAnimation}
           Previewable: {animation.IsPreviewable}
           """;

    private static Image BuildPlaceholderImage()
    {
        var bitmap = new Bitmap(128, 88);
        using var g = Graphics.FromImage(bitmap);
        g.Clear(Color.FromArgb(42, 48, 56));
        using var pen = new Pen(Color.FromArgb(110, 126, 145), 2);
        g.DrawRectangle(pen, 16, 14, 96, 60);
        using var brush = new SolidBrush(Color.WhiteSmoke);
        using var font = new Font("Segoe UI", 9, FontStyle.Bold);
        g.DrawString("UE5", font, brush, 48, 34);
        return bitmap;
    }
}
