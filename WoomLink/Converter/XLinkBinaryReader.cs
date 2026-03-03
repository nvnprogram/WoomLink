using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace WoomLink.Converter;

public class XLinkBinaryReader
{
    private byte[] _data = null!;

    private uint U32(int off) => BinaryPrimitives.ReadUInt32LittleEndian(_data.AsSpan(off));
    private int S32(int off) => BinaryPrimitives.ReadInt32LittleEndian(_data.AsSpan(off));
    private ushort U16(int off) => BinaryPrimitives.ReadUInt16LittleEndian(_data.AsSpan(off));
    private short S16(int off) => BinaryPrimitives.ReadInt16LittleEndian(_data.AsSpan(off));
    private byte U8(int off) => _data[off];
    private float F32(int off) => BitConverter.Int32BitsToSingle(S32(off));

    private static readonly Encoding _enc = Encoding.Latin1;

    private string Str(int nameTableBase, uint relOffset)
    {
        int pos = nameTableBase + (int)relOffset;
        int end = pos;
        while (end < _data.Length && _data[end] != 0) end++;
        return _enc.GetString(_data, pos, end - pos);
    }

    private string StrAbs(int pos)
    {
        int end = pos;
        while (end < _data.Length && _data[end] != 0) end++;
        return _enc.GetString(_data, pos, end - pos);
    }

    public XLinkFile Read(byte[] data)
    {
        _data = data;
        var file = new XLinkFile();

        if (U32(0) != 0x4B4E4C58)
            throw new InvalidDataException("Invalid XLNK magic");

        file.Version = U32(8);

        int numResAssetParam     = S32(16);
        int numTrigOverwrite     = S32(20);
        int trigOverwritePos     = (int)U32(24);
        int localPropPos         = (int)U32(28);
        int numLocalProp         = S32(32);
        int numLocalEnum         = S32(36);
        int numDirect            = S32(40);
        int numRandom            = S32(44);
        int numCurve             = S32(48);
        int numCurvePoint        = S32(52);
        int exRegionPos          = (int)U32(56);
        int numUser              = S32(60);
        int condTablePos         = (int)U32(64);
        int nameTablePos         = (int)U32(68);

        int userHashStart = 72;
        int userOffStart  = userHashStart + numUser * 4;
        int pdtStart      = Align4(userOffStart + numUser * 4);

        file.UserHashes = new List<uint>(numUser);
        var userOffsets = new List<int>(numUser);
        for (int i = 0; i < numUser; i++)
        {
            file.UserHashes.Add(U32(userHashStart + i * 4));
            userOffsets.Add(S32(userOffStart + i * 4));
        }

        ReadNameTable(file, nameTablePos, data.Length);
        ReadPDT(file, pdtStart, nameTablePos);
        int numAssetDefs   = file.AssetParamDefs.Count;
        int numTriggerDefs = file.TriggerParamDefs.Count;

        int assetParamStart = Align4(pdtStart + S32(pdtStart));

        var assetParamOffMap = ReadAssetParams(file, assetParamStart, numResAssetParam, numAssetDefs);
        var trigParamOffMap  = ReadTriggerOverwriteParams(file, trigOverwritePos, numTrigOverwrite, numTriggerDefs);

        for (int i = 0; i < numLocalProp; i++)
            file.LocalProperties.Add(Str(nameTablePos, U32(localPropPos + i * 4)));

        int localEnumStart = localPropPos + numLocalProp * 4;
        for (int i = 0; i < numLocalEnum; i++)
            file.LocalPropertyEnumValues.Add(Str(nameTablePos, U32(localEnumStart + i * 4)));

        int directStart = localEnumStart + numLocalEnum * 4;
        for (int i = 0; i < numDirect; i++)
            file.DirectValues.Add(new DirectValue { Raw = U32(directStart + i * 4) });

        int randomStart = directStart + numDirect * 4;
        for (int i = 0; i < numRandom; i++)
            file.RandomTable.Add(new RandomCall { Min = F32(randomStart + i * 8), Max = F32(randomStart + i * 8 + 4) });

        int curveStart = randomStart + numRandom * 8;
        int curvePointStart = curveStart + numCurve * 20;
        for (int i = 0; i < numCurve; i++)
        {
            int o = curveStart + i * 20;
            var cc = new CurveCall
            {
                CurvePointBaseIdx = U16(o),
                NumPoints = U16(o + 2),
                CurveType = U16(o + 4),
                IsGlobal = U16(o + 6),
                PropName = Str(nameTablePos, U32(o + 8)),
                PropIdx = U32(o + 12),
                PropertyIndex = S16(o + 16),
                Padding = U16(o + 18),
                Points = new List<CurvePoint>(),
            };
            for (int j = 0; j < cc.NumPoints; j++)
            {
                int po = curvePointStart + (cc.CurvePointBaseIdx + j) * 8;
                cc.Points.Add(new CurvePoint { X = F32(po), Y = F32(po + 4) });
            }
            file.CurveTable.Add(cc);
        }

        var condIdxMap = BuildCondIdxMap(condTablePos, nameTablePos);
        ReadConditions(file, condTablePos, nameTablePos);

        var arrangeOffsets = new HashSet<uint>();

        for (int i = 0; i < numUser; i++)
            ReadUser(file, userOffsets[i], nameTablePos, file.UserParamDefs.Count,
                numAssetDefs, assetParamOffMap, trigParamOffMap, condIdxMap, arrangeOffsets);

        ResolveArrangeGroups(file, exRegionPos, nameTablePos, arrangeOffsets);
        ResolveStringParams(file, nameTablePos);

        return file;
    }

