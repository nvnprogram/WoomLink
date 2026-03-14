using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace WoomLink.Converter;

public class XLinkBinaryWriter
{
    private static readonly Encoding _enc = Encoding.Latin1;

    private List<byte> _buf = null!;
    private Dictionary<string, int> _strOff = null!;
    private Dictionary<string, int> _pdtStrOff = null!;

    public byte[] Write(XLinkFile file)
    {
        _buf = new List<byte>();

        BuildStringTable(file);
        BuildPdtStringTable(file);
        SortUsersByHash(file);

        int headerSize = 72;
        int numUser = file.Users.Count;

        _buf.AddRange(new byte[headerSize]);

        foreach (var h in file.UserHashes) W32(h);
        int userOffArrayPos = _buf.Count;
        for (int i = 0; i < numUser; i++) W32(0);

        WritePDT(file);
        Pad4();

        int apStart = _buf.Count;
        var apOff = WriteAssetParams(file);

        int tpStart = _buf.Count;
        var tpOff = WriteTriggerOverwriteParams(file);

        int lpStart = _buf.Count;
        foreach (var lp in file.LocalProperties)       W32((uint)SO(lp));
        foreach (var le in file.LocalPropertyEnumValues) W32((uint)SO(le));
        foreach (var dv in file.DirectValues)            W32(dv.Raw);
        foreach (var r in file.RandomTable) { WF(r.Min); WF(r.Max); }
        WriteCurves(file);

        int exStart = _buf.Count;
        var agOff = WriteArrangeGroups(file);

        var userStarts = new int[numUser];
        for (int i = 0; i < numUser; i++)
        {
            userStarts[i] = _buf.Count;
            WriteUser(file, file.Users[i], apOff, tpOff, agOff);
        }

        int condStart = _buf.Count;
        var condOff = WriteConditions(file);

        FixupUserCondAndACTRefs(file, userStarts, condStart, condOff);

        int ntStart = _buf.Count;
        WriteNameTable(file);

        int fileSize = Align4(_buf.Count);
        while (_buf.Count < fileSize) _buf.Add(0);

        var span = System.Runtime.InteropServices.CollectionsMarshal.AsSpan(_buf);

        for (int i = 0; i < numUser; i++)
            BinaryPrimitives.WriteInt32LittleEndian(span[(userOffArrayPos + i * 4)..], userStarts[i]);

        int numParams = file.AssetParams.Sum(ap => ap.Params.Count);

        BinaryPrimitives.WriteUInt32LittleEndian(span[0..], 0x4B4E4C58);
        BinaryPrimitives.WriteUInt32LittleEndian(span[4..], (uint)fileSize);
        BinaryPrimitives.WriteUInt32LittleEndian(span[8..], file.Version);
        BinaryPrimitives.WriteInt32LittleEndian(span[12..], numParams);
        BinaryPrimitives.WriteInt32LittleEndian(span[16..], file.AssetParams.Count);
        BinaryPrimitives.WriteInt32LittleEndian(span[20..], file.TriggerOverwriteParams.Count);
        BinaryPrimitives.WriteUInt32LittleEndian(span[24..], (uint)tpStart);
        BinaryPrimitives.WriteUInt32LittleEndian(span[28..], (uint)lpStart);
        BinaryPrimitives.WriteInt32LittleEndian(span[32..], file.LocalProperties.Count);
        BinaryPrimitives.WriteInt32LittleEndian(span[36..], file.LocalPropertyEnumValues.Count);
        BinaryPrimitives.WriteInt32LittleEndian(span[40..], file.DirectValues.Count);
        BinaryPrimitives.WriteInt32LittleEndian(span[44..], file.RandomTable.Count);
        BinaryPrimitives.WriteInt32LittleEndian(span[48..], file.CurveTable.Count);
        BinaryPrimitives.WriteInt32LittleEndian(span[52..], file.CurveTable.Sum(c => c.Points.Count));
        BinaryPrimitives.WriteUInt32LittleEndian(span[56..], (uint)exStart);
        BinaryPrimitives.WriteInt32LittleEndian(span[60..], numUser);
        BinaryPrimitives.WriteUInt32LittleEndian(span[64..], (uint)condStart);
        BinaryPrimitives.WriteUInt32LittleEndian(span[68..], (uint)ntStart);

        return _buf.ToArray();
    }

