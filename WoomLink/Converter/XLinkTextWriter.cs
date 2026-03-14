using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;

namespace WoomLink.Converter;

public class XLinkTextWriter
{
    private readonly TextWriter _w;
    private readonly Dictionary<uint, string>? _actorNames;
    private XLinkFile _file = null!;
    private int _indent;

    private static readonly HashSet<string> PlainEnumValues = new(StringComparer.OrdinalIgnoreCase)
    {
        "True", "False"
    };

    public XLinkTextWriter(TextWriter writer, Dictionary<uint, string>? actorNames = null)
    {
        _w = writer;
        _actorNames = actorNames;
    }

    public void Write(XLinkFile file)
    {
        _file = file;
        _indent = 0;

        L($"xlink_version {file.Version}");
        L();

        WriteParamDefs("asset_params", file.AssetParamDefs);
        WriteParamDefs("trigger_params", file.TriggerParamDefs);
        WriteParamDefs("user_params", file.UserParamDefs);

        L($"user_asset_param_count {file.UserAssetParamCount}");
        L();

        foreach (var user in file.Users)
            WriteUser(user);
    }

    private void WriteParamDefs(string name, List<ParamDef> defs)
    {
        if (defs.Count == 0) return;
        L($"{name} {{");
        _indent++;
        foreach (var d in defs)
        {
            string defVal = FormatDefaultValue(d);
            if (defVal.Length > 0)
                L($"{Q(d.Name)} : {d.Type} = {defVal}");
            else
                L($"{Q(d.Name)} : {d.Type}");
        }
        _indent--;
        L("}");
        L();
    }

    private static string FormatDefaultValue(ParamDef d)
    {
        if (d.Type == PType.String)
        {
            string s = d.StringDefault ?? "";
            return s.Length == 0 ? "" : Q(s);
        }
        return d.Type switch
        {
            PType.Int => d.RawDefault == 0 ? "" : ((int)d.RawDefault).ToString(),
            PType.Float => d.RawDefault == 0 ? "" : Fl(BitConverter.Int32BitsToSingle((int)d.RawDefault)),
            PType.Bool => d.RawDefault != 0 ? "true" : "",
            PType.Bitfield => d.RawDefault != 0 ? $"0x{d.RawDefault:X8}" : "",
            PType.Enum => d.RawDefault != 0 ? ((int)d.RawDefault).ToString() : "",
            _ => d.RawDefault != 0 ? $"0x{d.RawDefault:X8}" : "",
        };
    }

    private void WriteArrangeBlock(int idx)
    {
        if (idx < 0 || idx >= _file.ArrangeGroups.Count)
        {
            _w.WriteLine($"(Arrange @{idx})");
            return;
        }

        var ag = _file.ArrangeGroups[idx];
        if (ag.Entries.Count == 0)
        {
            _w.WriteLine("arrange {}");
            return;
        }

        _w.WriteLine("arrange {");
        _indent++;
        foreach (var e in ag.Entries)
            L($"entry {Q(e.GroupName)} limit_type={e.LimitType} threshold={Fl(e.LimitThreshold)} unk={e.Unk}");
        _indent--;
        L("}");
    }

    private void WriteUser(UserData user)
    {
        string userName;
        if (_actorNames != null && _actorNames.TryGetValue(user.Hash, out string? resolved))
            userName = Q(resolved);
        else
            userName = $"0x{user.Hash:X8}";

        L($"user {userName} {{");
        _indent++;

        if (user.LocalProperties.Count > 0)
        {
            L("local_properties {");
            _indent++;
            foreach (var lp in user.LocalProperties)
                L(Q(lp));
            _indent--;
            L("}");
        }

        WriteUserParams(user);
        WriteAssetCallTree(user);
        WriteActions(user);
        WriteProperties(user);
        WriteAlwaysTriggers(user);

        _indent--;
        L("}");
        L();
    }

