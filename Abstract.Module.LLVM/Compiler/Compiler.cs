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

internal class LlvmCompiler(LLVMContextRef ctx, TargetsList target)
{
    private ILanguageOutputConfiguration configuration;
    private TargetsList target = target;
    
    private Dictionary<BaseFunctionBuilder, (LLVMTypeRef ftype, LLVMValueRef fobj)> functions = [];
    private Dictionary<StructureBuilder, (LLVMTypeRef type, LLVMValueRef typetbl)> structures = [];
    private Dictionary<string, LLVMValueRef> intrinsincs = [];
    
    private readonly LLVMContextRef llvmContext = ctx;
    private LLVMModuleRef llvmModule;
    private LLVMBuilderRef llvmBuilder;

    private Dictionary<int, LLVMValueRef> staticBufferMap = [];
    
    internal LLVMModuleRef Compile(ProgramBuilder program, ILanguageOutputConfiguration config) 
    {
        intrinsincs.Clear();
        functions.Clear();
        structures.Clear();
        configuration = config;
        
        llvmModule = llvmContext.CreateModuleWithName(program.Modules[0].Symbol);
        llvmBuilder = llvmContext.CreateBuilder();

        InitializeIntrinsics();
        foreach (var m in program.Modules) DeclareModuleMembers(m);
        
        CompileFunctions();
        CompileStructs();
        
        var ll = llvmModule.PrintToString();
        File.WriteAllText($".abs-cache/debug/{program.Modules[0].Symbol}.llvmout.ll", ll);

        if (!llvmModule.TryVerify(LLVMVerifierFailureAction.LLVMReturnStatusAction, out var msg))
            File.WriteAllText($".abs-cache/debug/{program.Modules[0].Symbol}.llvmdump.txt", msg);
        return llvmModule;
    }

    private void InitializeIntrinsics()
    {
        LLVMTypeRef type;
        LLVMValueRef func;
        
        switch (target)
        {
            case TargetsList.Wasm:
            {
                type = LLVMTypeRef.CreateFunction(LLVMTypeRef.Int32, []);
                func = llvmModule.AddFunction("llvm.wasm.memory.size", type);
                intrinsincs.Add("wasm.memory.size", func);
                
                type = LLVMTypeRef.CreateFunction(LLVMTypeRef.Int32, [LLVMTypeRef.Int32]);
                func = llvmModule.AddFunction("llvm.wasm.memory.grow", type);
                intrinsincs.Add("wasm.memory.grow", func);

            } break;
            default: throw new ArgumentOutOfRangeException();
        }
        
    }

    private void DeclareModuleMembers(ModuleBuilder baseModule)
    {
        // Abstract should have unnested all namespaces!
        foreach (var i in baseModule.Structures) UnwrapStructureHeader(i);
        foreach (var i in baseModule.Functions) UnwrapFunctionHeader(i);
    }

    
    private void UnwrapStructureHeader(StructureBuilder struc)
    {
        var st = llvmContext.CreateNamedStruct(struc.Symbol);
        
        var ttt = LLVMTypeRef.CreateStruct([
            LLVMTypeRef.CreatePointer(GetNativeInt(), 0), // Parent table
            GetNativeInt(), // Type size
            CreateSlice(LLVMTypeRef.Int8), // StructName
            LLVMTypeRef.CreatePointer(GetNativeInt(), 0) // Vtable
        ], true);
        var tt = llvmModule.AddGlobal(ttt, struc.Symbol + ".vtable");
        structures.Add(struc, (st, tt));
    }
    private void UnwrapFunctionHeader(BaseFunctionBuilder baseFunc)
    {
        if (baseFunc is AbstractFunctionBuilder) return;
        
        var argumentTypes = baseFunc.Parameters.Select(e => ConvType(e.type)).ToArray();
        var functype = LLVMTypeRef.CreateFunction(ConvType(baseFunc.ReturnType), argumentTypes);
        
        var fun = llvmModule.AddFunction(baseFunc.Symbol, functype);
        
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
            } break;

            case ImportedFunctionBuilder importedFunc:
            {
                fun.Linkage = LLVMLinkage.LLVMExternalLinkage;
                fun.DLLStorageClass = LLVMDLLStorageClass.LLVMDLLImportStorageClass;
                
                fun.AddTargetDependentFunctionAttr("wasm-import-module", importedFunc.ImportDomain ?? "env");
                fun.AddTargetDependentFunctionAttr("wasm-import-name", importedFunc.ImportSymbol!);
            } break;
            