    private static void SortUsersByHash(XLinkFile file)
    {
        if (file.Users.Count != file.UserHashes.Count) return;
        var indices = Enumerable.Range(0, file.Users.Count)
            .OrderBy(i => file.UserHashes[i])
            .ToArray();

        bool isSorted = true;
        for (int i = 0; i < indices.Length; i++)
        {
            if (indices[i] != i) { isSorted = false; break; }
        }
        if (isSorted) return;

        var newHashes = indices.Select(i => file.UserHashes[i]).ToList();
        var newUsers = indices.Select(i => file.Users[i]).ToList();
        file.UserHashes.Clear();
        file.UserHashes.AddRange(newHashes);
        file.Users.Clear();
        file.Users.AddRange(newUsers);
    }

    private void BuildStringTable(XLinkFile file)
    {
        _strOff = new Dictionary<string, int>();

        if (file.StringTable.Count > 0)
        {
            int off = 0;
            foreach (var s in file.StringTable)
            {
                _strOff.TryAdd(s, off);
                off += _enc.GetByteCount(s) + 1;
            }
        }
        else
        {
            var strList = new List<string>();
            strList.Add("");
            _strOff[""] = 0;
            int curOff = 1;
            void A(string? s)
            {
                if (s == null || _strOff.ContainsKey(s)) return;
                _strOff[s] = curOff;
                strList.Add(s);
                curOff += _enc.GetByteCount(s) + 1;
            }
            foreach (var lp in file.LocalProperties) A(lp);
            foreach (var le in file.LocalPropertyEnumValues) A(le);
            foreach (var c in file.CurveTable) A(c.PropName);
            foreach (var ag in file.ArrangeGroups) foreach (var e in ag.Entries) A(e.GroupName);
            foreach (var c in file.Conditions) if (c.SwitchEnumName != null) A(c.SwitchEnumName);
            foreach (var u in file.Users)
            {
                foreach (var lp in u.LocalProperties) A(lp);
                foreach (var p in u.UserParams) if (p.StringValue != null) A(p.StringValue);
                foreach (var c in u.Containers) if (c.SwitchPropName != null) A(c.SwitchPropName);
                foreach (var act in u.AssetCalls) A(act.KeyName);
                foreach (var s in u.ActionSlots) A(s.Name);
                foreach (var a in u.Actions) A(a.Name);
                foreach (var t in u.ActionTriggers) if (t.PreviousActionName != null) A(t.PreviousActionName);
                foreach (var p in u.Properties) A(p.Name);
            }
            foreach (var ap in file.AssetParams) foreach (var p in ap.Params) if (p.StringValue != null) A(p.StringValue);
            foreach (var tp in file.TriggerOverwriteParams) foreach (var p in tp.Params) if (p.StringValue != null) A(p.StringValue);
            file.StringTable = strList;
        }
    }

    private void BuildPdtStringTable(XLinkFile file)
    {
        _pdtStrOff = new Dictionary<string, int>();

        if (file.PdtStringTable.Count > 0)
        {
            int off = 0;
            foreach (var s in file.PdtStringTable)
            {
                _pdtStrOff.TryAdd(s, off);
                off += _enc.GetByteCount(s) + 1;
            }
        }
        else
        {
            var pdtNames = new List<string>();
            int pdtNP = 0;
            void PNO(string s)
            {
                if (_pdtStrOff.ContainsKey(s)) return;
                _pdtStrOff[s] = pdtNP;
                pdtNames.Add(s);
                pdtNP += _enc.GetByteCount(s) + 1;
            }
            foreach (var d in file.UserParamDefs) { PNO(d.Name); if (d.Type == PType.String && d.StringDefault != null) PNO(d.StringDefault); }
            foreach (var d in file.AssetParamDefs) { PNO(d.Name); if (d.Type == PType.String && d.StringDefault != null) PNO(d.StringDefault); }
            foreach (var d in file.TriggerParamDefs) { PNO(d.Name); if (d.Type == PType.String && d.StringDefault != null) PNO(d.StringDefault); }
            file.PdtStringTable = pdtNames;
        }
    }

    private void WriteNameTable(XLinkFile file)
    {
        foreach (var s in file.StringTable)
        {
            _buf.AddRange(_enc.GetBytes(s));
            _buf.Add(0);
        }
    }