    private void WriteUserParams(UserData user)
    {
        var nonDefault = new List<(Param p, ParamDef def)>();
        for (int i = 0; i < user.UserParams.Count && i < _file.UserParamDefs.Count; i++)
        {
            var p = user.UserParams[i];
            var def = _file.UserParamDefs[i];
            if (!IsDefaultParam(p, def))
                nonDefault.Add((p, def));
        }
        if (nonDefault.Count == 0) return;

        L("user_params {");
        _indent++;
        foreach (var (p, def) in nonDefault)
            WriteParamLine(Q(def.Name), p, def);
        _indent--;
        L("}");
    }

    private HashSet<int> _writtenCalls = null!;
    private Dictionary<int, string> _callSwitchProp = null!;

    private void BuildCallSwitchPropMap(UserData user)
    {
        _callSwitchProp = new Dictionary<int, string>();
        foreach (var ctn in user.Containers)
        {
            if (ctn.Type != CtnType.Switch || ctn.SwitchPropName == null) continue;
            int start = ctn.ChildStartIdx;
            int end = ctn.ChildEndIdx;
            if (start < 0 || end < start) continue;
            for (int i = start; i <= end && i < user.AssetCalls.Count; i++)
            {
                if (user.AssetCalls[i].ConditionIdx >= 0)
                    _callSwitchProp.TryAdd(i, ctn.SwitchPropName);
            }
        }
    }

    private void WriteAssetCallTree(UserData user)
    {
        if (user.AssetCalls.Count == 0) return;

        L();
        L("assets {");
        _indent++;

        _writtenCalls = new HashSet<int>();
        BuildCallSwitchPropMap(user);
        for (int i = 0; i < user.AssetCalls.Count; i++)
        {
            if (user.AssetCalls[i].ParentIndex == -1)
                WriteAssetCall(user, i);
        }

        var trigRefs = new HashSet<int>();
        foreach (var t in user.ActionTriggers)
            if (t.AssetCallIdx >= 0 && t.AssetCallIdx < user.AssetCalls.Count)
                trigRefs.Add(t.AssetCallIdx);
        foreach (var t in user.PropertyTriggers)
            if (t.AssetCallIdx >= 0 && t.AssetCallIdx < user.AssetCalls.Count)
                trigRefs.Add(t.AssetCallIdx);
        foreach (var t in user.AlwaysTriggers)
            if (t.AssetCallIdx >= 0 && t.AssetCallIdx < user.AssetCalls.Count)
                trigRefs.Add(t.AssetCallIdx);

        foreach (var idx in trigRefs.OrderBy(x => x))
        {
            if (!_writtenCalls.Contains(idx))
                WriteAssetCall(user, idx);
        }

        _indent--;
        L("}");
    }

    private void WriteAssetCall(UserData user, int idx, string? inheritedSwitchProp = null)
    {
        _writtenCalls.Add(idx);
        var call = user.AssetCalls[idx];

        if (call.IsContainer && call.ContainerParamIdx >= 0 && call.ContainerParamIdx < user.Containers.Count)
        {
            L($"asset {Q(call.KeyName)} [duration={call.Duration}] {{");
            _indent++;
            WriteContainerBody(user, idx, user.Containers[call.ContainerParamIdx], inheritedSwitchProp);
            _indent--;
            L("}");
        }
        else
        {
            WriteLeafCall(user, idx, call);
        }
    }

    private void WriteLeafCall(UserData user, int idx, AssetCall call)
    {
        int apIdx = call.AssetParamIdx;
        bool hasParams = apIdx >= 0 && apIdx < _file.AssetParams.Count && _file.AssetParams[apIdx].Params.Count > 0;

        var children = new List<int>();
        for (int i = 0; i < user.AssetCalls.Count; i++)
            if (user.AssetCalls[i].ParentIndex == idx)
                children.Add(i);

        if (!hasParams && children.Count == 0)
        {
            L($"asset {Q(call.KeyName)} [duration={call.Duration}] {{}}");
            return;
        }

        L($"asset {Q(call.KeyName)} [duration={call.Duration}] {{");
        _indent++;
        if (hasParams)
            WriteInlineAssetParams(apIdx);
        foreach (int ci in children)
            WriteAssetCall(user, ci);
        _indent--;
        L("}");
    }

