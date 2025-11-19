using System.Diagnostics;
using System.Numerics;
using System.Text;
using LLVMSharp.Interop;
using Tq.Module.LLVM.Targets;
using Tq.Realizer.Builder;
using Tq.Realizer.Builder.Language.Omega;
using Tq.Realizer.Builder.ProgramMembers;
using Tq.Realizer.Builder.References;
using Tq.Realizer.Core.Configuration.LangOutput;
using Tq.Realizer.Core.Intermediate.Values;

namespace Tq.Module.LLVM.Compiler;

internal partial class LlvmCompiler(LLVMContextRef ctx, TargetsList target)
{
    private ILanguageOutputConfiguration _configuration;
    private TargetsList _target = target;
    
    private Dictionary<BaseFunctionBuilder, (LLVMTypeRef ftype, LLVMValueRef fobj)> _functions = [];
    private Dictionary<StaticFieldBuilder, (LLVMTypeRef ftype, LLVMValueRef fobj)> _staticFields = [];
    private Dictionary<StructureBuilder, (LLVMTypeRef type, LLVMValueRef typetbl)> _structures = [];
    private Dictionary<string, BaseFunctionBuilder> _exportMap = [];
    
    private Dictionary<StructureBuilder, VirtualFunctionBuilder?[]> _vtables = [];
    private Dictionary<string, LLVMValueRef> _intrinsincs = [];

    private LLVMModuleRef _llvmModule;
    private LLVMBuilderRef _llvmBuilder;

    private uint metanameCount = 0;
    private Dictionary<int, LLVMValueRef> _staticMap = [];

    
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


        type = LlvmFunctionType(LlvmStructType([LlvmInt8, LlvmBool], false), [LlvmInt8, LlvmInt8]);
        func = _llvmModule.AddFunction("llvm.sadd.with.overflow.i8", type);
        _intrinsincs.Add("sadd.w.ovf.i8", func);
        func = _llvmModule.AddFunction("llvm.uadd.with.overflow.i8", type);
        _intrinsincs.Add("uadd.w.ovf.i8", func);
        
        type = LlvmFunctionType(LlvmStructType([LlvmInt16, LlvmBool], false), [LlvmInt16, LlvmInt16]);
        func = _llvmModule.AddFunction("llvm.sadd.with.overflow.i16", type);
        _intrinsincs.Add("sadd.w.ovf.i16", func);
        func = _llvmModule.AddFunction("llvm.uadd.with.overflow.i16", type);
        _intrinsincs.Add("uadd.w.ovf.i16", func);
        
        type = LlvmFunctionType(LlvmStructType([LlvmInt32, LlvmBool], false), [LlvmInt32, LlvmInt32]);
        func = _llvmModule.AddFunction("llvm.sadd.with.overflow.i32", type);
        _intrinsincs.Add("sadd.w.ovf.i32", func);
        func = _llvmModule.AddFunction("llvm.uadd.with.overflow.i32", type);
        _intrinsincs.Add("uadd.w.ovf.i32", func);
        
        type = LlvmFunctionType(LlvmStructType([LlvmInt64, LlvmBool], false), [LlvmInt64, LlvmInt64]);
        func = _llvmModule.AddFunction("llvm.sadd.with.overflow.i64", type);
        _intrinsincs.Add("sadd.w.ovf.i64", func);
        func = _llvmModule.AddFunction("llvm.uadd.with.overflow.i64", type);
        _intrinsincs.Add("uadd.w.ovf.i64", func);
        