    private void WritePDT(XLinkFile file)
    {
        int pdtBase = _buf.Count;

        int total = file.UserParamDefs.Count + file.AssetParamDefs.Count + file.TriggerParamDefs.Count;
        int pdtNameTableSize = 0;
        foreach (var s in file.PdtStringTable)
            pdtNameTableSize += _enc.GetByteCount(s) + 1;
        int pdtSize = Align4(20 + total * 12 + pdtNameTableSize + 1);

        W32((uint)pdtSize);
        W32((uint)file.UserParamDefs.Count);
        W32((uint)file.AssetParamDefs.Count);
        W32((uint)file.UserAssetParamCount);
        W32((uint)file.TriggerParamDefs.Count);

        void WriteDefs(List<ParamDef> defs)
        {
            foreach (var d in defs)
            {
                W32((uint)PSO(d.Name));
                W32((uint)d.Type);
                W32(d.Type == PType.String ? (uint)PSO(d.StringDefault ?? "") : d.RawDefault);
            }
        }
        WriteDefs(file.UserParamDefs);
        WriteDefs(file.AssetParamDefs);
        WriteDefs(file.TriggerParamDefs);

        foreach (var s in file.PdtStringTable)
        {
            _buf.AddRange(_enc.GetBytes(s));
            _buf.Add(0);
        }

        while (_buf.Count < pdtBase + pdtSize)
            _buf.Add(0);
    }

    private List<int> WriteAssetParams(XLinkFile file)
    {
        var off = new List<int>();
        int base_ = _buf.Count;
        foreach (var ap in file.AssetParams)
        {
            off.Add(_buf.Count - base_);
            W64(ap.Bitfield);
            foreach (var p in ap.Params) WP(p);
        }
        return off;
    }

    private List<int> WriteTriggerOverwriteParams(XLinkFile file)
    {
        var off = new List<int>();
        int base_ = _buf.Count;
        foreach (var tp in file.TriggerOverwriteParams)
        {
            off.Add(_buf.Count - base_);
            W32(tp.Bitfield);
            foreach (var p in tp.Params) WP(p);
        }
        return off;
    }

    private void WriteCurves(XLinkFile file)
    {
        ushort baseIdx = 0;
        foreach (var c in file.CurveTable)
        {
            W16(baseIdx);
            ushort numPts = (ushort)c.Points.Count;
            W16(numPts);
            W16(c.CurveType);
            W16(c.IsGlobal);
            W32((uint)SO(c.PropName));
            W32(c.PropIdx);
            W16S(c.PropertyIndex);
            W16(c.Padding);
            baseIdx += numPts;
        }
        foreach (var c in file.CurveTable)
            foreach (var p in c.Points) { WF(p.X); WF(p.Y); }
    }

    private List<int> WriteArrangeGroups(XLinkFile file)
    {
        var off = new List<int>();
        int base_ = _buf.Count;
        foreach (var ag in file.ArrangeGroups)
        {
            off.Add(_buf.Count - base_);
            W32((uint)ag.Entries.Count);
            foreach (var e in ag.Entries)
            {
                W32((uint)SO(e.GroupName));
                W32(e.LimitType);
                WF(e.LimitThreshold);
                W32(e.Unk);
            }
        }
        return off;
    }

