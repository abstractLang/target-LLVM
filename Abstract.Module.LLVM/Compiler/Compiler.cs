using System.Diagnostics;
using System.Numerics;
using System.Text;
using Abstract.Module.LLVM.Targets;
using Abstract.Realizer.Builder;
using Abstract.Realizer.Builder.Language.Omega;
using Abstract.Realizer.Builder.ProgramMembers;
using Abstract.Realizer.Builder.References;
using Abstract.Realizer.Core.Configuration.LangOutput;
using LLVMSharp.Interop;

namespace Abstract.Module.LLVM.Compiler;

internal partial class LlvmCompiler(LLVMContextRef ctx, TargetsList target)
{
    private ILanguageOutputConfiguration _configuration;
    private TargetsList _target = target;
    
    private Dictionary<BaseFunctionBuilder, (LLVMTypeRef ftype, LLVMValueRef fobj)> _functions = [];
    private Dictionary<StructureBuilder, (LLVMTypeRef type, LLVMValueRef typetbl)> _structures = [];
    private Dictionary<StructureBuilder, VirtualFunctionBuilder?[]> _vtables = [];
    private Dictionary<string, LLVMValueRef> _intrinsincs = [];

    private LLVMModuleRef _llvmModule;
    private LLVMBuilderRef _llvmBuilder;

    private Dictionary<int, LLVMValueRef> _staticBufferMap = [];
    
    
    internal LLVMModuleRef Compile(ProgramBuilder program, ILanguageOutputConfiguration config) 
    {
        _intrinsincs.Clear();
        _functions.Clear();
        _structures.Clear();
        _configuration = config;
        
        _llvmModule = ctx.CreateModuleWithName(program.Modules[0].Symbol);
        _llvmBuilder = ctx.CreateBuilder();

        InitializeIntrinsics();
        foreach (var m in program.Modules) DeclareModuleMembers(m);
        
        // Order matters here
        CompileFunctions();
        CompileStructs();
        
        var ll = _llvmModule.PrintToString();
        File.WriteAllText($".abs-cache/debug/{program.Modules[0].Symbol}.llvmout.ll", ll);
        
        if (!_llvmModule.TryVerify(LLVMVerifierFailureAction.LLVMReturnStatusAction, out var msg))
            File.WriteAllText($".abs-cache/debug/{program.Modules[0].Symbol}.llvmdump.txt", msg);
        
        return _llvmModule;
    }

    private void InitializeIntrinsics()
    {
        LLVMTypeRef type;
        LLVMValueRef func;
        
        switch (_target)
        {
            case TargetsList.Wasm:
            {
                type = LLVMTypeRef.CreateFunction(LlvmInt32, []);
                func = _llvmModule.AddFunction("llvm.wasm.memory.size", type);
                _intrinsincs.Add("wasm.memory.size", func);
                
                type = LLVMTypeRef.CreateFunction(LlvmInt32, [LlvmInt32]);
                func = _llvmModule.AddFunction("llvm.wasm.memory.grow", type);
                _intrinsincs.Add("wasm.memory.grow", func);

            } break;
            default: throw new ArgumentOutOfRangeException();
        }
        
    }

