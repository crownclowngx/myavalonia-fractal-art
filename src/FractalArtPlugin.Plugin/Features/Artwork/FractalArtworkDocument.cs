using Avalonia.Media.Imaging;
using System.Collections.ObjectModel;
using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using FractalArtPlugin.Application;
using FractalArtPlugin.Application.Workflow;
using MyAvaloniaManagement.PluginSdk;

namespace FractalArtPlugin.Features.Artwork;

/// <summary>分形作品 Document：编排快照、历史、异步渲染、过期结果抑制和导出。</summary>
/// <remarks>
/// 数学、着色、PNG、文件事务和文件窗口都由窄接口承担。Document 只拥有当前不可变配方以及本实例的
/// 生命周期状态；每次渲染先捕获配方，再以 generation 检查结果，旧任务即使迟到也不能覆盖新预览。
/// </remarks>
public sealed partial class FractalArtworkDocument : ObservableObject, IPersistablePluginDocument, IDisposable
{
    private readonly IArtworkValidator _validator;
    private readonly IArtworkSnapshotCodec _snapshotCodec;
    private readonly IArtworkRenderPipeline _renderPipeline;
    private readonly IPreviewImageFactory _previewImageFactory;
    private readonly IArtworkExporter _exporter;
    private readonly IArtworkExportDialog _exportDialog;
    private readonly IArtworkHistory _history;
    private readonly IArtisticParameterMapper _artisticParameterMapper;
    private readonly IVariationExplorer _variationExplorer;
    private readonly IArtworkPresetCatalog _presetCatalog;
    private readonly ILSystemValidator _lSystemValidator;
    private readonly IImageLabArtEffectExportCoordinator? _imageLabCoordinator;
    private readonly IImageLabExportDialog? _imageLabExportDialog;
    private readonly IWorkflowRecipeFiles? _workflowRecipeFiles;
    private readonly IWorkflowRecipeDialog? _workflowRecipeDialog;
    private readonly IArtworkLayerEditor _layerEditor;
    private readonly IDocumentLifetime _lifetime;
    private ArtworkDefinition _artwork = ArtworkDefinition.CreateDefault();
    private DocumentPresentationState _presentation = new("分形作品");
    private CancellationTokenSource? _previewCancellation;
    private CancellationTokenSource? _exportCancellation;
    private CancellationTokenSource? _variationCancellation;
    private long _previewGeneration;
    private long _revision;
    private long _acceptedRevision;
    private long _variationGeneration;
    private ArtworkDefinition? _viewportInteractionStart;
    private bool _disposed;

    public FractalArtworkDocument(
        IArtworkValidator validator,
        IArtworkSnapshotCodec snapshotCodec,
        IArtworkRenderPipeline renderPipeline,
        IPreviewImageFactory previewImageFactory,
        IArtworkExporter exporter,
        IArtworkExportDialog exportDialog,
        IArtworkHistory history,
        IArtisticParameterMapper artisticParameterMapper,
        IVariationExplorer variationExplorer,
        IArtworkPresetCatalog presetCatalog,
        IDocumentLifetime lifetime,
        ILSystemValidator? lSystemValidator = null,
        IImageLabArtEffectExportCoordinator? imageLabCoordinator = null,
        IImageLabExportDialog? imageLabExportDialog = null,
        IWorkflowRecipeFiles? workflowRecipeFiles = null,
        IWorkflowRecipeDialog? workflowRecipeDialog = null,
        IArtworkLayerEditor? layerEditor = null,
        MathLensSession? mathLensSession = null)
    {
        _validator = validator;
        _snapshotCodec = snapshotCodec;
        _renderPipeline = renderPipeline;
        _previewImageFactory = previewImageFactory;
        _exporter = exporter;
        _exportDialog = exportDialog;
        _history = history;
        _artisticParameterMapper = artisticParameterMapper;
        _variationExplorer = variationExplorer;
        _presetCatalog = presetCatalog;
        _lifetime = lifetime;
        _lSystemValidator = lSystemValidator ?? new LSystemValidator();
        _imageLabCoordinator = imageLabCoordinator;
        _imageLabExportDialog = imageLabExportDialog;
        _workflowRecipeFiles = workflowRecipeFiles;
        _workflowRecipeDialog = workflowRecipeDialog;
        _layerEditor = layerEditor ?? new ArtworkLayerEditor(validator);
        MathLens = mathLensSession ?? new MathLensSession(null);
        MathLens.PropertyChanged += HandleMathLensPropertyChanged;
    }

    [ObservableProperty] private Bitmap? _previewImage;
    [ObservableProperty] private bool _isRendering;
    [ObservableProperty] private bool _isExporting;
    [ObservableProperty] private bool _isImageLabExporting;
    [ObservableProperty] private bool _isExploring;
    [ObservableProperty] private string _statusMessage = "正在准备分形预览…";
    [ObservableProperty] private string _lastPreviewFingerprint = string.Empty;
    [ObservableProperty] private TransientPreviewTransform _transientPreview = TransientPreviewTransform.Identity;

    public DocumentPresentationState Presentation => _presentation;
    public bool IsDirty => _revision != _acceptedRevision;
    public bool CanUndo => _history.CanUndo;
    public bool CanRedo => _history.CanRedo;
    public bool IsOperationBusy => IsRendering || IsExporting || IsImageLabExporting || IsExploring || MathLens.IsBusy;
    public bool IsPreviewEmpty => PreviewImage is null;
    public bool IsMathLensOpen => MathLens.IsOpen;
    public bool IsMathLensClosed => !MathLens.IsOpen;

    // G0007 参数是一次导出会话的临时状态，不写入作品快照，也不推进 Document Revision。
    [ObservableProperty] private bool _imageLabBlurEnabled = true;
    [ObservableProperty] private double _imageLabBlurSigma = 1.5d;
    [ObservableProperty] private bool _imageLabBloomEnabled = true;
    [ObservableProperty] private double _imageLabBloomThreshold = 0.72d;
    [ObservableProperty] private double _imageLabBloomSigma = 5d;
    [ObservableProperty] private double _imageLabBloomStrength = 0.8d;
    [ObservableProperty] private bool _imageLabGrainEnabled = true;
    [ObservableProperty] private double _imageLabGrainAmount = 3d;
    [ObservableProperty] private long _imageLabGrainSeed;

    internal ArtworkDefinition Artwork => _artwork;
    public MathLensSession MathLens { get; }
    public ObservableCollection<ArtworkLayerItem> LayerItems { get; } = [];
    public ObservableCollection<MaskSourceOption> MaskSources { get; } = [];
    public ObservableCollection<LayerGroupOption> GroupOptions { get; } = [];
    public ObservableCollection<VariationCandidateItem> VariationCandidates { get; } = [];
    public IReadOnlyList<FavoriteVariationDefinition> Favorites => _artwork.Exploration.Favorites;
    public IReadOnlyList<ArtworkPresetDefinition> ArtworkPresets => _presetCatalog.ArtworkPresets;
    public IReadOnlyList<ArtworkPresetDefinition> JuliaExamples =>
        _presetCatalog.ArtworkPresets.Where(item => item.GeneratorKind == FractalGeneratorKind.Julia).ToArray();
    public IReadOnlyList<ArtworkPresetDefinition> MandelbrotExamples =>
        _presetCatalog.ArtworkPresets.Where(item => item.GeneratorKind == FractalGeneratorKind.Mandelbrot).ToArray();
    public IReadOnlyList<ArtworkPresetDefinition> LSystemExamples =>
        _presetCatalog.ArtworkPresets.Where(item => item.GeneratorKind == FractalGeneratorKind.LSystem).ToArray();
    public IReadOnlyList<ArtworkPresetDefinition> AttractorExamples =>
        _presetCatalog.ArtworkPresets.Where(item => item.GeneratorKind == FractalGeneratorKind.StrangeAttractor).ToArray();
    public IReadOnlyList<PalettePresetDefinition> PalettePresets => _presetCatalog.PalettePresets;
    public ArtworkLayerDefinition SelectedLayer =>
        ArtworkLayerTree.Find(_artwork.Layers, _artwork.Presentation.SelectedLayerId) ?? _artwork.SelectedFractalLayer;
    public bool IsFractalLayerSelected => SelectedLayer is FractalLayerDefinition;
    public bool IsGroupSelected => SelectedLayer is LayerGroupDefinition;
    public bool CanMoveSelectedIntoGroup => IsFractalLayerSelected && GroupOptions.Count > 0;
    public bool CanMoveSelectedOutOfGroup => _artwork.Layers.OfType<LayerGroupDefinition>()
        .Any(group => group.Children.Any(child => child.Id == SelectedLayer.Id));
    public string SelectedLayerName
    {
        get => SelectedLayer.Name;
        set
        {
            if (!string.IsNullOrWhiteSpace(value) && value != SelectedLayer.Name)
            {
                UpdateSelectedLayer(SelectedLayer switch
                {
                    FractalLayerDefinition fractal => fractal with { Name = value.Trim() },
                    LayerGroupDefinition group => group with { Name = value.Trim() },
                    UnavailableLayerDefinition unavailable => unavailable with { Name = value.Trim() },
                    _ => SelectedLayer
                });
            }
        }
    }

    public bool SelectedLayerVisible
    {
        get => SelectedLayer.IsVisible;
        set => UpdateSelectedLayer(SelectedLayer switch
        {
            FractalLayerDefinition fractal => fractal with { IsVisible = value },
            LayerGroupDefinition group => group with { IsVisible = value },
            UnavailableLayerDefinition unavailable => unavailable with { IsVisible = value },
            _ => SelectedLayer
        });
    }