    private void WriteChildCall(UserData user, int idx, Container? parentCtn, string? inheritedSwitchProp)
    {
        var call = user.AssetCalls[idx];
        var cond = (call.ConditionIdx >= 0 && call.ConditionIdx < _file.Conditions.Count)
            ? (Condition?)_file.Conditions[call.ConditionIdx] : null;

        string? switchProp = parentCtn?.Type == CtnType.Switch
            ? parentCtn?.SwitchPropName
            : inheritedSwitchProp;

        if (cond != null && cond.Value.ParentType == CtnType.Switch && switchProp == null)
            _callSwitchProp.TryGetValue(idx, out switchProp);

        if (cond != null)
        {
            WriteConditionOpen(cond.Value, parentCtn, switchProp);
            _indent++;
        }

        WriteAssetCall(user, idx, switchProp);

        if (cond != null)
        {
            _indent--;
            L("}");
        }
    }

    private void WriteContainerBody(UserData user, int parentCallIdx, Container ctn, string? inheritedSwitchProp = null)
    {
        string? switchProp = ctn.Type == CtnType.Switch ? ctn.SwitchPropName : inheritedSwitchProp;

        string header = ctn.Type switch
        {
            CtnType.Switch =>
                $"switch {Q(ctn.SwitchPropName ?? "")} {(ctn.SwitchIsGlobal ? "global" : "local")} {{",
            CtnType.Random => "random {",
            CtnType.Random2 => "random2 {",
            CtnType.Blend => "blend {",
            CtnType.Sequence => "sequence {",
            CtnType.Mono => "mono {",
            _ => $"container_{(uint)ctn.Type} {{",
        };
        L(header);
        _indent++;

        int start = ctn.ChildStartIdx;
        int end = ctn.ChildEndIdx;
        if (start >= 0 && end >= start)
        {
            for (int i = start; i <= end; i++)
            {
                if (user.AssetCalls[i].ParentIndex == parentCallIdx)
                    WriteChildCall(user, i, ctn, switchProp);
            }
        }

        _indent--;
        L("}");
    }

    private void WriteConditionOpen(Condition cond, Container? parentCtn, string? overridePropName = null)
    {
        switch (cond.ParentType)
        {
            case CtnType.Switch:
            {
                string propName = overridePropName ?? parentCtn?.SwitchPropName ?? "?";
                string op = FmtCmpOp(cond.SwitchCompareType);
                string val;
                switch (cond.SwitchPropType)
                {
                    case PropType.Enum:
                        val = FormatEnumValue(propName, cond.SwitchEnumName ?? "?");
                        break;
                    case PropType.S32:
                        val = ((int)cond.SwitchValue).ToString();
                        break;
                    case PropType.F32:
                        val = Fl(BitConverter.Int32BitsToSingle((int)cond.SwitchValue));
                        break;
                    default:
                        val = cond.SwitchValue.ToString();
                        break;
                }
                L($"if ({Q(propName)} {op} {val}) {{");
                break;
            }
            case CtnType.Random:
            case CtnType.Random2:
                L($"weight {Fl(cond.RandomWeight)} {{");
                break;
            case CtnType.Blend:
                L($"blend_range {Fl(cond.BlendMin)} {Fl(cond.BlendMax)} type_to_max={cond.BlendTypeToMax} type_to_min={cond.BlendTypeToMin} {{");
                break;
            case CtnType.Sequence:
                L($"step continue_on_fade={cond.SequenceContinueOnFade} {{");
                break;
            default:
                L($"condition_{(uint)cond.ParentType} {{");
                break;
        }
    }

    private static string FormatEnumValue(string propName, string enumName)
    {
        if (PlainEnumValues.Contains(enumName))
            return enumName;
        return $"{propName}::{enumName}";
    }

    private void WriteInlineAssetParams(int apIdx)
    {
        var ps = _file.AssetParams[apIdx];
        foreach (var p in ps.Params)
        {
            if (p.Index < 0 || p.Index >= _file.AssetParamDefs.Count) continue;
            var def = _file.AssetParamDefs[p.Index];
            WriteParamLine(Q(def.Name), p, def);
        }
    }