    private void DeclareModuleMembers(ModuleBuilder baseModule)
    {
        // Abstract should have unnested all namespaces!
        foreach (var i in baseModule.Structures) UnwrapStructureHeader(i);
        foreach (var i in baseModule.Functions) UnwrapFunctionHeader(i);
        
        foreach (var i in baseModule.Structures
                     .SelectMany(e => e.Functions)) UnwrapFunctionHeader(i);
    }

    
    private void UnwrapStructureHeader(StructureBuilder struc)
    {
        var st = ctx.CreateNamedStruct(struc.Symbol);
        
        var ttt = ctx.GetStructType([
            LlvmOpaquePtr, // Parent table
            GetNativeInt(), // Type size
            GetNativeInt(), // Type alignment
            CreateSlice(LlvmInt8), // StructName
            GetNativeInt(), // Vtable length
            LlvmArray(LlvmOpaquePtr, struc.VTableSize ?? 0) // Table
        ], false);
        var tt = _llvmModule.AddGlobal(ttt, struc.Symbol + ".vtable");
        _structures.Add(struc, (st, tt));

        if (struc.VTableSize.HasValue)
        {
            var vtlist = new VirtualFunctionBuilder[struc.VTableSize.Value];
            foreach (var virt in struc.Functions.OfType<VirtualFunctionBuilder>())
                if (virt.BytecodeBuilder != null) vtlist[virt.Index] = virt;
            _vtables.Add(struc, vtlist);
        }
    }
    private void UnwrapFunctionHeader(BaseFunctionBuilder baseFunc)
    {
        var argumentTypes = baseFunc.Parameters.Select(e => ConvType(e.type)).ToArray();
        var functype = LlvmFunctionType(ConvType(baseFunc.ReturnType), argumentTypes);

        LLVMValueRef fun = default;
        if (baseFunc is not FunctionBuilder { BytecodeBuilder: null })
        {
            fun = _llvmModule.AddFunction(baseFunc.Symbol, functype);
            
            switch (baseFunc)
            {
                case FunctionBuilder @func:
                {
                    if (func.ExportSymbol != null)
                    {
                        fun.Linkage = LLVMLinkage.LLVMExternalLinkage;
                        fun.DLLStorageClass = LLVMDLLStorageClass.LLVMDLLExportStorageClass;
                        fun.AddTargetDependentFunctionAttr("wasm-export-name", func.ExportSymbol);
                    }
                }
                    break;

                case ImportedFunctionBuilder importedFunc:
                {
                    fun.Linkage = LLVMLinkage.LLVMExternalLinkage;
                    fun.DLLStorageClass = LLVMDLLStorageClass.LLVMDLLImportStorageClass;

                    fun.AddTargetDependentFunctionAttr("wasm-import-module", importedFunc.ImportDomain ?? "env");
                    fun.AddTargetDependentFunctionAttr("wasm-import-name", importedFunc.ImportSymbol!);
                }
                    break;

                default: throw new UnreachableException();
            }
        }

        _functions.Add(baseFunc, (functype, fun));
    }

    
    private void CompileFunctions()
    {
        foreach (var (baseFunction, (llvmFuncType, llvmFunction)) in _functions)
            if (baseFunction is FunctionBuilder @fb && fb.BytecodeBuilder != null) CompileFunction(fb, llvmFunction);
    }
    private void CompileFunction(FunctionBuilder baseFunc, LLVMValueRef llvmFunction)
    {
        var entry = LLVMAppendBasicBlock(llvmFunction, "entry");
        _llvmBuilder.PositionAtEnd(entry);
        
        var args = new LLVMValueRef[llvmFunction.ParamsCount];
        foreach (var (i, (_, type)) in baseFunc.Parameters.Index())
        {
            var paramValue = llvmFunction.GetParam((uint)i);
            if (type is NodeTypeReference { TypeReference: StructureBuilder })
            {
                var local = _llvmBuilder.BuildAlloca(paramValue.TypeOf);
                var store = _llvmBuilder.BuildStore(paramValue, local);
                var align = Math.Max(1, (type.Alignment ?? _configuration.NativeIntegerSize) / _configuration.MemoryUnit);
                local.SetAlignment(align);
                store.SetAlignment(align);
                
                paramValue = local;
            }

            args[i] = paramValue;
        }
        
        var body = new Queue<IOmegaInstruction>((baseFunc.BytecodeBuilder as OmegaBytecodeBuilder 
                                                 ?? throw new Exception("Expected OmegaBytecodeBuilder")).InstructionsList);

        var ctx = new CompileFunctionCtx(null)
        {
            Function = llvmFunction,
            Args = args,
            _selfLocals = [],
            Body = body,
        };
        while (body.Count > 0) CompileFunctionInstruction(ctx);
    }

    
    private void CompileStructs()
    {
        foreach (var (baseStruct, (llvmStruct, tt)) in _structures)
            CompileStruct(baseStruct, llvmStruct, tt);
    }
    private void CompileStruct(StructureBuilder baseStruct, LLVMTypeRef llvmStruct, LLVMValueRef typeTbl)
    {
        List<LLVMTypeRef> fields = [];
        
        fields.Add(LlvmOpaquePtr);
        if (baseStruct.Extends != null)
            fields.AddRange(baseStruct.Extends.Fields.Select(field => ConvType(field.Type!)));
        fields.AddRange(baseStruct.Fields.Select(field => ConvType(field.Type!)));
        
        llvmStruct.StructSetBody(fields.ToArray(), false);
        
        var parentTablePointer = baseStruct.Extends == null
            ? LLVMValueRef.CreateConstPointerNull(LlvmOpaquePtr)
            : _structures[baseStruct.Extends].typetbl;
        
        var selfvtable = _vtables[baseStruct];
        for (var i = 0; i < (baseStruct.VTableSize ?? 0); i++)
        {
            if (selfvtable[i] != null) continue;
            StructureBuilder? curr = baseStruct;
            while (curr != null)
            {
                var tab = _vtables[curr];
                if (tab[i] != null)
                {
                    if (_functions.ContainsKey(tab[i])) selfvtable[i] = tab[i];
                    break;
                }
                curr = curr.Extends;
            }
        }

        List<LLVMValueRef> values = [];
        
        values.AddRange(selfvtable.Select(i => i == null
            ? LLVMValueRef.CreateConstPointerNull(LlvmOpaquePtr)
            : _functions[i].fobj));
        
        typeTbl.Initializer = LLVMValueRef.CreateConstStruct([
            parentTablePointer,
            LLVMValueRef.CreateConstInt(GetNativeInt(), baseStruct.Length!.Value),
            LLVMValueRef.CreateConstInt(GetNativeInt(), baseStruct.Alignment!.Value),
            StoreBufferUtf8(string.Join('.', baseStruct.GlobalIdentifier)),
            LLVMValueRef.CreateConstInt(GetNativeInt(), (ulong)selfvtable.Length),
            LLVMValueRef.CreateConstArray(LlvmOpaquePtr, [..values]),
        ], false);
    }

    
    private void CompileFunctionInstruction(CompileFunctionCtx ctx)
    {
        var a = ctx.Body.Peek();
        switch (a)
        {
            case MacroDefineLocal @deflocal:
            {
                var alloca = _llvmBuilder.BuildAlloca(ConvType(deflocal.Type));
                var align = Math.Max(1, (deflocal.Type.Alignment ?? _configuration.NativeIntegerSize) / _configuration.MemoryUnit);
                alloca.SetAlignment(align);
                
                ctx.Body.Dequeue();
                ctx._selfLocals.Add((deflocal.Type, alloca));
            } break;

            case InstStLocal @stlocal:
            {
                ctx.Body.Dequeue();
                var val = CompileFunctionValueNullable(ctx);
                if (!val.HasValue) return;
                var store = _llvmBuilder.BuildStore(val.Value, ctx.Locals[stlocal.index].ptr);
                var align = Math.Max(1, (ctx.Locals[stlocal.index].type.Alignment ?? _configuration.NativeIntegerSize) / _configuration.MemoryUnit);
                store.SetAlignment(align);
            } break;

            case InstIf:
            {
                ctx.Body.Dequeue();
                var condition = CompileFunctionValue(ctx);

                Queue<IOmegaInstruction> iftrueInsts = [];
                Queue<IOmegaInstruction> iffalseInsts = [];

                while (ctx.Body.Peek() is not InstElse and not InstEnd)
                    iftrueInsts.Enqueue(ctx.Body.Dequeue());

                if (ctx.Body.Dequeue() is not InstEnd)
                {
                    while (ctx.Body.Peek() is not InstEnd)
                        iffalseInsts.Enqueue(ctx.Body.Dequeue());
                    ctx.Body.Dequeue();
                }

                var trueblock = LLVMAppendBasicBlock(ctx.Function, "a");
                var falseblock = iffalseInsts.Count > 0 ? LLVMAppendBasicBlock(ctx.Function, "b") : default;
                var breakblock = LLVMAppendBasicBlock(ctx.Function, "c");

                _llvmBuilder.BuildCondBr(condition, trueblock, falseblock.Handle != 0 ? falseblock : breakblock);
                
                _llvmBuilder.PositionAtEnd(trueblock);
                var subctx = new CompileFunctionCtx(null)
                {
                    Function = ctx.Function,
                    Args = ctx.Args,
                    _selfLocals = [],
                    Body = iftrueInsts,
                };
                while (iftrueInsts.Count > 0) CompileFunctionInstruction(subctx);
                _llvmBuilder.BuildBr(breakblock);

                if (falseblock != null)
                {
                    _llvmBuilder.PositionAtEnd(falseblock);
                    subctx = new CompileFunctionCtx(null)
                    {
                        Function = ctx.Function,
                        Args = ctx.Args,
                        _selfLocals = [],
                        Body = iffalseInsts,
                    };
                    while (iffalseInsts.Count > 0) CompileFunctionInstruction(subctx);
                    _llvmBuilder.BuildBr(breakblock);
                }
                
                _llvmBuilder.PositionAtEnd(breakblock);
            } break;
            
            default:
                CompileFunctionValue(ctx);
                break;
        }
    }