    public double SelectedLayerOpacity
    {
        get => SelectedLayer.Opacity;
        set => UpdateSelectedCommon(opacity: value);
    }

    public string SelectedBlendMode
    {
        get => BlendModeLabel(SelectedLayer.BlendMode);
        set
        {
            var mode = value switch
            {
                "Multiply（正片叠底）" => LayerBlendMode.Multiply,
                "Screen（滤色）" => LayerBlendMode.Screen,
                "Add（相加）" => LayerBlendMode.Add,
                "Overlay（叠加）" => LayerBlendMode.Overlay,
                _ => LayerBlendMode.Normal
            };
            UpdateSelectedCommon(blendMode: mode);
        }
    }

    public IReadOnlyList<string> BlendModeOptions { get; } =
        ["Normal（正常）", "Multiply（正片叠底）", "Screen（滤色）", "Add（相加）", "Overlay（叠加）"];

    public double LayerPositionX { get => SelectedLayer.Transform.PositionXPercent; set => UpdateTransform(t => t with { PositionXPercent = value }); }
    public double LayerPositionY { get => SelectedLayer.Transform.PositionYPercent; set => UpdateTransform(t => t with { PositionYPercent = value }); }
    public double LayerScale { get => SelectedLayer.Transform.ScalePercent; set => UpdateTransform(t => t with { ScalePercent = value }); }
    public double LayerRotation { get => SelectedLayer.Transform.RotationDegrees; set => UpdateTransform(t => t with { RotationDegrees = value }); }
    public double LayerAnchorX { get => SelectedLayer.Transform.AnchorXPercent; set => UpdateTransform(t => t with { AnchorXPercent = value }); }
    public double LayerAnchorY { get => SelectedLayer.Transform.AnchorYPercent; set => UpdateTransform(t => t with { AnchorYPercent = value }); }

    public MaskSourceOption? SelectedMaskSource
    {
        get => SelectedLayer.Mask is null
            ? MaskSources.FirstOrDefault(option => option.Id.Length == 0)
            : MaskSources.FirstOrDefault(option => option.Id == SelectedLayer.Mask.SourceLayerId);
        set
        {
            var mask = value is null || value.Id.Length == 0
                ? null
                : SelectedLayer.Mask is { } current && current.SourceLayerId == value.Id
                    ? current
                    : new ScalarMaskDefinition(value.Id, 0.5, 0.1, false);
            UpdateSelectedCommon(mask: mask, setMask: true);
        }
    }

    public double MaskThreshold
    {
        get => SelectedLayer.Mask?.Threshold ?? 0.5;
        set { if (SelectedLayer.Mask is { } mask) UpdateSelectedCommon(mask: mask with { Threshold = value }, setMask: true); }
    }

    public double MaskSoftness
    {
        get => SelectedLayer.Mask?.Softness ?? 0.1;
        set { if (SelectedLayer.Mask is { } mask) UpdateSelectedCommon(mask: mask with { Softness = value }, setMask: true); }
    }

    public bool MaskInverted
    {
        get => SelectedLayer.Mask?.IsInverted ?? false;
        set { if (SelectedLayer.Mask is { } mask) UpdateSelectedCommon(mask: mask with { IsInverted = value }, setMask: true); }
    }

    public bool HasSelectedMask => SelectedLayer.Mask is not null;
    public LayerGroupOption? SelectedTargetGroup { get; set; }
    public bool IsEscapeTimeFamily => IsFractalLayerSelected && _artwork.GeneratorFamily == GeneratorFamily.EscapeTime;
    public bool IsLSystemFamily => IsFractalLayerSelected && _artwork.GeneratorFamily == GeneratorFamily.LSystem;
    public bool IsJuliaGenerator => IsFractalLayerSelected && _artwork.GeneratorKind == FractalGeneratorKind.Julia;
    public bool IsMandelbrotGenerator => IsFractalLayerSelected && _artwork.GeneratorKind == FractalGeneratorKind.Mandelbrot;
    public bool IsLSystemGenerator => IsFractalLayerSelected && _artwork.GeneratorKind == FractalGeneratorKind.LSystem;
    public bool IsRecursiveTreeGenerator => IsFractalLayerSelected && _artwork.GeneratorKind == FractalGeneratorKind.RecursiveTree;
    public bool IsAttractorGenerator => IsFractalLayerSelected && _artwork.GeneratorKind == FractalGeneratorKind.StrangeAttractor;
    public bool IsSeedControlVisible => IsJuliaGenerator || IsRecursiveTreeGenerator || IsAttractorGenerator;
    public IReadOnlyList<string> AttractorFormulaOptions { get; } = ["Clifford", "De Jong"];
    public string SelectedAttractorFormula
    {
        get => _artwork.StrangeAttractor.Formula == AttractorFormula.DeJong ? "De Jong" : "Clifford";
        set
        {
            var formula = value == "De Jong" ? AttractorFormula.DeJong : AttractorFormula.Clifford;
            if (formula != _artwork.StrangeAttractor.Formula)
            {
                Mutate(_artwork with { StrangeAttractor = _artwork.StrangeAttractor with { Formula = formula } });
            }
        }
    }
    public string GeneratorKindName => SelectedLayer switch
    {
        LayerGroupDefinition => "分组属性",
        UnavailableLayerDefinition unavailable => $"不可用图层 · {unavailable.TypeId} v{unavailable.Version}",
        _ => _artwork.GeneratorKind switch
        {
            FractalGeneratorKind.Julia => "时间逃逸 · Julia",
            FractalGeneratorKind.Mandelbrot => "时间逃逸 · Mandelbrot",
            FractalGeneratorKind.LSystem => "递归路径 · L-System",
            FractalGeneratorKind.StrangeAttractor => $"星云与粒子 · {_artwork.StrangeAttractor.Formula}",
            _ => "递归路径 · 递归树（兼容）"
        }
    };

    /// <summary>
    /// 艺术滑杆每次都从 Julia 真实参数反算，setter 也立即写回 Julia；快照中不存在 Detail/Flow/Curl 副本。
    /// </summary>
    public int Detail
    {
        get => IsMandelbrotGenerator
            ? Math.Clamp((int)Math.Round((_artwork.Mandelbrot.MaxIterations - 64) / 960d * 100d), 0, 100)
            : _artisticParameterMapper.Read(_artwork.Julia).Detail;
        set
        {
            if (IsMandelbrotGenerator)
            {
                var iterations = Math.Clamp((int)Math.Round((64 + Math.Clamp(value, 0, 100) / 100d * 960) / 16d) * 16, 64, 1024);
                TryMutate(_artwork with { Mandelbrot = _artwork.Mandelbrot with { MaxIterations = iterations } });
                return;
            }

            TryMutate(_artwork with { Julia = _artisticParameterMapper.SetDetail(_artwork.Julia, value) });
        }
    }

    public int Flow
    {
        get => _artisticParameterMapper.Read(_artwork.Julia).Flow;
        set => TryMutate(_artwork with { Julia = _artisticParameterMapper.SetFlow(_artwork.Julia, value) });
    }

    public int Curl
    {
        get => _artisticParameterMapper.Read(_artwork.Julia).Curl;
        set => TryMutate(_artwork with { Julia = _artisticParameterMapper.SetCurl(_artwork.Julia, value) });
    }

    public double MutationStrength
    {
        get => _artwork.Exploration.MutationStrength;
        set => TryMutateExploration(_artwork.Exploration with { MutationStrength = Math.Round(value, 2) });
    }

    public bool IsSeedLocked
    {
        get => IsLocked(VariationLockGroups.Seed);
        set => SetLock(VariationLockGroups.Seed, value);
    }

    public bool IsCompositionLocked
    {
        get => IsLocked(VariationLockGroups.Composition);
        set => SetLock(VariationLockGroups.Composition, value);
    }

    public bool IsShapeLocked
    {
        get => IsLocked(VariationLockGroups.Shape);
        set => SetLock(VariationLockGroups.Shape, value);
    }

    public bool IsColorLocked
    {
        get => IsLocked(VariationLockGroups.Color);
        set => SetLock(VariationLockGroups.Color, value);
    }

    public string VariationModeName => _artwork.Exploration.Mode switch
    {
        VariationMode.ShapeOnly => "只改变形态",
        VariationMode.TextureOnly => "只改变质感",
        _ => "形态与质感"
    };

    public int CanvasWidth
    {
        get => _artwork.Canvas.Width;
        set => Mutate(value == CanvasWidth ? _artwork : _artwork with { Canvas = _artwork.Canvas with { Width = value } });
    }

    public int CanvasHeight
    {
        get => _artwork.Canvas.Height;
        set => Mutate(value == CanvasHeight ? _artwork : _artwork with { Canvas = _artwork.Canvas with { Height = value } });
    }

    public string BackgroundHex
    {
        get => _artwork.Canvas.Background.ToHex();
        set => SetColor(value, _artwork.Canvas.Background, color => _artwork with { Canvas = _artwork.Canvas with { Background = color } });
    }

    public long Seed
    {
        get => _artwork.Seed;
        set => Mutate(value == Seed ? _artwork : _artwork with { Seed = value });
    }

    public string CenterX
    {
        get => IsMandelbrotGenerator ? _artwork.Mandelbrot.CenterX : _artwork.Julia.CenterX;
        set => SetHighPrecisionNumber(value, CenterX, number =>
            IsMandelbrotGenerator
                ? _artwork with { Mandelbrot = _artwork.Mandelbrot with { CenterX = number } }
                : _artwork with { Julia = _artwork.Julia with { CenterX = number } });
    }