    private void ReadNameTable(XLinkFile file, int nameTablePos, int fileSize)
    {
        int pos = nameTablePos;
        do
        {
            string s = StrAbs(pos);
            file.StringTable.Add(s);
            pos += _enc.GetByteCount(s) + 1;
        } while (pos < fileSize && pos < _data.Length && _data[pos] != 0);
    }

    private void ReadPDT(XLinkFile file, int pdtStart, int nameTablePos)
    {
        int pdtSize   = S32(pdtStart);
        int numUser   = S32(pdtStart + 4);
        int numAsset  = S32(pdtStart + 8);
        int numUA     = S32(pdtStart + 12);
        int numTrig   = S32(pdtStart + 16);

        file.SystemUserParamCount = numUser;
        file.SystemAssetParamCount = numAsset;
        file.UserAssetParamCount = numUA;

        int defStart = pdtStart + 20;
        int total = numUser + numAsset + numTrig;
        int strBase = defStart + total * 12;
        int pdtEnd = pdtStart + pdtSize;

        int spos = strBase;
        if (spos < pdtEnd)
        {
            do
            {
                string s = StrAbs(spos);
                file.PdtStringTable.Add(s);
                spos += _enc.GetByteCount(s) + 1;
            } while (spos < pdtEnd && _data[spos] != 0);
        }

        void Read(List<ParamDef> list, int offset, int count)
        {
            for (int i = 0; i < count; i++)
            {
                int o = offset + i * 12;
                uint nameOff = U32(o);
                uint typeRaw = U32(o + 4);
                uint defRaw  = U32(o + 8);
                var def = new ParamDef
                {
                    Name = StrAbs(strBase + (int)nameOff),
                    Type = (PType)typeRaw,
                    RawDefault = defRaw,
                };
                if (def.Type == PType.String)
                    def.StringDefault = StrAbs(strBase + (int)defRaw);
                list.Add(def);
            }
        }

        Read(file.UserParamDefs, defStart, numUser);
        Read(file.AssetParamDefs, defStart + numUser * 12, numAsset);
        Read(file.TriggerParamDefs, defStart + (numUser + numAsset) * 12, numTrig);
    }

    private Dictionary<int, int> ReadAssetParams(XLinkFile file, int start, int count, int numDefs)
    {
        var map = new Dictionary<int, int>();
        int pos = 0;
        for (int i = 0; i < count; i++)
        {
            map[pos] = i;
            ulong bits = BinaryPrimitives.ReadUInt64LittleEndian(_data.AsSpan(start + pos));
            int n = System.Numerics.BitOperations.PopCount(bits);
            pos += 8;
            var ps = new ParamSet { Bitfield = bits, Params = new List<Param>(n) };
            int pi = 0;
            for (int j = 0; j < numDefs && pi < n; j++)
            {
                if ((bits >> j & 1) == 1)
                {
                    ps.Params.Add(ReadParam(start + pos + pi * 4, j));
                    pi++;
                }
            }
            pos += n * 4;
            file.AssetParams.Add(ps);
        }
        return map;
    }