    private LLVMValueRef CompileFunctionValue(CompileFunctionCtx ctx)
    {
        var v = CompileFunctionValueNullable(ctx);
        if (v.HasValue) return v.Value;
        throw new UnreachableException();
    }
    private LLVMValueRef? CompileFunctionValueNullable(CompileFunctionCtx ctx)
    {
        var a = ctx.Body.Dequeue();
        LLVMValueRef val;
        TypeReference? holding = null;
        
        switch (a)
        {
            case InstLdConstIptr @ldconstptr:
                val = LLVMValueRef.CreateConstInt(GetNativeInt(), unchecked((ulong)(Int128)ldconstptr.Value), true);
                break;
            case InstLdConstI @ldconstix:
            {
                val = ldconstix.Len switch
                {
                    1  => LLVMValueRef.CreateConstInt(LlvmBool, unchecked((ulong)(Int128)ldconstix.Value), true),
                    8  => LLVMValueRef.CreateConstInt(LlvmInt8, unchecked((ulong)(Int128)ldconstix.Value), true),
                    16 => LLVMValueRef.CreateConstInt(LlvmInt16, unchecked((ulong)(Int128)ldconstix.Value), true),
                    32 => LLVMValueRef.CreateConstInt(LlvmInt32, unchecked((ulong)(Int128)ldconstix.Value), true),
                    64 => LLVMValueRef.CreateConstInt(LlvmInt64, unchecked((ulong)(Int128)ldconstix.Value), true),
                    _  => LLVMValueRef.CreateConstIntOfArbitraryPrecision(
                        LLVMTypeRef.CreateInt(ldconstix.Len), BigIntegerToULongs(ldconstix.Value, ldconstix.Len)),
                };
                break;
            }

            case InstLdStringUtf8 @str:
                return StoreBufferUtf8(str.Value);
            
            case InstLdLocal @ldlocal:
                if (ldlocal.Local < 0) val = ctx.Args[(-ldlocal.Local) - 1];
                else
                {
                    val = ctx.Locals[ldlocal.Local].ptr;
                    holding = ctx.Locals[ldlocal.Local].type;
                }
                break;
            case InstLdLocalRef @ldlocalref:
                val = ldlocalref.Local < 0
                    ? ctx.Args[(-ldlocalref.Local) - 1]
                    : ctx.Locals[ldlocalref.Local].ptr;
                break;


            case InstLdNewObject @newobj:
            {
                var structRef = newobj.Type;
                var typetype = _structures[structRef].type;
                var typetbl = _structures[structRef].typetbl;

                return LLVMValueRef.CreateConstStruct([typetbl], false);
            }

            case InstRet @r:
                return r.value
                    ? _llvmBuilder.BuildRet(CompileFunctionValue(ctx))
                    : _llvmBuilder.BuildRetVoid();
            
            case FlagTypeInt @tint:
            {
                var ty = new IntegerTypeReference(tint.Signed, tint.Size);
                return CompileFunctionValueTyped(ctx, ty);
            }

            case InstCall @call:
            {
                List<LLVMValueRef> argsList = [];
                (LLVMTypeRef ftype, LLVMValueRef fun) funck = (default, default);

                switch (call.function)
                {
                    case VirtualFunctionBuilder @virt:
                    {
                        var functype = _functions[virt].ftype;
                        var instance = CompileFunctionValue(ctx);
                        
                        argsList.Add(instance);
                        for (var i = 1; i < call.function.Parameters.Count; i++) 
                            argsList.Add(CompileFunctionValue(ctx));

                        var tableType = _structures[(StructureBuilder)virt.Parent!].typetbl.TypeOf;
                        var instanceType = _structures[(StructureBuilder)virt.Parent!].typetbl;

                        throw new NotImplementedException("TODO: make this shit properly works i am getting crzy");
                        
                        //funck = (functype, func);
                        
                    } break;

                    default:
                    {
                        for (var i = 0; i < call.function.Parameters.Count; i++) 
                            argsList.Add(CompileFunctionValue(ctx));

                        funck = BuilderToValueRef(call.function);
                    } break;
                }
                
                val = _llvmBuilder.BuildCall2(
                    funck.ftype,
                    funck.fun,
                    argsList.ToArray());
                
            } break;
            
            default: throw new UnreachableException();
        }
        
        while (ctx.Body.Count > 0)
        {
            var curr = ctx.Body.Peek();
            if (curr is InstLdField)
            {
                val = GetFieldPtr(val, curr, out holding);
                ctx.Body.Dequeue();
            }
            else if (curr is InstStField)
            {
                var ptr = GetFieldPtr(val, curr, out holding);
                ctx.Body.Dequeue();
                var tostore = CompileFunctionValue(ctx);
                val = _llvmBuilder.BuildStore(tostore, ptr);
                var align = Math.Max(1, (holding.Alignment ?? _configuration.NativeIntegerSize) / _configuration.MemoryUnit);
                val.SetAlignment(align);
                holding = null;
                break;
            }
            else break;
        }

        if (holding == null) return val;
        val = _llvmBuilder.BuildLoad2(ConvType(holding), val);
        var align2 = Math.Max(1, (holding.Alignment ?? _configuration.NativeIntegerSize) / _configuration.MemoryUnit);
        val.SetAlignment(align2);
        return val;
    }

