using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;

namespace WoomLink.Converter;

public class XLinkTextReader
{
    private List<string> _lines = null!;
    private int _pos;
    private XLinkFile _file = null!;

    private Dictionary<uint, int> _dvMap = null!;
    private Dictionary<(float, float), int> _rdMap = null!;
    private Dictionary<string, int> _assetParamDedup = null!;
    private Dictionary<string, int> _condDedup = null!;
    private Dictionary<string, int> _trigParamDedup = null!;
    private Dictionary<string, int> _curveDedup = null!;

    private HashSet<string> _allEnumNames = null!;
    private int _explicitUserAssetParamCount = -1;
    private List<string>? _explicitLocalEnums;

    public XLinkFile Read(TextReader reader)
    {
        _file = new XLinkFile();
        _lines = new List<string>();
        string? line;
        while ((line = reader.ReadLine()) != null)
            _lines.Add(line);
        _pos = 0;
        _dvMap = new Dictionary<uint, int>();
        _rdMap = new Dictionary<(float, float), int>();
        _assetParamDedup = new Dictionary<string, int>();
        _condDedup = new Dictionary<string, int>();
        _trigParamDedup = new Dictionary<string, int>();
        _curveDedup = new Dictionary<string, int>();
        _allEnumNames = new HashSet<string>();
        _explicitUserAssetParamCount = -1;
        _explicitLocalEnums = null;

        while (_pos < _lines.Count)
        {
            string trimmed = _lines[_pos].Trim();
            if (trimmed.Length == 0 || trimmed.StartsWith('#') || trimmed.StartsWith("//"))
            { _pos++; continue; }

            var tok = Tokenize(trimmed);
            if (tok.Length == 0) { _pos++; continue; }

            switch (tok[0])
            {
                case "xlink_version":
                    _file.Version = uint.Parse(tok[1]);
                    _pos++;
                    break;
                case "asset_params":
                    _file.AssetParamDefs = ParseParamDefs();
                    break;
                case "trigger_params":
                    _file.TriggerParamDefs = ParseParamDefs();
                    break;
                case "user_params":
                    _file.UserParamDefs = ParseParamDefs();
                    break;
                case "user_asset_param_count":
                    _explicitUserAssetParamCount = int.Parse(tok[1]);
                    _pos++;
                    break;
                case "local_enums":
                    _explicitLocalEnums = ParseLocalEnums();
                    break;
                case "arrange_groups":
                    ParseArrangeGroups();
                    break;
                case "user":
                    ParseUser(tok);
                    break;
                default:
                    _pos++;
                    break;
            }
        }

        Reconstruct();
        return _file;
    }

    private List<string> ParseLocalEnums()
    {
        var enums = new List<string>();
        _pos++;
        while (_pos < _lines.Count)
        {
            string trimmed = _lines[_pos].Trim();
            if (trimmed == "}") { _pos++; break; }
            if (trimmed.Length == 0 || trimmed.StartsWith('#'))
            { _pos++; continue; }
            enums.Add(Unquote(trimmed));
            _pos++;
        }
        return enums;
    }

    // ============================
    // Parsing: param definitions
    // ============================

