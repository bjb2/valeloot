using System;
using System.Runtime.InteropServices;
using Il2CppInterop.Runtime;

namespace ValeLoot;

/// <summary>
/// An always-visible carried-weight warning on the HUD inventory button.
///
/// The game already owns both answers: <c>Formula.GetWeightValue(InventoryData)</c> computes current
/// carried weight and <c>Formula.GetWeightLimit(PlayerController)</c> computes the live limit, including
/// level and status bonuses. ValeLoot only maps their ratio to presentation.
///
/// It never creates Unity objects. The warning only modifies existing Graphics under the button,
/// preserving each layer's original colour for scene changes and unload.
/// </summary>
internal static class BagFillIndicator
{
    private const int TickInterval = 15;
    private const int GraphicDepth = 4;
    private const int MaxGraphics = 16;


    private delegate int WeightFn(IntPtr value, IntPtr methodInfo);
    private delegate void VoidFn(IntPtr self, IntPtr methodInfo);
    private delegate IntPtr PointerFn(IntPtr self, IntPtr methodInfo);
    private delegate IntPtr GetComponentFn(IntPtr self, IntPtr type, IntPtr methodInfo);
    private delegate int CountFn(IntPtr self, IntPtr methodInfo);
    private delegate IntPtr GetChildFn(IntPtr self, int index, IntPtr methodInfo);

    private readonly struct Color
    {
        public readonly float R;
        public readonly float G;
        public readonly float B;
        public readonly float A;

        public Color(float r, float g, float b, float a)
        {
            R = r;
            G = g;
            B = b;
            A = a;
        }
    }

    public static bool Installed { get; private set; }
    public static bool Enabled { get; private set; }
    public static int YellowPercent { get; private set; } = 60;
    public static int RedPercent { get; private set; } = 80;
    public static float TintStrength { get; private set; } = 0.85f;
    public static long Updates;
    public static long Errors;

    private static Action<string> _log = _ => { };
    private static bool _failed;
    private static bool _attachedReported;
    private static bool _updateReported;
    private static int _frames;

    // PlayerSave -> local character -> inventory/controller.
    private static int _saveData = -1;
    private static int _saveController = -1;
    private static int _characterInventory = -1;
    private static int _controllerStatus = -1;
    private static int _statusLevel = -1;

    // App.UI -> UIManager.Game -> UIGame.ButtonInventory.
    private static IntPtr _appClass;
    private static int _uiGame = -1;
    private static int _gameInventoryButton = -1;

    // Existing Button/Transform/Graphic traversal.
    private static int _targetGraphic = -1;
    private static int _graphicColor = -1;
    private static IntPtr _graphicType;
    private static PointerFn? _getTransform;
    private static GetComponentFn? _getComponent;
    private static CountFn? _getChildCount;
    private static GetChildFn? _getChild;
    private static VoidFn? _setAllDirty;


    private static WeightFn? _getWeightValue;
    private static WeightFn? _getWeightLimit;

    // Fixed buffers: avoid allocating a list on the main-thread update path.
    private static readonly IntPtr[] Graphics = new IntPtr[MaxGraphics];
    private static readonly Color[] OriginalColors = new Color[MaxGraphics];
    private static int _graphicCount;
    private static IntPtr _button;
    private static int _lastCurrent = int.MinValue;
    private static int _lastLimit = int.MinValue;