    private LLVMValueRef CompileFunctionValueTyped(CompileFunctionCtx ctx, TypeReference ty)
    {
        var a = ctx.Body.Dequeue();
        switch (a)
        {
            case InstAdd: return _llvmBuilder.BuildAdd(CompileFunctionValue(ctx), CompileFunctionValue(ctx));
            
            case InstMul: return _llvmBuilder.BuildMul(CompileFunctionValue(ctx), CompileFunctionValue(ctx));

            case InstAnd: return _llvmBuilder.BuildAnd(CompileFunctionValue(ctx), CompileFunctionValue(ctx));
            case InstOr: return _llvmBuilder.BuildOr(CompileFunctionValue(ctx), CompileFunctionValue(ctx));
            case InstXor: return _llvmBuilder.BuildXor(CompileFunctionValue(ctx), CompileFunctionValue(ctx));
            
            case InstConv: return _llvmBuilder.BuildIntCast(CompileFunctionValue(ctx), ConvType(ty));
            case InstExtend: return (((IntegerTypeReference)ty).Signed)
                    ? _llvmBuilder.BuildSExt(CompileFunctionValue(ctx), ConvType(ty))
                    : _llvmBuilder.BuildZExt(CompileFunctionValue(ctx), ConvType(ty));
            
            case InstTrunc: return _llvmBuilder.BuildTrunc(CompileFunctionValue(ctx), ConvType(ty));
            case InstSigcast: return CompileFunctionValue(ctx); // LLVM handles signess in context
            
            default: throw new UnreachableException();
        }
    }
    