    private Dictionary<int, int> ReadTriggerOverwriteParams(XLinkFile file, int start, int count, int numDefs)
    {
        var map = new Dictionary<int, int>();
        int pos = 0;
        for (int i = 0; i < count; i++)
        {
            map[pos] = i;
            uint bits = U32(start + pos);
            int n = System.Numerics.BitOperations.PopCount(bits);
            pos += 4;
            var ps = new TriggerParamSet { Bitfield = bits, Params = new List<Param>(n) };
            int pi = 0;
            for (int j = 0; j < numDefs && pi < n; j++)
            {
                if ((bits >> j & 1) == 1)
                {
                    ps.Params.Add(ReadParam(start + pos + pi * 4, j));
                    pi++;
                }
            }
            pos += n * 4;
            file.TriggerOverwriteParams.Add(ps);
        }
        return map;
    }

    private Param ReadParam(int off, int index)
    {
        uint raw = U32(off);
        return new Param { Index = index, Type = (RefType)(raw >> 24), Value = raw & 0xFFFFFF };
    }

    private void ReadConditions(XLinkFile file, int condStart, int nameTableEnd)
    {
        int pos = condStart;
        while (pos < nameTableEnd)
        {
            file.Conditions.Add(ReadOneCondition(pos, nameTableEnd, out pos));
        }
    }

    private Condition ReadOneCondition(int pos, int nameTableBase, out int next)
    {
        var c = new Condition { ParentType = (CtnType)U32(pos) };
        switch (c.ParentType)
        {
            case CtnType.Switch:
                c.SwitchPropType = (PropType)U32(pos + 4);
                c.SwitchCompareType = (CmpType)U32(pos + 8);
                c.SwitchValue = U32(pos + 12);
                c.SwitchEnumValue = S16(pos + 16);
                c.SwitchIsGlobal = U8(pos + 19) != 0;
                if (c.SwitchPropType == PropType.Enum)
                    c.SwitchEnumName = Str(nameTableBase, c.SwitchValue);
                next = pos + 20;
                break;
            case CtnType.Random:
            case CtnType.Random2:
                c.RandomWeight = F32(pos + 4);
                next = pos + 8;
                break;
            case CtnType.Blend:
                c.BlendMin = F32(pos + 4);
                c.BlendMax = F32(pos + 8);
                c.BlendTypeToMax = U8(pos + 12);
                c.BlendTypeToMin = U8(pos + 13);
                next = pos + 16;
                break;
            case CtnType.Sequence:
                c.SequenceContinueOnFade = S32(pos + 4);
                next = pos + 8;
                break;
            default:
                next = pos + 8;
                break;
        }
        return c;
    }

    private Dictionary<int, int> BuildCondIdxMap(int condStart, int nameTableEnd)
    {
        var map = new Dictionary<int, int>();
        int pos = condStart;
        int idx = 0;
        while (pos < nameTableEnd)
        {
            map[pos - condStart] = idx++;
            ReadOneCondition(pos, nameTableEnd, out pos);
        }
        return map;
    }