    public static bool Install(bool enabled, int yellowPercent, int redPercent, float tintStrength, Action<string> log)
    {
        _log = log;
        Enabled = enabled;
        YellowPercent = yellowPercent < 1 ? 1 : yellowPercent > 98 ? 98 : yellowPercent;
        RedPercent = redPercent <= YellowPercent ? YellowPercent + 1 : redPercent > 99 ? 99 : redPercent;
        TintStrength = tintStrength < 0.1f ? 0.1f : tintStrength > 1f ? 1f : tintStrength;

        if (!Enabled)
        {
            log("bag fill indicator disabled by config");
            return false;
        }

        IntPtr playerSave = Il2CppMeta.FindClass("", "PlayerSave", HookCensus.GameAssemblies);
        IntPtr characterData = Il2CppMeta.FindClass("", "CharacterData", HookCensus.GameAssemblies);
        IntPtr baseController = Il2CppMeta.FindClass("", "BaseUnitController", HookCensus.GameAssemblies);
        IntPtr status = Il2CppMeta.FindClass("", "StatusComponent", HookCensus.GameAssemblies);
        IntPtr formula = Il2CppMeta.FindClass("", "Formula", HookCensus.GameAssemblies);
        IntPtr uiManager = Il2CppMeta.FindClass("", "UIManager", HookCensus.GameAssemblies);
        IntPtr uiGame = Il2CppMeta.FindClass("", "UIGame", HookCensus.GameAssemblies);
        IntPtr selectable = Il2CppMeta.FindClass("UnityEngine.UI", "Selectable", "UnityEngine.UI.dll", "Unity.ugui.dll");
        IntPtr component = Il2CppMeta.FindClass("UnityEngine", "Component", "UnityEngine.CoreModule.dll");
        IntPtr transform = Il2CppMeta.FindClass("UnityEngine", "Transform", "UnityEngine.CoreModule.dll");
        IntPtr graphic = Il2CppMeta.FindClass("UnityEngine.UI", "Graphic", "UnityEngine.UI.dll", "Unity.ugui.dll");

        _appClass = Il2CppMeta.FindClass("", "App", HookCensus.GameAssemblies);
        _saveData = Il2CppMeta.PropertyFieldOffset(playerSave, "Data");
        _saveController = Il2CppMeta.PropertyFieldOffset(playerSave, "controller");
        _characterInventory = Il2CppMeta.PropertyFieldOffset(characterData, "Inventory");
        _controllerStatus = Il2CppMeta.PropertyFieldOffset(baseController, "Status");
        _statusLevel = Il2CppMeta.PropertyFieldOffset(status, "Level");
        _uiGame = Il2CppMeta.PropertyFieldOffset(uiManager, "Game");
        _gameInventoryButton = Il2CppMeta.PropertyFieldOffset(uiGame, "ButtonInventory");
        _targetGraphic = Il2CppMeta.FieldOffsetUp(selectable, "m_TargetGraphic");
        _graphicColor = Il2CppMeta.FieldOffsetUp(graphic, "m_Color");

        Il2CppMeta.MethodInfo? weightValue = Il2CppMeta.FindOverload(formula, "GetWeightValue", "InventoryData");
        Il2CppMeta.MethodInfo? weightLimit = Il2CppMeta.FindOverload(formula, "GetWeightLimit", "PlayerController");
        Il2CppMeta.MethodInfo? getTransform = Il2CppMeta.FindOverload(component, "get_transform");
        Il2CppMeta.MethodInfo? getComponent = Il2CppMeta.FindOverload(component, "GetComponent", "System.Type");
        Il2CppMeta.MethodInfo? childCount = Il2CppMeta.FindOverload(transform, "get_childCount");
        Il2CppMeta.MethodInfo? getChild = Il2CppMeta.FindOverload(transform, "GetChild", "System.Int32");
        Il2CppMeta.MethodInfo? setAllDirty = Il2CppMeta.FindOverload(graphic, "SetAllDirty");

        bool ready = _appClass != IntPtr.Zero
            && _saveData >= 0 && _saveController >= 0 && _characterInventory >= 0
            && _controllerStatus >= 0 && _statusLevel >= 0 && _uiGame >= 0 && _gameInventoryButton >= 0
            && _targetGraphic >= 0 && _graphicColor >= 0 && graphic != IntPtr.Zero
            && weightValue is not null && weightLimit is not null
            && getTransform is not null && getComponent is not null
            && childCount is not null && getChild is not null && setAllDirty is not null;

        if (!ready)
        {
            log("bag fill indicator NOT ready: weight or visible-graphic metadata did not resolve");
            return false;
        }

        _graphicType = IL2CPP.il2cpp_type_get_object(IL2CPP.il2cpp_class_get_type(graphic));
        if (_graphicType == IntPtr.Zero)
        {
            log("bag fill indicator NOT ready: Graphic System.Type did not resolve");
            return false;
        }

        _getWeightValue = Marshal.GetDelegateForFunctionPointer<WeightFn>(weightValue!.NativePtr);
        _getWeightLimit = Marshal.GetDelegateForFunctionPointer<WeightFn>(weightLimit!.NativePtr);
        _getTransform = Marshal.GetDelegateForFunctionPointer<PointerFn>(getTransform!.NativePtr);
        _getComponent = Marshal.GetDelegateForFunctionPointer<GetComponentFn>(getComponent!.NativePtr);
        _getChildCount = Marshal.GetDelegateForFunctionPointer<CountFn>(childCount!.NativePtr);
        _getChild = Marshal.GetDelegateForFunctionPointer<GetChildFn>(getChild!.NativePtr);
        _setAllDirty = Marshal.GetDelegateForFunctionPointer<VoidFn>(setAllDirty!.NativePtr);

        Installed = true;
        log($"bag fill indicator ready (yellow above {YellowPercent}%, red above {RedPercent}%, tint strength {TintStrength:0.00}); waiting for the HUD inventory button");
        return true;
    }