    private LLVMValueRef GetFieldPtr(LLVMValueRef from, IOmegaInstruction access, out TypeReference ptrType)
    {
        StructureBuilder struc;
        uint fidx;
        
        switch (access)
        {
            case InstStField @stfield:
            {
                var field = stfield.StaticField;
                struc = (StructureBuilder)field.Parent!;
                fidx = (uint)struc.Fields.IndexOf(field);
                ptrType = field.Type!;
            } break;
            
            case InstLdField @ldfield:
            {
                var field = ldfield.StaticField;
                struc = (StructureBuilder)field.Parent!;
                fidx = (uint)struc.Fields.IndexOf(field);
                ptrType = field.Type!;
            } break;

            default: throw new UnreachableException();
        }
        
        fidx += 1; // jump signature
        
        return _llvmBuilder.BuildGEP2(BuilderToTypeRef(struc), from, 
        [
            LLVMValueRef.CreateConstInt(LlvmInt64, 0),
            LLVMValueRef.CreateConstInt(LlvmInt32, fidx),
        ]);
    }


    private LLVMTypeRef ConvType(TypeReference? typeref)
    {
        if (typeref == null) return LlvmVoid;
        return typeref switch
        {
            IntegerTypeReference @intt => intt.Bits! switch
            {
                1 => LlvmBool,
                8 => LlvmInt8,
                16 => LlvmInt16,
                32 => LlvmInt32,
                64 => LlvmInt64,
                _ => LlvmInt(intt.Bits ?? _configuration.NativeIntegerSize),
            },
            AnytypeTypeReference => LlvmVoid,
            
            
            NodeTypeReference @nodet => nodet.TypeReference switch
            { 
                StructureBuilder @stb => BuilderToTypeRef(stb),
                TypeDefinitionBuilder => GetNativeInt(),
                _ => throw new UnreachableException(),
            },
            
            SliceTypeReference @slice => CreateSlice(ConvType(slice.Subtype)),
            ReferenceTypeReference @refe => LlvmPtr(ConvType(refe.Subtype)),
            
            _ => throw new UnreachableException()
        };
        
    }
    
    
    private (LLVMTypeRef ftype, LLVMValueRef fun) BuilderToValueRef(BaseFunctionBuilder builder) => _functions[builder];
    private LLVMTypeRef BuilderToTypeRef(StructureBuilder builder) => _structures[builder].type;