    public string CenterY
    {
        get => IsMandelbrotGenerator ? _artwork.Mandelbrot.CenterY : _artwork.Julia.CenterY;
        set => SetHighPrecisionNumber(value, CenterY, number =>
            IsMandelbrotGenerator
                ? _artwork with { Mandelbrot = _artwork.Mandelbrot with { CenterY = number } }
                : _artwork with { Julia = _artwork.Julia with { CenterY = number } });
    }

    public string Scale
    {
        get => IsMandelbrotGenerator ? _artwork.Mandelbrot.Scale : _artwork.Julia.Scale;
        set => SetHighPrecisionNumber(value, Scale, number =>
            IsMandelbrotGenerator
                ? _artwork with { Mandelbrot = _artwork.Mandelbrot with { Scale = number } }
                : _artwork with { Julia = _artwork.Julia with { Scale = number } });
    }

    public string ConstantReal
    {
        get => _artwork.Julia.ConstantReal;
        set => SetHighPrecisionNumber(value, ConstantReal, number =>
            _artwork with { Julia = _artwork.Julia with { ConstantReal = number } });
    }

    public string ConstantImaginary
    {
        get => _artwork.Julia.ConstantImaginary;
        set => SetHighPrecisionNumber(value, ConstantImaginary, number =>
            _artwork with { Julia = _artwork.Julia with { ConstantImaginary = number } });
    }

    public int MaxIterations
    {
        get => IsMandelbrotGenerator ? _artwork.Mandelbrot.MaxIterations : _artwork.Julia.MaxIterations;
        set => Mutate(value == MaxIterations
            ? _artwork
            : IsMandelbrotGenerator
                ? _artwork with { Mandelbrot = _artwork.Mandelbrot with { MaxIterations = value } }
                : _artwork with { Julia = _artwork.Julia with { MaxIterations = value } });
    }

    public bool ForceHighPrecision
    {
        get => IsMandelbrotGenerator ? _artwork.Mandelbrot.ForceHighPrecision : _artwork.Julia.ForceHighPrecision;
        set => Mutate(value == ForceHighPrecision
            ? _artwork
            : IsMandelbrotGenerator
                ? _artwork with { Mandelbrot = _artwork.Mandelbrot with { ForceHighPrecision = value } }
                : _artwork with { Julia = _artwork.Julia with { ForceHighPrecision = value } });
    }

    public int PrecisionDigits
    {
        get => IsMandelbrotGenerator ? _artwork.Mandelbrot.PrecisionDigits : _artwork.Julia.PrecisionDigits;
        set
        {
            if (value == PrecisionDigits)
            {
                return;
            }

            TryMutate(IsMandelbrotGenerator
                ? _artwork with { Mandelbrot = _artwork.Mandelbrot with { PrecisionDigits = value } }
                : _artwork with { Julia = _artwork.Julia with { PrecisionDigits = value } });
        }
    }

    public double AttractorA { get => _artwork.StrangeAttractor.A; set => SetAttractor(value, current => current with { A = value }); }
    public double AttractorB { get => _artwork.StrangeAttractor.B; set => SetAttractor(value, current => current with { B = value }); }
    public double AttractorC { get => _artwork.StrangeAttractor.C; set => SetAttractor(value, current => current with { C = value }); }
    public double AttractorD { get => _artwork.StrangeAttractor.D; set => SetAttractor(value, current => current with { D = value }); }
    public int AttractorBurnIn
    {
        get => _artwork.StrangeAttractor.BurnInIterations;
        set => SetAttractor(value, current => current with { BurnInIterations = value });
    }
    public int AttractorSampleCount
    {
        get => _artwork.StrangeAttractor.SampleCount;
        set => SetAttractor(value, current => current with { SampleCount = value });
    }
    public double AttractorExposure { get => _artwork.StrangeAttractor.Exposure; set => SetAttractor(value, current => current with { Exposure = value }); }
    public double AttractorGamma { get => _artwork.StrangeAttractor.Gamma; set => SetAttractor(value, current => current with { Gamma = value }); }
    public bool AttractorGlowEnabled
    {
        get => _artwork.StrangeAttractor.GlowEnabled;
        set => SetAttractor(value, current => current with { GlowEnabled = value });
    }
    public double AttractorGlowSigma { get => _artwork.StrangeAttractor.GlowSigma; set => SetAttractor(value, current => current with { GlowSigma = value }); }
    public double AttractorGlowStrength { get => _artwork.StrangeAttractor.GlowStrength; set => SetAttractor(value, current => current with { GlowStrength = value }); }

    public string LSystemAxiom
    {
        get => _artwork.LSystem.Axiom;
        set => TryMutate(_artwork with { LSystem = _artwork.LSystem with { Axiom = value ?? string.Empty } });
    }

    /// <summary>
    /// 自定义规则使用“一行一个 A=替换串”的紧凑文本。解析成功后才一次性替换领域规则，
    /// 因此半行输入或重复左值不会把不完整语法写入作品历史。
    /// </summary>
    public string LSystemRulesText
    {
        get => string.Join(Environment.NewLine, _artwork.LSystem.Rules.Select(rule => $"{rule.Symbol}={rule.Replacement}"));
        set
        {
            try
            {
                var rules = ParseLSystemRules(value);
                TryMutate(_artwork with { LSystem = _artwork.LSystem with { Rules = rules } });
            }
            catch (InvalidDataException exception)
            {
                StatusMessage = exception.Message;
            }
        }
    }

    public int LSystemIterations
    {
        get => _artwork.LSystem.Iterations;
        set => TryMutate(_artwork with { LSystem = _artwork.LSystem with { Iterations = value } });
    }

    public double LSystemTurnAngle
    {
        get => _artwork.LSystem.TurnAngleDegrees;
        set => TryMutate(_artwork with { LSystem = _artwork.LSystem with { TurnAngleDegrees = value } });
    }

    public double LSystemInitialHeading
    {
        get => _artwork.LSystem.InitialHeadingDegrees;
        set => TryMutate(_artwork with { LSystem = _artwork.LSystem with { InitialHeadingDegrees = value } });
    }

    public double LSystemStepLength
    {
        get => _artwork.LSystem.StepLength;
        set => TryMutate(_artwork with { LSystem = _artwork.LSystem with { StepLength = value } });
    }

    public double LSystemLengthDecay
    {
        get => _artwork.LSystem.LengthDecay;
        set => TryMutate(_artwork with { LSystem = _artwork.LSystem with { LengthDecay = value } });
    }

    public double LSystemStrokeWidth
    {
        get => _artwork.LSystem.StrokeWidth;
        set => TryMutate(_artwork with { LSystem = _artwork.LSystem with { StrokeWidth = value } });
    }

    public double LSystemStrokeWidthDecay
    {
        get => _artwork.LSystem.StrokeWidthDecay;
        set => TryMutate(_artwork with { LSystem = _artwork.LSystem with { StrokeWidthDecay = value } });
    }

    public string LSystemDiagnostics
    {
        get
        {
            var result = _lSystemValidator.Analyze(_artwork.LSystem);
            return result.IsValid
                ? $"预计 {result.ExpandedSymbolCount:N0} 个符号 · {result.EstimatedSegmentCount:N0} 条线段 · 栈深 {result.EstimatedStackDepth:N0}"
                : string.Join(" ", result.Errors.Select(error => error.Message));
        }
    }

    public int TreeDepth
    {
        get => _artwork.RecursiveTree.Depth;
        set => TryMutate(value == TreeDepth
            ? _artwork
            : _artwork with { RecursiveTree = _artwork.RecursiveTree with { Depth = value } });
    }

    public int TreeBranches
    {
        get => _artwork.RecursiveTree.Branches;
        set => TryMutate(value == TreeBranches
            ? _artwork
            : _artwork with { RecursiveTree = _artwork.RecursiveTree with { Branches = value } });
    }

    public double TreeBranchAngle
    {
        get => _artwork.RecursiveTree.BranchAngleDegrees;
        set => TryMutate(value.Equals(TreeBranchAngle)
            ? _artwork
            : _artwork with { RecursiveTree = _artwork.RecursiveTree with { BranchAngleDegrees = value } });
    }

    public double TreeLengthDecay
    {
        get => _artwork.RecursiveTree.LengthDecay;
        set => TryMutate(value.Equals(TreeLengthDecay)
            ? _artwork
            : _artwork with { RecursiveTree = _artwork.RecursiveTree with { LengthDecay = value } });
    }

    public double TreeRandomness
    {
        get => _artwork.RecursiveTree.Randomness;
        set => TryMutate(value.Equals(TreeRandomness)
            ? _artwork
            : _artwork with { RecursiveTree = _artwork.RecursiveTree with { Randomness = value } });
    }

    public double TreeTrunkLength
    {
        get => _artwork.RecursiveTree.TrunkLength;
        set => TryMutate(value.Equals(TreeTrunkLength)
            ? _artwork
            : _artwork with { RecursiveTree = _artwork.RecursiveTree with { TrunkLength = value } });
    }

    public double TreeStrokeWidth
    {
        get => _artwork.RecursiveTree.StrokeWidth;
        set => TryMutate(value.Equals(TreeStrokeWidth)
            ? _artwork
            : _artwork with { RecursiveTree = _artwork.RecursiveTree with { StrokeWidth = value } });
    }

    public string GradientStartHex
    {
        get => _artwork.Gradient.Start.ToHex();
        set => SetColor(value, _artwork.Gradient.Start, color => _artwork with { Gradient = _artwork.Gradient with { Start = color } });
    }