    private void WriteUser(XLinkFile file, UserData u,
        List<int> apOff, List<int> tpOff, List<int> agOff)
    {
        int userBase = _buf.Count;
        int numCall = u.AssetCalls.Count;
        int numAsset = u.AssetCalls.Count(a => !a.IsContainer);

        W32(0);
        WS(u.LocalProperties.Count);
        WS(numCall);
        WS(numAsset);
        WS(u.AssetCalls.Count(a => a.IsContainer && a.ContainerParamIdx >= 0 &&
            a.ContainerParamIdx < u.Containers.Count &&
            u.Containers[a.ContainerParamIdx].Type is CtnType.Random or CtnType.Random2));
        WS(u.ActionSlots.Count);
        WS(u.Actions.Count);
        WS(u.ActionTriggers.Count);
        WS(u.Properties.Count);
        WS(u.PropertyTriggers.Count);
        WS(u.AlwaysTriggers.Count);
        int trigOffFieldPos = _buf.Count;
        W32(0);

        foreach (var lp in u.LocalProperties) W32((uint)SO(lp));

        foreach (var p in u.UserParams)
        {
            if (p.Type == RefType.ArrangeParam && (int)p.Value < agOff.Count)
                W32(((uint)p.Type << 24) | ((uint)agOff[(int)p.Value] & 0xFFFFFF));
            else
                WP(p);
        }

        var sortedIds = Enumerable.Range(0, numCall)
            .OrderBy(i => u.AssetCalls[i].KeyName, StringComparer.Ordinal)
            .ToArray();
        foreach (var i in sortedIds) W16((ushort)i);
        if (numCall % 2 != 0) W16(0);

        var ctnOff = new List<int>();
        { int off = 0; foreach (var c in u.Containers) { ctnOff.Add(off); off += c.Type == CtnType.Switch ? 24 : 12; } }

        foreach (var act in u.AssetCalls)
        {
            W32((uint)SO(act.KeyName));
            W16S(act.AssetIndex);
            W16(act.Flag);
            WS(act.Duration);
            WS(act.ParentIndex);
            W32(act.Guid);
            W32(act.KeyNameHash);

            if (act.IsContainer)
            {
                int co = (act.ContainerParamIdx >= 0 && act.ContainerParamIdx < ctnOff.Count)
                    ? ctnOff[act.ContainerParamIdx] : -1;
                WS(co);
            }
            else
            {
                int ao = (act.AssetParamIdx >= 0 && act.AssetParamIdx < apOff.Count)
                    ? apOff[act.AssetParamIdx] : 0;
                WS(ao);
            }

            WS(-1);
        }

        foreach (var c in u.Containers)
        {
            W32((uint)c.Type);
            WS(c.ChildStartIdx);
            WS(c.ChildEndIdx);
            if (c.Type == CtnType.Switch)
            {
                W32((uint)SO(c.SwitchPropName ?? ""));
                WS(c.SwitchWatchPropertyId);
                W16S((short)c.SwitchPropertyIndex);
                _buf.Add(c.SwitchIsGlobal ? (byte)1 : (byte)0);
                _buf.Add(0);
            }
        }

        int trigStart = _buf.Count;
        BinaryPrimitives.WriteUInt32LittleEndian(
            System.Runtime.InteropServices.CollectionsMarshal.AsSpan(_buf).Slice(trigOffFieldPos),
            (uint)(trigStart - userBase));

        foreach (var s in u.ActionSlots)
        {
            W32((uint)SO(s.Name));
            W16(s.ActionStartIdx);
            W16(s.ActionEndIdx);
        }

        foreach (var a in u.Actions)
        {
            W32((uint)SO(a.Name));
            W32(a.TriggerStartIdx);
            W32(a.TriggerEndIdx);
        }

        foreach (var t in u.ActionTriggers)
        {
            W32(t.Guid);
            W32((uint)(t.AssetCallIdx * 32));
            W32(t.IsNameMatch ? (uint)SO(t.PreviousActionName ?? "") : t.StartFrame);
            WS(t.EndFrame);
            W16(t.Flag);
            W16(t.OverwriteHash);
            WS(t.TriggerOverwriteIdx >= 0 && t.TriggerOverwriteIdx < tpOff.Count
                ? tpOff[t.TriggerOverwriteIdx] : -1);
        }

        foreach (var p in u.Properties)
        {
            W32((uint)SO(p.Name));
            W32(p.IsGlobal);
            W32(p.TriggerStartIdx);
            W32(p.TriggerEndIdx);
        }

        foreach (var t in u.PropertyTriggers)
        {
            W32(t.Guid);
            W32((uint)(t.AssetCallIdx * 32));
            WS(-1);
            W16(t.Flag);
            W16(t.OverwriteHash);
            WS(t.TriggerOverwriteIdx >= 0 && t.TriggerOverwriteIdx < tpOff.Count
                ? tpOff[t.TriggerOverwriteIdx] : -1);
        }

        foreach (var t in u.AlwaysTriggers)
        {
            W32(t.Guid);
            W32((uint)(t.AssetCallIdx * 32));
            W16(t.Flag);
            W16(t.OverwriteHash);
            WS(t.TriggerOverwriteIdx >= 0 && t.TriggerOverwriteIdx < tpOff.Count
                ? tpOff[t.TriggerOverwriteIdx] : -1);
        }
    }