    private void WriteActions(UserData user)
    {
        if (user.ActionSlots.Count == 0) return;

        L("actions {");
        _indent++;
        foreach (var slot in user.ActionSlots)
        {
            bool hasActions = slot.ActionStartIdx <= slot.ActionEndIdx
                              && slot.ActionStartIdx < user.Actions.Count;
            if (!hasActions)
            {
                L($"slot {Q(slot.Name)} {{}}");
                continue;
            }
            L($"slot {Q(slot.Name)} {{");
            _indent++;
            for (int ai = slot.ActionStartIdx; ai <= slot.ActionEndIdx && ai < user.Actions.Count; ai++)
            {
                var action = user.Actions[ai];
                bool hasTriggers = action.TriggerStartIdx <= action.TriggerEndIdx
                                   && action.TriggerStartIdx < user.ActionTriggers.Count;
                if (!hasTriggers)
                {
                    L($"action {Q(action.Name)} {{}}");
                    continue;
                }
                L($"action {Q(action.Name)} {{");
                _indent++;
                for (uint ti = action.TriggerStartIdx; ti <= action.TriggerEndIdx && ti < user.ActionTriggers.Count; ti++)
                    WriteActionTrigger(user, user.ActionTriggers[(int)ti]);
                _indent--;
                L("}");
            }
            _indent--;
            L("}");
        }
        _indent--;
        L("}");
        L();
    }

    private void WriteActionTrigger(UserData user, ActionTrigger t)
    {
        string assetRef = AssetRef(user, t.AssetCallIdx);
        string timing = t.IsNameMatch
            ? $"name={Q(t.PreviousActionName ?? "")}"
            : $"frame={t.StartFrame}";

        bool hasOw = t.TriggerOverwriteIdx >= 0 && t.TriggerOverwriteIdx < _file.TriggerOverwriteParams.Count
                     && _file.TriggerOverwriteParams[t.TriggerOverwriteIdx].Params.Count > 0;

        var parts = new List<string> { timing };
        if (t.EndFrame != int.MaxValue)
            parts.Add($"end={t.EndFrame}");
        parts.Add($"hash={t.OverwriteHash}");
        parts.Add($"flag={t.Flag}");
        string meta = string.Join(' ', parts);

        _w.Write(Indent());
        _w.Write($"trigger [{meta}] -> {assetRef}");

        if (hasOw)
        {
            _w.WriteLine(" {");
            _indent++;
            WriteInlineTriggerParams(t.TriggerOverwriteIdx);
            _indent--;
            L("}");
        }
        else
        {
            _w.WriteLine();
        }
    }

    private void WriteInlineTriggerParams(int tpIdx)
    {
        var ps = _file.TriggerOverwriteParams[tpIdx];
        foreach (var p in ps.Params)
        {
            if (p.Index < 0 || p.Index >= _file.TriggerParamDefs.Count) continue;
            var def = _file.TriggerParamDefs[p.Index];
            WriteParamLine(Q(def.Name), p, def);
        }
    }

    private void WriteProperties(UserData user)
    {
        if (user.Properties.Count == 0) return;

        L("properties {");
        _indent++;
        foreach (var prop in user.Properties)
        {
            string gl = prop.IsGlobal != 0 ? " global" : "";
            bool hasTriggers = prop.TriggerStartIdx <= prop.TriggerEndIdx
                               && prop.TriggerStartIdx < user.PropertyTriggers.Count;
            if (!hasTriggers)
            {
                L($"prop {Q(prop.Name)}{gl} {{}}");
                continue;
            }
            L($"prop {Q(prop.Name)}{gl} {{");
            _indent++;
            for (uint i = prop.TriggerStartIdx; i <= prop.TriggerEndIdx && i < user.PropertyTriggers.Count; i++)
                WritePropertyTrigger(user, user.PropertyTriggers[(int)i], prop.Name);
            _indent--;
            L("}");
        }
        _indent--;
        L("}");
        L();
    }