    private void ReadUser(XLinkFile file, int off, int ntBase, int numUserParams,
        int numAssetDefs, Dictionary<int, int> apMap,
        Dictionary<int, int> tpMap, Dictionary<int, int> condMap,
        HashSet<uint> arrangeOffsets)
    {
        var u = new UserData { Hash = file.UserHashes[file.Users.Count] };

        int numLocalProp   = S32(off + 4);
        int numCall        = S32(off + 8);
        int numAsset       = S32(off + 12);
        int numActionSlot  = S32(off + 20);
        int numAction      = S32(off + 24);
        int numActionTrig  = S32(off + 28);
        int numProp        = S32(off + 32);
        int numPropTrig    = S32(off + 36);
        int numAlwaysTrig  = S32(off + 40);
        uint trigTableOff  = U32(off + 44);

        int pos = off + 48;

        for (int i = 0; i < numLocalProp; i++)
        {
            u.LocalProperties.Add(Str(ntBase, U32(pos)));
            pos += 4;
        }

        for (int i = 0; i < numUserParams; i++)
        {
            var p = ReadParam(pos, i);
            if (p.Type == RefType.ArrangeParam) arrangeOffsets.Add(p.Value);
            u.UserParams.Add(p);
            pos += 4;
        }

        pos += numCall * 2;
        if (numCall % 2 != 0) pos += 2;

        int actBase = pos;
        const int ACT_SIZE = 32;
        for (int i = 0; i < numCall; i++)
        {
            int a = actBase + i * ACT_SIZE;
            var act = new AssetCall
            {
                KeyName = Str(ntBase, U32(a)),
                AssetIndex = S16(a + 4),
                Flag = U16(a + 6),
                Duration = S32(a + 8),
                ParentIndex = S32(a + 12),
                Guid = U32(a + 16),
                KeyNameHash = U32(a + 20),
                AssetParamIdx = -1,
                ContainerParamIdx = -1,
                ConditionIdx = -1,
            };

            uint paramOff = U32(a + 24);
            if (act.IsContainer)
                act.ContainerParamIdx = (int)paramOff;
            else if (apMap.TryGetValue((int)paramOff, out int ai))
                act.AssetParamIdx = ai;

            int condOff = S32(a + 28);
            if (condOff != -1 && condMap.TryGetValue(condOff, out int ci))
                act.ConditionIdx = ci;

            u.AssetCalls.Add(act);
        }

        int containerStart = actBase + numCall * ACT_SIZE;
        int trigStart = off + (int)trigTableOff;
        ReadContainers(u, containerStart, trigStart, ntBase);

        pos = trigStart;
        for (int i = 0; i < numActionSlot; i++)
        {
            u.ActionSlots.Add(new ActionSlot
            {
                Name = Str(ntBase, U32(pos)),
                ActionStartIdx = U16(pos + 4),
                ActionEndIdx = U16(pos + 6),
            });
            pos += 8;
        }

        for (int i = 0; i < numAction; i++)
        {
            u.Actions.Add(new Action
            {
                Name = Str(ntBase, U32(pos)),
                TriggerStartIdx = U32(pos + 4),
                TriggerEndIdx = U32(pos + 8),
            });
            pos += 12;
        }

        for (int i = 0; i < numActionTrig; i++)
        {
            ushort flag = U16(pos + 16);
            bool nameMatch = (flag & 0x10) != 0;
            int owOff = S32(pos + 20);
            int owIdx = -1;
            if (owOff != -1 && tpMap.TryGetValue(owOff, out int ti))
                owIdx = ti;

            var at = new ActionTrigger
            {
                Guid = U32(pos),
                AssetCallIdx = (int)(U32(pos + 4) / ACT_SIZE),
                StartFrame = U32(pos + 8),
                EndFrame = S32(pos + 12),
                Flag = flag,
                OverwriteHash = U16(pos + 18),
                TriggerOverwriteIdx = owIdx,
            };
            if (nameMatch)
                at.PreviousActionName = Str(ntBase, at.StartFrame);
            u.ActionTriggers.Add(at);
            pos += 24;
        }

        for (int i = 0; i < numProp; i++)
        {
            u.Properties.Add(new Property
            {
                Name = Str(ntBase, U32(pos)),
                IsGlobal = U32(pos + 4),
                TriggerStartIdx = U32(pos + 8),
                TriggerEndIdx = U32(pos + 12),
            });
            pos += 16;
        }

        for (int i = 0; i < numPropTrig; i++)
        {
            int ptCondOff = S32(pos + 8);
            int ptCondIdx = -1;
            if (ptCondOff != -1 && condMap.TryGetValue(ptCondOff, out int ci2))
                ptCondIdx = ci2;
            int ptOwOff = S32(pos + 16);
            int ptOwIdx = -1;
            if (ptOwOff != -1 && tpMap.TryGetValue(ptOwOff, out int ti2))
                ptOwIdx = ti2;

            u.PropertyTriggers.Add(new PropertyTrigger
            {
                Guid = U32(pos),
                AssetCallIdx = (int)(U32(pos + 4) / ACT_SIZE),
                ConditionIdx = ptCondIdx,
                Flag = U16(pos + 12),
                OverwriteHash = U16(pos + 14),
                TriggerOverwriteIdx = ptOwIdx,
            });
            pos += 20;
        }

        for (int i = 0; i < numAlwaysTrig; i++)
        {
            int atOwOff = S32(pos + 12);
            int atOwIdx = -1;
            if (atOwOff != -1 && tpMap.TryGetValue(atOwOff, out int ti3))
                atOwIdx = ti3;

            u.AlwaysTriggers.Add(new AlwaysTrigger
            {
                Guid = U32(pos),
                Flag = U16(pos + 4),
                OverwriteHash = U16(pos + 6),
                AssetCallIdx = (int)(U32(pos + 8) / ACT_SIZE),
                TriggerOverwriteIdx = atOwIdx,
            });
            pos += 16;
        }

        file.Users.Add(u);
    }

