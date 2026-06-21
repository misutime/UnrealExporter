using System.Diagnostics;
using System.Drawing;
using System.Runtime.InteropServices;

namespace UE5LibraryBrowser;

internal sealed class MainForm : Form
{
    private const int LvmFirst = 0x1000;
    private const int LvmSetExtendedListViewStyle = LvmFirst + 54;
    private const int LvmSetIconSpacing = LvmFirst + 53;
    private const int LvsExDoubleBuffer = 0x00010000;
    private const int LargeIconCellWidth = 176;
    private const int LargeIconCellHeight = 156;
    private const int MaxUnfilteredThumbnailItems = 360;
    private const int MaxFilteredThumbnailItems = 1200;
    private readonly ToolStrip _toolbar = new();
    private readonly ToolStripButton _openButton = new("打开素材库");
    private readonly ToolStripDropDownButton _recentButton = new("最近");
    private readonly ToolStripButton _refreshButton = new("刷新");
    private readonly ToolStripButton _openModelButton = new("打开模型");
    private readonly ToolStripButton _openFolderButton = new("打开目录");
    private readonly ToolStripComboBox _modelKindBox = new();
    private readonly ToolStripComboBox _modelQualityBox = new();
    private readonly ToolStripComboBox _thumbnailStateBox = new();
    private readonly ToolStripButton _showFavoriteModelsButton = new("只看收藏");
    private readonly ToolStripButton _hideIgnoredButton = new("隐藏忽略");
    private readonly ToolStripLabel _statusLabel = new("请选择 UE5 素材库");
    private readonly TextBox _modelFilter = new();
    private readonly TextBox _animationFilter = new();
    private readonly ListView _modelList = new();
    private readonly ImageList _modelImages = new();
    private readonly DataGridView _animationGrid = new();
    private readonly ContextMenuStrip _modelMenu = new();
    private readonly ContextMenuStrip _animationMenu = new();
    private readonly Label _modelHeader = new();
    private readonly Label _animationHeader = new();
    private readonly TextBox _details = new();
    private readonly Image _placeholder = BuildPlaceholderImage();
    private readonly List<UeLibraryModel> _visibleModels = [];
    private readonly Dictionary<string, int> _visibleModelIndices = new(StringComparer.OrdinalIgnoreCase);
    private readonly RecentLibraryStore _recentStore = new();

    private UeLibraryIndex? _index;
    private UeLibraryCurationStore? _curationStore;
    private ThumbnailService? _thumbnails;
    private PreviewComposer? _previewComposer;
    private ViewerSafeGltfCache? _viewerSafeCache;
    private CancellationTokenSource? _thumbnailCts;
    private int _thumbnailTotal;
    private int _thumbnailCompleted;
    private int _thumbnailCached;
    private int _thumbnailFailed;
    private int _thumbnailActive;
    private int _thumbnailCandidateTotal;
    private string _root = "";
    private string? _initialRoot;

    public MainForm(string? initialRoot)
    {
        _initialRoot = initialRoot;
        Text = "UE5 Library Browser";
        LoadAppIcon();
        Width = 1500;
        Height = 920;
        MinimumSize = new Size(1100, 700);

        BuildLayout();
        WireEvents();
    }

    private void LoadAppIcon()
    {
        var iconPath = Path.Combine(AppContext.BaseDirectory, "Assets", "AppIcon.ico");
        if (File.Exists(iconPath))
            Icon = new Icon(iconPath);
    }

    protected override async void OnShown(EventArgs e)
    {
        base.OnShown(e);
        if (!string.IsNullOrWhiteSpace(_initialRoot))
            await OpenLibraryAsync(_initialRoot);
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        _thumbnailCts?.Cancel();
        _thumbnails?.Dispose();
        base.OnFormClosing(e);
    }