    private void WritePropertyTrigger(UserData user, PropertyTrigger t, string propName)
    {
        string assetRef = AssetRef(user, t.AssetCallIdx);

        bool hasCond = t.ConditionIdx >= 0 && t.ConditionIdx < _file.Conditions.Count;
        bool hasOw = t.TriggerOverwriteIdx >= 0 && t.TriggerOverwriteIdx < _file.TriggerOverwriteParams.Count
                     && _file.TriggerOverwriteParams[t.TriggerOverwriteIdx].Params.Count > 0;

        bool hasBody = hasCond || hasOw;

        _w.Write(Indent());
        _w.Write($"ptrig [hash={t.OverwriteHash} flag={t.Flag}] -> {assetRef}");

        if (hasBody)
        {
            _w.WriteLine(" {");
            _indent++;
            if (hasCond)
                L($"cond = {FmtConditionExpr(_file.Conditions[t.ConditionIdx], propName)}");
            if (hasOw)
            {
                L("props {");
                _indent++;
                WriteInlineTriggerParams(t.TriggerOverwriteIdx);
                _indent--;
                L("}");
            }
            _indent--;
            L("}");
        }
        else
        {
            _w.WriteLine();
        }
    }

    private string FmtConditionExpr(Condition cond, string propName)
    {
        string op = FmtCmpOp(cond.SwitchCompareType);
        string val = cond.SwitchPropType switch
        {
            PropType.Enum => FormatEnumValue(propName, cond.SwitchEnumName ?? "?"),
            PropType.S32 => ((int)cond.SwitchValue).ToString(),
            PropType.F32 => Fl(BitConverter.Int32BitsToSingle((int)cond.SwitchValue)),
            _ => cond.SwitchValue.ToString(),
        };
        return $"({Q(propName)} {op} {val})";
    }

    private void WriteAlwaysTriggers(UserData user)
    {
        if (user.AlwaysTriggers.Count == 0) return;

        L("always_triggers {");
        _indent++;
        foreach (var t in user.AlwaysTriggers)
        {
            string assetRef = AssetRef(user, t.AssetCallIdx);
            bool hasOw = t.TriggerOverwriteIdx >= 0 && t.TriggerOverwriteIdx < _file.TriggerOverwriteParams.Count
                         && _file.TriggerOverwriteParams[t.TriggerOverwriteIdx].Params.Count > 0;

            _w.Write(Indent());
            _w.Write($"always [hash={t.OverwriteHash} flag={t.Flag}] -> {assetRef}");

            if (hasOw)
            {
                _w.WriteLine(" {");
                _indent++;
                WriteInlineTriggerParams(t.TriggerOverwriteIdx);
                _indent--;
                L("}");
            }
            else
            {
                _w.WriteLine();
            }
        }
        _indent--;
        L("}");
        L();
    }

    // === Formatting helpers ===

    private void WriteParamLine(string name, Param p, ParamDef def)
    {
        if (p.Type == RefType.Curve)
        {
            _w.Write(Indent());
            _w.Write($"{name} = ");
            WriteCurveBlock((int)p.Value);
            return;
        }
        if (p.Type == RefType.ArrangeParam)
        {
            _w.Write(Indent());
            _w.Write($"{name} = ");
            WriteArrangeBlock((int)p.Value);
            return;
        }
        L($"{name} = {FmtParamVal(p, def)}");
    }