    private void ReadContainers(UserData user, int start, int end, int ntBase)
    {
        var offMap = new Dictionary<int, int>();
        int pos = start;
        int idx = 0;
        while (pos < end)
        {
            offMap[pos - start] = idx;
            var type = (CtnType)U32(pos);
            var c = new Container
            {
                Type = type,
                ChildStartIdx = S32(pos + 4),
                ChildEndIdx = S32(pos + 8),
            };

            if (type == CtnType.Switch)
            {
                c.SwitchPropName = Str(ntBase, U32(pos + 12));
                c.SwitchWatchPropertyId = S32(pos + 16);
                c.SwitchPropertyIndex = S16(pos + 20);
                c.SwitchIsGlobal = U8(pos + 22) != 0;
                pos += 24;
            }
            else
            {
                pos += 12;
            }

            user.Containers.Add(c);
            idx++;
        }

        for (int i = 0; i < user.AssetCalls.Count; i++)
        {
            var act = user.AssetCalls[i];
            if (act.IsContainer && act.ContainerParamIdx >= 0 &&
                offMap.TryGetValue(act.ContainerParamIdx, out int ci))
            {
                act.ContainerParamIdx = ci;
                user.AssetCalls[i] = act;
            }
        }
    }

    private void ResolveArrangeGroups(XLinkFile file, int exRegion, int ntBase, HashSet<uint> arrangeOffsets)
    {
        var sorted = new List<uint>(arrangeOffsets);
        sorted.Sort();
        var idxMap = new Dictionary<uint, int>();
        for (int i = 0; i < sorted.Count; i++)
        {
            idxMap[sorted[i]] = i;
            int pos = exRegion + (int)sorted[i];
            uint count = U32(pos);
            pos += 4;
            var g = new ArrangeGroup { Entries = new List<ArrangeGroupEntry>((int)count) };
            for (uint j = 0; j < count; j++)
            {
                g.Entries.Add(new ArrangeGroupEntry
                {
                    GroupName = Str(ntBase, U32(pos)),
                    LimitType = U32(pos + 4),
                    LimitThreshold = F32(pos + 8),
                    Unk = U32(pos + 12),
                });
                pos += 16;
            }
            file.ArrangeGroups.Add(g);
        }

        void Fix(List<Param> parms)
        {
            for (int i = 0; i < parms.Count; i++)
            {
                var p = parms[i];
                if (p.Type == RefType.ArrangeParam && idxMap.TryGetValue(p.Value, out int idx))
                    parms[i] = p with { Value = (uint)idx };
            }
        }

        foreach (var u in file.Users) Fix(u.UserParams);
        foreach (var ap in file.AssetParams) Fix(ap.Params);
        foreach (var tp in file.TriggerOverwriteParams) Fix(tp.Params);
    }

    private void ResolveStringParams(XLinkFile file, int ntBase)
    {
        void Resolve(List<Param> parms)
        {
            for (int i = 0; i < parms.Count; i++)
            {
                var p = parms[i];
                if (p.Type == RefType.String)
                    parms[i] = p with { StringValue = Str(ntBase, p.Value) };
            }
        }

        foreach (var u in file.Users) Resolve(u.UserParams);
        foreach (var ap in file.AssetParams) Resolve(ap.Params);
        foreach (var tp in file.TriggerOverwriteParams) Resolve(tp.Params);
    }

    private static int Align4(int v) => (v + 3) & ~3;
}