    private unsafe LLVMValueRef StoreBufferUtf8(string data)
    {
        var d = Encoding.UTF8.GetBytes(data);
        var alloc = stackalloc ulong[d.Length];
        for (var i = 0; i < d.Length; i++) alloc[i] = d[i];
        var span = new ReadOnlySpan<ulong>(alloc, d.Length);
        
        return StoreStaticBuffer(LlvmInt8, span);
    }
    private LLVMValueRef StoreStaticBuffer(LLVMTypeRef elmtype, ReadOnlySpan<ulong> data) 
    {
        var h = 0; foreach (var i in data) h = HashCode.Combine(h, i);
        
        if (_staticBufferMap.TryGetValue(h, out var dedup)) return dedup;
        
        var arrayType = LLVMTypeRef.CreateArray(elmtype, (uint)data.Length);
        var sliceType = CreateSlice(elmtype);
                
        var global = _llvmModule.AddGlobal(arrayType, $"ro.static.buffer.{_staticBufferMap.Count:0000}");
        var datallvm = new LLVMValueRef[data.Length];
        for (var i = 0; i < data.Length; i++) datallvm[i] = LLVMValueRef.CreateConstInt(elmtype, data[i]);

        global.Alignment = 1;
        global.Initializer = LLVMValueRef.CreateConstArray(elmtype, datallvm);
        
        var gep = LLVMValueRef.CreateConstInBoundsGEP2(
            LLVMTypeRef.CreatePointer(elmtype, 0), global,
            [LLVMValueRef.CreateConstInt(LlvmInt16, 0),
                LLVMValueRef.CreateConstInt(LlvmInt16, 0)]);
                
        var ptr = LLVMValueRef.CreateConstNamedStruct(sliceType, [gep,
            LLVMValueRef.CreateConstInt(GetNativeInt(), (ulong)data.Length)]);

        _staticBufferMap.Add(h, ptr);
        return ptr;
    }
    
    private LLVMTypeRef CreateSlice(LLVMTypeRef elementType) => LLVMTypeRef.CreateStruct([
        LLVMTypeRef.CreatePointer(elementType, 0), GetNativeInt()], false);
    private LLVMTypeRef GetNativeInt() => _configuration.NativeIntegerSize switch
    {
        1 => LlvmBool,
        8 => LlvmInt8,
        16 => LlvmInt16,
        32 => LlvmInt32,
        64 => LlvmInt64,
        _ => LlvmInt(_configuration.NativeIntegerSize),
    }; 
        
    private ulong[] BigIntegerToULongs(BigInteger value, int numBits)
    {
        if (value.Sign < 0)
            throw new ArgumentException("somente positivos suportados");

        int numWords = (numBits + 63) / 64;
        ulong[] words = new ulong[numWords];

        BigInteger remaining = value;
        for (int i = 0; i < numWords; i++)
        {
            words[i] = (ulong)(remaining & 0xFFFFFFFFFFFFFFFF);
            remaining >>= 64;
        }

        return words;
    }
    
    
    private class CompileFunctionCtx(CompileFunctionCtx? parent)
    {
        private CompileFunctionCtx? _parent = parent;
        public LLVMValueRef Function;
        
        public LLVMValueRef[] Args;
        public Queue<IOmegaInstruction> Body;
        
        public List<(TypeReference type, LLVMValueRef ptr)> _selfLocals;
        public (TypeReference type, LLVMValueRef ptr)[] Locals => parent != null
            ? [.. parent.Locals, .. _selfLocals]
            : [.. _selfLocals];
    }
}