    private string FmtParamVal(Param p, ParamDef def)
    {
        switch (p.Type)
        {
            case RefType.Direct:
                if (p.Value < (uint)_file.DirectValues.Count)
                {
                    uint raw = _file.DirectValues[(int)p.Value].Raw;
                    return def.Type switch
                    {
                        PType.Int => ((int)raw).ToString(),
                        PType.Float => Fl(BitConverter.Int32BitsToSingle((int)raw)),
                        PType.Bool => raw != 0 ? "true" : "false",
                        PType.Enum => ((int)raw).ToString(),
                        PType.Bitfield => $"0b{Convert.ToString(raw, 2).PadLeft(8, '0')}",
                        _ => raw.ToString(),
                    };
                }
                return p.Value.ToString();

            case RefType.String:
                return Q(p.StringValue ?? "");

            case RefType.Bitfield:
                if (p.Value < (uint)_file.DirectValues.Count)
                {
                    uint raw = _file.DirectValues[(int)p.Value].Raw;
                    return $"0b{Convert.ToString(raw, 2).PadLeft(8, '0')}";
                }
                return $"0b{Convert.ToString(p.Value, 2).PadLeft(8, '0')}";

            default:
                if (p.Value < (uint)_file.RandomTable.Count)
                {
                    var r = _file.RandomTable[(int)p.Value];
                    return $"({p.Type} {Fl(r.Min)} {Fl(r.Max)})";
                }
                return $"({p.Type} {p.Value})";
        }
    }

    private void WriteCurveBlock(int curveIdx)
    {
        if (curveIdx < 0 || curveIdx >= _file.CurveTable.Count)
        {
            _w.WriteLine($"(Curve @{curveIdx})");
            return;
        }

        var c = _file.CurveTable[curveIdx];
        string typeName = c.CurveType.ToString();
        string propTag = $" prop={Q(c.PropName)}";

        if (c.Points.Count == 0)
        {
            _w.WriteLine($"curve {typeName}{propTag} {{}}");
            return;
        }

        _w.WriteLine($"curve {typeName}{propTag} {{");
        _indent++;
        foreach (var pt in c.Points)
            L($"({Fl(pt.X)},{Fl(pt.Y)})");
        _indent--;
        L("}");
    }

    private bool IsDefaultParam(Param p, ParamDef def)
    {
        if (p.Type == RefType.Direct && p.Value < (uint)_file.DirectValues.Count)
        {
            uint raw = _file.DirectValues[(int)p.Value].Raw;
            return def.Type != PType.String && raw == def.RawDefault;
        }
        if (p.Type == RefType.String)
            return def.Type == PType.String && p.StringValue == def.StringDefault;
        return false;
    }

    private string AssetRef(UserData user, int callIdx)
    {
        if (callIdx < 0 || callIdx >= user.AssetCalls.Count)
            return $"#{callIdx}";
        return Q(user.AssetCalls[callIdx].KeyName);
    }

    private static string FmtCmpOp(CmpType c) => c switch
    {
        CmpType.Equal => "==",
        CmpType.GreaterThan => ">",
        CmpType.GreaterThanOrEqual => ">=",
        CmpType.LessThan => "<",
        CmpType.LessThanOrEqual => "<=",
        CmpType.NotEqual => "!=",
        _ => "??",
    };

    private void L(string s = "")
    {
        if (s.Length == 0) { _w.WriteLine(); return; }
        _w.Write(Indent());
        _w.WriteLine(s);
    }

    private string Indent() => new string(' ', _indent * 4);

    private static string Fl(float f) => f.ToString("G9", CultureInfo.InvariantCulture);

    private static bool NeedsQuoting(string s)
    {
        if (s.Length == 0) return true;
        foreach (char c in s)
        {
            if (c == ' ' || c == '"' || c == '\\' || c == '{' || c == '}' || c == '[' || c == ']'
                || c == '(' || c == ')' || c == '=' || c == '#' || c == ',' || c == ':'
                || c < 0x20 || (c >= 0x7F && c <= 0x9F))
                return true;
        }
        return false;
    }

    private static string Q(string s)
    {
        if (!NeedsQuoting(s)) return s;
        var sb = new System.Text.StringBuilder(s.Length + 4);
        sb.Append('"');
        foreach (char c in s)
        {
            if (c == '\\') sb.Append("\\\\");
            else if (c == '"') sb.Append("\\\"");
            else if (c < 0x20 || (c >= 0x7F && c <= 0x9F)) sb.Append($"\\x{(int)c:X2}");
            else sb.Append(c);
        }
        sb.Append('"');
        return sb.ToString();
    }
}