    public string GradientEndHex
    {
        get => _artwork.Gradient.End.ToHex();
        set => SetColor(value, _artwork.Gradient.End, color => _artwork with { Gradient = _artwork.Gradient with { End = color } });
    }

    public bool HighQualityPreview
    {
        get => _artwork.Presentation.HighQualityPreview;
        set => Mutate(value == HighQualityPreview
            ? _artwork
            : _artwork with { Presentation = _artwork.Presentation with { HighQualityPreview = value } });
    }

    private ToneEffectDefinition ToneEffect => _artwork.MasterEffects.Effects.OfType<ToneEffectDefinition>().FirstOrDefault() ??
        new ToneEffectDefinition(false, 0, 0, 1);
    private BloomEffectDefinition BloomEffect => _artwork.MasterEffects.Effects.OfType<BloomEffectDefinition>().FirstOrDefault() ??
        new BloomEffectDefinition(false, 0.72, 2.4, 0.8);

    public bool ToneEnabled { get => ToneEffect.IsEnabled; set => UpdateMasterEffect(ToneEffect with { IsEnabled = value }); }
    public double ToneBrightness { get => ToneEffect.Brightness; set => UpdateMasterEffect(ToneEffect with { Brightness = value }); }
    public double ToneContrast { get => ToneEffect.Contrast; set => UpdateMasterEffect(ToneEffect with { Contrast = value }); }
    public double ToneSaturation { get => ToneEffect.Saturation; set => UpdateMasterEffect(ToneEffect with { Saturation = value }); }
    public bool MasterBloomEnabled { get => BloomEffect.IsEnabled; set => UpdateMasterEffect(BloomEffect with { IsEnabled = value }); }
    public double MasterBloomThreshold { get => BloomEffect.Threshold; set => UpdateMasterEffect(BloomEffect with { Threshold = value }); }
    public double MasterBloomSigma { get => BloomEffect.Sigma; set => UpdateMasterEffect(BloomEffect with { Sigma = value }); }
    public double MasterBloomStrength { get => BloomEffect.Strength; set => UpdateMasterEffect(BloomEffect with { Strength = value }); }

    public event EventHandler? PresentationChanged;
    public event EventHandler? IsDirtyChanged;

    /// <summary>
    /// 新建时使用默认完整配方，恢复时先严格解码到局部变量。只有解码和验证全部成功才替换当前状态，
    /// 因此取消、损坏内容或未知版本不会留下半初始化 Document。
    /// </summary>
    public async ValueTask InitializeAsync(DocumentActivation activation, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(activation);
        cancellationToken.ThrowIfCancellationRequested();

        var candidate = activation is RestoreDocumentActivation restore
            ? _snapshotCodec.Decode(restore.RestoredContent)
            : ArtworkDefinition.CreateDefault();
        _validator.Validate(candidate);
        cancellationToken.ThrowIfCancellationRequested();

        MathLens.Close();
        _artwork = candidate;
        _history.Clear();
        _revision = _acceptedRevision = 0;
        _presentation = new DocumentPresentationState(
            string.IsNullOrWhiteSpace(activation.Title) ? "分形作品" : activation.Title);
        PresentationChanged?.Invoke(this, EventArgs.Empty);
        NotifyArtworkProperties();
        await RenderPreviewCoreAsync(debounce: false, cancellationToken).ConfigureAwait(true);
        if (_artwork.Exploration.Candidates.Count > 0)
        {
            await RestoreVariationPreviewsAsync(cancellationToken).ConfigureAwait(true);
        }
    }