            default: throw new UnreachableException();
        }
        
        functions.Add(baseFunc, (functype, fun));
    }


    private void CompileStructs()
    {
        foreach (var (baseStruct, (llvmStruct, tt)) in structures)
            CompileStruct(baseStruct, llvmStruct, tt);
    }
    private void CompileStruct(StructureBuilder baseStruct, LLVMTypeRef llvmStruct, LLVMValueRef typeTbl)
    {
        List<LLVMTypeRef> fields = [];
        
        fields.Add(LLVMTypeRef.CreatePointer(LLVMTypeRef.Void, 0));
        if (baseStruct.Extends != null) fields.Add(BuilderToTypeRef(baseStruct.Extends));
        fields.AddRange(baseStruct.Fields.Select(field => ConvType(field.Type!)));
        
        llvmStruct.StructSetBody(fields.ToArray(), true);
        
        
        LLVMValueRef parentTablePointer = baseStruct.Extends == null
            ? LLVMValueRef.CreateConstPointerNull(LLVMTypeRef.CreatePointer(LLVMTypeRef.Void, 0))
            : structures[baseStruct.Extends].typetbl;

        
        typeTbl.Initializer = LLVMValueRef.CreateConstStruct([
            parentTablePointer,
            LLVMValueRef.CreateConstInt(GetNativeInt(), baseStruct.Length!.Value),
            StoreBufferUtf8(string.Join('.', baseStruct.GlobalIdentifier)),
        ], true);
    }
    
    private void CompileFunctions()
    {
        foreach (var (baseFunction, (llvmFuncType, llvmFunction)) in functions)
        {
            if (baseFunction is not FunctionBuilder @fb) continue;
            CompileFunction(fb, llvmFunction);
        }
    }
    private void CompileFunction(FunctionBuilder baseFunc, LLVMValueRef llvmFunction)
    {
        var entry = llvmFunction.AppendBasicBlock("entry");
        llvmBuilder.PositionAtEnd(entry);
        
        var args = new LLVMValueRef[llvmFunction.ParamsCount];
        foreach (var (i, (_, type)) in baseFunc.Parameters.Index())
        {
            var paramValue = llvmFunction.GetParam((uint)i);
            if (type is NodeTypeReference { TypeReference: StructureBuilder })
            {
                var local = llvmBuilder.BuildAlloca(paramValue.TypeOf);
                var store = llvmBuilder.BuildStore(paramValue, local);
                local.SetAlignment((type.Alignment ?? configuration.NativeIntegerSize) / configuration.MemoryUnit);
                store.SetAlignment((type.Alignment ?? configuration.NativeIntegerSize) / configuration.MemoryUnit);
                
                paramValue = local;
            }

            args[i] = paramValue;
        }
        
        List<(TypeReference, LLVMValueRef)> locals = [];
        var body = new Queue<IOmegaInstruction>((baseFunc.BytecodeBuilder as OmegaBytecodeBuilder 
                                                 ?? throw new Exception("Expected OmegaBytecodeBuilder")).InstructionsList);

        var ctx = new CompileFunctionCtx
        {
            args = args,
            locals = locals,
            body = body,
        };
        
        while (body.Count > 0) CompileFunctionInstruction(ctx);
    }

    private void CompileFunctionInstruction(CompileFunctionCtx ctx)
    {
        var a = ctx.body.Peek();
        switch (a)
        {
            case MacroDefineLocal @deflocal:
            {
                var alloca = llvmBuilder.BuildAlloca(ConvType(deflocal.Type));
                alloca.SetAlignment((deflocal.Type.Alignment ?? configuration.NativeIntegerSize) / configuration.MemoryUnit);
                
                ctx.body.Dequeue();
                ctx.locals.Add((deflocal.Type, alloca));
            } break;

            case InstStLocal @stlocal:
            {
                ctx.body.Dequeue();
                var val = CompileFunctionValueNullable(ctx);
                if (!val.HasValue) return;
                var store = llvmBuilder.BuildStore(val.Value, ctx.locals[stlocal.index].ptr);
                store.SetAlignment((ctx.locals[stlocal.index].type.Alignment ?? configuration.NativeIntegerSize)
                                   / configuration.MemoryUnit);
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
        var a = ctx.body.Dequeue();
        LLVMValueRef val;
        TypeReference? holding = null;
        
        switch (a)
        {
            case InstLdConstIptr: throw new Exception("iptrs should've already been reassigned to a valid integer size");
            case InstLdConstI @ldconstix:
            {
                val = ldconstix.Len switch
                {
                    1  => LLVMValueRef.CreateConstInt(LLVMTypeRef.Int1, unchecked((ulong)(Int128)ldconstix.Value), true),
                    8  => LLVMValueRef.CreateConstInt(LLVMTypeRef.Int8, unchecked((ulong)(Int128)ldconstix.Value), true),
                    16 => LLVMValueRef.CreateConstInt(LLVMTypeRef.Int16, unchecked((ulong)(Int128)ldconstix.Value), true),
                    32 => LLVMValueRef.CreateConstInt(LLVMTypeRef.Int32, unchecked((ulong)(Int128)ldconstix.Value), true),
                    64 => LLVMValueRef.CreateConstInt(LLVMTypeRef.Int64, unchecked((ulong)(Int128)ldconstix.Value), true),
                    _  => LLVMValueRef.CreateConstIntOfArbitraryPrecision(
                        LLVMTypeRef.CreateInt(ldconstix.Len), BigIntegerToULongs(ldconstix.Value, ldconstix.Len)),
                };
                break;
            }

            case InstLdStringUtf8 @str:
                return StoreBufferUtf8(str.Value);
            
            case InstLdLocal @ldlocal:
                if (ldlocal.Local < 0) val = ctx.args[(-ldlocal.Local) - 1];
                else
                {
                    val = ctx.locals[ldlocal.Local].ptr;
                    holding = ctx.locals[ldlocal.Local].type;
                }
                break;
            case InstLdLocalRef @ldlocalref:
                val = ldlocalref.Local < 0
                    ? ctx.args[(-ldlocalref.Local) - 1]
                    : ctx.locals[ldlocalref.Local].ptr;
                break;


            case InstLdNewObject @newobj:
            {
                var structRef = newobj.Type;
                var typetype = structures[structRef].type;
                var typetbl = structures[structRef].typetbl;

                return LLVMValueRef.CreateConstStruct([typetbl], false);
            }

            case InstRet @r:
                return r.value
                    ? llvmBuilder.BuildRet(CompileFunctionValue(ctx))
                    : llvmBuilder.BuildRetVoid();
            
            case FlagTypeInt @tint:
            {
                var ty = new IntegerTypeReference(tint.Signed, tint.Size);
                return CompileFunctionValueTyped(ctx, ty);
            }

            case InstCall @call:
            {
                List<LLVMValueRef> argsList = [];
                
                for (var i = 0; i < call.function.Parameters.Count; i++) 
                    argsList.Add(CompileFunctionValue(ctx));

                var funck = BuilderToValueRef(call.function);
                
                val = llvmBuilder.BuildCall2(
                    funck.ftype,
                    funck.fun,
                    argsList.ToArray());
            } break;
            
            default: throw new UnreachableException();
        }
        
        while (ctx.body.Count > 0)
        {
            var curr = ctx.body.Peek();
            if (curr is InstLdField)
            {
                val = GetFieldPtr(val, curr, out holding);
                ctx.body.Dequeue();
            }
            else if (curr is InstStField)
            {
                var ptr = GetFieldPtr(val, curr, out holding);
                ctx.body.Dequeue();
                var tostore = CompileFunctionValue(ctx);
                val = llvmBuilder.BuildStore(tostore, ptr);
                val.SetAlignment((holding.Alignment ?? configuration.NativeIntegerSize) / configuration.MemoryUnit);
                holding = null;
                break;
            }
            else break;
        }

        if (holding == null) return val;
        val = llvmBuilder.BuildLoad2(ConvType(holding), val);
        val.SetAlignment((holding.Alignment ?? configuration.NativeIntegerSize) / configuration.MemoryUnit);
        return val;
    }

    private LLVMValueRef CompileFunctionValueTyped(CompileFunctionCtx ctx, TypeReference ty)
    {
        var a = ctx.body.Dequeue();
        switch (a)
        {
            case InstAdd: return llvmBuilder.BuildAdd(CompileFunctionValue(ctx), CompileFunctionValue(ctx));
            
            case InstMul: return llvmBuilder.BuildMul(CompileFunctionValue(ctx), CompileFunctionValue(ctx));

            case InstAnd: return llvmBuilder.BuildAnd(CompileFunctionValue(ctx), CompileFunctionValue(ctx));
            case InstOr: return llvmBuilder.BuildOr(CompileFunctionValue(ctx), CompileFunctionValue(ctx));
            case InstXor: return llvmBuilder.BuildXor(CompileFunctionValue(ctx), CompileFunctionValue(ctx));
            
            case InstConv: return llvmBuilder.BuildIntCast(CompileFunctionValue(ctx), ConvType(ty));
            case InstExtend:
                return (((IntegerTypeReference)ty).Signed)
                    ? llvmBuilder.BuildSExt(CompileFunctionValue(ctx), ConvType(ty))
                    : llvmBuilder.BuildZExt(CompileFunctionValue(ctx), ConvType(ty));
            case InstTrunc: return llvmBuilder.BuildTrunc(CompileFunctionValue(ctx), ConvType(ty));
            case InstSigcast: return CompileFunctionValue(ctx); // LLVM handles signess in context
            
            default: throw new UnreachableException();
        }
    }
    
    private LLVMValueRef GetFieldPtr(
        LLVMValueRef from,
        IOmegaInstruction access,
        out TypeReference ptrType)
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
        if (struc.Extends != null) fidx += 1; // jump annonymous parent field
        
        return llvmBuilder.BuildStructGEP2(BuilderToTypeRef(struc), from, fidx);
    }


    private LLVMTypeRef ConvType(TypeReference? typeref)
    {
        if (typeref == null) return LLVMTypeRef.Void;
        return typeref switch
        {
            IntegerTypeReference @intt => intt.Bits! switch
            {
                1 => LLVMTypeRef.Int1,
                8 => LLVMTypeRef.Int8,
                16 => LLVMTypeRef.Int16,
                32 => LLVMTypeRef.Int32,
                64 => LLVMTypeRef.Int64,
                _ => LLVMTypeRef.CreateInt(intt.Bits ?? configuration.NativeIntegerSize),
            },
            AnytypeTypeReference => LLVMTypeRef.Void,
            
            
            NodeTypeReference @nodet => nodet.TypeReference switch
            { 
                StructureBuilder @stb => BuilderToTypeRef(stb),
                TypeDefinitionBuilder => GetNativeInt(),
                _ => throw new UnreachableException(),
            },
            
            SliceTypeReference @slice => CreateSlice(ConvType(slice.Subtype)),
            ReferenceTypeReference @refe => LLVMTypeRef.CreatePointer(ConvType(refe.Subtype), 0),
            
            _ => throw new UnreachableException()
        };
        
    }
    
    
    private (LLVMTypeRef ftype, LLVMValueRef fun) BuilderToValueRef(BaseFunctionBuilder builder) => functions[builder];
    private LLVMTypeRef BuilderToTypeRef(StructureBuilder builder) => structures[builder].type;


    private unsafe LLVMValueRef StoreBufferUtf8(string data)
    {
        var d = Encoding.UTF8.GetBytes(data);
        var alloc = stackalloc ulong[d.Length];
        for (var i = 0; i < d.Length; i++) alloc[i] = d[i];
        var span = new ReadOnlySpan<ulong>(alloc, d.Length);
        
        return StoreStaticBuffer(LLVMTypeRef.Int8, span);
    }
    private LLVMValueRef StoreStaticBuffer(LLVMTypeRef elmtype, ReadOnlySpan<ulong> data)
    {
        var h = 0; foreach (var i in data) h = HashCode.Combine(h, i);
        
        if (staticBufferMap.TryGetValue(h, out var dedup)) return dedup;
        
        var arrayType = LLVMTypeRef.CreateArray(elmtype, (uint)data.Length);
        var sliceType = CreateSlice(elmtype);
                
        var global = llvmModule.AddGlobal(arrayType, $"ro.static.buffer.{staticBufferMap.Count:0000}");
        var datallvm = new LLVMValueRef[data.Length];
        for (var i = 0; i < data.Length; i++) datallvm[i] = LLVMValueRef.CreateConstInt(elmtype, data[i]);

        global.Alignment = 1;
        global.Initializer = LLVMValueRef.CreateConstArray(elmtype, datallvm);
        
        var gep = LLVMValueRef.CreateConstInBoundsGEP2(
            LLVMTypeRef.CreatePointer(elmtype, 0), global,
            [LLVMValueRef.CreateConstInt(LLVMTypeRef.Int16, 0),
                LLVMValueRef.CreateConstInt(LLVMTypeRef.Int16, 0)]);
                
        var ptr = LLVMValueRef.CreateConstNamedStruct(sliceType, [gep,
            LLVMValueRef.CreateConstInt(GetNativeInt(), (ulong)data.Length)]);

        staticBufferMap.Add(h, ptr);
        return ptr;
    }
    
    private LLVMTypeRef CreateSlice(LLVMTypeRef elementType) => LLVMTypeRef.CreateStruct([
        LLVMTypeRef.CreatePointer(elementType, 0), GetNativeInt()], false);
    private LLVMTypeRef GetNativeInt() => configuration.NativeIntegerSize switch
    {
        1 => LLVMTypeRef.Int1,
        8 => LLVMTypeRef.Int8,
        16 => LLVMTypeRef.Int16,
        32 => LLVMTypeRef.Int32,
        64 => LLVMTypeRef.Int64,
        _ => LLVMTypeRef.CreateInt(configuration.NativeIntegerSize),
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
    
    
    private struct CompileFunctionCtx
    {
        public LLVMValueRef[] args;
        public List<(TypeReference type, LLVMValueRef ptr)> locals;
        public Queue<IOmegaInstruction> body;
    }
}