    private void FixupUserCondAndACTRefs(XLinkFile file, int[] userStarts,
        int condStart, List<int> condOff)
    {
        var span = System.Runtime.InteropServices.CollectionsMarshal.AsSpan(_buf);
        for (int ui = 0; ui < file.Users.Count; ui++)
        {
            var u = file.Users[ui];
            int uBase = userStarts[ui];
            int numCall = u.AssetCalls.Count;
            int numUserParams = file.UserParamDefs.Count;

            int pos = uBase + 48 + u.LocalProperties.Count * 4 + numUserParams * 4;
            pos += numCall * 2;
            if (numCall % 2 != 0) pos += 2;

            int actFieldBase = pos;
            for (int i = 0; i < numCall; i++)
            {
                int condField = actFieldBase + i * 32 + 28;
                int ci = u.AssetCalls[i].ConditionIdx;
                int val = (ci >= 0 && ci < condOff.Count) ? condOff[ci] : -1;
                BinaryPrimitives.WriteInt32LittleEndian(span[condField..], val);
            }

            int trigTablePos = uBase + (int)BinaryPrimitives.ReadUInt32LittleEndian(span[uBase..].Slice(44));
            int ptOff = trigTablePos
                + u.ActionSlots.Count * 8
                + u.Actions.Count * 12
                + u.ActionTriggers.Count * 24
                + u.Properties.Count * 16;

            for (int i = 0; i < u.PropertyTriggers.Count; i++)
            {
                int condField = ptOff + i * 20 + 8;
                int ci = u.PropertyTriggers[i].ConditionIdx;
                int val = (ci >= 0 && ci < condOff.Count) ? condOff[ci] : -1;
                BinaryPrimitives.WriteInt32LittleEndian(span[condField..], val);
            }
        }
    }

    private List<int> WriteConditions(XLinkFile file)
    {
        var off = new List<int>();
        int base_ = _buf.Count;
        foreach (var c in file.Conditions)
        {
            off.Add(_buf.Count - base_);
            W32((uint)c.ParentType);
            switch (c.ParentType)
            {
                case CtnType.Switch:
                    W32((uint)c.SwitchPropType);
                    W32((uint)c.SwitchCompareType);
                    W32(c.SwitchPropType == PropType.Enum && c.SwitchEnumName != null
                        ? (uint)SO(c.SwitchEnumName) : c.SwitchValue);
                    W16S(c.SwitchEnumValue);
                    _buf.Add(0);
                    _buf.Add(c.SwitchIsGlobal ? (byte)1 : (byte)0);
                    break;
                case CtnType.Random:
                case CtnType.Random2:
                    WF(c.RandomWeight);
                    break;
                case CtnType.Blend:
                    WF(c.BlendMin);
                    WF(c.BlendMax);
                    _buf.Add(c.BlendTypeToMax);
                    _buf.Add(c.BlendTypeToMin);
                    _buf.Add(0); _buf.Add(0);
                    break;
                case CtnType.Sequence:
                    WS(c.SequenceContinueOnFade);
                    break;
                default:
                    WS(0);
                    break;
            }
        }
        return off;
    }

    private int SO(string s) => _strOff.TryGetValue(s, out int o) ? o : 0;
    private int PSO(string s) => _pdtStrOff.TryGetValue(s, out int o) ? o : 0;

    private void WP(Param p)
    {
        uint v = p.Type == RefType.String ? (uint)SO(p.StringValue ?? "") : p.Value;
        W32(((uint)p.Type << 24) | (v & 0xFFFFFF));
    }

    private void W32(uint v) { Span<byte> b = stackalloc byte[4]; BinaryPrimitives.WriteUInt32LittleEndian(b, v); _buf.AddRange(b.ToArray()); }
    private void WS(int v) => W32((uint)v);
    private void W16(ushort v) { Span<byte> b = stackalloc byte[2]; BinaryPrimitives.WriteUInt16LittleEndian(b, v); _buf.AddRange(b.ToArray()); }
    private void W16S(short v) => W16((ushort)v);
    private void WF(float v) => WS(BitConverter.SingleToInt32Bits(v));
    private void W64(ulong v) { Span<byte> b = stackalloc byte[8]; BinaryPrimitives.WriteUInt64LittleEndian(b, v); _buf.AddRange(b.ToArray()); }
    private void Pad4() { while (_buf.Count % 4 != 0) _buf.Add(0); }
    private static int Align4(int v) => (v + 3) & ~3;
}