    public ValueTask<DocumentSaveSnapshot> CaptureSaveSnapshotAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var content = _snapshotCodec.Encode(_artwork);
        return ValueTask.FromResult(new DocumentSaveSnapshot(new DocumentRevision(_revision), content));
    }

    public void AcceptChanges(DocumentRevision savedRevision)
    {
        var wasDirty = IsDirty;
        if (savedRevision.Value == _revision)
        {
            _acceptedRevision = _revision;
        }

        if (wasDirty != IsDirty)
        {
            IsDirtyChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    [RelayCommand]
    private Task RefreshPreviewAsync() => RenderPreviewCoreAsync(debounce: false, CancellationToken.None);

    [RelayCommand]
    private void Undo()
    {
        if (!_history.CanUndo)
        {
            return;
        }

        ApplyHistory(_history.Undo(_artwork));
    }

    [RelayCommand]
    private void Redo()
    {
        if (!_history.CanRedo)
        {
            return;
        }

        ApplyHistory(_history.Redo(_artwork));
    }

    /// <summary>
    /// 一整批九宫格只有在配方和全部缩略图都成功完成后才提交到作品。取消、异常或迟到批次都不会改变
    /// 当前作品、历史和候选集合，这与主预览的 generation 提交规则保持一致。
    /// </summary>
    [RelayCommand]
    private async Task GenerateVariationsAsync()
    {
        CancelAndDispose(ref _variationCancellation);
        _variationCancellation = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.ClosingToken);
        var current = _variationCancellation;
        var token = current.Token;
        var generation = Interlocked.Increment(ref _variationGeneration);
        IsExploring = true;
        StatusMessage = "正在以最多 3 路并发生成九宫格变体…";
        try
        {
            var source = _artwork;
            var result = await _variationExplorer.ExploreAsync(source, 9, token).ConfigureAwait(true);
            token.ThrowIfCancellationRequested();
            if (generation != Volatile.Read(ref _variationGeneration) || _disposed || _lifetime.IsClosing)
            {
                return;
            }

            var exploration = source.Exploration with
            {
                Generation = result.Batch.Generation,
                Candidates = result.Batch.Candidates
            };
            Mutate(source with { Exploration = exploration }, renderPreview: false);
            ReplaceVariationItems(result.RenderedCandidates);
            var cacheHits = result.RenderedCandidates.Count(item => item.FromCache);
            StatusMessage = $"第 {result.Batch.Generation} 轮变体完成 · 9 个候选 · 缓存命中 {cacheHits}。";
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            if (generation == Volatile.Read(ref _variationGeneration) && !_lifetime.IsClosing)
            {
                StatusMessage = "变体生成已取消，当前作品和上一批候选保持不变。";
            }
        }
        catch (Exception exception)
        {
            if (generation == Volatile.Read(ref _variationGeneration))
            {
                StatusMessage = $"变体生成失败：{exception.Message}";
            }
        }
        finally
        {
            if (Interlocked.CompareExchange(ref _variationCancellation, null, current) == current)
            {
                current.Dispose();
                IsExploring = false;
            }
        }
    }

    [RelayCommand]
    private void ApplyVariation(VariationCandidateItem? item)
    {
        if (item is null)
        {
            return;
        }

        CancelVariationWork();
        Mutate(_artwork.ApplyVariationRecipe(item.Definition.Recipe));
        StatusMessage = $"已采用{item.Title}；可撤销，也可从该结果继续探索。";
    }

    [RelayCommand]
    private async Task ContinueFromVariationAsync(VariationCandidateItem? item)
    {
        if (item is null)
        {
            return;
        }

        ApplyVariation(item);
        await GenerateVariationsAsync().ConfigureAwait(true);
    }

    [RelayCommand]
    private void ToggleFavorite(VariationCandidateItem? item)
    {
        if (item is null)
        {
            return;
        }

        var favorites = _artwork.Exploration.Favorites.ToList();
        var existing = favorites.FindIndex(favorite => favorite.Id == $"fav-{item.Definition.Id}");
        if (existing >= 0)
        {
            favorites.RemoveAt(existing);
            item.IsFavorite = false;
        }
        else
        {
            if (favorites.Count >= 64)
            {
                StatusMessage = "收藏已达到 64 项上限，请先移除旧收藏。";
                return;
            }

            favorites.Add(new FavoriteVariationDefinition(
                $"fav-{item.Definition.Id}",
                $"第 {_artwork.Exploration.Generation} 轮 · {item.Title}",
                item.Definition.Recipe));
            item.IsFavorite = true;
        }

        TryMutateExploration(_artwork.Exploration with { Favorites = favorites });
    }

    [RelayCommand]
    private void RestoreFavorite(FavoriteVariationDefinition? favorite)
    {
        if (favorite is null)
        {
            return;
        }

        CancelVariationWork();
        Mutate(_artwork.ApplyVariationRecipe(favorite.Recipe));
        StatusMessage = $"已恢复收藏“{favorite.Name}”。";
    }

    [RelayCommand]
    private async Task ContinueFromFavoriteAsync(FavoriteVariationDefinition? favorite)
    {
        if (favorite is null)
        {
            return;
        }

        RestoreFavorite(favorite);
        await GenerateVariationsAsync().ConfigureAwait(true);
    }

    [RelayCommand]
    private void ApplyArtworkPreset(string? id)
    {
        if (!string.IsNullOrWhiteSpace(id))
        {
            Mutate(_presetCatalog.ApplyArtworkPreset(_artwork, id));
        }
    }

    [RelayCommand]
    private void ApplyPalettePreset(string? id)
    {
        if (!string.IsNullOrWhiteSpace(id))
        {
            Mutate(_presetCatalog.ApplyPalettePreset(_artwork, id));
        }
    }

    [RelayCommand]
    private void SelectGenerator(string? kind)
    {
        if (Enum.TryParse<FractalGeneratorKind>(kind, out var parsed) && parsed != _artwork.GeneratorKind)
        {
            Mutate(_artwork.WithGeneratorKind(parsed));
        }
    }

    [RelayCommand]
    private void AddLayer(string? kind)
    {
        if (!Enum.TryParse<FractalGeneratorKind>(kind, out var parsed))
        {
            return;
        }

        TryLayerOperation(() => _layerEditor.AddFractal(_artwork, parsed));
    }

    [RelayCommand]
    private void AddGroup() => TryLayerOperation(() => _layerEditor.AddGroup(_artwork));

    [RelayCommand]
    private void SelectLayer(ArtworkLayerItem? item)
    {
        if (item is null || item.Id == _artwork.Presentation.SelectedLayerId)
        {
            return;
        }

        CancelVariationWork();
        Mutate(_artwork.SelectLayer(item.Id), recordHistory: false, renderPreview: false);
        RefreshVariationPresentationAfterHistory();
    }

    [RelayCommand]
    private void ToggleLayerVisibility(ArtworkLayerItem? item)
    {
        if (item is null || ArtworkLayerTree.Find(_artwork.Layers, item.Id) is not { } layer)
        {
            return;
        }

        var replacement = layer switch
        {
            FractalLayerDefinition fractal => fractal with { IsVisible = !fractal.IsVisible },
            LayerGroupDefinition group => group with { IsVisible = !group.IsVisible },
            UnavailableLayerDefinition unavailable => unavailable with { IsVisible = !unavailable.IsVisible },
            _ => layer
        };
        TryLayerOperation(() => _layerEditor.Update(_artwork, replacement));
    }

    [RelayCommand]
    private void MoveLayerUp() => TryLayerOperation(() => _layerEditor.Move(_artwork, SelectedLayer.Id, -1));

    [RelayCommand]
    private void MoveLayerDown() => TryLayerOperation(() => _layerEditor.Move(_artwork, SelectedLayer.Id, 1));

    [RelayCommand]
    private void DeleteLayer() => TryLayerOperation(() => _layerEditor.Delete(_artwork, SelectedLayer.Id));

    [RelayCommand]
    private void MoveIntoGroup()
    {
        if (SelectedTargetGroup is { } group)
        {
            TryLayerOperation(() => _layerEditor.MoveIntoGroup(_artwork, SelectedLayer.Id, group.Id));
        }
    }

    [RelayCommand]
    private void MoveOutOfGroup() => TryLayerOperation(() => _layerEditor.MoveOutOfGroup(_artwork, SelectedLayer.Id));

    [RelayCommand]
    private void SetVariationMode(string? mode)
    {
        if (Enum.TryParse<VariationMode>(mode, out var parsed))
        {
            TryMutateExploration(_artwork.Exploration with { Mode = parsed });
        }
    }

    [RelayCommand]
    private async Task ExportPngAsync()
    {
        CancelAndDispose(ref _exportCancellation);
        _exportCancellation = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.ClosingToken);
        var current = _exportCancellation;
        var token = current.Token;
        try
        {
            var path = await _exportDialog.PickPngPathAsync("fractal-art.png", token).ConfigureAwait(true);
            if (string.IsNullOrWhiteSpace(path))
            {
                StatusMessage = "已取消 PNG 导出。";
                return;
            }

            IsExporting = true;
            StatusMessage = $"正在以 {CanvasWidth}×{CanvasHeight} 最终质量重新渲染…";
            var snapshot = _artwork;
            await _exporter.ExportAsync(snapshot, path, token).ConfigureAwait(true);
            if (!_lifetime.IsClosing)
            {
                StatusMessage = $"PNG 已原子导出：{path}";
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            if (!_lifetime.IsClosing)
            {
                StatusMessage = "导出已取消，未报告成功。";
            }
        }
        catch (Exception exception)
        {
            StatusMessage = $"导出失败：{exception.Message}";
        }
        finally
        {
            if (ReferenceEquals(current, _exportCancellation))
            {
                _exportCancellation = null;
                current.Dispose();
                IsExporting = false;
            }
        }
    }

    /// <summary>把当前不可变作品渲染到临时 PNG，再从 Document 顶层调用 ImageLab Action。</summary>
    [RelayCommand]
    private async Task ExportWithImageLabAsync()
    {
        if (_imageLabCoordinator is null || _imageLabExportDialog is null ||
            !_imageLabCoordinator.IsAvailable())
        {
            StatusMessage = "ImageLab 艺术效果当前不可用；普通 PNG 导出仍可使用。";
            return;
        }

        CancelAndDispose(ref _exportCancellation);
        _exportCancellation = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.ClosingToken);
        var current = _exportCancellation;
        var token = current.Token;
        try
        {
            var path = await _imageLabExportDialog
                .PickOutputPathAsync("fractal-art-imagelab.png", token)
                .ConfigureAwait(true);
            if (string.IsNullOrWhiteSpace(path))
            {
                StatusMessage = "已取消 ImageLab 艺术导出。";
                return;
            }

            IsImageLabExporting = true;
            var effects = new ImageLabEffectSettings(
                new BlurEffectSettings(ImageLabBlurEnabled, ImageLabBlurSigma),
                new BloomEffectSettings(
                    ImageLabBloomEnabled,
                    ImageLabBloomThreshold,
                    ImageLabBloomSigma,
                    ImageLabBloomStrength),
                new GrainEffectSettings(ImageLabGrainEnabled, ImageLabGrainAmount, ImageLabGrainSeed));
            var progress = new Progress<int>(percent =>
                StatusMessage = $"ImageLab 艺术导出处理中 · {percent}%");
            StatusMessage = "正在渲染 ImageLab 临时输入…";
            var result = await _imageLabCoordinator.ExportAsync(
                _artwork, effects, path, progress, token).ConfigureAwait(true);
            if (!_lifetime.IsClosing)
            {
                StatusMessage = $"ImageLab 艺术 PNG 已导出：{result.OutputPath}";
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            if (!_lifetime.IsClosing)
            {
                StatusMessage = "ImageLab 艺术导出已取消。";
            }
        }
        catch (Exception exception)
        {
            StatusMessage = $"ImageLab 艺术导出失败：{exception.Message}";
        }
        finally
        {
            if (ReferenceEquals(current, _exportCancellation))
            {
                _exportCancellation = null;
                current.Dispose();
                IsImageLabExporting = false;
            }
        }
    }

    /// <summary>导出小型版本化配方，供 Workflow Studio 的 Fractal Render Action 使用。</summary>
    [RelayCommand]
    private async Task ExportWorkflowRecipeAsync()
    {
        if (_workflowRecipeFiles is null || _workflowRecipeDialog is null)
        {
            StatusMessage = "Workflow 配方导出当前不可用。";
            return;
        }

        CancelAndDispose(ref _exportCancellation);
        _exportCancellation = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.ClosingToken);
        var current = _exportCancellation;
        var token = current.Token;
        try
        {
            var path = await _workflowRecipeDialog
                .PickSavePathAsync("fractal-art.fractal-workflow.json", token)
                .ConfigureAwait(true);
            if (string.IsNullOrWhiteSpace(path))
            {
                StatusMessage = "已取消 Workflow 配方导出。";
                return;
            }
            IsExporting = true;
            await _workflowRecipeFiles.ExportAsync(_artwork, path, token).ConfigureAwait(true);
            StatusMessage = $"Workflow 配方已导出：{path}";
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            if (!_lifetime.IsClosing)
            {
                StatusMessage = "Workflow 配方导出已取消。";
            }
        }
        catch (Exception exception)
        {
            StatusMessage = $"Workflow 配方导出失败：{exception.Message}";
        }
        finally
        {
            if (ReferenceEquals(current, _exportCancellation))
            {
                _exportCancellation = null;
                current.Dispose();
                IsExporting = false;
            }
        }
    }

    [RelayCommand]
    private void CancelOperation()
    {
        _previewCancellation?.Cancel();
        _exportCancellation?.Cancel();
        _variationCancellation?.Cancel();
        if (MathLens.IsOpen)
        {
            MathLens.Cancel();
        }
    }

    [RelayCommand]
    private async Task ToggleMathLensAsync()
    {
        if (MathLens.IsOpen)
        {
            MathLens.Close();
            StatusMessage = "数学透镜已关闭，画布交互已恢复。";
            return;
        }

        await MathLens.OpenAsync(_artwork, SelectedLayer.Id).ConfigureAwait(true);
        StatusMessage = MathLens.Status;
    }

    [RelayCommand]
    private void PlayMathLens() => MathLens.Play();

    [RelayCommand]
    private void PauseMathLens() => MathLens.Pause();

    [RelayCommand]
    private void PreviousMathLensFrame() => MathLens.Previous();

    [RelayCommand]
    private void NextMathLensFrame() => MathLens.Next();

    [RelayCommand]
    private void ResetMathLens() => MathLens.Reset();

    [RelayCommand]
    private void CancelMathLens() => MathLens.Cancel();

    internal Task SelectMathLensPointAsync(double normalizedX, double normalizedY) =>
        MathLens.SelectPointAsync(_artwork, SelectedLayer.Id, normalizedX, normalizedY);

    internal Task RenderPreviewNowAsync(CancellationToken cancellationToken = default) =>
        RenderPreviewCoreAsync(debounce: false, cancellationToken);

    /// <summary>开始一次连续拖动画布；拖动期间可以多次刷新，但撤销历史只记录手势开始前的一份作品。</summary>
    internal void BeginViewportInteraction()
    {
        ThrowIfDisposed();
        if (!IsEscapeTimeFamily)
        {
            return;
        }

        _viewportInteractionStart ??= _artwork;
    }

    internal void PanViewport(double deltaX, double deltaY, double viewportHeight)
    {
        if (!IsEscapeTimeFamily)
        {
            return;
        }

        var candidate = IsMandelbrotGenerator
            ? WithMandelbrotViewport(HighPrecisionViewport.Pan(
                ToJuliaViewport(_artwork.Mandelbrot), deltaX, deltaY, viewportHeight))
            : _artwork with
            {
                Julia = HighPrecisionViewport.Pan(_artwork.Julia, deltaX, deltaY, viewportHeight)
            };
        TransientPreview = TransientPreview.Pan(deltaX, deltaY);
        TryMutate(candidate, recordHistory: _viewportInteractionStart is null);
    }

    internal void EndViewportInteraction()
    {
        var start = _viewportInteractionStart;
        _viewportInteractionStart = null;
        if (start is null || start == _artwork)
        {
            return;
        }

        _history.Record(start);
        OnPropertyChanged(nameof(CanUndo));
        OnPropertyChanged(nameof(CanRedo));
    }

    /// <summary>围绕鼠标指向的复平面位置缩放；中心和尺度全程使用作品声明的十进制精度。</summary>
    internal void ZoomViewport(
        double pointerX,
        double pointerY,
        double viewportWidth,
        double viewportHeight,
        double wheelDelta)
    {
        if (!IsEscapeTimeFamily)
        {
            return;
        }

        var candidate = IsMandelbrotGenerator
            ? WithMandelbrotViewport(HighPrecisionViewport.ZoomAt(
                ToJuliaViewport(_artwork.Mandelbrot),
                pointerX,
                pointerY,
                viewportWidth,
                viewportHeight,
                wheelDelta))
            : _artwork with
            {
                Julia = HighPrecisionViewport.ZoomAt(
                    _artwork.Julia,
                    pointerX,
                    pointerY,
                    viewportWidth,
                    viewportHeight,
                    wheelDelta)
            };
        if (candidate == _artwork)
        {
            return;
        }

        var steps = Math.Clamp((int)Math.Ceiling(Math.Abs(wheelDelta)), 1, 8);
        var factor = Math.Pow(wheelDelta > 0 ? 0.8d : 1.25d, steps);
        TransientPreview = TransientPreview.Zoom(factor, pointerX, pointerY);
        TryMutate(candidate);
    }

    private static IReadOnlyList<LSystemRuleDefinition> ParseLSystemRules(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            throw new InvalidDataException("请至少输入一条产生式，例如 F=F+F。");
        }

        var rules = new List<LSystemRuleDefinition>();
        foreach (var rawLine in text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (rawLine.Length < 2 || rawLine[1] != '=' || rawLine[0] is < 'A' or > 'Z')
            {
                throw new InvalidDataException($"规则“{rawLine}”格式错误；请使用一行一个 A=替换串。");
            }

            rules.Add(new LSystemRuleDefinition(rawLine[0], rawLine[2..]));
        }

        return rules;
    }

    /// <summary>
    /// 视口算法本身只依赖中心、尺度和精度。这里用 JuliaDefinition 作为已有值对象适配器，
    /// 然后只把三项视口结果写回 Mandelbrot；常量字段从不进入 Mandelbrot 领域模型。
    /// </summary>
    private static JuliaDefinition ToJuliaViewport(MandelbrotDefinition definition) => new(
        definition.CenterX,
        definition.CenterY,
        definition.Scale,
        "0",
        "0",
        definition.MaxIterations,
        definition.ForceHighPrecision,
        definition.PrecisionDigits);

    private ArtworkDefinition WithMandelbrotViewport(JuliaDefinition viewport) => _artwork with
    {
        Mandelbrot = _artwork.Mandelbrot with
        {
            CenterX = viewport.CenterX,
            CenterY = viewport.CenterY,
            Scale = viewport.Scale
        }
    };

    private bool IsLocked(VariationLockGroups group) => _artwork.Exploration.Locks.HasFlag(group);

    private void SetLock(VariationLockGroups group, bool isLocked)
    {
        var locks = isLocked
            ? _artwork.Exploration.Locks | group
            : _artwork.Exploration.Locks & ~group;
        TryMutateExploration(_artwork.Exploration with { Locks = locks });
    }

    private void TryMutateExploration(ArtworkExplorationDefinition exploration)
    {
        TryMutate(_artwork with { Exploration = exploration }, renderPreview: false);
    }

    private void CancelVariationWork()
    {
        Interlocked.Increment(ref _variationGeneration);
        CancelAndDispose(ref _variationCancellation);
        IsExploring = false;
    }

    private async Task RestoreVariationPreviewsAsync(CancellationToken cancellationToken)
    {
        var rendered = await _variationExplorer.RenderAsync(
            _artwork,
            _artwork.Exploration.Candidates,
            cancellationToken).ConfigureAwait(true);
        cancellationToken.ThrowIfCancellationRequested();
        ReplaceVariationItems(rendered);
    }

    private void ReplaceVariationItems(IReadOnlyList<RenderedVariation> rendered)
    {
        foreach (var oldItem in VariationCandidates)
        {
            oldItem.Dispose();
        }

        VariationCandidates.Clear();
        var favoriteIds = _artwork.Exploration.Favorites
            .Select(favorite => favorite.Id)
            .ToHashSet(StringComparer.Ordinal);
        foreach (var renderedVariation in rendered.OrderBy(item => item.Candidate.Number))
        {
            var bitmap = _previewImageFactory.Create(renderedVariation.Image, CancellationToken.None);
            VariationCandidates.Add(new VariationCandidateItem(
                renderedVariation.Candidate,
                bitmap,
                favoriteIds.Contains($"fav-{renderedVariation.Candidate.Id}")));
        }
    }

    private void TryLayerOperation(Func<ArtworkDefinition> operation)
    {
        try
        {
            Mutate(operation());
        }
        catch (Exception exception) when (exception is InvalidOperationException or InvalidDataException)
        {
            StatusMessage = exception.Message;
        }
    }

    private void UpdateSelectedLayer(ArtworkLayerDefinition replacement) =>
        TryLayerOperation(() => _layerEditor.Update(_artwork, replacement));

    private void UpdateSelectedCommon(
        double? opacity = null,
        LayerBlendMode? blendMode = null,
        ScalarMaskDefinition? mask = null,
        bool setMask = false,
        LayerTransformDefinition? transform = null)
    {
        var current = SelectedLayer;
        var replacement = current switch
        {
            FractalLayerDefinition fractal => fractal with
            {
                Opacity = opacity ?? fractal.Opacity,
                BlendMode = blendMode ?? fractal.BlendMode,
                Mask = setMask ? mask : fractal.Mask,
                Transform = transform ?? fractal.Transform
            },
            LayerGroupDefinition group => group with
            {
                Opacity = opacity ?? group.Opacity,
                BlendMode = blendMode ?? group.BlendMode,
                Mask = setMask ? mask : group.Mask,
                Transform = transform ?? group.Transform
            },
            UnavailableLayerDefinition unavailable => unavailable with
            {
                Opacity = opacity ?? unavailable.Opacity,
                BlendMode = blendMode ?? unavailable.BlendMode,
                Mask = setMask ? mask : unavailable.Mask,
                Transform = transform ?? unavailable.Transform
            },
            _ => current
        };
        UpdateSelectedLayer(replacement);
    }

    private void UpdateTransform(Func<LayerTransformDefinition, LayerTransformDefinition> update) =>
        UpdateSelectedCommon(transform: update(SelectedLayer.Transform));

    private void UpdateMasterEffect(ArtworkEffectDefinition replacement)
    {
        var effects = _artwork.MasterEffects.Effects.ToList();
        var index = effects.FindIndex(effect => effect.TypeId == replacement.TypeId);
        if (index >= 0)
        {
            effects[index] = replacement;
        }
        else if (replacement is ToneEffectDefinition)
        {
            // 宽容读取的 v7 文件可能暂时没有已知 Tone。编辑时按规范位置补入，而不是丢弃用户操作。
            var bloomIndex = effects.FindIndex(effect => effect is BloomEffectDefinition);
            effects.Insert(bloomIndex < 0 ? 0 : bloomIndex, replacement);
        }
        else if (replacement is BloomEffectDefinition)
        {
            var lastTone = effects.FindLastIndex(effect => effect is ToneEffectDefinition);
            effects.Insert(lastTone + 1, replacement);
        }

        TryMutate(_artwork with
        {
            MasterEffects = new EffectChainDefinition(_artwork.MasterEffects.Version, effects)
        });
    }

    private static string BlendModeLabel(LayerBlendMode mode) => mode switch
    {
        LayerBlendMode.Multiply => "Multiply（正片叠底）",
        LayerBlendMode.Screen => "Screen（滤色）",
        LayerBlendMode.Add => "Add（相加）",
        LayerBlendMode.Overlay => "Overlay（叠加）",
        _ => "Normal（正常）"
    };

    private void RefreshLayerItems()
    {
        LayerItems.Clear();
        MaskSources.Clear();
        GroupOptions.Clear();
        MaskSources.Add(new MaskSourceOption(string.Empty, "无遮罩"));
        foreach (var layer in _artwork.Layers)
        {
            LayerItems.Add(CreateLayerItem(layer, false));
            if (layer is LayerGroupDefinition group)
            {
                GroupOptions.Add(new LayerGroupOption(group.Id, group.Name));
                foreach (var child in group.Children)
                {
                    LayerItems.Add(CreateLayerItem(child, true));
                }
            }
        }

        foreach (var source in ArtworkLayerTree.EnumerateFractals(_artwork.Layers)
                     .Where(layer => (layer.GeneratorKind is FractalGeneratorKind.Julia or FractalGeneratorKind.Mandelbrot or
                                         FractalGeneratorKind.StrangeAttractor) &&
                                     layer.Id != SelectedLayer.Id))
        {
            MaskSources.Add(new MaskSourceOption(source.Id, source.Name));
        }

        SelectedTargetGroup = GroupOptions.FirstOrDefault();
    }

    private static ArtworkLayerItem CreateLayerItem(ArtworkLayerDefinition layer, bool isChild) => new(
        layer.Id,
        layer.Name,
        layer.IsVisible,
        layer is LayerGroupDefinition,
        isChild,
        layer switch
        {
            FractalLayerDefinition fractal => fractal.GeneratorKind.ToString(),
            LayerGroupDefinition => "Group",
            UnavailableLayerDefinition unavailable => $"Unavailable: {unavailable.TypeId} v{unavailable.Version}",
            _ => "Unknown"
        },
        BlendModeLabel(layer.BlendMode));

    private void Mutate(ArtworkDefinition candidate, bool recordHistory = true, bool renderPreview = true)
    {
        ThrowIfDisposed();
        if (ReferenceEquals(candidate, _artwork) || candidate == _artwork)
        {
            return;
        }

        _validator.Validate(candidate);
        var previousLayer = _artwork.SelectedFractalLayer;
        var wasDirty = IsDirty;
        if (recordHistory)
        {
            _history.Record(_artwork);
        }
        _artwork = candidate;
        _revision++;
        NotifyArtworkProperties();
        NotifyHistoryAndDirty(wasDirty);
        RefreshMathLens(previousLayer, candidate.SelectedFractalLayer);
        if (renderPreview)
        {
            _ = RenderPreviewCoreAsync(debounce: true, CancellationToken.None);
        }
    }

    private void TryMutate(ArtworkDefinition candidate, bool recordHistory = true, bool renderPreview = true)
    {
        try
        {
            Mutate(candidate, recordHistory, renderPreview);
        }
        catch (InvalidDataException exception)
        {
            StatusMessage = exception.Message;
        }
    }

    private void SetAttractor<T>(T _, Func<StrangeAttractorDefinition, StrangeAttractorDefinition> replace) =>
        TryMutate(_artwork with { StrangeAttractor = replace(_artwork.StrangeAttractor) });

    private void SetHighPrecisionNumber(
        string? text,
        string current,
        Func<string, ArtworkDefinition> createCandidate)
    {
        if (!ArbitraryDecimal.TryParse(text, out var value))
        {
            StatusMessage = "请输入普通十进制或科学计数法，例如 -0.745、1e-40。";
            return;
        }

        var normalized = value.Round(PrecisionDigits).ToString();
        if (!string.Equals(normalized, current, StringComparison.Ordinal))
        {
            TryMutate(createCandidate(normalized));
        }
    }

    private void SetColor(
        string? text,
        RgbaColor current,
        Func<RgbaColor, ArtworkDefinition> createCandidate)
    {
        if (!RgbaColor.TryParse(text, out var color))
        {
            StatusMessage = "颜色必须是 #RRGGBB 或 #RRGGBBAA。";
            return;
        }

        if (color != current)
        {
            Mutate(createCandidate(color));
        }
    }

    private void ApplyHistory(ArtworkDefinition candidate)
    {
        CancelVariationWork();
        var previousLayer = _artwork.SelectedFractalLayer;
        var wasDirty = IsDirty;
        _artwork = candidate;
        _revision++;
        NotifyArtworkProperties();
        NotifyHistoryAndDirty(wasDirty);
        RefreshVariationPresentationAfterHistory();
        RefreshMathLens(previousLayer, candidate.SelectedFractalLayer);
        _ = RenderPreviewCoreAsync(debounce: false, CancellationToken.None);
    }

    private void RefreshVariationPresentationAfterHistory()
    {
        if (VariationCandidates.Select(item => item.Definition)
            .SequenceEqual(_artwork.Exploration.Candidates))
        {
            SynchronizeVariationFavoriteStates();
            return;
        }

        foreach (var item in VariationCandidates)
        {
            item.Dispose();
        }

        VariationCandidates.Clear();
        if (_artwork.Exploration.Candidates.Count == 0)
        {
            return;
        }

        _variationCancellation = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.ClosingToken);
        var current = _variationCancellation;
        var generation = Interlocked.Increment(ref _variationGeneration);
        IsExploring = true;
        _ = RestoreVariationPreviewsAfterHistoryAsync(current, generation);
    }

    /// <summary>撤销/重做可能切换候选批次；缩略图可重算，因此异步恢复失败只影响呈现，不回滚已经正确恢复的作品。</summary>
    private async Task RestoreVariationPreviewsAfterHistoryAsync(CancellationTokenSource current, long generation)
    {
        try
        {
            var rendered = await _variationExplorer.RenderAsync(
                _artwork,
                _artwork.Exploration.Candidates,
                current.Token).ConfigureAwait(true);
            if (generation == Volatile.Read(ref _variationGeneration) && !_disposed && !_lifetime.IsClosing)
            {
                ReplaceVariationItems(rendered);
            }
        }
        catch (OperationCanceledException) when (current.IsCancellationRequested)
        {
            // 新命令或关闭会取消恢复；作品配方已正确切换，不需要把取消当成错误。
        }
        catch (Exception exception)
        {
            if (generation == Volatile.Read(ref _variationGeneration))
            {
                StatusMessage = $"候选配方已恢复，但缩略图重建失败：{exception.Message}";
            }
        }
        finally
        {
            if (Interlocked.CompareExchange(ref _variationCancellation, null, current) == current)
            {
                current.Dispose();
                IsExploring = false;
            }
        }
    }

    private void SynchronizeVariationFavoriteStates()
    {
        var favoriteIds = _artwork.Exploration.Favorites.Select(item => item.Id).ToHashSet(StringComparer.Ordinal);
        foreach (var item in VariationCandidates)
        {
            item.IsFavorite = favoriteIds.Contains($"fav-{item.Definition.Id}");
        }
    }

    private async Task RenderPreviewCoreAsync(bool debounce, CancellationToken externalCancellation)
    {
        ThrowIfDisposed();
        var current = CancellationTokenSource.CreateLinkedTokenSource(
            _lifetime.ClosingToken,
            externalCancellation);
        var previous = Interlocked.Exchange(ref _previewCancellation, current);
        previous?.Cancel();
        previous?.Dispose();
        var generation = Interlocked.Increment(ref _previewGeneration);
        IsRendering = true;

        try
        {
            var snapshot = _artwork;
            var requestedContext = RenderContext.ForPreview(snapshot);
            // 连续输入先提交最低成本的真实预览；若用户开启精细预览，则同一 generation 在稳定后再提升质量。
            // 两次结果都来自真实数值管线，暂态 Bitmap 变换只负责填补首个结果到达前的感知延迟。
            var context = debounce && snapshot.Presentation.HighQualityPreview
                ? RenderContext.ForPreview(snapshot with
                {
                    Presentation = snapshot.Presentation with { HighQualityPreview = false }
                })
                : requestedContext;
            var precisionLabel = snapshot.GeneratorFamily switch
            {
                GeneratorFamily.LSystem => "递归路径描边",
                GeneratorFamily.Attractor =>
                    $"{snapshot.StrangeAttractor.Formula} 点云密度 · {context.PointSampleBudget:N0} 点预算",
                _ when context.NumericPrecision == NumericPrecision.Arbitrary =>
                    $"任意精度 {context.EffectivePrecisionDigits}/{context.ConfiguredPrecisionDigits} 位",
                _ => "double 快速模式"
            };
            StatusMessage = $"正在渲染 {context.Width}×{context.Height} 交互预览 · {precisionLabel}…";
            if (debounce)
            {
                await Task.Delay(120, current.Token).ConfigureAwait(true);
            }

            var result = await _renderPipeline.RenderAsync(snapshot, context, current.Token).ConfigureAwait(true);
            if (!TryCommitPreview(result.Image, context, generation, current.Token))
            {
                return;
            }

            if (debounce && requestedContext.Width != context.Width)
            {
                await Task.Delay(160, current.Token).ConfigureAwait(true);
                var detailed = await _renderPipeline.RenderAsync(snapshot, requestedContext, current.Token).ConfigureAwait(true);
                TryCommitPreview(detailed.Image, requestedContext, generation, current.Token);
            }
        }
        catch (OperationCanceledException) when (current.IsCancellationRequested)
        {
            // 新请求、Document 关闭或显式取消都属于正常控制流；只有当前代次才更新用户状态。
            if (generation == Volatile.Read(ref _previewGeneration) && !_lifetime.IsClosing)
            {
                StatusMessage = "预览已取消。";
            }
        }
        catch (Exception exception)
        {
            if (generation == Volatile.Read(ref _previewGeneration))
            {
                StatusMessage = $"预览失败：{exception.Message}";
            }
        }
        finally
        {
            if (Interlocked.CompareExchange(ref _previewCancellation, null, current) == current)
            {
                current.Dispose();
                IsRendering = false;
            }
        }
    }

    /// <summary>
    /// 真实帧提交的唯一入口。generation 检查位于 Bitmap 创建前后；即使底层忽略取消，旧帧也无法覆盖新状态。
    /// 成功提交后立即清除暂态变换，确保屏幕重新与真实像素坐标对齐。
    /// </summary>
    private bool TryCommitPreview(ImageSurface result, RenderContext context, long generation, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (generation != Volatile.Read(ref _previewGeneration) || _lifetime.IsClosing || _disposed)
        {
            return false;
        }

        var bitmap = _previewImageFactory.Create(result, token);
        if (generation != Volatile.Read(ref _previewGeneration) || _lifetime.IsClosing || _disposed)
        {
            bitmap?.Dispose();
            return false;
        }

        var old = PreviewImage;
        PreviewImage = bitmap;
        old?.Dispose();
        TransientPreview = TransientPreviewTransform.Identity;
        LastPreviewFingerprint = RenderFingerprint.Create(result);
        var diagnostics = result.Diagnostics;
        var fallback = diagnostics is { PrecisionFallbackPixels: > 0 }
            ? $" · 回退 {diagnostics.PrecisionFallbackPixels} 像素"
            : string.Empty;
        var precision = result.Diagnostics?.Kernel is "recursive-tree" or "l-system" or "attractor-density"
            ? result.Diagnostics.Kernel switch
            {
                "l-system" => "L-System 路径描边",
                "attractor-density" => $"{_artwork.StrangeAttractor.Formula} 点云密度",
                _ => "递归树路径描边"
            }
            : context.NumericPrecision == NumericPrecision.Arbitrary
            ? $"任意精度 {context.EffectivePrecisionDigits}/{context.ConfiguredPrecisionDigits} 位"
            : "double 快速模式";
        StatusMessage = $"预览完成 · {precision}{fallback} · renderer v{context.RendererVersion} · {LastPreviewFingerprint}";
        return true;
    }

    private void NotifyArtworkProperties()
    {
        RefreshLayerItems();
        OnPropertyChanged(nameof(SelectedLayer));
        OnPropertyChanged(nameof(IsFractalLayerSelected));
        OnPropertyChanged(nameof(IsGroupSelected));
        OnPropertyChanged(nameof(CanMoveSelectedIntoGroup));
        OnPropertyChanged(nameof(CanMoveSelectedOutOfGroup));
        OnPropertyChanged(nameof(SelectedLayerName));
        OnPropertyChanged(nameof(SelectedLayerVisible));
        OnPropertyChanged(nameof(SelectedLayerOpacity));
        OnPropertyChanged(nameof(SelectedBlendMode));
        OnPropertyChanged(nameof(LayerPositionX));
        OnPropertyChanged(nameof(LayerPositionY));
        OnPropertyChanged(nameof(LayerScale));
        OnPropertyChanged(nameof(LayerRotation));
        OnPropertyChanged(nameof(LayerAnchorX));
        OnPropertyChanged(nameof(LayerAnchorY));
        OnPropertyChanged(nameof(SelectedMaskSource));
        OnPropertyChanged(nameof(MaskThreshold));
        OnPropertyChanged(nameof(MaskSoftness));
        OnPropertyChanged(nameof(MaskInverted));
        OnPropertyChanged(nameof(HasSelectedMask));
        OnPropertyChanged(nameof(CanvasWidth));
        OnPropertyChanged(nameof(CanvasHeight));
        OnPropertyChanged(nameof(BackgroundHex));
        OnPropertyChanged(nameof(Seed));
        OnPropertyChanged(nameof(IsEscapeTimeFamily));
        OnPropertyChanged(nameof(IsLSystemFamily));
        OnPropertyChanged(nameof(IsJuliaGenerator));
        OnPropertyChanged(nameof(IsMandelbrotGenerator));
        OnPropertyChanged(nameof(IsLSystemGenerator));
        OnPropertyChanged(nameof(IsRecursiveTreeGenerator));
        OnPropertyChanged(nameof(IsAttractorGenerator));
        OnPropertyChanged(nameof(IsSeedControlVisible));
        OnPropertyChanged(nameof(GeneratorKindName));
        OnPropertyChanged(nameof(SelectedAttractorFormula));
        OnPropertyChanged(nameof(AttractorA));
        OnPropertyChanged(nameof(AttractorB));
        OnPropertyChanged(nameof(AttractorC));
        OnPropertyChanged(nameof(AttractorD));
        OnPropertyChanged(nameof(AttractorBurnIn));
        OnPropertyChanged(nameof(AttractorSampleCount));
        OnPropertyChanged(nameof(AttractorExposure));
        OnPropertyChanged(nameof(AttractorGamma));
        OnPropertyChanged(nameof(AttractorGlowEnabled));
        OnPropertyChanged(nameof(AttractorGlowSigma));
        OnPropertyChanged(nameof(AttractorGlowStrength));
        OnPropertyChanged(nameof(CenterX));
        OnPropertyChanged(nameof(CenterY));
        OnPropertyChanged(nameof(Scale));
        OnPropertyChanged(nameof(ConstantReal));
        OnPropertyChanged(nameof(ConstantImaginary));
        OnPropertyChanged(nameof(MaxIterations));
        OnPropertyChanged(nameof(Detail));
        OnPropertyChanged(nameof(Flow));
        OnPropertyChanged(nameof(Curl));
        OnPropertyChanged(nameof(ForceHighPrecision));
        OnPropertyChanged(nameof(PrecisionDigits));
        OnPropertyChanged(nameof(TreeDepth));
        OnPropertyChanged(nameof(TreeBranches));
        OnPropertyChanged(nameof(TreeBranchAngle));
        OnPropertyChanged(nameof(TreeLengthDecay));
        OnPropertyChanged(nameof(TreeRandomness));
        OnPropertyChanged(nameof(TreeTrunkLength));
        OnPropertyChanged(nameof(TreeStrokeWidth));
        OnPropertyChanged(nameof(LSystemAxiom));
        OnPropertyChanged(nameof(LSystemRulesText));
        OnPropertyChanged(nameof(LSystemIterations));
        OnPropertyChanged(nameof(LSystemTurnAngle));
        OnPropertyChanged(nameof(LSystemInitialHeading));
        OnPropertyChanged(nameof(LSystemStepLength));
        OnPropertyChanged(nameof(LSystemLengthDecay));
        OnPropertyChanged(nameof(LSystemStrokeWidth));
        OnPropertyChanged(nameof(LSystemStrokeWidthDecay));
        OnPropertyChanged(nameof(LSystemDiagnostics));
        OnPropertyChanged(nameof(GradientStartHex));
        OnPropertyChanged(nameof(GradientEndHex));
        OnPropertyChanged(nameof(HighQualityPreview));
        OnPropertyChanged(nameof(ToneEnabled));
        OnPropertyChanged(nameof(ToneBrightness));
        OnPropertyChanged(nameof(ToneContrast));
        OnPropertyChanged(nameof(ToneSaturation));
        OnPropertyChanged(nameof(MasterBloomEnabled));
        OnPropertyChanged(nameof(MasterBloomThreshold));
        OnPropertyChanged(nameof(MasterBloomSigma));
        OnPropertyChanged(nameof(MasterBloomStrength));
        OnPropertyChanged(nameof(MutationStrength));
        OnPropertyChanged(nameof(IsSeedLocked));
        OnPropertyChanged(nameof(IsCompositionLocked));
        OnPropertyChanged(nameof(IsShapeLocked));
        OnPropertyChanged(nameof(IsColorLocked));
        OnPropertyChanged(nameof(VariationModeName));
        OnPropertyChanged(nameof(Favorites));
    }

    private void NotifyHistoryAndDirty(bool wasDirty)
    {
        OnPropertyChanged(nameof(CanUndo));
        OnPropertyChanged(nameof(CanRedo));
        if (wasDirty != IsDirty)
        {
            IsDirtyChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    partial void OnIsRenderingChanged(bool value) => OnPropertyChanged(nameof(IsOperationBusy));
    partial void OnIsExportingChanged(bool value) => OnPropertyChanged(nameof(IsOperationBusy));
    partial void OnIsImageLabExportingChanged(bool value) => OnPropertyChanged(nameof(IsOperationBusy));
    partial void OnIsExploringChanged(bool value) => OnPropertyChanged(nameof(IsOperationBusy));
    partial void OnPreviewImageChanged(Bitmap? value) => OnPropertyChanged(nameof(IsPreviewEmpty));

    private void HandleMathLensPropertyChanged(object? sender, PropertyChangedEventArgs eventArgs)
    {
        if (eventArgs.PropertyName == nameof(MathLensSession.IsOpen))
        {
            OnPropertyChanged(nameof(IsMathLensOpen));
            OnPropertyChanged(nameof(IsMathLensClosed));
        }

        if (eventArgs.PropertyName == nameof(MathLensSession.IsBusy))
        {
            OnPropertyChanged(nameof(IsOperationBusy));
        }

        if (eventArgs.PropertyName == nameof(MathLensSession.Status))
        {
            StatusMessage = MathLens.Status;
        }
    }

    private void RefreshMathLens(FractalLayerDefinition previous, FractalLayerDefinition current)
    {
        if (!MathLens.IsOpen)
        {
            return;
        }

        var preserveSelection = previous.Id == current.Id &&
            previous.GeneratorKind is FractalGeneratorKind.Julia or FractalGeneratorKind.Mandelbrot &&
            current.GeneratorKind is FractalGeneratorKind.Julia or FractalGeneratorKind.Mandelbrot;
        _ = MathLens.RefreshAsync(_artwork, SelectedLayer.Id, preserveSelection);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        Interlocked.Increment(ref _previewGeneration);
        CancelAndDispose(ref _previewCancellation);
        CancelAndDispose(ref _exportCancellation);
        CancelAndDispose(ref _variationCancellation);
        MathLens.PropertyChanged -= HandleMathLensPropertyChanged;
        MathLens.Dispose();
        foreach (var item in VariationCandidates)
        {
            item.Dispose();
        }
        VariationCandidates.Clear();
        PreviewImage?.Dispose();
        PreviewImage = null;
    }

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_disposed, this);

    private static void CancelAndDispose(ref CancellationTokenSource? source)
    {
        var current = Interlocked.Exchange(ref source, null);
        current?.Cancel();
        current?.Dispose();
    }
}