        switch (_target)
        {
            case TargetsList.Wasm:
            {
                type = LlvmFunctionType(LlvmInt32, []);
                func = _llvmModule.AddFunction("llvm.wasm.memory.size", type);
                _intrinsincs.Add("wasm.memory.size", func);
                
                type = LlvmFunctionType(LlvmInt32, [LlvmInt32]);
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
        foreach (var i in baseModule.Fields) UnwrapStaticFieldHeader(i);
        foreach (var i in baseModule.Functions) UnwrapFunctionHeader(i);
        
        foreach (var i in baseModule.Structures
                     .SelectMany(e => e.Functions)) UnwrapFunctionHeader(i);
    }

    private void UnwrapStaticFieldHeader(StaticFieldBuilder field)
    {
        var t = ConvType(field.Type);
        var gb = _llvmModule.AddGlobal(t, field.Symbol);
        gb.Linkage = LLVMLinkage.LLVMInternalLinkage;
        
        if (field.Initializer != null) gb.Initializer = Const2Llvm(field.Initializer).v;
        
        _staticFields.Add(field, (t, gb));
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
            LlvmArray(LlvmOpaquePtr, 0) // Table
        ], false);
        var tt = _llvmModule.AddGlobal(ttt, struc.Symbol + ".vtable");
        _structures.Add(struc, (st, tt));
        unsafe {LLVMSharp.Interop.LLVM.SetGlobalConstant(tt, 1);}