    public static void Tick(IntPtr playerSave)
    {
        if (!Installed || !Enabled || _failed || playerSave == IntPtr.Zero) return;

        try
        {
            IntPtr character = Marshal.ReadIntPtr(playerSave, _saveData);
            IntPtr inventory = character == IntPtr.Zero ? IntPtr.Zero : Marshal.ReadIntPtr(character, _characterInventory);
            if (inventory == IntPtr.Zero) return;

            IntPtr controller = Marshal.ReadIntPtr(playerSave, _saveController);
            if (controller == IntPtr.Zero) return;
            IntPtr status = Marshal.ReadIntPtr(controller, _controllerStatus);
            IntPtr level = status == IntPtr.Zero ? IntPtr.Zero : Marshal.ReadIntPtr(status, _statusLevel);
            if (status == IntPtr.Zero || level == IntPtr.Zero) return;

            if (++_frames < TickInterval) return;
            _frames = 0;

            int current = _getWeightValue!(inventory, IntPtr.Zero);
            int limit = _getWeightLimit!(controller, IntPtr.Zero);
            if (current < 0 || limit <= 0) return;

            IntPtr button = ResolveInventoryButton();
            if (button == IntPtr.Zero || !EnsureGraphics(button)) return;
            if (current == _lastCurrent && limit == _lastLimit) return;

            _lastCurrent = current;
            _lastLimit = limit;
            UpdateVisual(current, limit);
            Updates++;
        }
        catch (Exception e)
        {
            Errors++;
            _failed = true;
            _log($"bag fill indicator stopped after an error — {e.Message}. Loot filtering, sounds and the editor are unaffected.");
        }
    }

    public static void Uninstall()
    {
        try
        {
            if (ResolveInventoryButton() == _button) RestoreOriginalColors();
        }
        catch
        {
            // Teardown must never throw or touch a hierarchy Unity has already destroyed.
        }

        ResetLiveObjects();
        Installed = false;
        Enabled = false;
        _failed = false;
        _attachedReported = false;
        _updateReported = false;
        _frames = 0;
        _getWeightValue = null;
        _getWeightLimit = null;
        _getTransform = null;
        _getComponent = null;
        _getChildCount = null;
        _getChild = null;
        _setAllDirty = null;
        _graphicType = IntPtr.Zero;
        _log = _ => { };
    }

    public static string Status()
        => Enabled
            ? $"bag fill indicator: installed {Installed}, graphics {_graphicCount}, updates {Updates}, errors {Errors}"
            : "bag fill indicator: disabled";

    private static IntPtr ResolveInventoryButton()
    {
        if (_appClass == IntPtr.Zero || _uiGame < 0 || _gameInventoryButton < 0) return IntPtr.Zero;
        IntPtr ui = Il2CppMeta.StaticObjectField(_appClass, "UI");
        if (ui == IntPtr.Zero) return IntPtr.Zero;
        IntPtr game = Marshal.ReadIntPtr(ui, _uiGame);
        return game == IntPtr.Zero ? IntPtr.Zero : Marshal.ReadIntPtr(game, _gameInventoryButton);
    }

    private static bool EnsureGraphics(IntPtr button)
    {
        if (_button == button && _graphicCount > 0) return true;

        ResetLiveObjects();
        _button = button;
        AddGraphic(Marshal.ReadIntPtr(button, _targetGraphic));
        IntPtr root = _getTransform!(button, IntPtr.Zero);
        CollectGraphics(root, GraphicDepth);
        if (_graphicCount == 0)
        {
            _button = IntPtr.Zero;
            return false;
        }

        for (int i = 0; i < _graphicCount; i++)
            OriginalColors[i] = ReadColor(Graphics[i]);

        _lastCurrent = int.MinValue;
        _lastLimit = int.MinValue;

        if (!_attachedReported)
        {
            _attachedReported = true;
            _log($"bag fill indicator attached to {_graphicCount} existing graphic layer(s); safe tint-only mode");
        }
        return true;
    }

