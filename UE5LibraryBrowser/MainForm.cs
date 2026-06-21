using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
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
    private readonly TabControl _mainTabs = new();
    private readonly TabPage _modelsPage = new("模型");
    private readonly TabPage _globalAnimationsPage = new("全局动画");
    private readonly TabPage _assetsPage = new("贴图材质");
    private readonly TabPage _componentsPage = new("组件关系");
    private readonly TextBox _modelFilter = new();
    private readonly TextBox _animationFilter = new();
    private readonly TextBox _globalAnimationFilter = new();
    private readonly TextBox _assetFilter = new();
    private readonly TextBox _componentFilter = new();
    private readonly ListView _modelList = new();
    private readonly ListView _globalAnimationList = new();
    private readonly ListView _assetList = new();
    private readonly ListView _componentSummaryList = new();
    private readonly ImageList _modelImages = new();
    private readonly ImageList _assetImages = new();
    private readonly DataGridView _animationGrid = new();
    private readonly DataGridView _globalAnimationModelsGrid = new();
    private readonly DataGridView _componentRelationGrid = new();
    private readonly ContextMenuStrip _modelMenu = new();
    private readonly ContextMenuStrip _animationMenu = new();
    private readonly ContextMenuStrip _globalAnimationMenu = new();
    private readonly ContextMenuStrip _globalAnimationModelMenu = new();
    private readonly ContextMenuStrip _assetMenu = new();
    private readonly ContextMenuStrip _componentSummaryMenu = new();
    private readonly ContextMenuStrip _componentRelationMenu = new();
    private readonly Label _modelHeader = new();
    private readonly Label _animationHeader = new();
    private readonly Label _globalAnimationHeader = new();
    private readonly Label _globalAnimationModelsHeader = new();
    private readonly Label _assetHeader = new();
    private readonly Label _componentHeader = new();
    private readonly Label _componentRelationHeader = new();
    private readonly TextBox _details = new();
    private readonly TextBox _globalAnimationDetails = new();
    private readonly TextBox _assetDetails = new();
    private readonly TextBox _componentDetails = new();
    private readonly Image _placeholder = BuildPlaceholderImage();
    private readonly List<UeLibraryModel> _visibleModels = [];
    private readonly Dictionary<string, int> _visibleModelIndices = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<UeLibraryAnimationGroup> _visibleGlobalAnimationGroups = [];
    private readonly List<UeLibraryAsset> _visibleAssets = [];
    private readonly List<UeLibraryComponentSummary> _componentSummaries = [];
    private readonly List<UeLibraryComponentSummary> _visibleComponentSummaries = [];
    private CancellationTokenSource? _componentLoadCts;
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

        _mainTabs.Dock = DockStyle.Fill;
        _mainTabs.TabPages.Add(_modelsPage);
        _mainTabs.TabPages.Add(_globalAnimationsPage);
        _mainTabs.TabPages.Add(_assetsPage);
        _mainTabs.TabPages.Add(_componentsPage);
        Controls.Add(_mainTabs);
        _mainTabs.BringToFront();

        var split = new SplitContainer
        {
            Dock = DockStyle.Fill,
            SplitterDistance = 760,
            Orientation = Orientation.Vertical
        };
        _modelsPage.Controls.Add(split);

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

        BuildGlobalAnimationPage();
        BuildAssetPage();
        BuildComponentPage();
    }

    private void BuildGlobalAnimationPage()
    {
        var split = new SplitContainer
        {
            Dock = DockStyle.Fill,
            SplitterDistance = 650,
            Orientation = Orientation.Vertical
        };
        _globalAnimationsPage.Controls.Add(split);

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

        _globalAnimationHeader.Dock = DockStyle.Fill;
        _globalAnimationHeader.TextAlign = ContentAlignment.MiddleLeft;
        _globalAnimationHeader.Font = new Font(Font, FontStyle.Bold);
        left.Controls.Add(_globalAnimationHeader, 0, 0);

        _globalAnimationFilter.Dock = DockStyle.Fill;
        _globalAnimationFilter.PlaceholderText = "筛选动画、路径、证据、验证状态...";
        left.Controls.Add(_globalAnimationFilter, 0, 1);

        ConfigureGlobalAnimationList();
        left.Controls.Add(_globalAnimationList, 0, 2);

        var right = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            RowCount = 3,
            ColumnCount = 1,
            Padding = new Padding(8)
        };
        right.RowStyles.Add(new RowStyle(SizeType.Absolute, 28));
        right.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        right.RowStyles.Add(new RowStyle(SizeType.Absolute, 190));
        split.Panel2.Controls.Add(right);

        _globalAnimationModelsHeader.Dock = DockStyle.Fill;
        _globalAnimationModelsHeader.TextAlign = ContentAlignment.MiddleLeft;
        _globalAnimationModelsHeader.Font = new Font(Font, FontStyle.Bold);
        right.Controls.Add(_globalAnimationModelsHeader, 0, 0);

        ConfigureGlobalAnimationModelsGrid();
        right.Controls.Add(_globalAnimationModelsGrid, 0, 1);

        _globalAnimationDetails.Dock = DockStyle.Fill;
        _globalAnimationDetails.Multiline = true;
        _globalAnimationDetails.ReadOnly = true;
        _globalAnimationDetails.ScrollBars = ScrollBars.Vertical;
        _globalAnimationDetails.Font = new Font("Consolas", 9);
        right.Controls.Add(_globalAnimationDetails, 0, 2);
    }

    private void BuildAssetPage()
    {
        var split = new SplitContainer
        {
            Dock = DockStyle.Fill,
            SplitterDistance = 980,
            Orientation = Orientation.Vertical
        };
        _assetsPage.Controls.Add(split);

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

        _assetHeader.Dock = DockStyle.Fill;
        _assetHeader.TextAlign = ContentAlignment.MiddleLeft;
        _assetHeader.Font = new Font(Font, FontStyle.Bold);
        left.Controls.Add(_assetHeader, 0, 0);

        _assetFilter.Dock = DockStyle.Fill;
        _assetFilter.PlaceholderText = "筛选贴图/材质、路径、类型、hash...";
        left.Controls.Add(_assetFilter, 0, 1);

        ConfigureAssetList();
        left.Controls.Add(_assetList, 0, 2);

        _assetDetails.Dock = DockStyle.Fill;
        _assetDetails.Multiline = true;
        _assetDetails.ReadOnly = true;
        _assetDetails.ScrollBars = ScrollBars.Vertical;
        _assetDetails.Font = new Font("Consolas", 9);
        split.Panel2.Controls.Add(_assetDetails);
    }

    private void BuildComponentPage()
    {
        var split = new SplitContainer
        {
            Dock = DockStyle.Fill,
            SplitterDistance = 680,
            Orientation = Orientation.Vertical
        };
        _componentsPage.Controls.Add(split);

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

        _componentHeader.Dock = DockStyle.Fill;
        _componentHeader.TextAlign = ContentAlignment.MiddleLeft;
        _componentHeader.Font = new Font(Font, FontStyle.Bold);
        left.Controls.Add(_componentHeader, 0, 0);

        _componentFilter.Dock = DockStyle.Fill;
        _componentFilter.PlaceholderText = "筛选蓝图/地图 source path...";
        left.Controls.Add(_componentFilter, 0, 1);

        ConfigureComponentSummaryList();
        left.Controls.Add(_componentSummaryList, 0, 2);

        var right = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            RowCount = 3,
            ColumnCount = 1,
            Padding = new Padding(8)
        };
        right.RowStyles.Add(new RowStyle(SizeType.Absolute, 28));
        right.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        right.RowStyles.Add(new RowStyle(SizeType.Absolute, 170));
        split.Panel2.Controls.Add(right);

        _componentRelationHeader.Dock = DockStyle.Fill;
        _componentRelationHeader.TextAlign = ContentAlignment.MiddleLeft;
        _componentRelationHeader.Font = new Font(Font, FontStyle.Bold);
        right.Controls.Add(_componentRelationHeader, 0, 0);

        ConfigureComponentRelationGrid();
        right.Controls.Add(_componentRelationGrid, 0, 1);

        _componentDetails.Dock = DockStyle.Fill;
        _componentDetails.Multiline = true;
        _componentDetails.ReadOnly = true;
        _componentDetails.ScrollBars = ScrollBars.Vertical;
        _componentDetails.Font = new Font("Consolas", 9);
        right.Controls.Add(_componentDetails, 0, 2);
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

    private void ConfigureGlobalAnimationList()
    {
        _globalAnimationList.Dock = DockStyle.Fill;
        _globalAnimationList.View = View.Details;
        _globalAnimationList.FullRowSelect = true;
        _globalAnimationList.HideSelection = false;
        _globalAnimationList.MultiSelect = false;
        _globalAnimationList.VirtualMode = true;
        _globalAnimationList.Columns.Add("动画", 260);
        _globalAnimationList.Columns.Add("模型", 58);
        _globalAnimationList.Columns.Add("可信", 58);
        _globalAnimationList.Columns.Add("兼容", 58);
        _globalAnimationList.Columns.Add("可预览", 66);
        _globalAnimationList.Columns.Add("时长", 58);
        _globalAnimationList.Columns.Add("文件", 360);
        _globalAnimationList.RetrieveVirtualItem += GlobalAnimationList_RetrieveVirtualItem;
        _globalAnimationList.HandleCreated += (_, _) => EnableListViewDoubleBuffer(_globalAnimationList);
        _globalAnimationList.ContextMenuStrip = _globalAnimationMenu;

        _globalAnimationMenu.Items.Add("复制动画路径", null, (_, _) => CopySelectedGlobalAnimationPath());
        _globalAnimationMenu.Items.Add("复制源资源路径", null, (_, _) => CopySelectedGlobalAnimationSource());
        _globalAnimationMenu.Items.Add(new ToolStripSeparator());
        _globalAnimationMenu.Items.Add("收藏动画", null, (_, _) => SetSelectedGlobalAnimationFavorite(true));
        _globalAnimationMenu.Items.Add("取消收藏", null, (_, _) => SetSelectedGlobalAnimationFavorite(false));
    }

    private void ConfigureGlobalAnimationModelsGrid()
    {
        _globalAnimationModelsGrid.Dock = DockStyle.Fill;
        _globalAnimationModelsGrid.AllowUserToAddRows = false;
        _globalAnimationModelsGrid.AllowUserToDeleteRows = false;
        _globalAnimationModelsGrid.AllowUserToResizeRows = false;
        _globalAnimationModelsGrid.MultiSelect = false;
        _globalAnimationModelsGrid.ReadOnly = true;
        _globalAnimationModelsGrid.RowHeadersVisible = false;
        _globalAnimationModelsGrid.SelectionMode = DataGridViewSelectionMode.FullRowSelect;
        _globalAnimationModelsGrid.AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill;
        _globalAnimationModelsGrid.BackgroundColor = SystemColors.Window;
        _globalAnimationModelsGrid.Columns.Add(new DataGridViewTextBoxColumn { Name = "Model", HeaderText = "模型", FillWeight = 26 });
        _globalAnimationModelsGrid.Columns.Add(new DataGridViewTextBoxColumn { Name = "Recommended", HeaderText = "推荐", FillWeight = 13 });
        _globalAnimationModelsGrid.Columns.Add(new DataGridViewTextBoxColumn { Name = "Relationship", HeaderText = "关系", FillWeight = 13 });
        _globalAnimationModelsGrid.Columns.Add(new DataGridViewTextBoxColumn { Name = "Confidence", HeaderText = "置信", FillWeight = 14 });
        _globalAnimationModelsGrid.Columns.Add(new DataGridViewTextBoxColumn { Name = "Validation", HeaderText = "验证", FillWeight = 12 });
        _globalAnimationModelsGrid.Columns.Add(new DataGridViewTextBoxColumn { Name = "ModelFile", HeaderText = "模型文件", FillWeight = 30 });
        _globalAnimationModelsGrid.ContextMenuStrip = _globalAnimationModelMenu;

        _globalAnimationModelMenu.Items.Add("生成并打开 preview", null, async (_, _) => await GenerateAndOpenSelectedGlobalAnimationPreviewAsync());
        _globalAnimationModelMenu.Items.Add("打开模型", null, (_, _) => OpenSelectedGlobalAnimationModel());
        _globalAnimationModelMenu.Items.Add("打开模型目录", null, (_, _) => OpenSelectedGlobalAnimationModelFolder());
        _globalAnimationModelMenu.Items.Add(new ToolStripSeparator());
        _globalAnimationModelMenu.Items.Add("复制模型路径", null, (_, _) => CopyText(GetSelectedGlobalAnimationUsage()?.Model.Output));
        _globalAnimationModelMenu.Items.Add("复制动画路径", null, (_, _) => CopyText(GetSelectedGlobalAnimationUsage()?.Animation.Output));
    }

    private void ConfigureAssetList()
    {
        _assetImages.ImageSize = new Size(128, 128);
        _assetImages.ColorDepth = ColorDepth.Depth32Bit;
        _assetImages.Images.Add("__texture", BuildAssetPlaceholderImage("Texture"));
        _assetImages.Images.Add("__material", BuildAssetPlaceholderImage("Material"));

        _assetList.Dock = DockStyle.Fill;
        _assetList.View = View.LargeIcon;
        _assetList.LargeImageList = _assetImages;
        _assetList.HideSelection = false;
        _assetList.MultiSelect = false;
        _assetList.VirtualMode = true;
        _assetList.ShowItemToolTips = true;
        _assetList.RetrieveVirtualItem += AssetList_RetrieveVirtualItem;
        _assetList.HandleCreated += (_, _) =>
        {
            EnableListViewDoubleBuffer(_assetList);
            SetLargeIconSpacing(_assetList);
        };
        _assetList.ContextMenuStrip = _assetMenu;

        _assetMenu.Items.Add("打开文件", null, (_, _) => OpenSelectedAsset());
        _assetMenu.Items.Add("打开目录", null, (_, _) => OpenSelectedAssetFolder());
        _assetMenu.Items.Add(new ToolStripSeparator());
        _assetMenu.Items.Add("复制输出路径", null, (_, _) => CopyText(GetSelectedAsset()?.Output));
        _assetMenu.Items.Add("复制共享贴图路径", null, (_, _) => CopyText(GetSelectedAsset()?.SharedTexture));
        _assetMenu.Items.Add("复制源资源路径", null, (_, _) => CopyText(GetSelectedAsset()?.Source));
    }

    private void ConfigureComponentSummaryList()
    {
        _componentSummaryList.Dock = DockStyle.Fill;
        _componentSummaryList.View = View.Details;
        _componentSummaryList.FullRowSelect = true;
        _componentSummaryList.HideSelection = false;
        _componentSummaryList.MultiSelect = false;
        _componentSummaryList.VirtualMode = true;
        _componentSummaryList.Columns.Add("Source", 330);
        _componentSummaryList.Columns.Add("关系", 58);
        _componentSummaryList.Columns.Add("Owner", 58);
        _componentSummaryList.Columns.Add("组件", 58);
        _componentSummaryList.Columns.Add("模型", 58);
        _componentSummaryList.Columns.Add("材质", 58);
        _componentSummaryList.Columns.Add("贴图", 58);
        _componentSummaryList.Columns.Add("动画", 58);
        _componentSummaryList.RetrieveVirtualItem += ComponentSummaryList_RetrieveVirtualItem;
        _componentSummaryList.HandleCreated += (_, _) => EnableListViewDoubleBuffer(_componentSummaryList);
        _componentSummaryList.ContextMenuStrip = _componentSummaryMenu;

        _componentSummaryMenu.Items.Add("复制 Source Path", null, (_, _) => CopyText(GetSelectedComponentSummary()?.SourcePath));
    }

    private void ConfigureComponentRelationGrid()
    {
        _componentRelationGrid.Dock = DockStyle.Fill;
        _componentRelationGrid.AllowUserToAddRows = false;
        _componentRelationGrid.AllowUserToDeleteRows = false;
        _componentRelationGrid.AllowUserToResizeRows = false;
        _componentRelationGrid.MultiSelect = false;
        _componentRelationGrid.ReadOnly = true;
        _componentRelationGrid.RowHeadersVisible = false;
        _componentRelationGrid.SelectionMode = DataGridViewSelectionMode.FullRowSelect;
        _componentRelationGrid.AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill;
        _componentRelationGrid.BackgroundColor = SystemColors.Window;
        _componentRelationGrid.Columns.Add(new DataGridViewTextBoxColumn { Name = "Kind", HeaderText = "目标", FillWeight = 10 });
        _componentRelationGrid.Columns.Add(new DataGridViewTextBoxColumn { Name = "Relation", HeaderText = "关系", FillWeight = 14 });
        _componentRelationGrid.Columns.Add(new DataGridViewTextBoxColumn { Name = "Target", HeaderText = "目标名", FillWeight = 22 });
        _componentRelationGrid.Columns.Add(new DataGridViewTextBoxColumn { Name = "Status", HeaderText = "状态", FillWeight = 12 });
        _componentRelationGrid.Columns.Add(new DataGridViewTextBoxColumn { Name = "Component", HeaderText = "组件", FillWeight = 18 });
        _componentRelationGrid.Columns.Add(new DataGridViewTextBoxColumn { Name = "Output", HeaderText = "输出", FillWeight = 30 });
        _componentRelationGrid.ContextMenuStrip = _componentRelationMenu;

        _componentRelationMenu.Items.Add("打开目标文件", null, (_, _) => OpenSelectedComponentTarget());
        _componentRelationMenu.Items.Add("打开目标目录", null, (_, _) => OpenSelectedComponentTargetFolder());
        _componentRelationMenu.Items.Add(new ToolStripSeparator());
        _componentRelationMenu.Items.Add("复制目标输出路径", null, (_, _) => CopyText(GetSelectedComponentRelation()?.TargetAssetOutput));
        _componentRelationMenu.Items.Add("复制目标资源路径", null, (_, _) => CopyText(GetSelectedComponentRelation()?.TargetPath));
        _componentRelationMenu.Items.Add("复制 Owner 路径", null, (_, _) => CopyText(GetSelectedComponentRelation()?.OwnerObjectPath));
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
        _globalAnimationFilter.TextChanged += (_, _) => RebuildGlobalAnimationList();
        _globalAnimationList.SelectedIndexChanged += (_, _) => RebuildGlobalAnimationModelsGrid();
        _globalAnimationList.DoubleClick += async (_, _) => await GenerateAndOpenSelectedGlobalAnimationPreviewAsync();
        _globalAnimationList.MouseDown += (_, e) => SelectListViewItemOnRightClick(_globalAnimationList, e);
        _globalAnimationModelsGrid.SelectionChanged += (_, _) => ShowSelectedGlobalAnimationUsageDetails();
        _globalAnimationModelsGrid.CellDoubleClick += async (_, _) => await GenerateAndOpenSelectedGlobalAnimationPreviewAsync();
        _globalAnimationModelsGrid.MouseDown += (_, e) => SelectGridRowOnRightClick(_globalAnimationModelsGrid, e);
        _assetFilter.TextChanged += (_, _) => RebuildAssetGrid();
        _assetList.SelectedIndexChanged += (_, _) => ShowSelectedAssetDetails();
        _assetList.DoubleClick += (_, _) => OpenSelectedAsset();
        _assetList.MouseDown += (_, e) => SelectListViewItemOnRightClick(_assetList, e);
        _mainTabs.SelectedIndexChanged += (_, _) =>
        {
            if (_mainTabs.SelectedTab == _componentsPage)
                _ = EnsureComponentSummariesLoadedAsync();
        };
        _componentFilter.TextChanged += (_, _) => RebuildComponentSummaryGrid();
        _componentSummaryList.SelectedIndexChanged += async (_, _) => await LoadSelectedComponentRelationsAsync();
        _componentSummaryList.MouseDown += (_, e) => SelectListViewItemOnRightClick(_componentSummaryList, e);
        _componentRelationGrid.SelectionChanged += (_, _) => ShowSelectedComponentRelationDetails();
        _componentRelationGrid.MouseDown += (_, e) => SelectGridRowOnRightClick(_componentRelationGrid, e);
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
            RebuildGlobalAnimationList();
            RebuildAssetGrid();
            _componentSummaries.Clear();
            _visibleComponentSummaries.Clear();
            _componentSummaryList.VirtualListSize = 0;
            _componentRelationGrid.Rows.Clear();
            _componentHeader.Text = "组件关系";
            _componentRelationHeader.Text = "关系明细";
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

    private void RebuildGlobalAnimationList()
    {
        if (_index == null)
        {
            _globalAnimationHeader.Text = "全局动画";
            _globalAnimationList.VirtualListSize = 0;
            _globalAnimationModelsGrid.Rows.Clear();
            _globalAnimationDetails.Clear();
            return;
        }

        var filter = _globalAnimationFilter.Text.Trim();
        var groups = _index.AnimationGroups
            .Where(x => MatchesGlobalAnimationFilter(x, filter))
            .OrderByDescending(x => x.DefaultTrustedCount)
            .ThenByDescending(x => x.CompatibleCount)
            .ThenByDescending(x => x.PreviewableCount)
            .ThenByDescending(x => x.ModelCount)
            .ThenBy(x => x.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();

        _visibleGlobalAnimationGroups.Clear();
        _visibleGlobalAnimationGroups.AddRange(groups);
        _globalAnimationList.VirtualListSize = _visibleGlobalAnimationGroups.Count;
        _globalAnimationHeader.Text = $"全局动画 {_visibleGlobalAnimationGroups.Count}/{_index.AnimationGroups.Count}  关系 {_index.AnimationUsages.Count}";
        if (_globalAnimationList.VirtualListSize > 0)
        {
            _globalAnimationList.SelectedIndices.Clear();
            _globalAnimationList.SelectedIndices.Add(0);
        }
        else
        {
            RebuildGlobalAnimationModelsGrid();
        }
        _globalAnimationList.Refresh();
    }

    private void GlobalAnimationList_RetrieveVirtualItem(object? sender, RetrieveVirtualItemEventArgs e)
    {
        if (e.ItemIndex < 0 || e.ItemIndex >= _visibleGlobalAnimationGroups.Count)
        {
            e.Item = new ListViewItem("");
            return;
        }

        var group = _visibleGlobalAnimationGroups[e.ItemIndex];
        var animation = group.Representative;
        var name = _curationStore?.IsFavoriteAnimation(animation) == true ? "[*] " + group.Name : group.Name;
        var item = new ListViewItem(name);
        item.SubItems.Add(group.ModelCount.ToString());
        item.SubItems.Add(group.DefaultTrustedCount.ToString());
        item.SubItems.Add(group.CompatibleCount.ToString());
        item.SubItems.Add(group.PreviewableCount.ToString());
        item.SubItems.Add(animation.Duration > 0 ? animation.Duration.ToString("0.###") : "");
        item.SubItems.Add(group.Output);
        item.Tag = group;
        item.ToolTipText = BuildGlobalAnimationGroupDetails(group);
        e.Item = item;
    }

    private void RebuildGlobalAnimationModelsGrid()
    {
        _globalAnimationModelsGrid.Rows.Clear();
        var group = GetSelectedGlobalAnimationGroup();
        if (group == null)
        {
            _globalAnimationModelsHeader.Text = "关联模型";
            _globalAnimationDetails.Clear();
            return;
        }

        var usages = group.Usages
            .OrderBy(x => RecommendedUseSortKey(x.Animation.RecommendedUse))
            .ThenByDescending(x => x.Animation.IsPreviewable)
            .ThenByDescending(x => x.Model.TrustedAnimationCount)
            .ThenBy(x => x.Model.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();

        foreach (var usage in usages)
        {
            var rowIndex = _globalAnimationModelsGrid.Rows.Add(
                usage.Model.Name,
                DisplayRecommendedUse(usage.Animation),
                DisplayRelationshipKind(usage.Animation),
                string.IsNullOrWhiteSpace(usage.Animation.ConfidenceTier) ? DisplayUsageEvidence(usage.Animation) : usage.Animation.ConfidenceTier,
                string.IsNullOrWhiteSpace(usage.Animation.ValidationStatus) ? usage.Animation.Status : usage.Animation.ValidationStatus,
                usage.Model.Output);
            var row = _globalAnimationModelsGrid.Rows[rowIndex];
            row.Tag = usage;
            SetRowTooltip(row, BuildAnimationDetails(usage.Model, usage.Animation));
            if (!usage.Animation.IsPreviewable)
                row.DefaultCellStyle.ForeColor = Color.DimGray;
        }

        _globalAnimationModelsHeader.Text = $"关联模型: {group.Name}  模型 {group.ModelCount} / 可信 {group.DefaultTrustedCount} / 兼容 {group.CompatibleCount} / 可预览 {group.PreviewableCount}";
        if (_globalAnimationModelsGrid.Rows.Count > 0)
            _globalAnimationModelsGrid.Rows[0].Selected = true;
        ShowSelectedGlobalAnimationUsageDetails();
    }

    private void RebuildAssetGrid()
    {
        if (_index == null)
        {
            _assetHeader.Text = "贴图材质";
            _assetList.VirtualListSize = 0;
            _assetDetails.Clear();
            return;
        }

        var filter = _assetFilter.Text.Trim();
        var assets = _index.Textures
            .Concat(_index.Materials)
            .Where(x => MatchesAssetFilter(x, filter))
            .OrderBy(x => x.Kind, StringComparer.OrdinalIgnoreCase)
            .ThenBy(x => x.ResourceKind, StringComparer.OrdinalIgnoreCase)
            .ThenBy(x => x.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();

        _visibleAssets.Clear();
        _visibleAssets.AddRange(assets);
        _assetList.VirtualListSize = _visibleAssets.Count;
        _assetHeader.Text = $"贴图材质 {_visibleAssets.Count}/{_index.Textures.Count + _index.Materials.Count}  贴图 {_index.Textures.Count} / 材质 {_index.Materials.Count}";
        if (_assetList.VirtualListSize > 0)
        {
            _assetList.SelectedIndices.Clear();
            _assetList.SelectedIndices.Add(0);
        }
        else
        {
            _assetDetails.Clear();
        }
        _assetList.Refresh();
    }

    private void AssetList_RetrieveVirtualItem(object? sender, RetrieveVirtualItemEventArgs e)
    {
        if (e.ItemIndex < 0 || e.ItemIndex >= _visibleAssets.Count)
        {
            e.Item = new ListViewItem("");
            return;
        }

        var asset = _visibleAssets[e.ItemIndex];
        var item = new ListViewItem(BuildAssetCardText(asset), GetAssetImageKey(asset))
        {
            Tag = asset,
            ToolTipText = BuildAssetDetails(asset)
        };
        e.Item = item;
    }

    private async Task EnsureComponentSummariesLoadedAsync()
    {
        if (_index == null || string.IsNullOrWhiteSpace(_root) || _componentSummaries.Count > 0)
            return;

        _componentLoadCts?.Cancel();
        _componentLoadCts = new CancellationTokenSource();
        var token = _componentLoadCts.Token;
        _componentHeader.Text = "组件关系: 正在后台读取 component_asset_relations...";
        try
        {
            var summaries = await Task.Run(() => UeLibraryComponentRelationReader.LoadSummaries(_root), token);
            if (token.IsCancellationRequested)
                return;

            _componentSummaries.Clear();
            _componentSummaries.AddRange(summaries);
            RebuildComponentSummaryGrid();
        }
        catch (Exception ex)
        {
            if (!token.IsCancellationRequested)
            {
                _componentHeader.Text = "组件关系读取失败";
                _componentDetails.Text = ex.Message;
            }
        }
    }

    private void RebuildComponentSummaryGrid()
    {
        var filter = _componentFilter.Text.Trim();
        var summaries = _componentSummaries
            .Where(x => MatchesComponentSummaryFilter(x, filter))
            .OrderByDescending(x => x.RelationCount)
            .ThenBy(x => x.SourcePath, StringComparer.OrdinalIgnoreCase)
            .ToList();

        _visibleComponentSummaries.Clear();
        _visibleComponentSummaries.AddRange(summaries);
        _componentSummaryList.VirtualListSize = _visibleComponentSummaries.Count;
        _componentHeader.Text = _componentSummaries.Count == 0
            ? "组件关系"
            : $"组件关系 {_visibleComponentSummaries.Count}/{_componentSummaries.Count}  注: 仅汇总有导出目标的模型/材质/贴图/动画关系";
        if (_componentSummaryList.VirtualListSize > 0)
        {
            _componentSummaryList.SelectedIndices.Clear();
            _componentSummaryList.SelectedIndices.Add(0);
        }
        else
        {
            _componentRelationGrid.Rows.Clear();
            _componentDetails.Clear();
        }
        _componentSummaryList.Refresh();
    }

    private void ComponentSummaryList_RetrieveVirtualItem(object? sender, RetrieveVirtualItemEventArgs e)
    {
        if (e.ItemIndex < 0 || e.ItemIndex >= _visibleComponentSummaries.Count)
        {
            e.Item = new ListViewItem("");
            return;
        }

        var summary = _visibleComponentSummaries[e.ItemIndex];
        var item = new ListViewItem(summary.Name);
        item.SubItems.Add(summary.RelationCount.ToString());
        item.SubItems.Add(summary.OwnerCount.ToString());
        item.SubItems.Add(summary.ComponentCount.ToString());
        item.SubItems.Add(summary.ModelReferenceCount.ToString());
        item.SubItems.Add(summary.MaterialReferenceCount.ToString());
        item.SubItems.Add(summary.TextureReferenceCount.ToString());
        item.SubItems.Add(summary.AnimationReferenceCount.ToString());
        item.Tag = summary;
        item.ToolTipText = BuildComponentSummaryDetails(summary);
        e.Item = item;
    }

    private async Task LoadSelectedComponentRelationsAsync()
    {
        var summary = GetSelectedComponentSummary();
        if (summary == null || string.IsNullOrWhiteSpace(_root))
            return;

        _componentRelationHeader.Text = $"关系明细: 正在读取 {summary.Name}...";
        _componentRelationGrid.Rows.Clear();
        try
        {
            var sourcePath = summary.SourcePath;
            var relations = await Task.Run(() => UeLibraryComponentRelationReader.LoadRelationsForSource(_root, sourcePath));
            if (!string.Equals(GetSelectedComponentSummary()?.SourcePath, sourcePath, StringComparison.OrdinalIgnoreCase))
                return;

            foreach (var relation in relations)
            {
                var rowIndex = _componentRelationGrid.Rows.Add(
                    relation.TargetAssetKind,
                    relation.RelationType,
                    string.IsNullOrWhiteSpace(relation.TargetName) ? relation.TargetPath : relation.TargetName,
                    relation.MatchStatus,
                    string.IsNullOrWhiteSpace(relation.ComponentName) ? relation.ComponentType : relation.ComponentName,
                    relation.TargetAssetOutput);
                var row = _componentRelationGrid.Rows[rowIndex];
                row.Tag = relation;
                SetRowTooltip(row, BuildComponentRelationDetails(relation));
            }

            _componentRelationHeader.Text = $"关系明细: {summary.Name}  显示 {relations.Count} / 总关系 {summary.RelationCount}";
            if (_componentRelationGrid.Rows.Count > 0)
                _componentRelationGrid.Rows[0].Selected = true;
            ShowSelectedComponentRelationDetails();
        }
        catch (Exception ex)
        {
            _componentRelationHeader.Text = "关系明细读取失败";
            _componentDetails.Text = ex.Message;
        }
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

    private void ShowSelectedGlobalAnimationUsageDetails()
    {
        var usage = GetSelectedGlobalAnimationUsage();
        if (usage != null)
        {
            _globalAnimationDetails.Text = BuildAnimationDetails(usage.Model, usage.Animation);
            return;
        }

        var group = GetSelectedGlobalAnimationGroup();
        _globalAnimationDetails.Text = group == null ? "" : BuildGlobalAnimationGroupDetails(group);
    }

    private void ShowSelectedAssetDetails()
    {
        var asset = GetSelectedAsset();
        _assetDetails.Text = asset == null ? "" : BuildAssetDetails(asset);
    }

    private void ShowSelectedComponentRelationDetails()
    {
        var relation = GetSelectedComponentRelation();
        if (relation != null)
        {
            _componentDetails.Text = BuildComponentRelationDetails(relation);
            return;
        }

        var summary = GetSelectedComponentSummary();
        _componentDetails.Text = summary == null ? "" : BuildComponentSummaryDetails(summary);
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

    private UeLibraryAnimationGroup? GetSelectedGlobalAnimationGroup()
    {
        if (_globalAnimationList.SelectedIndices.Count == 0)
            return null;

        var index = _globalAnimationList.SelectedIndices[0];
        return index >= 0 && index < _visibleGlobalAnimationGroups.Count ? _visibleGlobalAnimationGroups[index] : null;
    }

    private UeLibraryAnimationUsage? GetSelectedGlobalAnimationUsage()
        => _globalAnimationModelsGrid.SelectedRows.Count == 0
            ? null
            : _globalAnimationModelsGrid.SelectedRows[0].Tag as UeLibraryAnimationUsage;

    private UeLibraryAsset? GetSelectedAsset()
    {
        if (_assetList.SelectedIndices.Count == 0)
            return null;

        var index = _assetList.SelectedIndices[0];
        return index >= 0 && index < _visibleAssets.Count ? _visibleAssets[index] : null;
    }

    private UeLibraryComponentSummary? GetSelectedComponentSummary()
    {
        if (_componentSummaryList.SelectedIndices.Count == 0)
            return null;

        var index = _componentSummaryList.SelectedIndices[0];
        return index >= 0 && index < _visibleComponentSummaries.Count ? _visibleComponentSummaries[index] : null;
    }

    private UeLibraryComponentRelation? GetSelectedComponentRelation()
        => _componentRelationGrid.SelectedRows.Count == 0
            ? null
            : _componentRelationGrid.SelectedRows[0].Tag as UeLibraryComponentRelation;

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

    private void OpenSelectedGlobalAnimationModel()
    {
        var usage = GetSelectedGlobalAnimationUsage();
        if (usage?.Model.Output is { Length: > 0 } path && File.Exists(path))
            PreviewComposer.OpenWithF3d(_viewerSafeCache?.GetViewerSafeModelPath(path) ?? path);
    }

    private void OpenSelectedGlobalAnimationModelFolder()
    {
        var usage = GetSelectedGlobalAnimationUsage();
        if (usage == null)
            return;
        var directory = Path.GetDirectoryName(usage.Model.Output);
        if (!string.IsNullOrWhiteSpace(directory) && Directory.Exists(directory))
            Process.Start(new ProcessStartInfo(directory) { UseShellExecute = true });
    }

    private void OpenSelectedAsset()
    {
        var asset = GetSelectedAsset();
        var path = asset?.Kind == "Texture" && File.Exists(asset.SharedTexture)
            ? asset.SharedTexture
            : asset?.Output;
        if (!string.IsNullOrWhiteSpace(path) && File.Exists(path))
            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
    }

    private void OpenSelectedAssetFolder()
    {
        var asset = GetSelectedAsset();
        if (asset == null)
            return;
        var path = File.Exists(asset.Output) ? asset.Output : File.Exists(asset.SharedTexture) ? asset.SharedTexture : "";
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrWhiteSpace(directory) && Directory.Exists(directory))
            Process.Start(new ProcessStartInfo(directory) { UseShellExecute = true });
    }

    private void OpenSelectedComponentTarget()
    {
        var relation = GetSelectedComponentRelation();
        if (relation?.TargetAssetOutput is { Length: > 0 } path && File.Exists(path))
            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
    }

    private void OpenSelectedComponentTargetFolder()
    {
        var relation = GetSelectedComponentRelation();
        if (relation == null)
            return;
        var directory = Path.GetDirectoryName(relation.TargetAssetOutput);
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

    private void CopySelectedGlobalAnimationPath()
        => CopyText(GetSelectedGlobalAnimationGroup()?.Representative.Output);

    private void CopySelectedGlobalAnimationSource()
        => CopyText(GetSelectedGlobalAnimationGroup()?.Representative.Source);

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

    private void SetSelectedGlobalAnimationFavorite(bool favorite)
    {
        _curationStore?.SetFavoriteAnimation(GetSelectedGlobalAnimationGroup()?.Representative, favorite);
        RebuildGlobalAnimationList();
    }

    private async Task GenerateAndOpenSelectedGlobalAnimationPreviewAsync()
    {
        var usage = GetSelectedGlobalAnimationUsage();
        if (usage == null)
        {
            var group = GetSelectedGlobalAnimationGroup();
            usage = group?.Usages
                .OrderBy(x => RecommendedUseSortKey(x.Animation.RecommendedUse))
                .ThenByDescending(x => x.Animation.IsPreviewable)
                .FirstOrDefault(x => x.Animation.IsPreviewable)
                ?? group?.Usages.FirstOrDefault();
        }

        if (usage == null || _previewComposer == null)
            return;

        if (!usage.Animation.IsPreviewable)
        {
            MessageBox.Show(this, BuildAnimationDetails(usage.Model, usage.Animation), "动画不可直接预览", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        try
        {
            UseWaitCursor = true;
            _statusLabel.Text = $"正在合成 {usage.Model.Name} + {usage.Animation.Name}...";
            var result = await _previewComposer.EnsurePreviewAsync(usage.Model, usage.Animation, CancellationToken.None);
            _globalAnimationDetails.Text = BuildAnimationDetails(usage.Model, usage.Animation) + Environment.NewLine + Environment.NewLine + result.Message;
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

    private static bool MatchesGlobalAnimationFilter(UeLibraryAnimationGroup group, string filter)
    {
        if (string.IsNullOrWhiteSpace(filter))
            return true;

        var animation = group.Representative;
        return Contains(group.Name, filter)
            || Contains(group.Output, filter)
            || Contains(group.Source, filter)
            || Contains(animation.UsageEvidence, filter)
            || Contains(animation.ConfidenceTier, filter)
            || Contains(animation.RelationshipKind, filter)
            || Contains(animation.RecommendedUse, filter)
            || Contains(animation.RelationSource, filter)
            || Contains(animation.ValidationStatus, filter)
            || Contains(animation.ValidationReason, filter)
            || group.Usages.Any(x => Contains(x.Model.Name, filter) || Contains(x.Model.Output, filter));
    }

    private static bool MatchesAssetFilter(UeLibraryAsset asset, string filter)
    {
        if (string.IsNullOrWhiteSpace(filter))
            return true;

        return Contains(asset.Name, filter)
            || Contains(asset.Kind, filter)
            || Contains(asset.Output, filter)
            || Contains(asset.SharedTexture, filter)
            || Contains(asset.Source, filter)
            || Contains(asset.SourceType, filter)
            || Contains(asset.ResourceKind, filter)
            || Contains(asset.Format, filter)
            || Contains(asset.Sha256, filter)
            || Contains(asset.BlendMode, filter)
            || Contains(asset.ShadingModel, filter);
    }

    private static bool MatchesComponentSummaryFilter(UeLibraryComponentSummary summary, string filter)
    {
        if (string.IsNullOrWhiteSpace(filter))
            return true;

        return Contains(summary.SourcePath, filter)
            || Contains(summary.Name, filter);
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

    private string BuildGlobalAnimationGroupDetails(UeLibraryAnimationGroup group)
    {
        var animation = group.Representative;
        return $"""
               Animation: {group.Name}
               Animation file: {group.Output}
               Source: {group.Source}
               Related models: {group.ModelCount}
               Default trusted: {group.DefaultTrustedCount}
               Compatible candidates: {group.CompatibleCount}
               Previewable pairs: {group.PreviewableCount}
               Review/not usable pairs: {group.ReviewCount}
               Recommended use: {animation.RecommendedUse}
               Relationship kind: {animation.RelationshipKind}
               Confidence tier: {animation.ConfidenceTier}
               Evidence: {DisplayUsageEvidence(animation)} ({animation.UsageEvidence})
               Evidence chain: {animation.EvidenceSummary}
               Validation: {animation.ValidationStatus} / {animation.ValidationCategory}
               Duration: {animation.Duration:0.###}
               Frames: {animation.FrameCount}
               Tracks: {animation.TrackCount}
               Container animation: {animation.IsContainerAnimation}
               Favorite: {_curationStore?.IsFavoriteAnimation(animation) == true}
               """;
    }

    private string BuildAssetCardText(UeLibraryAsset asset)
    {
        var name = asset.Name.Length <= 24 ? asset.Name : asset.Name[..21] + "...";
        if (string.Equals(asset.Kind, "Material", StringComparison.OrdinalIgnoreCase))
            return $"{name}{Environment.NewLine}{asset.SourceType}{Environment.NewLine}Slots {asset.TextureSlotCount}";

        var size = asset.SizeBytes > 0 ? FormatBytes(asset.SizeBytes) : "";
        var shared = File.Exists(asset.SharedTexture) ? "shared" : "local";
        return $"{name}{Environment.NewLine}{asset.ResourceKind}{Environment.NewLine}{shared} {size}";
    }

    private string BuildAssetDetails(UeLibraryAsset asset)
        => string.Equals(asset.Kind, "Material", StringComparison.OrdinalIgnoreCase)
            ? $"""
               Material: {asset.Name}
               File: {asset.Output}
               Source: {asset.Source}
               Source type: {asset.SourceType}
               Resource kind: {asset.ResourceKind}
               Texture slots: {asset.TextureSlotCount}
               Colors: {asset.ColorCount}
               Scalars: {asset.ScalarCount}
               Switches: {asset.SwitchCount}
               Blend mode: {asset.BlendMode}
               Shading model: {asset.ShadingModel}
               Size: {FormatBytes(asset.SizeBytes)}
               Validation: {asset.ValidationStatus}
               """
            : $"""
               Texture: {asset.Name}
               File: {asset.Output}
               Shared: {asset.SharedTexture}
               Source: {asset.Source}
               Source type: {asset.SourceType}
               Resource kind: {asset.ResourceKind}
               Format: {asset.Format}
               Size: {FormatBytes(asset.SizeBytes)}
               Sha256: {asset.Sha256}
               Hard linked: {asset.HardLinked}
               Link error: {asset.LinkError}
               Validation: {asset.ValidationStatus}
               """;

    private static string BuildComponentSummaryDetails(UeLibraryComponentSummary summary)
        => $"""
           Source: {summary.SourcePath}
           Relations: {summary.RelationCount}
           Owners: {summary.OwnerCount}
           Components: {summary.ComponentCount}
           Models: {summary.ModelReferenceCount}
           Materials: {summary.MaterialReferenceCount}
           Textures: {summary.TextureReferenceCount}
           Animations: {summary.AnimationReferenceCount}
           Missing/nonmatched: {summary.MissingReferenceCount}
           """;

    private static string BuildComponentRelationDetails(UeLibraryComponentRelation relation)
        => $"""
           Owner: {relation.OwnerObjectPath}
           Owner type: {relation.OwnerType}
           Component: {relation.ComponentObjectPath}
           Component type: {relation.ComponentType}
           Component name: {relation.ComponentName}
           Relation source: {relation.RelationSource}
           Relation type: {relation.RelationType}
           Target kind: {relation.TargetAssetKind}
           Target path: {relation.TargetPath}
           Target name: {relation.TargetName}
           Target output: {relation.TargetAssetOutput}
           Match status: {relation.MatchStatus}
           Match reason: {relation.MatchReason}
           Socket: {relation.SocketName}
           """;

    private string GetAssetImageKey(UeLibraryAsset asset)
    {
        if (string.Equals(asset.Kind, "Material", StringComparison.OrdinalIgnoreCase))
            return "__material";

        var key = asset.SharedTexture.Length > 0 ? asset.SharedTexture : asset.Output;
        if (_assetImages.Images.ContainsKey(key))
            return key;

        var imagePath = File.Exists(asset.SharedTexture) ? asset.SharedTexture : asset.Output;
        var image = TryCreateTextureThumbnail(imagePath, _assetImages.ImageSize);
        if (image == null)
            return "__texture";

        _assetImages.Images.Add(key, image);
        return key;
    }

    private static Image? TryCreateTextureThumbnail(string path, Size imageSize)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            return null;

        var extension = Path.GetExtension(path);
        if (!extension.Equals(".png", StringComparison.OrdinalIgnoreCase)
            && !extension.Equals(".jpg", StringComparison.OrdinalIgnoreCase)
            && !extension.Equals(".jpeg", StringComparison.OrdinalIgnoreCase)
            && !extension.Equals(".bmp", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        try
        {
            using var source = Image.FromFile(path);
            var bitmap = new Bitmap(imageSize.Width, imageSize.Height);
            using var g = Graphics.FromImage(bitmap);
            g.Clear(Color.FromArgb(42, 48, 56));
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
            var scale = Math.Min((float)imageSize.Width / source.Width, (float)imageSize.Height / source.Height);
            var width = Math.Max(1, (int)(source.Width * scale));
            var height = Math.Max(1, (int)(source.Height * scale));
            var x = (imageSize.Width - width) / 2;
            var y = (imageSize.Height - height) / 2;
            g.DrawImage(source, x, y, width, height);
            return bitmap;
        }
        catch
        {
            return null;
        }
    }

    private static Image BuildAssetPlaceholderImage(string label)
    {
        var bitmap = new Bitmap(128, 128);
        using var g = Graphics.FromImage(bitmap);
        g.Clear(Color.FromArgb(42, 48, 56));
        using var pen = new Pen(Color.FromArgb(110, 126, 145), 2);
        g.DrawRectangle(pen, 18, 18, 92, 92);
        using var brush = new SolidBrush(Color.WhiteSmoke);
        using var font = new Font("Segoe UI", 12, FontStyle.Bold);
        var size = g.MeasureString(label, font);
        g.DrawString(label, font, brush, (128 - size.Width) / 2, (128 - size.Height) / 2);
        return bitmap;
    }

    private static string FormatBytes(long bytes)
    {
        if (bytes <= 0)
            return "";
        string[] units = ["B", "KB", "MB", "GB"];
        var value = (double)bytes;
        var unit = 0;
        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }
        return $"{value:0.##} {units[unit]}";
    }

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