        // if (struc.VTableSize.HasValue)
        // {
        //     var vtlist = new VirtualFunctionBuilder[struc.VTableSize.Value];
        //     foreach (var virt in struc.Functions.OfType<VirtualFunctionBuilder>())
        //         if (virt.CodeBlocks.Count == 0) vtlist[virt.Index] = virt;
        //     _vtables.Add(struc, vtlist);
        // }
    }
    private void UnwrapFunctionHeader(BaseFunctionBuilder baseFunc)
    {
        var argumentTypes = baseFunc.Parameters.Select(e => ConvType(e.type)).ToArray();
        var functype = LlvmFunctionType(ConvType(baseFunc.ReturnType), argumentTypes);

        switch (baseFunc)
        {
            case VirtualFunctionBuilder { CodeBlocks.Count: 0 }:
                _functions.Add(baseFunc, (functype, default));
                return;
            
            case FunctionBuilder { ExportSymbol: not null } @f:
                _exportMap.Add(f.ExportSymbol, f);
                break;
        }

        var fun = _llvmModule.AddFunction(baseFunc.Symbol, functype);
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
        _functions.Add(baseFunc, (functype, fun));
        
    }

    
    private void CompileFunctions()
    {
        foreach (var (baseFunction, (llvmFuncType, llvmFunction)) in _functions)
            if (baseFunction is FunctionBuilder { CodeBlocks.Count: > 0 } @fb) CompileFunction(fb, llvmFunction);
    }
    private void CompileFunction(FunctionBuilder baseFunc, LLVMValueRef llvmFunction)
    {
        var args = llvmFunction.GetParams();
        
        var codeBlocks = new (OmegaBlockBuilder baseBlock, LLVMBasicBlockRef llvmBlock)[baseFunc.CodeBlocks.Count];
        foreach (var (i, block) in baseFunc.CodeBlocks.Index())
        {
            if (block is not OmegaBlockBuilder @omega) throw new Exception("Expected OmegaBytecodeBuilder");
            
            var llvmblock = LLVMAppendBasicBlock(llvmFunction, block.Name);
            codeBlocks[i] = (omega, llvmblock);
        }

        if (codeBlocks.Length > 0)
        {
            _llvmBuilder.PositionAtEnd(codeBlocks[0].llvmBlock);
            foreach (var (i, (_, type)) in baseFunc.Parameters.Index())
            {
                var paramValue = llvmFunction.GetParam((uint)i);
                if (type is NodeTypeReference { TypeReference: StructureBuilder @struc })
                {
                    var local = _llvmBuilder.BuildAlloca(paramValue.TypeOf);
                    var store = _llvmBuilder.BuildStore(paramValue, local);
                    var align = AlignOf(struc);
                    local.SetAlignment(align);
                    store.SetAlignment(align);
                
                    paramValue = local;
                }

                args[i] = paramValue;
            }
        }

        List<(TypeReference type, LLVMValueRef ptr)> locals = [];

        var count = 0;
        foreach (var (baseblock, llvmblock) in codeBlocks)
        {
            var body = new Queue<IOmegaInstruction>(baseblock.InstructionsList);
            var ctx = new CompileCodeBlockCtx {
               RealizerFunction = baseFunc,
               LlvmFunction = llvmFunction,
               BlockMap = codeBlocks,
               Args = args,
               Locals = locals,
               Body = body,
            };

            _llvmBuilder.PositionAtEnd(llvmblock);
            while (body.Count > 0) CompileCodeBlockInstruction(ctx);
        }
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
        
        List<LLVMValueRef> values = [];

        var identifier = string.Join('.', baseStruct.GlobalIdentifier);
        
        typeTbl.Initializer = LLVMValueRef.CreateConstStruct([
            parentTablePointer,
            LLVMValueRef.CreateConstInt(GetNativeInt(), baseStruct.Length!.Value),
            LLVMValueRef.CreateConstInt(GetNativeInt(), baseStruct.Alignment!.Value),
            LLVMValueRef.CreateConstStruct([
                StoreMetadataStructName(identifier),
                LLVMValueRef.CreateConstInt(GetNativeInt(), (ulong)identifier.Length),
                ], false),
            LLVMValueRef.CreateConstInt(GetNativeInt(), 0),
            LLVMValueRef.CreateConstArray(LlvmOpaquePtr, [])
        ], false);
    }

    
    private void CompileCodeBlockInstruction(CompileCodeBlockCtx ctx)
    {
        var a = ctx.Body.Peek();
        switch (a)
        {
            case MacroDefineLocal @deflocal:
            {
                var alloca = _llvmBuilder.BuildAlloca(ConvType(deflocal.Type));
                alloca.SetAlignment(AlignOf(deflocal.Type));
                
                ctx.Body.Dequeue();
                ctx.Locals.Add((deflocal.Type, alloca));
            } break;

            case InstStLocal @stlocal:
            {
                ctx.Body.Dequeue();
                var val = CompileCodeBlockValueNullable(ctx);
                if (!val.HasValue) return;
                var store = _llvmBuilder.BuildStore(val.Value, ctx.Locals[stlocal.index].ptr);
                store.SetAlignment(AlignOf(ctx.Locals[stlocal.index].type));
            } break;
            
            case InstStStaticField @stfield:
            {
                ctx.Body.Dequeue();
                var val = CompileCodeBlockValueNullable(ctx);
                if (!val.HasValue) return;
                var store = _llvmBuilder.BuildStore(val.Value,BuilderToValueRef(stfield.StaticField).fld);
                store.SetAlignment(AlignOf(stfield.StaticField.Type));
            } break;

            case InstBranch @branch:
            {
                ctx.Body.Dequeue();
                var toblock = ctx.BlockMap[branch.To];
                _llvmBuilder.BuildBr(toblock.llvmb);
            } break;

            case InstBranchIf @branchif:
            {
                ctx.Body.Dequeue();
                var condition = CompileCodeBlockValue(ctx);

                var trueblock = ctx.BlockMap[branchif.IfTrue];
                var falseblock = ctx.BlockMap[branchif.IfFalse];
                
                _llvmBuilder.BuildCondBr(condition, trueblock.llvmb, falseblock.llvmb);
            } break;
            
            default:
                CompileCodeBlockValue(ctx);
                break;
        }
    }

    private LLVMValueRef CompileCodeBlockValue(CompileCodeBlockCtx ctx)
    {
        var v = CompileCodeBlockValueNullable(ctx);
        if (v.HasValue) return v.Value;
        throw new UnreachableException();
    }
    private LLVMValueRef? CompileCodeBlockValueNullable(CompileCodeBlockCtx ctx)
    {
        var a = ctx.Body.Dequeue();
        LLVMValueRef val;
        TypeReference? holding = null;
        
        switch (a)
        {
            case InstLdConst @co:
                return Const2Llvm(co.Value).v;

            case InstLdSlice @slice:
            {
                var data = ctx.RealizerFunction.DataBlocks[slice.Index];
                return Const2Llvm(data).v;
            } break;
            
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

            case InstLdStaticField @ldfld:
            {
                var r = BuilderToValueRef(ldfld.StaticField);
                val = r.fld;
                holding = ldfld.StaticField.Type;
            } break;

            case InstLdNewObject @newobj:
            {
                var structRef = newobj.Type;
                var typetype = _structures[structRef].type;
                var typetbl = _structures[structRef].typetbl;

                return LLVMValueRef.CreateConstStruct([typetbl], false);
            }

            case InstRet @r:
                return r.value
                    ? _llvmBuilder.BuildRet(CompileCodeBlockValue(ctx))
                    : _llvmBuilder.BuildRetVoid();
            
            case FlagTypeInt @tint:
            {
                var ty = new IntegerTypeReference(tint.Signed, tint.Size);
                return CompileCodeBlockValueTyped(ctx, ty);
            }
            case FlagTypeReference:
                return CompileCodeBlockValueTyped(ctx, new ReferenceTypeReference(null));
            
            case InstCall @call:
            {
                List<LLVMValueRef> argsList = [];
                (LLVMTypeRef ftype, LLVMValueRef fun) funck = (default, default);

                switch (call.function)
                {
                    case VirtualFunctionBuilder @virt:
                    {
                        var functype = _functions[virt].ftype;
                        var instance = CompileCodeBlockValue(ctx);
                        
                        argsList.Add(instance);
                        for (var i = 1; i < call.function.Parameters.Count; i++) 
                            argsList.Add(CompileCodeBlockValue(ctx));

                        var tableType = _structures[(StructureBuilder)virt.Parent!].typetbl.TypeOf;
                        var instanceType = _structures[(StructureBuilder)virt.Parent!].typetbl;

                        throw new NotImplementedException("TODO: make this shit properly works i am getting crzy");
                        
                        //funck = (functype, func);
                        
                    } break;

                    default:
                    {
                        for (var i = 0; i < call.function.Parameters.Count; i++) 
                            argsList.Add(CompileCodeBlockValue(ctx));

                        funck = BuilderToValueRef(call.function);
                    } break;
                }
                
                val = _llvmBuilder.BuildCall2(funck.ftype, funck.fun, argsList.ToArray());
            } break;
            
            
            case InstLdField:
            case InstStField: 
                throw new Exception();
            
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
                var tostore = CompileCodeBlockValue(ctx);
                val = _llvmBuilder.BuildStore(tostore, ptr);
                val.SetAlignment(AlignOf(holding));
                holding = null;
                break;
            }
            else break;
        }

        if (holding == null) return val;
        val = _llvmBuilder.BuildLoad2(ConvType(holding), val);
        val.SetAlignment(AlignOf(holding));
        return val;
    }

    private LLVMValueRef CompileCodeBlockValueTyped(CompileCodeBlockCtx ctx, TypeReference ty)
    {
        var a = ctx.Body.Dequeue();
        switch (a)
        {
            case InstAdd: return _llvmBuilder.BuildAdd(CompileCodeBlockValue(ctx), CompileCodeBlockValue(ctx));
            case InstSub: return _llvmBuilder.BuildSub(CompileCodeBlockValue(ctx), CompileCodeBlockValue(ctx));
            case InstMul: return _llvmBuilder.BuildMul(CompileCodeBlockValue(ctx), CompileCodeBlockValue(ctx));

            case InstAnd: return _llvmBuilder.BuildAnd(CompileCodeBlockValue(ctx), CompileCodeBlockValue(ctx));
            case InstOr: return _llvmBuilder.BuildOr(CompileCodeBlockValue(ctx), CompileCodeBlockValue(ctx));
            case InstXor: return _llvmBuilder.BuildXor(CompileCodeBlockValue(ctx), CompileCodeBlockValue(ctx));
            
            case InstCmpEq: return _llvmBuilder.BuildICmp(LLVMIntPredicate.LLVMIntEQ,
                    CompileCodeBlockValue(ctx), CompileCodeBlockValue(ctx));
            case InstCmpNeq: return _llvmBuilder.BuildICmp(LLVMIntPredicate.LLVMIntNE,
                    CompileCodeBlockValue(ctx), CompileCodeBlockValue(ctx));
            case InstCmpGr: return _llvmBuilder.BuildICmp(((IntegerTypeReference)ty).Signed
                ? LLVMIntPredicate.LLVMIntSGT : LLVMIntPredicate.LLVMIntUGT,
                    CompileCodeBlockValue(ctx), CompileCodeBlockValue(ctx));
            case InstCmpGe: return _llvmBuilder.BuildICmp(((IntegerTypeReference)ty).Signed
                ? LLVMIntPredicate.LLVMIntSGE : LLVMIntPredicate.LLVMIntUGE,
                    CompileCodeBlockValue(ctx), CompileCodeBlockValue(ctx));
            case InstCmpLr: return _llvmBuilder.BuildICmp(((IntegerTypeReference)ty).Signed
                ? LLVMIntPredicate.LLVMIntSLT : LLVMIntPredicate.LLVMIntULT,
                    CompileCodeBlockValue(ctx), CompileCodeBlockValue(ctx));
            case InstCmpLe: return _llvmBuilder.BuildICmp(((IntegerTypeReference)ty).Signed
                ? LLVMIntPredicate.LLVMIntSLE : LLVMIntPredicate.LLVMIntULE,
                    CompileCodeBlockValue(ctx), CompileCodeBlockValue(ctx));
            
            case InstConv: return _llvmBuilder.BuildIntCast(CompileCodeBlockValue(ctx), ConvType(ty));
            case InstExtend: return (((IntegerTypeReference)ty).Signed)
                    ? _llvmBuilder.BuildSExt(CompileCodeBlockValue(ctx), ConvType(ty))
                    : _llvmBuilder.BuildZExt(CompileCodeBlockValue(ctx), ConvType(ty));
            
            case InstTrunc: return _llvmBuilder.BuildTrunc(CompileCodeBlockValue(ctx), ConvType(ty));
            case InstSigcast: return CompileCodeBlockValue(ctx); // LLVM handles signess in context
            
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
            
            NoreturnTypeReference or
            AnytypeTypeReference => LlvmVoid,
            
            NodeTypeReference @nodet => nodet.TypeReference switch
            { 
                StructureBuilder @stb => BuilderToTypeRef(stb),
                TypedefBuilder => GetNativeInt(),
                _ => throw new UnreachableException(),
            },
            
            SliceTypeReference @slice => CreateSlice(ConvType(slice.Subtype)),
            ReferenceTypeReference @refe => LlvmOpaquePtr,
            NullableTypeReference @refe => refe.Subtype is ReferenceTypeReference
                    ? LlvmOpaquePtr
                    : LlvmStructType([ConvType(refe.Subtype), LlvmBool], true),
            
            _ => throw new UnreachableException()
        };
        
    }

    private uint BitsToMemUnit(uint? size) => Math.Max(1, (size ?? _configuration.NativeIntegerSize) / _configuration.MemoryUnit);

    private uint AlignOf(StructureBuilder struc) => BitsToMemUnit(@struc.Alignment);
    private uint AlignOf(TypeReference? type)
    {
        if (type == null) return 0;
        return type switch
        {
            NodeTypeReference { TypeReference: StructureBuilder @struct } => BitsToMemUnit(@struct.Alignment),
            NodeTypeReference { TypeReference: TypedefBuilder @typedef } => AlignOf(typedef.BackingType),
            
            IntegerTypeReference @intt => BitsToMemUnit(@intt.Bits),
            ReferenceTypeReference or SliceTypeReference => _configuration.NativeIntegerSize,
            AnytypeTypeReference => throw new Exception("Anytype should already had ben solved"),
            
            _ => throw new UnreachableException(),
        };
    }
    
    private (LLVMTypeRef ftype, LLVMValueRef fun) BuilderToValueRef(BaseFunctionBuilder builder) => _functions[builder];
    private (LLVMTypeRef ftype, LLVMValueRef fld) BuilderToValueRef(StaticFieldBuilder builder) => _staticFields[builder];
    private LLVMTypeRef BuilderToTypeRef(StructureBuilder builder) => _structures[builder].type;


    private unsafe LLVMValueRef StoreMetadataStructName(string value)
    {
        var bytes = Encoding.ASCII.GetBytes(value);
        var spanptr = stackalloc LLVMValueRef[bytes.Length];
        var span = new Span<LLVMValueRef>(spanptr, bytes.Length);
        for (var i = 0; i < bytes.Length; i++) span[i] = LLVMValueRef.CreateConstInt(LlvmInt8, bytes[i]);
        
        var llvmArrType = LLVMTypeRef.CreateArray2(LlvmInt8, (ulong)bytes.Length);
        var g = _llvmModule.AddGlobal(llvmArrType, $"ro.meta.name.{metanameCount++:x}");
        LlvmSetGlobalConst(g, true);
        g.Initializer = LLVMValueRef.CreateConstArray(LlvmInt8, span);

        return g;
    }

    private LLVMValueRef StoreGlobalArray(LLVMTypeRef elmtype, RealizerConstantValue[] data, bool isConstant) 
    {
        var h = data.Aggregate(0, HashCode.Combine);

        if (_staticMap.TryGetValue(h, out var dedup)) return dedup;
        
        var arrayType = LlvmArray(elmtype, (uint)data.Length);
        var sliceType = CreateSlice(elmtype);
                
        var global = _llvmModule.AddGlobal(arrayType, $"ro.static.buffer.{_staticMap.Count:0000}");
        LlvmSetGlobalConst(global, isConstant);
        var datallvm = new LLVMValueRef[data.Length];
        for (var i = 0; i < data.Length; i++) datallvm[i] = Const2Llvm(data[i]).v;

        global.Alignment = 1;
        global.Initializer = LLVMValueRef.CreateConstArray(elmtype, datallvm);
        
        var gep = LLVMValueRef.CreateConstInBoundsGEP2(
            LLVMTypeRef.CreatePointer(elmtype, 0), global,
            [LLVMValueRef.CreateConstInt(LlvmInt16, 0),
                LLVMValueRef.CreateConstInt(LlvmInt16, 0)]);
                
        var ptr = LLVMValueRef.CreateConstNamedStruct(sliceType, [gep,
            LLVMValueRef.CreateConstInt(GetNativeInt(), (ulong)data.Length)]);

        _staticMap.Add(h, ptr);
        return ptr;
    }
    private (LLVMValueRef v, LLVMTypeRef t) Const2Llvm(RealizerConstantValue val)
    {
        switch (val)
        {
            case NullConstantValue:
                return (LLVMValueRef.CreateConstNull(LlvmOpaquePtr), LlvmOpaquePtr);
            
            case IntegerConstantValue i:
            {
                var value = unchecked((ulong)(Int128)i.Value);
                
                return i.BitSize switch
                {
                    0 => (LLVMValueRef.CreateConstInt(GetNativeInt(), value, true), LlvmBool),
                    1  => (LLVMValueRef.CreateConstInt(LlvmBool, value, true), LlvmBool),
                    8  => (LLVMValueRef.CreateConstInt(LlvmInt8, value, true), LlvmInt8),
                    16 => (LLVMValueRef.CreateConstInt(LlvmInt16, value, true), LlvmInt16),
                    32 => (LLVMValueRef.CreateConstInt(LlvmInt32, value, true), LlvmInt32),
                    64 => (LLVMValueRef.CreateConstInt(LlvmInt64, value, true), LlvmInt64),
                    
                    _  => (LLVMValueRef.CreateConstIntOfArbitraryPrecision(
                            LlvmInt(i.BitSize), BigIntegerToULongs(i.Value, i.BitSize)),
                        LlvmInt(i.BitSize)),
                };
            }

            case SliceConstantValue slice:
            {
                var elmtype = ConvType(slice.ElementType);
                
                var v = StoreGlobalArray(elmtype, slice.Content, true);
                var t = LLVMTypeRef.CreateArray(elmtype, (uint)slice.Content.Length);

                return (v, t);
            }
            
            default: throw new UnreachableException();
        }
    }
    
    private LLVMTypeRef CreateSlice(LLVMTypeRef elementType) => LLVMTypeRef.CreateStruct([
        LlvmPtr(elementType), GetNativeInt()], false);
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
    
    
    private class CompileCodeBlockCtx()
    {
        public FunctionBuilder RealizerFunction;
        public LLVMValueRef LlvmFunction;
        public (OmegaBlockBuilder baseb, LLVMBasicBlockRef llvmb)[] BlockMap;
        
        public LLVMValueRef[] Args;
        public List<(TypeReference type, LLVMValueRef ptr)> Locals;
        public Queue<IOmegaInstruction> Body;
        
    }
}