    private void BuildLayout()
    {
        _toolbar.GripStyle = ToolStripGripStyle.Hidden;
        ConfigureToolbarFilters();
        _toolbar.Items.AddRange([
            _openButton,
            _recentButton,
            _refreshButton,
            new ToolStripSeparator(),
            _openModelButton,
            _openFolderButton,
            new ToolStripSeparator(),
            new ToolStripLabel("类型"),
            _modelKindBox,
            new ToolStripLabel("质量"),
            _modelQualityBox,
            new ToolStripLabel("缩略图"),
            _thumbnailStateBox,
            _showFavoriteModelsButton,
            _hideIgnoredButton,
            new ToolStripSeparator(),
            _statusLabel
        ]);
        Controls.Add(_toolbar);
        RebuildRecentMenu();

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

        ConfigureModelList();
        left.Controls.Add(_modelList, 0, 2);

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

    private void ConfigureToolbarFilters()
    {
        _modelKindBox.DropDownStyle = ComboBoxStyle.DropDownList;
        _modelKindBox.Width = 130;
        _modelKindBox.Items.Add("All");
        _modelKindBox.SelectedIndex = 0;

        _modelQualityBox.DropDownStyle = ComboBoxStyle.DropDownList;
        _modelQualityBox.Width = 145;
        _modelQualityBox.Items.AddRange([
            "全部质量",
            "有可信动画",
            "有兼容动画",
            "可预览动画",
            "需复查动画",
            "有骨骼",
            "无骨骼",
            "有材质",
            "缺材质",
            "验证OK",
            "验证警告/问题",
            "Player/NPC"
        ]);
        _modelQualityBox.SelectedIndex = 0;

        _thumbnailStateBox.DropDownStyle = ComboBoxStyle.DropDownList;
        _thumbnailStateBox.Width = 110;
        _thumbnailStateBox.Items.AddRange(["全部", "已有", "未生成"]);
        _thumbnailStateBox.SelectedIndex = 0;

        _showFavoriteModelsButton.CheckOnClick = true;
        _hideIgnoredButton.CheckOnClick = true;
        _hideIgnoredButton.Checked = true;
    }

    private void ConfigureModelList()
    {
        _modelImages.ImageSize = new Size(168, 118);
        _modelImages.ColorDepth = ColorDepth.Depth32Bit;
        _modelImages.Images.Add("__placeholder", _placeholder);

        _modelList.Dock = DockStyle.Fill;
        _modelList.View = View.LargeIcon;
        _modelList.LargeImageList = _modelImages;
        _modelList.MultiSelect = false;
        _modelList.HideSelection = false;
        _modelList.ShowItemToolTips = true;
        _modelList.Sorting = SortOrder.None;
        _modelList.BackColor = SystemColors.Window;
        _modelList.BorderStyle = BorderStyle.FixedSingle;
        _modelList.VirtualMode = true;
        _modelList.RetrieveVirtualItem += ModelList_RetrieveVirtualItem;
        _modelList.HandleCreated += (_, _) =>
        {
            EnableListViewDoubleBuffer(_modelList);
            SetLargeIconSpacing(_modelList);
        };

        _modelMenu.Items.Add("复制模型路径", null, (_, _) => CopySelectedModelPath());
        _modelMenu.Items.Add("复制源资源路径", null, (_, _) => CopySelectedModelSource());
        _modelMenu.Items.Add("复制 Skeleton 路径", null, (_, _) => CopySelectedModelSkeleton());
        _modelMenu.Items.Add(new ToolStripSeparator());
        _modelMenu.Items.Add("收藏模型", null, (_, _) => SetSelectedModelFavorite(true));
        _modelMenu.Items.Add("取消收藏", null, (_, _) => SetSelectedModelFavorite(false));
        _modelMenu.Items.Add("忽略模型", null, (_, _) => SetSelectedModelIgnored(true));
        _modelMenu.Items.Add("取消忽略", null, (_, _) => SetSelectedModelIgnored(false));
        _modelMenu.Items.Add(new ToolStripSeparator());
        _modelMenu.Items.Add("用 F3D 打开", null, (_, _) => OpenSelectedModel());
        _modelMenu.Items.Add("打开所在目录", null, (_, _) => OpenSelectedModelFolder());
        _modelList.ContextMenuStrip = _modelMenu;
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
        _animationGrid.Columns.Add(new DataGridViewTextBoxColumn { Name = "Recommended", HeaderText = "推荐", FillWeight = 12 });
        _animationGrid.Columns.Add(new DataGridViewTextBoxColumn { Name = "Relationship", HeaderText = "关系", FillWeight = 12 });
        _animationGrid.Columns.Add(new DataGridViewTextBoxColumn { Name = "Confidence", HeaderText = "置信", FillWeight = 13 });
        _animationGrid.Columns.Add(new DataGridViewTextBoxColumn { Name = "Relation", HeaderText = "来源", FillWeight = 11 });
        _animationGrid.Columns.Add(new DataGridViewTextBoxColumn { Name = "Validation", HeaderText = "验证", FillWeight = 12 });
        _animationGrid.Columns.Add(new DataGridViewTextBoxColumn { Name = "Duration", HeaderText = "时长", FillWeight = 8 });
        _animationGrid.Columns.Add(new DataGridViewTextBoxColumn { Name = "Tracks", HeaderText = "Track", FillWeight = 8 });
        _animationGrid.Columns.Add(new DataGridViewTextBoxColumn { Name = "Output", HeaderText = "文件", FillWeight = 18 });

        _animationMenu.Items.Add("复制动画路径", null, (_, _) => CopySelectedAnimationPath());
        _animationMenu.Items.Add("复制源资源路径", null, (_, _) => CopySelectedAnimationSource());
        _animationMenu.Items.Add(new ToolStripSeparator());
        _animationMenu.Items.Add("收藏动画", null, (_, _) => SetSelectedAnimationFavorite(true));
        _animationMenu.Items.Add("取消收藏", null, (_, _) => SetSelectedAnimationFavorite(false));
        _animationMenu.Items.Add(new ToolStripSeparator());
        _animationMenu.Items.Add("生成并打开 preview", null, async (_, _) => await GenerateAndOpenSelectedAnimationAsync());
        _animationGrid.ContextMenuStrip = _animationMenu;
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
        _modelKindBox.SelectedIndexChanged += (_, _) => RebuildModelGrid();
        _modelQualityBox.SelectedIndexChanged += (_, _) => RebuildModelGrid();
        _thumbnailStateBox.SelectedIndexChanged += (_, _) => RebuildModelGrid();
        _showFavoriteModelsButton.CheckedChanged += (_, _) => RebuildModelGrid();
        _hideIgnoredButton.CheckedChanged += (_, _) => RebuildModelGrid();
        _animationFilter.TextChanged += (_, _) => RebuildAnimationGrid(GetSelectedModel());
        _modelList.SelectedIndexChanged += (_, _) => RebuildAnimationGrid(GetSelectedModel());
        _modelList.DoubleClick += (_, _) => OpenSelectedModel();
        _modelList.MouseDown += (_, e) => SelectListViewItemOnRightClick(_modelList, e);
        _animationGrid.SelectionChanged += (_, _) => ShowSelectedAnimationDetails();
        _animationGrid.CellDoubleClick += async (_, _) => await GenerateAndOpenSelectedAnimationAsync();
        _animationGrid.MouseDown += (_, e) => SelectGridRowOnRightClick(_animationGrid, e);
    }

    private async Task ChooseAndOpenLibraryAsync()
    {
        using var dialog = new FolderBrowserDialog
        {
            Description = "选择 UnrealExporter 生成的 UE5 素材库根目录",
            SelectedPath = ChooseInitialBrowsePath()
        };
        if (dialog.ShowDialog(this) == DialogResult.OK)
            await OpenLibraryAsync(dialog.SelectedPath);
    }

    private string ChooseInitialBrowsePath()
    {
        if (Directory.Exists(_root))
            return _root;

        var dDriveAssets = @"D:\UE5-Assets";
        if (Directory.Exists(dDriveAssets))
            return dDriveAssets;

        var recent = _recentStore.Load().FirstOrDefault();
        if (!string.IsNullOrWhiteSpace(recent) && Directory.Exists(recent))
            return recent;

        return Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
    }

    private void RebuildRecentMenu()
    {
        _recentButton.DropDownItems.Clear();
        var recentPaths = _recentStore.Load().ToList();
        if (recentPaths.Count == 0)
        {
            _recentButton.DropDownItems.Add(new ToolStripMenuItem("暂无最近素材库") { Enabled = false });
            return;
        }

        foreach (var path in recentPaths)
        {
            var item = new ToolStripMenuItem(BuildRecentLabel(path))
            {
                ToolTipText = path
            };
            item.Click += async (_, _) => await OpenLibraryAsync(path);
            _recentButton.DropDownItems.Add(item);
        }
    }

    private static string BuildRecentLabel(string path)
    {
        var name = Path.GetFileName(path);
        return string.IsNullOrWhiteSpace(name) ? path : $"{name}  ({path})";
    }

    private async Task OpenLibraryAsync(string root)
    {
        try
        {
            UseWaitCursor = true;
            _statusLabel.Text = "正在读取 library_index.db...";
            _modelList.VirtualListSize = 0;
            _visibleModels.Clear();
            _visibleModelIndices.Clear();
            _animationGrid.Rows.Clear();
            _details.Clear();
            _thumbnailCts?.Cancel();
            _thumbnailCts = new CancellationTokenSource();
            _thumbnails?.Dispose();

            var index = await Task.Run(() => UeLibraryIndexReader.Load(root));
            _root = index.Root;
            _index = index;
            _curationStore = new UeLibraryCurationStore(_root);
            _viewerSafeCache = new ViewerSafeGltfCache(_root);
            _thumbnails = new ThumbnailService(_root, _viewerSafeCache, GetThumbnailConcurrency());
            _previewComposer = new PreviewComposer(_root, _viewerSafeCache);

            _recentStore.Add(_root);
            RebuildRecentMenu();
            RebuildModelKindFilter();
            _statusLabel.Text = $"已打开: {_root}";
            RebuildModelGrid();
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
            .Where(MatchesModelKindFilter)
            .Where(MatchesModelQualityFilter)
            .Where(MatchesCurationFilter)
            .Where(MatchesThumbnailStateFilter)
            .OrderByDescending(x => x.TrustedAnimationCount)
            .ThenByDescending(x => x.CompatibleAnimationCount)
            .ThenByDescending(x => x.UsableAnimationCount)
            .ThenByDescending(x => x.AnimationCount)
            .ThenBy(x => x.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();

        _modelList.BeginUpdate();
        _visibleModels.Clear();
        _visibleModelIndices.Clear();
        _visibleModels.AddRange(models);
        for (var i = 0; i < _visibleModels.Count; i++)
        {
            _visibleModelIndices[_visibleModels[i].Output] = i;
        }
        _modelList.VirtualListSize = _visibleModels.Count;
        _modelList.EndUpdate();

        _modelHeader.Text = $"模型 {models.Count}/{_index.Models.Count}";
        _animationHeader.Text = "动画";
        if (_modelList.VirtualListSize > 0)
        {
            _modelList.SelectedIndices.Clear();
            _modelList.SelectedIndices.Add(0);
            _modelList.EnsureVisible(0);
        }
        else
        {
            RebuildAnimationGrid(null);
        }

        Interlocked.Exchange(ref _thumbnailCandidateTotal, _visibleModels.Count);
        StartThumbnailQueue(LimitThumbnailItems(_visibleModels, !string.IsNullOrWhiteSpace(filter)));
    }

    private void ModelList_RetrieveVirtualItem(object? sender, RetrieveVirtualItemEventArgs e)
    {
        if (e.ItemIndex < 0 || e.ItemIndex >= _visibleModels.Count)
        {
            e.Item = new ListViewItem("");
            return;
        }

        var model = _visibleModels[e.ItemIndex];
        var imageKey = _modelImages.Images.ContainsKey(model.Output) ? model.Output : "__placeholder";
        e.Item = new ListViewItem(BuildModelCardText(model), imageKey)
        {
            Tag = model,
            ToolTipText = BuildModelDetails(model)
        };
    }

    private static IReadOnlyList<UeLibraryModel> LimitThumbnailItems(
        IReadOnlyList<UeLibraryModel> items,
        bool hasFilter)
    {
        var limit = hasFilter ? MaxFilteredThumbnailItems : MaxUnfilteredThumbnailItems;
        return items.Count <= limit ? items : items.Take(limit).ToArray();
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
            .OrderBy(x => RecommendedUseSortKey(x.RecommendedUse))
            .ThenByDescending(x => x.IsPreviewable)
            .ThenBy(x => x.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();

        foreach (var animation in visible)
        {
            var displayName = _curationStore?.IsFavoriteAnimation(animation) == true
                ? "[*] " + animation.Name
                : animation.Name;
            var rowIndex = _animationGrid.Rows.Add(
                displayName,
                DisplayRecommendedUse(animation),
                DisplayRelationshipKind(animation),
                string.IsNullOrWhiteSpace(animation.ConfidenceTier) ? DisplayUsageEvidence(animation) : animation.ConfidenceTier,
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

        _animationHeader.Text = $"动画: {model.Name}  默认可信 {model.TrustedAnimationCount} / 兼容候选 {model.CompatibleAnimationCount} / 可预览 {model.UsableAnimationCount} / 总数 {model.AnimationCount}";
        if (_animationGrid.Rows.Count > 0)
            _animationGrid.Rows[0].Selected = true;
        ShowSelectedAnimationDetails();
    }

    private void StartThumbnailQueue(IReadOnlyList<UeLibraryModel> items)
    {
        _thumbnailCts?.Cancel();
        _thumbnailCts = new CancellationTokenSource();
        var cancellationToken = _thumbnailCts.Token;

        Interlocked.Exchange(ref _thumbnailTotal, items.Count);
        Interlocked.Exchange(ref _thumbnailCompleted, 0);
        Interlocked.Exchange(ref _thumbnailCached, 0);
        Interlocked.Exchange(ref _thumbnailFailed, 0);
        Interlocked.Exchange(ref _thumbnailActive, 0);
        UpdateThumbnailStatus();

        var thumbnails = _thumbnails;
        if (items.Count == 0 || thumbnails == null)
            return;

        _ = LoadThumbnailsAsync(items.ToArray(), thumbnails, cancellationToken);
    }

    private async Task LoadThumbnailsAsync(UeLibraryModel[] items, ThumbnailService thumbnails, CancellationToken cancellationToken)
    {
        var nextIndex = -1;
        var workerCount = Math.Min(GetThumbnailConcurrency(), items.Length);
        var workers = Enumerable.Range(0, workerCount)
            .Select(_ => Task.Run(async () =>
            {
                while (!cancellationToken.IsCancellationRequested)
                {
                    var index = Interlocked.Increment(ref nextIndex);
                    if (index >= items.Length)
                        break;

                    await LoadOneThumbnailAsync(items[index], thumbnails, cancellationToken);
                }
            }, cancellationToken))
            .ToArray();

        try
        {
            await Task.WhenAll(workers);
        }
        catch (OperationCanceledException)
        {
            // A newer filter/open operation replaced this queue.
        }

        if (!cancellationToken.IsCancellationRequested)
            SafeBeginInvoke(UpdateThumbnailStatus);
    }

    private async Task LoadOneThumbnailAsync(UeLibraryModel model, ThumbnailService thumbnails, CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref _thumbnailActive);
        try
        {
            var thumbnail = await thumbnails.GetThumbnailAsync(model, cancellationToken).ConfigureAwait(false);
            if (thumbnail.FromCache)
                Interlocked.Increment(ref _thumbnailCached);
            else if (!thumbnail.Success)
                Interlocked.Increment(ref _thumbnailFailed);

            if (!cancellationToken.IsCancellationRequested)
            {
                SafeBeginInvoke(() =>
                {
                    var key = model.Output;
                    if (!_modelImages.Images.ContainsKey(key))
                        _modelImages.Images.Add(key, thumbnail.Image);
                    if (_visibleModelIndices.TryGetValue(key, out var index) && index >= 0 && index < _modelList.VirtualListSize)
                    {
                        _modelList.RedrawItems(index, index, false);
                    }
                    UpdateThumbnailStatus();
                });
            }
            else
            {
                thumbnail.Image.Dispose();
            }
        }
        catch (OperationCanceledException)
        {
            // RebuildModelGrid cancels obsolete thumbnail queues.
        }
        catch
        {
            Interlocked.Increment(ref _thumbnailFailed);
        }
        finally
        {
            Interlocked.Decrement(ref _thumbnailActive);
            Interlocked.Increment(ref _thumbnailCompleted);
            if (!cancellationToken.IsCancellationRequested)
                SafeBeginInvoke(UpdateThumbnailStatus);
        }
    }

    private void UpdateThumbnailStatus()
    {
        var total = Math.Max(0, Volatile.Read(ref _thumbnailTotal));
        var candidateTotal = Math.Max(total, Volatile.Read(ref _thumbnailCandidateTotal));
        if (total == 0)
        {
            _statusLabel.Text = string.IsNullOrWhiteSpace(_root)
                ? "请选择 UE5 素材库"
                : $"已打开: {_root} | 缩略图 0/0";
            return;
        }

        var completed = Math.Min(total, Math.Max(0, Volatile.Read(ref _thumbnailCompleted)));
        var active = Math.Max(0, Volatile.Read(ref _thumbnailActive));
        var queued = Math.Max(0, total - completed - active);
        var cached = Math.Max(0, Volatile.Read(ref _thumbnailCached));
        var failed = Math.Max(0, Volatile.Read(ref _thumbnailFailed));
        var state = completed >= total ? "完成" : "后台生成";
        var renderer = _thumbnails?.RendererLabel ?? "OpenGL worker";
        var scope = candidateTotal > total
            ? $"前 {completed}/{total}（当前列表 {candidateTotal}）"
            : $"{completed}/{total}";
        _statusLabel.Text = $"已打开: {_root} | 缩略图{state} {scope} | 缓存 {cached} | 失败 {failed} | 队列 {queued} | 运行 {active} | 并发 {GetThumbnailConcurrency()} | {renderer}";
    }

    private static int GetThumbnailConcurrency()
        => Math.Clamp(Environment.ProcessorCount / 4, 1, 3);

    private void SafeBeginInvoke(Action action)
    {
        try
        {
            if (!IsDisposed && IsHandleCreated)
                BeginInvoke(action);
        }
        catch
        {
            // The form may be closing while background thumbnail workers finish.
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
    {
        if (_modelList.SelectedIndices.Count == 0)
            return null;

        var index = _modelList.SelectedIndices[0];
        return index >= 0 && index < _visibleModels.Count ? _visibleModels[index] : null;
    }

    private UeLibraryAnimation? GetSelectedAnimation()
        => _animationGrid.SelectedRows.Count == 0 ? null : _animationGrid.SelectedRows[0].Tag as UeLibraryAnimation;

    private void RebuildModelKindFilter()
    {
        var selected = _modelKindBox.SelectedItem as string ?? "All";
        _modelKindBox.Items.Clear();
        _modelKindBox.Items.Add("All");
        if (_index != null)
        {
            foreach (var kind in _index.Models
                .Select(x => string.IsNullOrWhiteSpace(x.DisplayKind) ? "Unknown" : x.DisplayKind)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(x => x, StringComparer.OrdinalIgnoreCase))
            {
                _modelKindBox.Items.Add(kind);
            }
        }
        _modelKindBox.SelectedItem = _modelKindBox.Items.Contains(selected) ? selected : "All";
    }

    private void OpenSelectedModel()
    {
        var model = GetSelectedModel();
        if (model?.Output is { Length: > 0 } path && File.Exists(path))
            PreviewComposer.OpenWithF3d(_viewerSafeCache?.GetViewerSafeModelPath(path) ?? path);
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

    private void CopySelectedModelPath()
        => CopyText(GetSelectedModel()?.Output);

    private void CopySelectedModelSource()
        => CopyText(GetSelectedModel()?.Source);

    private void CopySelectedModelSkeleton()
        => CopyText(GetSelectedModel()?.SkeletonPath);

    private void CopySelectedAnimationPath()
        => CopyText(GetSelectedAnimation()?.Output);

    private void CopySelectedAnimationSource()
        => CopyText(GetSelectedAnimation()?.Source);

    private void SetSelectedModelFavorite(bool favorite)
    {
        _curationStore?.SetFavoriteModel(GetSelectedModel(), favorite);
        RebuildModelGrid();
    }

    private void SetSelectedModelIgnored(bool ignored)
    {
        _curationStore?.SetIgnored(GetSelectedModel(), ignored);
        RebuildModelGrid();
    }

    private void SetSelectedAnimationFavorite(bool favorite)
    {
        _curationStore?.SetFavoriteAnimation(GetSelectedAnimation(), favorite);
        RebuildAnimationGrid(GetSelectedModel());
    }

    private static void CopyText(string? text)
    {
        if (!string.IsNullOrWhiteSpace(text))
            Clipboard.SetText(text);
    }

    private static void SelectListViewItemOnRightClick(ListView list, MouseEventArgs e)
    {
        if (e.Button != MouseButtons.Right)
            return;

        var hit = list.HitTest(e.X, e.Y);
        if (hit.Item == null)
            return;

        if (list.VirtualMode)
        {
            list.SelectedIndices.Clear();
            list.SelectedIndices.Add(hit.Item.Index);
            hit.Item.Focused = true;
            return;
        }

        list.SelectedItems.Clear();
        hit.Item.Selected = true;
        hit.Item.Focused = true;
    }

    private static void SelectGridRowOnRightClick(DataGridView grid, MouseEventArgs e)
    {
        if (e.Button != MouseButtons.Right)
            return;

        var hit = grid.HitTest(e.X, e.Y);
        if (hit.RowIndex < 0)
            return;

        grid.ClearSelection();
        grid.Rows[hit.RowIndex].Selected = true;
    }

    private string BuildModelCardText(UeLibraryModel model)
    {
        var name = model.Name.Length <= 24 ? model.Name : model.Name[..21] + "...";
        if (_curationStore?.IsFavoriteModel(model) == true)
            name = "[*] " + name;
        return $"{name}{Environment.NewLine}可信 {model.TrustedAnimationCount} 兼容 {model.CompatibleAnimationCount}{Environment.NewLine}预览 {model.UsableAnimationCount} 总 {model.AnimationCount}";
    }

    private bool MatchesModelKindFilter(UeLibraryModel model)
    {
        var kind = _modelKindBox.SelectedItem as string ?? "All";
        if (string.Equals(kind, "All", StringComparison.OrdinalIgnoreCase))
            return true;

        var modelKind = string.IsNullOrWhiteSpace(model.DisplayKind) ? "Unknown" : model.DisplayKind;
        return string.Equals(modelKind, kind, StringComparison.OrdinalIgnoreCase);
    }

    private bool MatchesModelQualityFilter(UeLibraryModel model)
    {
        var quality = _modelQualityBox.SelectedItem as string ?? "全部质量";
        return quality switch
        {
            "有可信动画" => model.TrustedAnimationCount > 0,
            "有兼容动画" => model.CompatibleAnimationCount > 0,
            "可预览动画" => model.UsableAnimationCount > 0,
            "需复查动画" => model.ReviewAnimationCount > 0,
            "有骨骼" => model.HasSkin || model.BoneCount > 0 || !string.IsNullOrWhiteSpace(model.SkeletonPath),
            "无骨骼" => !model.HasSkin && model.BoneCount <= 0 && string.IsNullOrWhiteSpace(model.SkeletonPath),
            "有材质" => model.MaterialCount > 0,
            "缺材质" => model.MaterialCount <= 0,
            "验证OK" => Contains(model.ValidationStatus, "ok") || Contains(model.ValidationStatus, "pass"),
            "验证警告/问题" => !string.IsNullOrWhiteSpace(model.ValidationStatus)
                && !Contains(model.ValidationStatus, "ok")
                && !Contains(model.ValidationStatus, "pass"),
            "Player/NPC" => Contains(model.Output, "/Player/") || Contains(model.Output, "\\Player\\") || Contains(model.Output, "/Npc/") || Contains(model.Output, "\\Npc\\"),
            _ => true
        };
    }

    private bool MatchesCurationFilter(UeLibraryModel model)
    {
        if (_hideIgnoredButton.Checked && _curationStore?.IsIgnored(model) == true)
            return false;
        if (_showFavoriteModelsButton.Checked && _curationStore?.IsFavoriteModel(model) != true)
            return false;
        return true;
    }

    private bool MatchesThumbnailStateFilter(UeLibraryModel model)
    {
        var state = _thumbnailStateBox.SelectedItem as string ?? "全部";
        if (string.Equals(state, "全部", StringComparison.OrdinalIgnoreCase) || _thumbnails == null)
            return true;

        var cached = _thumbnails.IsCached(model);
        return state switch
        {
            "已有" => cached,
            "未生成" => !cached,
            _ => true
        };
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
            || Contains(animation.UsageEvidence, filter)
            || Contains(animation.ConfidenceTier, filter)
            || Contains(animation.RelationshipKind, filter)
            || Contains(animation.RecommendedUse, filter)
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

    private string BuildModelDetails(UeLibraryModel model)
        => $"""
           Model: {model.Name}
           File: {model.Output}
           Source: {model.Source}
           Kind: {model.DisplayKind}
           Skeleton: {model.SkeletonPath}
           Bones: {model.BoneCount}
           Materials: {model.MaterialCount}
           Animations: trusted={model.TrustedAnimationCount}, compatible={model.CompatibleAnimationCount}, previewable={model.UsableAnimationCount}, total={model.AnimationCount}
           Validation: {model.ValidationStatus}
           Favorite: {_curationStore?.IsFavoriteModel(model) == true}
           Ignored: {_curationStore?.IsIgnored(model) == true}
           """;

    private string BuildAnimationDetails(UeLibraryModel model, UeLibraryAnimation animation)
        => $"""
           Model: {model.Name}
           Model file: {model.Output}

           Animation: {animation.Name}
           Animation file: {animation.Output}
           Source: {animation.Source}
           Relation: {animation.RelationSource}
           Recommended use: {animation.RecommendedUse}
           Relationship kind: {animation.RelationshipKind}
           Confidence tier: {animation.ConfidenceTier}
           Evidence: {DisplayUsageEvidence(animation)} ({animation.UsageEvidence})
           Evidence chain: {animation.EvidenceSummary}
           Deterministic usage: {animation.IsDeterministicUsage}
           Compatibility candidate: {animation.IsCompatibilityCandidate}
           Explicit usage: {animation.IsExplicitUsage}
           Skeleton compatible: {animation.IsSkeletonCompatible}
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
           Favorite: {_curationStore?.IsFavoriteAnimation(animation) == true}
           """;

    private static string DisplayUsageEvidence(UeLibraryAnimation animation)
    {
        if (animation.IsExplicitUsage)
            return "显式使用";
        if (animation.IsSkeletonCompatible)
            return "Skeleton兼容";
        return string.IsNullOrWhiteSpace(animation.UsageEvidence) ? "未知" : animation.UsageEvidence;
    }

    private static string DisplayRecommendedUse(UeLibraryAnimation animation)
        => animation.RecommendedUse switch
        {
            "defaultTrusted" => animation.IsPreviewable ? "默认可信" : "默认可信(不可预览)",
            "compatibleCandidate" => animation.IsPreviewable ? "兼容候选" : "兼容候选(不可预览)",
            "manualReview" => "人工复查",
            "compatibleNeedsReview" => "兼容复查",
            "notUsable" => "不可用",
            _ => string.IsNullOrWhiteSpace(animation.RecommendedUse) ? "未知" : animation.RecommendedUse
        };

    private static string DisplayRelationshipKind(UeLibraryAnimation animation)
        => animation.RelationshipKind switch
        {
            "deterministicUsage" => "确定使用",
            "contextualUsage" => "上下文",
            "compatibilityCandidate" => "兼容候选",
            "unknown" => "未知",
            _ => string.IsNullOrWhiteSpace(animation.RelationshipKind) ? "未知" : animation.RelationshipKind
        };

    private static int RecommendedUseSortKey(string value)
        => value switch
        {
            "defaultTrusted" => 0,
            "compatibleCandidate" => 1,
            "manualReview" => 2,
            "compatibleNeedsReview" => 3,
            "notUsable" => 4,
            _ => 5
        };

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

    private static void SetLargeIconSpacing(ListView list)
    {
        if (list.IsHandleCreated)
            SendMessage(list.Handle, LvmSetIconSpacing, IntPtr.Zero, MakeLParam(LargeIconCellWidth, LargeIconCellHeight));
    }

    private static void EnableListViewDoubleBuffer(ListView list)
    {
        if (list.IsHandleCreated)
            SendMessage(list.Handle, LvmSetExtendedListViewStyle, (IntPtr)LvsExDoubleBuffer, (IntPtr)LvsExDoubleBuffer);
    }

    private static IntPtr MakeLParam(int low, int high)
        => (IntPtr)((high << 16) | (low & 0xffff));

    [DllImport("user32.dll")]
    private static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);
}