    private static void CollectGraphics(IntPtr transform, int depth)
    {
        if (transform == IntPtr.Zero || _graphicCount >= MaxGraphics) return;
        AddGraphic(_getComponent!(transform, _graphicType, IntPtr.Zero));
        if (depth <= 0) return;

        int count = _getChildCount!(transform, IntPtr.Zero);
        if (count < 0) return;
        if (count > 64) count = 64;
        for (int i = 0; i < count && _graphicCount < MaxGraphics; i++)
            CollectGraphics(_getChild!(transform, i, IntPtr.Zero), depth - 1);
    }

    private static void AddGraphic(IntPtr graphic)
    {
        if (graphic == IntPtr.Zero || _graphicCount >= MaxGraphics) return;
        for (int i = 0; i < _graphicCount; i++)
            if (Graphics[i] == graphic) return;
        Graphics[_graphicCount++] = graphic;
    }



    private static void UpdateVisual(int current, int limit)
    {
        float ratio = current / (float)limit;
        Color target;
        string state;
        float tintAmount;
        if (ratio > RedPercent / 100f)
        {
            target = new Color(1f, 0.025f, 0.035f, 1f);
            state = "red";
            tintAmount = TintStrength;
        }
        else if (ratio > YellowPercent / 100f)
        {
            target = new Color(1f, 0.70f, 0.02f, 1f);
            state = "yellow";
            tintAmount = TintStrength;
        }
        else
        {
            target = default;
            state = "neutral";
            tintAmount = 0f;
        }

        for (int i = 0; i < _graphicCount; i++)
        {
            Color original = OriginalColors[i];
            WriteColor(Graphics[i], new Color(
                Lerp(original.R, target.R, tintAmount),
                Lerp(original.G, target.G, tintAmount),
                Lerp(original.B, target.B, tintAmount),
                original.A));
        }

        int percent = (int)Math.Round(ratio * 100f, MidpointRounding.AwayFromZero);

        if (!_updateReported)
        {
            _updateReported = true;
            _log($"bag fill indicator first update: weight {current}/{limit} ({percent}%), state {state}, tint {tintAmount:0.00}, graphics {_graphicCount}");
        }
    }

    private static void RestoreOriginalColors()
    {
        for (int i = 0; i < _graphicCount; i++) WriteColor(Graphics[i], OriginalColors[i]);
    }

    private static Color ReadColor(IntPtr graphic)
        => ReadColorAt(graphic, _graphicColor);

    private static Color ReadColorAt(IntPtr value, int offset)
        => new(
            BitConverter.Int32BitsToSingle(Marshal.ReadInt32(value, offset)),
            BitConverter.Int32BitsToSingle(Marshal.ReadInt32(value, offset + 4)),
            BitConverter.Int32BitsToSingle(Marshal.ReadInt32(value, offset + 8)),
            BitConverter.Int32BitsToSingle(Marshal.ReadInt32(value, offset + 12)));

    private static void WriteColor(IntPtr graphic, Color color)
    {
        if (graphic == IntPtr.Zero) return;
        WriteColorAt(graphic, _graphicColor, color);
        _setAllDirty!(graphic, IntPtr.Zero);
    }

    private static void WriteColorAt(IntPtr value, int offset, Color color)
    {
        Marshal.WriteInt32(value, offset, BitConverter.SingleToInt32Bits(color.R));
        Marshal.WriteInt32(value, offset + 4, BitConverter.SingleToInt32Bits(color.G));
        Marshal.WriteInt32(value, offset + 8, BitConverter.SingleToInt32Bits(color.B));
        Marshal.WriteInt32(value, offset + 12, BitConverter.SingleToInt32Bits(color.A));
    }

    private static float Lerp(float from, float to, float amount) => from + ((to - from) * amount);


    private static void ResetLiveObjects()
    {
        for (int i = 0; i < _graphicCount; i++)
        {
            Graphics[i] = IntPtr.Zero;
            OriginalColors[i] = default;
        }
        _graphicCount = 0;
        _button = IntPtr.Zero;
        _lastCurrent = int.MinValue;
        _lastLimit = int.MinValue;
    }
}