    private List<ParamDef> ParseParamDefs()
    {
        var defs = new List<ParamDef>();
        ExpectOpenBrace();
        while (_pos < _lines.Count)
        {
            string trimmed = _lines[_pos].Trim();
            if (trimmed == "}") { _pos++; break; }
            if (trimmed.Length == 0 || trimmed.StartsWith('#')) { _pos++; continue; }

            int colonIdx = trimmed.IndexOf(':');
            if (colonIdx < 0) { _pos++; continue; }

            string name = Unquote(trimmed[..colonIdx].Trim());
            string rest = trimmed[(colonIdx + 1)..].Trim();

            int eqIdx = rest.IndexOf('=');
            string typeStr;
            string? valStr = null;
            if (eqIdx >= 0)
            {
                typeStr = rest[..eqIdx].Trim();
                valStr = rest[(eqIdx + 1)..].Trim();
            }
            else
            {
                typeStr = rest.Trim();
            }

            var def = new ParamDef
            {
                Name = name,
                Type = Enum.Parse<PType>(typeStr),
            };

            if (valStr != null && valStr.Length > 0)
            {
                if (def.Type == PType.String)
                    def.StringDefault = Unquote(valStr);
                else if (def.Type == PType.Float)
                    def.RawDefault = (uint)BitConverter.SingleToInt32Bits(ParseFloat(valStr));
                else if (def.Type == PType.Bool)
                    def.RawDefault = valStr == "true" ? 1u : 0u;
                else if (valStr.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
                    def.RawDefault = uint.Parse(valStr[2..], NumberStyles.HexNumber);
                else
                    def.RawDefault = (uint)int.Parse(valStr);
            }
            else
            {
                if (def.Type == PType.String)
                    def.StringDefault = "";
            }

            defs.Add(def);
            _pos++;
        }
        return defs;
    }

    // ============================
    // Parsing: arrange groups
    // ============================

    private void ParseArrangeGroups()
    {
        ExpectOpenBrace();
        while (_pos < _lines.Count)
        {
            string trimmed = _lines[_pos].Trim();
            if (trimmed == "}") { _pos++; break; }
            if (trimmed.Length == 0 || trimmed.StartsWith('#')) { _pos++; continue; }

            if (trimmed.StartsWith("group"))
            {
                _pos++;
                var ag = new ArrangeGroup { Entries = new List<ArrangeGroupEntry>() };
                while (_pos < _lines.Count)
                {
                    string line = _lines[_pos].Trim();
                    if (line == "}") { _pos++; break; }
                    if (line.StartsWith("entry"))
                    {
                        var tok = Tokenize(line);
                        var e = new ArrangeGroupEntry { GroupName = Unquote(tok[1]) };
                        foreach (var t in tok.Skip(2))
                        {
                            var kv = t.Split('=');
                            if (kv.Length != 2) continue;
                            switch (kv[0])
                            {
                                case "limit_type": e.LimitType = uint.Parse(kv[1]); break;
                                case "threshold": e.LimitThreshold = ParseFloat(kv[1]); break;
                                case "unk": e.Unk = uint.Parse(kv[1]); break;
                            }
                        }
                        ag.Entries.Add(e);
                    }
                    _pos++;
                }
                _file.ArrangeGroups.Add(ag);
            }
            else
            {
                _pos++;
            }
        }
    }

    // ============================
    // Parsing: user
    // ============================

    private void ParseUser(string[] firstTok)
    {
        string userIdent = Unquote(firstTok[1]);
        uint hash;
        if (userIdent.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
            hash = uint.Parse(userIdent[2..], NumberStyles.HexNumber);
        else
            hash = sead.HashCrc32.CalcStringHash(userIdent);

        var u = new UserData { Hash = hash };
        _file.UserHashes.Add(hash);

        _pos++;

        while (_pos < _lines.Count)
        {
            string trimmed = _lines[_pos].Trim();
            if (trimmed == "}") { _pos++; break; }
            if (trimmed.Length == 0 || trimmed.StartsWith('#') || trimmed.StartsWith("//"))
            { _pos++; continue; }

            var tok = Tokenize(trimmed);
            if (tok.Length == 0) { _pos++; continue; }

            switch (tok[0])
            {
                case "local_properties":
                    u.LocalProperties = ParseLocalProperties(trimmed);
                    break;
                case "user_params":
                    ParseUserParams(u);
                    break;
                case "assets":
                    ParseAssetsBlock(u);
                    break;
                case "actions":
                    ParseActions(u);
                    break;
                case "properties":
                    ParseProperties(u);
                    break;
                case "always_triggers":
                    ParseAlwaysTriggers(u);
                    break;
                default:
                    ParseAssetCall(u, -1, null);
                    break;
            }
        }

        ComputeAssetIndices(u);
        FillDefaultUserParams(u);
        _file.Users.Add(u);
    }

    private List<string> ParseLocalProperties(string firstLine)
    {
        if (firstLine.Contains('{') && firstLine.Contains('}'))
        {
            int open = firstLine.IndexOf('{');
            int close = firstLine.LastIndexOf('}');
            string inner = firstLine[(open + 1)..close].Trim();
            _pos++;
            if (inner.Length == 0) return new List<string>();
            return inner.Split(',').Select(s => Unquote(s.Trim())).Where(s => s.Length > 0).ToList();
        }
        return ParseStringBlock();
    }

    private List<string> ParseStringBlock()
    {
        var list = new List<string>();
        ExpectOpenBrace();
        while (_pos < _lines.Count)
        {
            string trimmed = _lines[_pos].Trim();
            if (trimmed == "}") { _pos++; break; }
            if (trimmed.Length == 0 || trimmed.StartsWith('#')) { _pos++; continue; }
            list.Add(Unquote(trimmed));
            _pos++;
        }
        return list;
    }

    private void ParseUserParams(UserData u)
    {
        ExpectOpenBrace();
        var paramMap = new Dictionary<string, Param>();
        while (_pos < _lines.Count)
        {
            string trimmed = _lines[_pos].Trim();
            if (trimmed == "}") { _pos++; break; }
            if (trimmed.Length == 0 || trimmed.StartsWith('#')) { _pos++; continue; }

            int eqIdx = FindEquals(trimmed);
            if (eqIdx < 0) { _pos++; continue; }

            string name = Unquote(trimmed[..eqIdx].Trim());
            string valStr = CollectMultilineValue(trimmed[(eqIdx + 1)..].Trim());

            int defIdx = _file.UserParamDefs.FindIndex(d => d.Name == name);
            if (defIdx >= 0)
            {
                var def = _file.UserParamDefs[defIdx];
                paramMap[name] = ResolveParamValue(defIdx, def, valStr);
            }
            _pos++;
        }

        u.UserParams = new List<Param>();
        for (int i = 0; i < _file.UserParamDefs.Count; i++)
        {
            var def = _file.UserParamDefs[i];
            if (paramMap.TryGetValue(def.Name, out var p))
                u.UserParams.Add(p);
            else
                u.UserParams.Add(MakeDefaultParam(i, def));
        }
    }

    private void FillDefaultUserParams(UserData u)
    {
        if (u.UserParams.Count >= _file.UserParamDefs.Count) return;

        var existing = new HashSet<int>(u.UserParams.Select(p => p.Index));
        var full = new List<Param>();
        for (int i = 0; i < _file.UserParamDefs.Count; i++)
        {
            var found = u.UserParams.FirstOrDefault(p => p.Index == i);
            if (existing.Contains(i))
                full.Add(found);
            else
                full.Add(MakeDefaultParam(i, _file.UserParamDefs[i]));
        }
        u.UserParams = full;
    }

    // ============================
    // Parsing: asset calls (tree)
    // ============================

    private void ParseAssetsBlock(UserData u)
    {
        _pos++; // skip "assets {"
        while (_pos < _lines.Count)
        {
            string trimmed = _lines[_pos].Trim();
            if (trimmed == "}") { _pos++; break; }
            if (trimmed.Length == 0 || trimmed.StartsWith('#') || trimmed.StartsWith("//"))
            { _pos++; continue; }
            ParseAssetCall(u, -1, null);
        }
    }

    private void ParseAssetCall(UserData u, int parentIdx, Container? parentCtn)
    {
        string trimmed = _lines[_pos].Trim();
        var tok = Tokenize(trimmed);

        int nameIdx = 0;
        if (tok[0] == "asset") nameIdx = 1;
        string keyName = Unquote(tok[nameIdx]);
        int duration = 0;

        foreach (var t in tok.Skip(nameIdx + 1))
        {
            if (t == "{" || t == "{}") break;
            var raw = StripBrackets(t);
            var kv = raw.Split('=');
            if (kv.Length != 2) continue;
            if (kv[0] == "duration") duration = int.Parse(kv[1]);
        }

        bool isEmpty = trimmed.EndsWith("{}");
        int callIdx = u.AssetCalls.Count;

        if (isEmpty)
        {
            u.AssetCalls.Add(new AssetCall
            {
                KeyName = keyName,
                Guid = sead.HashCrc32.CalcStringHash(keyName),
                Duration = duration,
                Flag = 0,
                ParentIndex = parentIdx,
                KeyNameHash = sead.HashCrc32.CalcStringHash(keyName),
                AssetParamIdx = -1,
                ContainerParamIdx = -1,
                ConditionIdx = -1,
                AssetIndex = -1,
            });
            _pos++;
            return;
        }

        _pos++;
        SkipEmpty();

        string nextLine = PeekTrimmed();
        bool isContainer = nextLine.StartsWith("switch ") || nextLine.StartsWith("random ") ||
                           nextLine.StartsWith("random2 ") || nextLine == "random {" ||
                           nextLine == "random2 {" || nextLine == "blend {" ||
                           nextLine == "sequence {" || nextLine == "mono {" ||
                           nextLine.StartsWith("container_");

        if (isContainer)
        {
            var ctn = ParseContainerHeader();
            int ctnIdx = u.Containers.Count;
            u.Containers.Add(ctn);

            u.AssetCalls.Add(new AssetCall
            {
                KeyName = keyName,
                Guid = sead.HashCrc32.CalcStringHash(keyName),
                Duration = duration,
                Flag = 1,
                ParentIndex = parentIdx,
                KeyNameHash = sead.HashCrc32.CalcStringHash(keyName),
                AssetParamIdx = -1,
                ContainerParamIdx = ctnIdx,
                ConditionIdx = -1,
                AssetIndex = -1,
            });

            int childStart = u.AssetCalls.Count;

            while (_pos < _lines.Count)
            {
                string ct = _lines[_pos].Trim();
                if (ct == "}") { _pos++; break; }
                if (ct.Length == 0 || ct.StartsWith('#')) { _pos++; continue; }

                int condIdx = -1;
                if (IsConditionLine(ct))
                    condIdx = ParseConditionAndGetIdx(ctn, u);

                if (_pos < _lines.Count && _lines[_pos].Trim() != "}")
                {
                    int beforeCount = u.AssetCalls.Count;
                    ParseAssetCall(u, callIdx, ctn);
                    if (beforeCount < u.AssetCalls.Count && condIdx >= 0)
                    {
                        var child = u.AssetCalls[beforeCount];
                        child.ConditionIdx = condIdx;
                        u.AssetCalls[beforeCount] = child;
                    }
                }

                if (condIdx >= 0)
                {
                    SkipEmpty();
                    if (_pos < _lines.Count && _lines[_pos].Trim() == "}")
                        _pos++;
                }
            }

            int childEnd = u.AssetCalls.Count - 1;
            var c = u.Containers[ctnIdx];
            c.ChildStartIdx = childStart;
            c.ChildEndIdx = childEnd >= childStart ? childEnd : childStart;
            u.Containers[ctnIdx] = c;
        }
        else
        {
            var paramList = new List<Param>();
            var deferredChildLines = new List<int>();
            while (_pos < _lines.Count)
            {
                string pt = _lines[_pos].Trim();
                if (pt == "}") { _pos++; break; }
                if (pt.Length == 0 || pt.StartsWith('#')) { _pos++; continue; }

                if (pt.StartsWith("asset "))
                {
                    deferredChildLines.Add(_pos);
                    SkipBlock();
                    continue;
                }

                int eqIdx = FindEquals(pt);
                if (eqIdx < 0) { _pos++; continue; }

                string pName = Unquote(pt[..eqIdx].Trim());
                string pVal = CollectMultilineValue(pt[(eqIdx + 1)..].Trim());

                int defIdx = _file.AssetParamDefs.FindIndex(d => d.Name == pName);
                if (defIdx >= 0)
                    paramList.Add(ResolveParamValue(defIdx, _file.AssetParamDefs[defIdx], pVal));
                _pos++;
            }

            int apIdx = DeduplicateAssetParams(paramList);

            u.AssetCalls.Add(new AssetCall
            {
                KeyName = keyName,
                Guid = sead.HashCrc32.CalcStringHash(keyName),
                Duration = duration,
                Flag = 0,
                ParentIndex = parentIdx,
                KeyNameHash = sead.HashCrc32.CalcStringHash(keyName),
                AssetParamIdx = apIdx,
                ContainerParamIdx = -1,
                ConditionIdx = -1,
                AssetIndex = -1,
            });

            int savedPos = _pos;
            foreach (int childLinePos in deferredChildLines)
            {
                _pos = childLinePos;
                ParseAssetCall(u, callIdx, null);
            }
            _pos = savedPos;
        }

        if (isContainer)
        {
            SkipEmpty();
            if (_pos < _lines.Count && _lines[_pos].Trim() == "}")
                _pos++;
        }
    }

    private Container ParseContainerHeader()
    {
        string line = _lines[_pos].Trim();
        var tok = Tokenize(line);
        var ctn = new Container { SwitchWatchPropertyId = -1, SwitchPropertyIndex = -1 };

        switch (tok[0])
        {
            case "switch":
                ctn.Type = CtnType.Switch;
                ctn.SwitchPropName = Unquote(tok[1]);
                foreach (var t in tok.Skip(2))
                {
                    if (t == "{") break;
                    if (t == "global") { ctn.SwitchIsGlobal = true; continue; }
                    if (t == "local") { ctn.SwitchIsGlobal = false; continue; }
                }
                break;
            case "random": ctn.Type = CtnType.Random; break;
            case "random2": ctn.Type = CtnType.Random2; break;
            case "blend": ctn.Type = CtnType.Blend; break;
            case "sequence": ctn.Type = CtnType.Sequence; break;
            case "mono": ctn.Type = CtnType.Mono; break;
            default:
                if (tok[0].StartsWith("container_"))
                    ctn.Type = (CtnType)uint.Parse(tok[0][10..]);
                break;
        }

        _pos++;
        return ctn;
    }

    private bool IsConditionLine(string trimmed)
    {
        return trimmed.StartsWith("if ") || trimmed.StartsWith("weight ") ||
               trimmed.StartsWith("blend_range ") || trimmed.StartsWith("step ") ||
               trimmed.StartsWith("condition_");
    }

    private int ParseConditionAndGetIdx(Container ctn, UserData? user = null)
    {
        string trimmed = _lines[_pos].Trim();
        var cond = new Condition();

        if (trimmed.StartsWith("if "))
        {
            cond.ParentType = CtnType.Switch;
            int parenOpen = trimmed.IndexOf('(');
            int parenClose = trimmed.IndexOf(')');

            if (parenOpen >= 0 && parenClose > parenOpen)
            {
                string inner = trimmed[(parenOpen + 1)..parenClose].Trim();
                ParseSwitchCondition(inner, ref cond, ctn, user);
            }
        }
        else if (trimmed.StartsWith("weight "))
        {
            cond.ParentType = ctn.Type == CtnType.Random2 ? CtnType.Random2 : CtnType.Random;
            var tok = Tokenize(trimmed);
            cond.RandomWeight = ParseFloat(tok[1]);
        }
        else if (trimmed.StartsWith("blend_range "))
        {
            cond.ParentType = CtnType.Blend;
            var tok = Tokenize(trimmed);
            cond.BlendMin = ParseFloat(tok[1]);
            cond.BlendMax = ParseFloat(tok[2]);
            foreach (var t in tok.Skip(3))
            {
                var kv = t.Split('=');
                if (kv.Length != 2) continue;
                switch (kv[0])
                {
                    case "type_to_max": cond.BlendTypeToMax = byte.Parse(kv[1]); break;
                    case "type_to_min": cond.BlendTypeToMin = byte.Parse(kv[1]); break;
                }
            }
        }
        else if (trimmed.StartsWith("step "))
        {
            cond.ParentType = CtnType.Sequence;
            var tok = Tokenize(trimmed);
            foreach (var t in tok.Skip(1))
            {
                var kv = t.Split('=');
                if (kv.Length != 2) continue;
                if (kv[0] == "continue_on_fade")
                    cond.SequenceContinueOnFade = int.Parse(kv[1]);
            }
        }

        _pos++;
        return DeduplicateCondition(cond);
    }

    private void ParseSwitchCondition(string inner, ref Condition cond, Container ctn, UserData? user)
    {
        string[] ops = { ">=", "<=", "!=", "==", ">", "<" };
        foreach (var op in ops)
        {
            int opIdx = inner.IndexOf(op, StringComparison.Ordinal);
            if (opIdx < 0) continue;

            cond.SwitchCompareType = op switch
            {
                "==" => CmpType.Equal,
                ">" => CmpType.GreaterThan,
                ">=" => CmpType.GreaterThanOrEqual,
                "<" => CmpType.LessThan,
                "<=" => CmpType.LessThanOrEqual,
                "!=" => CmpType.NotEqual,
                _ => CmpType.Equal,
            };

            string lhs = Unquote(inner[..opIdx].Trim());
            string valStr = inner[(opIdx + op.Length)..].Trim();
            valStr = Unquote(valStr);

            if (valStr.Contains("::"))
            {
                int colonIdx = valStr.IndexOf("::", StringComparison.Ordinal);
                valStr = valStr[(colonIdx + 2)..];
            }

            if (float.TryParse(valStr, NumberStyles.Float, CultureInfo.InvariantCulture, out float fv) &&
                valStr.Contains('.'))
            {
                cond.SwitchPropType = PropType.F32;
                cond.SwitchValue = (uint)BitConverter.SingleToInt32Bits(fv);
            }
            else if (int.TryParse(valStr, out int iv))
            {
                cond.SwitchPropType = PropType.S32;
                cond.SwitchValue = (uint)iv;
            }
            else
            {
                cond.SwitchPropType = PropType.Enum;
                cond.SwitchEnumName = valStr;
                _allEnumNames.Add(valStr);
            }

            if (ctn.Type == CtnType.Switch)
            {
                cond.SwitchIsGlobal = ctn.SwitchIsGlobal;
            }
            else
            {
                cond.SwitchPropName = lhs;
                if (user != null)
                    cond.SwitchIsGlobal = !user.LocalProperties.Contains(lhs);
            }
            break;
        }
    }

    // ============================
    // Parsing: triggers
    // ============================

    private void ParseActions(UserData u)
    {
        ExpectOpenBrace();
        while (_pos < _lines.Count)
        {
            string trimmed = _lines[_pos].Trim();
            if (trimmed == "}") { _pos++; break; }
            if (trimmed.Length == 0 || trimmed.StartsWith('#')) { _pos++; continue; }

            if (trimmed.StartsWith("slot "))
            {
                var tok = Tokenize(trimmed);
                string slotName = Unquote(tok[1]);
                ushort actionStart = (ushort)u.Actions.Count;

                if (trimmed.EndsWith("{}"))
                {
                    _pos++;
                }
                else
                {
                    _pos++;
                    while (_pos < _lines.Count)
                    {
                        string at = _lines[_pos].Trim();
                        if (at == "}") { _pos++; break; }
                        if (at.Length == 0 || at.StartsWith('#')) { _pos++; continue; }

                        if (at.StartsWith("action "))
                        {
                            var atTok = Tokenize(at);
                            string actionName = Unquote(atTok[1]);
                            uint trigStart = (uint)u.ActionTriggers.Count;

                            if (at.EndsWith("{}"))
                            {
                                _pos++;
                            }
                            else
                            {
                                _pos++;
                                while (_pos < _lines.Count)
                                {
                                    string tt = _lines[_pos].Trim();
                                    if (tt == "}") { _pos++; break; }
                                    if (tt.Length == 0 || tt.StartsWith('#')) { _pos++; continue; }

                                    if (tt.StartsWith("trigger "))
                                        ParseActionTrigger(u);
                                    else
                                        _pos++;
                                }
                            }

                            uint trigEnd = (uint)u.ActionTriggers.Count;
                            u.Actions.Add(new Action
                            {
                                Name = actionName,
                                TriggerStartIdx = trigStart,
                                TriggerEndIdx = trigEnd > trigStart ? trigEnd - 1 : trigStart - 1,
                            });
                        }
                        else
                        {
                            _pos++;
                        }
                    }
                }

                ushort actionEnd = (ushort)u.Actions.Count;
                u.ActionSlots.Add(new ActionSlot
                {
                    Name = slotName,
                    ActionStartIdx = actionStart,
                    ActionEndIdx = actionEnd > actionStart ? (ushort)(actionEnd - 1) : (ushort)(actionStart - 1),
                });
            }
            else
            {
                _pos++;
            }
        }
    }

    private void ParseActionTrigger(UserData u)
    {
        string line = _lines[_pos].Trim();
        var tok = Tokenize(line);

        var t = new ActionTrigger { TriggerOverwriteIdx = -1, EndFrame = int.MaxValue };
        string? targetKeyName = null;
        bool hasBody = line.TrimEnd().EndsWith("{");

        bool seenArrow = false;
        foreach (var token in tok.Skip(1))
        {
            if (token == "{") break;
            if (token == "->") { seenArrow = true; continue; }
            if (seenArrow)
            {
                targetKeyName = Unquote(token);
                continue;
            }
            var raw = StripBrackets(token);
            foreach (var part in raw.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            {
                var kv = part.Split('=');
                if (kv.Length != 2) continue;
                switch (kv[0])
                {
                    case "hash": t.OverwriteHash = ushort.Parse(kv[1]); break;
                    case "frame": t.StartFrame = uint.Parse(kv[1]); break;
                    case "end": t.EndFrame = int.Parse(kv[1]); break;
                    case "name":
                        t.PreviousActionName = Unquote(kv[1]);
                        t.Flag |= 0x10;
                        break;
                }
            }
        }

        t.Flag |= 0x01;
        t.AssetCallIdx = ResolveAssetCallIdx(u, targetKeyName);
        t.Guid = sead.HashCrc32.CalcStringHash(targetKeyName ?? "");
        _pos++;

        if (hasBody)
        {
            var trigParams = new List<Param>();
            while (_pos < _lines.Count)
            {
                string pt = _lines[_pos].Trim();
                if (pt == "}") { _pos++; break; }
                if (pt.Length == 0 || pt.StartsWith('#')) { _pos++; continue; }

                int eqIdx = FindEquals(pt);
                if (eqIdx >= 0)
                {
                    string pName = Unquote(pt[..eqIdx].Trim());
                    string pVal = CollectMultilineValue(pt[(eqIdx + 1)..].Trim());
                    int defIdx = _file.TriggerParamDefs.FindIndex(d => d.Name == pName);
                    if (defIdx >= 0)
                        trigParams.Add(ResolveParamValue(defIdx, _file.TriggerParamDefs[defIdx], pVal));
                }
                _pos++;
            }

            if (trigParams.Count > 0)
                t.TriggerOverwriteIdx = DeduplicateTriggerParams(trigParams);
        }

        u.ActionTriggers.Add(t);
    }

    private void ParseProperties(UserData u)
    {
        ExpectOpenBrace();
        while (_pos < _lines.Count)
        {
            string trimmed = _lines[_pos].Trim();
            if (trimmed == "}") { _pos++; break; }
            if (trimmed.Length == 0 || trimmed.StartsWith('#')) { _pos++; continue; }

            if (trimmed.StartsWith("prop "))
            {
                var tok = Tokenize(trimmed);
                string propName = Unquote(tok[1]);
                uint isGlobal = tok.Any(t => t == "global") ? 1u : 0u;
                uint trigStart = (uint)u.PropertyTriggers.Count;

                if (trimmed.EndsWith("{}"))
                {
                    _pos++;
                }
                else
                {
                    _pos++;
                    while (_pos < _lines.Count)
                    {
                        string pt = _lines[_pos].Trim();
                        if (pt == "}") { _pos++; break; }
                        if (pt.Length == 0 || pt.StartsWith('#')) { _pos++; continue; }

                        if (pt.StartsWith("ptrig "))
                            ParsePropertyTrigger(u, propName);
                        else
                            _pos++;
                    }
                }

                uint trigEnd = (uint)u.PropertyTriggers.Count;
                u.Properties.Add(new Property
                {
                    Name = propName,
                    IsGlobal = isGlobal,
                    TriggerStartIdx = trigStart,
                    TriggerEndIdx = trigEnd > trigStart ? trigEnd - 1 : trigStart - 1,
                });
            }
            else
            {
                _pos++;
            }
        }
    }

    private void ParsePropertyTrigger(UserData u, string ownerPropName)
    {
        string line = _lines[_pos].Trim();
        var tok = Tokenize(line);

        var t = new PropertyTrigger { ConditionIdx = -1, TriggerOverwriteIdx = -1 };
        bool hasBody = line.TrimEnd().EndsWith("{");
        string? targetKeyName = null;

        bool seenArrow = false;
        foreach (var token in tok.Skip(1))
        {
            if (token == "{") break;
            if (token == "->") { seenArrow = true; continue; }
            if (seenArrow)
            {
                targetKeyName = Unquote(token);
                continue;
            }
            var raw = StripBrackets(token);
            foreach (var part in raw.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            {
                var kv = part.Split('=');
                if (kv.Length != 2) continue;
                switch (kv[0])
                {
                    case "hash": t.OverwriteHash = ushort.Parse(kv[1]); break;
                }
            }
        }

        t.Flag = 0x0001;
        t.AssetCallIdx = ResolveAssetCallIdx(u, targetKeyName);
        t.Guid = sead.HashCrc32.CalcStringHash(targetKeyName ?? "");
        _pos++;

        if (hasBody)
        {
            var trigParams = new List<Param>();
            while (_pos < _lines.Count)
            {
                string pt = _lines[_pos].Trim();
                if (pt == "}") { _pos++; break; }
                if (pt.Length == 0 || pt.StartsWith('#')) { _pos++; continue; }

                if (pt.StartsWith("cond ") || pt.StartsWith("cond="))
                {
                    t.ConditionIdx = ParseCondExpr(ownerPropName, u);
                    _pos++;
                }
                else if (IsConditionLine(pt))
                {
                    t.ConditionIdx = ParseConditionAndGetIdx(
                        new Container { Type = CtnType.Switch }, u);
                    if (_pos < _lines.Count && _lines[_pos].Trim() == "}")
                        _pos++;
                }
                else if (pt == "props {" || pt == "props{")
                {
                    _pos++;
                    while (_pos < _lines.Count)
                    {
                        string pp = _lines[_pos].Trim();
                        if (pp == "}") { _pos++; break; }
                        if (pp.Length == 0 || pp.StartsWith('#')) { _pos++; continue; }
                        int eqIdx = FindEquals(pp);
                        if (eqIdx >= 0)
                        {
                            string pName = Unquote(pp[..eqIdx].Trim());
                            string pVal = CollectMultilineValue(pp[(eqIdx + 1)..].Trim());
                            int defIdx = _file.TriggerParamDefs.FindIndex(d => d.Name == pName);
                            if (defIdx >= 0)
                                trigParams.Add(ResolveParamValue(defIdx, _file.TriggerParamDefs[defIdx], pVal));
                        }
                        _pos++;
                    }
                }
                else
                {
                    int eqIdx = FindEquals(pt);
                    if (eqIdx >= 0)
                    {
                        string pName = Unquote(pt[..eqIdx].Trim());
                        string pVal = CollectMultilineValue(pt[(eqIdx + 1)..].Trim());
                        int defIdx = _file.TriggerParamDefs.FindIndex(d => d.Name == pName);
                        if (defIdx >= 0)
                            trigParams.Add(ResolveParamValue(defIdx, _file.TriggerParamDefs[defIdx], pVal));
                    }
                    _pos++;
                }
            }

            if (trigParams.Count > 0)
                t.TriggerOverwriteIdx = DeduplicateTriggerParams(trigParams);
        }

        u.PropertyTriggers.Add(t);
    }

    private int ParseCondExpr(string ownerPropName, UserData u)
    {
        string line = _lines[_pos].Trim();
        int eqPos = line.IndexOf('=');
        if (eqPos < 0) return -1;
        string expr = line[(eqPos + 1)..].Trim();

        if (expr.StartsWith('(') && expr.EndsWith(')'))
            expr = expr[1..^1].Trim();

        string[] ops = { ">=", "<=", "!=", "==", ">", "<" };
        var cond = new Condition { ParentType = CtnType.Switch };

        foreach (var op in ops)
        {
            int opIdx = expr.IndexOf(op, StringComparison.Ordinal);
            if (opIdx < 0) continue;

            cond.SwitchCompareType = op switch
            {
                "==" => CmpType.Equal,
                ">" => CmpType.GreaterThan,
                ">=" => CmpType.GreaterThanOrEqual,
                "<" => CmpType.LessThan,
                "<=" => CmpType.LessThanOrEqual,
                "!=" => CmpType.NotEqual,
                _ => CmpType.Equal,
            };

            string lhs = Unquote(expr[..opIdx].Trim());
            string valStr = expr[(opIdx + op.Length)..].Trim();
            valStr = Unquote(valStr);

            if (valStr.Contains("::"))
            {
                int colonIdx = valStr.IndexOf("::", StringComparison.Ordinal);
                valStr = valStr[(colonIdx + 2)..];
            }

            if (float.TryParse(valStr, NumberStyles.Float, CultureInfo.InvariantCulture, out float fv) &&
                valStr.Contains('.'))
            {
                cond.SwitchPropType = PropType.F32;
                cond.SwitchValue = (uint)BitConverter.SingleToInt32Bits(fv);
            }
            else if (int.TryParse(valStr, out int iv))
            {
                cond.SwitchPropType = PropType.S32;
                cond.SwitchValue = (uint)iv;
            }
            else
            {
                cond.SwitchPropType = PropType.Enum;
                cond.SwitchEnumName = valStr;
                _allEnumNames.Add(valStr);
            }

            cond.SwitchPropName = lhs;
            cond.SwitchIsGlobal = !u.LocalProperties.Contains(lhs);
            break;
        }

        return DeduplicateCondition(cond);
    }

    private void ParseAlwaysTriggers(UserData u)
    {
        ExpectOpenBrace();
        while (_pos < _lines.Count)
        {
            string trimmed = _lines[_pos].Trim();
            if (trimmed == "}") { _pos++; break; }
            if (trimmed.Length == 0 || trimmed.StartsWith('#')) { _pos++; continue; }

            if (trimmed.StartsWith("always "))
            {
                var tok = Tokenize(trimmed);
                var t = new AlwaysTrigger { TriggerOverwriteIdx = -1 };
                bool hasBody = trimmed.TrimEnd().EndsWith("{");
                string? targetKeyName = null;

                bool seenArrow = false;
                foreach (var token in tok.Skip(1))
                {
                    if (token == "{" || token == "->") { if (token == "->") seenArrow = true; continue; }
                    if (seenArrow)
                    {
                        targetKeyName = Unquote(token);
                        continue;
                    }
                    var raw = StripBrackets(token);
                    foreach (var part in raw.Split(' ', StringSplitOptions.RemoveEmptyEntries))
                    {
                        var kv = part.Split('=');
                        if (kv.Length != 2) continue;
                        switch (kv[0])
                        {
                            case "hash": t.OverwriteHash = ushort.Parse(kv[1]); break;
                        }
                    }
                }

                t.Flag = 0x0001;
                t.AssetCallIdx = ResolveAssetCallIdx(u, targetKeyName);
                t.Guid = sead.HashCrc32.CalcStringHash(targetKeyName ?? "");
                _pos++;

                if (hasBody)
                {
                    var trigParams = new List<Param>();
                    while (_pos < _lines.Count)
                    {
                        string pt = _lines[_pos].Trim();
                        if (pt == "}") { _pos++; break; }
                        if (pt.Length == 0 || pt.StartsWith('#')) { _pos++; continue; }

                        int eqIdx = FindEquals(pt);
                        if (eqIdx >= 0)
                        {
                            string pName = Unquote(pt[..eqIdx].Trim());
                            string pVal = CollectMultilineValue(pt[(eqIdx + 1)..].Trim());
                            int defIdx = _file.TriggerParamDefs.FindIndex(d => d.Name == pName);
                            if (defIdx >= 0)
                                trigParams.Add(ResolveParamValue(defIdx, _file.TriggerParamDefs[defIdx], pVal));
                        }
                        _pos++;
                    }

                    if (trigParams.Count > 0)
                        t.TriggerOverwriteIdx = DeduplicateTriggerParams(trigParams);
                }

                u.AlwaysTriggers.Add(t);
            }
            else
            {
                _pos++;
            }
        }
    }

    // ============================
    // Asset call reference resolution
    // ============================

    private static int ResolveAssetCallIdx(UserData u, string? keyName)
    {
        if (keyName == null) return -1;
        if (keyName.StartsWith("#"))
        {
            if (int.TryParse(keyName[1..], out int idx)) return idx;
            return -1;
        }
        for (int i = 0; i < u.AssetCalls.Count; i++)
        {
            if (u.AssetCalls[i].KeyName == keyName) return i;
        }
        return -1;
    }

    private void ComputeAssetIndices(UserData u)
    {
        short assetIdx = 0;
        for (int i = 0; i < u.AssetCalls.Count; i++)
        {
            var ac = u.AssetCalls[i];
            if (ac.IsContainer)
                ac.AssetIndex = -1;
            else
                ac.AssetIndex = assetIdx++;
            u.AssetCalls[i] = ac;
        }
    }

    // ============================
    // Value resolution
    // ============================

    private Param ResolveParamValue(int defIdx, ParamDef def, string valStr)
    {
        // Inline curve: "curve 0 { (x,y) (x,y) }"
        if (valStr.StartsWith("curve "))
        {
            int curveIdx = ParseInlineCurve(valStr, defIdx, def);
            return new Param { Index = defIdx, Type = RefType.Curve, Value = (uint)curveIdx };
        }
        if (valStr.StartsWith("(Arrange @"))
        {
            uint idx = uint.Parse(valStr[10..].TrimEnd(')'));
            return new Param { Index = defIdx, Type = RefType.ArrangeParam, Value = idx };
        }
        if (valStr.StartsWith("arrange ") || valStr.StartsWith("arrange{"))
        {
            int agIdx = ParseInlineArrangeGroup(valStr);
            return new Param { Index = defIdx, Type = RefType.ArrangeParam, Value = (uint)agIdx };
        }
        // Random types: "(Random min max)", "(RandomPow2 min max)", etc.
        if (valStr.StartsWith("(") && valStr.EndsWith(")"))
        {
            int spaceIdx = valStr.IndexOf(' ', 1);
            if (spaceIdx > 1)
            {
                string typeName = valStr[1..spaceIdx];
                if (Enum.TryParse<RefType>(typeName, out var rt) && rt >= RefType.Random)
                {
                    string rest = valStr[(spaceIdx + 1)..^1].Trim();
                    var parts = rest.Split(' ');
                    if (parts.Length >= 2)
                    {
                        float min = ParseFloat(parts[0]);
                        float max = ParseFloat(parts[1]);
                        uint rdIdx = GetOrAddRandom(min, max);
                        return new Param { Index = defIdx, Type = rt, Value = rdIdx };
                    }
                }
            }
        }
        // Bitfield: "0b00000111" – value goes into DirectValues table; keep RefType.Bitfield
        // so the binary type tag is preserved and the text writer outputs 0b format on roundtrip.
        // The game's getResParamValueInt_ treats Bitfield identically to Direct (DV table lookup).
        if (valStr.StartsWith("0b"))
        {
            uint val = Convert.ToUInt32(valStr[2..], 2);
            uint dvIdx = GetOrAddDirectValue(val);
            return new Param { Index = defIdx, Type = RefType.Bitfield, Value = dvIdx };
        }
        // String
        if (valStr.StartsWith("\"") || (def.Type == PType.String && !valStr.StartsWith("(")))
        {
            return new Param { Index = defIdx, Type = RefType.String, StringValue = Unquote(valStr) };
        }
        if (valStr == "true" || valStr == "false")
        {
            uint raw = valStr == "true" ? 1u : 0u;
            uint dvIdx = GetOrAddDirectValue(raw);
            return new Param { Index = defIdx, Type = RefType.Direct, Value = dvIdx };
        }
        if (def.Type == PType.Float || valStr.Contains('.'))
        {
            float fv = ParseFloat(valStr);
            uint raw = (uint)BitConverter.SingleToInt32Bits(fv);
            uint dvIdx = GetOrAddDirectValue(raw);
            return new Param { Index = defIdx, Type = RefType.Direct, Value = dvIdx };
        }
        if (valStr.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
        {
            uint raw = uint.Parse(valStr[2..], NumberStyles.HexNumber);
            uint dvIdx = GetOrAddDirectValue(raw);
            return new Param { Index = defIdx, Type = RefType.Direct, Value = dvIdx };
        }
        {
            int iv = int.Parse(valStr);
            uint dvIdx = GetOrAddDirectValue((uint)iv);
            return new Param { Index = defIdx, Type = RefType.Direct, Value = dvIdx };
        }
    }

    private Dictionary<string, int> _arrangeDedup = new();

    private int ParseInlineArrangeGroup(string valStr)
    {
        int braceStart = valStr.IndexOf('{');
        int braceEnd = valStr.LastIndexOf('}');
        if (braceStart < 0 || braceEnd <= braceStart)
        {
            _file.ArrangeGroups.Add(new ArrangeGroup { Entries = new List<ArrangeGroupEntry>() });
            return _file.ArrangeGroups.Count - 1;
        }

        string inner = valStr[(braceStart + 1)..braceEnd].Trim();
        var ag = new ArrangeGroup { Entries = new List<ArrangeGroupEntry>() };
        if (inner.Length > 0)
        {
            int searchStart = 0;
            while (searchStart < inner.Length)
            {
                int entryPos = inner.IndexOf("entry ", searchStart, StringComparison.Ordinal);
                if (entryPos < 0) break;
                int nextEntry = inner.IndexOf("entry ", entryPos + 6, StringComparison.Ordinal);
                string segment = (nextEntry >= 0
                    ? inner[entryPos..nextEntry]
                    : inner[entryPos..]).Trim().TrimEnd(';');
                searchStart = nextEntry >= 0 ? nextEntry : inner.Length;

                var tok = Tokenize(segment);
                if (tok.Length < 2) continue;
                var e = new ArrangeGroupEntry { GroupName = Unquote(tok[1]) };
                foreach (var t in tok.Skip(2))
                {
                    var kv = t.Split('=');
                    if (kv.Length != 2) continue;
                    switch (kv[0])
                    {
                        case "limit_type": e.LimitType = uint.Parse(kv[1]); break;
                        case "threshold": e.LimitThreshold = ParseFloat(kv[1]); break;
                        case "unk": e.Unk = uint.Parse(kv[1]); break;
                    }
                }
                ag.Entries.Add(e);
            }
        }

        string key = string.Join(";", ag.Entries.Select(e =>
            $"{e.GroupName}|{e.LimitType}|{e.LimitThreshold}|{e.Unk}"));
        if (_arrangeDedup.TryGetValue(key, out int existing))
            return existing;

        int idx = _file.ArrangeGroups.Count;
        _arrangeDedup[key] = idx;
        _file.ArrangeGroups.Add(ag);
        return idx;
    }

    private int ParseInlineCurve(string valStr, int defIdx, ParamDef def)
    {
        // "curve TYPE prop="PropName" { (x,y) ... }" or "curve TYPE { (x,y) ... }"
        var tok = Tokenize(valStr);
        ushort curveType = ushort.Parse(tok[1]);
        string propName = def.Name;
        var points = new List<CurvePoint>();

        for (int i = 2; i < tok.Length; i++)
        {
            string t = tok[i];
            if (t.StartsWith("prop="))
            {
                propName = Unquote(t[5..]);
                continue;
            }
            if (t == "{" || t == "{}" || t == "}") continue;
            if (t.StartsWith("("))
            {
                t = t.Trim('(', ')');
                var parts = t.Split(',');
                if (parts.Length >= 2)
                {
                    points.Add(new CurvePoint
                    {
                        X = ParseFloat(parts[0].Trim()),
                        Y = ParseFloat(parts[1].Trim()),
                    });
                }
            }
        }

        string key = $"{propName}:{curveType}:{string.Join(";", points.Select(p => $"{p.X},{p.Y}"))}";
        if (_curveDedup.TryGetValue(key, out int existing))
            return existing;

        int idx = _file.CurveTable.Count;
        _curveDedup[key] = idx;
        _file.CurveTable.Add(new CurveCall
        {
            CurveType = curveType,
            NumPoints = (ushort)points.Count,
            PropName = propName,
            Points = points,
        });
        return idx;
    }

    private Param MakeDefaultParam(int defIdx, ParamDef def)
    {
        if (def.Type == PType.String)
            return new Param { Index = defIdx, Type = RefType.String, StringValue = def.StringDefault ?? "" };

        uint dvIdx = GetOrAddDirectValue(def.RawDefault);
        return new Param { Index = defIdx, Type = RefType.Direct, Value = dvIdx };
    }

    // ============================
    // Deduplication
    // ============================

    private uint GetOrAddDirectValue(uint raw)
    {
        if (_dvMap.TryGetValue(raw, out int idx))
            return (uint)idx;
        idx = _file.DirectValues.Count;
        _dvMap[raw] = idx;
        _file.DirectValues.Add(new DirectValue { Raw = raw });
        return (uint)idx;
    }

    private uint GetOrAddRandom(float min, float max)
    {
        var key = (min, max);
        if (_rdMap.TryGetValue(key, out int idx))
            return (uint)idx;
        idx = _file.RandomTable.Count;
        _rdMap[key] = idx;
        _file.RandomTable.Add(new RandomCall { Min = min, Max = max });
        return (uint)idx;
    }

    private static string ParamKey(Param p) =>
        p.Type == RefType.String
            ? $"{p.Index}:S:{p.StringValue}"
            : $"{p.Index}:{p.Type}:{p.Value}";

    private int DeduplicateAssetParams(List<Param> parms)
    {
        if (parms.Count == 0)
        {
            const string key = "EMPTY";
            if (_assetParamDedup.TryGetValue(key, out int ei)) return ei;
            int ni = _file.AssetParams.Count;
            _assetParamDedup[key] = ni;
            _file.AssetParams.Add(new ParamSet { Bitfield = 0, Params = new List<Param>() });
            return ni;
        }

        ulong bitfield = 0;
        foreach (var p in parms)
            bitfield |= 1ul << p.Index;

        string k = $"{bitfield:X16}:" + string.Join(",", parms.Select(ParamKey));
        if (_assetParamDedup.TryGetValue(k, out int existing))
            return existing;

        int idx = _file.AssetParams.Count;
        _assetParamDedup[k] = idx;
        parms.Sort((a, b) => a.Index.CompareTo(b.Index));
        _file.AssetParams.Add(new ParamSet { Bitfield = bitfield, Params = parms });
        return idx;
    }

    private int DeduplicateTriggerParams(List<Param> parms)
    {
        uint bitfield = 0;
        foreach (var p in parms)
            bitfield |= 1u << p.Index;

        string k = $"{bitfield:X8}:" + string.Join(",", parms.Select(ParamKey));
        if (_trigParamDedup.TryGetValue(k, out int existing))
            return existing;

        int idx = _file.TriggerOverwriteParams.Count;
        _trigParamDedup[k] = idx;
        parms.Sort((a, b) => a.Index.CompareTo(b.Index));
        _file.TriggerOverwriteParams.Add(new TriggerParamSet { Bitfield = bitfield, Params = parms });
        return idx;
    }

    private int DeduplicateCondition(Condition cond)
    {
        string k = $"{cond.ParentType}:{cond.SwitchPropType}:{cond.SwitchCompareType}:{cond.SwitchIsGlobal}:" +
                   $"{cond.SwitchEnumValue}:{cond.SwitchValue}:{cond.SwitchEnumName}:" +
                   $"{cond.RandomWeight}:{cond.BlendMin}:{cond.BlendMax}:" +
                   $"{cond.BlendTypeToMax}:{cond.BlendTypeToMin}:{cond.SequenceContinueOnFade}";
        if (_condDedup.TryGetValue(k, out int existing))
            return existing;

        int idx = _file.Conditions.Count;
        _condDedup[k] = idx;
        _file.Conditions.Add(cond);
        return idx;
    }

    // ============================
    // Reconstruction (post-parse)
    // ============================

    private void Reconstruct()
    {
        foreach (var u in _file.Users)
            ReorderAssetCallsBFS(u);

        ReconstructLocalPropertyGlobals();
        ReconstructEnumValues();
        ReconstructSwitchPropertyIndices();
        ReconstructCurveMetadata();
        ReconstructPdtCounts();

        _file.StringTable.Clear();
        _file.PdtStringTable.Clear();
    }

    private static void ReorderAssetCallsBFS(UserData u)
    {
        if (u.AssetCalls.Count == 0) return;

        var childrenOf = new Dictionary<int, List<int>>();
        for (int i = 0; i < u.AssetCalls.Count; i++)
        {
            int parent = u.AssetCalls[i].ParentIndex;
            if (!childrenOf.TryGetValue(parent, out var list))
            {
                list = new List<int>();
                childrenOf[parent] = list;
            }
            list.Add(i);
        }

        var newOrder = new List<int>(u.AssetCalls.Count);

        if (childrenOf.TryGetValue(-1, out var roots))
        {
            foreach (var root in roots)
            {
                var queue = new Queue<int>();
                queue.Enqueue(root);
                while (queue.Count > 0)
                {
                    int idx = queue.Dequeue();
                    newOrder.Add(idx);
                    if (childrenOf.TryGetValue(idx, out var kids))
                        foreach (var k in kids) queue.Enqueue(k);
                }
            }
        }

        if (newOrder.Count != u.AssetCalls.Count) return;

        bool isIdentity = true;
        for (int i = 0; i < newOrder.Count; i++)
        {
            if (newOrder[i] != i) { isIdentity = false; break; }
        }

        if (!isIdentity)
        {
            var oldToNew = new int[u.AssetCalls.Count];
            var newCalls = new List<AssetCall>(u.AssetCalls.Count);
            for (int ni = 0; ni < newOrder.Count; ni++)
            {
                oldToNew[newOrder[ni]] = ni;
                newCalls.Add(u.AssetCalls[newOrder[ni]]);
            }

            for (int i = 0; i < newCalls.Count; i++)
            {
                var call = newCalls[i];
                if (call.ParentIndex >= 0)
                    call.ParentIndex = oldToNew[call.ParentIndex];
                newCalls[i] = call;
            }

            for (int i = 0; i < u.ActionTriggers.Count; i++)
            {
                var t = u.ActionTriggers[i];
                if (t.AssetCallIdx >= 0 && t.AssetCallIdx < oldToNew.Length)
                    t.AssetCallIdx = oldToNew[t.AssetCallIdx];
                u.ActionTriggers[i] = t;
            }
            for (int i = 0; i < u.PropertyTriggers.Count; i++)
            {
                var t = u.PropertyTriggers[i];
                if (t.AssetCallIdx >= 0 && t.AssetCallIdx < oldToNew.Length)
                    t.AssetCallIdx = oldToNew[t.AssetCallIdx];
                u.PropertyTriggers[i] = t;
            }
            for (int i = 0; i < u.AlwaysTriggers.Count; i++)
            {
                var t = u.AlwaysTriggers[i];
                if (t.AssetCallIdx >= 0 && t.AssetCallIdx < oldToNew.Length)
                    t.AssetCallIdx = oldToNew[t.AssetCallIdx];
                u.AlwaysTriggers[i] = t;
            }

            u.AssetCalls.Clear();
            u.AssetCalls.AddRange(newCalls);
        }

        for (int i = 0; i < u.Containers.Count; i++)
        {
            var ctn = u.Containers[i];
            int ctnCallIdx = -1;
            for (int j = 0; j < u.AssetCalls.Count; j++)
            {
                if (u.AssetCalls[j].ContainerParamIdx == i)
                { ctnCallIdx = j; break; }
            }
            if (ctnCallIdx < 0) continue;

            int minChild = int.MaxValue, maxChild = int.MinValue;
            for (int j = 0; j < u.AssetCalls.Count; j++)
            {
                if (u.AssetCalls[j].ParentIndex == ctnCallIdx)
                {
                    minChild = Math.Min(minChild, j);
                    maxChild = Math.Max(maxChild, j);
                }
            }
            if (minChild != int.MaxValue)
            {
                ctn.ChildStartIdx = minChild;
                ctn.ChildEndIdx = maxChild;
            }
            u.Containers[i] = ctn;
        }

        short assetIdx = 0;
        for (int i = 0; i < u.AssetCalls.Count; i++)
        {
            var ac = u.AssetCalls[i];
            ac.AssetIndex = ac.IsContainer ? (short)-1 : assetIdx++;
            u.AssetCalls[i] = ac;
        }
    }

    private void ReconstructLocalPropertyGlobals()
    {
        var globalProps = new HashSet<string>();

        foreach (var u in _file.Users)
            foreach (var lp in u.LocalProperties)
                globalProps.Add(lp);

        _file.LocalProperties = globalProps.OrderBy(s => s, StringComparer.Ordinal).ToList();

        if (_explicitLocalEnums != null)
        {
            _file.LocalPropertyEnumValues = new List<string>(_explicitLocalEnums);
        }
        else
        {
            var globalEnums = new HashSet<string>();
            foreach (var en in _allEnumNames)
                globalEnums.Add(en);
            _file.LocalPropertyEnumValues = globalEnums.OrderBy(s => s, StringComparer.Ordinal).ToList();
        }
    }

    private void ReconstructEnumValues()
    {
        var sorted = _file.LocalPropertyEnumValues;
        for (int i = 0; i < _file.Conditions.Count; i++)
        {
            var cond = _file.Conditions[i];
            if (cond.SwitchPropType == PropType.Enum && cond.SwitchEnumName != null)
            {
                cond.SwitchEnumValue = (short)sorted.IndexOf(cond.SwitchEnumName);
                _file.Conditions[i] = cond;
            }
        }
    }

    private void ReconstructSwitchPropertyIndices()
    {
        foreach (var u in _file.Users)
        {
            for (int ci = 0; ci < u.Containers.Count; ci++)
            {
                var ctn = u.Containers[ci];
                if (ctn.Type != CtnType.Switch || ctn.SwitchPropName == null) continue;

                int localIdx = u.LocalProperties.IndexOf(ctn.SwitchPropName);
                if (localIdx >= 0)
                {
                    ctn.SwitchPropertyIndex = _file.LocalProperties.IndexOf(ctn.SwitchPropName);
                }
                else
                {
                    ctn.SwitchPropertyIndex = -1;
                }
                u.Containers[ci] = ctn;
            }
        }
    }

    private void ReconstructCurveMetadata()
    {
        for (int i = 0; i < _file.CurveTable.Count; i++)
        {
            var c = _file.CurveTable[i];
            int propDefIdx = _file.AssetParamDefs.FindIndex(d => d.Name == c.PropName);
            c.PropIdx = propDefIdx >= 0 ? (uint)propDefIdx : 0;

            bool isGlobal = true;
            int propIdx = -1;

            foreach (var u in _file.Users)
            {
                int localIdx = u.LocalProperties.IndexOf(c.PropName);
                if (localIdx >= 0)
                {
                    isGlobal = false;
                    propIdx = _file.LocalProperties.IndexOf(c.PropName);
                    break;
                }
            }

            c.IsGlobal = (ushort)(isGlobal ? 1 : 0);
            c.PropertyIndex = (short)propIdx;
            c.Padding = 0;
            _file.CurveTable[i] = c;
        }
    }

    private void ReconstructPdtCounts()
    {
        _file.SystemUserParamCount = _file.UserParamDefs.Count;
        _file.SystemAssetParamCount = _file.AssetParamDefs.Count;

        if (_explicitUserAssetParamCount >= 0)
        {
            _file.UserAssetParamCount = _explicitUserAssetParamCount;
            return;
        }

        int userAssetCount = 0;
        bool foundSystem = false;
        for (int i = 0; i < _file.AssetParamDefs.Count; i++)
        {
            if (_file.AssetParamDefs[i].Name == "AssetName")
            {
                foundSystem = true;
            }
            if (foundSystem && _file.AssetParamDefs[i].Type == PType.String &&
                _file.AssetParamDefs[i].Name != "AssetName" && _file.AssetParamDefs[i].Name != "RuntimeAssetName")
            {
                userAssetCount = _file.AssetParamDefs.Count - i;
                break;
            }
        }
        _file.UserAssetParamCount = userAssetCount;
    }

    // ============================
    // Parsing utilities
    // ============================

    private string CollectMultilineValue(string firstLine)
    {
        if (firstLine.EndsWith("{}"))
            return firstLine;

        bool hasOpen = firstLine.Contains('{');
        bool hasClosed = firstLine.Contains('}');

        if (!hasOpen || hasClosed)
            return firstLine;

        var sb = new System.Text.StringBuilder();
        sb.Append(firstLine);
        _pos++;
        while (_pos < _lines.Count)
        {
            string ln = _lines[_pos].Trim();
            sb.Append(' ').Append(ln);
            if (ln == "}" || ln.EndsWith("}"))
            {
                break;
            }
            _pos++;
        }
        return sb.ToString();
    }

    private void ExpectOpenBrace()
    {
        while (_pos < _lines.Count)
        {
            string trimmed = _lines[_pos].Trim();
            _pos++;
            if (trimmed.EndsWith("{")) return;
            if (trimmed.Length == 0 || trimmed.StartsWith('#')) continue;
        }
    }

    private void SkipBlock()
    {
        string first = _lines[_pos].Trim();
        if (first.EndsWith("{}")) { _pos++; return; }
        int depth = 0;
        while (_pos < _lines.Count)
        {
            string t = _lines[_pos].Trim();
            if (t.EndsWith("{") && !t.EndsWith("{}")) depth++;
            if (t == "}") depth--;
            _pos++;
            if (depth <= 0) break;
        }
    }

    private void SkipEmpty()
    {
        while (_pos < _lines.Count)
        {
            string trimmed = _lines[_pos].Trim();
            if (trimmed.Length > 0 && !trimmed.StartsWith('#') && !trimmed.StartsWith("//"))
                break;
            _pos++;
        }
    }

    private string PeekTrimmed()
    {
        int p = _pos;
        while (p < _lines.Count)
        {
            string t = _lines[p].Trim();
            if (t.Length > 0 && !t.StartsWith('#') && !t.StartsWith("//"))
                return t;
            p++;
        }
        return "";
    }

    private static int FindEquals(string s)
    {
        bool inQuote = false;
        int braceDepth = 0;
        for (int i = 0; i < s.Length; i++)
        {
            char c = s[i];
            if (c == '"') inQuote = !inQuote;
            else if (c == '\\' && inQuote && i + 1 < s.Length) i++;
            else if (!inQuote && c == '{') braceDepth++;
            else if (!inQuote && c == '}') braceDepth--;
            else if (c == '=' && !inQuote && braceDepth == 0) return i;
        }
        return -1;
    }

    private static string[] Tokenize(string line)
    {
        var result = new List<string>();
        int i = 0;
        while (i < line.Length)
        {
            while (i < line.Length && line[i] == ' ') i++;
            if (i >= line.Length) break;

            if (line[i] == '"')
            {
                int start = i;
                i++;
                while (i < line.Length)
                {
                    if (line[i] == '\\' && i + 1 < line.Length) i += 2;
                    else if (line[i] == '"') { i++; break; }
                    else i++;
                }
                result.Add(line[start..i]);
            }
            else if (line[i] == '[')
            {
                int start = i;
                i++;
                while (i < line.Length && line[i] != ']') i++;
                if (i < line.Length) i++;
                result.Add(line[start..i]);
            }
            else
            {
                int start = i;
                while (i < line.Length && line[i] != ' ') i++;
                result.Add(line[start..i]);
            }
        }
        return result.ToArray();
    }

    private static string StripBrackets(string s)
    {
        if (s.Length >= 2 && s[0] == '[' && s[^1] == ']')
            return s[1..^1];
        return s;
    }

    private static string Unquote(string s)
    {
        s = s.Trim();
        if (s.Length >= 2 && s[0] == '"' && s[^1] == '"')
        {
            s = s[1..^1];
            var sb = new System.Text.StringBuilder(s.Length);
            for (int i = 0; i < s.Length; i++)
            {
                if (s[i] == '\\' && i + 1 < s.Length)
                {
                    if (s[i + 1] == 'x' && i + 3 < s.Length)
                    {
                        sb.Append((char)int.Parse(s.AsSpan(i + 2, 2), NumberStyles.HexNumber));
                        i += 3;
                    }
                    else { sb.Append(s[i + 1]); i++; }
                }
                else sb.Append(s[i]);
            }
            return sb.ToString();
        }
        return s;
    }

    private static uint ParseHex(string s) =>
        s.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
            ? uint.Parse(s[2..], NumberStyles.HexNumber)
            : uint.Parse(s);

    private static float ParseFloat(string s) =>
        float.Parse(s, NumberStyles.Float, CultureInfo.InvariantCulture);
}
