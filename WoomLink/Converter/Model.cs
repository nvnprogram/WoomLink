using System;
using System.Collections.Generic;

namespace WoomLink.Converter;

public enum RefType : byte
{
    Direct = 0,
    String = 1,
    Curve = 2,
    Random = 3,
    ArrangeParam = 4,
    Bitfield = 5,
    RandomPowHalf2 = 6,
    RandomPowHalf3 = 7,
    RandomPowHalf4 = 8,
    RandomPowHalf5 = 9,
    RandomPow2 = 0xA,
    RandomPow3 = 0xB,
    RandomPow4 = 0xC,
    RandomPow5 = 0xD,
    RandomPowComplement2 = 0xE,
    RandomPowComplement3 = 0xF,
    RandomPowComplement4 = 0x10,
    RandomPowComplement1Point5 = 0x11,
}

public enum PType : uint
{
    Int = 0,
    Float = 1,
    Bool = 2,
    Enum = 3,
    String = 4,
    Bitfield = 5,
}

public enum CmpType : uint
{
    Equal = 0,
    GreaterThan = 1,
    GreaterThanOrEqual = 2,
    LessThan = 3,
    LessThanOrEqual = 4,
    NotEqual = 5,
}

public enum PropType : uint
{
    Enum = 0,
    S32 = 1,
    F32 = 2,
    End = 3,
}

public enum CtnType : uint
{
    Switch = 0,
    Random = 1,
    Random2 = 2,
    Blend = 3,
    Sequence = 4,
    Mono = 5,
}

public struct ParamDef
{
    public string Name;
    public PType Type;
    public uint RawDefault;
    public string? StringDefault;
}

public struct Param
{
    public int Index;
    public RefType Type;
    public uint Value;
    public string? StringValue;
}

public struct ParamSet
{
    public ulong Bitfield;
    public List<Param> Params;
}

public struct TriggerParamSet
{
    public uint Bitfield;
    public List<Param> Params;
}

public struct DirectValue
{
    public uint Raw;
}

public struct RandomCall
{
    public float Min;
    public float Max;
}

public struct CurvePoint
{
    public float X;
    public float Y;
}

public struct CurveCall
{
    public ushort CurvePointBaseIdx;
    public ushort NumPoints;
    public ushort CurveType;
    public ushort IsGlobal;
    public string PropName;
    public uint PropIdx;
    public short PropertyIndex;
    public ushort Padding;
    public List<CurvePoint> Points;
}

public struct ArrangeGroupEntry
{
    public string GroupName;
    public uint LimitType;
    public float LimitThreshold;
    public uint Unk;
}

public struct ArrangeGroup
{
    public List<ArrangeGroupEntry> Entries;
}

public struct Condition
{
    public CtnType ParentType;

    public PropType SwitchPropType;
    public CmpType SwitchCompareType;
    public bool SwitchIsGlobal;
    public short SwitchEnumValue;
    public uint SwitchValue;
    public string? SwitchEnumName;
    public string? SwitchPropName;

    public float RandomWeight;

    public float BlendMin;
    public float BlendMax;
    public byte BlendTypeToMax;
    public byte BlendTypeToMin;

    public int SequenceContinueOnFade;
}

public struct Container
{
    public CtnType Type;
    public int ChildStartIdx;
    public int ChildEndIdx;

    public string? SwitchPropName;
    public int SwitchPropertyIndex;
    public bool SwitchIsGlobal;
    public int SwitchWatchPropertyId;
}

public struct AssetCall
{
    public string KeyName;
    public short AssetIndex;
    public ushort Flag;
    public int Duration;
    public int ParentIndex;
    public uint Guid;
    public uint KeyNameHash;
    public int AssetParamIdx;
    public int ContainerParamIdx;
    public int ConditionIdx;

    public bool IsContainer => (Flag & 1) != 0;
}

public struct ActionSlot
{
    public string Name;
    public ushort ActionStartIdx;
    public ushort ActionEndIdx;
}

public struct Action
{
    public string Name;
    public uint TriggerStartIdx;
    public uint TriggerEndIdx;
}

public struct ActionTrigger
{
    public uint Guid;
    public int AssetCallIdx;
    public uint StartFrame;
    public string? PreviousActionName;
    public int EndFrame;
    public ushort Flag;
    public ushort OverwriteHash;
    public int TriggerOverwriteIdx;

    public bool IsNameMatch => (Flag & 0x10) != 0;
}

public struct Property
{
    public string Name;
    public uint IsGlobal;
    public uint TriggerStartIdx;
    public uint TriggerEndIdx;
}

public struct PropertyTrigger
{
    public uint Guid;
    public ushort Flag;
    public ushort OverwriteHash;
    public int AssetCallIdx;
    public int ConditionIdx;
    public int TriggerOverwriteIdx;
}

public struct AlwaysTrigger
{
    public uint Guid;
    public ushort Flag;
    public ushort OverwriteHash;
    public int AssetCallIdx;
    public int TriggerOverwriteIdx;
}

public class UserData
{
    public uint Hash;
    public List<string> LocalProperties = new();
    public List<Param> UserParams = new();
    public List<Container> Containers = new();
    public List<AssetCall> AssetCalls = new();
    public List<ActionSlot> ActionSlots = new();
    public List<Action> Actions = new();
    public List<ActionTrigger> ActionTriggers = new();
    public List<Property> Properties = new();
    public List<PropertyTrigger> PropertyTriggers = new();
    public List<AlwaysTrigger> AlwaysTriggers = new();
}

public class XLinkFile
{
    public uint Version;
    public int SystemUserParamCount;
    public int SystemAssetParamCount;
    public int UserAssetParamCount;
    public List<ParamDef> UserParamDefs = new();
    public List<ParamDef> AssetParamDefs = new();
    public List<ParamDef> TriggerParamDefs = new();
    public List<string> LocalProperties = new();
    public List<string> LocalPropertyEnumValues = new();
    public List<DirectValue> DirectValues = new();
    public List<RandomCall> RandomTable = new();
    public List<CurveCall> CurveTable = new();
    public List<ArrangeGroup> ArrangeGroups = new();
    public List<ParamSet> AssetParams = new();
    public List<TriggerParamSet> TriggerOverwriteParams = new();
    public List<Condition> Conditions = new();
    public List<UserData> Users = new();
    public List<uint> UserHashes = new();

    /// <summary>Main name table strings in original binary order (for lossless roundtrip).</summary>
    public List<string> StringTable = new();
    /// <summary>PDT-internal string table in original binary order.</summary>
    public List<string> PdtStringTable = new();
}
